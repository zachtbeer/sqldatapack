using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

/// <summary>
/// Streams rows from an open source reader into a package data table. This is the hottest loop in
/// the export path, which is why it lives here rather than inline in <c>SqlDataPackExporter</c>:
/// keeping it behind a reader-shaped seam lets the benchmark project drive the real production code
/// with a synthetic reader instead of measuring a copy of it.
/// </summary>
internal static class SqlitePackageWriter {
    public static async Task<long> WriteTableAsync(SqliteConnection sqlite, DbDataReader reader, TableMetadata table, int batchSize, IProgress<SqlDataPackProgress>? progress, CancellationToken cancellationToken) {
        var columns = table.ExportedColumns;
        // Resolved once per column rather than once per cell; see ValueConverter.ToSqliteValue.
        var kinds = new ColumnKind[columns.Count];
        for (var i = 0; i < columns.Count; i++) {
            kinds[i] = columns[i].Kind;
        }

        var insertColumnNames = string.Join(", ", columns.Select(c => SqlDataPackIdentifier.QuoteSqliteName(c.Name)));
        var parameterNames = columns.Select((_, index) => "$p" + index).ToArray();
        var insertSql = $"INSERT INTO {SqlDataPackIdentifier.QuoteSqliteName(table.SqliteTableName)} ({insertColumnNames}) VALUES ({string.Join(", ", parameterNames)})";

        long rowCount = 0;
        SqliteTransaction? transaction = null;
        SqliteCommand? insert = null;
        try {
            transaction = (SqliteTransaction)await sqlite.BeginTransactionAsync(cancellationToken);
            insert = sqlite.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = insertSql;
            for (var i = 0; i < parameterNames.Length; i++) {
                insert.Parameters.Add(parameterNames[i], SqliteTypeFor(columns[i]));
            }

            // The SQLite side runs synchronously on purpose. Microsoft.Data.Sqlite's *Async methods are
            // synchronous wrappers over a local file handle -- there is no I/O to overlap -- so awaiting
            // once per row bought nothing and cost a state machine per row. Cancellation is honoured at
            // the batch boundary and on every source read, which is where the work actually blocks.
            while (await reader.ReadAsync(cancellationToken)) {
                for (var i = 0; i < columns.Count; i++) {
                    var value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    ValueConverter.BindSqliteParameter(insert.Parameters[i], ValueConverter.ToSqliteValue(value, columns[i], kinds[i]));
                }

                insert.ExecuteNonQuery();
                rowCount++;

                if (rowCount % batchSize == 0) {
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                    progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, table.Name.FullName, rowCount, table.EstimatedSourceRowCount));
                    await transaction.DisposeAsync();
                    transaction = (SqliteTransaction)await sqlite.BeginTransactionAsync(cancellationToken);
                    insert.Transaction = transaction;
                }
            }

            transaction.Commit();
            if (rowCount == 0 || rowCount % batchSize != 0) {
                progress?.Report(new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, table.Name.FullName, rowCount, table.EstimatedSourceRowCount));
            }

            return rowCount;
        }
        finally {
            if (insert is not null) {
                await insert.DisposeAsync();
            }

            if (transaction is not null) {
                await transaction.DisposeAsync();
            }
        }
    }

    private static SqliteType SqliteTypeFor(ColumnMetadata column) {
        return ValueConverter.SqliteTypeFor(column) switch {
            "INTEGER" => SqliteType.Integer,
            "REAL" => SqliteType.Real,
            "BLOB" => SqliteType.Blob,
            _ => SqliteType.Text
        };
    }
}
