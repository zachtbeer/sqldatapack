using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.Internal;

namespace SqlDataPack.IntegrationTests.Harness;

internal static class SqlitePackageAssertions {
    private static readonly string[] RequiredMetadataTables = [
        "zsdp_export_runs",
        "zsdp_tables",
        "zsdp_columns",
        "zsdp_exclusions",
        "zsdp_warnings",
        "zsdp_table_stats",
        "zsdp_import_plan"
    ];

    public static async Task HasRequiredMetadataTablesAsync(SqliteConnection connection) {
        foreach (var table in RequiredMetadataTables) {
            (await connection.TableExistsAsync(table)).ShouldBeTrue($"Expected metadata table '{table}' to exist.");
        }
    }

    public static async Task HasExportedTablesAsync(SqliteConnection connection, params string[] expectedFullNames) {
        var actual = await connection.ReadStringsAsync("""
                                                       SELECT source_schema || '.' || source_table
                                                       FROM zsdp_tables
                                                       ORDER BY source_schema, source_table
                                                       """);

        actual.ShouldBe(expectedFullNames.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static async Task HasImportPlanAsync(SqliteConnection connection, params string[] expectedFullNames) {
        var actual = await connection.ReadStringsAsync("""
                                                       SELECT source_schema || '.' || source_table
                                                       FROM zsdp_import_plan
                                                       ORDER BY sequence
                                                       """);

        actual.ShouldBe(expectedFullNames);
    }

    public static async Task HasRunMetadataAsync(SqliteConnection connection) {
        // Read the format version from production rather than repeating a literal here: a hand-copied
        // number drifts from the shipping one and the assertion quietly stops meaning anything.
        (await connection.ScalarIntAsync($"""
                                          SELECT COUNT(*)
                                          FROM zsdp_export_runs
                                          WHERE package_format_version = {SqlDataPackVersion.PackageFormatVersion}
                                            AND application_version <> ''
                                            AND exported_at_utc <> ''
                                            AND length(source_schema_hash) = 64
                                          """)).ShouldBe(1);
    }

    public static async Task HasTableRowCountAsync(SqliteConnection connection, string fullName, int expectedRows) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT s.exported_row_count, s.estimated_source_row_count, s.estimated_source_bytes, s.export_batch_size
                              FROM zsdp_table_stats s
                              INNER JOIN zsdp_tables t ON t.id = s.table_id
                              WHERE t.source_schema || '.' || t.source_table = $name
                              """;
        command.Parameters.AddWithValue("$name", fullName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"Expected table stats for '{fullName}'.");
        reader.GetInt32(0).ShouldBe(expectedRows);
        reader.GetInt64(1).ShouldBeGreaterThanOrEqualTo(0);
        reader.GetInt64(2).ShouldBeGreaterThanOrEqualTo(0);
        reader.GetInt32(3).ShouldBeGreaterThan(0);
    }

    public static async Task HasExclusionAsync(SqliteConnection connection, string type, string targetName) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM zsdp_exclusions
                              WHERE exclusion_type = $type
                                AND target_name = $target
                              """;
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$target", targetName);
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(1);
    }

    /// <summary>Every warning row in the package, in insertion order.</summary>
    public static async Task<IReadOnlyList<string>> ReadWarningsAsync(SqliteConnection connection) {
        return await connection.ReadStringsAsync("SELECT warning_text FROM zsdp_warnings ORDER BY rowid");
    }

    /// <summary>
    /// Asserts exactly <paramref name="expectedCount"/> warning rows contain <paramref name="substring"/>
    /// (case-insensitive). The failure message lists every warning actually present.
    /// </summary>
    public static async Task HasWarningMatchingAsync(SqliteConnection connection, string substring, int expectedCount = 1) {
        var warnings = await ReadWarningsAsync(connection);
        var matches = warnings.Count(w => w.Contains(substring, StringComparison.OrdinalIgnoreCase));

        matches.ShouldBe(expectedCount, $"Expected {expectedCount} warning(s) containing '{substring}'. Warnings in package: {Render(warnings)}");
    }

    /// <summary>
    /// Asserts a column's captured metadata. Only the arguments you pass are checked, so a call reads as the
    /// list of facts that matter for that column rather than a wall of nulls.
    /// </summary>
    public static async Task HasColumnMetadataAsync(
        SqliteConnection connection,
        string fullTableName,
        string columnName,
        string? typeName = null,
        int? precision = null,
        int? scale = null,
        int? maxLength = null,
        bool? isNullable = null,
        bool? isIdentity = null,
        bool? isComputed = null,
        bool? isExcluded = null,
        int? ordinal = null,
        string? collationName = null,
        int? vectorBaseType = null,
        int? vectorDimensions = null) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT c.sql_server_type_name, c.precision_value, c.scale_value, c.max_length,
                                     c.is_nullable, c.is_identity, c.is_computed, c.is_excluded, c.ordinal,
                                     c.collation_name, c.vector_base_type, c.vector_dimensions
                              FROM zsdp_columns c
                              INNER JOIN zsdp_tables t ON t.id = c.table_id
                              WHERE t.source_schema || '.' || t.source_table = $table
                                AND c.column_name = $column
                              """;
        command.Parameters.AddWithValue("$table", fullTableName);
        command.Parameters.AddWithValue("$column", columnName);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"Expected column metadata for '{fullTableName}.{columnName}'.");

        var where = $"{fullTableName}.{columnName}";
        if (typeName is not null) {
            reader.GetString(0).ShouldBe(typeName, $"{where}: sql_server_type_name");
        }

        if (precision is not null) {
            reader.GetInt32(1).ShouldBe(precision.Value, $"{where}: precision_value");
        }

        if (scale is not null) {
            reader.GetInt32(2).ShouldBe(scale.Value, $"{where}: scale_value");
        }

        if (maxLength is not null) {
            reader.GetInt32(3).ShouldBe(maxLength.Value, $"{where}: max_length");
        }

        if (isNullable is not null) {
            (reader.GetInt32(4) == 1).ShouldBe(isNullable.Value, $"{where}: is_nullable");
        }

        if (isIdentity is not null) {
            (reader.GetInt32(5) == 1).ShouldBe(isIdentity.Value, $"{where}: is_identity");
        }

        if (isComputed is not null) {
            (reader.GetInt32(6) == 1).ShouldBe(isComputed.Value, $"{where}: is_computed");
        }

        if (isExcluded is not null) {
            (reader.GetInt32(7) == 1).ShouldBe(isExcluded.Value, $"{where}: is_excluded");
        }

        if (ordinal is not null) {
            reader.GetInt32(8).ShouldBe(ordinal.Value, $"{where}: ordinal");
        }

        if (collationName is not null) {
            (reader.IsDBNull(9) ? null : reader.GetString(9)).ShouldBe(collationName, $"{where}: collation_name");
        }

        if (vectorBaseType is not null) {
            (reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10)).ShouldBe(vectorBaseType.Value, $"{where}: vector_base_type");
        }

        if (vectorDimensions is not null) {
            (reader.IsDBNull(11) ? (int?)null : reader.GetInt32(11)).ShouldBe(vectorDimensions.Value, $"{where}: vector_dimensions");
        }

        (await reader.ReadAsync()).ShouldBeFalse($"Expected exactly one column metadata row for '{where}'.");
    }

    /// <summary>
    /// First column of the first row as uppercase hex, for byte-exact comparisons against the server. A BLOB
    /// is rendered as stored; TEXT is rendered as its UTF-16LE bytes, which is what SQL Server's
    /// <c>CONVERT(VARBINARY(MAX), &lt;nvarchar&gt;)</c> produces.
    /// </summary>
    public static async Task<string> ReadHexAsync(SqliteConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return ToHex(await command.ExecuteScalarAsync());
    }

    /// <summary>Every row's first column as hex, in query order.</summary>
    public static async Task<IReadOnlyList<string>> ReadHexListAsync(SqliteConnection connection, string sql) {
        var rows = new List<string>();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            rows.Add(ToHex(reader.IsDBNull(0) ? null : reader.GetValue(0)));
        }

        return rows;
    }

    private static string ToHex(object? value) {
        var bytes = value switch {
            null or DBNull => throw new InvalidOperationException("Value is NULL; there are no bytes to compare."),
            byte[] blob => blob,
            string text => Encoding.Unicode.GetBytes(text),
            _ => throw new InvalidOperationException($"Value of type '{value.GetType().Name}' has no byte-exact rendering; CAST it in the query.")
        };

        return Convert.ToHexString(bytes);
    }

    private static string Render(IReadOnlyList<string> warnings) {
        return warnings.Count == 0 ? "(none)" : Environment.NewLine + string.Join(Environment.NewLine, warnings.Select(w => "  - " + w));
    }
}
