// Computes the next version from the last release tag and a bump level.
//
//     dotnet run build/NextVersion.cs -- --from v1.2.3 --bump minor    ->  1.3.0
//     dotnet run build/NextVersion.cs -- --from "" --bump patch        ->  0.0.1
//     dotnet run build/NextVersion.cs -- --self-test
//
// Prints the version and nothing else, so a workflow can capture stdout directly.
// An empty --from means nothing has been released yet and is a supported input, not
// an error. Prereleases are rejected: the automatic path only ever produces X.Y.Z,
// and a preview is tagged by hand. See docs/RELEASE.md.

using System.Globalization;
using System.Text.RegularExpressions;

const string SeedVersion = "0.0.0";

if (args.Contains("--self-test"))
{
    return SelfTest();
}

string? from = null;
string? bump = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--from" when i + 1 < args.Length:
            from = args[++i];
            break;
        case "--bump" when i + 1 < args.Length:
            bump = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unrecognised argument '{args[i]}'");
            return 64;
    }
}

if (bump is null)
{
    Console.Error.WriteLine("usage: dotnet run build/NextVersion.cs -- --from <tag> --bump <major|minor|patch>");
    Console.Error.WriteLine("       dotnet run build/NextVersion.cs -- --self-test");
    Console.Error.WriteLine("       --from may be empty, meaning nothing has been released yet");
    return 64;
}

try
{
    Console.WriteLine(Next(from ?? string.Empty, bump));
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static string Next(string from, string bump)
{
    if (bump is not ("major" or "minor" or "patch"))
    {
        throw new ArgumentException($"--bump must be major, minor or patch, not '{bump}'.");
    }

    // No tag yet means this is the first release, and it bumps from 0.0.0 like any
    // other: patch gives 0.0.1, minor 0.1.0, major 1.0.0. The label decides.
    var raw = string.IsNullOrWhiteSpace(from)
        ? SeedVersion
        : from.Trim().TrimStart('v', 'V');

    var match = Regex.Match(
        raw,
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$",
        RegexOptions.ExplicitCapture);

    if (!match.Success)
    {
        throw new ArgumentException(
            $"'{from}' is not a stable three-part version. The automatic release path does not "
            + "bump prereleases; tag those by hand.");
    }

    var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
    var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
    var patch = int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture);

    return bump switch
    {
        "major" => $"{major + 1}.0.0",
        "minor" => $"{major}.{minor + 1}.0",
        _ => $"{major}.{minor}.{patch + 1}",
    };
}

static int SelfTest()
{
    (string From, string Bump, string Expected)[] accepted =
    [
        ("v1.2.3", "patch", "1.2.4"),
        ("v1.2.3", "minor", "1.3.0"),
        ("v1.2.3", "major", "2.0.0"),
        ("1.2.3", "patch", "1.2.4"),
        ("v0.9.9", "minor", "0.10.0"),
        ("v1.9.0", "major", "2.0.0"),
        ("v10.0.0", "patch", "10.0.1"),
        ("v1.2.9", "patch", "1.2.10"),
        ("", "patch", "0.0.1"),
        ("", "minor", "0.1.0"),
        ("", "major", "1.0.0"),
        ("   ", "minor", "0.1.0"),
    ];

    (string From, string Bump)[] rejected =
    [
        ("v1.0.0-preview.13", "patch"),
        ("v1.0.0+build.1", "patch"),
        ("v1.0", "patch"),
        ("v1.0.0.0", "patch"),
        ("v01.0.0", "patch"),
        ("not-a-version", "patch"),
        ("v1.2.3", "none"),
        ("v1.2.3", "Major"),
        ("v1.2.3", ""),
    ];

    var failures = 0;

    foreach (var (from, bump, expected) in accepted)
    {
        string actual;

        try
        {
            actual = Next(from, bump);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"FAIL  ('{from}', '{bump}') threw: {exception.Message}");
            failures++;
            continue;
        }

        if (actual == expected)
        {
            Console.WriteLine($"ok    ('{from}', '{bump}') -> {actual}");
        }
        else
        {
            Console.Error.WriteLine($"FAIL  ('{from}', '{bump}') -> {actual}, expected {expected}");
            failures++;
        }
    }

    foreach (var (from, bump) in rejected)
    {
        try
        {
            var actual = Next(from, bump);
            Console.Error.WriteLine($"FAIL  ('{from}', '{bump}') -> {actual}, expected a rejection");
            failures++;
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"ok    ('{from}', '{bump}') rejected");
        }
    }

    Console.WriteLine();

    if (failures > 0)
    {
        Console.Error.WriteLine($"{failures} case(s) failed.");
        return 1;
    }

    Console.WriteLine($"{accepted.Length + rejected.Length} cases passed.");
    return 0;
}
