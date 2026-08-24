using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// What a host UI sees while an operation runs, and the batch size it actually runs at.
/// core-commerce gives us dbo.Orders (500 rows, an exact multiple of the batch sizes used here) and
/// dbo.OrderLines (1001 rows, never a multiple) -- the pair that exposes a duplicate or missing final
/// report at the batch boundary.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class ProgressAndBatchingTests {
    private const string SourceFixture = "core-commerce.sql";

    // The one column exclusion core-commerce needs to export whole: sql_variant is unsupported.
    private const string UnsupportedColumn = "dbo.CustomerProfiles.LegacyFlags";

    private const int EvenlyDividingBatchSize = 100;
    private const int SmallExportBatchSize = 50;
    private const int LargeImportBatchSize = 5_000;

    // Sits between dbo.Orders (500 rows) and dbo.OrderLines (1001), so adaptive batching fires on exactly
    // one real table.
    private const long AdaptiveRowThreshold = 600;
    private const int AdaptiveBatchSize = 60;
    private const int UnreducedBatchSize = 1_000;

    // High enough that the byte-derived caps never bind, leaving the row threshold as the only lever.
    private const long NoByteThreshold = 1L << 40;
    private const long NoByteCap = 1L << 30;

    // dbo.sysdiagrams is absent on purpose: ExcludeSsmsDiagrams drops it from every export by default.
    private const string ExportedRowCountSql = """
                                               SELECT 'dbo.Countries', COUNT_BIG(*) FROM dbo.Countries
                                               UNION ALL SELECT 'dbo.Currencies', COUNT_BIG(*) FROM dbo.Currencies
                                               UNION ALL SELECT 'dbo.CustomerDocuments', COUNT_BIG(*) FROM dbo.CustomerDocuments
                                               UNION ALL SELECT 'dbo.CustomerProfiles', COUNT_BIG(*) FROM dbo.CustomerProfiles
                                               UNION ALL SELECT 'dbo.Customers', COUNT_BIG(*) FROM dbo.Customers
                                               UNION ALL SELECT 'dbo.GlobalSettings', COUNT_BIG(*) FROM dbo.GlobalSettings
                                               UNION ALL SELECT 'dbo.OrderLines', COUNT_BIG(*) FROM dbo.OrderLines
                                               UNION ALL SELECT 'dbo.Orders', COUNT_BIG(*) FROM dbo.Orders
                                               UNION ALL SELECT 'tenant.Customers', COUNT_BIG(*) FROM tenant.Customers
                                               UNION ALL SELECT 'tenant.Partners', COUNT_BIG(*) FROM tenant.Partners
                                               """;

    private readonly SqlServerContainerFixture _fixture;

    public ProgressAndBatchingTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task Export_ProgressStream_IsOrderedAndComplete() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var sqlite = new SqliteTempFileHarness();
        var progress = new ProgressRecorder();
        var options = new ExportOptions {
            ExcludeColumns = [UnsupportedColumn],
            BatchSize = EvenlyDividingBatchSize,
            AdaptiveBatchingEnabled = false,
            Progress = progress
        };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        var rowCounts = await ReadExportedRowCountsAsync(source);
        AssertProgressStreamIsOrderedAndComplete(progress.Events, rowCounts);
        AssertFinalReportAtBatchBoundary(progress.Events, rowCounts, EvenlyDividingBatchSize);
    }

    [Fact]
    public async Task Import_ProgressStream_IsOrderedAndComplete() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, SourceFixture);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            ExcludeColumns = [UnsupportedColumn],
            BatchSize = EvenlyDividingBatchSize,
            AdaptiveBatchingEnabled = false
        });
        var progress = new ProgressRecorder();
        var options = new ImportOptions {
            BatchSize = EvenlyDividingBatchSize,
            AdaptiveBatchingEnabled = false,
            Progress = progress
        };

        await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, options);

        var rowCounts = await ReadExportedRowCountsAsync(source);
        AssertProgressStreamIsOrderedAndComplete(progress.Events, rowCounts);
        AssertFinalReportAtBatchBoundary(progress.Events, rowCounts, EvenlyDividingBatchSize);
    }

    [Fact]
    public async Task Warnings_ArriveInBothChannels() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var sqlite = new SqliteTempFileHarness();
        var progress = new ProgressRecorder();
        var options = new ExportOptions {
            // An AllExcept pattern matching nothing warns rather than aborting; sysdiagrams is dropped
            // silently unless the exclusion is announced. Two unrelated warning producers, one export.
            TableSelection = ExportTableSelectionMode.AllExcept,
            Tables = ["dbo.NoSuchTableAnywhere"],
            ExcludeColumns = [UnsupportedColumn],
            Progress = progress
        };

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        var reported = progress.Events
            .Where(e => e.Kind == SqlDataPackProgressKind.Warning)
            .Select(e => e.Message!)
            .ToArray();
        reported.ShouldBe(result.Warnings.ToArray());
        result.Warnings.ShouldContain("Exclude pattern 'dbo.NoSuchTableAnywhere' did not match any user table and was ignored.");
        result.Warnings.ShouldContain(w => w.Contains("Excluded SSMS database diagram table 'dbo.sysdiagrams'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_EffectiveBatchSize_IsClampedToTheRecordedPackageBatchSize() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, SourceFixture);
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            ExcludeColumns = [UnsupportedColumn],
            BatchSize = SmallExportBatchSize,
            AdaptiveBatchingEnabled = false
        });
        var progress = new ProgressRecorder();
        var options = new ImportOptions {
            BatchSize = LargeImportBatchSize,
            AdaptiveBatchingEnabled = true,
            LargeTableThresholdBytes = NoByteThreshold,
            LargeTableRowThreshold = 1_000_000,
            MaxBatchBytes = NoByteCap,
            Progress = progress
        };

        var result = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString, options);

        var manifest = await new SqlDataPackReader().ReadManifestAsync(sqlite.FilePath);
        manifest.Tables.Select(t => t.ExportBatchSize).Distinct().ShouldBe(new[] { SmallExportBatchSize });

        var rowCounts = await ReadExportedRowCountsAsync(source);
        foreach (var (table, rows) in rowCounts) {
            ShouldReportRowsCopiedInBatchesOf(RowsCopiedMarks(progress.Events, table), SmallExportBatchSize, rows);
            result.Warnings.ShouldContain($"Adaptive batching set import batch size for '{table}' to {SmallExportBatchSize} rows.");
        }
    }

    [Fact]
    public async Task Export_AdaptiveBatching_WarningMatchesRuntimeBehaviour() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var sqlite = new SqliteTempFileHarness();
        var progress = new ProgressRecorder();
        var options = new ExportOptions {
            ExcludeColumns = [UnsupportedColumn],
            BatchSize = UnreducedBatchSize,
            AdaptiveBatchingEnabled = true,
            LargeTableThresholdBytes = NoByteThreshold,
            LargeTableRowThreshold = AdaptiveRowThreshold,
            LargeTableBatchSize = AdaptiveBatchSize,
            MaxBatchBytes = NoByteCap,
            Progress = progress
        };

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        var rowCounts = await ReadExportedRowCountsAsync(source);
        var adaptiveWarnings = result.Warnings
            .Where(w => w.StartsWith("Adaptive batching set export batch size", StringComparison.Ordinal))
            .ToArray();

        // Only tables at or over the row threshold get reduced -- the cap never raises anyone else.
        adaptiveWarnings.Select(TableNamedIn)
            .ShouldBe(rowCounts.Where(c => c.Value >= AdaptiveRowThreshold).Select(c => c.Key), ignoreOrder: true);
        adaptiveWarnings.ShouldNotBeEmpty();

        foreach (var warning in adaptiveWarnings) {
            var table = TableNamedIn(warning);
            var declared = BatchSizeNamedIn(warning);
            declared.ShouldBeLessThan(UnreducedBatchSize);
            ShouldReportRowsCopiedInBatchesOf(RowsCopiedMarks(progress.Events, table), declared, rowCounts[table]);
        }

        var reduced = adaptiveWarnings.Select(TableNamedIn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in rowCounts.Keys.Where(t => !reduced.Contains(t))) {
            ShouldReportRowsCopiedInBatchesOf(RowsCopiedMarks(progress.Events, table), UnreducedBatchSize, rowCounts[table]);
        }
    }

    private static void AssertProgressStreamIsOrderedAndComplete(IReadOnlyList<SqlDataPackProgress> events, IReadOnlyDictionary<string, long> expectedRowCounts) {
        events.Count(e => e.Kind == SqlDataPackProgressKind.OperationStarted).ShouldBe(1);
        events[0].Kind.ShouldBe(SqlDataPackProgressKind.OperationStarted);
        events.Count(e => e.Kind == SqlDataPackProgressKind.OperationCompleted).ShouldBe(1);
        events[^1].Kind.ShouldBe(SqlDataPackProgressKind.OperationCompleted);

        TableNames(events, SqlDataPackProgressKind.TableStarted).ShouldBe(expectedRowCounts.Keys, ignoreOrder: true);
        TableNames(events, SqlDataPackProgressKind.TableCompleted).ShouldBe(expectedRowCounts.Keys, ignoreOrder: true);

        foreach (var (table, expected) in expectedRowCounts) {
            var started = IndexOfSingle(events, SqlDataPackProgressKind.TableStarted, table);
            var completed = IndexOfSingle(events, SqlDataPackProgressKind.TableCompleted, table);
            started.ShouldBeLessThan(completed);

            var marks = new List<long>();
            for (var i = started + 1; i < completed; i++) {
                if (IsRowsCopiedFor(events[i], table)) {
                    marks.Add(events[i].RowsProcessed);
                }
            }

            marks.ShouldNotBeEmpty($"no RowsCopied reported between TableStarted and TableCompleted for {table}");
            marks.Count.ShouldBe(events.Count(e => IsRowsCopiedFor(e, table)), $"{table} reported rows outside its own start/complete pair");
            // Monotonic: a progress bar that jumps backwards is as broken as one that stalls.
            marks.ShouldBe(marks.OrderBy(m => m).ToArray());
            marks[^1].ShouldBe(expected);
            events[completed].RowsProcessed.ShouldBe(expected);
        }
    }

    /// <summary>
    /// The boundary the whole file exists for: a table whose row count divides evenly by the batch size and
    /// one that does not, each checked against the exact sequence of reports it must produce.
    /// </summary>
    private static void AssertFinalReportAtBatchBoundary(IReadOnlyList<SqlDataPackProgress> events, IReadOnlyDictionary<string, long> rowCounts, int batchSize) {
        (rowCounts["dbo.Orders"] % batchSize).ShouldBe(0L, "dbo.Orders must divide evenly by the batch size or this test probes nothing");
        (rowCounts["dbo.OrderLines"] % batchSize).ShouldNotBe(0L, "dbo.OrderLines must not divide evenly by the batch size or this test probes nothing");

        ShouldReportRowsCopiedInBatchesOf(RowsCopiedMarks(events, "dbo.Orders"), batchSize, rowCounts["dbo.Orders"]);
        ShouldReportRowsCopiedInBatchesOf(RowsCopiedMarks(events, "dbo.OrderLines"), batchSize, rowCounts["dbo.OrderLines"]);
    }

    /// <summary>
    /// Rebuilds the reports a run at <paramref name="batchSize"/> must produce: one at every full batch, plus a
    /// trailing one only when the last batch is short. This is how the effective batch size is observed without
    /// reaching into internals.
    /// </summary>
    private static void ShouldReportRowsCopiedInBatchesOf(IReadOnlyList<long> marks, int batchSize, long totalRows) {
        var expected = new List<long>();
        for (var mark = (long)batchSize; mark <= totalRows; mark += batchSize) {
            expected.Add(mark);
        }

        if (totalRows == 0 || totalRows % batchSize != 0) {
            expected.Add(totalRows);
        }

        marks.ShouldBe(expected);
    }

    private static IReadOnlyList<long> RowsCopiedMarks(IReadOnlyList<SqlDataPackProgress> events, string table) {
        return events.Where(e => IsRowsCopiedFor(e, table)).Select(e => e.RowsProcessed).ToArray();
    }

    private static bool IsRowsCopiedFor(SqlDataPackProgress progress, string table) {
        return progress.Kind == SqlDataPackProgressKind.RowsCopied && string.Equals(progress.TableName, table, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> TableNames(IReadOnlyList<SqlDataPackProgress> events, SqlDataPackProgressKind kind) {
        return events.Where(e => e.Kind == kind).Select(e => e.TableName!).ToArray();
    }

    private static int IndexOfSingle(IReadOnlyList<SqlDataPackProgress> events, SqlDataPackProgressKind kind, string table) {
        var indexes = new List<int>();
        for (var i = 0; i < events.Count; i++) {
            if (events[i].Kind == kind && string.Equals(events[i].TableName, table, StringComparison.OrdinalIgnoreCase)) {
                indexes.Add(i);
            }
        }

        indexes.Count.ShouldBe(1, $"expected exactly one {kind} event for {table}");
        return indexes[0];
    }

    private static string TableNamedIn(string warning) {
        var match = Regex.Match(warning, @"for '([^']+)'");
        match.Success.ShouldBeTrue($"warning names no table: {warning}");
        return match.Groups[1].Value;
    }

    private static int BatchSizeNamedIn(string warning) {
        var match = Regex.Match(warning, @"to (\d+) rows\.$");
        match.Success.ShouldBeTrue($"warning names no batch size: {warning}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, long>> ReadExportedRowCountsAsync(SqlServerFixtureDatabase db) {
        var records = await db.ReadRecordsAsync(ExportedRowCountSql);
        return records.ToDictionary(r => r[0], r => long.Parse(r[1], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProgressRecorder : IProgress<SqlDataPackProgress> {
        private readonly List<SqlDataPackProgress> _events = [];

        public IReadOnlyList<SqlDataPackProgress> Events {
            get {
                lock (_events) {
                    return _events.ToArray();
                }
            }
        }

        public void Report(SqlDataPackProgress value) {
            lock (_events) {
                _events.Add(value);
            }
        }
    }
}
