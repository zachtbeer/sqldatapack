using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// System-versioned tables. Every period assertion goes through TemporalAssertions.DumpSystemVersionedAsync:
/// a FOR SYSTEM_TIME ALL dump with the period columns rendered as raw bytes. Row counts and spot checks
/// cannot tell a preserved ValidFrom from one the engine reassigned on insert; the byte dump can.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class TemporalRoundTripTests {
    private const string Suite = "temporal-suite.sql";

    private readonly SqlServerContainerFixture _fixture;

    public TemporalRoundTripTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task RoundTrip_SystemVersionedPair_PreservesPeriodsToTheTick() {
        await using var source = await SeededSourceAsync();
        await using var target = await UnseededTargetAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await ExportOnlyAsync(source, sqlite, "dbo.Departments", "dbo.DepartmentHistory");
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        // Import replays the package's export warnings, and the export one also says "temporarily suspended",
        // so match only what the importer's own message carries -- otherwise this passes with nothing suspended.
        // The export warning is covered by TemporalSuspendFailureTests.
        importResult.Warnings.ShouldContain(w => w.Contains("Temporal table 'dbo.Departments'") && w.Contains("system versioning is temporarily suspended") && w.Contains("SYSTEM_TIME period dropped") && w.Contains("dbo.DepartmentHistory"));
        importResult.TableCount.ShouldBe(2);
        importResult.RowCount.ShouldBe(5);

        // History rows must land in the history table, not get replayed into current.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Departments")).ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Departments"));
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DepartmentHistory")).ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DepartmentHistory"));

        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Departments")).ShouldBeTrue();
        (await TemporalAssertions.ReadHistoryTableNameAsync(target, "dbo.Departments")).ShouldBe("dbo.DepartmentHistory");

        var targetDump = await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Departments", "ValidFrom", "ValidTo");
        targetDump.ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Departments", "ValidFrom", "ValidTo"));
    }

    [Fact]
    public async Task RoundTrip_ViaDacpacSchemaDeploy_PreservesPeriodsToTheTick() {
        await using var source = await SeededSourceAsync();
        // No target schema: DacFx has to recreate the period and the history binding from the captured dacpac.
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Departments", "dbo.DepartmentHistory"],
            SchemaCaptureMode = SchemaCaptureMode.Dacpac,
            CommandTimeout = 120
        });
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, new ImportOptions { SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac });

        importResult.RowCount.ShouldBe(5);
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Departments")).ShouldBeTrue();
        (await TemporalAssertions.ReadHistoryTableNameAsync(target, "dbo.Departments")).ShouldBe("dbo.DepartmentHistory");

        var targetDump = await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Departments", "ValidFrom", "ValidTo");
        targetDump.ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Departments", "ValidFrom", "ValidTo"));
    }

    [Fact]
    public async Task RoundTrip_CustomPeriodNamesAndFiniteRetention_ArePreserved() {
        await using var source = await SeededSourceAsync();
        await using var target = await UnseededTargetAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await ExportOnlyAsync(source, sqlite, "dbo.Subscriptions", "dbo.Subscription_History", "dbo.Departments", "dbo.DepartmentHistory");
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.RowCount.ShouldBe(8);

        var targetDump = await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Subscriptions", "LastUpdateDate", "LastUpdateValidTo");
        targetDump.ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Subscriptions", "LastUpdateDate", "LastUpdateValidTo"));

        var targetPeriod = await TemporalAssertions.ReadPeriodColumnNamesAsync(target, "dbo.Subscriptions");
        targetPeriod.ShouldBe(await TemporalAssertions.ReadPeriodColumnNamesAsync(source, "dbo.Subscriptions"));

        // SET SYSTEM_VERSIONING = OFF resets retention to INFINITE. Without the capture-and-re-apply the
        // retention policy is gone after every import and nothing else notices.
        (await TemporalAssertions.ReadRetentionAsync(target, "dbo.Subscriptions")).ShouldBe((3, "MONTH"));

        // And the pair that never had retention must not pick one up from a stray clause on restore.
        (await TemporalAssertions.ReadRetentionAsync(target, "dbo.Departments")).ShouldBe((-1, "INFINITE"));
    }

    [Fact]
    public async Task RoundTrip_HiddenPeriodColumns_PinsHiddenState() {
        await using var source = await SeededSourceAsync();
        await using var target = await UnseededTargetAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await ExportOnlyAsync(source, sqlite, "dbo.Flags", "dbo.FlagHistory");

        await using (var package = await sqlite.OpenConnectionAsync()) {
            // HIDDEN columns are invisible to SELECT *; export has to find them through sys.columns.
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.Flags", "ValidFrom", typeName: "datetime2", isExcluded: false);
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.Flags", "ValidTo", typeName: "datetime2", isExcluded: false);
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.RowCount.ShouldBe(3);
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Flags")).ShouldBeTrue();

        var targetDump = await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Flags", "ValidFrom", "ValidTo");
        targetDump.ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Flags", "ValidFrom", "ValidTo"));

        // Known gap (v1_todo): the restore re-adds the period without HIDDEN, so the target's period columns
        // come back visible and SELECT * on dbo.Flags starts returning two extra datetime2 columns. Pinned
        // here so fixing it is a deliberate change to this assertion, not a silent one.
        (await TemporalAssertions.IsHiddenAsync(target, "dbo.Flags", "ValidFrom")).ShouldBeFalse();
        (await TemporalAssertions.IsHiddenAsync(target, "dbo.Flags", "ValidTo")).ShouldBeFalse();
    }

    [Fact]
    public async Task RoundTrip_MultipleTemporalPairs_AllSuspendedAndRestored() {
        await using var source = await SeededSourceAsync();
        await using var target = await UnseededTargetAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await ExportOnlyAsync(source, sqlite, "dbo.Regions", "dbo.RegionHistory", "dbo.Teams", "dbo.TeamHistory");
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(4);
        importResult.RowCount.ShouldBe(5);

        // A loop that suspends or restores only the first pair passes every single-pair test and fails here.
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Regions")).ShouldBeTrue();
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Teams")).ShouldBeTrue();

        (await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Regions", "ValidFrom", "ValidTo"))
            .ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Regions", "ValidFrom", "ValidTo"));
        (await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Teams", "ValidFrom", "ValidTo"))
            .ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Teams", "ValidFrom", "ValidTo"));
    }

    [Fact]
    public async Task RoundTrip_TemporalChildWithForeignKeyToNonTemporalParent() {
        await using var source = await SeededSourceAsync();
        await using var target = await UnseededTargetAsync();
        await using var sqlite = new SqliteTempFileHarness();

        await ExportOnlyAsync(source, sqlite, "dbo.Offices", "dbo.Workers", "dbo.WorkerHistory");

        List<string> plan;
        await using (var package = await sqlite.OpenConnectionAsync()) {
            plan = (await package.ReadStringsAsync("SELECT source_schema || '.' || source_table FROM zsdp_import_plan ORDER BY sequence")).ToList();
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        plan.IndexOf("dbo.Offices").ShouldBeLessThan(plan.IndexOf("dbo.Workers"));
        importResult.RowCount.ShouldBe(5);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Offices")).ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Offices"));

        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Workers")).ShouldBeTrue();
        (await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Workers", "ValidFrom", "ValidTo"))
            .ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Workers", "ValidFrom", "ValidTo"));

        // The suspend/restore ALTER ceremony must not drop an FK it never recreates.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_Workers_Offices'")).ShouldBe(1);
    }

    [Fact]
    public async Task RoundTrip_ScopeVariants_SuspendOnlyWhenNeeded() {
        await using var source = await SeededSourceAsync();

        // (a) Current and history both in scope: suspend, drop the period, reload both, restore.
        {
            await using var target = await UnseededTargetAsync();
            await using var sqlite = new SqliteTempFileHarness();

            await ExportOnlyAsync(source, sqlite, "dbo.Departments", "dbo.DepartmentHistory");
            var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

            result.RowCount.ShouldBe(5);
            (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Departments")).ShouldBeTrue();
            (await TemporalAssertions.DumpSystemVersionedAsync(target, "dbo.Departments", "ValidFrom", "ValidTo"))
                .ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, "dbo.Departments", "ValidFrom", "ValidTo"));
        }

        // (b) Current table only. Still has to end up system-versioned: a ResolveSuspensions that suspends
        // whenever a temporal table is merely in scope, and forgets to restore, fails right here.
        {
            await using var target = await UnseededTargetAsync();
            await using var sqlite = new SqliteTempFileHarness();

            await ExportOnlyAsync(source, sqlite, "dbo.Departments");
            var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

            result.TableCount.ShouldBe(1);
            result.RowCount.ShouldBe(2);
            (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Departments")).ShouldBe(2);
            (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DepartmentHistory")).ShouldBe(0);
            (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Departments")).ShouldBeTrue();
        }

        // (c) History table only: the opposite branch, and a restore over a suspended window with zero
        // rows written to the current table.
        {
            await using var target = await UnseededTargetAsync();
            await using var sqlite = new SqliteTempFileHarness();

            await ExportOnlyAsync(source, sqlite, "dbo.DepartmentHistory");
            var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

            result.TableCount.ShouldBe(1);
            result.RowCount.ShouldBe(3);
            (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Departments")).ShouldBe(0);
            (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DepartmentHistory")).ShouldBe(3);
            (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Departments")).ShouldBeTrue();
        }

        // (d) Plain non-temporal source into a live system-versioned target. Nothing may be suspended and
        // the SYSTEM_TIME period must survive untouched, because dropping it here strips versioning off a
        // table nobody asked to change.
        {
            await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
            await TargetSchemaScripts.ApplyVariantAsync(target, TargetSchemaScripts.Variants.TemporalTargetForPlainSource);
            await using var sqlite = new SqliteTempFileHarness();

            await ExportOnlyAsync(source, sqlite, "dbo.Ledgers");

            var preflight = await new SqlDataPackImporter().PreflightAsync(sqlite.FilePath, target.ConnectionString);
            preflight.Errors.ShouldBeEmpty();
            preflight.IsValid.ShouldBeTrue();

            var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

            result.RowCount.ShouldBe(3);
            result.Warnings.ShouldNotContain(w => w.Contains("dbo.Ledgers") && w.Contains("system versioning is temporarily suspended"));

            (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Ledgers")).ShouldBeTrue();
            (await TemporalAssertions.ReadPeriodColumnNamesAsync(target, "dbo.Ledgers")).ShouldBe(("ValidFrom", "ValidTo"));

            // The package carries no period values, so these can only have come from the engine at insert time.
            (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Ledgers WHERE ValidFrom > DATEADD(MINUTE, -30, SYSUTCDATETIME()) AND ValidTo = CONVERT(DATETIME2(7), '9999-12-31 23:59:59.9999999')")).ShouldBe(3);
        }
    }

    private async Task<SqlServerFixtureDatabase> SeededSourceAsync() {
        var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Suite));
        return db;
    }

    private async Task<SqlServerFixtureDatabase> UnseededTargetAsync() {
        var db = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(db, Suite);
        return db;
    }

    private static Task<SqlDataPackResult> ExportOnlyAsync(SqlServerFixtureDatabase source, SqliteTempFileHarness sqlite, params string[] tables) {
        return new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables
        });
    }
}
