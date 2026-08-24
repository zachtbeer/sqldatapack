#:package NuGet.Versioning@7.6.0

// Pre-flight checks for a release version. Run this before tagging:
//
//     dotnet run build/VersionGuard.cs -- 1.0.0-rc.13
//
// The Release workflow runs the same command, so a version that passes here is a
// version that will publish. Everything it checks used to be a step the maintainer
// had to remember; see .github/AGENTS.md for the two incidents that motivated it.

using System.Globalization;
using System.Text.RegularExpressions;
using NuGet.Versioning;

const string PackageId = "SqlDataPack";

// The official SemVer 2.0.0 regex from semver.org. NuGetVersion.Parse is more
// permissive than the spec (it accepts "1.0" and four-part "1.0.0.0"), so the
// version is validated against this first and only compared with NuGetVersion.
const string SemVerPattern =
    @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)"
    + @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
    + @"(?:\+(?<buildmetadata>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$";

// --no-git-check is a flag; the single positional argument is the version.
var arguments = new List<string>();
var skipGitCheck = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--no-git-check":
            skipGitCheck = true;
            break;
        default:
            arguments.Add(args[i]);
            break;
    }
}

if (arguments.Count != 1)
{
    Console.Error.WriteLine("usage: dotnet run build/VersionGuard.cs -- <version> [--no-git-check]");
    Console.Error.WriteLine("       <version> has no leading 'v', for example 1.0.0-rc.13");
    return 64;
}

var repoRoot = FindRepositoryRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("FAIL  Not inside a git repository.");
    return 1;
}

var raw = arguments[0].TrimStart('v', 'V');
var failures = new List<string>();
var warnings = new List<string>();

Console.WriteLine($"Checking {PackageId} {raw}");
Console.WriteLine();

// 1. The version is a well-formed SemVer 2.0.0 version.
var match = Regex.Match(raw, SemVerPattern, RegexOptions.ExplicitCapture);
if (!match.Success)
{
    Report(false, "Valid SemVer 2.0.0", $"'{raw}' is not a SemVer 2.0.0 version.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Nothing else could be checked. See https://semver.org/spec/v2.0.0.html.");
    return 1;
}

var version = NuGetVersion.Parse(raw);
var isPrerelease = version.IsPrerelease;
Report(true, "Valid SemVer 2.0.0", isPrerelease ? "prerelease" : "stable release");

// 2. The version sorts strictly above everything already on NuGet.org.
//
// This is the check that both published incidents needed. SemVer compares numeric
// prerelease identifiers numerically, so 1.0.0-rc.2 sorts BELOW 1.0.0-rc.10, and
// nuget.org supports no permanent deletion, so getting this wrong is forever.
var published = await FetchPublishedVersionsAsync();
NuGetVersion? newestPublished = null;

if (published is null)
{
    warnings.Add("Could not reach nuget.org, so the version was not compared against what is published.");
    Report(null, "Sorts above published versions", "skipped, nuget.org unreachable");
}
else if (published.Count == 0)
{
    Report(true, "Sorts above published versions", "nothing published yet");
}
else
{
    newestPublished = published.Max()!;

    if (published.Contains(version))
    {
        Report(false, "Sorts above published versions", $"{raw} is already published. Versions cannot be replaced on nuget.org.");
    }
    else if (version <= newestPublished)
    {
        Report(false, "Sorts above published versions", $"{raw} sorts below the published {newestPublished}. Restore would keep resolving to {newestPublished}.");
        Console.Error.WriteLine($"        Next version above {newestPublished}: {SuggestNext(newestPublished)}");
    }
    else
    {
        Report(true, "Sorts above published versions", $"newest published is {newestPublished}");
    }
}

// 3. The tag is on HEAD, so the release workflow will resolve this exact version.
if (skipGitCheck)
{
    Report(null, "Tag is on HEAD", "skipped (--no-git-check)");
}
else
{
    var (exitCode, described) = RunGit(repoRoot, "describe", "--tags", "--exact-match", "HEAD");
    var expected = "v" + raw;

    if (exitCode != 0)
    {
        Report(false, "Tag is on HEAD", $"HEAD carries no tag. Expected {expected}.");
        Console.Error.WriteLine($"        The release workflow resolves the version from the tag, so create it:  git tag {expected}");
    }
    else if (!described.Split('\n').Select(t => t.Trim()).Contains(expected))
    {
        Report(false, "Tag is on HEAD", $"HEAD is tagged '{described.Trim()}', not '{expected}'.");
    }
    else
    {
        Report(true, "Tag is on HEAD", expected);
    }
}

// Hand the results to the workflow.
var githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
if (!string.IsNullOrEmpty(githubOutput))
{
    File.AppendAllLines(githubOutput,
    [
        $"version={raw}",
        $"prerelease={(isPrerelease ? "true" : "false")}",
    ]);
}

Console.WriteLine();
foreach (var warning in warnings)
{
    Console.WriteLine($"WARN  {warning}");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failures.Count} check(s) failed. Nothing was published.");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"{raw} is ready to release. Push the tag to start the Release workflow:");
Console.WriteLine();
Console.WriteLine($"    git tag -a v{raw} -m \"v{raw}\"");
Console.WriteLine($"    git push origin v{raw}");
return 0;

void Report(bool? ok, string check, string detail)
{
    var label = ok switch { true => "OK  ", false => "FAIL", _ => "SKIP" };
    var writer = ok == false ? Console.Error : Console.Out;
    writer.WriteLine($"{label}  {check,-34}  {detail}");

    if (ok == false)
    {
        failures.Add(check);
    }
}

static string? FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName;
}

static async Task<HashSet<NuGetVersion>?> FetchPublishedVersionsAsync()
{
    var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId.ToLowerInvariant()}/index.json";

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(url);

        // A package that has never been published has no index document.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(v => NuGetVersion.Parse(v.GetString()!))
            .ToHashSet();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return null;
    }
}

// The next version that sorts above `previous`, for the error message. Numeric
// prerelease identifiers increment; anything else falls back to a patch bump.
static string SuggestNext(NuGetVersion previous)
{
    var labels = previous.ReleaseLabels.ToArray();

    if (labels.Length > 0 && int.TryParse(labels[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var last))
    {
        labels[^1] = (last + 1).ToString(CultureInfo.InvariantCulture);
        return $"{previous.Major}.{previous.Minor}.{previous.Patch}-{string.Join('.', labels)}";
    }

    return previous.IsPrerelease
        ? $"{previous.Major}.{previous.Minor}.{previous.Patch}"
        : $"{previous.Major}.{previous.Minor}.{previous.Patch + 1}";
}

static (int ExitCode, string Output) RunGit(string workingDirectory, params string[] arguments)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo("git")
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = System.Diagnostics.Process.Start(startInfo)!;
    var output = process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit();

    return (process.ExitCode, output);
}
