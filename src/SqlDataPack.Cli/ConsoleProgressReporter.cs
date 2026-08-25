using SqlDataPack.Models;

namespace SqlDataPack.Cli;

/// <summary>
/// Writes progress to stderr so stdout carries only the result summary and stays pipeable.
/// An export can sit silent for minutes, which is tolerable in a library and not in a command.
/// </summary>
internal sealed class ConsoleProgressReporter : IProgress<SqlDataPackProgress> {
    private static readonly TimeSpan RowUpdateInterval = TimeSpan.FromSeconds(2);

    private readonly TextWriter writer;
    private readonly bool quiet;
    private readonly Lock gate = new();
    private string? currentTable;
    private long lastReportedTicks;

    public ConsoleProgressReporter(TextWriter writer, bool quiet) {
        this.writer = writer;
        this.quiet = quiet;
    }

    public void Report(SqlDataPackProgress value) {
        lock (this.gate) {
            switch (value.Kind) {
                case SqlDataPackProgressKind.Warning:
                    // Warnings survive --quiet: they are the reason someone re-reads the output.
                    this.writer.WriteLine($"warning: {value.Message ?? "(no message)"}");
                    break;

                case SqlDataPackProgressKind.TableStarted when !this.quiet:
                    this.currentTable = value.TableName;
                    this.lastReportedTicks = Environment.TickCount64;
                    this.writer.WriteLine($"  {value.TableName}");
                    break;

                case SqlDataPackProgressKind.RowsCopied when !this.quiet:
                    this.ReportRows(value);
                    break;

                case SqlDataPackProgressKind.TableCompleted when !this.quiet:
                    this.writer.WriteLine($"  {value.TableName ?? this.currentTable}: {value.RowsProcessed:N0} rows");
                    this.currentTable = null;
                    break;
            }
        }
    }

    private void ReportRows(SqlDataPackProgress value) {
        var now = Environment.TickCount64;
        if (now - this.lastReportedTicks < RowUpdateInterval.TotalMilliseconds) {
            return;
        }

        this.lastReportedTicks = now;
        var table = value.TableName ?? this.currentTable ?? "(table)";
        var of = value.TotalRows is { } total ? $" of {total:N0}" : string.Empty;
        this.writer.WriteLine($"  {table}: {value.RowsProcessed:N0}{of} rows");
    }
}
