using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

// What happens when a type does not fit, is not known, or narrows on the way in. Every test here either
// asserts the failure names the offending column, or pins a silent behaviour so that changing it is
// deliberate rather than accidental.
[Collection(nameof(SqlServerCollection))]
public sealed class TypePrecisionAndFailureTests {
    private const string TypeVault = "type-vault.sql";

    private readonly SqlServerContainerFixture _fixture;

    public TypePrecisionAndFailureTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("dbo.AliasTypeHazard", "Phone", "PhoneNumber", "")]
    [InlineData("dbo.SysnameHazard", "CatalogName", "sysname", "")]
    [InlineData("dbo.SpatialHazard", "Location", "geography", "")]
    [InlineData("dbo.HierarchyHazard", "OrgNode", "hierarchyid", "")]
    [InlineData("dbo.VariantHazard", "LegacyValue", "sql_variant", "")]
    // Geography is the first unsupported column on SpatialHazard, so geometry only gets to name itself
    // once geography is out of the way.
    [InlineData("dbo.SpatialHazard", "Shape", "geometry", "dbo.SpatialHazard.Location")]
    public async Task Export_UnsupportedColumnType_FailsNamingColumnAndType(string table, string column, string declaredTypeName, string excludedColumn) {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var sqlite = new SqliteTempFileHarness();
        var options = ExportOnly(table);
        if (excludedColumn.Length > 0) {
            options.ExcludeColumns = [excludedColumn];
        }

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options));

        // v1_todo 4.1: the alias and sysname cases abort the whole export because the catalog read joins on
        // user_type_id, so the alias name never reaches the known-type map. Loud is the point -- if a fix
        // resolves the base type instead, the column must still be named, not quietly exported.
        exception.Message.ShouldContain($"{table}.{column}");
        exception.Message.ShouldContain(declaredTypeName);
        File.Exists(sqlite.FilePath).ShouldBeFalse("A failed export must not leave a package behind.");
    }

    [Fact]
    public async Task Export_UnsupportedTypeColumns_AreRejectedNotDropped() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var rejected = new SqliteTempFileHarness();

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(source.ConnectionString, rejected.FilePath, ExportOnly("dbo.SpatialHazard", "dbo.HierarchyHazard", "dbo.VariantHazard")));

        // Tables are validated in name order, so hierarchyid is the one that gets to speak.
        exception.Message.ShouldContain("dbo.HierarchyHazard.OrgNode");
        exception.Message.ShouldContain("hierarchyid");

        // geography, geometry and hierarchyid are CLR types: system_type_id 240, and sys.types has no row
        // whose user_type_id is 240. A catalog read that resolved types by joining on the base type would
        // drop those three columns out of the result set entirely, turning the error above into a column
        // that vanishes without comment. sql_variant (system_type_id 98) rides along as the fourth type the
        // package cannot store. Excluding all four proves they were read, named and carried into the
        // package metadata -- never filtered out.
        await using var exported = new SqliteTempFileHarness();
        var options = ExportOnly("dbo.SpatialHazard", "dbo.HierarchyHazard", "dbo.VariantHazard");
        options.ExcludeColumns = [
            "dbo.SpatialHazard.Location",
            "dbo.SpatialHazard.Shape",
            "dbo.HierarchyHazard.OrgNode",
            "dbo.VariantHazard.LegacyValue"
        ];

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, exported.FilePath, options);

        result.TableCount.ShouldBe(3);
        result.RowCount.ShouldBe(3);

        await using var connection = await exported.OpenConnectionAsync();
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.SpatialHazard", "Location", typeName: "geography", isExcluded: true);
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.SpatialHazard", "Shape", typeName: "geometry", isExcluded: true);
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.HierarchyHazard", "OrgNode", typeName: "hierarchyid", isExcluded: true);
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.VariantHazard", "LegacyValue", typeName: "sql_variant", isExcluded: true);
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.SpatialHazard", "SpatialHazardId", typeName: "int", isExcluded: false);

        await SqlitePackageAssertions.HasExclusionAsync(connection, "column", "dbo.SpatialHazard.Location");
        await SqlitePackageAssertions.HasExclusionAsync(connection, "column", "dbo.SpatialHazard.Shape");
        await SqlitePackageAssertions.HasExclusionAsync(connection, "column", "dbo.HierarchyHazard.OrgNode");
        await SqlitePackageAssertions.HasExclusionAsync(connection, "column", "dbo.VariantHazard.LegacyValue");

        // The CLR columns are gone from the physical shape, the rows they sat on are not.
        (await connection.TableColumnExistsAsync("dbo__spatialhazard", "Location")).ShouldBeFalse();
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__spatialhazard")).ShouldBe(1);
    }

    [Fact]
    public async Task Export_DecimalOverflow_ThrowsBareOverflowExceptionNamingNothing() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var overflowing = new SqliteTempFileHarness();

        var exception = await Should.ThrowAsync<OverflowException>(() => new SqlDataPackExporter().ExportAsync(source.ConnectionString, overflowing.FilePath, ExportOnly("dbo.LedgerAmounts")));

        // Pinned, not endorsed. v1_todo: a decimal(38,x) beyond .NET decimal's range dies mid-copy with a
        // framework exception that names neither the table, the column nor the value. When the guard lands
        // this flips to a SqlDataPackException naming dbo.LedgerAmounts.HugeWholeAmount and the value, and
        // this test gets rewritten deliberately.
        exception.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase).ShouldBeTrue($"Unexpected overflow message: {exception.Message}");
        exception.Message.ShouldNotContain("LedgerAmounts");
        exception.Message.ShouldNotContain("HugeWholeAmount");
        File.Exists(overflowing.FilePath).ShouldBeFalse("A failed export must not leave a package behind.");

        // The guard, whatever form it takes, must not become a blanket ban on high-precision decimals:
        // decimal(28,10) is inside .NET decimal's range and has to survive digit for digit.
        await using var inRange = new SqliteTempFileHarness();

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, inRange.FilePath, ExportOnly("dbo.LegacyImportRows"));

        result.RowCount.ShouldBe(3);
        await using var connection = await inRange.OpenConnectionAsync();
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.LegacyImportRows", "DecimalHighPrecision", typeName: "decimal", precision: 28, scale: 10);
        var stored = await connection.ReadStringsAsync("SELECT DecimalHighPrecision FROM dbo__legacyimportrows ORDER BY LegacyImportRowId");
        var onServer = await source.ReadStringsAsync("SELECT CONVERT(VARCHAR(40), DecimalHighPrecision) FROM dbo.LegacyImportRows ORDER BY LegacyImportRowId");
        stored.ShouldBe(onServer);
    }

    [Fact]
    public async Task RoundTrip_HigherPrecisionIntoLowerPrecisionTarget_TruncatesAndWarns() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplyTargetVariantAsync(target, TypeVault, null, TargetSchemaScripts.Variants.DatePrecisionCollapse);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, ExportOnly("dbo.ChronoExtremes"));
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, NoAdaptiveBatching());

        importResult.RowCount.ShouldBe(3);

        // Dt2Precision7 narrows datetime2(7) -> datetime2(3): same type name, smaller scale, Lossy.
        // RegularDt (datetime -> datetime2(3)) and SmallDt (smalldatetime -> datetime2(0)) change type name
        // and are reported as Widening even though neither actually loses data here.
        importResult.Warnings.ShouldContain(w => w.Contains("dbo.ChronoExtremes.Dt2Precision7") && w.Contains("datetime2(3)") && w.Contains("datetime2(7)") && w.Contains("truncated"));
        importResult.Warnings.ShouldContain(w => w.Contains("dbo.ChronoExtremes.RegularDt"));
        importResult.Warnings.ShouldContain(w => w.Contains("dbo.ChronoExtremes.SmallDt"));

        const string Projection = """
                                  SELECT
                                      ChronoExtremeId,
                                      CONVERT(VARCHAR(40), Dt2Precision7, 121),
                                      CONVERT(VARCHAR(40), RegularDt, 121),
                                      CONVERT(VARCHAR(40), SmallDt, 121)
                                  FROM dbo.ChronoExtremes
                                  ORDER BY ChronoExtremeId
                                  """;

        // datetime2(7) -> datetime2(3) truncates, it does not round. The source's .0000000 / .9999999 /
        // .1234567 land as .000 / .999 / .123, so the 9999 row keeps .999 instead of rolling the year over.
        // datetime -> datetime2(3) and smalldatetime -> datetime2(0) lose nothing.
        (await target.ReadRowsAsync(Projection)).ShouldBe(new[] {
            "1 | 0001-01-01 00:00:00.000 | 1753-01-01 00:00:00.000 | 1900-01-01 00:00:00",
            "2 | 9999-12-31 23:59:59.999 | 9999-12-31 23:59:59.997 | 2079-06-06 23:59:00",
            "3 | 2024-06-15 12:30:45.123 | 2024-06-15 12:30:45.123 | 2024-06-15 12:31:00"
        });
    }

    [Fact]
    public async Task Import_TypeDrift_WarnsButStillImports() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplyTargetVariantAsync(target, TypeVault, null, TargetSchemaScripts.Variants.TypeDrift);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, ExportOnly("dbo.DriftSamples"));

        // Import matches columns by name; type drift no longer passes silently. datetime2(7) -> datetime2(3),
        // nvarchar(100) -> varchar(100) and decimal(18,6) -> decimal(18,2) all warn as Lossy, but none of
        // them block the import.
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, NoAdaptiveBatching());

        importResult.RowCount.ShouldBe(2);
        importResult.Warnings.ShouldContain(w => w.Contains("dbo.DriftSamples.RecordedAt") && w.Contains("datetime2(3)") && w.Contains("datetime2(7)") && w.Contains("truncated"));
        importResult.Warnings.ShouldContain(w => w.Contains("dbo.DriftSamples.Description") && w.Contains("varchar(100)") && w.Contains("nvarchar(100)") && w.Contains("code page"));
        importResult.Warnings.ShouldContain(w => w.Contains("dbo.DriftSamples.Amount") && w.Contains("decimal(18,2)") && w.Contains("decimal(18,6)") && w.Contains("rounded"));

        const string Projection = """
                                  SELECT
                                      DriftSampleId,
                                      CONVERT(VARCHAR(40), RecordedAt, 121),
                                      CONVERT(VARCHAR(40), Amount)
                                  FROM dbo.DriftSamples
                                  ORDER BY DriftSampleId
                                  """;

        // Fractional seconds truncated to 3 digits, amounts rounded to 2 places.
        (await target.ReadRowsAsync(Projection)).ShouldBe(new[] {
            "1 | 2024-05-01 10:11:12.123 | 1234.57",
            "2 | 2024-05-02 08:09:10.765 | 100.00"
        });

        // The nvarchar row is mangled by the varchar target: both CJK characters become '?', while the
        // Latin-1 e-acute survives because the target collation's code page happens to have it. Compared as
        // raw UTF-16 bytes so a collation-aware string compare cannot hide the damage.
        (await DescriptionBytesAsync(target, 1)).ShouldNotBe(await DescriptionBytesAsync(source, 1));
        (await DescriptionBytesAsync(target, 2)).ShouldBe(await DescriptionBytesAsync(source, 2));
        (await target.ScalarStringAsync("SELECT CONVERT(NVARCHAR(100), Description) FROM dbo.DriftSamples WHERE DriftSampleId = 1")).ShouldBe("?? report \u00E9dition");
    }

    /// <summary>
    /// Adaptive batching emits a per-table warning on fixture tables this small, which would swamp the
    /// "was anything recorded?" assertions. Off here, so a warning means something actually went wrong.
    /// </summary>
    private static ExportOptions ExportOnly(params string[] tables) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables,
            AdaptiveBatchingEnabled = false
        };
    }

    private static ImportOptions NoAdaptiveBatching() {
        return new ImportOptions { AdaptiveBatchingEnabled = false };
    }

    /// <summary>The description as raw UTF-16 bytes, so source and target compare byte for byte.</summary>
    private static Task<string> DescriptionBytesAsync(SqlServerFixtureDatabase db, int id) {
        return db.ScalarHexAsync($"SELECT CONVERT(VARBINARY(MAX), CONVERT(NVARCHAR(100), Description)) FROM dbo.DriftSamples WHERE DriftSampleId = {id}");
    }
}
