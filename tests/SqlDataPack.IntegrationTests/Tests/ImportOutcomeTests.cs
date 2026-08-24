using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// What the target database actually looks like after a copy runs -- including a copy that fails partway.
/// Every test here covers something the library reports as a success, or reports as a failure while having
/// already changed the target.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class ImportOutcomeTests {
    private const string CoreCommerce = "core-commerce.sql";

    /// <summary>The dbo.CustomerDocuments columns the DefaultedNullables target variant puts a DEFAULT on. KeepNulls means the DEFAULT never fires on import.</summary>
    private static readonly string[] DefaultedDocumentColumns = ["Label", "PageCount", "Amount", "IsVerified"];

    /// <summary>The nullable columns of the same table that the variant leaves without a DEFAULT.</summary>
    private static readonly string[] UndefaultedDocumentColumns = ["IssuedAt", "ScanBytes", "ExternalRef"];

    private readonly SqlServerContainerFixture _fixture;

    public ImportOutcomeTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    /// <summary>
    /// <c>SqlBulkCopy</c> runs with <c>KeepNulls</c>, so a source NULL lands as NULL in the target even where
    /// the target column has a DEFAULT -- the DEFAULT never fires on import. Covers both the columns the
    /// DefaultedNullables variant puts a DEFAULT on and the columns it leaves without one, so a regression
    /// that reintroduces DEFAULT fallthrough shows up regardless of which set it hits.
    /// </summary>
    [Fact]
    public async Task Import_NullSource_LandsAsTargetNull() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplyTargetVariantAsync(target, CoreCommerce, null, TargetSchemaScripts.Variants.DefaultedNullables);
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions { TableSelection = ExportTableSelectionMode.Only, Tables = ["dbo.CustomerDocuments"] };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        // Everything the caller gets back looks clean: every row arrived and nothing mentions loss.
        result.RowCount.ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.CustomerDocuments"));
        result.Warnings.ShouldNotContain(warning => warning.Contains("default", StringComparison.OrdinalIgnoreCase));

        // Per column, so a failure names the column rather than dumping ten rows. Source NULLs stay NULL in
        // the target regardless of whether the column has a DEFAULT.
        foreach (var column in DefaultedDocumentColumns.Concat(UndefaultedDocumentColumns)) {
            var countNulls = $"SELECT COUNT(*) FROM dbo.CustomerDocuments WHERE {column} IS NULL";
            var sourceNulls = await source.ScalarIntAsync(countNulls);

            sourceNulls.ShouldBeGreaterThan(0, $"the fixture no longer seeds NULLs in '{column}', so this proves nothing");
            (await target.ScalarIntAsync(countNulls)).ShouldBe(sourceNulls, $"'{column}' does not hold the same NULLs as the source -- KeepNulls may have been dropped, in which case this test states the wrong behaviour");
        }

        // The fully-populated row survives unchanged: nothing here depends on NULL handling.
        const string populatedRow = """
                                    SELECT CONCAT_WS('|',
                                               Label,
                                               PageCount,
                                               CONVERT(varchar(40), Amount),
                                               CONVERT(varchar(40), IssuedAt, 121),
                                               CONVERT(varchar(40), ScanBytes, 1),
                                               CONVERT(varchar(40), ExternalRef),
                                               CONVERT(varchar(1), IsVerified))
                                    FROM dbo.CustomerDocuments
                                    WHERE Label = N'Signed Contract'
                                    """;
        (await target.ScalarStringAsync(populatedRow)).ShouldBe(await source.ScalarStringAsync(populatedRow));
    }

    /// <summary>
    /// Deleting rows from a package is the documented edit workflow, so the default import takes the package
    /// as it stands and says what moved rather than refusing it.
    /// </summary>
    [Fact]
    public async Task Import_RowsDeletedFromPackage_ImportsWhatRemainsAndWarns() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerce);
        await using var sqlite = new SqliteTempFileHarness();
        var exportedRows = await ExportThenDeleteOneRowAsync(source, sqlite);

        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(exportedRows - 1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.GlobalSettings")).ShouldBe(exportedRows - 1);
        result.Warnings.ShouldContain(w => w.Contains("dbo.GlobalSettings", StringComparison.Ordinal)
                                           && w.Contains($"holds {exportedRows - 1} rows", StringComparison.Ordinal)
                                           && w.Contains($"recorded {exportedRows}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The strict mode an unattended scrub pipeline sets, where a count that moved means a script bug. It has
    /// to reject before writing rather than partway through, so the target stays usable and the run is
    /// retryable -- which is exactly what the old in-loop check could not do.
    /// </summary>
    [Fact]
    public async Task Import_RowsDeletedFromPackage_WithFail_RefusesBeforeWritingAnything() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerce);
        await using var sqlite = new SqliteTempFileHarness();
        var exportedRows = await ExportThenDeleteOneRowAsync(source, sqlite);

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter()
            .ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { RowCountDrift = RowCountDrift.Fail }));

        exception.Message.ShouldContain("dbo.GlobalSettings");
        exception.Message.ShouldContain($"holds {exportedRows - 1} rows");
        exception.Message.ShouldContain($"recorded {exportedRows}");

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.GlobalSettings")).ShouldBe(0);
    }

    /// <summary>
    /// Preflight exists to answer "will this import work" without writing anything, so it has to reach the
    /// same verdict the import will. Before this it called an edited package valid and the import then
    /// refused it, which is the contradiction that made a half-loaded target so easy to hit.
    /// </summary>
    [Fact]
    public async Task Preflight_RowsDeletedFromPackage_WarnsByDefaultAndErrorsUnderFail() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerce);
        await using var sqlite = new SqliteTempFileHarness();
        var exportedRows = await ExportThenDeleteOneRowAsync(source, sqlite);

        var warnPreflight = await new SqlDataPackImporter().PreflightAsync(sqlite.FilePath, target.ConnectionString);

        warnPreflight.IsValid.ShouldBeTrue();
        warnPreflight.Errors.ShouldBeEmpty();
        warnPreflight.Warnings.ShouldContain(w => w.Contains("dbo.GlobalSettings", StringComparison.Ordinal)
                                                  && w.Contains($"holds {exportedRows - 1} rows", StringComparison.Ordinal));

        var failPreflight = await new SqlDataPackImporter().PreflightAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { RowCountDrift = RowCountDrift.Fail });

        failPreflight.IsValid.ShouldBeFalse();
        failPreflight.Errors.ShouldContain(e => e.Contains("dbo.GlobalSettings", StringComparison.Ordinal));
        failPreflight.Warnings.ShouldNotContain(w => w.Contains($"holds {exportedRows - 1} rows", StringComparison.Ordinal));
    }

    /// <summary>
    /// Exports one table, deletes its first row from the package, and returns the count the export recorded.
    /// The package then holds one row fewer than its manifest claims, which is what the documented edit
    /// workflow produces. Resolves the data table from the manifest because DataTablePrefix is configurable.
    /// </summary>
    private static async Task<int> ExportThenDeleteOneRowAsync(SqlServerFixtureDatabase source, SqliteTempFileHarness sqlite) {
        var options = new ExportOptions { TableSelection = ExportTableSelectionMode.Only, Tables = ["dbo.GlobalSettings"] };
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
        var exportedRows = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.GlobalSettings");

        await using (var package = await sqlite.OpenConnectionAsync()) {
            var dataTable = await package.ScalarStringAsync("SELECT sqlite_table FROM zsdp_tables WHERE source_schema = 'dbo' AND source_table = 'GlobalSettings'");
            await package.ExecuteSqlAsync($"""DELETE FROM "{dataTable}" WHERE rowid = (SELECT MIN(rowid) FROM "{dataTable}")""");
        }

        return exportedRows;
    }

    /// <summary>
    /// Bulk copy runs without <c>SqlBulkCopyOptions.CheckConstraints</c>, so a row violating a target CHECK
    /// or FK loads without error during the copy itself. The post-load re-check (see
    /// <c>SqlDataPackImporter.CheckConstraintsAsync</c>) then catches it and throws instead of reporting
    /// success: rows already landed in the target, but the caller finds out the target rejected them.
    /// </summary>
    [Fact]
    public async Task Import_ConstraintsOnTarget_ThrowsAfterLoadingRows() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerce);
        await using var sqlite = new SqliteTempFileHarness();

        // Orders alone: dbo.Customers stays empty in the target, so every copied row violates
        // FK_Orders_Customers without any tampering.
        var options = new ExportOptions { TableSelection = ExportTableSelectionMode.Only, Tables = ["dbo.Orders"] };
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
        var exportedRows = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders");
        await using (var package = await sqlite.OpenConnectionAsync()) {
            // Decimal columns are stored as TEXT in the package (ValueConverter), hence the quoted value.
            var dataTable = await package.ScalarStringAsync("SELECT sqlite_table FROM zsdp_tables WHERE source_schema = 'dbo' AND source_table = 'Orders'");
            await package.ExecuteSqlAsync($"""UPDATE "{dataTable}" SET OrderTotal = '-1.00' WHERE OrderId = (SELECT MIN(OrderId) FROM "{dataTable}")""");
        }

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        exception.Message.ShouldContain("dbo.Orders");
        exception.Message.ShouldContain("CK_Orders_OrderTotal");

        // The bulk copy already committed the rows before the re-check ran, so the bad row is still there.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders")).ShouldBe(exportedRows);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders WHERE OrderTotal < 0")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT CONVERT(int, is_not_trusted) FROM sys.check_constraints WHERE name = 'CK_Orders_OrderTotal'")).ShouldBe(1);
    }

    /// <summary>
    /// v1_todo: there is no import-wide transaction. A table that fails mid-copy leaves every table already
    /// copied committed, and the empty-target precondition then blocks the retry -- which is the part that
    /// actually strands the caller.
    /// </summary>
    [Fact]
    public async Task Import_FailurePartwayThrough_LeavesEarlierTablesCommitted() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerce));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplyTargetVariantAsync(target, CoreCommerce, null, TargetSchemaScripts.Variants.ThirdTableIncompatible);
        await using var sqlite = new SqliteTempFileHarness();

        // FK dependencies fix the import order: Customers, then Orders, then OrderLines.
        var options = new ExportOptions { TableSelection = ExportTableSelectionMode.Only, Tables = ["dbo.Customers", "dbo.Orders", "dbo.OrderLines"] };
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
        var sourceCustomers = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers");
        var sourceOrders = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders");
        var sourceOrderLines = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.OrderLines");
        var importer = new SqlDataPackImporter();

        var exception = await Should.ThrowAsync<Exception>(() => importer.ImportAsync(sqlite.FilePath, target.ConnectionString));

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers")).ShouldBe(sourceCustomers);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders")).ShouldBe(sourceOrders);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.OrderLines")).ShouldBeLessThan(sourceOrderLines);

        // Pinning the gap, not endorsing it: the copy failure surfaces raw from SqlBulkCopy, so it is not a
        // SqlDataPack diagnostic and it names neither of the two tables that are now populated.
        exception.ShouldNotBeOfType<SqlDataPackException>();
        exception.Message.ShouldNotContain("dbo.Customers");
        exception.Message.ShouldNotContain("dbo.Orders");

        // The actual user harm: nothing was rolled back, so the retry cannot get past validation either.
        var retry = await Should.ThrowAsync<SqlDataPackException>(() => importer.ImportAsync(sqlite.FilePath, target.ConnectionString));
        retry.Message.ShouldContain("Target table 'dbo.Customers' must be empty");
    }

    /// <summary>
    /// A NULL in a native json column used to truncate the bulk copy silently (see type-vault.sql's
    /// DocumentPayloads): SqlBulkCopy returned normally with fewer rows landed than the package holds. This
    /// pins the outcome the caller must never see: success reported alongside a target row count short of
    /// the source.
    /// </summary>
    [Fact]
    public async Task Import_XmlColumnWithNullPayload_DoesNotReportSuccessWithMissingRows() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript("type-vault.sql"));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, "type-vault.sql");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.DocumentPayloads"));

        var sourceRows = await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DocumentPayloads");

        // Reporting success with fewer rows in the target than the package holds is the one
        // outcome this forbids.
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        var targetRows = await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DocumentPayloads");
        targetRows.ShouldBe(sourceRows, "the import reported success but the target has fewer rows than the source.");
        result.RowCount.ShouldBe(sourceRows);
    }

    /// <summary>
    /// Pins the batch-size-1 workaround in <c>SqlDataPackImporter.ImportAsync</c> directly, rather than
    /// relying on the round-trip tests to prove it indirectly. Row 2 carries the NULL and row 3 follows it in
    /// the same batch -- the shape that truncates without the override.
    /// </summary>
    [SkippableFact]
    public async Task Import_NullableJsonColumn_WarnsThatRowsAreBatchedOneAtATime() {
        Requires.SqlServer2025(_fixture);

        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Docs (Id INT NOT NULL PRIMARY KEY, Payload JSON NULL); INSERT INTO dbo.Docs VALUES (1, '{\"a\":1}'), (2, NULL), (3, '{\"b\":2}');");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Docs (Id INT NOT NULL PRIMARY KEY, Payload JSON NULL);");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Docs"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Docs")).ShouldBe(3);
        result.Warnings.ShouldContain(w => w.Contains("dbo.Docs") && w.Contains("nullable json"));
    }

    [Fact]
    public async Task Import_CleanLoad_LeavesConstraintsTrusted() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Scores (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL CONSTRAINT CK_Scores_Value CHECK (Value BETWEEN 0 AND 100)); INSERT INTO dbo.Scores VALUES (1, 50);");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Scores (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL CONSTRAINT CK_Scores_Value CHECK (Value BETWEEN 0 AND 100));");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Scores"));
        await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Scores_Value' AND is_not_trusted = 1")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RowsViolatingATargetCheckConstraint_Throws() {
        // The source has no constraint, so the rows export cleanly. The target has one they violate.
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Scores (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL); INSERT INTO dbo.Scores VALUES (1, 500);");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Scores (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL CONSTRAINT CK_Scores_Value CHECK (Value BETWEEN 0 AND 100));");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Scores"));

        var exception = await Should.ThrowAsync<SqlDataPackException>(async () => await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        exception.Message.ShouldContain("dbo.Scores");
        exception.Message.ShouldContain("CK_Scores_Value");
    }

    /// <summary>
    /// Subset import whose FK parent lives outside the package is supported: the target already holds the
    /// rows the child references, so the re-check finds nothing to complain about and trusts the FK.
    /// </summary>
    [Fact]
    public async Task Import_ChildTableOnly_WithParentAlreadySatisfiedInTarget_Succeeds() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Parents (Id INT NOT NULL PRIMARY KEY); CREATE TABLE dbo.Children (Id INT NOT NULL PRIMARY KEY, ParentId INT NOT NULL CONSTRAINT FK_Children_Parents FOREIGN KEY REFERENCES dbo.Parents (Id)); INSERT INTO dbo.Parents VALUES (1); INSERT INTO dbo.Children VALUES (1, 1);");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Parents (Id INT NOT NULL PRIMARY KEY); CREATE TABLE dbo.Children (Id INT NOT NULL PRIMARY KEY, ParentId INT NOT NULL CONSTRAINT FK_Children_Parents FOREIGN KEY REFERENCES dbo.Parents (Id)); INSERT INTO dbo.Parents VALUES (1);");
        await using var sqlite = new SqliteTempFileHarness();

        // Only dbo.Children is exported; dbo.Parents never enters the package.
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Children"));
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(1);
        (await target.ScalarIntAsync("SELECT CONVERT(int, is_not_trusted) FROM sys.foreign_keys WHERE name = 'FK_Children_Parents'")).ShouldBe(0);
    }

    /// <summary>
    /// The mirror case: a subset import whose FK parent lives outside the package, where the target's
    /// existing data does not satisfy the constraint. The parent table is not this import's responsibility --
    /// the caller may be populating it separately -- so the import succeeds with a warning that names the
    /// constraint and the out-of-package table, rather than failing an import that never touched that table.
    /// </summary>
    [Fact]
    public async Task Import_ChildTableOnly_WithParentNotSatisfiedInTarget_SucceedsWithWarning() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Parents (Id INT NOT NULL PRIMARY KEY); CREATE TABLE dbo.Children (Id INT NOT NULL PRIMARY KEY, ParentId INT NOT NULL CONSTRAINT FK_Children_Parents FOREIGN KEY REFERENCES dbo.Parents (Id)); INSERT INTO dbo.Parents VALUES (1); INSERT INTO dbo.Children VALUES (1, 1);");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Parents (Id INT NOT NULL PRIMARY KEY); CREATE TABLE dbo.Children (Id INT NOT NULL PRIMARY KEY, ParentId INT NOT NULL CONSTRAINT FK_Children_Parents FOREIGN KEY REFERENCES dbo.Parents (Id));");
        await using var sqlite = new SqliteTempFileHarness();

        // Only dbo.Children is exported; the target's dbo.Parents stays empty, so Children.ParentId = 1
        // references a row that does not exist in the target.
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Children"));

        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(1);
        result.Warnings.ShouldContain(w => w.Contains("dbo.Children") && w.Contains("FK_Children_Parents") && w.Contains("dbo.Parents"));

        // The rows are loaded; the constraint is simply left untrusted rather than blocking the import.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Children")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT CONVERT(int, is_not_trusted) FROM sys.foreign_keys WHERE name = 'FK_Children_Parents'")).ShouldBe(1);
    }

    /// <summary>
    /// The constraint re-check's metadata queries pass the unquoted "schema.table" text to OBJECT_ID, which
    /// returns NULL for a table name containing '.'. Both queries then return zero rows and the table's
    /// constraints are silently never re-checked. Names the table so OBJECT_ID(@table) would resolve wrong
    /// (or not at all) if the queries ever regress to that pattern.
    /// </summary>
    [Fact]
    public async Task Import_TableNameRequiresQuoting_StillReChecksConstraints() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.[Sco.res] (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL); INSERT INTO dbo.[Sco.res] VALUES (1, 500);");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.[Sco.res] (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL CONSTRAINT CK_Scores_Dotted_Value CHECK (Value BETWEEN 0 AND 100));");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Sco.res"));

        var exception = await Should.ThrowAsync<SqlDataPackException>(async () => await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString));

        exception.Message.ShouldContain("Sco.res");
        exception.Message.ShouldContain("CK_Scores_Dotted_Value");
    }

    /// <summary>
    /// A caller who disables an FK before a partial load and re-enables it themselves afterwards is a normal
    /// workflow. Without <c>AND is_disabled = 0</c> in the metadata queries, the re-check enables the
    /// constraint mid-import and then throws when the loaded data does not satisfy it. This pins that a
    /// constraint the caller disabled stays disabled and out of the re-check entirely.
    /// </summary>
    [Fact]
    public async Task Import_TargetConstraintCallerDisabled_LeavesItDisabled() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync("CREATE TABLE dbo.Scores (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL); INSERT INTO dbo.Scores VALUES (1, 500);");
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync("CREATE TABLE dbo.Scores (Id INT NOT NULL PRIMARY KEY, Value INT NOT NULL CONSTRAINT CK_Scores_Value CHECK (Value BETWEEN 0 AND 100));");
        await target.ExecuteSqlAsync("ALTER TABLE dbo.Scores NOCHECK CONSTRAINT CK_Scores_Value;");
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.Scores"));

        // Row violates the disabled constraint; the import must succeed rather than re-enabling and throwing.
        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        result.RowCount.ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Scores")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT CONVERT(int, is_disabled) FROM sys.check_constraints WHERE name = 'CK_Scores_Value'")).ShouldBe(1);
    }

    private static ExportOptions OnlyTable(string fullName) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [fullName]
        };
    }
}
