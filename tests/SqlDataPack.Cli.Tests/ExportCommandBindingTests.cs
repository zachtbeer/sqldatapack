using System.CommandLine;
using Shouldly;
using SqlDataPack.Cli;
using SqlDataPack.Cli.Commands;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Cli.Tests;

/// <summary>
/// Parses real command lines and checks what reaches ExportOptions. Going through the parser rather
/// than calling the binder with hand-built values means a renamed flag fails here too.
/// </summary>
public sealed class ExportCommandBindingTests {
    private const string Connection = "Server=.;Database=Northwind;Integrated Security=true";

    private static ExportRequest Bind(string commandLine) {
        var command = new ExportCommand();
        ParseResult parseResult = command.Parse(commandLine);
        parseResult.Errors.ShouldBeEmpty();
        return command.Bind(parseResult);
    }

    [Fact]
    public void Tables_SwitchesSelectionToOnly() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite --tables dbo.Customers,dbo.Orders");

        request.Options.TableSelection.ShouldBe(ExportTableSelectionMode.Only);
        request.Options.Tables.ShouldBe(["dbo.Customers", "dbo.Orders"]);
    }

    [Fact]
    public void ExcludeTables_SwitchesSelectionToAllExcept() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite --exclude-tables dbo.AuditLog");

        request.Options.TableSelection.ShouldBe(ExportTableSelectionMode.AllExcept);
        request.Options.Tables.ShouldBe(["dbo.AuditLog"]);
    }

    [Fact]
    public void TablesAndExcludeTablesTogether_IsRejected() {
        Should.Throw<CliUsageException>(() => Bind($"-c \"{Connection}\" -o slice.sqlite --tables dbo.A --exclude-tables dbo.B"))
            .Message.ShouldContain("opposite directions");
    }

    [Fact]
    public void NeitherTableFlag_LeavesTheLibraryDefault() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite");

        request.Options.TableSelection.ShouldBe(new ExportOptions().TableSelection);
        request.Options.Tables.ShouldBeEmpty();
    }

    [Fact]
    public void RepeatedFlagsAndCommaLists_BothAccumulate() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite --exclude-column dbo.Customers.NationalId --exclude-column dbo.Staff.Salary,dbo.Staff.Bonus");

        request.Options.ExcludeColumns.ShouldBe(["dbo.Customers.NationalId", "dbo.Staff.Salary", "dbo.Staff.Bonus"]);
    }

    [Fact]
    public void GlobalWhere_SplitsOnTheFirstColonOnly() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite --global-where \"CreatedAt:CreatedAt > '2024-01-01 08:30:00'\"");

        GlobalWhereClause clause = request.Options.GlobalWhereClauses.ShouldHaveSingleItem();
        clause.ColumnNames.ShouldBe(["CreatedAt"]);
        // The predicate keeps its own colons; only the separator is consumed.
        clause.WhereClause.ShouldBe("CreatedAt > '2024-01-01 08:30:00'");
    }

    [Fact]
    public void GlobalWhere_AcceptsSeveralColumnNames() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite --global-where \"CustomerId,TenantId:CustomerId = 42\"");

        request.Options.GlobalWhereClauses.ShouldHaveSingleItem().ColumnNames.ShouldBe(["CustomerId", "TenantId"]);
    }

    [Fact]
    public void TableWhere_KeepsTheTableName() {
        ExportRequest request = Bind($"-c \"{Connection}\" -o slice.sqlite --table-where \"dbo.Orders:CustomerId = 42\"");

        PerTableWhereClause clause = request.Options.PerTableWhereClauses.ShouldHaveSingleItem();
        clause.TableName.ShouldBe("dbo.Orders");
        clause.WhereClause.ShouldBe("CustomerId = 42");
    }

    [Theory]
    [InlineData("nocolon")]
    [InlineData(":missing key")]
    [InlineData("missing predicate:")]
    public void MalformedWhereClause_IsRejected(string value) {
        Should.Throw<CliUsageException>(() => Bind($"-c \"{Connection}\" -o slice.sqlite --global-where \"{value}\""));
    }

    [Fact]
    public void SchemaDacpac_TurnsOnCapture() {
        Bind($"-c \"{Connection}\" -o slice.sqlite --schema dacpac").Options.SchemaCaptureMode.ShouldBe(SchemaCaptureMode.Dacpac);
        Bind($"-c \"{Connection}\" -o slice.sqlite --schema none").Options.SchemaCaptureMode.ShouldBe(SchemaCaptureMode.None);
    }

    [Fact]
    public void Overwrite_IsOffUnlessAsked() {
        Bind($"-c \"{Connection}\" -o slice.sqlite").Options.OverwriteExistingPackage.ShouldBeFalse();
        Bind($"-c \"{Connection}\" -o slice.sqlite --overwrite").Options.OverwriteExistingPackage.ShouldBeTrue();
    }

    [Theory]
    [InlineData("--batch-size 0")]
    [InlineData("--batch-size -5")]
    [InlineData("--timeout 0")]
    public void NonPositiveNumbers_AreRejected(string flags) {
        Should.Throw<CliUsageException>(() => Bind($"-c \"{Connection}\" -o slice.sqlite {flags}"));
    }

    [Fact]
    public void ConnectionFallsBackToTheEnvironmentVariable() {
        string? original = Environment.GetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable);
        try {
            Environment.SetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable, Connection);

            Bind("-o slice.sqlite").ConnectionString.ShouldBe(Connection);
        }
        finally {
            Environment.SetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable, original);
        }
    }

    [Fact]
    public void ExplicitConnectionBeatsTheEnvironmentVariable() {
        string? original = Environment.GetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable);
        try {
            Environment.SetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable, "Server=from-env;Database=x;Integrated Security=true");

            Bind($"-c \"{Connection}\" -o slice.sqlite").ConnectionString.ShouldBe(Connection);
        }
        finally {
            Environment.SetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable, original);
        }
    }

    [Fact]
    public void NoConnectionAnywhere_IsRejected() {
        string? original = Environment.GetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable);
        try {
            Environment.SetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable, null);

            Should.Throw<CliUsageException>(() => Bind("-o slice.sqlite"))
                .Message.ShouldContain(CommandSupport.ConnectionEnvironmentVariable);
        }
        finally {
            Environment.SetEnvironmentVariable(CommandSupport.ConnectionEnvironmentVariable, original);
        }
    }

    [Fact]
    public void OutIsRequired() {
        var command = new ExportCommand();

        command.Parse($"-c \"{Connection}\"").Errors.ShouldNotBeEmpty();
    }
}
