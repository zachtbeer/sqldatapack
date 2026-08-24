using System.Diagnostics;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Fuzzing;

/// <summary>
/// Fuzzes the identifier parsing and wildcard matching that run over hand-typed CLI arguments and
/// config values. These must be total functions: terminate within the declared timeout and either
/// succeed or throw a documented <see cref="SqlDataPackException"/>.
/// </summary>
public sealed class IdentifierFuzzTests {
    /// <summary>sysname caps a schema or table name at 128 chars, so patterns are generated up to that length.</summary>
    private const int SysnameLimit = 128;

    /// <summary>
    /// Derived from the production timeout, so a regression that creeps up to it still fails. The headroom is a
    /// full second because the fuzz classes run in parallel and this is wall clock, not CPU time.
    /// </summary>
    private static readonly TimeSpan TerminationBound = SqlDataPackIdentifier.PatternMatchTimeout + TimeSpan.FromSeconds(1);

    // 'a' rides along with the metacharacters so soup can partially match a run of a's before failing.
    private static readonly char[] PatternAlphabet = ['*', '?', '[', ']', '(', ')', '{', '}', '+', '|', '\\', '.', '^', '$', 'a'];

    // 'q' never appears in a subject, so a tail carrying one forces the whole chain of '.*' to backtrack
    // before the match fails. The other two tails match, which keeps the succeeding path in the sample.
    private static readonly string[] PatternTails = ["", "q", "*q", "a"];

    private static readonly char[] RunCharacters = ['a', 'A'];

    private static readonly char[] OffAlphabetCharacters = ['_', '0', 'z'];

    private static readonly Gen<string> MetacharacterSoup =
        from length in Gen.Choose(1, SysnameLimit)
        from chars in Gen.ArrayOf(Gen.Elements(PatternAlphabet), length)
        select new string(chars);

    // Every '*' becomes a '.*', so a long run is a long chain of them.
    private static readonly Gen<string> StarRun =
        from stars in Gen.Choose(2, SysnameLimit - 1)
        from tail in Gen.Elements(PatternTails)
        select new string('*', stars) + tail;

    private static readonly Gen<string> AlternatingStars =
        from repeats in Gen.Choose(2, SysnameLimit / 2)
        from tail in Gen.Elements(PatternTails)
        select string.Concat(Enumerable.Repeat("a*", repeats)) + tail;

    // Production escapes everything but '*', so these parens stay literal and the match dies on the first
    // character rather than nesting anything. That is what makes it worth generating: it is the shape a
    // regex-injection attempt actually takes, and it has to stay cheap.
    private static readonly Gen<string> LiteralGroupsWithStars =
        from depth in Gen.Choose(1, SysnameLimit / 6)
        select string.Concat(Enumerable.Repeat("(a*", depth)) + string.Concat(Enumerable.Repeat(")*", depth));

    private static readonly Gen<string> AdversarialPattern = Gen.Frequency(new[] {
        (3, StarRun),
        (3, AlternatingStars),
        (2, LiteralGroupsWithStars),
        (3, MetacharacterSoup),
    });

    // Catastrophic backtracking needs a subject of one repeated character; a varied name fails fast
    // and proves nothing. The off-alphabet characters keep the fast-fail path in the sample too.
    private static readonly Gen<TableName> RepeatedCharacterTable =
        from ch in Gen.Frequency(new[] { (4, Gen.Elements(RunCharacters)), (1, Gen.Elements(OffAlphabetCharacters)) })
        from schemaLength in Gen.Choose(1, 32)
        from nameLength in Gen.Choose(1, 96)
        select new TableName(new string(ch, schemaLength), new string(ch, nameLength));

