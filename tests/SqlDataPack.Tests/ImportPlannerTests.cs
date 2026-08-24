using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Covers the topological sort behind <see cref="ImportPlanner.BuildImportOrder"/>: referenced tables land before
/// the tables that point at them, ties break on name so the plan is reproducible, and the two shapes that have no
/// valid order (a multi-table cycle, a self-referencing foreign key) fail with a message naming the tables.
/// </summary>
public sealed class ImportPlannerTests {
    [Fact]
    public void BuildImportOrder_OrdersReferencedTablesBeforeDependents() {
        var grandparent = new TableName("dbo", "Grandparent");
        var parent = new TableName("dbo", "Parent");
        var child = new TableName("dbo", "Child");
        // Scrambled, and alphabetically backwards from the required order, so neither input order
        // nor the name tie-break can satisfy the assertion by accident.
        var tables = new[] { child, grandparent, parent };
        var foreignKeys = new[] {
            new ForeignKeyMetadata(child, parent),
            new ForeignKeyMetadata(parent, grandparent)
        };

        var result = ImportPlanner.BuildImportOrder(tables, foreignKeys).ToList();

        result.IndexOf(grandparent).ShouldBeLessThan(result.IndexOf(parent));
        result.IndexOf(parent).ShouldBeLessThan(result.IndexOf(child));
        result.ShouldBe(tables, ignoreOrder: true);
    }

    [Fact]
    public void BuildImportOrder_IndependentTables_UsesStableNameOrder() {
        var epsilon = new TableName("dbo", "Epsilon");
        var delta = new TableName("dbo", "Delta");
        var gamma = new TableName("dbo", "Gamma");
        var beta = new TableName("dbo", "Beta");
        var alpha = new TableName("dbo", "Alpha");

        var result = ImportPlanner.BuildImportOrder([gamma, epsilon, delta, beta, alpha], []);

        result.ShouldBe([alpha, beta, delta, epsilon, gamma]);
    }

    [Fact]
    public void BuildImportOrder_ForeignKeyCycle_Throws() {
        var first = new TableName("dbo", "First");
        var second = new TableName("dbo", "Second");
        var foreignKeys = new[] {
            new ForeignKeyMetadata(first, second),
            new ForeignKeyMetadata(second, first)
        };

        var exception = Should.Throw<SqlDataPackException>(() => ImportPlanner.BuildImportOrder([first, second], foreignKeys));

        exception.Message.ShouldContain("foreign-key cycle");
        exception.Message.ShouldContain("dbo.First");
        exception.Message.ShouldContain("dbo.Second");
        exception.Message.ShouldContain("Exclude");
    }

    [Fact]
    public void BuildImportOrder_SelfReferencingForeignKey_Throws() {
        var employee = new TableName("dbo", "Employee");
        var foreignKeys = new[] { new ForeignKeyMetadata(employee, employee) };

        var exception = Should.Throw<SqlDataPackException>(() => ImportPlanner.BuildImportOrder([employee], foreignKeys));

        exception.Message.ShouldContain("self-referencing foreign key");
        exception.Message.ShouldContain("dbo.Employee");
        exception.Message.ShouldContain("Exclude");
    }
}
