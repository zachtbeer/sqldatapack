using Shouldly;
using SqlDataPack.Internal;

namespace SqlDataPack.IntegrationTests.Harness;

/// <summary>
/// Compares a table in the source database against the same table in the target database with <c>EXCEPT</c> in
/// both directions, on the one container both databases live on. It compares whatever columns the table
/// actually has, so a column added to a fixture later is compared too -- a hand-written column-by-column WHERE
/// clause silently would not.
/// </summary>
internal static class CrossDatabaseCompare {
    /// <summary>
    /// Asserts source and target hold exactly the same rows for <paramref name="table"/>.
    /// <para>
    /// Both sides must have the same column names. Pass <paramref name="ignoreColumns"/> for columns that
    /// cannot match by construction -- a ROWVERSION the target regenerates, an identity the target assigns
    /// itself -- and nothing else.
    /// </para>
    /// </summary>
    public static async Task AssertTablesIdenticalAsync(SqlServerFixtureDatabase source, SqlServerFixtureDatabase target, string table, params string[] ignoreColumns) {
        var name = Parse(table);
        var ignored = new HashSet<string>(ignoreColumns, StringComparer.OrdinalIgnoreCase);

        var sourceColumns = await ReadColumnsAsync(source, name);
        var targetColumns = await ReadColumnsAsync(target, name);

        sourceColumns.Count.ShouldBeGreaterThan(0, $"'{table}' does not exist in source database '{source.DatabaseName}'.");
        targetColumns.Count.ShouldBeGreaterThan(0, $"'{table}' does not exist in target database '{target.DatabaseName}'.");

        var sourceNames = sourceColumns.Select(c => c.Name).Where(n => !ignored.Contains(n)).ToArray();
        var targetNames = targetColumns.Select(c => c.Name).Where(n => !ignored.Contains(n)).ToArray();

        sourceNames.Order(StringComparer.OrdinalIgnoreCase).ShouldBe(
            targetNames.Order(StringComparer.OrdinalIgnoreCase),
            $"'{table}' has different columns in '{source.DatabaseName}' and '{target.DatabaseName}'.");

        var selectList = string.Join(", ", sourceColumns.Where(c => !ignored.Contains(c.Name)).Select(Render));
        var sourceTable = $"{Quote(source.DatabaseName)}.{SqlDataPackIdentifier.QuoteSqlServerTable(name)}";
        var targetTable = $"{Quote(target.DatabaseName)}.{SqlDataPackIdentifier.QuoteSqlServerTable(name)}";

        var sourceRows = await source.ScalarIntAsync($"SELECT COUNT(*) FROM {sourceTable}");
        var targetRows = await source.ScalarIntAsync($"SELECT COUNT(*) FROM {targetTable}");
        targetRows.ShouldBe(sourceRows, $"'{table}' row count differs between '{source.DatabaseName}' and '{target.DatabaseName}'.");

        var onlyInSource = await ReadDifferenceAsync(source, selectList, sourceTable, targetTable);
        var onlyInTarget = await ReadDifferenceAsync(source, selectList, targetTable, sourceTable);

        if (onlyInSource.Count > 0 || onlyInTarget.Count > 0) {
            throw new ShouldAssertException($"""
                                             '{table}' differs between source and target.
                                             Rows only in source ({source.DatabaseName}): {Render(onlyInSource)}
                                             Rows only in target ({target.DatabaseName}): {Render(onlyInTarget)}
                                             Compared columns: {string.Join(", ", sourceNames)}
                                             """);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadDifferenceAsync(SqlServerFixtureDatabase runner, string selectList, string left, string right) {
        return await runner.ReadRowsAsync($"""
                                           SELECT TOP (5) *
                                           FROM (
                                               SELECT {selectList} FROM {left}
                                               EXCEPT
                                               SELECT {selectList} FROM {right}
                                           ) AS difference
                                           """);
    }

    private static async Task<IReadOnlyList<(string Name, string TypeName)>> ReadColumnsAsync(SqlServerFixtureDatabase db, TableName name) {
        var rows = await db.ReadRecordsAsync($"""
                                              SELECT c.name, TYPE_NAME(c.user_type_id)
                                              FROM sys.columns c
                                              WHERE c.object_id = OBJECT_ID(QUOTENAME({Literal(name.Schema)}) + N'.' + QUOTENAME({Literal(name.Name)}))
                                                AND c.is_hidden = 0
                                              ORDER BY c.column_id
                                              """);

        return rows.Select(row => (row[0], row[1])).ToArray();
    }

    // Types SQL Server refuses to compare with EXCEPT get a comparable rendering rather than being dropped:
    // dropping one would be exactly the silent skip this class exists to prevent.
    private static string Render((string Name, string TypeName) column) {
        var quoted = SqlDataPackIdentifier.QuoteSqlServerName(column.Name);

        return column.TypeName.ToLowerInvariant() switch {
            "xml" or "text" or "ntext" => $"CONVERT(NVARCHAR(MAX), {quoted}) AS {quoted}",
            "image" => $"CONVERT(VARBINARY(MAX), {quoted}) AS {quoted}",
            "json" or "vector" => $"CONVERT(NVARCHAR(MAX), {quoted}) AS {quoted}",
            "geography" or "geometry" or "hierarchyid" => $"{quoted}.ToString() AS {quoted}",
            _ => quoted
        };
    }

    private static TableName Parse(string table) {
        var parts = table.Split('.', 2);
        return parts.Length == 2 ? new TableName(parts[0], parts[1]) : new TableName("dbo", parts[0]);
    }

    private static string Quote(string identifier) {
        return SqlDataPackIdentifier.QuoteSqlServerName(identifier);
    }

    private static string Literal(string value) {
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string Render(IReadOnlyList<string> rows) {
        return rows.Count == 0 ? "(none)" : Environment.NewLine + string.Join(Environment.NewLine, rows.Select(r => "  " + r));
    }
}
