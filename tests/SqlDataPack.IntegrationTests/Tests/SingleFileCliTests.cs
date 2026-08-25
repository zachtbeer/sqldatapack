// Only on net10.0. The published binary is identical whichever framework the test host runs on, and
// building it from both would have two processes publishing the same project into the same obj at
// the same time.

#if NET10_0_OR_GREATER
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Runs the published single-file executable, not the CLI's loose assemblies.
/// <para>
/// The point is the dacpac path. DacFx reads <c>Assembly.Location</c>, which is empty for a bundled
/// assembly, so without <c>IncludeAllContentForSelfExtract</c> in SqlDataPack.Cli.csproj the shipped
/// binary throws "Could not save package to file. The path is empty." the moment anyone asks for
/// schema capture -- while every other test in the repository stays green, because they all run
/// against ordinary assemblies on disk. winget and the direct downloads are the builds affected,
/// which is to say most users.
/// </para>
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class SingleFileCliTests {
    private const string SourceFixture = "core-commerce.sql";

    /// <summary>
    /// sql_variant is not exportable, so any scope holding dbo.CustomerProfiles needs this out.
    /// See TableAndColumnSelectionTests, which documents the same exclusion.
    /// </summary>
    private const string UnsupportedColumn = "dbo.CustomerProfiles.LegacyFlags";

    /// <summary>Children before parents, so the target is empty for the tables being imported.</summary>
    private const string EmptyTheTarget = """
                                          DELETE FROM dbo.OrderLines;
                                          DELETE FROM dbo.Orders;
                                          DELETE FROM dbo.CustomerDocuments;
                                          DELETE FROM dbo.CustomerProfiles;
                                          DELETE FROM dbo.Customers;
                                          """;

    private readonly SqlServerContainerFixture _fixture;

    public SingleFileCliTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    /// <summary>
    /// IncludeAllContentForSelfExtract bundles the native libraries too, so a correct publish is
    /// literally one file. If this starts finding e_sqlite3 and friends beside the executable, the
    /// property has been dropped and the dacpac tests below are about to fail for a reason nobody
    /// would guess from their names.
    /// </summary>
    [Fact]
    public async Task PublishedBinary_IsOneFile() {
        var directory = await PublishedCliHarness.GetOutputDirectoryAsync();

        var files = Directory.GetFiles(directory);

        files.ShouldHaveSingleItem($"Expected a single self-contained executable, found: {string.Join(", ", files.Select(Path.GetFileName))}");
    }

    [Fact]
    public async Task ExportThenImport_DataOnly_RoundTripsThroughTheSingleFileBinary() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var sqlite = new SqliteTempFileHarness();

        var export = await PublishedCliHarness.RunAsync([
            "export",
            "--connection", source.ConnectionString,
            "--out", sqlite.FilePath,
            "--tables", "dbo.Customers,dbo.Orders",
            "--overwrite"
        ]);

        export.ExitCode.ShouldBe(0, export.AllOutput);
        export.StandardOutput.ShouldContain("Exported");
        File.Exists(sqlite.FilePath).ShouldBeTrue();

        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await target.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await target.ExecuteSqlAsync(EmptyTheTarget);

        var import = await PublishedCliHarness.RunAsync([
            "import", sqlite.FilePath,
            "--connection", target.ConnectionString
        ]);

        import.ExitCode.ShouldBe(0, import.AllOutput);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers")).ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers"));
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders")).ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Orders"));
    }

    /// <summary>
    /// The regression this file exists for, on the whole-database extract path
    /// (<c>DacServices.Extract</c>), deployed back with <c>DacServices.Deploy</c>.
    /// </summary>
    [Fact]
    public async Task ExportWithDacpac_ThenDeployIntoAnEmptyTarget_WorksFromTheSingleFileBinary() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var sqlite = new SqliteTempFileHarness();

        var export = await PublishedCliHarness.RunAsync([
            "export",
            "--connection", source.ConnectionString,
            "--out", sqlite.FilePath,
            "--schema", "dacpac",
            "--exclude-column", UnsupportedColumn,
            "--overwrite"
        ]);

        export.ExitCode.ShouldBe(0, export.AllOutput);
        export.AllOutput.ShouldNotContain("The path is empty");

        // Empty target: without the dacpac there would be nothing to import into.
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);

        var import = await PublishedCliHarness.RunAsync([
            "import", sqlite.FilePath,
            "--connection", target.ConnectionString,
            "--deploy-schema", "dacpac"
        ]);

        import.ExitCode.ShouldBe(0, import.AllOutput);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers")).ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers"));
    }

    /// <summary>
    /// The same regression on the other DacFx entry point. A plan-scoped dacpac goes through
    /// <c>TSqlModel</c> and <c>DacPackageExtensions.BuildPackage</c>, which is the call actually
    /// observed to throw under a plain single-file publish.
    /// </summary>
    [Fact]
    public async Task ExportWithSelectedTableDacpacScope_WorksFromTheSingleFileBinary() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(SourceFixture));
        await using var sqlite = new SqliteTempFileHarness();

        var optionsFile = Path.Combine(Path.GetTempPath(), $"zsdp-options-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(optionsFile, """
                                                  { "dacpacCaptureOptions": { "schemaScope": "SelectedExportTables" } }
                                                  """);

        try {
            var export = await PublishedCliHarness.RunAsync([
                "export",
                "--connection", source.ConnectionString,
                "--out", sqlite.FilePath,
                "--schema", "dacpac",
                "--tables", "dbo.Customers,dbo.Orders",
                "--options", optionsFile,
                "--overwrite"
            ]);

            export.ExitCode.ShouldBe(0, export.AllOutput);
            export.AllOutput.ShouldNotContain("The path is empty");
        }
        finally {
            File.Delete(optionsFile);
        }
    }

    /// <summary>
    /// Mistyping the package path is the easiest mistake to make, and the library reports it as a
    /// raw SQLite driver error (issue #14). It should read as a sentence and exit non-zero, because
    /// CI is an obvious consumer of this tool.
    /// </summary>
    [Fact]
    public async Task AMissingPackage_FailsWithAMessageRatherThanAStackTrace() {
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);

        var import = await PublishedCliHarness.RunAsync([
            "import", Path.Combine(Path.GetTempPath(), $"no-such-package-{Guid.NewGuid():N}.sqlite"),
            "--connection", target.ConnectionString
        ]);

        import.ExitCode.ShouldBe(2, import.AllOutput);
        import.StandardError.ShouldContain("Package not found");
        import.StandardError.ShouldNotContain("   at ");
    }

    /// <summary>
    /// Nothing should print a stack trace unless asked. This drives the failure through the generic
    /// handler rather than the usage path, which is where a raw exception dump would escape.
    /// </summary>
    [Fact]
    public async Task AnUnreachableServer_ReportsAMessageAndNotAStackTrace() {
        var import = await PublishedCliHarness.RunAsync([
            "export",
            "--connection", "Server=127.0.0.1,1;Database=nope;User Id=sa;Password=no;Connect Timeout=2;TrustServerCertificate=true",
            "--out", Path.Combine(Path.GetTempPath(), $"zsdp-{Guid.NewGuid():N}.sqlite")
        ]);

        import.ExitCode.ShouldNotBe(0);
        import.StandardError.ShouldNotContain("   at ");
    }

    /// <summary>
    /// An options file carrying a credential is refused before anything connects. The file is meant
    /// to be committed, so this is the check that keeps it committable.
    /// </summary>
    [Fact]
    public async Task AnOptionsFileHoldingAConnectionString_IsRefused() {
        var optionsFile = Path.Combine(Path.GetTempPath(), $"zsdp-options-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(optionsFile, """
                                                  { "connectionString": "Server=.;Database=Northwind;User Id=sa;Password=hunter2" }
                                                  """);

        try {
            var export = await PublishedCliHarness.RunAsync([
                "export",
                "--connection", _fixture.MasterConnectionString,
                "--out", Path.Combine(Path.GetTempPath(), $"zsdp-{Guid.NewGuid():N}.sqlite"),
                "--options", optionsFile
            ]);

            export.ExitCode.ShouldBe(2, export.AllOutput);
            export.StandardError.ShouldContain("connection string");
        }
        finally {
            File.Delete(optionsFile);
        }
    }
}

#endif
