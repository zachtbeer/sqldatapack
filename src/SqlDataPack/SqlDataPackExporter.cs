using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using SqlDataPack.Internal;
using SqlDataPack.Models;

namespace SqlDataPack;

/// <summary>
/// Exports SQL Server table data into a self-describing SQLite package.
/// </summary>
public sealed class SqlDataPackExporter {
    /// <summary>
    /// Exports selected SQL Server user tables into a SQLite package.
    /// </summary>
    /// <param name="sqlServerConnectionString">The SQL Server source connection string.</param>
    /// <param name="sqliteFilePath">The destination SQLite package path.</param>
    /// <param name="options">The export options. When omitted, all user tables are exported.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A summary of exported tables, rows, and warnings.</returns>
    public async Task<SqlDataPackResult> ExportAsync(string sqlServerConnectionString, string sqliteFilePath, ExportOptions? options = null, CancellationToken cancellationToken = default) {
        options ??= ExportOptions.Default;
        BatchPlanner.Validate(options);
        var progress = SqlDataPackProgressLog.Wrap(options.Progress, options.Logger);
        progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.OperationStarted, Message: "Export started."));

        var destinationPath = Path.GetFullPath(sqliteFilePath);
        if (File.Exists(destinationPath) && !options.OverwriteExistingPackage) {
            throw new SqlDataPackException($"SQLite package '{destinationPath}' already exists. Set OverwriteExistingPackage to true to replace it after a successful export.");
        }

        var plan = await SqlServerSchemaReader.CreateExportPlanAsync(sqlServerConnectionString, options, cancellationToken);
        var warnings = BuildExportWarnings(plan, options);
        plan = plan with { Warnings = warnings };
        ReportWarnings(progress, warnings);
        SchemaPackage? schemaPackage = options.SchemaCaptureMode switch {
            SchemaCaptureMode.None => null,
            SchemaCaptureMode.Dacpac => await DacpacSchemaManager.ExtractAsync(sqlServerConnectionString, plan, options.DacpacCaptureOptions, cancellationToken),
            _ => throw new SqlDataPackException($"SchemaCaptureMode '{options.SchemaCaptureMode}' is not supported.")
        };
        var tempPath = CreateTemporaryPackagePath(destinationPath);
        var sqliteBuilder = new SqliteConnectionStringBuilder { DataSource = tempPath };
        await using var sqlite = new SqliteConnection(sqliteBuilder.ConnectionString);

        try {
            await sqlite.OpenAsync(cancellationToken);
            await SqlitePackage.InitializeAsync(sqlite, plan, cancellationToken);
            if (schemaPackage is not null) {
                await SqlitePackage.StoreSchemaPackageAsync(sqlite, schemaPackage, cancellationToken);
            }

            await using var sqlServer = new SqlConnection(sqlServerConnectionString);
            await sqlServer.OpenAsync(cancellationToken);

            var tablesByName = plan.Tables.ToDictionary(t => t.Name.FullName, StringComparer.OrdinalIgnoreCase);

            long totalRows = 0;
            foreach (var table in plan.ImportOrder.Select(name => tablesByName[name.FullName])) {
                var batchSize = BatchPlanner.GetEffectiveBatchSize(options, table.EstimatedSourceRowCount, table.EstimatedSourceBytes);
                progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.TableStarted, table.Name.FullName, TotalRows: table.EstimatedSourceRowCount));
                var rows = await ExportTableAsync(sqlServer, sqlite, table, batchSize, options.CommandTimeout, progress, cancellationToken);
                await SqlitePackage.RecordTableStatsAsync(sqlite, table, rows, batchSize, cancellationToken);
                totalRows += rows;
                progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.TableCompleted, table.Name.FullName, rows, rows));
            }

            await sqlite.CloseAsync();
            // Pooling keeps the sqlite3 file handle alive past CloseAsync/Dispose.
            // Evict it so the move/delete below can succeed on Windows.
            SqliteConnection.ClearPool(sqlite);
            File.Move(tempPath, destinationPath, options.OverwriteExistingPackage);

            progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.OperationCompleted, RowsProcessed: totalRows, TotalRows: totalRows, Message: "Export completed."));
            return new SqlDataPackResult(plan.Tables.Count, totalRows, plan.Warnings);
        }
        catch {
            // Same ordering the success path uses: the pooled sqlite3 file handle outlives
            // CloseAsync, and ClearPool cannot evict a handle the open connection still holds,
            // so the delete below would fail and be swallowed.
            try {
                await sqlite.CloseAsync();
                SqliteConnection.ClearPool(sqlite);
            }
            catch {
                /* best effort */
            }

            DeleteTemporaryPackage(tempPath, progress);
            throw;
        }
    }

    /// <summary>
    /// Validates an export plan without creating a SQLite package or copying rows.
    /// </summary>
    /// <param name="sqlServerConnectionString">The SQL Server source connection string.</param>
    /// <param name="options">The export options. When omitted, all user tables are included.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The preflight validation result.</returns>
    public async Task<SqlDataPackPreflightResult> PreflightAsync(string sqlServerConnectionString, ExportOptions? options = null, CancellationToken cancellationToken = default) {
        options ??= ExportOptions.Default;
        var errors = new List<string>();
        try {
            BatchPlanner.Validate(options);
            if (options.SchemaCaptureMode == SchemaCaptureMode.Dacpac) {
                var builder = new SqlConnectionStringBuilder(sqlServerConnectionString);
                if (string.IsNullOrWhiteSpace(builder.InitialCatalog)) {
                    errors.Add("Schema capture requires a SQL Server connection string with a database name.");
                }
            }

            var plan = await SqlServerSchemaReader.CreateExportPlanAsync(sqlServerConnectionString, options, cancellationToken);
            var warnings = BuildExportWarnings(plan, options);
            plan = plan with { Warnings = warnings };
            return new SqlDataPackPreflightResult(errors.Count == 0, errors, warnings, SqlitePackage.CreatePlannedManifest(plan, options));
        }
        catch (SqlDataPackException exception) {
            errors.Add(exception.Message);
        }
        catch (SqlException exception) {
            errors.Add(exception.Message);
        }

        return new SqlDataPackPreflightResult(false, errors, Array.Empty<string>(), null);
    }

    private static string CreateTemporaryPackagePath(string destinationPath) {
        var directory = Path.GetDirectoryName(destinationPath);
        var fileName = Path.GetFileName(destinationPath);
        return Path.Combine(string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static void DeleteTemporaryPackage(string tempPath, IProgress<SqlDataPackProgress>? progress) {
        try {
            if (File.Exists(tempPath)) {
                File.Delete(tempPath);
            }
        }
        catch (IOException exception) {
            // Preserve the original export failure; the caller still needs to know a file was left behind.
            progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.Warning, Message: $"Temporary package '{tempPath}' could not be deleted after the export failed: {exception.Message}"));
        }
        catch (UnauthorizedAccessException exception) {
            progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.Warning, Message: $"Temporary package '{tempPath}' could not be deleted after the export failed: {exception.Message}"));
        }
    }

    private static async Task<long> ExportTableAsync(SqlConnection sqlServer, SqliteConnection sqlite, TableMetadata table, int batchSize, int? commandTimeout, IProgress<SqlDataPackProgress>? progress, CancellationToken cancellationToken) {
        var columns = table.ExportedColumns;
        var selectColumns = string.Join(", ", columns.Select(c => SqlDataPackIdentifier.QuoteSqlServerName(c.Name)));
        await using var select = sqlServer.CreateCommand();
        select.CommandText = $"SELECT {selectColumns} FROM {SqlDataPackIdentifier.QuoteSqlServerTable(table.Name)}{SqlServerSchemaReader.BuildWhereSql(table.WhereClauses)}";
        if (commandTimeout.HasValue) {
            select.CommandTimeout = commandTimeout.Value;
        }

        await using var reader = await select.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken);
        return await SqlitePackageWriter.WriteTableAsync(sqlite, reader, table, batchSize, progress, cancellationToken);
    }

    private static IReadOnlyList<string> BuildExportWarnings(ExportPlan plan, ExportOptions options) {
        var warnings = new List<string>(plan.Warnings);
        foreach (var table in plan.Tables) {
            var batchSize = BatchPlanner.GetEffectiveBatchSize(options, table.EstimatedSourceRowCount, table.EstimatedSourceBytes);
            if (options.AdaptiveBatchingEnabled && batchSize < options.BatchSize) {
                warnings.Add($"Adaptive batching set export batch size for '{table.Name.FullName}' to {batchSize} rows.");
            }
        }

        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ReportWarnings(IProgress<SqlDataPackProgress>? progress, IReadOnlyList<string> warnings) {
        if (progress is null) {
            return;
        }

        foreach (var warning in warnings) {
            progress.Report(new SqlDataPackProgress(SqlDataPackProgressKind.Warning, Message: warning));
        }
    }
}
