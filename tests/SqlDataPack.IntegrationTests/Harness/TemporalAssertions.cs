using SqlDataPack.Internal;

namespace SqlDataPack.IntegrationTests.Harness;

/// <summary>
/// Reads what a system-versioned table actually is, and renders its full history deterministically.
/// A row-count or a spot check on the current rows cannot tell a preserved period from one the engine
/// reassigned on insert; <see cref="DumpSystemVersionedAsync"/> can, because the period columns come back as
/// their raw bytes.
/// </summary>
internal static class TemporalAssertions {
    /// <summary>
    /// Every version of every row -- <c>FOR SYSTEM_TIME ALL</c>, ordered by primary key then period start --
    /// rendered one row per line, columns joined by <c>" | "</c>, with the two period columns as hex. The
    /// first line is the column-name header, so a shape difference shows up as a diff rather than hiding.
    /// Hidden period columns are included: the select list is built from sys.columns, not <c>SELECT *</c>.
    /// </summary>
    public static async Task<string> DumpSystemVersionedAsync(SqlServerFixtureDatabase db, string table, string periodStart, string periodEnd) {
        var name = Parse(table);

        if (!await IsSystemVersionedAsync(db, table)) {
            throw new InvalidOperationException($"'{table}' in database '{db.DatabaseName}' is not system-versioned; there is no history to dump.");
        }

        var columns = await ReadColumnsAsync(db, name);
        var keyColumns = await ReadPrimaryKeyColumnsAsync(db, name);
        if (keyColumns.Count == 0) {
            throw new InvalidOperationException($"'{table}' has no primary key, so its history cannot be ordered deterministically.");
        }

        var selectList = string.Join(", ", columns.Select(c => Render(c, periodStart, periodEnd)));
        var orderBy = string.Join(", ", keyColumns.Select(SqlDataPackIdentifier.QuoteSqlServerName).Append(SqlDataPackIdentifier.QuoteSqlServerName(periodStart)).Append(SqlDataPackIdentifier.QuoteSqlServerName(periodEnd)));

        var rows = await db.ReadRowsAsync($"SELECT {selectList} FROM {Quote(name)} FOR SYSTEM_TIME ALL ORDER BY {orderBy}");

        var header = string.Join(" | ", columns.Select(c => c.Name));
        return string.Join(Environment.NewLine, rows.Prepend(header));
    }

    public static async Task<bool> IsSystemVersionedAsync(SqlServerFixtureDatabase db, string table) {
        var name = Parse(table);
        return await db.ScalarIntAsync($"SELECT COUNT(*) FROM sys.tables WHERE object_id = {ObjectId(name)} AND temporal_type = 2") == 1;
    }

    /// <summary>The period start and end column names, whatever the table happens to call them.</summary>
    public static async Task<(string Start, string End)> ReadPeriodColumnNamesAsync(SqlServerFixtureDatabase db, string table) {
        var name = Parse(table);
        var values = await db.ReadRecordsAsync($"""
                                                SELECT sc.name, ec.name
                                                FROM sys.periods p
                                                INNER JOIN sys.columns sc ON sc.object_id = p.object_id AND sc.column_id = p.start_column_id
                                                INNER JOIN sys.columns ec ON ec.object_id = p.object_id AND ec.column_id = p.end_column_id
                                                WHERE p.object_id = {ObjectId(name)}
                                                """);

        if (values.Count != 1) {
            throw new InvalidOperationException($"'{table}' in database '{db.DatabaseName}' has no SYSTEM_TIME period.");
        }

        return (values[0][0], values[0][1]);
    }

    /// <summary>
    /// Finite retention as (value, unit), e.g. <c>(3, "MONTH")</c>. Infinite retention comes back as
    /// <c>(-1, "INFINITE")</c>, which is what SQL Server stores for a table with no retention set.
    /// </summary>
    public static async Task<(int Value, string Unit)> ReadRetentionAsync(SqlServerFixtureDatabase db, string table) {
        var name = Parse(table);
        var value = await db.ReadRecordsAsync($"""
                                               SELECT history_retention_period, history_retention_period_unit_desc
                                               FROM sys.tables
                                               WHERE object_id = {ObjectId(name)}
                                               """);

        if (value.Count != 1) {
            throw new InvalidOperationException($"'{table}' does not exist in database '{db.DatabaseName}'.");
        }

        return (int.Parse(value[0][0]), value[0][1]);
    }

