using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using SqlDataPack.Transformations;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Export transformations against a real source database: what lands in the package for a configured column,
/// what stays untouched, and what fails the export.
/// <para>
/// The assertions that matter are negative ones — the original values are nowhere in the package — plus the
/// deterministic-within-one-export contract, checked across two tables that share tenant identifiers.
/// </para>
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class TransformationTests {
    private const string SourceFixture = "core-commerce.sql";

    private readonly SqlServerContainerFixture _fixture;

    public TransformationTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_ConfiguredColumns_AreTransformedAndTheOriginalsNeverReachThePackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.Name", new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" }));
        options.Transformations.Add("dbo.Customers.Notes", new StringMasker());
        options.Transformations.Add("dbo.Customers.ExternalId", new GuidPseudonymizer());
        options.Transformations.Add("dbo.Customers.CreditLimit", new NumericPseudonymizer());

        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        await using var connection = await sqlite.OpenConnectionAsync();

        // Named source rows, masked to their first two characters plus the suffix.
        var names = await connection.ReadStringsAsync("SELECT Name FROM dbo__customers ORDER BY CustomerId");
        names.ShouldNotContain("O'Brien [VIP]");
        names.ShouldContain("O'test");
        names.ShouldAllBe(name => name.EndsWith("test", StringComparison.Ordinal));

        // Free-text notes are masked to stars, and the NULL ones were never handed to the transformer.
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE Notes IS NOT NULL AND Notes GLOB '*[^*]*'")).ShouldBe(0);
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE Notes IS NULL")).ShouldBeGreaterThan(0);

        // Every pseudonymized GUID is still a GUID, and none of them is a source GUID.
        var externalIds = await connection.ReadStringsAsync("SELECT ExternalId FROM dbo__customers");
        externalIds.Select(IsGuid).ShouldAllBe(isGuid => isGuid);
        var sourceExternalIds = (await db.ReadStringsAsync("SELECT CONVERT(NVARCHAR(36), ExternalId) FROM dbo.Customers")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        externalIds.ShouldAllBe(id => !sourceExternalIds.Contains(id));

        // decimal(18,2) stays inside its own precision and scale.
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE CAST(CreditLimit AS REAL) < 0")).ShouldBe(0);
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE LENGTH(SUBSTR(CreditLimit, INSTR(CreditLimit, '.') + 1)) > 2")).ShouldBe(0);

        // Untouched columns are untouched.
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE CustomerId = 100")).ShouldBe(1);
    }

    [Fact]
    public async Task Export_TheSameValueInTwoTables_PseudonymizesConsistentlyWithinTheExport() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.Customers", "dbo.Orders");
        options.Transformations.Add("dbo.Customers.TenantId", new NumericPseudonymizer());
        options.Transformations.Add("dbo.Orders.TenantId", new NumericPseudonymizer());

        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        await using var connection = await sqlite.OpenConnectionAsync();
        var fromCustomers = await connection.ReadStringsAsync("SELECT DISTINCT TenantId FROM dbo__customers ORDER BY TenantId");
        var fromOrders = await connection.ReadStringsAsync("SELECT DISTINCT TenantId FROM dbo__orders ORDER BY TenantId");

        // Three source tenants, so three pseudonyms, and both tables agree on them.
        fromCustomers.Count.ShouldBe(3);
        fromOrders.ShouldBe(fromCustomers);
        fromCustomers.ShouldNotContain("1");
    }

    [Fact]
    public async Task Export_RunTwice_ProducesDifferentPseudonymsForTheSameSource() {
        await using var db = await CreateSourceAsync();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.ExternalId", new GuidPseudonymizer());

        string First() => "SELECT ExternalId FROM dbo__customers ORDER BY CustomerId";

        await using var firstPackage = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, firstPackage.FilePath, options);
        await using var firstConnection = await firstPackage.OpenConnectionAsync();
        var first = await firstConnection.ReadStringsAsync(First());

        await using var secondPackage = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, secondPackage.FilePath, options);
        await using var secondConnection = await secondPackage.OpenConnectionAsync();
        var second = await secondConnection.ReadStringsAsync(First());

        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task Export_TransformedKeyColumn_IsAllowed() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        // An identity primary key. SqlDataPack does not stop this; keeping the rows joinable afterwards is
        // the caller's problem, and a deterministic pseudonymizer on both sides is how they would do it.
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.CustomerId", new NumericPseudonymizer());

        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        await using var connection = await sqlite.OpenConnectionAsync();
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE CustomerId < 0")).ShouldBe(0);
    }

    [Fact]
    public async Task Export_TransformationMetadata_IsRecordedInThePackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.Name", new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" }));
        options.Transformations.Add("dbo.Customers.Notes", new CustomTransformer((_, value) => $"TEST-{value}"));

        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);
        var manifest = await new SqlDataPackReader().ReadManifestAsync(sqlite.FilePath);

        manifest.Transformations.Select(t => $"{t.ColumnPath}|{t.TransformerType}|{t.Configuration}").ShouldBe([
            "dbo.Customers.Name|NameMasker|PreserveCharacters=2;Suffix=test",
            "dbo.Customers.Notes|Custom|"
        ]);
    }

    [Fact]
    public async Task Export_TransformerThatThrows_FailsWithoutLeavingAPackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.Name", new CustomTransformer((_, _) => throw new InvalidOperationException("boom")));

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("failed on dbo.Customers.Name");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_TransformerReturningNullForANonNullableColumn_FailsWithoutLeavingAPackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.Name", new CustomTransformer((_, _) => null));

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("which is not nullable");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_ResultLongerThanTheColumn_FailsRatherThanTruncating() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.Customers");
        // dbo.Customers.Name is nvarchar(100).
        options.Transformations.Add("dbo.Customers.Name", new CustomTransformer((_, _) => new string('x', 101)));

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("holds at most 100");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Preflight_TransformationOnAnUnknownColumn_IsRejectedBeforeAnyRowIsRead() {
        await using var db = await CreateSourceAsync();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.Emial", new EmailMasker());

        var result = await new SqlDataPackExporter().PreflightAsync(db.ConnectionString, options);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("references a column that does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preflight_ReportsThePlannedTransformations() {
        await using var db = await CreateSourceAsync();
        var options = Only("dbo.Customers");
        options.Transformations.Add("dbo.Customers.Name", new NameMasker());

        var result = await new SqlDataPackExporter().PreflightAsync(db.ConnectionString, options);

        result.IsValid.ShouldBeTrue();
        result.Manifest.ShouldNotBeNull();
        result.Manifest.Transformations.ShouldHaveSingleItem().ColumnPath.ShouldBe("dbo.Customers.Name");
    }

    private async Task<SqlServerFixtureDatabase> CreateSourceAsync() {
        var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        return db;
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;

    private static ExportOptions Only(params string[] patterns) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = patterns
        };
    }
}
