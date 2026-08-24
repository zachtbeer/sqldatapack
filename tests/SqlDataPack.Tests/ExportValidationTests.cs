using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// The export-side checks that read nothing from the catalog: <see cref="GlobalWhereClause"/> value
/// semantics, malformed WHERE-clause configuration, the Only selection mode's pattern requirement, and
/// the refuse-to-overwrite guard on the destination file. The validators are reached from
/// <c>CreateExportPlanAsync</c> after the SQL connection is open, but they only look at options and a
/// resolved table list, so they run here without a container.
/// </summary>
public sealed class ExportValidationTests {
    [Fact]
    public void SingleColumnConstructor_ProducesOneColumnName() {
        var clause = new GlobalWhereClause("TenantId", "TenantId = 123");

        clause.ColumnNames.ShouldBe(["TenantId"]);
        clause.WhereClause.ShouldBe("TenantId = 123");
    }

    [Fact]
    public void MultiColumnConstructor_PreservesOrderAndDuplicates() {
        var clause = new GlobalWhereClause(["TenantId", "Region", "TenantId"], "TenantId = 1 AND Region = 'EU'");

        clause.ColumnNames.ShouldBe(["TenantId", "Region", "TenantId"]);
    }

    [Fact]
    public void Constructor_CopiesTheSourceSequence() {
        var names = new List<string> { "TenantId" };
        var clause = new GlobalWhereClause(names, "TenantId = 123");

        // A caller building several clauses in a loop reuses one list; the clause must not follow it.
        names.Add("Active");

        clause.ColumnNames.ShouldBe(["TenantId"]);
    }

    [Fact]
    public void Equals_ComparesColumnNamesByValueNotReference() {
        var first = new GlobalWhereClause(new List<string> { "TenantId", "Active" }, "TenantId = 123");
        var second = new GlobalWhereClause(new List<string> { "TenantId", "Active" }, "TenantId = 123");

        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());

        first.ShouldNotBe(new GlobalWhereClause(["TenantId"], "TenantId = 123"));
        first.ShouldNotBe(new GlobalWhereClause(["TenantId", "Active"], "TenantId = 456"));
    }

    [Theory]
    [InlineData(MalformedClauseShape.GlobalClauseWithNoColumns, "must name at least one column")]
    [InlineData(MalformedClauseShape.GlobalClauseWithBlankColumnName, "column name cannot be empty")]
    [InlineData(MalformedClauseShape.GlobalClauseWithEmptyPredicate, "cannot be empty", "TenantId")]
    [InlineData(MalformedClauseShape.PerTableClauseWithBlankTableName, "table name cannot be empty")]
    [InlineData(MalformedClauseShape.PerTableClauseWithEmptyPredicate, "cannot be empty", "dbo.Orders")]
    [InlineData(MalformedClauseShape.PerTableClauseOutsideOnlyScope, "is not in the selected export scope", "dbo.Customers")]
    [InlineData(MalformedClauseShape.PerTableClauseOutsideAllExceptScope, "is not in the selected export scope", "dbo.Customers")]
    public void WhereClauseValidation_MalformedShapes_Throw(MalformedClauseShape shape, params string[] expectedFragments) {
        var options = BuildOptions(shape);
        var selected = SqlServerSchemaReader.ResolveTables(SourceCatalog(), options, []);

        var exception = Should.Throw<SqlDataPackException>(() => {
            SqlServerSchemaReader.ValidateGlobalWhereClauses(options);
            SqlServerSchemaReader.ValidatePerTableWhereClauses(selected, options);
        });

        foreach (var fragment in expectedFragments) {
            exception.Message.ShouldContain(fragment);
        }
    }

    [Fact]
    public void ResolveTables_OnlySelectionWithoutTablePatterns_Throws() {
        var options = new ExportOptions { TableSelection = ExportTableSelectionMode.Only };

        var exception = Should.Throw<SqlDataPackException>(() => SqlServerSchemaReader.ResolveTables(SourceCatalog(), options, []));

        exception.Message.ShouldContain("requires at least one table pattern");
    }

    [Fact]
    public async Task Exporter_ExistingPackageWithoutOverwrite_FailsBeforeOpeningSqlServer() {
        var path = Path.Combine(Path.GetTempPath(), $"sqldatapack-existing-{Guid.NewGuid():N}.sqlite");
        byte[] original = [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x00, 0xFF, 0x01, 0x02];
        await File.WriteAllBytesAsync(path, original);
        try {
            var exception = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync("Server=invalid;Database=invalid;User Id=invalid;Password=invalid;", path));

            exception.Message.ShouldContain("already exists");
            // Byte-identical is what proves an early exit rather than a failure after the file was touched.
            (await File.ReadAllBytesAsync(path)).ShouldBe(original);
        }
        finally {
            File.Delete(path);
        }
    }

    private static ExportOptions BuildOptions(MalformedClauseShape shape) {
        return shape switch {
            MalformedClauseShape.GlobalClauseWithNoColumns => new ExportOptions { GlobalWhereClauses = [new GlobalWhereClause([], "TenantId = 123")] },
            MalformedClauseShape.GlobalClauseWithBlankColumnName => new ExportOptions { GlobalWhereClauses = [new GlobalWhereClause(["TenantId", "   "], "TenantId = 123")] },
            MalformedClauseShape.GlobalClauseWithEmptyPredicate => new ExportOptions { GlobalWhereClauses = [new GlobalWhereClause("TenantId", "   ")] },
            MalformedClauseShape.PerTableClauseWithBlankTableName => new ExportOptions { PerTableWhereClauses = [new PerTableWhereClause("   ", "Active = 1")] },
            MalformedClauseShape.PerTableClauseWithEmptyPredicate => new ExportOptions { PerTableWhereClauses = [new PerTableWhereClause("dbo.Orders", "")] },
            MalformedClauseShape.PerTableClauseOutsideOnlyScope => new ExportOptions {
                TableSelection = ExportTableSelectionMode.Only,
                Tables = ["dbo.Orders"],
                PerTableWhereClauses = [new PerTableWhereClause("dbo.Customers", "Active = 1")]
            },
            MalformedClauseShape.PerTableClauseOutsideAllExceptScope => new ExportOptions {
                TableSelection = ExportTableSelectionMode.AllExcept,
                Tables = ["dbo.Customers"],
                PerTableWhereClauses = [new PerTableWhereClause("dbo.Customers", "Active = 1")]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }

    private static List<TableName> SourceCatalog() {
        return [new TableName("dbo", "Customers"), new TableName("dbo", "Orders")];
    }

    /// <summary>
    /// The malformed configurations the theory drives. Public because xUnit has to serialize it into
    /// the test case name.
    /// </summary>
    public enum MalformedClauseShape {
        GlobalClauseWithNoColumns,
        GlobalClauseWithBlankColumnName,
        GlobalClauseWithEmptyPredicate,
        PerTableClauseWithBlankTableName,
        PerTableClauseWithEmptyPredicate,
        PerTableClauseOutsideOnlyScope,
        PerTableClauseOutsideAllExceptScope
    }
}
