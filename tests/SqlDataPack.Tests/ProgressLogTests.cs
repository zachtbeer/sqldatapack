using Microsoft.Extensions.Logging;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Covers the progress/logger fan-out sink: when no logger is supplied the caller's progress
/// reporter is returned untouched, and when a logger is supplied every <see cref="SqlDataPackProgress"/>
/// event reaches both the progress reporter and the logger at the expected level.
/// </summary>
public sealed class ProgressLogTests {
    // The kind -> level contract, pinned here so a change in the product has to be a deliberate edit.
    // RowsCopied must stay at Trace (per-batch noise) and Warning at Warning (or hosts never see it).
    private static readonly (SqlDataPackProgress Event, LogLevel Level)[] EventStream = [
        (new SqlDataPackProgress(SqlDataPackProgressKind.OperationStarted, Message: "Export started."), LogLevel.Information),
        (new SqlDataPackProgress(SqlDataPackProgressKind.TableStarted, "dbo.Customers", TotalRows: 10), LogLevel.Information),
        (new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, "dbo.Customers", 5, 10), LogLevel.Trace),
        (new SqlDataPackProgress(SqlDataPackProgressKind.TableCompleted, "dbo.Customers", 10, 10), LogLevel.Information),
        (new SqlDataPackProgress(SqlDataPackProgressKind.Warning, Message: "heads up"), LogLevel.Warning),
        (new SqlDataPackProgress(SqlDataPackProgressKind.OperationCompleted, RowsProcessed: 10, TotalRows: 10, Message: "Export completed."), LogLevel.Information)
    ];

    [Fact]
    public void Wrap_WithoutLogger_ReturnsTheOriginalProgressInstance() {
        var progress = new RecordingProgress();

        SqlDataPackProgressLog.Wrap(progress, null).ShouldBeSameAs(progress);
        SqlDataPackProgressLog.Wrap(null, null).ShouldBeNull();
    }

    [Fact]
    public void Wrap_FansEveryEventOutToBothProgressAndLogger() {
        // Fails when a new kind is added without deciding its level here.
        EventStream.Select(e => e.Event.Kind).ShouldBe(Enum.GetValues<SqlDataPackProgressKind>());

        var progress = new RecordingProgress();
        var logger = new CapturingLogger();

        var sink = SqlDataPackProgressLog.Wrap(progress, logger);
        sink.ShouldNotBeNull();
        foreach (var (evt, _) in EventStream) {
            sink!.Report(evt);
        }

        // The progress reporter still observes every event, in order, unchanged.
        progress.Events.ShouldBe(EventStream.Select(e => e.Event));

        logger.Entries.Select(e => e.Level).ShouldBe(EventStream.Select(e => e.Level));
        logger.Entries[2].Message.ShouldContain("dbo.Customers");
        logger.Entries[4].Message.ShouldContain("heads up");

        // Same fan-out with no inner progress: the logger channel is unaffected.
        var loggerOnly = new CapturingLogger();
        var loggerOnlySink = SqlDataPackProgressLog.Wrap(null, loggerOnly);
        loggerOnlySink.ShouldNotBeNull();
        foreach (var (evt, _) in EventStream) {
            loggerOnlySink!.Report(evt);
        }

        loggerOnly.Entries.Select(e => e.Level).ShouldBe(EventStream.Select(e => e.Level));
    }

    [Fact]
    public void Wrap_RespectsDisabledLevels_SoTraceRowEventsAreSkippedWhenTraceIsOff() {
        var logger = new CapturingLogger { MinLevel = LogLevel.Information };

        var sink = SqlDataPackProgressLog.Wrap(null, logger);
        sink.ShouldNotBeNull();
        sink!.Report(new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, "dbo.Customers", 5, 10));
        sink.Report(new SqlDataPackProgress(SqlDataPackProgressKind.RowsCopied, "dbo.Customers", 10, 10));
        sink.Report(new SqlDataPackProgress(SqlDataPackProgressKind.TableCompleted, "dbo.Customers", 10, 10));

        // Both RowsCopied events are dropped by the generated IsEnabled guard; only TableCompleted survives.
        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Level.ShouldBe(LogLevel.Information);
        logger.Entries[0].Message.ShouldContain("dbo.Customers");
    }

    private sealed class RecordingProgress : IProgress<SqlDataPackProgress> {
        public List<SqlDataPackProgress> Events { get; } = [];

        public void Report(SqlDataPackProgress value) => Events.Add(value);
    }

    private sealed class CapturingLogger : ILogger {
        public LogLevel MinLevel { get; init; } = LogLevel.Trace;

        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));

        private sealed class NullScope : IDisposable {
            public static readonly NullScope Instance = new();

            public void Dispose() {
            }
        }
    }
}
