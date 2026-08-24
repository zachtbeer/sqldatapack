using Microsoft.Data.SqlClient;
using Shouldly;
using SqlDataPack.Internal;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Covers the suspend/restore ceremony around system-versioned temporal tables: which discovered pairs are
/// actually suspended for a given import scope (and whether the SYSTEM_TIME period has to be dropped along with
/// versioning), the exact catalog-guarded T-SQL emitted for suspend and restore, and the best-effort restore that
/// must swallow failures so it never masks the import error that triggered it.
/// </summary>
public sealed class TemporalTableManagerTests {
    private static TemporalTable Temporal(string schema = "dbo", string current = "Department", string history = "DepartmentHistory", string start = "ValidFrom", string end = "ValidTo", int retentionPeriod = -1, string? retentionUnit = null) =>
        new(new TableName(schema, current), new TableName(schema, history), start, end, retentionPeriod, retentionUnit);

    private static TemporalSuspension Suspend(bool dropPeriod, TemporalTable? table = null) =>
        new(table ?? Temporal(), dropPeriod);

    private static ColumnMetadata Col(TableName table, string name, int ordinal, bool excluded) =>
        new(table, name, ordinal, "datetime2", 8, 7, 7, IsNullable: false, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: excluded);

    private static TableMetadata Meta(string schema, string name, params string[] columns) =>
        MetaExcluding(schema, name, Array.Empty<string>(), columns);

    /// <summary>Table metadata where <paramref name="excluded"/> columns exist but were dropped from the export.</summary>
    private static TableMetadata MetaExcluding(string schema, string name, string[] excluded, params string[] columns) {
        var tableName = new TableName(schema, name);
        var cols = columns.Select((c, i) => Col(tableName, c, i, excluded.Contains(c, StringComparer.OrdinalIgnoreCase))).ToArray();
        return new TableMetadata(tableName, $"{schema}__{name}".ToLowerInvariant(), cols);
    }

    private static int IndexOf(string sql, string fragment) {
        var index = sql.IndexOf(fragment, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, $"'{fragment}' is missing from:\n{sql}");
        return index;
    }

    // ----- ResolveSuspensions ------------------------------------------------------------------

    [Fact]
    public void ResolveSuspensions_CurrentInScopeWithPeriodColumns_IncludedAndDropsPeriod() {
        var result = TemporalTableManager.ResolveSuspensions([Temporal()], [Meta("dbo", "Department", "DepartmentId", "ValidFrom", "ValidTo")]);

        // False here leaves the period defined, and SQL Server rejects the insert with Msg 13536.
        result.ShouldHaveSingleItem().DropPeriod.ShouldBeTrue();
    }

    [Fact]
    public void ResolveSuspensions_OnlyHistoryInScope_IncludedWithoutDroppingPeriod() {
        var result = TemporalTableManager.ResolveSuspensions([Temporal()], [Meta("dbo", "DepartmentHistory", "DepartmentId", "ValidFrom", "ValidTo")]);

        // The history insert needs versioning off, but no period values are being written to the current table.
        result.ShouldHaveSingleItem().DropPeriod.ShouldBeFalse();
    }

