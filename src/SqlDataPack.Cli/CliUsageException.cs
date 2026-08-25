namespace SqlDataPack.Cli;

/// <summary>
/// Thrown when the command line or the options file is wrong. Reported as a plain message
/// and <see cref="ExitCodes.UsageError"/>, never as a stack trace.
/// </summary>
internal sealed class CliUsageException : Exception {
    public CliUsageException(string message) : base(message) {
    }
}
