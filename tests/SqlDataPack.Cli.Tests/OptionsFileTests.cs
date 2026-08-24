using System.CommandLine;
using Shouldly;
using SqlDataPack.Cli.Commands;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Cli.Tests;

/// <summary>
/// The options file is the half of the surface that is not flags, and the half most likely to end
/// up committed to a repository. These cover what it accepts, what it refuses, and how it loses to
/// an explicit flag.
/// </summary>
public sealed class OptionsFileTests : IDisposable {
    private const string Connection = "Server=.;Database=Northwind;Integrated Security=true";

    private readonly List<string> temporaryFiles = [];

    public void Dispose() {
        foreach (string path in this.temporaryFiles) {
            File.Delete(path);
        }
    }

    private string WriteOptionsFile(string json) {
        string path = Path.Combine(Path.GetTempPath(), $"sqldatapack-options-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        this.temporaryFiles.Add(path);
        return path;
    }

    private ExportRequest BindExport(string commandLine) {
        var command = new ExportCommand();
        ParseResult parseResult = command.Parse(commandLine);
        parseResult.Errors.ShouldBeEmpty();
        return command.Bind(parseResult);
    }

    private ImportRequest BindImport(string commandLine) {
        var command = new ImportCommand();
        ParseResult parseResult = command.Parse(commandLine);
        parseResult.Errors.ShouldBeEmpty();
        return command.Bind(parseResult);
    }

    [Fact]
    public void ReadsTheFullExportSurface() {
        string path = this.WriteOptionsFile("""
                                            {
                                              "tableSelection": "Only",
                                              "tables": [ "dbo.Customers", "dbo.Orders" ],
                                              "excludeColumns": [ "dbo.Customers.NationalId" ],
                                              "globalWhereClauses": [ { "columnName": "CustomerId", "whereClause": "CustomerId = 42" } ],
                                              "perTableWhereClauses": [ { "tableName": "dbo.Orders", "whereClause": "Status = 'open'" } ],
                                              "batchSize": 5000,
                                              "schemaCaptureMode": "Dacpac",
                                              "dacpacCaptureOptions": { "schemaScope": "SelectedExportTables" }
                                            }
                                            """);

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\"").Options;

        options.TableSelection.ShouldBe(ExportTableSelectionMode.Only);
        options.Tables.ShouldBe(["dbo.Customers", "dbo.Orders"]);
        options.ExcludeColumns.ShouldBe(["dbo.Customers.NationalId"]);
        options.GlobalWhereClauses.ShouldHaveSingleItem().ColumnNames.ShouldBe(["CustomerId"]);
        options.PerTableWhereClauses.ShouldHaveSingleItem().TableName.ShouldBe("dbo.Orders");
        options.BatchSize.ShouldBe(5000);
        options.SchemaCaptureMode.ShouldBe(SchemaCaptureMode.Dacpac);
        options.DacpacCaptureOptions.SchemaScope.ShouldBe(DacpacSchemaScope.SelectedExportTables);
    }

    [Fact]
    public void GlobalWhereClauseAcceptsAnArrayOfColumns() {
        string path = this.WriteOptionsFile("""
                                            { "globalWhereClauses": [ { "columnNames": [ "CustomerId", "TenantId" ], "whereClause": "CustomerId = 42" } ] }
                                            """);

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\"").Options;

