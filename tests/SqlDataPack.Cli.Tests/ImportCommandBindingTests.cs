using System.CommandLine;
using Shouldly;
using SqlDataPack.Cli.Commands;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Cli.Tests;

public sealed class ImportCommandBindingTests {
    private const string Connection = "Server=.;Database=NorthwindDev;Integrated Security=true";

    private static ImportRequest Bind(string commandLine) {
        var command = new ImportCommand();
        ParseResult parseResult = command.Parse(commandLine);
        parseResult.Errors.ShouldBeEmpty();
        return command.Bind(parseResult);
    }

    [Fact]
    public void PackagePathIsPositional() {
        Bind($"dev-slice.sqlite -c \"{Connection}\"").PackagePath.ShouldBe("dev-slice.sqlite");
    }

    [Fact]
    public void PackagePathIsRequired() {
        new ImportCommand().Parse($"-c \"{Connection}\"").Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void DeploySchemaDacpacMapsToDeployDacpac() {
        // The flag vocabulary is none|dacpac on both verbs; the enum spells this one DeployDacpac.
        Bind($"slice.sqlite -c \"{Connection}\" --deploy-schema dacpac").Options.SchemaDeploymentMode.ShouldBe(SchemaDeploymentMode.DeployDacpac);
        Bind($"slice.sqlite -c \"{Connection}\" --deploy-schema none").Options.SchemaDeploymentMode.ShouldBe(SchemaDeploymentMode.None);
    }

    [Fact]
    public void RowCountDriftDefaultsToTheLibraryDefault() {
        Bind($"slice.sqlite -c \"{Connection}\"").Options.RowCountDrift.ShouldBe(new ImportOptions().RowCountDrift);
    }

    [Fact]
    public void RowCountDriftFailIsSelectable() {
        Bind($"slice.sqlite -c \"{Connection}\" --row-count-drift fail").Options.RowCountDrift.ShouldBe(RowCountDrift.Fail);
    }

    [Fact]
    public void TimeoutSetsTheBulkCopyTimeout() {
        Bind($"slice.sqlite -c \"{Connection}\" --timeout 600").Options.BulkCopyTimeout.ShouldBe(600);
    }

    [Fact]
    public void BadEnumValuesAreParseErrors() {
        new ImportCommand().Parse($"slice.sqlite -c \"{Connection}\" --row-count-drift maybe").Errors.ShouldNotBeEmpty();
        new ImportCommand().Parse($"slice.sqlite -c \"{Connection}\" --deploy-schema bacpac").Errors.ShouldNotBeEmpty();
    }
}
