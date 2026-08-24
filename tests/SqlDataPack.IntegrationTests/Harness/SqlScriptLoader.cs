using System.Reflection;

namespace SqlDataPack.IntegrationTests.Harness;

/// <summary>
/// Loads the embedded fixture scripts under <c>Fixtures/</c>.
/// <para>
/// A fixture file may be split into named sections with marker lines of the form
/// <c>-- @@SECTION &lt;name&gt;</c>, and each section may be split into a DDL half and a seed half with a
/// <c>-- @@SEED</c> marker line. That is what lets one file hold ten temporal pairs, or one table per
/// type hazard, without every test having to deploy all of it -- and what lets a target variant be built
/// from the source's own DDL instead of a hand-typed copy.
/// </para>
/// </summary>
internal static class SqlScriptLoader {
    private const string SectionMarker = "-- @@SECTION";
    private const string SeedMarker = "-- @@SEED";

    /// <summary>Whole file, markers and all.</summary>
    public static string LoadEmbeddedScript(string fileName) {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().Single(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// One named section of a fixture file. Throws when the file has no such section, or when the section
    /// exists but carries nothing executable -- a silently empty section makes a test pass having created
    /// nothing.
    /// </summary>
    public static string LoadSection(string fileName, string sectionName) {
        var sections = ParseSections(fileName);

        if (!sections.TryGetValue(sectionName, out var body)) {
            var available = sections.Count == 0
                ? "the file contains no '-- @@SECTION <name>' marker lines"
                : "available sections: " + string.Join(", ", sections.Keys);
            throw new InvalidOperationException($"Fixture '{fileName}' has no section '{sectionName}' ({available}).");
        }

        if (!HasExecutableText(body)) {
            throw new InvalidOperationException($"Fixture '{fileName}' section '{sectionName}' is empty (comments and whitespace only).");
        }

        return body;
    }

    /// <summary>
    /// The DDL half of a section: everything before its <c>-- @@SEED</c> marker. Pass
    /// <paramref name="sectionName"/> as <see langword="null"/> for a fixture that is not split into sections.
    /// This is what builds an unseeded target out of the source's own current DDL.
    /// </summary>
    public static string LoadDdl(string fileName, string? sectionName = null) {
        var section = sectionName is null ? WholeFile(fileName) : LoadSection(fileName, sectionName);
        var ddl = SplitOnSeedMarker(section).Ddl;

        if (!HasExecutableText(ddl)) {
            throw new InvalidOperationException($"Fixture '{fileName}'{Describe(sectionName)} has no DDL before its '{SeedMarker}' marker.");
        }

        return ddl;
    }

    /// <summary>
    /// The seed half of a section: everything after its <c>-- @@SEED</c> marker. Returns an empty string when
    /// the section has no seed marker -- a target-only fixture legitimately has no rows.
    /// </summary>
    public static string LoadSeed(string fileName, string? sectionName = null) {
        var section = sectionName is null ? WholeFile(fileName) : LoadSection(fileName, sectionName);
        return SplitOnSeedMarker(section).Seed;
    }

    /// <summary>Section names in file order. Useful for asserting a fixture still carries what a test needs.</summary>
    public static IReadOnlyList<string> SectionNames(string fileName) {
        return ParseSections(fileName).Keys.ToArray();
    }

    /// <summary>
    /// Splits a script into batches on lines that are exactly <c>GO</c>, the way sqlcmd and SSMS do.
    /// Whitespace-only batches are dropped. Everything that has to run in its own batch -- CREATE SCHEMA,
    /// CREATE LOGIN / CREATE USER / DENY sequences, an ALTER DATABASE that must land before the statements
    /// that depend on it -- relies on this.
    /// </summary>
    public static IReadOnlyList<string> SplitBatches(string sql) {
        var batches = new List<string>();
        var current = new List<string>();

        foreach (var line in sql.Split('\n')) {
            if (IsBatchSeparator(line)) {
                AddBatch(batches, current);
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        AddBatch(batches, current);
        return batches;
    }

    private static bool IsBatchSeparator(string line) {
        return line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddBatch(List<string> batches, List<string> lines) {
        var batch = string.Join("\n", lines).Trim();
        if (batch.Length > 0) {
            batches.Add(batch);
        }
    }

    private static string WholeFile(string fileName) {
        var text = LoadEmbeddedScript(fileName);
        if (!HasExecutableText(text)) {
            throw new InvalidOperationException($"Fixture '{fileName}' is empty (comments and whitespace only).");
        }

        return text;
    }

    private static Dictionary<string, string> ParseSections(string fileName) {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = LoadEmbeddedScript(fileName);

        string? currentName = null;
        var current = new List<string>();

        foreach (var line in text.Split('\n')) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(SectionMarker, StringComparison.OrdinalIgnoreCase)) {
                if (currentName is not null) {
                    sections[currentName] = string.Join("\n", current).Trim();
                }

                currentName = trimmed[SectionMarker.Length..].Trim();
                if (currentName.Length == 0) {
                    throw new InvalidOperationException($"Fixture '{fileName}' has a '{SectionMarker}' marker with no name.");
                }

                if (sections.ContainsKey(currentName)) {
                    throw new InvalidOperationException($"Fixture '{fileName}' declares section '{currentName}' more than once.");
                }

                // Reserve the slot so duplicate detection works before the section body is collected.
                sections[currentName] = string.Empty;
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        if (currentName is not null) {
            sections[currentName] = string.Join("\n", current).Trim();
        }

        return sections;
    }

    private static (string Ddl, string Seed) SplitOnSeedMarker(string section) {
        var ddl = new List<string>();
        var seed = new List<string>();
        var inSeed = false;

        foreach (var line in section.Split('\n')) {
            if (!inSeed && line.Trim().StartsWith(SeedMarker, StringComparison.OrdinalIgnoreCase)) {
                inSeed = true;
                continue;
            }

            (inSeed ? seed : ddl).Add(line);
        }

        return (string.Join("\n", ddl).Trim(), string.Join("\n", seed).Trim());
    }

    private static bool HasExecutableText(string script) {
        return script.Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal));
    }

    private static string Describe(string? sectionName) {
        return sectionName is null ? string.Empty : $" section '{sectionName}'";
    }
}
