using System.CommandLine;
using System.CommandLine.Parsing;

namespace SqlDataPack.Cli.Commands;

/// <summary>
/// Pieces both verbs need: resolving the connection string, and turning loose flag text into
/// the shapes the library's options objects want.
/// </summary>
internal static class CommandSupport {
    public const string ConnectionEnvironmentVariable = "SQLDATAPACK_CONNECTION";

    public static Option<string> CreateConnectionOption() => new("--connection", "-c") {
        Description = $"SQL Server connection string. Falls back to the {ConnectionEnvironmentVariable} environment variable, which keeps it out of shell history."
    };

    public static Option<FileInfo> CreateOptionsFileOption() => new("--options") {
        Description = "JSON file describing everything the flags do not cover. Explicit flags win over the file. It must not contain a connection string."
    };

    public static Option<int?> CreateBatchSizeOption() => new("--batch-size") {
        Description = "Rows per batch. Defaults to 1,000."
    };

    public static Option<bool> CreateQuietOption() => new("--quiet", "-q") {
        Description = "Suppress per-table progress. Warnings and errors are still reported."
    };

    public static Option<bool> CreateVerboseOption() => new("--verbose") {
        Description = "Print a stack trace when the tool fails unexpectedly."
    };

    /// <summary>
    /// Was the option actually typed? Anything absent must not overwrite a value the options file set.
    /// <para>
    /// The Implicit check is the whole point. An option carrying a default -- every bool flag -- still
    /// gets an OptionResult when it never appeared on the command line, so a plain null check reads
    /// "not typed" as "typed false" and quietly undoes the options file.
    /// </para>
    /// </summary>
    public static bool WasSpecified(ParseResult parseResult, Option option) => parseResult.GetResult(option) is OptionResult { Implicit: false };

    public static string ResolveConnectionString(ParseResult parseResult, Option<string> connectionOption) {
        var connection = parseResult.GetValue(connectionOption);
        if (!string.IsNullOrWhiteSpace(connection)) {
            return connection;
        }

        connection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(connection)) {
            return connection;
        }

        throw new CliUsageException($"No connection string. Pass --connection or set {ConnectionEnvironmentVariable}.");
    }

    /// <summary>
    /// Accepts both repeated flags and comma-separated values, because people reach for either.
    /// </summary>
    public static List<string> SplitList(IEnumerable<string>? values) {
        List<string> result = [];
        if (values is null) {
            return result;
        }

        foreach (var value in values) {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                result.Add(part);
            }
        }

        return result;
    }

    /// <summary>
    /// Splits "key:predicate" on the first colon only, so a predicate may contain one (a time
    /// literal, say) without needing to be escaped.
    /// </summary>
    public static (string Key, string Predicate) SplitKeyedPredicate(string value, string flagName, string keyDescription) {
        var separator = value.IndexOf(':');
        if (separator <= 0) {
            throw new CliUsageException($"{flagName} expects \"{keyDescription}:predicate\", for example {flagName} \"CustomerId:CustomerId = 42\". Got: {value}");
        }

        var key = value[..separator].Trim();
        var predicate = value[(separator + 1)..].Trim();

        if (key.Length == 0 || predicate.Length == 0) {
            throw new CliUsageException($"{flagName} expects \"{keyDescription}:predicate\", with something either side of the colon. Got: {value}");
        }

        return (key, predicate);
    }
}
