using Microsoft.Extensions.Logging;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

/// <summary>
/// Bridges the existing <see cref="IProgress{T}"/> progress channel and an optional
/// <see cref="ILogger"/> so a single sink fans every <see cref="SqlDataPackProgress"/> event out to both.
/// The event-to-log-level mapping lives here and nowhere else.
/// </summary>
internal static partial class SqlDataPackProgressLog {
    /// <summary>
    /// Combines a caller's progress reporter and logger into one sink. When <paramref name="logger"/> is
    /// <see langword="null"/> the original <paramref name="progress"/> instance is returned unchanged, so
    /// callers that never opt into logging pay no extra cost and observe no behaviour change.
    /// </summary>
    public static IProgress<SqlDataPackProgress>? Wrap(IProgress<SqlDataPackProgress>? progress, ILogger? logger) {
        if (logger is null) {
            return progress;
        }

        return new Sink(progress, logger);
    }

    private sealed class Sink : IProgress<SqlDataPackProgress> {
        private readonly IProgress<SqlDataPackProgress>? _progress;
        private readonly ILogger _logger;

        public Sink(IProgress<SqlDataPackProgress>? progress, ILogger logger) {
            _progress = progress;
            _logger = logger;
        }

        public void Report(SqlDataPackProgress value) {
            _progress?.Report(value);
            Log(_logger, value);
        }
    }

    private static void Log(ILogger logger, SqlDataPackProgress value) {
        switch (value.Kind) {
            case SqlDataPackProgressKind.OperationStarted:
                OperationStarted(logger, value.Message ?? string.Empty);
                break;
            case SqlDataPackProgressKind.TableStarted:
                TableStarted(logger, value.TableName ?? string.Empty, value.TotalRows);
                break;
            case SqlDataPackProgressKind.RowsCopied:
                RowsCopied(logger, value.TableName ?? string.Empty, value.RowsProcessed, value.TotalRows);
                break;
            case SqlDataPackProgressKind.TableCompleted:
                TableCompleted(logger, value.TableName ?? string.Empty, value.RowsProcessed, value.TotalRows);
                break;
            case SqlDataPackProgressKind.Warning:
                Warning(logger, value.Message ?? string.Empty);
                break;
            case SqlDataPackProgressKind.OperationCompleted:
                OperationCompleted(logger, value.RowsProcessed, value.Message ?? string.Empty);
                break;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Message}")]
    private static partial void OperationStarted(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Table {TableName} started ({TotalRows} estimated rows).")]
    private static partial void TableStarted(ILogger logger, string tableName, long? totalRows);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace, Message = "Table {TableName} copied {RowsProcessed}/{TotalRows} rows.")]
    private static partial void RowsCopied(ILogger logger, string tableName, long rowsProcessed, long? totalRows);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Table {TableName} completed: {RowsProcessed}/{TotalRows} rows.")]
    private static partial void TableCompleted(ILogger logger, string tableName, long rowsProcessed, long? totalRows);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void Warning(ILogger logger, string message);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "{Message} ({RowsProcessed} rows).")]
    private static partial void OperationCompleted(ILogger logger, long rowsProcessed, string message);
}
