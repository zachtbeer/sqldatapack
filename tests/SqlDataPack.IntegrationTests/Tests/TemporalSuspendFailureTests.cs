using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// What happens when the suspend/restore ceremony goes wrong. Every test here is about a database being left
/// stranded: versioning off, period dropped, and nothing telling the operator about it.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class TemporalSuspendFailureTests {
    private const string Fixture = "temporal-suite.sql";

    // Created by temporal-suite.sql: db_owner with ALTER denied on dbo.Sectors. A DENY does nothing when the
    // connection is sa, so the partial-failure test has to authenticate as this login or it proves nothing.
    private const string RestrictedLogin = "TemporalSuite_PartialFailureImporter";
    private const string RestrictedPassword = "P@rt1alFa!lure_2026";

    private readonly SqlServerContainerFixture _fixture;

    public TemporalSuspendFailureTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_TemporalSource_EmitsInformationalWarning() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Departments", "dbo.DepartmentHistory"]
        });

        // Without this the user's first sign that a ceremony exists is Msg 13560 with no context.
        result.Warnings.ShouldContain(w => w.Contains("dbo.Departments") && w.Contains("system-versioned temporal table") && w.Contains("system versioning is temporarily suspended") && w.Contains(nameof(ImportOptions.SuspendTemporalSystemVersioning)));

        // Match on the warning's own wording, not just the table name: adaptive batching also writes a
        // warning naming 'dbo.Departments', and HasWarningMatchingAsync asserts an exact match count.
        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasWarningMatchingAsync(connection, "Table 'dbo.Departments' is a system-versioned temporal table");
        await SqlitePackageAssertions.HasWarningMatchingAsync(connection, "system versioning is temporarily suspended");
    }

    [Fact]
    public async Task Import_InconsistentHistory_FailsWithCheckOnAndSucceedsWithCheckOff() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.Departments", "dbo.DepartmentHistory"]
        });

        // Every packaged history row now starts after it ends. SQLite evaluates both SET expressions against
        // the original row, so this is a swap, not an overwrite.
        await using (var package = await sqlite.OpenConnectionAsync()) {
            await package.ExecuteSqlAsync("UPDATE dbo__departmenthistory SET ValidFrom = ValidTo, ValidTo = ValidFrom");
        }

        SqliteConnection.ClearAllPools();

        await using (var targetOn = await SqlServerFixtureDatabase.CreateAsync(_fixture)) {
            await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(targetOn, Fixture);

            var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, targetOn.ConnectionString));

            exception.Message.ShouldContain("dbo.Departments");
            exception.Message.ShouldContain("consistency");
            exception.Message.ShouldContain(nameof(ImportOptions.TemporalDataConsistencyCheck));
            // A raw SqlException here would leave the caller with "Msg 13575" and no idea which option to turn off.
            exception.InnerException.ShouldBeOfType<SqlException>();
        }

        await using (var targetOff = await SqlServerFixtureDatabase.CreateAsync(_fixture)) {
            await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(targetOff, Fixture);
            var options = ImportOptions.Default;
            options.TemporalDataConsistencyCheck = false;

            var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, targetOff.ConnectionString, options);

            result.RowCount.ShouldBe(5);
            (await targetOff.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Departments")).ShouldBe(2);
            (await targetOff.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DepartmentHistory")).ShouldBe(3);
            // The escape hatch has to leave the table versioned again, not merely avoid throwing.
            (await TemporalAssertions.IsSystemVersionedAsync(targetOff, "dbo.Departments")).ShouldBeTrue();
            (await TemporalAssertions.ReadHistoryTableNameAsync(targetOff, "dbo.Departments")).ShouldBe("dbo.DepartmentHistory");
        }
    }

    [Fact]
    public async Task SuspendThenRestore_IsIdempotent() {
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, Fixture);

        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        var suspensions = await ResolveSuspensionsForAsync(connection, "dbo.Outposts");

        await TemporalTableManager.SuspendAsync(connection, suspensions, null, CancellationToken.None);
        await TemporalTableManager.SuspendAsync(connection, suspensions, null, CancellationToken.None);

        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Outposts")).ShouldBeFalse();
        (await HasSystemTimePeriodAsync(target, "dbo.Outposts")).ShouldBeFalse();

        await TemporalTableManager.RestoreAsync(connection, suspensions, dataConsistencyCheck: true, null, CancellationToken.None);
        // The best-effort cleanup path re-runs this SQL on top of a restore that may already have succeeded, so
        // a second run that throws turns a recoverable import failure into a hard crash.
        await TemporalTableManager.RestoreAsync(connection, suspensions, dataConsistencyCheck: true, null, CancellationToken.None);

        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Outposts")).ShouldBeTrue();
        (await TemporalAssertions.ReadHistoryTableNameAsync(target, "dbo.Outposts")).ShouldBe("dbo.OutpostHistory");
        (await TemporalAssertions.ReadPeriodColumnNamesAsync(target, "dbo.Outposts")).ShouldBe(("ValidFrom", "ValidTo"));
    }

    [Fact]
    public async Task TryRestoreBestEffort_WhenRestoreIsImpossible_WarnsInsteadOfThrowing() {
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, Fixture);

        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        var suspensions = await ResolveSuspensionsForAsync(connection, "dbo.Outposts");

        await TemporalTableManager.SuspendAsync(connection, suspensions, null, CancellationToken.None);
        // With the period gone the period columns are ordinary columns again, so dropping the end column makes
        // the restore's ADD PERIOD impossible. Dropping the history table would not work: SQL Server just
        // recreates one when HISTORY_TABLE names a table that is missing.
        await target.ExecuteSqlAsync("ALTER TABLE dbo.Outposts DROP COLUMN ValidTo");

        var warnings = new List<string>();
        await TemporalTableManager.TryRestoreBestEffortAsync(connection, suspensions, dataConsistencyCheck: true, warnings);
        // Two passes: the cleanup path is reachable from more than one failure and must not accumulate duplicates.
        await TemporalTableManager.TryRestoreBestEffortAsync(connection, suspensions, dataConsistencyCheck: true, warnings);

        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("dbo.Outposts");
        // The operator gets a statement to paste, not just the news that something failed.
        warnings[0].ShouldContain("ALTER TABLE [dbo].[Outposts] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[OutpostHistory]))");
    }

    [Fact]
    public async Task Import_SuspendFailsPartway_RestoresPairsAlreadySuspended() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [
                "dbo.Districts", "dbo.DistrictHistory",
                "dbo.Regions", "dbo.RegionHistory",
                "dbo.Sectors", "dbo.SectorHistory",
                "dbo.Teams", "dbo.TeamHistory",
                "dbo.Territories", "dbo.TerritoryHistory"
            ]
        });

        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, Fixture);

        // dbo.Sectors is third in the suspend walk, so pairs 1-2 are already suspended when it fails and pairs
        // 4-5 are never reached. The DENY only bites for this login, not for sa.
        var restricted = _fixture.ConnectionStringFor(target, RestrictedLogin, RestrictedPassword);

        // SuspendAsync runs inside the try/catch that calls TryRestoreBestEffortAsync, so the permission
        // failure on dbo.Sectors is caught, the best-effort restore runs for pairs already suspended, and
        // then the original SqlException is rethrown unwrapped.
        var exception = await Should.ThrowAsync<SqlException>(() => new SqlDataPackImporter().ImportAsync(sqlite.FilePath, restricted));

        exception.Message.ShouldContain("Sectors");

        // Pairs 1 and 2 were suspended before the failure, so the best-effort restore reaches them and puts
        // versioning back on -- they are not left stranded.
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Districts")).ShouldBeTrue();
        (await HasSystemTimePeriodAsync(target, "dbo.Districts")).ShouldBeTrue();
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Regions")).ShouldBeTrue();
        (await HasSystemTimePeriodAsync(target, "dbo.Regions")).ShouldBeTrue();

        // Pairs after the failure were never touched.
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Teams")).ShouldBeTrue();
        (await HasSystemTimePeriodAsync(target, "dbo.Teams")).ShouldBeTrue();
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Territories")).ShouldBeTrue();
        (await HasSystemTimePeriodAsync(target, "dbo.Territories")).ShouldBeTrue();

        // dbo.Sectors itself is stranded: the suspend batch is two statements and only the second one hits
        // the DENY, so SET (SYSTEM_VERSIONING = OFF) already went through by the time DROP PERIOD FOR
        // SYSTEM_TIME fails, and the same DENY blocks the best-effort restore's ADD PERIOD.
        (await TemporalAssertions.IsSystemVersionedAsync(target, "dbo.Sectors")).ShouldBeFalse();
        (await HasSystemTimePeriodAsync(target, "dbo.Sectors")).ShouldBeTrue();
    }

    /// <summary>The suspension the importer would build for one pair whose period values are packaged.</summary>
    private static async Task<IReadOnlyList<TemporalSuspension>> ResolveSuspensionsForAsync(SqlConnection connection, string fullName) {
        var temporals = await TemporalTableManager.DiscoverAsync(connection, null, CancellationToken.None);
        var table = temporals.Single(t => t.Current.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));
        return [new TemporalSuspension(table, DropPeriod: true)];
    }

    private static async Task<bool> HasSystemTimePeriodAsync(SqlServerFixtureDatabase db, string fullName) {
        return await db.ScalarIntAsync($"SELECT COUNT(*) FROM sys.periods WHERE object_id = OBJECT_ID(N'{fullName}') AND period_type = 1") == 1;
    }
}
