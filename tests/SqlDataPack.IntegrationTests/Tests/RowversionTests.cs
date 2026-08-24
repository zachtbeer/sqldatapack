using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// rowversion/timestamp: opaque on the way out, server-generated on the way in. The package carries the eight
/// bytes for inspection and warns; the import leaves the column out of its column set entirely so SQL Server
/// stamps its own values.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class RowversionTests {
    private const string RowversionFixture = "rowversion-audit.sql";

    private static readonly string[] EventNames = ["login", "logout", "password-reset"];

    private readonly SqlServerContainerFixture _fixture;

    public RowversionTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_RowversionColumn_StoresOpaqueBlobAndWarns() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(RowversionFixture));
        await using var sqlite = new SqliteTempFileHarness();

        var sourceBytes = new Dictionary<string, string>();
        foreach (var name in EventNames) {
            sourceBytes[name] = await source.ScalarHexAsync($"SELECT CONVERT(VARBINARY(8), Rv) FROM dbo.AuditTrails WHERE EventName = N'{name}'");
        }

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.AuditTrails"));

        exportResult.TableCount.ShouldBe(1);
        exportResult.RowCount.ShouldBe(3);
        exportResult.Warnings.ShouldContain(w => w.Contains("'dbo.AuditTrails' column 'Rv'", StringComparison.Ordinal));

        await using var package = await sqlite.OpenConnectionAsync();
        // SQL Server reports a ROWVERSION column under the type name 'timestamp'; the package records what the
        // server said, not what the DDL was written as.
        await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.AuditTrails", "Rv", typeName: "timestamp", isIdentity: false, isComputed: false, isExcluded: false);
        (await package.ScalarStringAsync("SELECT type FROM pragma_table_info('dbo__audittrails') WHERE name = 'Rv'")).ShouldBe("BLOB");

        foreach (var name in EventNames) {
            (await package.ScalarStringAsync($"SELECT typeof(Rv) FROM dbo__audittrails WHERE EventName = '{name}'")).ShouldBe("blob");
            var stored = await SqlitePackageAssertions.ReadHexAsync(package, $"SELECT Rv FROM dbo__audittrails WHERE EventName = '{name}'");
            stored.ShouldBe(sourceBytes[name], $"'{name}': the packaged rowversion is not the source's eight bytes.");
        }

        await SqlitePackageAssertions.HasWarningMatchingAsync(package, "'dbo.AuditTrails' column 'Rv' is a timestamp");
    }

    [Fact]
    public async Task Import_RowversionColumn_LetsTheServerGenerateNewValues() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(RowversionFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, RowversionFixture);
        await using var sqlite = new SqliteTempFileHarness();

        var sourceMaxRv = await source.ScalarHexAsync("SELECT TOP (1) CONVERT(VARBINARY(8), Rv) FROM dbo.AuditTrails ORDER BY Rv DESC");
        var targetDbts = await ReadDbtsAsync(target);
        // Churn rows through the target's own rowversion table until its @@DBTS is past every source value.
        // Without this the two databases' counters overlap and "target differs from source" proves nothing.
        for (var attempt = 0; attempt < 20 && string.CompareOrdinal(targetDbts, sourceMaxRv) <= 0; attempt++) {
            await target.ExecuteSqlAsync("""
                                         INSERT INTO dbo.AuditTrails (EventName) VALUES (N'dbts-bump');
                                         DELETE FROM dbo.AuditTrails;
                                         """);
            targetDbts = await ReadDbtsAsync(target);
        }

        string.CompareOrdinal(targetDbts, sourceMaxRv).ShouldBeGreaterThan(0, $"Target @@DBTS {targetDbts} must exceed the source's largest Rv {sourceMaxRv} before the import, or the comparison below is coincidence.");

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.AuditTrails"));

        // ImportAsync never builds the server-generated-column warning: the "values from the package are
        // skipped" wording comes out of PreflightAsync only. What ImportAsync surfaces is the export warning
        // carried in the package. Each is asserted where it actually appears.
        var preflight = await new SqlDataPackImporter().PreflightAsync(sqlite.FilePath, target.ConnectionString);
        preflight.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, preflight.Errors));
        preflight.Warnings.ShouldContain(w => w.Contains("'dbo.AuditTrails' column 'Rv'", StringComparison.Ordinal) && w.Contains("skipped", StringComparison.Ordinal));

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(3);
        importResult.Warnings.ShouldContain(w => w.Contains("'dbo.AuditTrails' column 'Rv'", StringComparison.Ordinal) && w.Contains("Bytes are captured for inspection", StringComparison.Ordinal));

        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, "dbo.AuditTrails", "Rv");

        // Every target value was stamped after the bump, so the smallest of them beats the largest source
        // value. A `<>` comparison would pass on any copied-then-mangled byte; this only passes if the column
        // was left out of the insert and SQL Server generated the values itself.
        var targetMinRv = await target.ScalarHexAsync("SELECT TOP (1) CONVERT(VARBINARY(8), Rv) FROM dbo.AuditTrails ORDER BY Rv ASC");
        string.CompareOrdinal(targetMinRv, sourceMaxRv).ShouldBeGreaterThan(0, $"Target's smallest Rv {targetMinRv} does not exceed the source's largest Rv {sourceMaxRv}; the package's bytes were written through.");
    }

    [Fact]
    public async Task Import_TargetOnlyRowversion_DoesNotTripTheExtraColumnRule() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(RowversionFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, RowversionFixture);
        // The evolved target: the same table the source has, plus the audit column the source never had.
        await target.ExecuteSqlAsync("ALTER TABLE dbo.AuditTrailsLegacy ADD Rv ROWVERSION;");
        await using var sqlite = new SqliteTempFileHarness();

        // The extra-target-column rule only fires on a non-nullable, undefaulted column, so this is the shape
        // that would be rejected if rowversion were not recognised as server-generated.
        (await target.ScalarIntAsync("""
                                     SELECT COUNT(*)
                                     FROM sys.columns
                                     WHERE object_id = OBJECT_ID(N'dbo.AuditTrailsLegacy')
                                       AND name = 'Rv'
                                       AND is_nullable = 0
                                       AND default_object_id = 0
                                     """)).ShouldBe(1);

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.AuditTrailsLegacy"));
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(3);

        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, "dbo.AuditTrailsLegacy", "Rv");
    }

    private static async Task<string> ReadDbtsAsync(SqlServerFixtureDatabase db) {
        return await db.ScalarHexAsync("SELECT CONVERT(VARBINARY(8), @@DBTS)");
    }

    /// <summary>
    /// rowversion-audit.sql holds both the table with the rowversion column and the one without, so every test
    /// exports only the half it is about.
    /// </summary>
    private static ExportOptions OnlyTable(string fullName) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [fullName]
        };
    }
}
