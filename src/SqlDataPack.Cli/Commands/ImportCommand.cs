using System.CommandLine;
using System.CommandLine.Parsing;
using SqlDataPack.Models;

namespace SqlDataPack.Cli.Commands;

/// <summary>
/// The result of turning an <c>import</c> command line into something the library can run.
/// </summary>
internal sealed record ImportRequest(string PackagePath, string ConnectionString, ImportOptions Options, bool Quiet);

/// <summary>
/// <c>sqldatapack import</c>.
/// </summary>
internal sealed class ImportCommand : Command {
    public ImportCommand() : base("import", "Import a SQLite package produced by export back into SQL Server.") {
        this.Connection = CommandSupport.CreateConnectionOption();
        this.OptionsFilePath = CommandSupport.CreateOptionsFileOption();
        this.BatchSize = CommandSupport.CreateBatchSizeOption();
        this.Quiet = CommandSupport.CreateQuietOption();
        this.Verbose = CommandSupport.CreateVerboseOption();

        this.Package = new Argument<string>("package") {
            Description = "The SQLite package to import."
        };

        // See the note in ExportCommand: none|dacpac is the flag vocabulary on both verbs, and it
        // does not line up with the enum member names.
        this.DeploySchema = new Option<string>("--deploy-schema") {
            Description = "Deploy the dacpac embedded in the package before loading data. none (default) or dacpac. Requires the package to have been exported with --schema dacpac.",
            HelpName = "none|dacpac"
        };
        this.DeploySchema.AcceptOnlyFromAmong("none", "dacpac");

        this.RowCountDrift = new Option<string>("--row-count-drift") {
            Description = "What to do when the package holds a different number of rows than the export recorded, which is what editing the file does. warn (default) imports what is there; fail rejects the package before writing anything.",
            HelpName = "warn|fail"
        };
        this.RowCountDrift.AcceptOnlyFromAmong("warn", "fail");

        this.Timeout = new Option<int?>("--timeout") {
            Description = "Bulk copy timeout in seconds. The separate validation timeout is available in the options file."
        };

        this.Add(this.Package);
        this.Add(this.Connection);
        this.Add(this.DeploySchema);
        this.Add(this.RowCountDrift);
        this.Add(this.BatchSize);
        this.Add(this.Timeout);
        this.Add(this.OptionsFilePath);
        this.Add(this.Quiet);
        this.Add(this.Verbose);
    }

    public Argument<string> Package { get; }

    public Option<string> Connection { get; }

    public Option<string> DeploySchema { get; }

    public Option<string> RowCountDrift { get; }

    public Option<int?> BatchSize { get; }

    public Option<int?> Timeout { get; }

    public Option<FileInfo> OptionsFilePath { get; }

    public Option<bool> Quiet { get; }

    public Option<bool> Verbose { get; }

    /// <summary>
    /// Builds the library options from the parsed command line, with the options file as the
    /// starting point and explicit flags overwriting it.
    /// </summary>
    public ImportRequest Bind(ParseResult parseResult) {
        var optionsFile = parseResult.GetValue(this.OptionsFilePath);
        var options = optionsFile is null
            ? new ImportOptions()
            : OptionsFile.LoadImportOptions(optionsFile.FullName);

        if (CommandSupport.WasSpecified(parseResult, this.DeploySchema)) {
            options.SchemaDeploymentMode = parseResult.GetValue(this.DeploySchema) switch {
                "dacpac" => SchemaDeploymentMode.DeployDacpac,
                _ => SchemaDeploymentMode.None
            };
        }

        if (CommandSupport.WasSpecified(parseResult, this.RowCountDrift)) {
            options.RowCountDrift = parseResult.GetValue(this.RowCountDrift) switch {
                "fail" => Models.RowCountDrift.Fail,
                _ => Models.RowCountDrift.Warn
            };
        }

        if (CommandSupport.WasSpecified(parseResult, this.BatchSize)) {
            options.BatchSize = RequirePositive(parseResult.GetValue(this.BatchSize), "--batch-size");
        }

        if (CommandSupport.WasSpecified(parseResult, this.Timeout)) {
            options.BulkCopyTimeout = RequirePositive(parseResult.GetValue(this.Timeout), "--timeout");
        }

        return new ImportRequest(
            parseResult.GetRequiredValue(this.Package),
            CommandSupport.ResolveConnectionString(parseResult, this.Connection),
            options,
            parseResult.GetValue(this.Quiet));
    }

    private static int RequirePositive(int? value, string flagName) {
        if (value is not { } number || number <= 0) {
            throw new CliUsageException($"{flagName} must be a positive number.");
        }

        return number;
    }
}
