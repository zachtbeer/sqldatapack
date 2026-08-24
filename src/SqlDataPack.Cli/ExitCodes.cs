namespace SqlDataPack.Cli;

/// <summary>
/// Process exit codes. CI is an obvious consumer, so these are part of the tool's contract.
/// </summary>
internal static class ExitCodes {
    /// <summary>The operation completed.</summary>
    public const int Success = 0;

    /// <summary>SqlDataPack rejected the operation. The message is printed without a stack trace.</summary>
    public const int OperationFailed = 1;

    /// <summary>The command line or the options file was wrong.</summary>
    public const int UsageError = 2;

    /// <summary>Anything else. The stack trace is printed only with --verbose.</summary>
    public const int Unexpected = 3;

    /// <summary>Cancelled with Ctrl+C. 128 + SIGINT, which is what shells expect.</summary>
    public const int Cancelled = 130;
}
