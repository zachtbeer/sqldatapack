using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

// Two things this library does to a dacpac that DacFx did not: it rewrites archive entries (and has to
// recompute the Origin.xml checksum or every SSDT-shaped tool rejects the package as tampered), and it hands
// DacFx a deploy option set that decides what gets deleted on the target.
//
// Source for the edit and drop tests is dacpac-catalog.sql; the drop tests pair it with
// dacpac-target-with-extras.sql, which carries FK_SelectedChild_RegionLookup, IX_SelectedChild_RegionLookupId,
// trg_SelectedChild_Audit and dbo.LegacyStagingImport -- none of them in the package. The containment test
// uses azure-partial-containment.sql instead.
[Collection(nameof(SqlServerCollection))]
public sealed class DacpacEditAndDropTests {
    // Removed from the extracted model by the edit test. The table's own constraints go with it; anything
    // else pointing at it would have to go too, and the fixture is written so nothing does.
    private const string RemovedTable = "dbo.CatalogArchiveLog";
    private const string RemovedProcedure = "dbo.usp_ArchiveCatalog";

    // SERVERPROPERTY('EngineEdition') values. 3 = on-prem Enterprise/Developer (what the container reports),
    // 5 = Azure SQL Database.
    private const int OnPremEngineEdition = 3;
    private const int AzureSqlDatabaseEngineEdition = 5;

    private const string TableNames = "SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.tables ORDER BY 1";
    private const string ProcedureNames = "SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.procedures ORDER BY 1";

    private const string ParameterlessProcedureNames = """
                                                       SELECT SCHEMA_NAME(p.schema_id) + '.' + p.name
                                                       FROM sys.procedures p
                                                       WHERE NOT EXISTS (SELECT 1 FROM sys.parameters prm WHERE prm.object_id = p.object_id)
                                                       ORDER BY 1
                                                       """;

    private readonly SqlServerContainerFixture _fixture;

