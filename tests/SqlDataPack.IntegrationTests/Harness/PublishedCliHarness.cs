using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SqlDataPack.IntegrationTests.Harness;

/// <summary>
/// Publishes SqlDataPack.Cli exactly the way a release does -- self-contained, single file -- and
/// runs the resulting executable.
/// <para>
/// This exists because the thing most likely to break the shipped binary cannot be reached by
/// running the CLI's own assemblies. DacFx resolves paths from <c>Assembly.Location</c>, which is an
/// empty string for an assembly bundled into a single file, so a plain <c>PublishSingleFile</c>
/// build throws "Could not save package to file. The path is empty." from
/// <c>DacPackageExtensions.BuildPackage</c>. Every unit test still passes, and only the downloaded
/// and winget builds are broken, on the dacpac path alone. <c>IncludeAllContentForSelfExtract</c> in
/// SqlDataPack.Cli.csproj is what prevents it.
/// </para>
/// <para>
/// The publish takes a minute or so and is done once per test run.
/// </para>
/// </summary>
internal static class PublishedCliHarness {
    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static string? publishedExecutablePath;
    private static string? publishOutputDirectory;

    public static async Task<string> GetExecutableAsync(CancellationToken cancellationToken = default) {
        if (publishedExecutablePath is not null) {
            return publishedExecutablePath;
        }

        await PublishGate.WaitAsync(cancellationToken);
        try {
            publishedExecutablePath ??= await PublishAsync(cancellationToken);
            return publishedExecutablePath;
        }
        finally {
            PublishGate.Release();
        }
    }

    /// <summary>
    /// The directory the single-file build was published into, so a test can assert that it really
    /// is one file.
    /// </summary>
    public static async Task<string> GetOutputDirectoryAsync(CancellationToken cancellationToken = default) {
        await GetExecutableAsync(cancellationToken);
        return publishOutputDirectory!;
    }

    private static async Task<string> PublishAsync(CancellationToken cancellationToken) {
        string repositoryRoot = FindRepositoryRoot();
        string project = Path.Combine(repositoryRoot, "src", "SqlDataPack.Cli", "SqlDataPack.Cli.csproj");
        string output = Path.Combine(Path.GetTempPath(), $"zsdp-cli-{Guid.NewGuid():N}");

        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };

        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(RuntimeInformation.RuntimeIdentifier);
        startInfo.ArgumentList.Add("--self-contained");
        startInfo.ArgumentList.Add("true");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(output);

        CliRunResult publish = await RunAsync(startInfo, TimeSpan.FromMinutes(10), cancellationToken);
        if (publish.ExitCode != 0) {
            throw new InvalidOperationException($"Publishing the CLI failed with exit code {publish.ExitCode}.{Environment.NewLine}{publish.StandardOutput}{Environment.NewLine}{publish.StandardError}");
        }

        string executable = Path.Combine(output, OperatingSystem.IsWindows() ? "SqlDataPack.Cli.exe" : "SqlDataPack.Cli");
        if (!File.Exists(executable)) {
            throw new InvalidOperationException($"Publish produced no executable at {executable}. Directory holds: {string.Join(", ", Directory.GetFiles(output).Select(Path.GetFileName))}");
        }

        publishOutputDirectory = output;
        return executable;
    }

    public static async Task<CliRunResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default) {
        string executable = await GetExecutableAsync(cancellationToken);

        var startInfo = new ProcessStartInfo(executable) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, TimeSpan.FromMinutes(10), cancellationToken);
    }

    private static async Task<CliRunResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken) {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{startInfo.FileName}' did not exit within {timeout}.");
        }

        return new CliRunResult(process.ExitCode, await standardOutput, await standardError);
    }

    /// <summary>
    /// Walks up from the test assembly looking for the solution file. Test binaries sit several
    /// directories deep and the depth differs between local runs and CI.
    /// </summary>
    private static string FindRepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "SqlDataPack.slnx"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find SqlDataPack.slnx walking up from {AppContext.BaseDirectory}.");
    }
}

internal sealed record CliRunResult(int ExitCode, string StandardOutput, string StandardError) {
    public string AllOutput => $"{this.StandardOutput}{Environment.NewLine}{this.StandardError}";
}
