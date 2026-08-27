using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal static class SqlServerSchemaReader {
    public static async Task<ExportPlan> CreateExportPlanAsync(string connectionString, ExportOptions options, CancellationToken cancellationToken) {
        SqlDataPackIdentifier.NormalizeSqliteDataTablePrefix(options.DataTablePrefix);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var allTables = await ReadTablesAsync(connection, options.CommandTimeout, cancellationToken);
        var warnings = new List<string>();
        var selected = ResolveTables(allTables, options, warnings);
        ValidateColumnExclusions(selected, options);
        ValidateGlobalWhereClauses(options);
        ValidatePerTableWhereClauses(selected, options);
        var tableStats = await ReadTableStatsAsync(connection, options.CommandTimeout, warnings, cancellationToken);

        var excludedColumnSet = options.ExcludeColumns.Select(SqlDataPackIdentifier.ParseColumnPath).Select(c => $"{c.Schema}.{c.Table}.{c.Column}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableMetadata>();
        foreach (var table in selected) {
            var columns = await ReadColumnsAsync(connection, table, excludedColumnSet, options.CommandTimeout, cancellationToken);
            var whereClauses = ResolveWhereClauses(table, columns, options);
            tableStats.TryGetValue(table.FullName, out var stats);
            var estimatedRows = whereClauses.Length == 0 ? stats?.EstimatedSourceRowCount ?? 0 : await CountFilteredRowsAsync(connection, table, whereClauses, options.CommandTimeout, cancellationToken);
            tables.Add(new TableMetadata(table, SqlDataPackIdentifier.ToSqliteDataTableName(table, options.DataTablePrefix), columns, estimatedRows, stats?.EstimatedSourceBytes ?? 0, AppliedWhereClauses: whereClauses));
        }

        var transformations = TransformationBinder.Validate(tables, options);
        SqlDataPackIdentifier.ValidateSqliteDataTableNamesUnique(tables);
        SqlDataPackIdentifier.ValidateSqliteDataTableNamesNotReserved(tables);
        ValidateGlobalWhereClauseMatches(tables, options.GlobalWhereClauses);
        warnings.AddRange(BuildGlobalWhereClauseCoverageWarnings(tables, options.GlobalWhereClauses));
        ValidateSupported(tables);
        warnings.AddRange(BuildServerGeneratedColumnWarnings(tables));
        warnings.AddRange(BuildVectorPreviewWarnings(tables));
        var temporalNames = await ReadTemporalCurrentTableNamesAsync(connection, options.CommandTimeout, cancellationToken);
        warnings.AddRange(BuildTemporalTableWarnings(selected, temporalNames));

        var allForeignKeys = await ReadForeignKeysAsync(connection, options.CommandTimeout, cancellationToken);
        var selectedNames = selected.Select(t => t.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignKeys = allForeignKeys.Where(fk => selectedNames.Contains(fk.ParentTable.FullName) && selectedNames.Contains(fk.ReferencedTable.FullName)).ToArray();
        warnings.AddRange(BuildForeignKeyScopeWarnings(allForeignKeys, selectedNames));
        var importOrder = ImportPlanner.BuildImportOrder(selected, foreignKeys);
        var schemaHash = ComputeSchemaHash(tables);
        var skippedTables = allTables.Where(t => !selected.Contains(t)).Select(t => t.FullName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var skippedColumns = tables.SelectMany(t => t.Columns.Where(c => c.IsComputed || c.IsExcluded)).Select(c => $"{c.Table.FullName}.{c.Name}").Order(StringComparer.OrdinalIgnoreCase).ToArray();

        return new ExportPlan(tables.OrderBy(t => t.Name.FullName, StringComparer.OrdinalIgnoreCase).ToArray(), foreignKeys, importOrder, warnings, skippedTables, skippedColumns, schemaHash, transformations);
    }

    public static async Task<IReadOnlyList<string>> ValidateImportTargetAsync(string connectionString, IReadOnlyList<TableMetadata> tables, int? commandTimeout, bool failOnLossyTypeMismatch, CancellationToken cancellationToken) {
        var warnings = new List<string>();
        var lossyColumns = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var table in tables) {
            if (!await TableExistsAsync(connection, table.Name, commandTimeout, cancellationToken)) {
                throw new SqlDataPackException($"Target table '{table.Name.FullName}' does not exist. Create the target schema before import or exclude this table from the export scope.");
            }

            var targetColumns = await ReadTargetColumnsAsync(connection, table.Name, commandTimeout, cancellationToken);
            foreach (var column in table.ExportedColumns) {
                if (ValueConverter.IsServerGenerated(column.SqlServerTypeName)) {
                    continue;
                }

                if (!targetColumns.TryGetValue(column.Name, out var target)) {
                    throw new SqlDataPackException($"Target column '{table.Name.FullName}.{column.Name}' does not exist. Create the target column before import or exclude the source column during export.");
                }

                var difference = ColumnTypeComparer.Compare(column, target.SqlServerTypeName, target.MaxLength, target.Precision, target.Scale, target.CollationName);
                if (difference != TypeDifference.None) {
                    var description = ColumnTypeComparer.Describe(column, target.SqlServerTypeName, target.MaxLength, target.Precision, target.Scale, target.CollationName, difference);
                    warnings.Add(description);
                    if (difference == TypeDifference.Lossy) {
                        lossyColumns.Add(description);
                    }
                }
            }

            foreach (var target in targetColumns.Values) {
                if (table.ExportedColumns.Any(c => string.Equals(c.Name, target.Name, StringComparison.OrdinalIgnoreCase)) || target.IsComputed || target.IsIdentity || target.IsGeneratedAlways || ValueConverter.IsServerGenerated(target.SqlServerTypeName)) {
                    // GENERATED ALWAYS period columns on a target-only temporal table are auto-populated by SQL
                    // Server, so a NOT NULL period column the package does not carry must not trip the extra-column
                    // check.
                    continue;
                }

                if (!target.IsNullable && !target.HasDefault) {
                    throw new SqlDataPackException($"Extra target column '{table.Name.FullName}.{target.Name}' is not nullable or defaulted. Make the column nullable, add a default constraint, or remove the table from the import scope.");
                }
            }

            var count = await ScalarLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {SqlDataPackIdentifier.QuoteSqlServerTable(table.Name)}", commandTimeout, cancellationToken);
            if (count != 0) {
                throw new SqlDataPackException($"Target table '{table.Name.FullName}' must be empty before import. Empty the target table and retry.");
            }
        }

        if (failOnLossyTypeMismatch && lossyColumns.Count > 0) {
            throw new SqlDataPackException($"Import would lose data in {lossyColumns.Count} target column(s): {string.Join(" ", lossyColumns)} Widen the target columns, or set ImportOptions.FailOnLossyTypeMismatch to false to import anyway.");
        }

        return warnings;
    }

    private static async Task<List<TableName>> ReadTablesAsync(SqlConnection connection, int? commandTimeout, CancellationToken cancellationToken) {
        const string sql = """
                           SELECT s.name, t.name
                           FROM sys.tables t
                           INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                           WHERE t.is_ms_shipped = 0
                           ORDER BY s.name, t.name;
                           """;

        var result = new List<TableName>();
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            result.Add(new TableName(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<Dictionary<string, TableSizeEstimate>> ReadTableStatsAsync(SqlConnection connection, int? commandTimeout, List<string> warnings, CancellationToken cancellationToken) {
        const string sql = """
                           SELECT
                               s.name,
                               t.name,
                               COALESCE(SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END), 0) AS estimated_rows,
                               COALESCE(SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.used_page_count ELSE 0 END), 0) * 8192 AS estimated_bytes
                           FROM sys.tables t
                           INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                           LEFT JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id
                           WHERE t.is_ms_shipped = 0
                           GROUP BY s.name, t.name;
                           """;

        var result = new Dictionary<string, TableSizeEstimate>(StringComparer.OrdinalIgnoreCase);
        try {
            await using var command = new SqlCommand(sql, connection);
            ApplyTimeout(command, commandTimeout);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                var table = new TableName(reader.GetString(0), reader.GetString(1));
                result[table.FullName] = new TableSizeEstimate(reader.GetInt64(2), reader.GetInt64(3));
            }
        }
        catch (SqlException exception) {
            warnings.Add($"Could not read SQL Server table size metadata from sys.dm_db_partition_stats. Adaptive batching will use caller batch sizes for unknown-size tables. SQL Server reported: {exception.Message}");
        }

        return result;
    }

    // internal, not private: selection resolution decides the scope the WHERE-clause validators check
    // against, and it needs no connection, so the unit tests drive it with a fabricated catalog.
    internal static List<TableName> ResolveTables(List<TableName> allTables, ExportOptions options, List<string> warnings) {
        foreach (var pattern in options.Tables) {
            if (allTables.Any(t => SqlDataPackIdentifier.MatchesPattern(t, pattern))) {
                continue;
            }

            // In AllExcept mode a pattern that matches nothing is harmless — there is simply
            // nothing to exclude — so warn rather than abort the export. In Only mode the pattern
            // is the export scope, so a non-match is a real configuration error.
            if (options.TableSelection == ExportTableSelectionMode.AllExcept) {
                warnings.Add($"Exclude pattern '{pattern}' did not match any user table and was ignored.");
            }
            else {
                throw new SqlDataPackException($"Table pattern '{pattern}' did not match any user table.");
            }
        }

        var selected = options.TableSelection switch {
            ExportTableSelectionMode.AllExcept => allTables.Where(t => !options.Tables.Any(p => SqlDataPackIdentifier.MatchesPattern(t, p))),
            ExportTableSelectionMode.Only when options.Tables.Count == 0 => throw new SqlDataPackException("TableSelection Only requires at least one table pattern."),
            ExportTableSelectionMode.Only => allTables.Where(t => options.Tables.Any(p => SqlDataPackIdentifier.MatchesPattern(t, p))),
            _ => throw new SqlDataPackException($"TableSelection '{options.TableSelection}' is not supported.")
        };

        var result = selected.Distinct().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();

        // Drop dbo.sysdiagrams last so the exclusion is the final word regardless of table selection.
        // SSMS creates it as a user table (is_ms_shipped = 0), so it is not caught by ReadTablesAsync.
        if (options.ExcludeSsmsDiagrams && result.RemoveAll(SqlDataPackIdentifier.IsSsmsDiagramTable) > 0) {
            warnings.Add("Excluded SSMS database diagram table 'dbo.sysdiagrams' from data export; set ExcludeSsmsDiagrams = false to include it.");
        }

        if (result.Count == 0) {
            throw new SqlDataPackException("No tables are selected for export.");
        }

        return result;
    }

    private static void ValidateColumnExclusions(List<TableName> selected, ExportOptions options) {
        foreach (var exclusion in options.ExcludeColumns) {
            var parsed = SqlDataPackIdentifier.ParseColumnPath(exclusion);
            if (!selected.Any(t => string.Equals(t.Schema, parsed.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, parsed.Table, StringComparison.OrdinalIgnoreCase))) {
                throw new SqlDataPackException($"Column exclusion '{exclusion}' references a table outside the selected export scope.");
            }
        }
    }

    // internal, not private: these two run before any connection is touched, so the unit tests can cover
    // them without a container.
    internal static void ValidateGlobalWhereClauses(ExportOptions options) {
        foreach (var clause in options.GlobalWhereClauses) {
            if (clause.ColumnNames.Count == 0) {
                throw new SqlDataPackException("Global WHERE clause must name at least one column.");
            }

            if (clause.ColumnNames.Any(string.IsNullOrWhiteSpace)) {
                throw new SqlDataPackException("Global WHERE clause column name cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(clause.WhereClause)) {
                throw new SqlDataPackException($"Global WHERE clause for {ColumnWord(clause)} {DescribeColumns(clause)} cannot be empty.");
            }
        }
    }

    private static string DescribeColumns(GlobalWhereClause clause) {
        return string.Join(", ", clause.ColumnNames.Select(name => $"'{name}'"));
    }

    private static string ColumnWord(GlobalWhereClause clause) {
        return clause.ColumnNames.Count == 1 ? "column" : "columns";
    }

    internal static void ValidatePerTableWhereClauses(List<TableName> selected, ExportOptions options) {
        foreach (var clause in options.PerTableWhereClauses) {
            if (string.IsNullOrWhiteSpace(clause.TableName)) {
                throw new SqlDataPackException("Per-table WHERE clause table name cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(clause.WhereClause)) {
                throw new SqlDataPackException($"Per-table WHERE clause for table '{clause.TableName}' cannot be empty.");
            }

            if (!selected.Any(t => string.Equals(t.FullName, clause.TableName.Trim(), StringComparison.OrdinalIgnoreCase))) {
                throw new SqlDataPackException($"Per-table WHERE clause table '{clause.TableName}' is not in the selected export scope.");
            }
        }
    }

    private static string[] ResolveWhereClauses(TableName table, List<ColumnMetadata> columns, ExportOptions options) {
        return ResolveGlobalWhereClauses(columns, options.GlobalWhereClauses).Concat(ResolvePerTableWhereClauses(table, options.PerTableWhereClauses)).ToArray();
    }

    private static string[] ResolveGlobalWhereClauses(List<ColumnMetadata> columns, IEnumerable<GlobalWhereClause> globalWhereClauses) {
        // A clause applies only where the table carries every column it names; a table missing any of
        // them is exported unfiltered.
        return globalWhereClauses.Where(clause => HasAllColumns(columns, clause)).Select(clause => clause.WhereClause.Trim()).ToArray();
    }

    private static bool HasAllColumns(IEnumerable<ColumnMetadata> columns, GlobalWhereClause clause) {
        return clause.ColumnNames.All(name => columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] ResolvePerTableWhereClauses(TableName table, IEnumerable<PerTableWhereClause> perTableWhereClauses) {
        return perTableWhereClauses.Where(clause => string.Equals(table.FullName, clause.TableName.Trim(), StringComparison.OrdinalIgnoreCase)).Select(clause => clause.WhereClause.Trim()).ToArray();
    }

    private static void ValidateGlobalWhereClauseMatches(List<TableMetadata> tables, IEnumerable<GlobalWhereClause> globalWhereClauses) {
        foreach (var clause in globalWhereClauses) {
            if (tables.Any(t => HasAllColumns(t.Columns, clause))) {
                continue;
            }

            // A clause that matches nothing is a configuration error, not a silent no-op: the caller
            // asked for filtering that would not happen.
            var message = $"Global WHERE clause {ColumnWord(clause)} {DescribeColumns(clause)} did not match any selected source table.";
            if (clause.ColumnNames.Count > 1) {
                message += " A global WHERE clause applies only to tables that have every column it names.";
            }

            throw new SqlDataPackException(message);
        }
    }

    private static string[] BuildGlobalWhereClauseCoverageWarnings(List<TableMetadata> tables, IEnumerable<GlobalWhereClause> globalWhereClauses) {
        return globalWhereClauses.SelectMany(clause => {
            var unmatched = tables.Where(t => !HasAllColumns(t.Columns, clause)).ToArray();
            // Unmatched everywhere is the hard failure in ValidateGlobalWhereClauseMatches; unmatched
            // nowhere needs no warning either. Only the partial-coverage middle case is silent today.
            return unmatched.Length == 0 || unmatched.Length == tables.Count
                ? []
                : unmatched.Select(t => $"Global WHERE clause '{clause.WhereClause.Trim()}' was not applied to table '{t.Name.FullName}' because it does not have {DescribeColumns(clause)}. That table exported unfiltered.");
        }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<long> CountFilteredRowsAsync(SqlConnection connection, TableName table, string[] whereClauses, int? commandTimeout, CancellationToken cancellationToken) {
        var whereSql = BuildWhereSql(whereClauses);
        return await ScalarLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {SqlDataPackIdentifier.QuoteSqlServerTable(table)}{whereSql}", commandTimeout, cancellationToken);
    }

    internal static string BuildWhereSql(IReadOnlyList<string> whereClauses) {
        if (whereClauses.Count == 0) {
            return string.Empty;
        }

        return " WHERE " + string.Join(" AND ", whereClauses.Select(clause => $"({clause})"));
    }

    private static async Task<List<ColumnMetadata>> ReadColumnsAsync(SqlConnection connection, TableName table, HashSet<string> excludedColumns, int? commandTimeout, CancellationToken cancellationToken) {
        const string sql = """
                           SELECT
                               c.name,
                               c.column_id,
                               ty.name,
                               c.max_length,
                               c.precision,
                               c.scale,
                               c.is_nullable,
                               c.is_identity,
                               c.is_computed,
                               c.collation_name
                           FROM sys.columns c
                           INNER JOIN sys.tables t ON t.object_id = c.object_id
                           INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                           INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                           WHERE s.name = @schema AND t.name = @table
                           ORDER BY c.column_id;
                           """;

        var result = new List<ColumnMetadata>();
        // Scope the reader so it is disposed before any follow-up query runs on the same connection
        // (MARS is not enabled, so an open reader would block the vector-metadata enrichment below).
        await using (var command = new SqlCommand(sql, connection)) {
            ApplyTimeout(command, commandTimeout);
            command.Parameters.AddWithValue("@schema", table.Schema);
            command.Parameters.AddWithValue("@table", table.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                var name = reader.GetString(0);
                result.Add(new ColumnMetadata(table, name, reader.GetInt32(1), reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9), excludedColumns.Contains($"{table.Schema}.{table.Name}.{name}")));
            }
        }

        foreach (var excluded in excludedColumns.Where(c => c.StartsWith(table.FullName + ".", StringComparison.OrdinalIgnoreCase))) {
            var columnName = excluded[(table.FullName.Length + 1)..];
            if (!result.Any(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))) {
                throw new SqlDataPackException($"Excluded column '{excluded}' does not exist.");
            }
        }

        if (result.Any(c => c.Kind == ColumnKind.Vector)) {
            var vectorMetadata = await ReadVectorColumnMetadataAsync(connection, table, commandTimeout, cancellationToken);
            for (var i = 0; i < result.Count; i++) {
                if (vectorMetadata.TryGetValue(result[i].Name, out var meta)) {
                    result[i] = result[i] with { VectorBaseType = meta.BaseType, VectorDimensions = meta.Dimensions };
                }
            }
        }

        return result;
    }

    private sealed record VectorColumnInfo(int? BaseType, int? Dimensions);

    private static async Task<Dictionary<string, VectorColumnInfo>> ReadVectorColumnMetadataAsync(SqlConnection connection, TableName table, int? commandTimeout, CancellationToken cancellationToken) {
        // vector_base_type / vector_dimensions only exist on vector-capable servers (SQL Server 2025+,
        // Azure SQL Database/MI current). This query is only reached when a vector column is present,
        // which implies such a server, so referencing those columns is safe here.
        const string sql = """
                           SELECT c.name, c.vector_base_type, c.vector_dimensions
                           FROM sys.columns c
                           INNER JOIN sys.tables t ON t.object_id = c.object_id
                           INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                           INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                           WHERE s.name = @schema AND t.name = @table AND ty.name = 'vector';
                           """;

        var result = new Dictionary<string, VectorColumnInfo>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var name = reader.GetString(0);
            int? baseType = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
            int? dimensions = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
            result[name] = new VectorColumnInfo(baseType, dimensions);
        }

        return result;
    }

    private static void ValidateSupported(IEnumerable<TableMetadata> tables) {
        foreach (var column in tables.SelectMany(t => t.Columns).Where(c => c.IsExported)) {
            if (ValueConverter.IsUnsupported(column.SqlServerTypeName)) {
                throw new SqlDataPackException($"Unsupported included type '{column.SqlServerTypeName}' on {column.Table.FullName}.{column.Name}. Exclude '{column.Table.FullName}.{column.Name}' explicitly or remove '{column.Table.FullName}' from the export scope.");
            }

            _ = ValueConverter.SqliteTypeFor(column);
        }
    }

    private static async Task<List<ForeignKeyMetadata>> ReadForeignKeysAsync(SqlConnection connection, int? commandTimeout, CancellationToken cancellationToken) {
        const string sql = """
                           SELECT
                               ps.name AS parent_schema,
                               pt.name AS parent_table,
                               rs.name AS referenced_schema,
                               rt.name AS referenced_table
                           FROM sys.foreign_keys fk
                           INNER JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
                           INNER JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
                           INNER JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
                           INNER JOIN sys.schemas rs ON rs.schema_id = rt.schema_id;
                           """;

        var result = new List<ForeignKeyMetadata>();
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var parent = new TableName(reader.GetString(0), reader.GetString(1));
            var referenced = new TableName(reader.GetString(2), reader.GetString(3));
            result.Add(new ForeignKeyMetadata(parent, referenced));
        }

        return result;
    }

    private static string[] BuildServerGeneratedColumnWarnings(IEnumerable<TableMetadata> tables) {
        return tables.SelectMany(t => t.ExportedColumns.Where(c => ValueConverter.IsServerGenerated(c.SqlServerTypeName)).Select(c => $"Table '{t.Name.FullName}' column '{c.Name}' is a {c.SqlServerTypeName}. Bytes are captured for inspection but SQL Server will generate fresh values on import.")).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildVectorPreviewWarnings(IEnumerable<TableMetadata> tables) {
        return tables.SelectMany(t => t.ExportedColumns.Where(c => c.IsFloat16Vector).Select(c => $"Table '{t.Name.FullName}' column '{c.Name}' is a float16 vector, a SQL Server preview feature. The import target must have the matching vector(N, float16) column and the PREVIEW_FEATURES database-scoped configuration enabled.")).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<HashSet<string>> ReadTemporalCurrentTableNamesAsync(SqlConnection connection, int? commandTimeout, CancellationToken cancellationToken) {
        // temporal_type = 2 is the system-versioned current table. Used only to enrich export warnings, so a
        // failure here is non-fatal (older engines without temporal support simply return nothing / are skipped).
        const string sql = """
                           SELECT s.name, t.name
                           FROM sys.tables t
                           INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                           WHERE t.temporal_type = 2;
                           """;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            await using var command = new SqlCommand(sql, connection);
            ApplyTimeout(command, commandTimeout);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                result.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
            }
        }
        catch (SqlException) {
            // Best effort: leave the set empty rather than failing the export over a metadata probe.
        }

        return result;
    }

    private static string[] BuildTemporalTableWarnings(List<TableName> selected, HashSet<string> temporalCurrentTableNames) {
        return selected.Where(t => temporalCurrentTableNames.Contains(t.FullName)).Select(t => $"Table '{t.FullName}' is a system-versioned temporal table. Its period columns and history rows are captured; on import, system versioning is temporarily suspended so the original values can be reloaded (see ImportOptions.SuspendTemporalSystemVersioning).").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildForeignKeyScopeWarnings(List<ForeignKeyMetadata> foreignKeys, HashSet<string> selectedTables) {
        return foreignKeys.Where(fk => selectedTables.Contains(fk.ParentTable.FullName) && !selectedTables.Contains(fk.ReferencedTable.FullName)).Select(fk => $"Selected table '{fk.ParentTable.FullName}' has a foreign key to unselected table '{fk.ReferencedTable.FullName}'. Import into an empty target can fail unless the referenced table is prepared separately.").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ComputeSchemaHash(List<TableMetadata> tables) {
        var builder = new StringBuilder();
        foreach (var table in tables.OrderBy(t => t.Name.FullName, StringComparer.OrdinalIgnoreCase)) {
            builder.AppendLine(table.Name.FullName);
            foreach (var column in table.Columns.OrderBy(c => c.Ordinal)) {
                builder.Append(column.Name).Append('|').Append(column.Ordinal).Append('|').Append(column.SqlServerTypeName).Append('|').Append(column.MaxLength).Append('|').Append(column.Precision).Append('|').Append(column.Scale).Append('|').Append(column.IsNullable).Append('|').Append(column.IsIdentity).Append('|').Append(column.IsComputed).Append('|').Append(column.CollationName);
                if (column.Kind == ColumnKind.Vector) {
                    // Append vector base type / dimensions only for vector columns so existing
                    // (non-vector) package hashes remain byte-identical.
                    builder.Append('|').Append(column.VectorBaseType).Append('|').Append(column.VectorDimensions);
                }

                builder.AppendLine();
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, TableName table, int? commandTimeout, CancellationToken cancellationToken) {
        const string sql = """
                               SELECT COUNT_BIG(*)
                               FROM sys.tables t
                               INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                               WHERE s.name = @schema AND t.name = @table;
                           """;
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private sealed record TargetColumn(string Name, string SqlServerTypeName, bool IsNullable, bool IsIdentity, bool IsComputed, bool HasDefault, bool IsGeneratedAlways, short MaxLength, byte Precision, byte Scale, string? CollationName);

    private sealed record TableSizeEstimate(long EstimatedSourceRowCount, long EstimatedSourceBytes);

    private static async Task<Dictionary<string, TargetColumn>> ReadTargetColumnsAsync(SqlConnection connection, TableName table, int? commandTimeout, CancellationToken cancellationToken) {
        const string sql = """
                           SELECT c.name, ty.name, c.is_nullable, c.is_identity, c.is_computed, CASE WHEN dc.object_id IS NULL THEN 0 ELSE 1 END, c.generated_always_type, c.max_length, c.precision, c.scale, c.collation_name
                           FROM sys.columns c
                           INNER JOIN sys.tables t ON t.object_id = c.object_id
                           INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                           INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                           LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                           WHERE s.name = @schema AND t.name = @table;
                           """;

        var result = new Dictionary<string, TargetColumn>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var column = new TargetColumn(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetInt32(5) == 1, reader.GetByte(6) != 0, reader.GetInt16(7), reader.GetByte(8), reader.GetByte(9), reader.IsDBNull(10) ? null : reader.GetString(10));
            result[column.Name] = column;
        }

        return result;
    }

    private static async Task<long> ScalarLongAsync(SqlConnection connection, string sql, int? commandTimeout, CancellationToken cancellationToken) {
        await using var command = new SqlCommand(sql, connection);
        ApplyTimeout(command, commandTimeout);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void ApplyTimeout(SqlCommand command, int? commandTimeout) {
        if (commandTimeout.HasValue) {
            command.CommandTimeout = commandTimeout.Value;
        }
    }
}
