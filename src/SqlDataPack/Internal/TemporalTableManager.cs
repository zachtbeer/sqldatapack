using Microsoft.Data.SqlClient;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal sealed record TemporalTable(TableName Current, TableName History, string PeriodStartColumn, string PeriodEndColumn, int RetentionPeriod, string? RetentionUnit) {
    /// <summary>True when the table has a finite history retention period that must be re-applied on restore.</summary>
    public bool HasFiniteRetention => RetentionPeriod >= 0 && !string.IsNullOrWhiteSpace(RetentionUnit);
}

/// <summary>A temporal pair selected for the suspend/restore ceremony, plus whether its period must be dropped.</summary>
internal sealed record TemporalSuspension(TemporalTable Table, bool DropPeriod);

/// <summary>
/// Discovers system-versioned temporal tables on a target database and brackets a data load with the
/// "stop system-versioning" ceremony so both the current and history rows can be inserted with their
/// original period (<c>ValidFrom</c>/<c>ValidTo</c>) values, then re-enables versioning.
/// </summary>
/// <remarks>
/// SQL Server forbids direct inserts into a temporal history table (Msg 13560) and into the
/// <c>GENERATED ALWAYS</c> period columns of the current table (Msg 13536) while system versioning is on.
/// Setting <c>SYSTEM_VERSIONING = OFF</c> alone is not enough: while the <c>SYSTEM_TIME</c> period is still
/// defined the engine keeps auto-populating the period columns, so writing explicit period values into the
/// current table additionally requires <c>DROP PERIOD FOR SYSTEM_TIME</c>. The reverse pair
/// (<c>ADD PERIOD</c> then <c>SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = ...))</c>) restores the table.
/// All statements are guarded on current catalog state so suspend/restore are idempotent and a best-effort
/// restore after a failed import is safe.
/// <para>
/// The period is only dropped when the package actually carries the period column values
/// (<see cref="ResolveSuspensions"/>). When it does not — a non-temporal source loaded into a temporal
/// target, or period columns excluded during export — versioning stays on and SQL Server auto-populates the
/// period. Finite <c>HISTORY_RETENTION_PERIOD</c> is captured before suspending (SET OFF resets it to
/// INFINITE) and re-applied on restore.
/// </para>
/// </remarks>
internal static class TemporalTableManager {
    public static async Task<IReadOnlyList<TemporalTable>> DiscoverAsync(SqlConnection connection, int? commandTimeout, CancellationToken cancellationToken) {
        // temporal_type = 2 is SYSTEM_VERSIONED_TEMPORAL_TABLE (the current table); history_table_id points
        // at its paired history table. period_type = 1 is the SYSTEM_TIME period; start/end column ids map to
        // the GENERATED ALWAYS AS ROW START / ROW END columns. history_retention_period is -1 for INFINITE and
        // must be read here (before SET SYSTEM_VERSIONING = OFF, which resets retention to INFINITE).
        const string sql = """
                           SELECT cs.name, ct.name, hs.name, ht.name, sc.name, ec.name,
                                  ct.history_retention_period, ct.history_retention_period_unit_desc
                           FROM sys.tables ct
                           INNER JOIN sys.schemas cs ON cs.schema_id = ct.schema_id
                           INNER JOIN sys.tables ht ON ht.object_id = ct.history_table_id
                           INNER JOIN sys.schemas hs ON hs.schema_id = ht.schema_id
                           INNER JOIN sys.periods p ON p.object_id = ct.object_id AND p.period_type = 1
                           INNER JOIN sys.columns sc ON sc.object_id = ct.object_id AND sc.column_id = p.start_column_id
                           INNER JOIN sys.columns ec ON ec.object_id = ct.object_id AND ec.column_id = p.end_column_id
                           WHERE ct.temporal_type = 2;
                           """;

        var result = new List<TemporalTable>();
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            result.Add(new TemporalTable(new TableName(reader.GetString(0), reader.GetString(1)), new TableName(reader.GetString(2), reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? -1 : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    public static IReadOnlyList<TemporalSuspension> ResolveSuspensions(IReadOnlyList<TemporalTable> temporalTables, IReadOnlyList<TableMetadata> tablesInScope) {
        var byName = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tablesInScope) {
            byName[table.Name.FullName] = table;
        }

        var result = new List<TemporalSuspension>();
        foreach (var temporal in temporalTables) {
            var currentInScope = byName.TryGetValue(temporal.Current.FullName, out var currentMeta);
            var historyInScope = byName.ContainsKey(temporal.History.FullName);

            // Drop the period only when the package carries explicit period values for the current table.
            var currentHasPeriodColumns = currentInScope && HasBothPeriodColumns(currentMeta!, temporal);

            // Nothing to do for a current-only table whose period columns were not exported: versioning can
            // stay on and SQL Server auto-populates the period on insert.
            if (!historyInScope && !currentHasPeriodColumns) {
                continue;
            }

            result.Add(new TemporalSuspension(temporal, DropPeriod: currentHasPeriodColumns));
        }

        return result;
    }

    public static async Task SuspendAsync(SqlConnection connection, IReadOnlyList<TemporalSuspension> suspensions, int? commandTimeout, CancellationToken cancellationToken) {
        foreach (var suspension in suspensions) {
            await ExecuteAsync(connection, BuildSuspendSql(suspension), commandTimeout, cancellationToken);
        }
    }

    public static async Task RestoreAsync(SqlConnection connection, IReadOnlyList<TemporalSuspension> suspensions, bool dataConsistencyCheck, int? commandTimeout, CancellationToken cancellationToken) {
        foreach (var suspension in suspensions) {
            try {
                await ExecuteAsync(connection, BuildRestoreSql(suspension, dataConsistencyCheck), commandTimeout, cancellationToken);
            }
            catch (SqlException exception) {
                var table = suspension.Table;
                throw new SqlDataPackException(
                    $"Failed to re-enable system versioning on temporal table '{table.Current.FullName}' (history '{table.History.FullName}'). " + "This usually means the current and history rows are inconsistent — for example the temporal table or its history was filtered with a WHERE clause, " + "a period column was excluded, or the source changed between reading the current and history tables. " +
                    "Re-export the full table pair, or set ImportOptions.TemporalDataConsistencyCheck = false to re-enable versioning without the consistency check. " + $"SQL Server reported: {exception.Message}", exception);
            }
        }
    }

    public static async Task TryRestoreBestEffortAsync(SqlConnection connection, IReadOnlyList<TemporalSuspension> suspensions, bool dataConsistencyCheck, List<string> warnings) {
        foreach (var suspension in suspensions) {
            try {
                // Cleanup path after a failed import: use a fresh, non-cancellable token so we still attempt to
                // leave the table system-versioned rather than stranded with SYSTEM_VERSIONING = OFF.
                await ExecuteAsync(connection, BuildRestoreSql(suspension, dataConsistencyCheck), commandTimeout: null, CancellationToken.None);
            }
            catch {
                var table = suspension.Table;
                var warning = $"System versioning could not be re-enabled on temporal table '{table.Current.FullName}' after the import failed; " + $"it was left with SYSTEM_VERSIONING = OFF. Restore it manually with " + $"ALTER TABLE {SqlDataPackIdentifier.QuoteSqlServerTable(table.Current)} SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = {SqlDataPackIdentifier.QuoteSqlServerTable(table.History)})).";
                if (!warnings.Contains(warning, StringComparer.Ordinal)) {
                    warnings.Add(warning);
                }
            }
        }
    }

    public static string DescribeSuspend(TemporalSuspension suspension) {
        var table = suspension.Table;
        var periodNote = suspension.DropPeriod ? " (and its SYSTEM_TIME period dropped)" : string.Empty;
        return $"Temporal table '{table.Current.FullName}': system versioning is temporarily suspended{periodNote} so the current and history rows ('{table.History.FullName}') can be imported, then restored.";
    }

    internal static string BuildSuspendSql(TemporalSuspension suspension) {
        var current = SqlDataPackIdentifier.QuoteSqlServerTable(suspension.Table.Current);
        var literal = Literal(current);
        var dropPeriod = suspension.DropPeriod
            ? $"""

               IF EXISTS (SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID(N'{literal}') AND period_type = 1)
                   ALTER TABLE {current} DROP PERIOD FOR SYSTEM_TIME;
               """
            : string.Empty;
        return $"""
                IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'{literal}') AND temporal_type = 2)
                    ALTER TABLE {current} SET (SYSTEM_VERSIONING = OFF);{dropPeriod}
                """;
    }

    internal static string BuildRestoreSql(TemporalSuspension suspension, bool dataConsistencyCheck) {
        var table = suspension.Table;
        var current = SqlDataPackIdentifier.QuoteSqlServerTable(table.Current);
        var history = SqlDataPackIdentifier.QuoteSqlServerTable(table.History);
        var literal = Literal(current);
        var consistency = dataConsistencyCheck ? "ON" : "OFF";
        // SET SYSTEM_VERSIONING = OFF resets retention to INFINITE, so re-apply a captured finite period.
        var retention = table.HasFiniteRetention ? $", HISTORY_RETENTION_PERIOD = {table.RetentionPeriod} {table.RetentionUnit}" : string.Empty;
        var addPeriod = suspension.DropPeriod
            ? $"""
               IF NOT EXISTS (SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID(N'{literal}') AND period_type = 1)
                   ALTER TABLE {current} ADD PERIOD FOR SYSTEM_TIME ({SqlDataPackIdentifier.QuoteSqlServerName(table.PeriodStartColumn)}, {SqlDataPackIdentifier.QuoteSqlServerName(table.PeriodEndColumn)});

               """
            : string.Empty;
        return $"""
                {addPeriod}IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'{literal}') AND temporal_type = 2)
                    ALTER TABLE {current} SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = {history}, DATA_CONSISTENCY_CHECK = {consistency}{retention}));
                """;
    }

    private static bool HasBothPeriodColumns(TableMetadata current, TemporalTable temporal) {
        var columns = current.ExportedColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return columns.Contains(temporal.PeriodStartColumn) && columns.Contains(temporal.PeriodEndColumn);
    }

    private static string Literal(string value) {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, int? commandTimeout, CancellationToken cancellationToken) {
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ApplyTimeout(SqlCommand command, int? commandTimeout) {
        if (commandTimeout.HasValue) {
            command.CommandTimeout = commandTimeout.Value;
        }
    }
}
