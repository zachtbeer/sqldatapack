using System.CommandLine;
using System.CommandLine.Parsing;
using SqlDataPack.Models;

namespace SqlDataPack.Cli.Commands;

/// <summary>
/// The result of turning an <c>export</c> command line into something the library can run.
/// </summary>
internal sealed record ExportRequest(string ConnectionString, string OutputPath, ExportOptions Options, bool Quiet);

/// <summary>
/// <c>sqldatapack export</c>.
/// </summary>
internal sealed class ExportCommand : Command {
    public ExportCommand() : base("export", "Export a slice of a SQL Server database into a single SQLite file.") {
        this.Connection = CommandSupport.CreateConnectionOption();
        this.OptionsFilePath = CommandSupport.CreateOptionsFileOption();
        this.BatchSize = CommandSupport.CreateBatchSizeOption();
        this.Quiet = CommandSupport.CreateQuietOption();
        this.Verbose = CommandSupport.CreateVerboseOption();

        this.Output = new Option<string>("--out", "-o") {
            Description = "Path of the SQLite package to write.",
            Required = true
        };

        this.Tables = new Option<string[]>("--tables") {
            Description = "Only export these tables, as schema.Table. Repeat the flag or separate with commas.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore
        };

        this.ExcludeTables = new Option<string[]>("--exclude-tables") {
            Description = "Export every table except these. Cannot be combined with --tables.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore
        };

        this.ExcludeColumns = new Option<string[]>("--exclude-column") {
            Description = "Leave a column out of the package entirely, as schema.Table.Column. An excluded column is never read from SQL Server. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore
        };

        this.GlobalWhere = new Option<string[]>("--global-where") {
            Description = "Row filter applied to every exported table holding the named column, as \"Column:predicate\". A table without the column exports unfiltered with a warning. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore
        };

        this.TableWhere = new Option<string[]>("--table-where") {
            Description = "Row filter for one table, as \"schema.Table:predicate\". Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore
        };

        // Mapped by hand rather than parsed as the enum: the flag vocabulary is none|dacpac on both
        // verbs, while the enums spell it None|Dacpac and None|DeployDacpac.
        this.Schema = new Option<string>("--schema") {
            Description = "Capture the source schema as a dacpac alongside the data, so import can recreate it. none (default) or dacpac.",
            HelpName = "none|dacpac"
        };
        this.Schema.AcceptOnlyFromAmong("none", "dacpac");

        this.Overwrite = new Option<bool>("--overwrite") {
            Description = "Replace the output file if it already exists."
        };

        this.Timeout = new Option<int?>("--timeout") {
            Description = "SQL command timeout in seconds."
        };

        this.Add(this.Connection);
        this.Add(this.Output);
        this.Add(this.Tables);
        this.Add(this.ExcludeTables);
        this.Add(this.ExcludeColumns);
        this.Add(this.GlobalWhere);
        this.Add(this.TableWhere);
        this.Add(this.Schema);
        this.Add(this.Overwrite);
        this.Add(this.BatchSize);
        this.Add(this.Timeout);
        this.Add(this.OptionsFilePath);
        this.Add(this.Quiet);
        this.Add(this.Verbose);
    }

    public Option<string> Connection { get; }

    public Option<string> Output { get; }

    public Option<string[]> Tables { get; }

    public Option<string[]> ExcludeTables { get; }

    public Option<string[]> ExcludeColumns { get; }

    public Option<string[]> GlobalWhere { get; }

    public Option<string[]> TableWhere { get; }

    public Option<string> Schema { get; }

    public Option<bool> Overwrite { get; }

    public Option<int?> BatchSize { get; }

    public Option<int?> Timeout { get; }

    public Option<FileInfo> OptionsFilePath { get; }

    public Option<bool> Quiet { get; }

    public Option<bool> Verbose { get; }

