using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Rows have to land in the target under their original keys, in an order the engine accepts. Covers
/// [identity-values-preserved], [fk-load-order] and [fk-cycle-and-self-reference-rejected].
/// <para>
/// Import bulk-copies without <c>CheckConstraints</c>, so the target's foreign keys do not reject an
/// out-of-order load -- they are left untrusted instead. That is why the load-order tests assert on the
/// rows themselves rather than trusting the import to have failed.
/// </para>
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class IdentityAndLoadOrderTests {
    private const string CoreCommerce = "core-commerce.sql";
    private const string FkCycle = "fk-cycle.sql";

    private readonly SqlServerContainerFixture _fixture;

    public IdentityAndLoadOrderTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task RoundTrip_IdentityValues_ArePreservedNotRenumbered() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));

        // CustomerProfiles ships with contiguous identity values 1..4, which a target that renumbers
        // from 1 would reproduce by accident. Re-key it so the child list is a fingerprint too.
        await source.ExecuteSqlAsync("""
                                     DELETE FROM dbo.CustomerProfiles;
                                     SET IDENTITY_INSERT dbo.CustomerProfiles ON;
                                     INSERT INTO dbo.CustomerProfiles (CustomerProfileId, CustomerId, DisplayName) VALUES
                                         (7,   1,   N'Profile A'),
                                         (19,  2,   N'Profile B'),
                                         (23,  10,  N'Profile C'),
                                         (104, 100, N'Profile D');
                                     SET IDENTITY_INSERT dbo.CustomerProfiles OFF;
                                     """);

        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerce);
        await using var sqlite = new SqliteTempFileHarness();

        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Countries", "dbo.Customers", "dbo.CustomerProfiles"],
            ExcludeColumns = ["dbo.CustomerProfiles.LegacyFlags"],
            // Keeps the export to the hand-seeded, non-contiguous identity block. The 200 bulk rows
            // above it are plain auto-increment and say nothing about renumbering.
            PerTableWhereClauses = [new PerTableWhereClause("dbo.Customers", "CustomerId <= 100")]
        };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(22);

        // The exact value lists are what catches renumbering. Gaps, a zero and a negative key mean a
        // target that reseeded from 1 produces a visibly different sequence, not a lucky match.
        (await target.ScalarStringAsync("SELECT STRING_AGG(CONVERT(VARCHAR(11), CustomerId), ',') WITHIN GROUP (ORDER BY CustomerId) FROM dbo.Customers"))
            .ShouldBe("-1,0,1,2,5,10,11,12,13,14,20,21,50,51,100");
        (await target.ScalarStringAsync("SELECT STRING_AGG(CONVERT(VARCHAR(11), CustomerProfileId), ',') WITHIN GROUP (ORDER BY CustomerProfileId) FROM dbo.CustomerProfiles"))
            .ShouldBe("7,19,23,104");
        (await target.ScalarStringAsync("SELECT STRING_AGG(CONVERT(VARCHAR(11), CustomerId), ',') WITHIN GROUP (ORDER BY CustomerProfileId) FROM dbo.CustomerProfiles"))
            .ShouldBe("1,2,10,100");
    }

    [Fact]
    public async Task Export_MultiHopChain_RecordsDeterministicImportOrder() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var firstPackage = new SqliteTempFileHarness();
        await using var secondPackage = new SqliteTempFileHarness();

        // Customers -> Orders -> OrderLines is the chain. GlobalSettings and tenant.Partners
        // depend on nothing and are here only to be ordered against each other.
        string[] tables = ["dbo.Customers", "dbo.Orders", "dbo.OrderLines", "dbo.GlobalSettings", "tenant.Partners"];
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables
        };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, firstPackage.FilePath, options);
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, secondPackage.FilePath, options);

        var plan = await ReadImportPlanAsync(firstPackage);

        // Membership first: IndexOf returns -1 for a table the plan never recorded, and -1 sorts before
        // everything, so the comparisons below would pass on a plan that dropped half the scope.
        plan.ShouldBe(tables, ignoreOrder: true);

        // Index comparisons, so a longer but still valid order passes.
        plan.IndexOf("dbo.Customers").ShouldBeLessThan(plan.IndexOf("dbo.Orders"));
        plan.IndexOf("dbo.Orders").ShouldBeLessThan(plan.IndexOf("dbo.OrderLines"));
        plan.IndexOf("dbo.GlobalSettings").ShouldBeLessThan(plan.IndexOf("tenant.Partners"));

        // The plan ships inside the package, so nondeterminism leaks into artifacts.
        (await ReadImportPlanAsync(secondPackage)).ShouldBe(plan);
    }

    [Fact]
    public async Task Import_MultiHopChain_ReplaysOrderAndLeavesNoOrphans() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerce);
        await using var sqlite = new SqliteTempFileHarness();

        // Countries and Currencies are in scope so the orphan checks below have parent rows to miss.
        // Leave them out and every Customers.CountryId dangles, which the import would still report as
        // a success -- bulk copy never checks the constraint.
        string[] tables = ["dbo.Countries", "dbo.Currencies", "dbo.Customers", "dbo.Orders", "dbo.OrderLines", "dbo.GlobalSettings", "tenant.Partners"];
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables
        };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.TableCount.ShouldBe(tables.Length);

        long expectedRows = 0;
        foreach (var table in tables) {
            var sourceRows = await source.ScalarIntAsync($"SELECT COUNT(*) FROM {table}");
            (await target.ScalarIntAsync($"SELECT COUNT(*) FROM {table}")).ShouldBe(sourceRows, table);
            expectedRows += sourceRows;
        }

        result.RowCount.ShouldBe(expectedRows);

        // With untrusted constraints an out-of-order load can "succeed" with dangling references.
        await ShouldHaveNoOrphansAsync(target, "dbo.Customers", "CountryId", "dbo.Countries", "CountryId");
        await ShouldHaveNoOrphansAsync(target, "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId");
        await ShouldHaveNoOrphansAsync(target, "dbo.Orders", "CurrencyId", "dbo.Currencies", "CurrencyId");
        await ShouldHaveNoOrphansAsync(target, "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId");
    }

    [Fact]
    public async Task Export_ForeignKeyCycle_FailsNamingBothTables() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(FkCycle));
        await using var blocked = new SqliteTempFileHarness();

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, blocked.FilePath));

        exception.Message.ShouldContain("dbo.CreditNotes");
        exception.Message.ShouldContain("dbo.Invoices");
        exception.Message.ShouldContain("Exclude one or more tables from the cycle.");
        File.Exists(blocked.FilePath).ShouldBeFalse();

        // The escape hatch the message points at has to actually work.
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.AllExcept,
            Tables = ["dbo.CreditNotes"]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(1);

        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasImportPlanAsync(connection, "dbo.Invoices");
    }

    [Fact]
    public async Task Export_SelfReferencingForeignKey_FailsNamingTheTable() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);

        // No fixture owns a self-referencing table: core-commerce dropped dbo.Employees so it could
        // export as a whole database, and fk-cycle.sql is reserved for the two-table cycle.
        await db.ExecuteSqlAsync("""
                                 CREATE TABLE dbo.Employees
                                 (
                                     EmployeeId INT IDENTITY(1,1) PRIMARY KEY,
                                     ManagerId  INT           NULL,
                                     FullName   NVARCHAR(100) NOT NULL,
                                     CONSTRAINT FK_Employees_Manager FOREIGN KEY (ManagerId) REFERENCES dbo.Employees (EmployeeId)
                                 );

                                 INSERT INTO dbo.Employees (ManagerId, FullName) VALUES (NULL, N'Root');
                                 INSERT INTO dbo.Employees (ManagerId, FullName) VALUES (1, N'Reports to root');
                                 """);
        await using var sqlite = new SqliteTempFileHarness();

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath));

        exception.Message.ShouldContain("dbo.Employees");
        exception.Message.ShouldContain("self-referencing foreign key");
        exception.Message.ShouldContain("Exclude the table");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    /// <summary>The recorded table names in plan order. The sequence column is just this list's index.</summary>
    private static async Task<List<string>> ReadImportPlanAsync(SqliteTempFileHarness sqlite) {
        await using var connection = await sqlite.OpenConnectionAsync();
        var names = await connection.ReadStringsAsync("""
                                                      SELECT source_schema || '.' || source_table
                                                      FROM zsdp_import_plan
                                                      ORDER BY sequence
                                                      """);
        return names.ToList();
    }

    private static async Task ShouldHaveNoOrphansAsync(SqlServerFixtureDatabase db, string childTable, string childColumn, string parentTable, string parentColumn) {
        var orphans = await db.ScalarIntAsync($"""
                                               SELECT COUNT(*)
                                               FROM {childTable} c
                                               LEFT JOIN {parentTable} p ON p.{parentColumn} = c.{childColumn}
                                               WHERE c.{childColumn} IS NOT NULL AND p.{parentColumn} IS NULL
                                               """);

        orphans.ShouldBe(0, $"{childTable}.{childColumn} -> {parentTable}.{parentColumn}");
    }
}
