using System.Globalization;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// One question per test: does a value survive SQL Server -&gt; SQLite -&gt; SQL Server unchanged? The target is
/// the source fixture's own DDL deployed unseeded, so the target shape cannot drift from the source it is
/// compared against. Every comparison runs in the engine over three-part names -- pulling both sides into .NET
/// and comparing strings there is what hides normalization and truncation damage.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class ValueFidelityRoundTripTests {
    private const string TypeVault = "type-vault.sql";

    private readonly SqlServerContainerFixture _fixture;

    public ValueFidelityRoundTripTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task RoundTrip_WideScalarTable_PreservesEveryValue() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVault);
        await using var sqlite = new SqliteTempFileHarness();

        // Both halves run under a comma-decimal locale. A ToString()/Parse() that forgot InvariantCulture
        // corrupts every decimal in every export on a non-US machine, and CI on en-US never sees it.
        var callerCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try {
            var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.LegacyImportRows"));
            exportResult.RowCount.ShouldBe(3);

            await using (var package = await sqlite.OpenConnectionAsync()) {
                // Storage side: decimal, numeric, money and smallmoney are text in SQLite, at full declared
                // scale, in invariant form -- not the locale's.
                (await package.ScalarStringAsync("SELECT typeof(NumericValue) FROM dbo__legacyimportrows WHERE LegacyImportRowId = 1")).ShouldBe("text");
                (await package.ScalarStringAsync("SELECT typeof(MoneyValue) FROM dbo__legacyimportrows WHERE LegacyImportRowId = 1")).ShouldBe("text");
                (await package.ScalarStringAsync("""
                                                 SELECT NumericValue || '|' || DecimalValue || '|' || DecimalTight || '|' ||
                                                        DecimalHighPrecision || '|' || MoneyValue || '|' || SmallMoneyValue
                                                 FROM dbo__legacyimportrows
                                                 WHERE LegacyImportRowId = 1
                                                 """)).ShouldBe("12345.6789|987654321.123456|0.99999|123456789012345678.9876543210|922337203685477.5807|214748.3647");

                (await package.ScalarStringAsync("SELECT GuidValue FROM dbo__legacyimportrows WHERE LegacyImportRowId = 1")).ShouldBe("6f9619ff-8b86-d011-b42d-00c04fc964ff");
                (await package.ScalarStringAsync("SELECT hex(BlobValue) FROM dbo__legacyimportrows WHERE LegacyImportRowId = 1")).ShouldBe("01020304");
                (await package.ScalarStringAsync("SELECT typeof(FlagValue) || ',' || FlagValue FROM dbo__legacyimportrows WHERE LegacyImportRowId = 1")).ShouldBe("integer,1");
            }

            var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);
            importResult.RowCount.ShouldBe(3);
        }
        finally {
            CultureInfo.CurrentCulture = callerCulture;
        }

        // Every column of every row, both directions, without naming a single column: a column added to the
        // fixture later is compared too.
        await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, "dbo.LegacyImportRows");

        // EXCEPT compares floats with '=', which cannot see a lost sign bit on zero or a dropped low bit.
        (await source.ScalarIntAsync($"""
                                      SELECT COUNT(*)
                                      FROM [{source.DatabaseName}].[dbo].[LegacyImportRows] s
                                      INNER JOIN [{target.DatabaseName}].[dbo].[LegacyImportRows] t ON t.LegacyImportRowId = s.LegacyImportRowId
                                      WHERE CONVERT(BINARY(8), s.FloatValue) <> CONVERT(BINARY(8), t.FloatValue)
                                         OR CONVERT(BINARY(4), s.RealValue) <> CONVERT(BINARY(4), t.RealValue)
                                      """)).ShouldBe(0);
    }

    [Fact]
    public async Task RoundTrip_TemporalScalarExtremes_PreservesEveryTick() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVault);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.ChronoExtremes"));
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.RowCount.ShouldBe(3);
        // The mismatch query inner-joins, so a dropped row would otherwise read as a clean pass.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.ChronoExtremes")).ShouldBe(3);

        var mismatches = await source.ReadStringsAsync(ByteMismatchSql(
            source,
            target,
            "[dbo].[ChronoExtremes]",
            "ChronoExtremeId",
            "Dt2Precision0", "Dt2Precision1", "Dt2Precision2", "Dt2Precision3", "Dt2Precision4",
            "Dt2Precision5", "Dt2Precision6", "Dt2Precision7", "DateOnly", "SmallDt", "RegularDt",
            "TimeOfDay", "OffsetHigh", "OffsetLow"));

        mismatches.ShouldBeEmpty();

        // The raw bytes above already carry the offset, but a value silently normalized to UTC is the
        // failure worth naming: +14:00 and -14:00 have to still be +14:00 and -14:00 in the target.
        const string OffsetsSql = """
                                  SELECT STRING_AGG(CONVERT(VARCHAR(12), Offset), ',') WITHIN GROUP (ORDER BY Offset)
                                  FROM (
                                      SELECT DATEPART(tzoffset, OffsetHigh) AS Offset FROM dbo.ChronoExtremes
                                      UNION
                                      SELECT DATEPART(tzoffset, OffsetLow) FROM dbo.ChronoExtremes
                                  ) AS o
                                  """;
        (await target.ScalarStringAsync(OffsetsSql)).ShouldBe("-840,0,720,840");
    }

    [Fact]
    public async Task RoundTrip_ExtendedUnicode_IsByteExact() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVault);
        await using var sqlite = new SqliteTempFileHarness();

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.UnicodeHazards"));
        exportResult.RowCount.ShouldBe(6);

        // Raw UTF-16 bytes at every hop. A string comparer -- or a SQL '=' under any collation -- would call
        // a decomposed 'a' + U+0301 equal to a precomposed U+00E1 and report a normalized value as intact.
        const string HexSql = """
                              SELECT CONVERT(VARCHAR(MAX), CONVERT(VARBINARY(MAX), HazardText), 2)
                              FROM dbo.UnicodeHazards
                              ORDER BY UnicodeHazardId
                              """;
        var sourceHex = await source.ReadStringsAsync(HexSql);

        await using (var package = await sqlite.OpenConnectionAsync()) {
            var packageHex = await SqlitePackageAssertions.ReadHexListAsync(package, "SELECT HazardText FROM dbo__unicodehazards ORDER BY UnicodeHazardId");
            packageHex.ShouldBe(sourceHex, "the SQLite text layer changed the bytes");
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.RowCount.ShouldBe(6);
        // A dropped row shortens both lists together, so the byte comparison alone could not see it.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.UnicodeHazards")).ShouldBe(6);
        (await target.ReadStringsAsync(HexSql)).ShouldBe(sourceHex);
    }

    [Fact]
    public async Task RoundTrip_FixedWidthAndCollation_PreservesPaddingAndRecordsCollation() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVault);
        await using var sqlite = new SqliteTempFileHarness();

        var labelCollation = await ColumnCollationAsync(source, "LabelNVarchar");
        var databaseCollation = await source.ScalarStringAsync("SELECT CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))");
        labelCollation.ShouldNotBe(databaseCollation, "LabelNVarchar has to carry a non-default collation or this test proves nothing about collation capture");

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.FixedWidthTexts"));

        await using (var package = await sqlite.OpenConnectionAsync()) {
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.FixedWidthTexts", "LabelNVarchar", collationName: labelCollation);
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.FixedWidthTexts", "LabelVarchar", collationName: await ColumnCollationAsync(source, "LabelVarchar"));
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.RowCount.ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.FixedWidthTexts")).ShouldBe(3);

        var mismatches = await source.ReadStringsAsync(ByteMismatchSql(
            source, target, "[dbo].[FixedWidthTexts]", "FixedWidthTextId",
            "CodeChar", "CodeNChar", "LabelVarchar", "LabelNVarchar"));
        mismatches.ShouldBeEmpty();

        // The byte compare above only says target == source, so the padding rules get named outright:
        // CHAR(10)/NCHAR(10) sit at 10 and 20 bytes on every row including the empty-string one, VARCHAR
        // keeps its three trailing spaces (17, not 14), and NVARCHAR is two bytes per character.
        (await target.ReadRowsAsync("""
                                    SELECT FixedWidthTextId, DATALENGTH(CodeChar), DATALENGTH(CodeNChar),
                                           DATALENGTH(LabelVarchar), DATALENGTH(LabelNVarchar)
                                    FROM dbo.FixedWidthTexts
                                    ORDER BY FixedWidthTextId
                                    """)).ShouldBe(new[] {
            "1 | 10 | 20 | 17 | 16",
            "2 | 10 | 20 | 0 | 0",
            "3 | 10 | 20 | 5 | 34"
        });
    }

    [Fact]
    public async Task RoundTrip_FixedWidthIntoSwappedCharTypes_SilentlyManglesNonAscii() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVault));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplyTargetVariantAsync(target, TypeVault, null, TargetSchemaScripts.Variants.CollationSwap);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.FixedWidthTexts"));
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        // Pins what happens today: import warns about the mismatched CHAR/NCHAR and VARCHAR/NVARCHAR types
        // but does not block, so every row still copies and the values go through the same implicit
        // conversion a CAST to the target type would do. A change here should be a deliberate decision, so
        // these assertions are meant to be re-read, not silently updated.
        importResult.RowCount.ShouldBe(3);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.FixedWidthTexts")).ShouldBe(3);

        var swapped = new[] {
            (Column: "CodeChar", TargetType: "NCHAR(10)"),
            (Column: "CodeNChar", TargetType: "CHAR(10)"),
            (Column: "LabelVarchar", TargetType: "NVARCHAR(50)"),
            (Column: "LabelNVarchar", TargetType: "VARCHAR(50)")
        };

        var notCastEquivalent = await source.ReadStringsAsync(string.Join("\nUNION ALL\n", swapped.Select(column => $"""
                                                                                                                     SELECT '{column.Column} (row ' + CONVERT(VARCHAR(12), s.FixedWidthTextId) + ')' AS Mismatch
                                                                                                                     FROM [{source.DatabaseName}].[dbo].[FixedWidthTexts] s
                                                                                                                     INNER JOIN [{target.DatabaseName}].[dbo].[FixedWidthTexts] t ON t.FixedWidthTextId = s.FixedWidthTextId
                                                                                                                     WHERE CONVERT(VARBINARY(MAX), t.[{column.Column}]) <> CONVERT(VARBINARY(MAX), CAST(s.[{column.Column}] AS {column.TargetType}))
                                                                                                                     """)) + "\nORDER BY Mismatch");
        notCastEquivalent.ShouldBeEmpty();

        // Which values that conversion actually changed, compared as UTF-16 bytes on both sides: only the
        // NVARCHAR-into-VARCHAR column, and only the row carrying characters the target code page cannot hold.
        var changed = await source.ReadStringsAsync(string.Join("\nUNION ALL\n", swapped.Select(column => $"""
                                                                                                           SELECT '{column.Column} (row ' + CONVERT(VARCHAR(12), s.FixedWidthTextId) + ')' AS Changed
                                                                                                           FROM [{source.DatabaseName}].[dbo].[FixedWidthTexts] s
                                                                                                           INNER JOIN [{target.DatabaseName}].[dbo].[FixedWidthTexts] t ON t.FixedWidthTextId = s.FixedWidthTextId
                                                                                                           WHERE CONVERT(VARBINARY(MAX), CAST(t.[{column.Column}] AS NVARCHAR(50))) <> CONVERT(VARBINARY(MAX), CAST(s.[{column.Column}] AS NVARCHAR(50)))
                                                                                                           """)) + "\nORDER BY Changed");
        changed.ShouldBe(["LabelNVarchar (row 1)"]);

        // Eight UTF-16 characters in, one byte per character out, with both CJK characters replaced by '?'.
        // Assumes the container's default collation is single-byte (CP1252 on the stock image); a UTF-8
        // default collation would land somewhere else, and this is the assertion that would say so.
        (await source.ScalarIntAsync("SELECT DATALENGTH(LabelNVarchar) FROM dbo.FixedWidthTexts WHERE FixedWidthTextId = 1")).ShouldBe(16);
        (await target.ScalarIntAsync("SELECT DATALENGTH(LabelNVarchar) FROM dbo.FixedWidthTexts WHERE FixedWidthTextId = 1")).ShouldBe(8);
        (await target.ScalarStringAsync("SELECT LabelNVarchar FROM dbo.FixedWidthTexts WHERE FixedWidthTextId = 1")).ShouldBe("\u00E9clat ??");
    }

    /// <summary>
    /// type-vault.sql deliberately holds tables that must fail export (LedgerAmounts, the unsupported-type
    /// hazards), so every test over it selects the one table it is about.
    /// </summary>
    private static ExportOptions OnlyTable(string fullName) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [fullName]
        };
    }

    private static async Task<string> ColumnCollationAsync(SqlServerFixtureDatabase db, string columnName) {
        return await db.ScalarStringAsync($"""
                                           SELECT collation_name
                                           FROM sys.columns
                                           WHERE object_id = OBJECT_ID('dbo.FixedWidthTexts')
                                             AND name = '{columnName}'
                                           """);
    }

    /// <summary>
    /// Every column-and-row pair whose bytes differ between the two databases, as one query over three-part
    /// names. VARBINARY equality is stricter than <c>DATEDIFF_BIG(ns, ...)</c> and works for every type here,
    /// including the year-0001 and year-9999 rows a nanosecond DATEDIFF overflows on.
    /// </summary>
    private static string ByteMismatchSql(SqlServerFixtureDatabase source, SqlServerFixtureDatabase target, string table, string keyColumn, params string[] columns) {
        var blocks = columns.Select(column => $"""
                                               SELECT '{column} (row ' + CONVERT(VARCHAR(12), s.[{keyColumn}]) + ')' AS Mismatch
                                               FROM [{source.DatabaseName}].{table} s
                                               INNER JOIN [{target.DatabaseName}].{table} t ON t.[{keyColumn}] = s.[{keyColumn}]
                                               WHERE CASE
                                                         WHEN CONVERT(VARBINARY(MAX), s.[{column}]) = CONVERT(VARBINARY(MAX), t.[{column}]) THEN 1
                                                         WHEN s.[{column}] IS NULL AND t.[{column}] IS NULL THEN 1
                                                         ELSE 0
                                                     END = 0
                                               """);

        return string.Join("\nUNION ALL\n", blocks) + "\nORDER BY Mismatch";
    }
}
