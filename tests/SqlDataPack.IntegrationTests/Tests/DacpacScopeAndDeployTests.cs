using Microsoft.Data.Sqlite;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

// What a DacpacSchemaScope.SelectedExportTables package contains, and whether it survives a deploy against the
// four target shapes that actually turn up: empty, partially matching, missing a non-dbo schema, and carrying
// unrelated data of its own.
//
// The contents assertions read the TSqlModel the export stored, not the exporter's own bookkeeping -- the
// exporter agreeing with itself about what it selected would prove nothing about the dacpac that ships.
[Collection(nameof(SqlServerCollection))]
public sealed class DacpacScopeAndDeployTests {
    private const string CatalogFixture = "dacpac-catalog.sql";
    private const string TargetWithExtrasFixture = "dacpac-target-with-extras.sql";

    private const string TemporalTable = "dbo.ProductPrices";

    // The auto-named history table is discovered per source database; the pattern is what selects it for export.
    private const string HistoryTablePattern = "dbo.MSSQL_TemporalHistoryFor_*";

    private readonly SqlServerContainerFixture _fixture;

    public DacpacScopeAndDeployTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_SelectedTableDacpac_ContainsExactlySelectedTablesAndDependencies() {
        await using var source = await CreateCatalogSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var historyTable = await ReadHistoryTableNameAsync(source);

        var export = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTablesExport("dbo.Products", "inventory.Categories", TemporalTable, HistoryTablePattern));

        export.Warnings.ShouldContain(w => w.Contains("foreign key to unselected table 'dbo.Suppliers'", StringComparison.Ordinal));

        var manifest = await new SqlDataPackReader().ReadManifestAsync(sqlite.FilePath);
        // This is the value DacpacSchemaManager.DeployAsync and SqlDataPackImporter.PreflightAsync read to refuse
        // AllowObjectDrops; DacpacEditAndDropTests.Deploy_SelectedTableScope_RefusesAllowObjectDrops and
        // DacpacUnitTests cover the guard itself.
        manifest.DacpacSchemaScope.ShouldBe(DacpacSchemaScope.SelectedExportTables);

        using var model = await LoadStoredDacpacModelAsync(sqlite.FilePath);

        HasTable(model, "dbo", "Products").ShouldBeTrue();
        HasTable(model, "inventory", "Categories").ShouldBeTrue();
        HasTable(model, "dbo", TableNameOf(TemporalTable)).ShouldBeTrue();
        HasTable(model, "dbo", TableNameOf(historyTable)).ShouldBeTrue();
        HasTable(model, "dbo", "Suppliers").ShouldBeFalse();
        HasTable(model, "dbo", "SelectedParent").ShouldBeFalse();

        var foreignKeys = ObjectNames(model, ModelSchema.ForeignKeyConstraint);
        foreignKeys.ShouldContain("FK_Products_Categories");
        foreignKeys.ShouldNotContain("FK_Products_Suppliers");

        ObjectNames(model, ModelSchema.Index).ShouldContain("IX_Products_CategoryId");

        var uniqueConstraints = ObjectNames(model, ModelSchema.UniqueConstraint);
        uniqueConstraints.ShouldContain("UQ_Products_Sku");
        uniqueConstraints.ShouldContain("UQ_Categories_Name");

        ObjectNames(model, ModelSchema.CheckConstraint).ShouldContain("CK_Products_Qty");

        // dbo is built into every model, so only the non-dbo schema says anything about the scoped extract.
        ObjectNames(model, ModelSchema.Schema).ShouldContain("inventory");

        // Non-table dependencies: the computed column's function and the primary key default's sequence. Without
        // either one the deployed CREATE TABLE cannot be executed at all.
        ObjectNames(model, ModelSchema.ScalarFunction).ShouldContain("NormalizeSku");
        ObjectNames(model, ModelSchema.Sequence).ShouldContain("ProductNumberSequence");