    [Fact]
    public void ResolveSuspensions_CurrentInScopeButPeriodColumnsExcluded_NotSuspended() {
        var scope = MetaExcluding("dbo", "Department", ["ValidFrom", "ValidTo"], "DepartmentId", "ValidFrom", "ValidTo");

        var result = TemporalTableManager.ResolveSuspensions([Temporal()], [scope]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveSuspensions_TemporalTargetWithNoPeriodColumnsInPackage_NotSuspended() {
        // Plain source table landing in a temporal target: nothing to suspend, the engine populates the period.
        var result = TemporalTableManager.ResolveSuspensions([Temporal()], [Meta("dbo", "Department", "DepartmentId", "Name")]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveSuspensions_MatchesScopeCaseInsensitively() {
        // Both the table lookup and the period column lookup are case-insensitive.
        var result = TemporalTableManager.ResolveSuspensions([Temporal()], [Meta("DBO", "DEPARTMENT", "validfrom", "VALIDTO")]);

        result.ShouldHaveSingleItem().DropPeriod.ShouldBeTrue();
    }

    [Fact]
    public void ResolveSuspensions_MultiplePairs_ReturnsOnlyInScope() {
        TemporalTable[] discovered = [
            Temporal(),
            Temporal(current: "Product", history: "ProductHistory"),
            Temporal(current: "Employee", history: "EmployeeHistory"),
            Temporal(current: "Region", history: "RegionHistory"),
            Temporal(schema: "sales", current: "Price", history: "PriceHistory")
        ];
        TableMetadata[] scope = [
            Meta("dbo", "Department", "ValidFrom", "ValidTo"),
            Meta("sales", "PriceHistory", "PriceId", "ValidFrom", "ValidTo")
        ];

        var result = TemporalTableManager.ResolveSuspensions(discovered, scope);

        result.Select(s => s.Table.Current.FullName).ShouldBe(["dbo.Department", "sales.Price"]);
        result.Select(s => s.DropPeriod).ShouldBe([true, false]);
    }

    // ----- BuildSuspendSql ---------------------------------------------------------------------

    [Fact]
    public void BuildSuspendSql_DropPeriodTrue_DisablesVersioningThenDropsPeriod() {
        var sql = TemporalTableManager.BuildSuspendSql(Suspend(dropPeriod: true));

        sql.ShouldContain("ALTER TABLE [dbo].[Department] SET (SYSTEM_VERSIONING = OFF)");
        // Dropping the period on a still-versioned table is rejected, so SET OFF has to come first.
        IndexOf(sql, "SYSTEM_VERSIONING = OFF").ShouldBeLessThan(IndexOf(sql, "DROP PERIOD FOR SYSTEM_TIME"));
    }

    [Fact]
    public void BuildSuspendSql_DropPeriodFalse_OnlyDisablesVersioning() {
        var sql = TemporalTableManager.BuildSuspendSql(Suspend(dropPeriod: false));

        sql.ShouldContain("SET (SYSTEM_VERSIONING = OFF)");
        sql.ShouldNotContain("DROP PERIOD");
    }

    // ----- BuildRestoreSql ---------------------------------------------------------------------

    [Fact]
    public void BuildRestoreSql_DropPeriodTrue_AddsPeriodThenEnablesVersioning_ConsistencyOnByDefault() {
        var sql = TemporalTableManager.BuildRestoreSql(Suspend(dropPeriod: true), dataConsistencyCheck: true);

        sql.ShouldContain("ADD PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])");
        sql.ShouldContain("HISTORY_TABLE = [dbo].[DepartmentHistory]");
        sql.ShouldContain("DATA_CONSISTENCY_CHECK = ON");
        // Versioning cannot be enabled without a period, so SET ON has to come last.
        IndexOf(sql, "ADD PERIOD").ShouldBeLessThan(IndexOf(sql, "SYSTEM_VERSIONING = ON"));
    }

    [Fact]
    public void BuildRestoreSql_DropPeriodFalse_OmitsAddPeriod_StillEnablesVersioning() {
        var sql = TemporalTableManager.BuildRestoreSql(Suspend(dropPeriod: false), dataConsistencyCheck: true);

        // Absent outright, not merely wrapped in the IF NOT EXISTS guard.
        sql.ShouldNotContain("ADD PERIOD");
        sql.ShouldContain("SET (SYSTEM_VERSIONING = ON");
    }

    [Fact]
    public void BuildRestoreSql_ConsistencyCheckDisabled_WritesConsistencyCheckOff() {
        var sql = TemporalTableManager.BuildRestoreSql(Suspend(dropPeriod: true), dataConsistencyCheck: false);

        sql.ShouldContain("DATA_CONSISTENCY_CHECK = OFF");
        sql.ShouldNotContain("DATA_CONSISTENCY_CHECK = ON");
    }

    [Fact]
    public void BuildRestoreSql_FiniteRetention_ReappliesHistoryRetentionPeriod() {
        // SET SYSTEM_VERSIONING = OFF resets retention to INFINITE, so restore has to put it back.
        // MONTH, not MONTHS: the unit comes from sys.tables.history_retention_period_unit_desc, which is singular.
        var table = Temporal(retentionPeriod: 3, retentionUnit: "MONTH");

        var sql = TemporalTableManager.BuildRestoreSql(new TemporalSuspension(table, DropPeriod: true), dataConsistencyCheck: true);

        sql.ShouldContain("HISTORY_RETENTION_PERIOD = 3 MONTH");
    }

    [Fact]
    public void BuildRestoreSql_InfiniteRetention_OmitsRetentionClause() {
        var sql = TemporalTableManager.BuildRestoreSql(Suspend(dropPeriod: true), dataConsistencyCheck: true);

        // A stray "HISTORY_RETENTION_PERIOD = -1" would break restore for the default case.
        sql.ShouldNotContain("HISTORY_RETENTION_PERIOD");
    }

    [Fact]
    public void BuildRestoreSql_UsesCustomPeriodColumnNames() {
        var table = Temporal(start: "LastUpdateDate", end: "LastUpdateValidTo");

        var sql = TemporalTableManager.BuildRestoreSql(new TemporalSuspension(table, DropPeriod: true), dataConsistencyCheck: true);

        sql.ShouldContain("ADD PERIOD FOR SYSTEM_TIME ([LastUpdateDate], [LastUpdateValidTo])");
        sql.ShouldNotContain("ValidFrom");
        sql.ShouldNotContain("[ValidTo]");
    }

    [Fact]
    public void BuildSuspendAndRestoreSql_AreCatalogGuarded() {
        var suspension = Suspend(dropPeriod: true);

        var suspend = TemporalTableManager.BuildSuspendSql(suspension);
        var restore = TemporalTableManager.BuildRestoreSql(suspension, dataConsistencyCheck: true);

        // Re-running either script after it already took effect must be a no-op, not an error.
        suspend.ShouldContain("IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[dbo].[Department]') AND temporal_type = 2)");
        suspend.ShouldContain("IF EXISTS (SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID(N'[dbo].[Department]') AND period_type = 1)");
        restore.ShouldContain("IF NOT EXISTS (SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID(N'[dbo].[Department]') AND period_type = 1)");
        restore.ShouldContain("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[dbo].[Department]') AND temporal_type = 2)");
    }

    [Fact]
    public void BuildSuspendAndRestoreSql_EscapeOddIdentifiers() {
        var table = Temporal(schema: "od'd", current: "Odd]Name", history: "Odd]Name_History");
        var suspension = new TemporalSuspension(table, DropPeriod: true);

        var suspend = TemporalTableManager.BuildSuspendSql(suspension);
        var restore = TemporalTableManager.BuildRestoreSql(suspension, dataConsistencyCheck: true);

        // Brackets double inside the quoted identifier; quotes double again inside the OBJECT_ID literal.
        suspend.ShouldContain("ALTER TABLE [od'd].[Odd]]Name]");
        suspend.ShouldContain("OBJECT_ID(N'[od''d].[Odd]]Name]')");
        restore.ShouldContain("OBJECT_ID(N'[od''d].[Odd]]Name]')");
        restore.ShouldContain("HISTORY_TABLE = [od'd].[Odd]]Name_History]");
    }

    // ----- TryRestoreBestEffortAsync (offline) -------------------------------------------------

    [Fact]
    public async Task TryRestoreBestEffortAsync_WhenRestoreFails_AddsOneDeduplicatedWarningPerTable() {
        // A closed connection makes every restore throw; cleanup must swallow so it never masks the import failure.
        await using var connection = new SqlConnection("Server=localhost;Database=does-not-matter;");
        var department = Suspend(dropPeriod: true);
        var product = Suspend(dropPeriod: false, Temporal(current: "Product", history: "ProductHistory"));
        var warnings = new List<string>();

        await TemporalTableManager.TryRestoreBestEffortAsync(connection, [department, department, product], dataConsistencyCheck: true, warnings);

        warnings.Count.ShouldBe(2);
        warnings.ShouldAllBe(w => w.Contains("SET (SYSTEM_VERSIONING = ON"));
        warnings.ShouldContain(w => w.Contains("dbo.Department") && w.Contains("HISTORY_TABLE = [dbo].[DepartmentHistory]"));
        warnings.ShouldContain(w => w.Contains("dbo.Product") && w.Contains("HISTORY_TABLE = [dbo].[ProductHistory]"));
    }
}
