using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Row filtering at export: which tables a clause reaches, how clauses combine, and what the package
/// records about it. Malformed-clause validation that never touches a database lives in the unit project;
/// only the cases needing a real catalog are here.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class WhereClauseTests {
    private const string Fixture = "core-commerce.sql";

    // Row counts seeded by core-commerce.sql. dbo.Customers carries both gating columns (TenantId,
    // IsActive), dbo.Orders carries TenantId plus OrderTotal, dbo.GlobalSettings carries neither, so
    // every scenario below lands on a different number and a filter that reaches the wrong table shows up
    // as a wrong count rather than a coincidence.
    private const int CustomersAll = 215;
    private const int CustomersTenant1 = 71;
    private const int CustomersTenant1Active = 54;
    private const int OrdersAll = 500;
    private const int OrdersTenant1 = 165;
    private const int OrdersOver500 = 350;
    private const int OrdersTenant1Over500Currency1 = 21;
    private const int GlobalSettingsAll = 3;
    private const int OrderLinesAll = 1001;
    private const int CountriesAll = 3;
    private const int CurrenciesAll = 3;
    private const int CustomerProfilesAll = 4;
    private const int CustomerDocumentsAll = 10;
    private const int TenantCustomersAll = 2;
    private const int TenantPartnersAll = 2;

    private readonly SqlServerContainerFixture _fixture;

    public WhereClauseTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_GlobalWhereClause_AppliesOnlyToTablesCarryingTheColumn() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(3);
        result.RowCount.ShouldBe(CustomersTenant1 + OrdersTenant1 + GlobalSettingsAll);

        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Customers", CustomersTenant1);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Orders", OrdersTenant1);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.GlobalSettings", GlobalSettingsAll);
        // The multi-tenant guarantee: no other tenant's rows are in the package at all.
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE TenantId <> 1")).ShouldBe(0);
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__orders WHERE TenantId <> 1")).ShouldBe(0);
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__globalsettings")).ShouldBe(GlobalSettingsAll);
    }

    [Fact]
    public async Task Export_MultipleGlobalClauses_ApplyIndependently() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            GlobalWhereClauses = [
                new GlobalWhereClause("TenantId", "TenantId = 1"),
                new GlobalWhereClause("IsActive", "IsActive = 1")
            ]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(3);
        result.RowCount.ShouldBe(CustomersTenant1Active + OrdersTenant1 + GlobalSettingsAll);

        await using var connection = await sqlite.OpenConnectionAsync();
        // Customers has both columns and gets both predicates ANDed; Orders has only TenantId and gets
        // only the first; GlobalSettings has neither and is untouched.
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Customers", CustomersTenant1Active);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Orders", OrdersTenant1);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.GlobalSettings", GlobalSettingsAll);
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customers WHERE TenantId <> 1 OR IsActive <> 1")).ShouldBe(0);
    }

    [Fact]
    public async Task Export_MultiColumnGlobalClause_AppliesOnlyToTablesHavingEveryColumn() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            GlobalWhereClauses = [new GlobalWhereClause(["TenantId", "IsActive"], "TenantId = 1 AND IsActive = 1")]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(3);
        result.RowCount.ShouldBe(CustomersTenant1Active + OrdersAll + GlobalSettingsAll);

        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Customers", CustomersTenant1Active);
        // Orders has TenantId but not IsActive, so the clause does not apply and it exports whole: 500,
        // not the 165 that Export_MultipleGlobalClauses_ApplyIndependently gets from a bare TenantId clause.
        // That contrast is the only thing separating multi-column from multiple single-column semantics.
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Orders", OrdersAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.GlobalSettings", GlobalSettingsAll);
    }

    [Fact]
    public async Task Export_MultiColumnGlobalClauseMatchingNoTable_FailsBeforeCreatingPackage() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            // IsActive lives only on Customers and OrderTotal only on Orders, so both columns exist but no
            // single table has all of them and the clause could never apply anywhere.
            GlobalWhereClauses = [new GlobalWhereClause(["IsActive", "OrderTotal"], "IsActive = 1 AND OrderTotal > 0")]
        };

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("Global WHERE clause columns 'IsActive', 'OrderTotal' did not match any selected source table");
        exception.Message.ShouldContain("every column it names");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_GlobalWhereClauseWithNoMatchingColumnAnywhere_FailsBeforeCreatingPackage() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            GlobalWhereClauses = [new GlobalWhereClause("MissingTenantId", "MissingTenantId = 1")]
        };

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("Global WHERE clause column 'MissingTenantId' did not match any selected source table");
        // A single-column clause gets no multi-column explanation appended: this is the no-such-column
        // path, not the columns-spread-across-tables one.
        exception.Message.ShouldNotContain("every column it names");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_GlobalWhereClauseExcludedByAllExceptScope_FailsBeforeCreatingPackage() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            // TenantId exists in the database, on Customers and Orders -- both of which this AllExcept
            // pattern removes. Validation has to run against the post-exclusion table set to notice.
            Tables = ["dbo.Customers", "dbo.Orders"],
            ExcludeColumns = ["dbo.CustomerProfiles.LegacyFlags"],
            GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")]
        };

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("Global WHERE clause column 'TenantId' did not match any selected source table");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_PerTableClause_StacksWithGlobalAndWithItself() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")],
            PerTableWhereClauses = [
                new PerTableWhereClause("dbo.Orders", "OrderTotal >= 500.00"),
                new PerTableWhereClause("dbo.Orders", "CurrencyId = 1")
            ]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(3);
        result.RowCount.ShouldBe(CustomersTenant1 + OrdersTenant1Over500Currency1 + GlobalSettingsAll);

        await using var connection = await sqlite.OpenConnectionAsync();
        // 21 is all three predicates ANDed. The fixture data separates that from every wrong combination:
        // 61 if the second per-table clause replaced the first (a dictionary keyed by table), 115 if the
        // second were dropped, 100 if the global clause were dropped, 403 if they were ORed.
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Orders", OrdersTenant1Over500Currency1);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Customers", CustomersTenant1);
        // OrderTotal lands in SQLite as TEXT, where an uncast comparison against 500.00 is lexicographic.
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__orders WHERE TenantId <> 1 OR CAST(OrderTotal AS REAL) < 500.00 OR CurrencyId <> 1")).ShouldBe(0);
    }

    [Fact]
    public async Task Export_PerTableClause_MatchesItsTableExactlyAndDoesNotLeak() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            PerTableWhereClauses = [new PerTableWhereClause("  dbo.orders  ", "TenantId = 1")]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(3);
        result.RowCount.ShouldBe(CustomersAll + OrdersTenant1 + GlobalSettingsAll);

        await using var connection = await sqlite.OpenConnectionAsync();
        // Padded and mis-cased still hits dbo.Orders: a strict match would leave it unfiltered at 500,
        // which is a silent no-op rather than an error.
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Orders", OrdersTenant1);
        // Customers carries TenantId too, so a column-matching implementation would over-filter it to 71.
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Customers", CustomersAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.GlobalSettings", GlobalSettingsAll);
    }

    [Theory]
    [InlineData(ExportTableSelectionMode.Only, "dbo.Customers")]
    [InlineData(ExportTableSelectionMode.AllExcept, "dbo.Orders")]
    public async Task Export_PerTableClauseOutsideSelectedScope_FailsBeforeCreatingPackage(ExportTableSelectionMode selection, string tablePattern) {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = selection,
            Tables = [tablePattern],
            PerTableWhereClauses = [new PerTableWhereClause("dbo.Orders", "TenantId = 1")]
        };

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("Per-table WHERE clause table 'dbo.Orders' is not in the selected export scope");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_FilterGate_ReadsSourceColumnsNotOutputColumns() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));

        // Single-column global clause gated on a column excluded from the output.
        await ExportsFilteredWithoutColumnAsync(
            new ExportOptions {
                TableSelection = ExportTableSelectionMode.Only,
                Tables = ["dbo.Orders"],
                ExcludeColumns = ["dbo.Orders.TenantId"],
                GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")]
            },
            "dbo__orders",
            "TenantId",
            OrdersTenant1);

        // Multi-column global clause: the excluded column both gates and filters.
        await ExportsFilteredWithoutColumnAsync(
            new ExportOptions {
                TableSelection = ExportTableSelectionMode.Only,
                Tables = ["dbo.Customers"],
                ExcludeColumns = ["dbo.Customers.IsActive"],
                GlobalWhereClauses = [new GlobalWhereClause(["TenantId", "IsActive"], "TenantId = 1 AND IsActive = 1")]
            },
            "dbo__customers",
            "IsActive",
            CustomersTenant1Active);

        // Per-table clause on an excluded column.
        await ExportsFilteredWithoutColumnAsync(
            new ExportOptions {
                TableSelection = ExportTableSelectionMode.Only,
                Tables = ["dbo.Orders"],
                ExcludeColumns = ["dbo.Orders.OrderTotal"],
                PerTableWhereClauses = [new PerTableWhereClause("dbo.Orders", "OrderTotal >= 500.00")]
            },
            "dbo__orders",
            "OrderTotal",
            OrdersOver500);

        async Task ExportsFilteredWithoutColumnAsync(ExportOptions options, string dataTable, string excludedColumn, int expectedRows) {
            await using var sqlite = new SqliteTempFileHarness();

            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

            result.RowCount.ShouldBe(expectedRows);
            await using var connection = await sqlite.OpenConnectionAsync();
            (await connection.ScalarIntAsync($"SELECT COUNT(*) FROM {dataTable}")).ShouldBe(expectedRows);
            (await connection.TableColumnExistsAsync(dataTable, excludedColumn)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Export_ClauseThatMissesMostTables_ExportsThemWholeAndWarnsForEachOne() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            // Whole database: two tables carry TenantId, eight do not.
            ExcludeColumns = ["dbo.CustomerProfiles.LegacyFlags"],
            GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(10);

        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Customers", CustomersTenant1);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Orders", OrdersTenant1);
        // Everything else fails open and ships whole, including the child tables of the rows the filter
        // just removed.
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.OrderLines", OrderLinesAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.CustomerDocuments", CustomerDocumentsAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.CustomerProfiles", CustomerProfilesAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.GlobalSettings", GlobalSettingsAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Countries", CountriesAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "dbo.Currencies", CurrenciesAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "tenant.Customers", TenantCustomersAll);
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, "tenant.Partners", TenantPartnersAll);

        var packageWarnings = await SqlitePackageAssertions.ReadWarningsAsync(connection);
        var filterEvidence = result.Warnings
            .Where(w => w.Contains("WHERE", StringComparison.OrdinalIgnoreCase) || w.Contains("unfiltered", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // The fix: one warning per table missing the gating column, naming both the clause and the table,
        // and it has to reach the package too, not just the in-memory result.
        var expectedTables = new[] { "dbo.Countries", "dbo.Currencies", "dbo.CustomerDocuments", "dbo.CustomerProfiles", "dbo.GlobalSettings", "dbo.OrderLines", "tenant.Customers", "tenant.Partners" };
        filterEvidence.Length.ShouldBe(expectedTables.Length, $"Warnings mentioning filtering: {string.Join(" | ", filterEvidence)}");
        foreach (var table in expectedTables) {
            filterEvidence.ShouldContain(w => w.Contains("TenantId = 1") && w.Contains($"'{table}'") && w.Contains("unfiltered"));
            packageWarnings.ShouldContain(w => w.Contains("TenantId = 1") && w.Contains($"'{table}'") && w.Contains("unfiltered"));
        }
    }

    [Fact]
    public async Task Export_GlobalWhereClause_WarnsForEachTableMissingTheGatingColumn() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            // Customers and Orders both carry TenantId; GlobalSettings carries neither gating column, so
            // it is the table that should export unfiltered and get a warning about it.
            Tables = ["dbo.Customers", "dbo.Orders", "dbo.GlobalSettings"],
            GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.Warnings.ShouldContain(w => w.Contains("TenantId = 1") && w.Contains("dbo.GlobalSettings") && w.Contains("unfiltered"));

        // The warning has to survive into the package, not just the result.
        await using var connection = await sqlite.OpenConnectionAsync();
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM zsdp_warnings WHERE warning_text LIKE '%unfiltered%'")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Export_FilterPredicate_EstimateAndActualSelectAgree() {
        await using var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Customers", "dbo.Orders"],
            GlobalWhereClauses = [new GlobalWhereClause("TenantId", "TenantId = 1")]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        // Counted from the source with the same predicate, independently of the export.
        var sourceCustomers = await db.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers WHERE TenantId = 1");
        var sourceOrders = await db.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders WHERE TenantId = 1");
        result.RowCount.ShouldBe(sourceCustomers + sourceOrders);

        await using var connection = await sqlite.OpenConnectionAsync();
        await AssertRowCountsAgreeAsync(connection, "dbo.Customers", "dbo__customers", sourceCustomers);
        await AssertRowCountsAgreeAsync(connection, "dbo.Orders", "dbo__orders", sourceOrders);
    }

    /// <summary>
    /// The recorded estimate, the recorded actual, the rows really in the package, and the source rows
    /// matching the predicate all have to be the same number: import reconciles against the recorded count,
    /// so an estimate built from a separately-constructed predicate fails every filtered import.
    /// </summary>
    private static async Task AssertRowCountsAgreeAsync(SqliteConnection connection, string fullName, string dataTable, int sourceRows) {
        int exported;
        int estimated;
        await using (var command = connection.CreateCommand()) {
            command.CommandText = """
                                  SELECT s.exported_row_count, s.estimated_source_row_count
                                  FROM zsdp_table_stats s
                                  INNER JOIN zsdp_tables t ON t.id = s.table_id
                                  WHERE t.source_schema || '.' || t.source_table = $name
                                  """;
            command.Parameters.AddWithValue("$name", fullName);

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue($"Expected table stats for '{fullName}'.");
            exported = reader.GetInt32(0);
            estimated = reader.GetInt32(1);
        }

        exported.ShouldBe(sourceRows, $"{fullName}: exported_row_count");
        estimated.ShouldBe(sourceRows, $"{fullName}: estimated_source_row_count");
        (await connection.ScalarIntAsync($"SELECT COUNT(*) FROM {dataTable}")).ShouldBe(sourceRows);
    }
}