    /// <summary>
    /// Builds the library options from the parsed command line. The options file supplies the
    /// starting point and explicit flags overwrite it, so a checked-in file can describe a slice
    /// while one run tweaks a single value.
    /// </summary>
    public ExportRequest Bind(ParseResult parseResult) {
        var optionsFile = parseResult.GetValue(this.OptionsFilePath);
        var options = optionsFile is null
            ? new ExportOptions()
            : OptionsFile.LoadExportOptions(optionsFile.FullName);

        var hasTables = CommandSupport.WasSpecified(parseResult, this.Tables);
        var hasExcludeTables = CommandSupport.WasSpecified(parseResult, this.ExcludeTables);

        if (hasTables && hasExcludeTables) {
            throw new CliUsageException("--tables and --exclude-tables select in opposite directions. Use one or the other.");
        }

        if (hasTables) {
            options.TableSelection = ExportTableSelectionMode.Only;
            options.Tables = CommandSupport.SplitList(parseResult.GetValue(this.Tables));
        }
        else if (hasExcludeTables) {
            options.TableSelection = ExportTableSelectionMode.AllExcept;
            options.Tables = CommandSupport.SplitList(parseResult.GetValue(this.ExcludeTables));
        }

        if (CommandSupport.WasSpecified(parseResult, this.ExcludeColumns)) {
            options.ExcludeColumns = CommandSupport.SplitList(parseResult.GetValue(this.ExcludeColumns));
        }

        if (CommandSupport.WasSpecified(parseResult, this.GlobalWhere)) {
            options.GlobalWhereClauses = BuildGlobalWhereClauses(parseResult.GetValue(this.GlobalWhere));
        }

        if (CommandSupport.WasSpecified(parseResult, this.TableWhere)) {
            options.PerTableWhereClauses = BuildPerTableWhereClauses(parseResult.GetValue(this.TableWhere));
        }

        if (CommandSupport.WasSpecified(parseResult, this.Schema)) {
            options.SchemaCaptureMode = parseResult.GetValue(this.Schema) switch {
                "dacpac" => SchemaCaptureMode.Dacpac,
                _ => SchemaCaptureMode.None
            };
        }

        if (CommandSupport.WasSpecified(parseResult, this.Overwrite)) {
            options.OverwriteExistingPackage = parseResult.GetValue(this.Overwrite);
        }

        if (CommandSupport.WasSpecified(parseResult, this.BatchSize)) {
            options.BatchSize = RequirePositive(parseResult.GetValue(this.BatchSize), "--batch-size");
        }

        if (CommandSupport.WasSpecified(parseResult, this.Timeout)) {
            options.CommandTimeout = RequirePositive(parseResult.GetValue(this.Timeout), "--timeout");
        }

        return new ExportRequest(
            CommandSupport.ResolveConnectionString(parseResult, this.Connection),
            parseResult.GetRequiredValue(this.Output),
            options,
            parseResult.GetValue(this.Quiet));
    }

    private static List<GlobalWhereClause> BuildGlobalWhereClauses(string[]? values) {
        List<GlobalWhereClause> clauses = [];
        foreach (var value in values ?? []) {
            (var columns, var predicate) = CommandSupport.SplitKeyedPredicate(value, "--global-where", "Column");
            var columnNames = CommandSupport.SplitList([columns]);
            if (columnNames.Count == 0) {
                throw new CliUsageException($"--global-where needs at least one column name before the colon. Got: {value}");
            }

            clauses.Add(new GlobalWhereClause(columnNames, predicate));
        }

        return clauses;
    }

    private static List<PerTableWhereClause> BuildPerTableWhereClauses(string[]? values) {
        List<PerTableWhereClause> clauses = [];
        foreach (var value in values ?? []) {
            (var table, var predicate) = CommandSupport.SplitKeyedPredicate(value, "--table-where", "schema.Table");
            clauses.Add(new PerTableWhereClause(table, predicate));
        }

        return clauses;
    }

    private static int RequirePositive(int? value, string flagName) {
        if (value is not { } number || number <= 0) {
            throw new CliUsageException($"{flagName} must be a positive number.");
        }

        return number;
    }
}
