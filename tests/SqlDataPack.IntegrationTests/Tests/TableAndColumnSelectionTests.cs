using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Which tables and columns end up in the package, and which are deliberately left out.
/// <para>
/// Source is core-commerce throughout: four tables sharing a "Customer" prefix across two schemas, an FK
/// chain that forces a non-alphabetical import order, a non-persisted computed column, a sql_variant column,
/// and the dbo.sysdiagrams trap.
/// </para>
/// <para>
/// A left-out table is asserted three ways -- absent from zsdp_tables, carrying an exclusion audit record,
/// and with no SQLite data table created at all. The third one is the point: an empty-but-present data table
/// reads as "the source table had no rows" to every consumer downstream.
/// </para>
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class TableAndColumnSelectionTests {
    private const string SourceFixture = "core-commerce.sql";

    /// <summary>
    /// dbo.CustomerProfiles.LegacyFlags is sql_variant, which the exporter refuses to package. Any scope that
    /// still contains that table needs this exclusion or the export fails on the type before anything else.
    /// </summary>
    private const string UnsupportedColumn = "dbo.CustomerProfiles.LegacyFlags";

    /// <summary>core-commerce has 11 user tables; dbo.sysdiagrams is dropped by default, leaving 10.</summary>
    private const int TablesWithoutDiagrams = 10;

    /// <summary>Rows core-commerce seeds into dbo.OrderLines: 1-3 lines per order over 500 orders.</summary>
    private const int OrderLinesRows = 1001;

    /// <summary>
    /// The diagram-exclusion warning's own wording. Matching on "dbo.sysdiagrams" alone would also hit the
    /// adaptive-batching warning the table earns once it is actually exported.
    /// </summary>
    private const string DiagramExclusionWarning = "Excluded SSMS database diagram table 'dbo.sysdiagrams'";

    private readonly SqlServerContainerFixture _fixture;

    public TableAndColumnSelectionTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_TableSelection_Only_MatchingPatterns_SelectsExpectedTablesAndPreservesImportOrder() {
        await using var db = await CreateSourceAsync();

        // A bare name is schema-agnostic, so it takes the same table name out of both schemas.
        await using (var sqlite = new SqliteTempFileHarness()) {
            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, Only("Customers"));

            result.TableCount.ShouldBe(2);

            await using var connection = await sqlite.OpenConnectionAsync();
            await SqlitePackageAssertions.HasExportedTablesAsync(connection, "dbo.Customers", "tenant.Customers");
        }

        // Qualifying the same name narrows it to one schema.
        await using (var sqlite = new SqliteTempFileHarness()) {
            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, Only("tenant.Customers"));

            result.TableCount.ShouldBe(1);
            result.RowCount.ShouldBe(2);

            await using var connection = await sqlite.OpenConnectionAsync();
            await SqlitePackageAssertions.HasExportedTablesAsync(connection, "tenant.Customers");
            (await connection.TableExistsAsync("dbo__customers")).ShouldBeFalse();
        }

        // Wildcard over the shared prefix, plus the FK ordering across the three tables it selects.
        await using (var sqlite = new SqliteTempFileHarness()) {
            var options = Only("dbo.Customer*");
            options.ExcludeColumns = [UnsupportedColumn];

            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

            result.TableCount.ShouldBe(3);

            await using var connection = await sqlite.OpenConnectionAsync();
            await SqlitePackageAssertions.HasExportedTablesAsync(connection, "dbo.CustomerDocuments", "dbo.CustomerProfiles", "dbo.Customers");

            // Customers sorts last of the three; the FK from both children is what pulls it to the front.
            await SqlitePackageAssertions.HasImportPlanAsync(connection, "dbo.Customers", "dbo.CustomerDocuments", "dbo.CustomerProfiles");

            foreach (var unselected in new[] { "dbo__countries", "dbo__orders", "dbo__orderlines", "tenant__customers" }) {
                (await connection.TableExistsAsync(unselected)).ShouldBeFalse($"'{unselected}' was never selected, so it should not exist even empty.");
            }
        }

        // Patterns match case-insensitively, so a lowercase spelling still finds a mixed-case table.
        await using (var sqlite = new SqliteTempFileHarness()) {
            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, Only("dbo.orderlines"));

            result.TableCount.ShouldBe(1);
            result.RowCount.ShouldBe(OrderLinesRows);

            await using var connection = await sqlite.OpenConnectionAsync();
            await SqlitePackageAssertions.HasExportedTablesAsync(connection, "dbo.OrderLines");
        }
    }

    [Fact]
    public async Task Export_TableSelection_Only_UnmatchedPattern_FailsBeforeCreatingPackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, Only("dbo.Cusomters")));

        exception.Message.ShouldContain("Table pattern 'dbo.Cusomters' did not match any user table");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    /// <summary>
    /// Only is strict and AllExcept is lenient, deliberately: one exclude list is meant to be pointed at
    /// several environments where not every table it names exists.
    /// </summary>
    [Fact]
    public async Task Export_TableSelection_AllExcept_UnmatchedPattern_WarnsAndExports() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.AllExcept,
            Tables = ["dbo.Cusomters"],
            ExcludeColumns = [UnsupportedColumn]
        };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(TablesWithoutDiagrams);
        result.Warnings.ShouldContain(w => w.Contains("dbo.Cusomters", StringComparison.Ordinal) && w.Contains("did not match", StringComparison.Ordinal));

        // In the package too, not just the in-process result: whoever reads the package later is the one who
        // needs to find out the exclude list has a typo in it.
        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasWarningMatchingAsync(connection, "dbo.Cusomters");
    }

    [Fact]
    public async Task Export_TableSelection_AllExcept_RemovesTablesAndRecordsSkippedRecords() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        // One exact name and one wildcard in the same list. The wildcard takes CustomerProfiles out of scope,
        // which is why this export needs no sql_variant column exclusion.
        var options = new ExportOptions { Tables = ["dbo.GlobalSettings", "dbo.Customer*"] };

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(6);

        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasExportedTablesAsync(connection, "dbo.Countries", "dbo.Currencies", "dbo.OrderLines", "dbo.Orders", "tenant.Customers", "tenant.Partners");

        (string Source, string DataTable)[] removed = [
            ("dbo.GlobalSettings", "dbo__globalsettings"),
            ("dbo.Customers", "dbo__customers"),
            ("dbo.CustomerDocuments", "dbo__customerdocuments"),
            ("dbo.CustomerProfiles", "dbo__customerprofiles")
        ];

        foreach (var (source, dataTable) in removed) {
            await SqlitePackageAssertions.HasExclusionAsync(connection, "table", source);
            (await connection.TableExistsAsync(dataTable)).ShouldBeFalse($"'{source}' was excluded, so '{dataTable}' should never have been created.");
        }
    }

    [Fact]
    public async Task Export_TableSelection_AllExcept_ExcludingEveryTable_FailsBeforeCreatingPackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions { Tables = ["*"] };

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("No tables are selected for export");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_ColumnExclusion_ExcludedColumn_IsAbsentFromDataAndRecorded() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.CustomerProfiles");
        options.ExcludeColumns = ["dbo.CustomerProfiles.Nickname", UnsupportedColumn];

        var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

        // Dropping a column drops no rows.
        result.RowCount.ShouldBe(4);

        await using var connection = await sqlite.OpenConnectionAsync();
        (await connection.TableColumnExistsAsync("dbo__customerprofiles", "Nickname")).ShouldBeFalse();
        (await connection.TableColumnExistsAsync("dbo__customerprofiles", "DisplayName")).ShouldBeTrue();
        (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__customerprofiles")).ShouldBe(4);

        // The column is still described in the metadata and flagged excluded, so a consumer can tell
        // "stripped on purpose" from "the source never had it".
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.CustomerProfiles", "Nickname", isExcluded: true);
        await SqlitePackageAssertions.HasExclusionAsync(connection, "column", "dbo.CustomerProfiles.Nickname");
    }

    [Fact]
    public async Task Export_ColumnExclusion_NonExistentColumn_FailsBeforeCreatingPackage() {
        await using var db = await CreateSourceAsync();
        await using var sqlite = new SqliteTempFileHarness();
        var options = Only("dbo.CustomerProfiles");
        options.ExcludeColumns = ["dbo.CustomerProfiles.Nicknam", UnsupportedColumn];

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options));

        exception.Message.ShouldContain("Excluded column 'dbo.CustomerProfiles.Nicknam' does not exist");
        File.Exists(sqlite.FilePath).ShouldBeFalse();
    }

    /// <summary>
    /// Exclusion is applied before the type check, which is the only thing that makes a table carrying an
    /// unsupported column exportable at all.
    /// </summary>
    [Fact]
    public async Task Export_ColumnExclusion_ExcludingUnsupportedColumn_MakesTableExportable() {
        await using var db = await CreateSourceAsync();

        await using (var sqlite = new SqliteTempFileHarness()) {
            var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, Only("dbo.CustomerProfiles")));

            exception.Message.ShouldContain($"Unsupported included type 'sql_variant' on {UnsupportedColumn}");
            File.Exists(sqlite.FilePath).ShouldBeFalse();
        }

        await using (var sqlite = new SqliteTempFileHarness()) {
            var options = Only("dbo.CustomerProfiles");
            options.ExcludeColumns = [UnsupportedColumn];

            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

            result.TableCount.ShouldBe(1);
            result.RowCount.ShouldBe(4);

            await using var connection = await sqlite.OpenConnectionAsync();
            (await connection.TableColumnExistsAsync("dbo__customerprofiles", "LegacyFlags")).ShouldBeFalse();
            await SqlitePackageAssertions.HasExclusionAsync(connection, "column", UnsupportedColumn);
        }
    }

    /// <summary>
    /// Computed columns are excluded without being asked for: SqlBulkCopy rejects a write to one outright, so
    /// carrying the values would break every import of the table.
    /// </summary>
    [Fact]
    public async Task Export_AutomaticExclusion_ComputedColumn_IsAutoExcludedAndRecomputedOnImport() {
        await using var source = await CreateSourceAsync();
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, SourceFixture);
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions { ExcludeColumns = [UnsupportedColumn] };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        await using (var connection = await sqlite.OpenConnectionAsync()) {
            (await connection.TableColumnExistsAsync("dbo__orderlines", "ExtendedPrice")).ShouldBeFalse();
            (await connection.TableColumnExistsAsync("dbo__orderlines", "Qty")).ShouldBeTrue();

            // is_computed, not is_excluded: the caller never asked for this one.
            await SqlitePackageAssertions.HasColumnMetadataAsync(connection, "dbo.OrderLines", "ExtendedPrice", isComputed: true, isExcluded: false);
            await SqlitePackageAssertions.HasExclusionAsync(connection, "column", "dbo.OrderLines.ExtendedPrice");
        }

        await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        const string totals = "SELECT CONCAT(COUNT(*), '|', SUM(Qty), '|', SUM(UnitPrice), '|', SUM(ExtendedPrice)) FROM dbo.OrderLines";

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.OrderLines")).ShouldBe(OrderLinesRows);

        // ExtendedPrice never travelled in the package; the target computes it from the Qty and UnitPrice
        // that did. Matching the source total is what proves those two arrived intact.
        (await target.ScalarStringAsync(totals)).ShouldBe(await source.ScalarStringAsync(totals));
    }

    /// <summary>
    /// dbo.sysdiagrams is a regular user table (is_ms_shipped = 0), so nothing else filters it out. Excluded
    /// by name by default, kept when the caller says so.
    /// </summary>
    [Fact]
    public async Task Export_AutomaticExclusion_SysDiagrams_IsExcludedByDefaultAndIncludedOnOptOut() {
        await using var db = await CreateSourceAsync();

        await using (var sqlite = new SqliteTempFileHarness()) {
            var options = new ExportOptions { ExcludeColumns = [UnsupportedColumn] };

            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

            result.TableCount.ShouldBe(TablesWithoutDiagrams);

            await using var connection = await sqlite.OpenConnectionAsync();
            (await ExportedDiagramTableCountAsync(connection)).ShouldBe(0);
            (await connection.TableExistsAsync("dbo__sysdiagrams")).ShouldBeFalse();
            await SqlitePackageAssertions.HasExclusionAsync(connection, "table", "dbo.sysdiagrams");
            await SqlitePackageAssertions.HasWarningMatchingAsync(connection, DiagramExclusionWarning);
        }

        await using (var sqlite = new SqliteTempFileHarness()) {
            var options = new ExportOptions {
                ExcludeColumns = [UnsupportedColumn],
                ExcludeSsmsDiagrams = false
            };

            var result = await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, options);

            result.TableCount.ShouldBe(TablesWithoutDiagrams + 1);

            await using var connection = await sqlite.OpenConnectionAsync();
            (await ExportedDiagramTableCountAsync(connection)).ShouldBe(1);
            (await connection.ScalarIntAsync("SELECT COUNT(*) FROM dbo__sysdiagrams")).ShouldBe(1);
            await SqlitePackageAssertions.HasWarningMatchingAsync(connection, DiagramExclusionWarning, expectedCount: 0);
        }
    }

    private async Task<SqlServerFixtureDatabase> CreateSourceAsync() {
        var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        return db;
    }

    private static ExportOptions Only(params string[] patterns) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = patterns
        };
    }

    private static async Task<int> ExportedDiagramTableCountAsync(SqliteConnection connection) {
        return await connection.ScalarIntAsync("SELECT COUNT(*) FROM zsdp_tables WHERE source_schema = 'dbo' AND source_table = 'sysdiagrams'");
    }
}
