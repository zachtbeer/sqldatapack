using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using SqlDataPack.Internal;
using SqlDataPack.Models;

namespace SqlDataPack;

/// <summary>
/// Imports a SqlDataPack SQLite package into compatible SQL Server target tables.
/// </summary>
public sealed class SqlDataPackImporter {
    /// <summary>
    /// Imports a SQLite package into empty compatible SQL Server target tables.
    /// </summary>
    /// <param name="sqliteFilePath">The SQLite package path.</param>
    /// <param name="sqlServerConnectionString">The SQL Server target connection string.</param>
    /// <param name="options">The import options.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A summary of imported tables and rows.</returns>
    public async Task<SqlDataPackResult> ImportAsync(string sqliteFilePath, string sqlServerConnectionString, ImportOptions? options = null, CancellationToken cancellationToken = default) {
        options ??= ImportOptions.Default;
        BatchPlanner.Validate(options);
        var progress = SqlDataPackProgressLog.Wrap(options.Progress, options.Logger);
        progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.OperationStarted, Message: "Import started."));

        var sqliteBuilder = new SqliteConnectionStringBuilder { DataSource = sqliteFilePath, Mode = SqliteOpenMode.ReadOnly };
        await using var sqlite = new SqliteConnection(sqliteBuilder.ConnectionString);
        try {
            await sqlite.OpenAsync(cancellationToken);

            await SqlitePackage.ValidateForImportAsync(sqlite, cancellationToken);

            switch (options.SchemaDeploymentMode) {
                case SchemaDeploymentMode.None:
                    break;
                case SchemaDeploymentMode.DeployDacpac:
                    var schemaPackage = await SqlitePackage.ReadSchemaPackageAsync(sqlite, cancellationToken);
                    if (schemaPackage is null) {
                        throw new SqlDataPackException("SQLite package does not contain a dacpac schema package. Export with SchemaCaptureMode.Dacpac before deploying schema during import.");
                    }

                    await DacpacSchemaManager.DeployAsync(sqlServerConnectionString, schemaPackage, options.DacpacDeploymentOptions, allowDacpacObjectDrops: false, cancellationToken);
                    break;
                default:
                    throw new SqlDataPackException($"SchemaDeploymentMode '{options.SchemaDeploymentMode}' is not supported.");
            }

            var tables = await SqlitePackage.ReadTablesAsync(sqlite, cancellationToken);
            var importOrder = await SqlitePackage.ReadImportOrderAsync(sqlite, cancellationToken);
            var packageWarnings = await SqlitePackage.ReadWarningsAsync(sqlite, cancellationToken);
            var warnings = new List<string>(packageWarnings);

            warnings.AddRange(await SqlServerSchemaReader.ValidateImportTargetAsync(sqlServerConnectionString, tables, options.ValidationCommandTimeout, options.FailOnLossyTypeMismatch, cancellationToken));
            ReportWarnings(progress, warnings);

            await using var sqlServer = new SqlConnection(sqlServerConnectionString);
            await sqlServer.OpenAsync(cancellationToken);

            // Declared outside the try so the catch can still restore whatever was suspended.
            IReadOnlyList<TemporalSuspension> temporalSuspensions = Array.Empty<TemporalSuspension>();
            long totalRows = 0;
            try {
                // System-versioned temporal tables reject direct inserts into their history table and into the
                // GENERATED ALWAYS period columns of the current table. Suspend versioning (and drop the period)
                // up front for every affected pair so the per-table bulk-copy loop can load both tables with their
                // original period values, then restore versioning afterwards. Inside the try because a failure
                // partway through the pairs still has to reach the restore in the catch.
                if (options.SuspendTemporalSystemVersioning) {
                    temporalSuspensions = TemporalTableManager.ResolveSuspensions(await TemporalTableManager.DiscoverAsync(sqlServer, options.ValidationCommandTimeout, cancellationToken), tables);
                    await TemporalTableManager.SuspendAsync(sqlServer, temporalSuspensions, options.ValidationCommandTimeout, cancellationToken);
                    foreach (var suspension in temporalSuspensions) {
                        AddWarning(warnings, TemporalTableManager.DescribeSuspend(suspension), progress);
                    }
                }

                foreach (var name in importOrder) {
                    var table = tables.Single(t => string.Equals(t.Name.FullName, name.FullName, StringComparison.OrdinalIgnoreCase));
                    var expected = await SqlitePackage.ReadExpectedRowCountAsync(sqlite, table, cancellationToken);
                    var batchSize = BatchPlanner.GetEffectiveBatchSize(options, expected, table.EstimatedSourceBytes);
                    if (options.AdaptiveBatchingEnabled) {
                        batchSize = Math.Min(batchSize, table.ExportBatchSize);
                    }

                    if (options.AdaptiveBatchingEnabled && batchSize < options.BatchSize) {
                        AddWarning(warnings, $"Adaptive batching set import batch size for '{table.Name.FullName}' to {batchSize} rows.", progress);
                    }

                    // A NULL in a native json destination column silently discards the rest of the SqlBulkCopy batch:
                    // the null row lands, everything after it in the same batch is dropped and WriteToServerAsync returns
                    // normally. Reproduces with a plain DataTable and no SqlDataPack code in the path, on SqlClient 6.1.5
                    // and 7.0.2 alike. One row per batch means there is never a row after the null left to lose.
                    if (batchSize != 1 && table.ExportedColumns.Any(c => c.Kind == ColumnKind.Json && c.IsNullable)) {
                        batchSize = 1;
                        AddWarning(warnings, $"Table '{table.Name.FullName}' has a nullable json column, so its rows are imported one batch at a time. Bulk copy silently drops rows following a null json value. This is slow and is a workaround for a SqlBulkCopy defect, not a SqlDataPack setting.", progress);
                    }

                    progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.TableStarted, table.Name.FullName, TotalRows: expected));
                    var rows = await ImportTableAsync(sqlite, sqlServer, table, batchSize, options.BulkCopyTimeout, progress, expected, cancellationToken);
                    if (rows != expected) {
                        throw new SqlDataPackException($"Imported row count for '{table.Name.FullName}' was {rows}, expected {expected}. Earlier tables in this import have already committed; every target table in this import scope has to be emptied before you retry.");
                    }

                    totalRows += rows;
                    progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.TableCompleted, table.Name.FullName, rows, expected));
                }

                await CheckConstraintsAsync(sqlServer, importOrder, options.ValidationCommandTimeout, warnings, progress, cancellationToken);

                await TemporalTableManager.RestoreAsync(sqlServer, temporalSuspensions, options.TemporalDataConsistencyCheck, options.ValidationCommandTimeout, cancellationToken);
            }
            catch {
                // The data load or restore failed; try not to strand temporal tables with versioning off.
                await TemporalTableManager.TryRestoreBestEffortAsync(sqlServer, temporalSuspensions, options.TemporalDataConsistencyCheck, warnings);
                throw;
            }

            progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.OperationCompleted, RowsProcessed: totalRows, TotalRows: totalRows, Message: "Import completed."));
            return new SqlDataPackResult(tables.Count, totalRows, warnings.Distinct(StringComparer.Ordinal).ToArray());
        }
        finally {
            // Release the pooled sqlite3 file handle so callers (and tests) can
            // safely delete or move the package file on Windows.
            try {
                SqliteConnection.ClearPool(sqlite);
            }
            catch {
                /* best effort */
            }
        }
    }

    /// <summary>
    /// Validates an import without deploying schema or copying rows.
    /// </summary>
    /// <param name="sqliteFilePath">The SQLite package path.</param>
    /// <param name="sqlServerConnectionString">The SQL Server target connection string.</param>
    /// <param name="options">The import options.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The preflight validation result.</returns>
    public async Task<SqlDataPackPreflightResult> PreflightAsync(string sqliteFilePath, string sqlServerConnectionString, ImportOptions? options = null, CancellationToken cancellationToken = default) {
        options ??= ImportOptions.Default;
        var errors = new List<string>();
        try {
            BatchPlanner.Validate(options);
            var sqliteBuilder = new SqliteConnectionStringBuilder { DataSource = sqliteFilePath, Mode = SqliteOpenMode.ReadOnly };
            await using var sqlite = new SqliteConnection(sqliteBuilder.ConnectionString);
            try {
                await sqlite.OpenAsync(cancellationToken);

                await SqlitePackage.ValidateForImportAsync(sqlite, cancellationToken);
                var manifest = await SqlitePackage.ReadManifestAsync(sqlite, cancellationToken);
                if (options.SchemaDeploymentMode == SchemaDeploymentMode.DeployDacpac && !manifest.ContainsDacpac) {
                    errors.Add("SQLite package does not contain a dacpac schema package. Export with SchemaCaptureMode.Dacpac before deploying schema during import.");
                }

                if (options.SchemaDeploymentMode == SchemaDeploymentMode.DeployDacpac && manifest.DacpacSchemaScope == DacpacSchemaScope.SelectedExportTables && options.DacpacDeploymentOptions.AllowObjectDrops) {
                    errors.Add("DacpacDeploymentOptions.AllowObjectDrops cannot be used with a selected-table dacpac schema package because unrelated target objects would be compared against a reduced source model.");
                }

                // Surface the cross-platform foot-gun: if the source is stamped as Azure and the user has
                // disabled the auto-adaptation, the deploy is very likely to fail with Msg 12824 on a
                // non-Azure target. We don't probe the target here (preflight should stay cheap and
                // network-only-once) — this is a heads-up, not a hard block.
                if (options.SchemaDeploymentMode == SchemaDeploymentMode.DeployDacpac && manifest.SourceEngineEdition is int sourceEdition && (sourceEdition is 5 or 8 or 11 or 12) && !options.DacpacDeploymentOptions.AdaptAzureSourceForOnPremTarget) {
                    errors.Insert(0, "Schema package was extracted from Azure SQL but DacpacDeploymentOptions.AdaptAzureSourceForOnPremTarget is disabled. Deploys to non-Azure SQL Server targets will fail with Msg 12824 (contained database authentication) unless the target has 'contained database authentication' enabled via sp_configure. Re-enable the flag or set DeployDatabaseOptions=true to proceed.");
                }

                var tables = await SqlitePackage.ReadTablesAsync(sqlite, cancellationToken);
                IReadOnlyList<string> typeWarnings = [];
                try {
                    typeWarnings = await SqlServerSchemaReader.ValidateImportTargetAsync(sqlServerConnectionString, tables, options.ValidationCommandTimeout, options.FailOnLossyTypeMismatch, cancellationToken);
                }
                catch (SqlDataPackException exception) {
                    errors.Add(exception.Message);
                }
                catch (SqlException exception) {
                    errors.Add(exception.Message);
                }

                var warnings = BuildImportWarnings(tables, manifest.Warnings, options).Concat(typeWarnings).Distinct(StringComparer.Ordinal).ToArray();
                return new SqlDataPackPreflightResult(errors.Count == 0, errors, warnings, manifest);
            }
            finally {
                try {
                    SqliteConnection.ClearPool(sqlite);
                }
                catch {
                    /* best effort */
                }
            }
        }
        catch (SqlDataPackException exception) {
            errors.Add(exception.Message);
        }
        catch (SqlException exception) {
            errors.Add(exception.Message);
        }


        return new SqlDataPackPreflightResult(false, errors, Array.Empty<string>(), null);
    }

    private static async Task<long> ImportTableAsync(SqliteConnection sqlite, SqlConnection sqlServer, TableMetadata table, int batchSize, int? bulkCopyTimeout, IProgress<SqlDataPackProgress>? progress, long expectedRows, CancellationToken cancellationToken) {
        var columns = table.ExportedColumns.Where(c => !ValueConverter.IsServerGenerated(c.SqlServerTypeName)).ToArray();
        var sqliteColumns = string.Join(", ", columns.Select(c => SqlDataPackIdentifier.QuoteSqliteName(c.Name)));

        await using var select = sqlite.CreateCommand();
        select.CommandText = $"SELECT {sqliteColumns} FROM {SqlDataPackIdentifier.QuoteSqliteName(table.SqliteTableName)}";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);

        var rows = 0L;
        using var bulk = new SqlBulkCopy(sqlServer, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.UseInternalTransaction, null) {
            DestinationTableName = SqlDataPackIdentifier.QuoteSqlServerTable(table.Name),
            BatchSize = batchSize,
            EnableStreaming = true
        };
        if (bulkCopyTimeout.HasValue) {
            bulk.BulkCopyTimeout = bulkCopyTimeout.Value;
        }

        foreach (var column in columns) {
            bulk.ColumnMappings.Add(column.Name, column.Name);
        }

        var projectingReader = new SqliteCoercingDataReader(reader, columns, () => {
            rows++;
            if (rows % batchSize == 0) {
                progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, table.Name.FullName, rows, expectedRows));
            }
        });
        await bulk.WriteToServerAsync(projectingReader, cancellationToken);

        // rows counts what came out of SQLite, not what SqlBulkCopy took. A bulk copy that stops early
        // returns normally, so without this the caller gets a success and a row count that says nothing
        // was lost. The target is required to be empty before import, so COUNT_BIG is this import's total.
        var landed = await ScalarLongAsync(sqlServer, $"SELECT COUNT_BIG(*) FROM {SqlDataPackIdentifier.QuoteSqlServerTable(table.Name)}", bulkCopyTimeout, cancellationToken);
        if (landed != rows) {
            throw new SqlDataPackException($"Bulk copy for '{table.Name.FullName}' read {rows} rows from the package but {landed} landed in the target. The import stopped early and the target table holds a partial load; every target table in this import scope has to be emptied before you retry.");
        }

        if (rows == 0 || rows % batchSize != 0) {
            progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, table.Name.FullName, rows, expectedRows));
        }

        return rows;
    }

    private static async Task CheckConstraintsAsync(SqlConnection sqlServer, IReadOnlyList<TableName> importOrder, int? commandTimeout, List<string> warnings, IProgress<SqlDataPackProgress>? progress, CancellationToken cancellationToken) {
        // Bulk copy does not evaluate constraints, so everything it loaded is untrusted and possibly
        // invalid. Re-checking here rather than via SqlBulkCopyOptions.CheckConstraints keeps the load
        // fast and avoids failing on an FK whose referenced table has not been loaded yet.
        //
        // An FK's referenced table might not be in this import at all: the caller may be populating the
        // parent separately, or it may already hold matching data in the target. That's not corruption, so a
        // violation there only warns and leaves the constraint untrusted. An FK whose referenced table IS in
        // the import order is a different story -- the package is supposed to be self-consistent -- so that
        // still throws, and so does every CHECK constraint, which is self-contained by definition.
        //
        // ALTER TABLE ... CHECK CONSTRAINT ALL can't express that split, so constraints are enumerated and
        // re-checked one at a time instead.
        var inPackage = new HashSet<string>(importOrder.Select(t => t.FullName), StringComparer.OrdinalIgnoreCase);

        foreach (var table in importOrder) {
            foreach (var foreignKey in await ReadForeignKeysAsync(sqlServer, table, commandTimeout, cancellationToken)) {
                try {
                    await CheckConstraintAsync(sqlServer, table, foreignKey.Name, commandTimeout, cancellationToken);
                }
                catch (SqlException exception) when (exception.Number == 547) {
                    if (inPackage.Contains(foreignKey.ReferencedTable.FullName)) {
                        throw new SqlDataPackException($"Imported rows in '{table.FullName}' violate a constraint the target defines: {exception.Message} The rows are loaded but the constraint is left untrusted; every target table in this import scope has to be emptied before you re-import against a matching source, or exclude the table.", exception);
                    }

                    AddWarning(warnings, $"Imported rows in '{table.FullName}' have no matching parent row in '{foreignKey.ReferencedTable.FullName}' for foreign key '{foreignKey.Name}'. The rows are loaded but the constraint stays untrusted until '{foreignKey.ReferencedTable.FullName}' is populated with the matching rows.", progress);
                }
            }

            foreach (var checkConstraintName in await ReadCheckConstraintNamesAsync(sqlServer, table, commandTimeout, cancellationToken)) {
                try {
                    await CheckConstraintAsync(sqlServer, table, checkConstraintName, commandTimeout, cancellationToken);
                }
                catch (SqlException exception) when (exception.Number == 547) {
                    throw new SqlDataPackException($"Imported rows in '{table.FullName}' violate a constraint the target defines: {exception.Message} The rows are loaded but the constraint is left untrusted; every target table in this import scope has to be emptied before you re-import against a matching source, or exclude the table.", exception);
                }
            }
        }
    }

    private static async Task CheckConstraintAsync(SqlConnection sqlServer, TableName table, string constraintName, int? commandTimeout, CancellationToken cancellationToken) {
        await using var command = sqlServer.CreateCommand();
        command.CommandText = $"ALTER TABLE {SqlDataPackIdentifier.QuoteSqlServerTable(table)} WITH CHECK CHECK CONSTRAINT {SqlDataPackIdentifier.QuoteSqlServerName(constraintName)};";
        if (commandTimeout.HasValue) {
            command.CommandTimeout = commandTimeout.Value;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<(string Name, TableName ReferencedTable)>> ReadForeignKeysAsync(SqlConnection sqlServer, TableName table, int? commandTimeout, CancellationToken cancellationToken) {
        await using var command = sqlServer.CreateCommand();
        command.CommandText = """
                              SELECT fk.name, rs.name, rt.name
                              FROM sys.foreign_keys fk
                              JOIN sys.tables t ON t.object_id = fk.parent_object_id
                              JOIN sys.schemas s ON s.schema_id = t.schema_id
                              JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
                              JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                              WHERE s.name = @schema AND t.name = @table AND fk.is_disabled = 0;
                              """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        if (commandTimeout.HasValue) {
            command.CommandTimeout = commandTimeout.Value;
        }

        var foreignKeys = new List<(string, TableName)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            foreignKeys.Add((reader.GetString(0), new TableName(reader.GetString(1), reader.GetString(2))));
        }

        return foreignKeys;
    }

    private static async Task<IReadOnlyList<string>> ReadCheckConstraintNamesAsync(SqlConnection sqlServer, TableName table, int? commandTimeout, CancellationToken cancellationToken) {
        await using var command = sqlServer.CreateCommand();
        command.CommandText = """
                              SELECT cc.name
                              FROM sys.check_constraints cc
                              JOIN sys.tables t ON t.object_id = cc.parent_object_id
                              JOIN sys.schemas s ON s.schema_id = t.schema_id
                              WHERE s.name = @schema AND t.name = @table AND cc.is_disabled = 0;
                              """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        if (commandTimeout.HasValue) {
            command.CommandTimeout = commandTimeout.Value;
        }

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> ScalarLongAsync(SqlConnection connection, string sql, int? commandTimeout, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (commandTimeout.HasValue) {
            command.CommandTimeout = commandTimeout.Value;
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static IReadOnlyList<string> BuildImportWarnings(IReadOnlyList<TableMetadata> tables, IReadOnlyList<string> packageWarnings, ImportOptions options) {
        var warnings = new List<string>(packageWarnings);
        foreach (var table in tables) {
            var batchSize = BatchPlanner.GetEffectiveBatchSize(options, table.EstimatedSourceRowCount, table.EstimatedSourceBytes);
            if (options.AdaptiveBatchingEnabled) {
                batchSize = Math.Min(batchSize, table.ExportBatchSize);
            }

            if (options.AdaptiveBatchingEnabled && batchSize < options.BatchSize) {
                warnings.Add($"Adaptive batching set import batch size for '{table.Name.FullName}' to {batchSize} rows.");
            }

            foreach (var column in table.ExportedColumns.Where(c => ValueConverter.IsServerGenerated(c.SqlServerTypeName))) {
                warnings.Add($"Table '{table.Name.FullName}' column '{column.Name}' is a {column.SqlServerTypeName}; values from the package are skipped and SQL Server will generate fresh values on import.");
            }
        }

        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddWarning(List<string> warnings, string warning, IProgress<SqlDataPackProgress>? progress) {
        if (!warnings.Contains(warning, StringComparer.Ordinal)) {
            warnings.Add(warning);
            progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.Warning, Message: warning));
        }
    }

    private static void ReportWarnings(IProgress<SqlDataPackProgress>? progress, IReadOnlyList<string> warnings) {
        if (progress is null) {
            return;
        }

        foreach (var warning in warnings.Distinct(StringComparer.Ordinal)) {
            progress.Report(new SqlDataPackProgress(SqlDataPackProgressKind.Warning, Message: warning));
        }
    }
}
