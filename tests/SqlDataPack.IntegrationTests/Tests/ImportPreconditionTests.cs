using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Everything import checks about the target before it copies a single row.
/// <para>
/// Every negative test here asserts two things: the exception names the offending object, and the target is
/// exactly as it was before the call. The second half is the point -- it proves validation runs up front
/// rather than interleaved with copying, which is what makes a failed import all-or-nothing and therefore
/// retryable (a half-populated target would be blocked by the empty-target rule on the retry).
/// </para>
/// <para>
/// Source is core-commerce's parent/child pair, dbo.Customers -> dbo.CustomerProfiles. Customers comes first
/// in import order, so it is the table that would have loaded if validation were interleaved.
/// </para>
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class ImportPreconditionTests {
    private const string SourceFixture = "core-commerce.sql";
    private const string Parent = "dbo.Customers";
    private const string Child = "dbo.CustomerProfiles";

    private readonly SqlServerContainerFixture _fixture;

    public ImportPreconditionTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Import_MissingTargetTable_FailsBeforeAnyCopy() {
        await using var source = await CreateSourceAsync();
        await using var target = await CreateTargetAsync(TargetSchemaScripts.Variants.MissingChildTable);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, PackageOptions());

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        exception.Message.ShouldContain($"Target table '{Child}' does not exist");
        exception.Message.ShouldContain("Create the target schema before import or exclude this table from the export scope");

        // Customers is first in import order and exists on the target, so an interleaved validator would
        // have loaded all of it before ever noticing CustomerProfiles was gone.
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Parent}")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_MissingTargetColumn_FailsBeforeAnyCopy() {
        await using var source = await CreateSourceAsync();
        await using var target = await CreateTargetAsync(TargetSchemaScripts.Variants.MissingColumn);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, PackageOptions());

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        // Without this check SqlBulkCopy fails mid-copy with a far less informative mapping error.
        exception.Message.ShouldContain($"Target column '{Parent}.Notes' does not exist");
        exception.Message.ShouldContain("Create the target column before import or exclude the source column during export");
        await ShouldHaveCopiedNothingAsync(target);
    }

    [Fact]
    public async Task Import_TargetTableNotEmpty_FailsBeforeAnyCopy() {
        await using var source = await CreateSourceAsync();
        await using var target = await CreateCorrectTargetAsync();
        // CountryId stays NULL: dbo.Countries is empty on an unseeded target and a direct INSERT does check the FK.
        await target.ExecuteSqlAsync($"""
                                      INSERT INTO {Parent} (TenantId, IsActive, ExternalId, Name, CreditLimit, CreatedAt, Notes, CountryId)
                                      VALUES (99, 1, '0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0', N'Pre-existing Row', 1234.56, '2019-06-05T04:03:02', N'must survive the failed import', NULL);
                                      """);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, PackageOptions());
        var before = await ReadCustomerRowsAsync(target);

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        exception.Message.ShouldContain($"Target table '{Parent}' must be empty before import");
        (await ReadCustomerRowsAsync(target)).ShouldBe(before);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Child}")).ShouldBe(0);

        // Preflight is the other entry point into the same check and must reach the same verdict.
        var preflight = await new SqlDataPackImporter().PreflightAsync(sqlite.FilePath, target.ConnectionString);

        preflight.IsValid.ShouldBeFalse();
        preflight.Errors.ShouldContain(error => error.Contains($"Target table '{Parent}' must be empty before import"));
        (await ReadCustomerRowsAsync(target)).ShouldBe(before);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Child}")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_ExtraRequiredTargetColumn_FailsBeforeAnyCopy() {
        await using var source = await CreateSourceAsync();
        await using var target = await CreateTargetAsync(TargetSchemaScripts.Variants.ExtraRequiredColumn);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, PackageOptions());

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        // Otherwise every row hits the NOT NULL constraint and bulk copy reports a raw constraint violation.
        exception.Message.ShouldContain($"Extra target column '{Child}.RequiredExtra' is not nullable or defaulted");
        exception.Message.ShouldContain("Make the column nullable, add a default constraint, or remove the table from the import scope");
        await ShouldHaveCopiedNothingAsync(target);
    }

    /// <summary>
    /// The other half of the extra-column rule: nullable, defaulted, computed and rowversion extras are all
    /// allowed through. ComputedExtra and RowVersionExtra are NOT NULL with no default constraint, so the
    /// import completing at all is what proves validation exempts them rather than rejecting them the way it
    /// rejects RequiredExtra above.
    /// </summary>
    [Fact]
    public async Task Import_ExtraAllowedTargetColumns_Succeeds() {
        await using var source = await CreateSourceAsync();
        await using var target = await CreateTargetAsync(TargetSchemaScripts.Variants.ExtraAllowedColumns);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, PackageOptions());

        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.TableCount.ShouldBe(2);
        result.RowCount.ShouldBe(await SourceRowCountAsync(source));
        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, Parent);
        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, Child, "LegacyFlags", "SurrogateId", "NullableExtra", "DefaultedExtra", "ComputedExtra", "RowVersionExtra");

        // The extras are not in the column mapping, so the target decides their values: NULL where there is
        // no default, the default where there is one. Bulk copy runs without KeepNulls, so a mapping
        // regression that pulled an extra column in would show up here.
        var rows = await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Child}");
        rows.ShouldBeGreaterThan(0);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Child} WHERE NullableExtra IS NOT NULL")).ShouldBe(0);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Child} WHERE DefaultedExtra = 42")).ShouldBe(rows);
    }

    [Fact]
    public async Task Import_CorrectTarget_Succeeds() {
        await using var source = await CreateSourceAsync();
        await using var target = await CreateCorrectTargetAsync();
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, PackageOptions());

        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.TableCount.ShouldBe(2);
        result.RowCount.ShouldBe(await SourceRowCountAsync(source));
        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, Parent);
        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, Child, "LegacyFlags");
    }

    [Fact]
    public async Task Import_NarrowerTargetColumn_WarnsAndStillImports() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(200) NULL); INSERT INTO dbo.Widths VALUES (1, N'short');");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(100) NULL);");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Widths"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(1);
        result.Warnings.ShouldContain(w => w.Contains("dbo.Widths.Note") && w.Contains("nvarchar(100)") && w.Contains("nvarchar(200)"));
    }

    [Fact]
    public async Task Import_WiderTargetColumn_WarnsWithoutClaimingLoss() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(100) NULL); INSERT INTO dbo.Widths VALUES (1, N'short');");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(200) NULL);");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Widths"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(1);
        result.Warnings.ShouldContain(w => w.Contains("dbo.Widths.Note"));
        result.Warnings.ShouldNotContain(w => w.Contains("truncated"));
    }

    [Fact]
    public async Task Import_NarrowerTargetColumn_WithFailOnLossy_ThrowsBeforeAnyRowLands() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(200) NULL); INSERT INTO dbo.Widths VALUES (1, N'short');");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(100) NULL);");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Widths"));

        var options = new ImportOptions { FailOnLossyTypeMismatch = true };
        var exception = await Should.ThrowAsync<SqlDataPackException>(async () => await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, options));

        exception.Message.ShouldContain("dbo.Widths.Note");
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Widths")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_WiderTargetColumn_WithFailOnLossy_StillImports() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(100) NULL); INSERT INTO dbo.Widths VALUES (1, N'short');");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Widths (Id INT NOT NULL PRIMARY KEY, Note NVARCHAR(200) NULL);");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Widths"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { FailOnLossyTypeMismatch = true });

        result.RowCount.ShouldBe(1);
    }

    private static ExportOptions OnlyTable(string table) {
        return new ExportOptions { TableSelection = ExportTableSelectionMode.Only, Tables = [table] };
    }

    /// <summary>
    /// The parent/child pair, with CustomerProfiles' sql_variant column excluded -- the one exclusion
    /// core-commerce needs to export cleanly.
    /// </summary>
    private static ExportOptions PackageOptions() {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [Parent, Child],
            ExcludeColumns = [$"{Child}.LegacyFlags"]
        };
    }

    private async Task<SqlServerFixtureDatabase> CreateSourceAsync() {
        var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        return source;
    }

    private async Task<SqlServerFixtureDatabase> CreateCorrectTargetAsync() {
        var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, SourceFixture);
        return target;
    }

    private async Task<SqlServerFixtureDatabase> CreateTargetAsync(string variant) {
        var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplyTargetVariantAsync(target, SourceFixture, sourceSection: null, variant);
        return target;
    }

    private static async Task<long> SourceRowCountAsync(SqlServerFixtureDatabase source) {
        return await source.ScalarIntAsync($"SELECT COUNT(*) FROM {Parent}") + await source.ScalarIntAsync($"SELECT COUNT(*) FROM {Child}");
    }

    /// <summary>Both packaged tables at zero rows: the failure happened before any bulk copy started.</summary>
    private static async Task ShouldHaveCopiedNothingAsync(SqlServerFixtureDatabase target) {
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Parent}")).ShouldBe(0);
        (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {Child}")).ShouldBe(0);
    }

    /// <summary>Every column of every row, so "untouched" means all values rather than a row count.</summary>
    private static async Task<IReadOnlyList<string>> ReadCustomerRowsAsync(SqlServerFixtureDatabase target) {
        return await target.ReadRowsAsync($"""
                                           SELECT CustomerId, TenantId, IsActive, ExternalId, Name,
                                                  CONVERT(VARCHAR(40), CreditLimit),
                                                  CONVERT(VARCHAR(40), CreatedAt, 126),
                                                  Notes, CountryId
                                           FROM {Parent}
                                           ORDER BY CustomerId
                                           """);
    }
}