    /// <summary>Whether a period column is HIDDEN, which a round trip must not change.</summary>
    public static async Task<bool> IsHiddenAsync(SqlServerFixtureDatabase db, string table, string columnName) {
        var name = Parse(table);
        return await db.ScalarIntAsync($"""
                                        SELECT COUNT(*)
                                        FROM sys.columns
                                        WHERE object_id = {ObjectId(name)}
                                          AND name = {Literal(columnName)}
                                          AND is_hidden = 1
                                        """) == 1;
    }

    /// <summary>The schema-qualified history table name, or null when the table is not system-versioned.</summary>
    public static async Task<string?> ReadHistoryTableNameAsync(SqlServerFixtureDatabase db, string table) {
        var name = Parse(table);
        var rows = await db.ReadStringsAsync($"""
                                              SELECT SCHEMA_NAME(h.schema_id) + '.' + h.name
                                              FROM sys.tables t
                                              INNER JOIN sys.tables h ON h.object_id = t.history_table_id
                                              WHERE t.object_id = {ObjectId(name)}
                                              """);

        return rows.Count == 1 ? rows[0] : null;
    }

    private static async Task<IReadOnlyList<(string Name, string TypeName)>> ReadColumnsAsync(SqlServerFixtureDatabase db, TableName name) {
        var rows = await db.ReadRecordsAsync($"""
                                              SELECT c.name, TYPE_NAME(c.user_type_id)
                                              FROM sys.columns c
                                              WHERE c.object_id = {ObjectId(name)}
                                              ORDER BY c.column_id
                                              """);

        return rows.Select(row => (row[0], row[1])).ToArray();
    }

    private static async Task<IReadOnlyList<string>> ReadPrimaryKeyColumnsAsync(SqlServerFixtureDatabase db, TableName name) {
        return await db.ReadStringsAsync($"""
                                          SELECT c.name
                                          FROM sys.indexes i
                                          INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                          INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                          WHERE i.object_id = {ObjectId(name)}
                                            AND i.is_primary_key = 1
                                          ORDER BY ic.key_ordinal
                                          """);
    }

    private static string Render((string Name, string TypeName) column, string periodStart, string periodEnd) {
        var quoted = SqlDataPackIdentifier.QuoteSqlServerName(column.Name);

        // The period columns are the whole point: compare their bytes, not their formatted text, so a
        // reassigned period cannot look equal to a preserved one.
        if (column.Name.Equals(periodStart, StringComparison.OrdinalIgnoreCase) || column.Name.Equals(periodEnd, StringComparison.OrdinalIgnoreCase)) {
            return $"ISNULL(CONVERT(VARCHAR(64), CONVERT(VARBINARY(16), {quoted}), 2), '<NULL>')";
        }

        return column.TypeName.ToLowerInvariant() switch {
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => $"ISNULL(CONVERT(VARCHAR(MAX), {quoted}, 2), '<NULL>')",
            "date" or "time" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => $"ISNULL(CONVERT(VARCHAR(64), {quoted}, 126), '<NULL>')",
            "float" or "real" => $"ISNULL(CONVERT(VARCHAR(64), {quoted}, 2), '<NULL>')",
            "xml" => $"ISNULL(CONVERT(NVARCHAR(MAX), {quoted}), '<NULL>')",
            _ => $"ISNULL(CONVERT(NVARCHAR(MAX), {quoted}), '<NULL>')"
        };
    }

    private static TableName Parse(string table) {
        var parts = table.Split('.', 2);
        return parts.Length == 2 ? new TableName(parts[0], parts[1]) : new TableName("dbo", parts[0]);
    }

    private static string Quote(TableName name) {
        return SqlDataPackIdentifier.QuoteSqlServerTable(name);
    }

    // OBJECT_ID takes a string, so the name has to survive as a literal: QUOTENAME does the bracket
    // escaping that adversarial-identifiers.sql exists to break.
    private static string ObjectId(TableName name) {
        return $"OBJECT_ID(QUOTENAME({Literal(name.Schema)}) + N'.' + QUOTENAME({Literal(name.Name)}))";
    }

    private static string Literal(string value) {
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