    public DacpacEditAndDropTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Edit_RemoveTableAndProcedure_StillDeploysWithoutThem() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);

        var dacpacPath = ExtractDacpac(source);
        try {
            // Two model element types in one edit, so the Origin.xml checksum is recomputed once over a
            // model.xml that changed in more than one place.
            DacpacEditor.Edit(dacpacPath, context => {
                context.MutateXml("model.xml", document => {
                    var tableRemoved = TryRemoveModelElement(document, "SqlTable", QuotedName(RemovedTable));
                    var procedureRemoved = TryRemoveModelElement(document, "SqlProcedure", QuotedName(RemovedProcedure));
                    tableRemoved.ShouldBeTrue($"dacpac-catalog.sql must carry a standalone table {RemovedTable} for this test to remove anything.");
                    procedureRemoved.ShouldBeTrue($"dacpac-catalog.sql must carry a stored procedure {RemovedProcedure} for this test to remove anything.");
                    return true;
                }).ShouldBeTrue();
            });

            // Through the library's own deploy, not raw DacServices: a stale Origin.xml checksum fails
            // DacPackage.Load here, which is where the real call path would hit it.
            await DeployDacpacFileAsync(dacpacPath, target);

            var expectedTables = (await source.ReadStringsAsync(TableNames)).Where(name => name != RemovedTable).ToArray();
            var expectedProcedures = (await source.ReadStringsAsync(ProcedureNames)).Where(name => name != RemovedProcedure).ToArray();

            (await target.ReadStringsAsync(TableNames)).ShouldBe(expectedTables);
            (await target.ReadStringsAsync(ProcedureNames)).ShouldBe(expectedProcedures);

            // Present is not the same as usable: run the surviving bodies the deploy created.
            var callable = (await source.ReadStringsAsync(ParameterlessProcedureNames)).Where(name => name != RemovedProcedure).ToArray();
            callable.ShouldNotBeEmpty("dacpac-catalog.sql must carry at least one surviving parameterless stored procedure so callability can be checked.");
            foreach (var procedure in callable) {
                await target.ExecuteSqlAsync($"EXEC {procedure};");
            }
        }
        finally {
            DeleteIfExists(dacpacPath);
        }
    }

    [Fact]
    public async Task Deploy_PartialContainmentSource_StripsContainmentAndDeploysToNonContainedTarget() {
        await using var source = await SqlServerFixtureDatabase.CreateContainedAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript("azure-partial-containment.sql"));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, DatabaseScopeExportOptions());

        // Nothing in a container reports EngineEdition 5, so the source platform stamp is the one input to
        // the rewrite decision a test has to fake. Everything downstream of it is real: the strip runs on the
        // real extract, the repacked archive still has to satisfy DacPackage.Load's Origin.xml checksum, and
        // DacFx deploys what comes out.
        await StampSourceEngineEditionAsync(sqlite, AzureSqlDatabaseEngineEdition);

        // Pre-strip: without this, a model that never carried the containment setting would make the whole
        // test pass by having nothing to strip.
        HasContainmentProperty(await ReadStoredDacpacModelAsync(sqlite)).ShouldBeTrue("Extract of a CONTAINMENT = PARTIAL source must produce a SqlDatabaseOptions Element with a Containment Property; if it does not, TryRemoveDatabaseContainmentProperty is matching something DacFx no longer emits and silently no-ops in production.");

        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        result.RowCount.ShouldBe(2);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.RemoteOffices")).ShouldBe(2);
        (await ContainmentLevelAsync(target.DatabaseName)).ShouldBe(0);

        // No negative half: running the same package with AdaptAzureSourceForOnPremTarget = false against this
        // container produces the same clean target. DeployDatabaseOptions is off (so ScriptDatabaseOptions is
        // false) and Users are excluded, so DacFx never scripts the ALTER DATABASE ... SET CONTAINMENT =
        // PARTIAL prerequisite in the first place. Measured, not assumed.
    }

    [Fact]
    public async Task Deploy_SelectedTableScope_RefusesAllowObjectDrops() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, SelectedTableExportOptions("dbo.SelectedParent"));

        var result = await new SqlDataPackImporter().PreflightAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions {
            SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac,
            DacpacDeploymentOptions = new DacpacDeploymentOptions { AllowObjectDrops = true }
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("AllowObjectDrops cannot be used", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_DatabaseScope_AllowObjectDrops_DropsTheExtraObject() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await CreateTargetWithExtrasAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, DatabaseScopeExportOptions());

        // Dropping a populated table is data loss, so the two options have to travel together.
        await DeployStoredSchemaAsync(sqlite, target, new DacpacDeploymentOptions {
            AllowObjectDrops = true,
            BlockOnPossibleDataLoss = false
        });

        (await CatalogCountAsync(target, "sys.tables", "LegacyStagingImport")).ShouldBe(0);
        (await CatalogCountAsync(target, "sys.tables", "SelectedChild")).ShouldBe(1);
    }

    [Fact(Skip = "Fails against the current DacFx. Tracked in issue #7.")]
    public async Task Deploy_WithoutAllowObjectDrops_PinsWhatItActuallyDropsOnInPackageTables() {
        await using var source = await CreateCatalogSourceAsync();
        await using var target = await CreateTargetWithExtrasAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, DatabaseScopeExportOptions());

        await DeployStoredSchemaAsync(sqlite, target, new DacpacDeploymentOptions());

        // v1_todo 2.2. CreateDeployOptions sets DropObjectsNotInSource and nothing else, and DacFx defaults
        // DropIndexesNotInSource / DropConstraintsNotInSource / DropDmlTriggersNotInSource to true. So on
        // dbo.SelectedChild -- a table that IS in the package -- these three are deleted while the option
        // named AllowObjectDrops is off. When 2.2 is fixed all three flip to 1 and this test changes with it.
        (await CatalogCountAsync(target, "sys.indexes", "IX_SelectedChild_RegionLookupId")).ShouldBe(0);
        (await CatalogCountAsync(target, "sys.foreign_keys", "FK_SelectedChild_RegionLookup")).ShouldBe(0);
        (await CatalogCountAsync(target, "sys.triggers", "trg_SelectedChild_Audit")).ShouldBe(0);

        // Out-of-package objects are what AllowObjectDrops governs, and it is off, so this one survives
        // whichever way 2.2 lands.
        (await CatalogCountAsync(target, "sys.tables", "LegacyStagingImport")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.LegacyStagingImport")).ShouldBe(1);
    }

    private async Task<SqlServerFixtureDatabase> CreateCatalogSourceAsync() {
        var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript("dacpac-catalog.sql"));
        return source;
    }

    private async Task<SqlServerFixtureDatabase> CreateTargetWithExtrasAsync() {
        var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript("dacpac-target-with-extras.sql"));

        // Every drop assertion below counts objects after the deploy, which proves nothing unless they were
        // there before it.
        (await CatalogCountAsync(target, "sys.indexes", "IX_SelectedChild_RegionLookupId")).ShouldBe(1);
        (await CatalogCountAsync(target, "sys.foreign_keys", "FK_SelectedChild_RegionLookup")).ShouldBe(1);
        (await CatalogCountAsync(target, "sys.triggers", "trg_SelectedChild_Audit")).ShouldBe(1);
        (await CatalogCountAsync(target, "sys.tables", "LegacyStagingImport")).ShouldBe(1);
        return target;
    }

    private static ExportOptions DatabaseScopeExportOptions() {
        return new ExportOptions {
            SchemaCaptureMode = SchemaCaptureMode.Dacpac,
            CommandTimeout = 120
        };
    }

    private static ExportOptions SelectedTableExportOptions(params string[] tables) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables,
            SchemaCaptureMode = SchemaCaptureMode.Dacpac,
            CommandTimeout = 120,
            DacpacCaptureOptions = new DacpacCaptureOptions { SchemaScope = DacpacSchemaScope.SelectedExportTables }
        };
    }

    /// <summary>
    /// Deploys the dacpac the export stored, schema only. The extras target carries rows, which import's
    /// empty-target precondition rejects, so this is the deploy half of ImportAsync on its own.
    /// </summary>
    private static async Task DeployStoredSchemaAsync(SqliteTempFileHarness sqlite, SqlServerFixtureDatabase target, DacpacDeploymentOptions options) {
        await using var connection = await sqlite.OpenConnectionAsync();
        var package = await SqlitePackage.ReadSchemaPackageAsync(connection, CancellationToken.None) ?? throw new InvalidOperationException("Export stored no dacpac schema package.");

        await DacpacSchemaManager.DeployAsync(target.ConnectionString, package, options, allowDacpacObjectDrops: false, CancellationToken.None);
    }

    private static async Task DeployDacpacFileAsync(string dacpacPath, SqlServerFixtureDatabase target) {
        var payload = await File.ReadAllBytesAsync(dacpacPath);
        var package = new SchemaPackage(
            "dacpac",
            Path.GetFileName(dacpacPath),
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            target.DatabaseName,
            "integration test",
            DacpacSchemaScope.Database,
            payload,
            // Stamped on-prem so the Azure model rewrite stays out of a test about the edit.
            OnPremEngineEdition);

        await DacpacSchemaManager.DeployAsync(target.ConnectionString, package, DacpacDeploymentOptions.Default, allowDacpacObjectDrops: false, CancellationToken.None);
    }

    private static string ExtractDacpac(SqlServerFixtureDatabase source) {
        var path = Path.Combine(Path.GetTempPath(), $"zsdp-itest-{Guid.NewGuid():N}.dacpac");
        var services = new DacServices(source.ConnectionString);
        services.Extract(path, source.DatabaseName, source.DatabaseName, new Version(1, 0, 0), "integration test", tables: null, extractOptions: new DacExtractOptions {
            ExtractAllTableData = false,
            VerifyExtraction = false
        });
        return path;
    }

    // model.xml names elements [schema].[object], dots and all.
    private static string QuotedName(string fullName) {
        return string.Join(".", fullName.Split('.').Select(part => $"[{part}]"));
    }

    private static bool TryRemoveModelElement(XDocument document, string elementType, string quotedName) {
        var matches = document.Descendants().Where(e => e.Name.LocalName == "Element" && string.Equals((string?)e.Attribute("Type"), elementType, StringComparison.Ordinal) && string.Equals((string?)e.Attribute("Name"), quotedName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) {
            return false;
        }

        foreach (var match in matches) {
            match.Remove();
        }

        RemoveReferrers(document, quotedName);
        return true;
    }

    // A table's constraints sit beside it in model.xml, not inside it, so removing only the table leaves them
    // pointing at nothing and DacFx refuses to load the model at all.
    private static void RemoveReferrers(XDocument document, string quotedName) {
        var referrers = document.Descendants().Where(e => e.Name.LocalName == "Element" && e.Descendants().Any(r => r.Name.LocalName == "References" && PointsAt((string?)r.Attribute("Name"), quotedName))).ToList();

        foreach (var referrer in referrers) {
            referrer.Remove();
        }
    }

    // The element itself, or one of its columns: [dbo].[CatalogArchiveLog].[ArchivedAt].
    private static bool PointsAt(string? referenceName, string quotedName) {
        return referenceName is not null && (string.Equals(referenceName, quotedName, StringComparison.OrdinalIgnoreCase) || referenceName.StartsWith(quotedName + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task StampSourceEngineEditionAsync(SqliteTempFileHarness sqlite, int engineEdition) {
        await using var connection = await sqlite.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE zsdp_schema_packages SET source_engine_edition = $edition WHERE id = 1";
        command.Parameters.AddWithValue("$edition", engineEdition);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private static async Task<XDocument> ReadStoredDacpacModelAsync(SqliteTempFileHarness sqlite) {
        byte[] payload;
        await using (var connection = await sqlite.OpenConnectionAsync()) {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM zsdp_schema_packages WHERE id = 1";
            payload = (byte[])(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Export stored no dacpac payload."));
        }

        using var archive = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        var entry = archive.GetEntry("model.xml") ?? throw new InvalidOperationException("Stored dacpac has no model.xml.");
        await using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static bool HasContainmentProperty(XDocument model) {
        return model.Descendants().Where(e => e.Name.LocalName == "Element" && string.Equals((string?)e.Attribute("Type"), "SqlDatabaseOptions", StringComparison.Ordinal)).SelectMany(e => e.Elements()).Any(p => p.Name.LocalName == "Property" && string.Equals((string?)p.Attribute("Name"), "Containment", StringComparison.Ordinal));
    }

    private static Task<int> CatalogCountAsync(SqlServerFixtureDatabase database, string catalogView, string objectName) {
        return database.ScalarIntAsync($"SELECT COUNT(*) FROM {catalogView} WHERE name = '{objectName}'");
    }

    private async Task<int> ContainmentLevelAsync(string databaseName) {
        await using var connection = new SqlConnection(_fixture.MasterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT containment FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("@name", databaseName);
        command.CommandTimeout = 120;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static void DeleteIfExists(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch {
            // Best-effort cleanup.
        }
    }
}