        var prices = model.GetObject(ModelSchema.Table, new ObjectIdentifier(["dbo", TableNameOf(TemporalTable)]), DacQueryScopes.UserDefined).ShouldNotBeNull();
        var pairedHistory = prices.GetReferenced(Table.TemporalSystemVersioningHistoryTable, DacQueryScopes.UserDefined).ToArray();
        pairedHistory.Length.ShouldBe(1, "The selected-table model must keep the system-versioned table paired with its history table.");
        FullName(pairedHistory[0].Name).ShouldBe(historyTable);
    }

    // Data selection and schema scope are separate knobs. Conflating them is easy and would silently shrink every
    // default-scope package.
    [Fact]
    public async Task Export_DefaultDacpac_IncludesUnselectedTables() {
        await using var source = await CreateCatalogSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["inventory.Categories"],
            SchemaCaptureMode = SchemaCaptureMode.Dacpac,
            CommandTimeout = 120
        });

        var manifest = await new SqlDataPackReader().ReadManifestAsync(sqlite.FilePath);
        manifest.DacpacSchemaScope.ShouldBe(DacpacSchemaScope.Database);
        manifest.Tables.Select(t => $"{t.SourceSchema}.{t.SourceTable}").ShouldBe(["inventory.Categories"]);

        using var model = await LoadStoredDacpacModelAsync(sqlite.FilePath);
        HasTable(model, "inventory", "Categories").ShouldBeTrue();
        HasTable(model, "dbo", "Products").ShouldBeTrue();
        HasTable(model, "dbo", "Suppliers").ShouldBeTrue();
        HasTable(model, "dbo", "SelectedParent").ShouldBeTrue();
    }

    [Fact]
    public async Task Import_SelectedTableDacpac_DeploysToEmptyTargetAndLoadsRows() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await using var sqlite = new SqliteTempFileHarness();
        var historyTable = await ReadHistoryTableNameAsync(source);

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTablesExport("dbo.Products", "inventory.Categories", TemporalTable, HistoryTablePattern));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        result.TableCount.ShouldBe(4);
        result.RowCount.ShouldBe(9);

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Products")).ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM inventory.Categories")).ShouldBe(2);

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Products_CategoryId'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'UQ_Products_Sku' AND type = 'UQ'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'UQ_Categories_Name' AND type = 'UQ'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Products_Qty'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Products') AND name = 'NormalizedSku' AND is_computed = 1")).ShouldBe(1);

        // Selecting the computed column runs dbo.NormalizeSku, so a function that deployed but is broken fails
        // here rather than passing as "the object exists".
        (await target.ScalarStringAsync("SELECT NormalizedSku FROM dbo.Products WHERE Sku = N'sku-hammer'")).ShouldBe("SKU-HAMMER");
        (await target.ScalarStringAsync("SELECT dbo.NormalizeSku(N'  mixed  ')")).ShouldBe("MIXED");

        // The default is only useful if the sequence it calls came along too. SQL Server burns a sequence value
        // per bulk-copied row even when the row supplies the column, so the exact number depends on the load --
        // what has to hold is that the value came from this sequence and not from nowhere.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.sequences WHERE name = 'ProductNumberSequence'")).ShouldBe(1);
        await target.ExecuteSqlAsync("INSERT INTO dbo.Products (CategoryId, SupplierId, Sku, Qty, UnitPrice) VALUES (1, 1, N'sku-new', 1, 1.00);");
        (await target.ScalarIntAsync("SELECT ProductId FROM dbo.Products WHERE Sku = N'sku-new'")).ShouldBeGreaterThanOrEqualTo(5000);

        (await TemporalAssertions.ReadHistoryTableNameAsync(target, TemporalTable)).ShouldBe(historyTable);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {historyTable}")).ShouldBe(2);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {TemporalTable} FOR SYSTEM_TIME ALL")).ShouldBe(4);

        var periods = await TemporalAssertions.ReadPeriodColumnNamesAsync(source, TemporalTable);
        (await TemporalAssertions.DumpSystemVersionedAsync(target, TemporalTable, periods.Start, periods.End))
            .ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, TemporalTable, periods.Start, periods.End));
    }

    // The common real-world target: the table is already there but has drifted. DacFx takes the alter-in-place
    // path here, which is materially different from CREATE TABLE.
    [Fact]
    public async Task Import_SelectedTableDacpac_DeploysOverPartiallyMatchingTarget() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CatalogFixture);
        await target.ExecuteSqlAsync("""
                                     DROP INDEX IX_Products_CategoryId ON dbo.Products;
                                     ALTER TABLE dbo.Products DROP CONSTRAINT CK_Products_Qty;
                                     -- The package cannot carry this FK (dbo.Suppliers is out of scope) and the
                                     -- target's Suppliers is empty, so it has to go or no product row can load.
                                     ALTER TABLE dbo.Products DROP CONSTRAINT FK_Products_Suppliers;
                                     """);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTablesExport("dbo.Products", "inventory.Categories"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        result.RowCount.ShouldBe(5);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Products_CategoryId'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Products_Qty'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Products")).ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM inventory.Categories")).ShouldBe(2);
    }

    // Forgetting to script the schema object breaks every multi-schema export outright, and nothing else covers
    // a non-dbo schema that does not exist on the target yet.
    [Fact]
    public async Task Import_SelectedTableDacpac_DeploysNonDboSchemaThatDoesNotExist() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTablesExport("inventory.Categories"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        result.TableCount.ShouldBe(1);
        result.RowCount.ShouldBe(2);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.schemas WHERE name = 'inventory'")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM inventory.Categories")).ShouldBe(2);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'UQ_Categories_Name' AND type = 'UQ'")).ShouldBe(1);
    }

    // The scariest failure mode: a scoped deploy quietly dropping data in a table the export never mentioned.
    [Fact]
    public async Task Import_SelectedTableDacpac_LeavesUnrelatedTargetObjectsUntouched() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TargetWithExtrasFixture));
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTablesExport("dbo.Products"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.LegacyStagingImport")).ShouldBe(1);
        (await target.ReadStringsAsync("SELECT RawPayload FROM dbo.LegacyStagingImport ORDER BY LegacyStagingImportId")).ShouldBe(["pre-existing, unrelated to the package"]);

        // The documented promise for a cross-scope FK: the table deploys and loads, the FK is simply absent.
        result.RowCount.ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Products")).ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_Products_Suppliers'")).ShouldBe(0);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.tables WHERE name = 'Suppliers'")).ShouldBe(0);
    }

    // The fact-table shape: columnstore metadata plus enough rows that bulk copy runs several batches at the
    // default batch size. A regression here would otherwise surface first on a customer's largest table.
    [Fact]
    public async Task Import_ColumnstoreFact_RoundTripsAndRecreatesColumnstore() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await using var sqlite = new SqliteTempFileHarness();

        var expectedRows = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.SalesFact");
        expectedRows.ShouldBeGreaterThan(ExportOptions.Default.BatchSize, "dacpac-catalog.sql must seed dbo.SalesFact past one batch or this test proves nothing about batching.");

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTablesExport("dbo.SalesFact"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        result.RowCount.ShouldBe(expectedRows);
        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, "dbo.SalesFact");

        // Index type 5 is a clustered columnstore.
        (await target.ScalarIntAsync("""
                                     SELECT COUNT(*)
                                     FROM sys.indexes
                                     WHERE object_id = OBJECT_ID('dbo.SalesFact') AND name = 'CCI_SalesFact' AND type = 5
                                     """)).ShouldBe(1);
    }

    private async Task<SqlServerFixtureDatabase> CreateCatalogSourceAsync() {
        var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CatalogFixture));
        return source;
    }

    private static async Task<string> ReadHistoryTableNameAsync(SqlServerFixtureDatabase source) {
        return await TemporalAssertions.ReadHistoryTableNameAsync(source, TemporalTable) ?? throw new InvalidOperationException($"'{TemporalTable}' is not system-versioned in {CatalogFixture}.");
    }

    private static ExportOptions SelectedTablesExport(params string[] tables) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables,
            SchemaCaptureMode = SchemaCaptureMode.Dacpac,
            CommandTimeout = 120,
            DacpacCaptureOptions = new DacpacCaptureOptions { SchemaScope = DacpacSchemaScope.SelectedExportTables }
        };
    }

    private static async Task<TSqlModel> LoadStoredDacpacModelAsync(string sqliteFilePath) {
        var dacpacPath = Path.Combine(Path.GetTempPath(), $"zsdp-test-{Guid.NewGuid():N}.dacpac");
        try {
            await using (var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder {
                             DataSource = sqliteFilePath,
                             Mode = SqliteOpenMode.ReadOnly
                         }.ConnectionString)) {
                await sqlite.OpenAsync();
                await using var command = sqlite.CreateCommand();
                command.CommandText = "SELECT payload FROM zsdp_schema_packages WHERE id = 1";
                var payload = (byte[])(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Export stored no dacpac payload."));
                await File.WriteAllBytesAsync(dacpacPath, payload);
            }

            return new TSqlModel(dacpacPath, DacSchemaModelStorageType.Memory);
        }
        finally {
            if (File.Exists(dacpacPath)) {
                File.Delete(dacpacPath);
            }
        }
    }

    private static bool HasTable(TSqlModel model, string schema, string table) {
        return model.GetObject(ModelSchema.Table, new ObjectIdentifier([schema, table]), DacQueryScopes.UserDefined) is not null;
    }

    /// <summary>
    /// The bare names of every model object of a type -- indexes and constraints are named
    /// <c>[schema].[table].[name]</c>, so the last part is the object's own name. Exact matches only: a
    /// substring match would let <c>UQ_Categories_Name_Old</c> satisfy an assertion about
    /// <c>UQ_Categories_Name</c>.
    /// </summary>
    private static IReadOnlyCollection<string> ObjectNames(TSqlModel model, ModelTypeClass objectType) {
        return model.GetObjects(DacQueryScopes.UserDefined, objectType).Select(o => Unquote(o.Name.Parts[^1])).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FullName(ObjectIdentifier identifier) {
        return string.Join(".", identifier.Parts.Select(Unquote));
    }

    private static string TableNameOf(string schemaQualifiedName) {
        return schemaQualifiedName.Split('.', 2)[1];
    }

    private static string Unquote(string part) {
        return part.Trim('[', ']');
    }
}
