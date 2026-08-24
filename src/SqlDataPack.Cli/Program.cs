using System.CommandLine;
using System.CommandLine.Parsing;
using SqlDataPack;
using SqlDataPack.Cli;
using SqlDataPack.Cli.Commands;
using SqlDataPack.Models;

var export = new ExportCommand();
var import = new ImportCommand();

var root = new RootCommand("Export a slice of a SQL Server database into a single SQLite file, and import it back.") {
    export,
    import
};

export.SetAction((parseResult, cancellationToken) => RunExportAsync(export, parseResult, cancellationToken));
import.SetAction((parseResult, cancellationToken) => RunImportAsync(import, parseResult, cancellationToken));

ParseResult parsed = root.Parse(args);

// System.CommandLine reports parse failures with exit code 1, which is the same code the tool uses
// for "SQL Server said no". Those are different problems, so parse errors are handled here.
if (parsed.Errors.Count > 0) {
    foreach (ParseError error in parsed.Errors) {
        Console.Error.WriteLine(error.Message);
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("Run 'sqldatapack --help' for usage.");
    return ExitCodes.UsageError;
}

// The tool prints its own failures; the default handler would swallow them into a stack trace.
var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

try {
    return await parsed.InvokeAsync(invocation);
}
catch (CliUsageException ex) {
    Console.Error.WriteLine(ex.Message);
    return ExitCodes.UsageError;
}
catch (SqlDataPackException ex) {
    // The library's own failures are the expected kind. A stack trace would bury the message.
    Console.Error.WriteLine(ex.Message);
    return ExitCodes.OperationFailed;
}
catch (OperationCanceledException) {
    Console.Error.WriteLine("Cancelled.");
    return ExitCodes.Cancelled;
}
catch (Exception ex) {
    Console.Error.WriteLine(IsVerbose(parsed, export, import) ? ex.ToString() : $"{ex.GetType().Name}: {ex.Message}");
    if (!IsVerbose(parsed, export, import)) {
        Console.Error.WriteLine("Run again with --verbose for a stack trace.");
    }

    return ExitCodes.Unexpected;
}

static async Task<int> RunExportAsync(ExportCommand command, ParseResult parseResult, CancellationToken cancellationToken) {
    ExportRequest request = command.Bind(parseResult);
    request.Options.Progress = new ConsoleProgressReporter(Console.Error, request.Quiet);

    if (!request.Quiet) {
        Console.Error.WriteLine($"Exporting to {request.OutputPath}");
    }

    SqlDataPackResult result = await SqlData.ExportAsync(request.ConnectionString, request.OutputPath, request.Options, cancellationToken);

    Console.WriteLine($"Exported {result.RowCount:N0} rows from {result.TableCount:N0} tables into {request.OutputPath}");
    ReportWarnings(result);
    return ExitCodes.Success;
}

static async Task<int> RunImportAsync(ImportCommand command, ParseResult parseResult, CancellationToken cancellationToken) {
    ImportRequest request = command.Bind(parseResult);

    // Mistyping the path is the easiest mistake to make here, and the library reports it as a raw
    // SQLite driver error (issue #14). Checking first costs nothing and keeps the common case legible.
    if (!File.Exists(request.PackagePath)) {
        throw new CliUsageException($"Package not found: {request.PackagePath}");
    }

    request.Options.Progress = new ConsoleProgressReporter(Console.Error, request.Quiet);

    if (!request.Quiet) {
        Console.Error.WriteLine($"Importing {request.PackagePath}");
    }

    SqlDataPackResult result = await SqlData.ImportAsync(request.PackagePath, request.ConnectionString, request.Options, cancellationToken);

    Console.WriteLine($"Imported {result.RowCount:N0} rows into {result.TableCount:N0} tables");
    ReportWarnings(result);
    return ExitCodes.Success;
}

static void ReportWarnings(SqlDataPackResult result) {
    if (result.Warnings.Count == 0) {
        return;
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"{result.Warnings.Count:N0} warning(s):");
    foreach (string warning in result.Warnings) {
        Console.Error.WriteLine($"  {warning}");
    }
}

// GetResult rather than GetValue: only one of the two commands was actually invoked, so reading a
// value off the other one is meaningless. Implicit:false because a bool option gets a result even
// when it never appeared on the command line, which would make everything verbose.
static bool IsVerbose(ParseResult parseResult, ExportCommand export, ImportCommand import) =>
    parseResult.GetResult(export.Verbose) is OptionResult { Implicit: false }
    || parseResult.GetResult(import.Verbose) is OptionResult { Implicit: false };
