using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// The comparison that decides whether a package was edited after export: the count export recorded
/// in zsdp_table_stats against what the data table holds now. It only ever reads the package, so it
/// runs without a SQL Server. Packages here are built by the production initializer and then edited
/// the way the documented workflow edits them.
/// </summary>
public sealed class RowCountDriftTests {
    private static readonly TableName Customers = new("dbo", "Customers");

    [Fact]
    public async Task EvaluateRowCountDrift_RowDeletedFromDataTable_ReportsBothCounts() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection, rowCount: 3);

        await ExecuteAsync(connection, $"DELETE FROM {QuotedDataTable} WHERE Id = 1");

        var findings = await EvaluateAsync(connection);

        var finding = findings.ShouldHaveSingleItem();
        finding.TableName.ShouldBe("dbo.Customers");
        finding.Expected.ShouldBe(3);
        finding.Actual.ShouldBe(2);
    }

    [Fact]
    public async Task EvaluateRowCountDrift_RowInsertedIntoDataTable_ReportsBothCounts() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection, rowCount: 3);

        await ExecuteAsync(connection, $"INSERT INTO {QuotedDataTable} (Id) VALUES (99)");

        var finding = (await EvaluateAsync(connection)).ShouldHaveSingleItem();
        finding.Expected.ShouldBe(3);
        finding.Actual.ShouldBe(4);
    }

    [Fact]
    public async Task EvaluateRowCountDrift_UntouchedPackage_ReportsNothing() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection, rowCount: 3);

        (await EvaluateAsync(connection)).ShouldBeEmpty();
    }

    /// <summary>
    /// An empty table is the case a naive implementation gets wrong: zero rows recorded and zero rows
    /// present is agreement, not drift.
    /// </summary>
    [Fact]
    public async Task EvaluateRowCountDrift_EmptyTableExportedEmpty_ReportsNothing() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection, rowCount: 0);

        (await EvaluateAsync(connection)).ShouldBeEmpty();
    }

    private static string QuotedDataTable => SqlDataPackIdentifier.QuoteSqliteName(SqlDataPackIdentifier.ToSqliteDataTableName(Customers));

    private static async Task<IReadOnlyList<SqlDataPackImporter.RowCountDriftFinding>> EvaluateAsync(SqliteConnection connection) {
        var tables = await SqlitePackage.ReadTablesAsync(connection, CancellationToken.None);
        return await SqlDataPackImporter.EvaluateRowCountDriftAsync(connection, tables, CancellationToken.None);
    }

    /// <summary>
    /// Production initializer and production stats writer, one table with one column, seeded with the
    /// number of rows the stats row claims. Edit exactly one thing on top of this per test.
    /// </summary>
    private static async Task CreateValidPackageAsync(SqliteConnection connection, int rowCount) {
        var table = new TableMetadata(
            Customers,
            SqlDataPackIdentifier.ToSqliteDataTableName(Customers),
            [new ColumnMetadata(Customers, "Id", 1, "int", 4, 10, 0, IsNullable: false, IsIdentity: true, IsComputed: false, CollationName: null, IsExcluded: false)]);

        var plan = new ExportPlan([table], [], [Customers], [], [], [], new string('a', 64));

        await SqlitePackage.InitializeAsync(connection, plan, CancellationToken.None);
        for (var id = 1; id <= rowCount; id++) {
            await ExecuteAsync(connection, $"INSERT INTO {QuotedDataTable} (Id) VALUES ({id})");
        }

        await SqlitePackage.RecordTableStatsAsync(connection, table, rowCount, exportBatchSize: 1000, CancellationToken.None);
    }

    private static async Task<SqliteConnection> OpenAsync(TempSqliteFile file) {
        var connection = new SqliteConnection(file.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A real file rather than :memory: so the package goes through the same page_size and journal
    /// settings the exporter uses. Pools are cleared on dispose or Windows keeps the handle open.
    /// </summary>
    private sealed class TempSqliteFile : IDisposable {
        public TempSqliteFile() {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sqldatapack-drift-{Guid.NewGuid():N}.sqlite");
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path, ForeignKeys = false }.ToString();
        }

        public string Path { get; }

        public string ConnectionString { get; }

        public void Dispose() {
            SqliteConnection.ClearAllPools();
            try {
                File.Delete(Path);
            }
            catch (IOException) {
                /* best effort */
            }
        }
    }
}