    /// <summary>
    /// A table pattern reaches this from a config file or a CLI argument. If one can hang, it hangs
    /// the whole export.
    /// </summary>
    [FuzzProperty]
    public Property MatchesPattern_AdversarialPatterns_TerminateWithinBound() {
        var gen = from table in RepeatedCharacterTable from pattern in AdversarialPattern select (table, pattern);

        return Prop.ForAll(gen.ToArbitrary(), x => {
            Exception? thrown = null;
            var stopwatch = Stopwatch.StartNew();
            try {
                SqlDataPackIdentifier.MatchesPattern(x.table, x.pattern);
            }
            catch (Exception ex) {
                thrown = ex;
            }

            stopwatch.Stop();
            stopwatch.Elapsed.ShouldBeLessThan(TerminationBound, $"pattern '{Describe(x.pattern)}' against '{x.table.FullName}' outran the declared {SqlDataPackIdentifier.PatternMatchTimeout} match timeout");

            if (thrown is null) {
                return true;
            }

            // A timeout is a legitimate outcome, but only as the documented exception naming the
            // offending pattern. A raw RegexMatchTimeoutException reaching the caller is a bug.
            var failure = thrown.ShouldBeOfType<SqlDataPackException>($"pattern '{Describe(x.pattern)}' surfaced an undocumented {thrown.GetType().Name}: {thrown.Message}");
            failure.Message.ShouldContain(x.pattern);
            failure.InnerException.ShouldBeOfType<RegexMatchTimeoutException>().MatchTimeout.ShouldBe(SqlDataPackIdentifier.PatternMatchTimeout);
            return true;
        });
    }

    /// <summary>One string entry point, named so a failure says which one broke.</summary>
    private sealed record StringEntryPoint(string Name, Func<string, object> Call);

    private static readonly StringEntryPoint[] SingleStringEntryPoints = [
        new("ParseColumnPath", value => SqlDataPackIdentifier.ParseColumnPath(value)),
        new("NormalizeSqliteDataTablePrefix", value => SqlDataPackIdentifier.NormalizeSqliteDataTablePrefix(value)),
        new("QuoteSqlServerName", value => SqlDataPackIdentifier.QuoteSqlServerName(value)),
        new("QuoteSqliteName", value => SqlDataPackIdentifier.QuoteSqliteName(value)),
        new("ToSqliteDataTableName", value => SqlDataPackIdentifier.ToSqliteDataTableName(new TableName(value, value))),
    ];

    private static readonly string[] AwkwardFragments = [
        "", " ", "\t", "\r\n", "\0", ".", "..", "dbo", "Customers", "Id",
        "[", "]", "]]", "\"", "\"\"", "'", "`",
        Units(0x0001), Units(0x007f), Units(0x00a0), Units(0x200b),
        Units(0xd83d, 0xde00), // U+1F600, a 4-byte code point
        Units(0xd83d), // lone high surrogate
        Units(0xde00), // lone low surrogate
    ];

    private static readonly Gen<string> AwkwardText =
        from count in Gen.Choose(0, 6)
        from parts in Gen.ArrayOf(Gen.Elements(AwkwardFragments), count)
        select string.Concat(parts);

    private static readonly Gen<string> HandTypedValue = Gen.Frequency(new[] { (2, Fuzz.Garbage), (3, AwkwardText) });

    /// <summary>
    /// Every one of these takes a value a person typed. An unhandled index or null-reference error
    /// here crashes the operation instead of reporting a bad option. Add a row when a new string
    /// entry point appears.
    /// </summary>
    [FuzzProperty]
    public Property SingleStringEntryPoints_ReturnOrThrowSqlDataPackException() {
        var gen = from entry in Gen.Elements(SingleStringEntryPoints) from value in HandTypedValue select (entry, value);

        return Prop.ForAll(gen.ToArbitrary(), x => {
            var thrown = Record.Exception(() => {
                x.entry.Call(x.value);
            });

            if (thrown is not null) {
                thrown.ShouldBeOfType<SqlDataPackException>($"{x.entry.Name}(\"{Describe(x.value)}\") threw an undocumented {thrown.GetType().Name}: {thrown.Message}");
            }

            return true;
        });
    }

    private static string Units(params int[] codeUnits) => new(codeUnits.Select(unit => (char)unit).ToArray());

    /// <summary>Renders control characters and surrogates so a failure message is readable.</summary>
    private static string Describe(string value) => string.Concat(value.Select(ch => ch is >= ' ' and <= '~' ? ch.ToString() : "\\u" + ((int)ch).ToString("X4")));
}
