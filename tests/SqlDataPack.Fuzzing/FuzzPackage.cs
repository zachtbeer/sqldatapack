using Microsoft.Data.Sqlite;
using SqlDataPack.Internal;

namespace SqlDataPack.Fuzzing;

/// <summary>
/// Builds a structurally valid, minimal SqlDataPack package on an open SQLite connection, so fuzz tests can
/// tamper with individual cells and observe how the reader handles a corrupt-but-well-formed package. The
/// schema comes from the production initializer rather than a copy: a copied schema (and a copied format
/// version) drifts, and then validation rejects the package before the corrupted cell is ever read, which
/// makes the property vacuous.
/// </summary>
internal static class FuzzPackage {
    public const int ValidPackageFormatVersion = SqlDataPackVersion.PackageFormatVersion;

    private static readonly TableName Customers = new("dbo", "Customers");

    public static async Task CreateValidMinimalPackageAsync(SqliteConnection connection) {
        var table = new TableMetadata(
            Customers,
            SqlDataPackIdentifier.ToSqliteDataTableName(Customers),
            [new ColumnMetadata(Customers, "Id", 1, "int", 4, 10, 0, IsNullable: false, IsIdentity: true, IsComputed: false, CollationName: null, IsExcluded: false)]);

        var plan = new ExportPlan(
            [table],
            [],
            [Customers],
            [],
            [],
            [],
            new string('a', 64));

        await SqlitePackage.InitializeAsync(connection, plan, CancellationToken.None);
        await SqlitePackage.RecordTableStatsAsync(connection, table, rowCount: 0, exportBatchSize: 1000, CancellationToken.None);
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
