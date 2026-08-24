using Microsoft.Data.Sqlite;
using SqlDataPack.Internal;

namespace SqlDataPack.Models;

/// <summary>
/// Reads metadata from a SqlDataPack SQLite package without importing rows.
/// </summary>
public sealed class SqlDataPackReader {
    /// <summary>
    /// Reads the package manifest from a SQLite package.
    /// </summary>
    /// <param name="sqliteFilePath">The SQLite package path.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The package manifest.</returns>
    public async Task<SqlDataPackManifest> ReadManifestAsync(string sqliteFilePath, CancellationToken cancellationToken = default) {
        var sqliteBuilder = new SqliteConnectionStringBuilder { DataSource = sqliteFilePath, Mode = SqliteOpenMode.ReadOnly };
        await using var sqlite = new SqliteConnection(sqliteBuilder.ConnectionString);
        try {
            await sqlite.OpenAsync(cancellationToken);

            await SqlitePackage.ValidateForImportAsync(sqlite, cancellationToken);
            return await SqlitePackage.ReadManifestAsync(sqlite, cancellationToken);
        }
        catch (Exception ex) when (ex is not SqlDataPackException and not OperationCanceledException) {
            throw new SqlDataPackException("SQLite package could not be opened or is not a valid SqlDataPack package.", ex);
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
}
