using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal static class SqlitePackage {
    private static readonly string[] RequiredMetadataTables = [
        "zsdp_export_runs",
        "zsdp_tables",
        "zsdp_columns",
        "zsdp_exclusions",
        "zsdp_warnings",
        "zsdp_table_stats",
        "zsdp_import_plan",
        "zsdp_schema_packages"
    ];

    /// <summary>
    /// Tunes the connection for the write-once bulk load an export performs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Relaxing durability is safe here because of how <c>SqlDataPackExporter</c> already works: it
    /// writes to a <c>.{name}.{guid}.tmp</c> file and only moves it into place after the export
    /// succeeds, deleting it on any failure. A half-written package is never observable, so paying for
    /// an fsync per batch commit buys nothing.
    /// </para>
    /// <para>
    /// None of these settings change the bytes a consumer sees. <c>journal_mode = MEMORY</c> is not
    /// persisted in the file (only WAL is), and <c>synchronous</c>, <c>temp_store</c>, and
    /// <c>cache_size</c> are per-connection. <c>page_size</c> <em>is</em> stored in the file header,
    /// which is why it must be set before the first CREATE TABLE — 8 KiB pages suit the wide rows and
    /// blobs typical of exported data. The result is an ordinary SQLite database in every respect.
    /// </para>
    /// <para>
    /// MEMORY rather than OFF for the journal: it delivers effectively the same throughput while
    /// keeping rollback working within a run, so an aborted transaction still leaves a coherent file.
    /// </para>
    /// </remarks>
    public static async Task ConfigureForBulkWriteAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        await ExecuteNonQueryAsync(connection, """
                                               PRAGMA page_size = 8192;
                                               PRAGMA journal_mode = MEMORY;
                                               PRAGMA synchronous = OFF;
                                               PRAGMA temp_store = MEMORY;
                                               PRAGMA cache_size = -65536;
                                               """, cancellationToken);
    }

    public static async Task InitializeAsync(SqliteConnection connection, ExportPlan plan, CancellationToken cancellationToken) {
        // Must run before the first CREATE TABLE: page_size is only settable on an empty database.
        await ConfigureForBulkWriteAsync(connection, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
                                               CREATE TABLE zsdp_export_runs (
                                                   id INTEGER PRIMARY KEY,
                                                   package_format_version INTEGER NOT NULL,
                                                   application_version TEXT NOT NULL,
                                                   exported_at_utc TEXT NOT NULL,
                                                   source_schema_hash TEXT NOT NULL
                                               );
                                               CREATE TABLE zsdp_tables (
                                                   id INTEGER PRIMARY KEY,
                                                   source_schema TEXT NOT NULL,
                                                   source_table TEXT NOT NULL,
                                                   sqlite_table TEXT NOT NULL
                                               );
                                               CREATE TABLE zsdp_columns (
                                                   table_id INTEGER NOT NULL,
                                                   column_name TEXT NOT NULL,
                                                   ordinal INTEGER NOT NULL,
                                                   sql_server_type_name TEXT NOT NULL,
                                                   max_length INTEGER NOT NULL,
                                                   precision_value INTEGER NOT NULL,
                                                   scale_value INTEGER NOT NULL,
                                                   is_nullable INTEGER NOT NULL,
                                                   is_identity INTEGER NOT NULL,
                                                   is_computed INTEGER NOT NULL,
                                                   is_excluded INTEGER NOT NULL,
                                                   collation_name TEXT NULL,
                                                   vector_base_type INTEGER NULL,
                                                   vector_dimensions INTEGER NULL,
                                                   FOREIGN KEY (table_id) REFERENCES zsdp_tables(id)
                                               );
                                               CREATE TABLE zsdp_exclusions (
                                                   exclusion_type TEXT NOT NULL,
                                                   target_name TEXT NOT NULL
                                               );
                                               CREATE TABLE zsdp_warnings (
                                                   warning_text TEXT NOT NULL
                                               );
                                               CREATE TABLE zsdp_table_stats (
                                                   table_id INTEGER NOT NULL,
                                                   exported_row_count INTEGER NOT NULL,
                                                   estimated_source_row_count INTEGER NOT NULL,
                                                   estimated_source_bytes INTEGER NOT NULL,
                                                   export_batch_size INTEGER NOT NULL,
                                                   FOREIGN KEY (table_id) REFERENCES zsdp_tables(id)
                                               );
                                               CREATE TABLE zsdp_import_plan (
                                                   sequence INTEGER NOT NULL,
                                                   source_schema TEXT NOT NULL,
                                                   source_table TEXT NOT NULL
                                               );
                                               CREATE TABLE zsdp_schema_packages (
                                                   id INTEGER PRIMARY KEY,
                                                   package_type TEXT NOT NULL,
                                                   package_name TEXT NOT NULL,
                                                   package_sha256 TEXT NOT NULL,
                                                   created_at_utc TEXT NOT NULL,
                                                   source_database_name TEXT NULL,
                                                   dacfx_version TEXT NULL,
                                                   schema_scope TEXT NULL,
                                                   payload BLOB NOT NULL,
                                                   source_engine_edition INTEGER NULL
                                               );
                                               """, cancellationToken);

        await InsertMetadataAsync(connection, plan, cancellationToken);
        await CreateDataTablesAsync(connection, plan.Tables, cancellationToken);
    }

    public static async Task ValidateForImportAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        try {
            await ValidateForImportCoreAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsCorruptPackageException(ex)) {
            throw new SqlDataPackException("SQLite package is invalid or corrupt and cannot be read. Recreate the package with SqlDataPack export.", ex);
        }
    }

    // The package file is untrusted input. Any parse, cast, or storage error from a tampered or
    // corrupt package is surfaced as a documented SqlDataPackException rather than a raw framework type.
    private static bool IsCorruptPackageException(Exception ex) =>
        ex is not SqlDataPackException and not OperationCanceledException;

    private static async Task ValidateForImportCoreAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        foreach (var tableName in RequiredMetadataTables) {
            if (!await TableExistsAsync(connection, tableName, cancellationToken)) {
                throw new SqlDataPackException($"SQLite package is invalid: required metadata table '{tableName}' is missing. Recreate the package with SqlDataPack export.");
            }
        }

        await ValidatePackageFormatVersionAsync(connection, cancellationToken);
        await ValidateTableStatsSchemaAsync(connection, cancellationToken);

        var tableCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM zsdp_tables", cancellationToken);
        if (tableCount == 0) {
            throw new SqlDataPackException("SQLite package is invalid: no table metadata exists in 'zsdp_tables'. Recreate the package with at least one selected table.");
        }

        var planCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM zsdp_import_plan", cancellationToken);
        if (planCount == 0) {
            throw new SqlDataPackException("SQLite package is invalid: import plan metadata is empty. Recreate the package with SqlDataPack export.");
        }

        var missingPlanMetadata = await ReadStringsAsync(connection, """
                                                                     SELECT p.source_schema || '.' || p.source_table
                                                                     FROM zsdp_import_plan p
                                                                     LEFT JOIN zsdp_tables t
                                                                         ON t.source_schema = p.source_schema
                                                                        AND t.source_table = p.source_table
                                                                     WHERE t.id IS NULL
                                                                     ORDER BY p.sequence
                                                                     """, cancellationToken);
        if (missingPlanMetadata.Count > 0) {
            throw new SqlDataPackException($"SQLite package is invalid: import plan references table '{missingPlanMetadata[0]}' but no matching row exists in 'zsdp_tables'. Recreate the package.");
        }

        var missingRowCounts = await ReadStringsAsync(connection, """
                                                                  SELECT t.source_schema || '.' || t.source_table
                                                                  FROM zsdp_import_plan p
                                                                  INNER JOIN zsdp_tables t
                                                                      ON t.source_schema = p.source_schema
                                                                     AND t.source_table = p.source_table
                                                                  LEFT JOIN zsdp_table_stats s ON s.table_id = t.id
                                                                  WHERE s.table_id IS NULL
                                                                  ORDER BY p.sequence
                                                                  """, cancellationToken);
        if (missingRowCounts.Count > 0) {
            throw new SqlDataPackException($"SQLite package is invalid: row-count metadata is missing for '{missingRowCounts[0]}'. Recreate the package.");
        }

        var invalidBatchSizes = await ReadStringsAsync(connection, """
                                                                   SELECT t.source_schema || '.' || t.source_table
                                                                   FROM zsdp_import_plan p
                                                                   INNER JOIN zsdp_tables t
                                                                       ON t.source_schema = p.source_schema
                                                                      AND t.source_table = p.source_table
                                                                   INNER JOIN zsdp_table_stats s ON s.table_id = t.id
                                                                   WHERE s.export_batch_size <= 0
                                                                   ORDER BY p.sequence
                                                                   """, cancellationToken);
        if (invalidBatchSizes.Count > 0) {
            throw new SqlDataPackException($"SQLite package is invalid: batch-size metadata is invalid for '{invalidBatchSizes[0]}'. Recreate the package.");
        }

        var dataTables = await ReadStringsAsync(connection, """
                                                            SELECT t.source_schema || '.' || t.source_table || '|' || t.sqlite_table
                                                            FROM zsdp_import_plan p
                                                            INNER JOIN zsdp_tables t
                                                                ON t.source_schema = p.source_schema
                                                               AND t.source_table = p.source_table
                                                            ORDER BY p.sequence
                                                            """, cancellationToken);
        foreach (var entry in dataTables) {
            var parts = entry.Split('|', 2);
            if (!await TableExistsAsync(connection, parts[1], cancellationToken)) {
                throw new SqlDataPackException($"SQLite package is invalid: data table '{parts[1]}' for '{parts[0]}' is missing. Recreate the package.");
            }
        }
    }

    public static async Task<IReadOnlyList<TableMetadata>> ReadTablesAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        try {
            return await ReadTablesCoreAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsCorruptPackageException(ex)) {
            throw new SqlDataPackException("SQLite package is invalid or corrupt and cannot be read. Recreate the package with SqlDataPack export.", ex);
        }
    }

    private static async Task<IReadOnlyList<TableMetadata>> ReadTablesCoreAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        var tableRows = new List<(long Id, TableName Table, string SqliteName, long EstimatedSourceRowCount, long EstimatedSourceBytes, int ExportBatchSize)>();
        await using (var command = connection.CreateCommand()) {
            command.CommandText = """
                                  SELECT t.id, t.source_schema, t.source_table, t.sqlite_table,
                                         s.estimated_source_row_count, s.estimated_source_bytes, s.export_batch_size
                                  FROM zsdp_tables t
                                  INNER JOIN zsdp_table_stats s ON s.table_id = t.id
                                  ORDER BY t.id
                                  """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                TableName? table = null;
                try {
                    table = new TableName(reader.GetString(1), reader.GetString(2));
                    tableRows.Add((reader.GetInt64(0), table, reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6)));
                }
                catch (Exception ex) when (IsCorruptPackageException(ex)) {
                    var where = table is null ? "a table" : $"table '{table.FullName}'";
                    throw new SqlDataPackException($"SQLite package is invalid: the stored metadata for {where} could not be read. Recreate the package with SqlDataPack export.", ex);
                }
            }
        }

        var result = new List<TableMetadata>();
        foreach (var row in tableRows) {
            var columns = new List<ColumnMetadata>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT column_name, ordinal, sql_server_type_name, max_length, precision_value, scale_value,
                                         is_nullable, is_identity, is_computed, collation_name, is_excluded, vector_base_type, vector_dimensions
                                  FROM zsdp_columns
                                  WHERE table_id = $table_id
                                  ORDER BY ordinal
                                  """;
            command.Parameters.AddWithValue("$table_id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                // Every cell below is untrusted. Name the table (and the column, when its own name cell
                // survived) so a corrupt package points at what to look at instead of just failing.
                string? columnName = null;
                try {
                    columnName = reader.GetString(0);
                    columns.Add(new ColumnMetadata(row.Table, columnName, reader.GetInt32(1), reader.GetString(2), checked((short)reader.GetInt32(3)), checked((byte)reader.GetInt32(4)), checked((byte)reader.GetInt32(5)), reader.GetInt32(6) == 1, reader.GetInt32(7) == 1, reader.GetInt32(8) == 1, reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10) == 1, reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetInt32(12)));
                }
                catch (Exception ex) when (IsCorruptPackageException(ex)) {
                    var where = columnName is null ? $"table '{row.Table.FullName}'" : $"column '{row.Table.FullName}.{columnName}'";
                    throw new SqlDataPackException($"SQLite package is invalid: the stored metadata for {where} could not be read. Recreate the package with SqlDataPack export.", ex);
                }
            }

            result.Add(new TableMetadata(row.Table, row.SqliteName, columns, row.EstimatedSourceRowCount, row.EstimatedSourceBytes, row.ExportBatchSize));
        }

        return result;
    }

    public static async Task<IReadOnlyList<TableName>> ReadImportOrderAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        var result = new List<TableName>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_schema, source_table FROM zsdp_import_plan ORDER BY sequence";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            result.Add(new TableName(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public static async Task<long> ReadExpectedRowCountAsync(SqliteConnection connection, TableMetadata table, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT s.exported_row_count
                              FROM zsdp_table_stats s
                              INNER JOIN zsdp_tables t ON t.id = s.table_id
                              WHERE t.source_schema = $schema AND t.source_table = $table
                              """;
        command.Parameters.AddWithValue("$schema", table.Name.Schema);
        command.Parameters.AddWithValue("$table", table.Name.Name);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null) {
            throw new SqlDataPackException($"SQLite package is invalid: row-count metadata is missing for '{table.Name.FullName}'. Recreate the package.");
        }

        return Convert.ToInt64(value);
    }

    public static async Task RecordTableStatsAsync(SqliteConnection connection, TableMetadata table, long rowCount, int exportBatchSize, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO zsdp_table_stats(
                                  table_id,
                                  exported_row_count,
                                  estimated_source_row_count,
                                  estimated_source_bytes,
                                  export_batch_size)
                              SELECT id, $row_count, $estimated_row_count, $estimated_bytes, $export_batch_size
                              FROM zsdp_tables
                              WHERE source_schema = $schema AND source_table = $table
                              """;
        command.Parameters.AddWithValue("$row_count", rowCount);
        command.Parameters.AddWithValue("$estimated_row_count", table.EstimatedSourceRowCount);
        command.Parameters.AddWithValue("$estimated_bytes", table.EstimatedSourceBytes);
        command.Parameters.AddWithValue("$export_batch_size", exportBatchSize);
        command.Parameters.AddWithValue("$schema", table.Name.Schema);
        command.Parameters.AddWithValue("$table", table.Name.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task StoreSchemaPackageAsync(SqliteConnection connection, SchemaPackage package, CancellationToken cancellationToken) {
        var actualHash = Convert.ToHexString(SHA256.HashData(package.Payload)).ToLowerInvariant();
        if (!string.Equals(actualHash, package.PackageSha256, StringComparison.OrdinalIgnoreCase)) {
            throw new SqlDataPackException("Cannot store schema package because payload hash does not match metadata.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO zsdp_schema_packages(
                                  id, package_type, package_name, package_sha256, created_at_utc, source_database_name, dacfx_version, schema_scope, payload, source_engine_edition)
                              VALUES (
                                  1, $type, $name, $sha256, $created, $source_database, $dacfx_version, $schema_scope, $payload, $source_engine_edition)
                              """;
        command.Parameters.AddWithValue("$type", package.PackageType);
        command.Parameters.AddWithValue("$name", package.PackageName);
        command.Parameters.AddWithValue("$sha256", package.PackageSha256);
        command.Parameters.AddWithValue("$created", package.CreatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$source_database", (object?)package.SourceDatabaseName ?? DBNull.Value);
        command.Parameters.AddWithValue("$dacfx_version", (object?)package.DacFxVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$schema_scope", package.SchemaScope.ToString());
        command.Parameters.Add("$payload", SqliteType.Blob).Value = package.Payload;
        command.Parameters.AddWithValue("$source_engine_edition", (object?)package.SourceEngineEdition ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<SchemaPackage?> ReadSchemaPackageAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT package_type, package_name, package_sha256, created_at_utc, source_database_name, dacfx_version, schema_scope, payload, source_engine_edition
                              FROM zsdp_schema_packages
                              WHERE id = 1
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }

        return new SchemaPackage(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? DacpacSchemaScope.Database : ParseDacpacSchemaScope(reader.GetString(6)), reader.GetFieldValue<byte[]>(7), reader.IsDBNull(8) ? null : reader.GetInt32(8));
    }

    public static async Task<SqlDataPackManifest> ReadManifestAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        try {
            return await ReadManifestCoreAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsCorruptPackageException(ex)) {
            throw new SqlDataPackException("SQLite package is invalid or corrupt and cannot be read. Recreate the package with SqlDataPack export.", ex);
        }
    }

    private static async Task<SqlDataPackManifest> ReadManifestCoreAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        var run = await ReadRunMetadataAsync(connection, cancellationToken);
        var tables = await ReadTablesAsync(connection, cancellationToken);
        var rowCounts = await ReadExpectedRowCountsAsync(connection, cancellationToken);
        var importOrder = (await ReadImportOrderAsync(connection, cancellationToken)).Select(t => t.FullName).ToArray();
        var exclusions = await ReadExclusionsAsync(connection, cancellationToken);
        var warnings = await ReadWarningsAsync(connection, cancellationToken);
        var containsDacpac = await ContainsDacpacAsync(connection, cancellationToken);
        var dacpacSchemaScope = await ReadDacpacSchemaScopeAsync(connection, cancellationToken);
        var sourceEngineEdition = await ReadSourceEngineEditionAsync(connection, cancellationToken);

        var tableManifests = tables.Select(t => ToTableManifest(t, rowCounts.TryGetValue(t.Name.FullName, out var rows) ? rows : 0)).ToArray();

        return new SqlDataPackManifest(run.PackageFormatVersion, run.ApplicationVersion, run.ExportedAtUtc, run.SourceSchemaHash, tableManifests, importOrder, exclusions, warnings, containsDacpac, dacpacSchemaScope, sourceEngineEdition);
    }

    public static SqlDataPackManifest CreatePlannedManifest(ExportPlan plan, ExportOptions options) {
        var tables = plan.Tables.Select(t => ToTableManifest(t, 0, BatchPlanner.GetEffectiveBatchSize(options, t.EstimatedSourceRowCount, t.EstimatedSourceBytes))).ToArray();

        var exclusions = plan.SkippedTables.Select(t => $"table:{t}").Concat(plan.SkippedColumns.Select(c => $"column:{c}")).ToArray();

        return new SqlDataPackManifest(SqlDataPackVersion.PackageFormatVersion, SqlDataPackVersion.PackageVersion, DateTimeOffset.UtcNow, plan.SchemaHash, tables, plan.ImportOrder.Select(t => t.FullName).ToArray(), exclusions, plan.Warnings, options.SchemaCaptureMode == SchemaCaptureMode.Dacpac, options.SchemaCaptureMode == SchemaCaptureMode.Dacpac ? options.DacpacCaptureOptions.SchemaScope : null);
    }

    private static async Task InsertMetadataAsync(SqliteConnection connection, ExportPlan plan, CancellationToken cancellationToken) {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO zsdp_export_runs(id, package_format_version, application_version, exported_at_utc, source_schema_hash) VALUES (1, $format_version, $version, $at, $hash)";
            command.Parameters.AddWithValue("$format_version", SqlDataPackVersion.PackageFormatVersion);
            command.Parameters.AddWithValue("$version", SqlDataPackVersion.PackageVersion);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$hash", plan.SchemaHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var tableIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in plan.Tables) {
            await using var tableCommand = connection.CreateCommand();
            tableCommand.Transaction = transaction;
            tableCommand.CommandText = "INSERT INTO zsdp_tables(source_schema, source_table, sqlite_table) VALUES ($schema, $table, $sqlite); SELECT last_insert_rowid();";
            tableCommand.Parameters.AddWithValue("$schema", table.Name.Schema);
            tableCommand.Parameters.AddWithValue("$table", table.Name.Name);
            tableCommand.Parameters.AddWithValue("$sqlite", table.SqliteTableName);
            var id = Convert.ToInt64(await tableCommand.ExecuteScalarAsync(cancellationToken));
            tableIds[table.Name.FullName] = id;

            foreach (var column in table.Columns) {
                await using var columnCommand = connection.CreateCommand();
                columnCommand.Transaction = transaction;
                columnCommand.CommandText = """
                                            INSERT INTO zsdp_columns(
                                                table_id, column_name, ordinal, sql_server_type_name, max_length, precision_value, scale_value,
                                                is_nullable, is_identity, is_computed, is_excluded, collation_name, vector_base_type, vector_dimensions)
                                            VALUES (
                                                $table_id, $name, $ordinal, $type, $max_length, $precision, $scale,
                                                $nullable, $identity, $computed, $excluded, $collation, $vector_base_type, $vector_dimensions)
                                            """;
                columnCommand.Parameters.AddWithValue("$table_id", id);
                columnCommand.Parameters.AddWithValue("$name", column.Name);
                columnCommand.Parameters.AddWithValue("$ordinal", column.Ordinal);
                columnCommand.Parameters.AddWithValue("$type", column.SqlServerTypeName);
                columnCommand.Parameters.AddWithValue("$max_length", column.MaxLength);
                columnCommand.Parameters.AddWithValue("$precision", column.Precision);
                columnCommand.Parameters.AddWithValue("$scale", column.Scale);
                columnCommand.Parameters.AddWithValue("$nullable", column.IsNullable ? 1 : 0);
                columnCommand.Parameters.AddWithValue("$identity", column.IsIdentity ? 1 : 0);
                columnCommand.Parameters.AddWithValue("$computed", column.IsComputed ? 1 : 0);
                columnCommand.Parameters.AddWithValue("$excluded", column.IsExcluded ? 1 : 0);
                columnCommand.Parameters.AddWithValue("$collation", (object?)column.CollationName ?? DBNull.Value);
                columnCommand.Parameters.AddWithValue("$vector_base_type", (object?)column.VectorBaseType ?? DBNull.Value);
                columnCommand.Parameters.AddWithValue("$vector_dimensions", (object?)column.VectorDimensions ?? DBNull.Value);
                await columnCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await InsertStringsAsync(connection, transaction, "zsdp_exclusions", "exclusion_type", "target_name", "table", plan.SkippedTables, cancellationToken);
        await InsertStringsAsync(connection, transaction, "zsdp_exclusions", "exclusion_type", "target_name", "column", plan.SkippedColumns, cancellationToken);
        await InsertSingleColumnStringsAsync(connection, transaction, "zsdp_warnings", "warning_text", plan.Warnings, cancellationToken);

        for (var index = 0; index < plan.ImportOrder.Count; index++) {
            await using var planCommand = connection.CreateCommand();
            planCommand.Transaction = transaction;
            planCommand.CommandText = "INSERT INTO zsdp_import_plan(sequence, source_schema, source_table) VALUES ($sequence, $schema, $table)";
            planCommand.Parameters.AddWithValue("$sequence", index);
            planCommand.Parameters.AddWithValue("$schema", plan.ImportOrder[index].Schema);
            planCommand.Parameters.AddWithValue("$table", plan.ImportOrder[index].Name);
            await planCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CreateDataTablesAsync(SqliteConnection connection, IReadOnlyList<TableMetadata> tables, CancellationToken cancellationToken) {
        foreach (var table in tables) {
            var columns = table.ExportedColumns.Select(c => $"{SqlDataPackIdentifier.QuoteSqliteName(c.Name)} {ValueConverter.SqliteTypeFor(c)}").ToArray();
            var sql = $"CREATE TABLE {SqlDataPackIdentifier.QuoteSqliteName(table.SqliteTableName)} ({string.Join(", ", columns)})";
            await ExecuteNonQueryAsync(connection, sql, cancellationToken);
        }
    }

    private static async Task ValidatePackageFormatVersionAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        if (!await ColumnExistsAsync(connection, "zsdp_export_runs", "package_format_version", cancellationToken)) {
            throw new SqlDataPackException("SQLite package is invalid: package format version metadata is missing. Recreate the package with SqlDataPack 1.0 or later.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_format_version FROM zsdp_export_runs WHERE id = 1";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull) {
            throw new SqlDataPackException("SQLite package is invalid: export run metadata is missing. Recreate the package with SqlDataPack export.");
        }

        var actual = Convert.ToInt32(value);
        if (actual > SqlDataPackVersion.PackageFormatVersion) {
            throw new SqlDataPackException($"SQLite package format version '{actual}' was produced by a newer version of SqlDataPack. This version reads package format versions up to '{SqlDataPackVersion.PackageFormatVersion}'. Upgrade SqlDataPack to import this package.");
        }

        if (actual < SqlDataPackVersion.MinimumSupportedPackageFormatVersion) {
            throw new SqlDataPackException($"SQLite package format version '{actual}' is no longer supported. This version reads package format version '{SqlDataPackVersion.MinimumSupportedPackageFormatVersion}' and later. Recreate the package with SqlDataPack export, or import it with a SqlDataPack version that still reads format version '{actual}'.");
        }
    }

    private static async Task<RunMetadata> ReadRunMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT package_format_version, application_version, exported_at_utc, source_schema_hash
                              FROM zsdp_export_runs
                              WHERE id = 1
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            throw new SqlDataPackException("SQLite package is invalid: export run metadata is missing. Recreate the package with SqlDataPack export.");
        }

        var exportedAt = DateTimeOffset.TryParse(reader.GetString(2), out var parsed) ? parsed : DateTimeOffset.MinValue;

        return new RunMetadata(reader.GetInt32(0), reader.GetString(1), exportedAt, reader.GetString(3));
    }

    /// <summary>
    /// The rows the data table holds right now, which is not necessarily what the export recorded:
    /// the package is editable on purpose, so a caller may have deleted or inserted rows since.
    /// </summary>
    public static async Task<long> ReadActualRowCountAsync(SqliteConnection connection, string sqliteTableName, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {SqlDataPackIdentifier.QuoteSqliteName(sqliteTableName)}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public static async Task<IReadOnlyDictionary<string, long>> ReadExpectedRowCountsAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT t.source_schema || '.' || t.source_table, s.exported_row_count
                              FROM zsdp_table_stats s
                              INNER JOIN zsdp_tables t ON t.id = s.table_id
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    public static async Task<IReadOnlyList<string>> ReadWarningsAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        return await ReadStringsAsync(connection, "SELECT warning_text FROM zsdp_warnings ORDER BY rowid", cancellationToken);
    }

    private static async Task<bool> ContainsDacpacAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM zsdp_schema_packages
                              WHERE id = 1
                                AND package_type = 'dacpac'
                              """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<DacpacSchemaScope?> ReadDacpacSchemaScopeAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT schema_scope
                              FROM zsdp_schema_packages
                              WHERE id = 1
                                AND package_type = 'dacpac'
                              """;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull) {
            return null;
        }

        return ParseDacpacSchemaScope(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static DacpacSchemaScope ParseDacpacSchemaScope(string value) {
        return Enum.TryParse<DacpacSchemaScope>(value, ignoreCase: true, out var scope) ? scope : DacpacSchemaScope.Database;
    }

    private static async Task<int?> ReadSourceEngineEditionAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT source_engine_edition
                              FROM zsdp_schema_packages
                              WHERE id = 1
                              """;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull) {
            return null;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadExclusionsAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        return await ReadStringsAsync(connection, """
                                                  SELECT exclusion_type || ':' || target_name
                                                  FROM zsdp_exclusions
                                                  ORDER BY exclusion_type, target_name
                                                  """, cancellationToken);
    }

    private static SqlDataPackTableManifest ToTableManifest(TableMetadata table, long exportedRowCount, int? exportBatchSize = null) {
        return new SqlDataPackTableManifest(table.Name.Schema, table.Name.Name, table.SqliteTableName, exportedRowCount, table.EstimatedSourceRowCount, table.EstimatedSourceBytes, exportBatchSize ?? table.ExportBatchSize, table.Columns.OrderBy(c => c.Ordinal).Select(c => new SqlDataPackColumnManifest(c.Name, c.Ordinal, c.SqlServerTypeName, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsIdentity, c.IsComputed, c.IsExcluded, c.CollationName, c.VectorBaseType, c.VectorDimensions)).ToArray());
    }

    private static async Task ValidateTableStatsSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        string[] requiredColumns = [
            "exported_row_count",
            "estimated_source_row_count",
            "estimated_source_bytes",
            "export_batch_size"
        ];

        foreach (var column in requiredColumns) {
            if (!await ColumnExistsAsync(connection, "zsdp_table_stats", column, cancellationToken)) {
                throw new SqlDataPackException($"SQLite package is invalid: table sizing metadata column 'zsdp_table_stats.{column}' is missing. Recreate the package with this version of SqlDataPack export.");
            }
        }
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM sqlite_master
                              WHERE type = 'table' AND name = $name
                              """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}') WHERE name = $name";
        command.Parameters.AddWithValue("$name", columnName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task InsertStringsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string kindColumn, string valueColumn, string kind, IReadOnlyList<string> values, CancellationToken cancellationToken) {
        foreach (var value in values) {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table}({kindColumn}, {valueColumn}) VALUES ($kind, $value)";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertSingleColumnStringsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, IReadOnlyList<string> values, CancellationToken cancellationToken) {
        foreach (var value in values) {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table}({column}) VALUES ($value)";
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

internal static class SqlDataPackVersion {
    // The package format version this build writes.
    public const int PackageFormatVersion = 1;

    // The oldest package format version this build can read. Import accepts the inclusive range
    // [MinimumSupportedPackageFormatVersion, PackageFormatVersion] so a later 1.x release keeps
    // reading packages written by earlier 1.x releases. Only a new major version may raise it.
    public const int MinimumSupportedPackageFormatVersion = 1;

    public static string PackageVersion { get; } = typeof(SqlDataPackVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(SqlDataPackVersion).Assembly.GetName().Version?.ToString() ?? "1.0.0";
}

internal sealed record RunMetadata(int PackageFormatVersion, string ApplicationVersion, DateTimeOffset ExportedAtUtc, string SourceSchemaHash);