        options.GlobalWhereClauses.ShouldHaveSingleItem().ColumnNames.ShouldBe(["CustomerId", "TenantId"]);
    }

    [Fact]
    public void ExplicitFlagsWinOverTheFile() {
        string path = this.WriteOptionsFile("""
                                            { "batchSize": 5000, "tables": [ "dbo.FromFile" ], "tableSelection": "Only" }
                                            """);

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\" --batch-size 250 --tables dbo.FromFlag").Options;

        options.BatchSize.ShouldBe(250);
        options.Tables.ShouldBe(["dbo.FromFlag"]);
    }

    [Fact]
    public void ValuesTheFlagsDoNotTouchSurviveTheOverride() {
        string path = this.WriteOptionsFile("""
                                            { "batchSize": 5000, "excludeColumns": [ "dbo.Customers.NationalId" ] }
                                            """);

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\" --batch-size 250").Options;

        options.BatchSize.ShouldBe(250);
        options.ExcludeColumns.ShouldBe(["dbo.Customers.NationalId"]);
    }

    [Fact]
    public void ABooleanFromTheFileSurvivesWhenItsFlagIsNotTyped() {
        // A flag that was never typed must not overwrite the file. Boolean flags are where this
        // goes wrong quietly, because "not specified" and "specified false" look the same
        // downstream.
        string path = this.WriteOptionsFile("""{ "overwriteExistingPackage": true }""");

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\"").Options;

        options.OverwriteExistingPackage.ShouldBeTrue();
    }

    [Fact]
    public void ABooleanFlagStillWinsWhenItIsTyped() {
        string path = this.WriteOptionsFile("""{ "overwriteExistingPackage": false }""");

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\" --overwrite").Options;

        options.OverwriteExistingPackage.ShouldBeTrue();
    }

    [Fact]
    public void AnEnumFromTheFileSurvivesWhenItsFlagIsNotTyped() {
        string path = this.WriteOptionsFile("""{ "schemaCaptureMode": "Dacpac" }""");

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\"").Options;

        options.SchemaCaptureMode.ShouldBe(SchemaCaptureMode.Dacpac);
    }

    [Fact]
    public void ImportOptionsAreReadToo() {
        string path = this.WriteOptionsFile("""
                                            { "rowCountDrift": "Fail", "batchSize": 250, "schemaDeploymentMode": "DeployDacpac" }
                                            """);

        ImportOptions options = this.BindImport($"slice.sqlite -c \"{Connection}\" --options \"{path}\"").Options;

        options.RowCountDrift.ShouldBe(RowCountDrift.Fail);
        options.BatchSize.ShouldBe(250);
        options.SchemaDeploymentMode.ShouldBe(SchemaDeploymentMode.DeployDacpac);
    }

    [Theory]
    [InlineData("""{ "connectionString": "Server=.;Database=x;Integrated Security=true" }""")]
    [InlineData("""{ "sqlServerConnection": "anything" }""")]
    [InlineData("""{ "perTableWhereClauses": [ { "tableName": "dbo.T", "whereClause": "Server=.;Database=x;Password=hunter2" } ] }""")]
    public void AConnectionStringInTheFileIsRefused(string json) {
        string path = this.WriteOptionsFile(json);

        Should.Throw<CliUsageException>(() => this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\""))
            .Message.ShouldContain("connection string");
    }

    [Fact]
    public void AWhereClauseMentioningPasswordIsStillAllowed() {
        // "Password" alone is a plausible column name. Only something that also names a server reads
        // as a credential, otherwise this flag would fire on ordinary predicates.
        string path = this.WriteOptionsFile("""
                                            { "perTableWhereClauses": [ { "tableName": "dbo.Users", "whereClause": "PasswordResetAt IS NOT NULL" } ] }
                                            """);

        ExportOptions options = this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\"").Options;

        options.PerTableWhereClauses.ShouldHaveSingleItem().WhereClause.ShouldBe("PasswordResetAt IS NOT NULL");
    }

    [Fact]
    public void AnUnknownPropertyIsRefusedRatherThanIgnored() {
        // A typo that silently does nothing produces a slice that is quietly wrong.
        string path = this.WriteOptionsFile("""{ "batchSizes": 5000 }""");

        Should.Throw<CliUsageException>(() => this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\""));
    }

    [Fact]
    public void SettingALoggerFromTheFileIsRefused() {
        string path = this.WriteOptionsFile("""{ "logger": "console" }""");

        Should.Throw<CliUsageException>(() => this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\""));
    }

    [Fact]
    public void MalformedJsonIsReportedAsAUsageError() {
        string path = this.WriteOptionsFile("{ not json");

        Should.Throw<CliUsageException>(() => this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\""))
            .Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void AMissingFileIsReportedAsAUsageError() {
        string path = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");

        Should.Throw<CliUsageException>(() => this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\""))
            .Message.ShouldContain("not found");
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed() {
        string path = this.WriteOptionsFile("""
                                            {
                                              // the slice we hand to support
                                              "batchSize": 250,
                                            }
                                            """);

        this.BindExport($"-c \"{Connection}\" -o slice.sqlite --options \"{path}\"").Options.BatchSize.ShouldBe(250);
    }
}
