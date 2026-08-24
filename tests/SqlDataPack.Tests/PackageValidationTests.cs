using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Everything the importer checks about a package file before it contacts a target, plus the
/// schema-package round trip. Every package here is built by the production initializer and then
/// broken in exactly one place, so the tests cannot drift from the DDL the exporter actually writes.
/// </summary>
public sealed class PackageValidationTests {
    private static readonly TableName Customers = new("dbo", "Customers");

    [Fact]
    public async Task ValidateForImport_MissingMetadataTable_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "DROP TABLE zsdp_tables;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("required metadata table 'zsdp_tables' is missing");
    }

    [Fact]
    public async Task ValidateForImport_NoTableMetadata_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "DELETE FROM zsdp_tables;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("no table metadata exists in 'zsdp_tables'");
    }

    [Fact]
    public async Task ValidateForImport_EmptyImportPlan_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "DELETE FROM zsdp_import_plan;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("import plan metadata is empty");
    }

    [Fact]
    public async Task ValidateForImport_ImportPlanReferencesMissingTableMetadata_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "INSERT INTO zsdp_import_plan(sequence, source_schema, source_table) VALUES (1, 'dbo', 'Ghost');");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("import plan references table 'dbo.Ghost'");
    }

    [Fact]
    public async Task ValidateForImport_MissingRowCountMetadata_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "DELETE FROM zsdp_table_stats;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("row-count metadata is missing for 'dbo.Customers'");
    }

    [Fact]
    public async Task ValidateForImport_MissingDataTable_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "DROP TABLE dbo__customers;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("data table 'dbo__customers' for 'dbo.Customers' is missing");
    }

    // The over-strict control: without it the validator can be tightened until valid packages fail
    // and every negative test above still passes.
    [Fact]
    public async Task ValidateForImport_ValidMinimalPackage_Succeeds() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);

        await SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None);
    }

    /// <summary>
    /// A hand-edited package recording export_batch_size = 0 must be rejected up front, before the
    /// import row loop's <c>rows % batchSize</c> can raise DivideByZeroException. The validator only
    /// checks tables reachable from the import plan, which is also the only place the modulo runs.
    /// </summary>
    [Fact]
    public async Task ValidateForImport_ZeroBatchSize_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "UPDATE zsdp_table_stats SET export_batch_size = 0;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("batch-size metadata is invalid for 'dbo.Customers'");
    }

    [Fact]
    public async Task ValidateForImport_FormatVersionAboveCurrent_Throws() {
        var version = SqlDataPackVersion.PackageFormatVersion + 1;
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, $"UPDATE zsdp_export_runs SET package_format_version = {version};");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain($"format version '{version}' was produced by a newer version of SqlDataPack");
        exception.Message.ShouldContain("Upgrade SqlDataPack");
    }

    [Fact]
    public async Task ValidateForImport_FormatVersionBelowMinimum_Throws() {
        var version = SqlDataPackVersion.MinimumSupportedPackageFormatVersion - 1;
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, $"UPDATE zsdp_export_runs SET package_format_version = {version};");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain($"format version '{version}' is no longer supported");
        exception.Message.ShouldContain($"reads package format version '{SqlDataPackVersion.MinimumSupportedPackageFormatVersion}' and later");
        exception.Message.ShouldContain("Recreate the package with SqlDataPack export");
    }

    // A package written before the format version existed at all.
    [Fact]
    public async Task ValidateForImport_FormatVersionColumnAbsent_Throws() {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);
        await ExecuteAsync(connection, "ALTER TABLE zsdp_export_runs DROP COLUMN package_format_version;");

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None));

        exception.Message.ShouldContain("package format version metadata is missing");
    }

    // Runs once while the minimum equals the current version; becomes real the moment the range widens.
    [Fact]
    public async Task ValidateForImport_EverySupportedFormatVersion_Succeeds() {
        for (var version = SqlDataPackVersion.MinimumSupportedPackageFormatVersion; version <= SqlDataPackVersion.PackageFormatVersion; version++) {
            using var file = new TempSqliteFile();
            await using var connection = await OpenAsync(file);
            await CreateValidPackageAsync(connection);
            await ExecuteAsync(connection, $"UPDATE zsdp_export_runs SET package_format_version = {version};");

            await SqlitePackage.ValidateForImportAsync(connection, CancellationToken.None);
        }
    }

    /// <summary>
    /// Every stored schema-package field has to survive the write/read round trip. SourceEngineEdition
    /// drives DacpacSchemaManager's Azure containment rewrite, so its null case is load-bearing.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(null)]
    public async Task StoreSchemaPackage_RoundTripsEveryField(int? sourceEngineEdition) {
        using var file = new TempSqliteFile();
        await using var connection = await OpenAsync(file);
        await CreateValidPackageAsync(connection);

        byte[] payload = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0x00, 0x2A];
        var written = new SchemaPackage(
            "dacpac",
            "core-commerce.dacpac",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            new DateTimeOffset(2026, 2, 3, 4, 5, 6, 789, TimeSpan.Zero),
            "CoreCommerce",
            "170.0.94",
            DacpacSchemaScope.SelectedExportTables,
            payload,
            sourceEngineEdition);

        await SqlitePackage.StoreSchemaPackageAsync(connection, written, CancellationToken.None);
        var read = await SqlitePackage.ReadSchemaPackageAsync(connection, CancellationToken.None);

        read.ShouldNotBeNull();
        read.PackageType.ShouldBe(written.PackageType);
        read.PackageName.ShouldBe(written.PackageName);
        read.PackageSha256.ShouldBe(written.PackageSha256);
        read.CreatedAtUtc.ShouldBe(written.CreatedAtUtc);
        read.SourceDatabaseName.ShouldBe(written.SourceDatabaseName);
        read.DacFxVersion.ShouldBe(written.DacFxVersion);
        read.SchemaScope.ShouldBe(written.SchemaScope);
        read.SourceEngineEdition.ShouldBe(sourceEngineEdition);
        read.Payload.ShouldBe(written.Payload);
    }

    /// <summary>
    /// A minimal but genuinely valid package: production initializer, production stats writer, one
    /// table with one column. Break exactly one thing per test on top of this.
    /// </summary>
    private static async Task CreateValidPackageAsync(SqliteConnection connection) {
        var table = new TableMetadata(
            Customers,
            SqlDataPackIdentifier.ToSqliteDataTableName(Customers),
            [new ColumnMetadata(Customers, "Id", 1, "int", 4, 10, 0, IsNullable: false, IsIdentity: true, IsComputed: false, CollationName: null, IsExcluded: false)]);

        var plan = new ExportPlan([table], [], [Customers], [], [], [], new string('a', 64));

        await SqlitePackage.InitializeAsync(connection, plan, CancellationToken.None);
        await SqlitePackage.RecordTableStatsAsync(connection, table, rowCount: 0, exportBatchSize: 1000, CancellationToken.None);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sqldatapack-{Guid.NewGuid():N}.sqlite");
            // FK enforcement off: a truncated or hand-edited package is exactly a file whose metadata
            // FKs no longer hold, and the tests have to be able to produce that state. e_sqlite3 is
            // built with foreign keys on, so dropping or emptying zsdp_tables fails without this.
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
