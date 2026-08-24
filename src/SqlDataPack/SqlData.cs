using SqlDataPack.Models;

namespace SqlDataPack;

/// <summary>
/// Provides simple entry points for exporting and importing a SqlDataPack.
/// </summary>
public static class SqlData {
    /// <summary>
    /// Exports selected SQL Server user tables into a SQLite package.
    /// </summary>
    /// <param name="sqlServerConnectionString">The SQL Server source connection string.</param>
    /// <param name="sqliteFilePath">The destination SQLite package path.</param>
    /// <param name="options">The export options. When omitted, all user tables are exported.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A summary of exported tables, rows, and warnings.</returns>
    public static Task<SqlDataPackResult> ExportAsync(string sqlServerConnectionString, string sqliteFilePath, ExportOptions? options = null, CancellationToken cancellationToken = default) {
        return new SqlDataPackExporter().ExportAsync(sqlServerConnectionString, sqliteFilePath, options, cancellationToken);
    }

    /// <summary>
    /// Imports a SQLite package into empty compatible SQL Server target tables.
    /// </summary>
    /// <param name="sqliteFilePath">The SQLite package path.</param>
    /// <param name="sqlServerConnectionString">The SQL Server target connection string.</param>
    /// <param name="options">The import options.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A summary of imported tables and rows.</returns>
    public static Task<SqlDataPackResult> ImportAsync(string sqliteFilePath, string sqlServerConnectionString, ImportOptions? options = null, CancellationToken cancellationToken = default) {
        return new SqlDataPackImporter().ImportAsync(sqliteFilePath, sqlServerConnectionString, options, cancellationToken);
    }
}
