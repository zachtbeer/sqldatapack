using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Covers SQLite data table name generation, the collision and reserved-namespace validators, table pattern
/// matching, and column path parsing. All of it is pure string work, so no database or file is involved.
/// </summary>
public sealed class IdentifierTests {
    [Theory]
    [InlineData(null, "dbo__orders")]
    [InlineData("", "dbo__orders")]
    [InlineData("   ", "dbo__orders")]
    [InlineData("custom_data", "custom_data_dbo__orders")]
    [InlineData("custom", "custom_dbo__orders")]
    public void ToSqliteDataTableName_UsesConfiguredDataTablePrefix(string? prefix, string expected) {
        var table = new TableName("dbo", "Orders");

        SqlDataPackIdentifier.ToSqliteDataTableName(table, prefix).ShouldBe(expected);
    }

    // A "collapse repeated underscores" tidy-up would merge __Name and _Name into one SQLite table.
    [Theory]
    [InlineData(null, "dbo____accountsbackup")]
    [InlineData("", "dbo____accountsbackup")]
    [InlineData("custom_data", "custom_data_dbo____accountsbackup")]
    public void ToSqliteDataTableName_PreservesSanitizedLeadingUnderscores(string? prefix, string expected) {
        var table = new TableName("dbo", "__AccountsBackup");

        SqlDataPackIdentifier.ToSqliteDataTableName(table, prefix).ShouldBe(expected);
    }

    [Theory]
    [InlineData("custom-data")]
    [InlineData("custom data")]
    [InlineData("custom.data")]
    public void ToSqliteDataTableName_InvalidPrefix_Throws(string prefix) {
        var table = new TableName("dbo", "Orders");

        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ToSqliteDataTableName(table, prefix));

        exception.Message.ShouldContain("DataTablePrefix");
    }

    [Theory]
    [InlineData("dbo.Orders", true)]
    [InlineData("Orders", true)]
    [InlineData("DBO.ORDERS", true)]
    [InlineData("ORDERS", true)]
    [InlineData("sales.Orders", false)]
    [InlineData("dbo.OrderItems", false)]
    [InlineData("OrderItems", false)]
    public void MatchesPattern_ExactNames_MatchesSchemaQualifiedOrTableName(string pattern, bool expected) {
        var table = new TableName("dbo", "Orders");

        SqlDataPackIdentifier.MatchesPattern(table, pattern).ShouldBe(expected);
    }

    [Theory]
    [InlineData("dbo", "OrderItems", "dbo.Order*", true)]
    [InlineData("dbo", "OrderItems", "DBO.ORDER*", true)]
    [InlineData("dbo", "OrderItems", "dbo.*", true)]
    [InlineData("dbo", "OrderItems", "DBO.*", true)]
    [InlineData("dbo", "OrderItems", "*Items", true)]
    [InlineData("dbo", "OrderItems", "*ITEMS", true)]
    // A wildcard pattern is matched against the schema-qualified name only, never the bare table name.
    [InlineData("dbo", "OrderItems", "Order*", false)]
    [InlineData("tenant", "Orders", "dbo.*", false)]
    [InlineData("dbo", "OrderItems", "sales.*", false)]
    [InlineData("dbo", "OrderItems", "*.Orders", false)]
    public void MatchesPattern_Wildcards_MatchFullNameCaseInsensitively(string schema, string name, string pattern, bool expected) {
        SqlDataPackIdentifier.MatchesPattern(new TableName(schema, name), pattern).ShouldBe(expected);
    }

    // A false positive here silently drops a real user table from the export.
    [Theory]
    [InlineData("dbo", "sysdiagrams", true)]
    [InlineData("dbo", "SYSDIAGRAMS", true)]
    [InlineData("DBO", "SysDiagrams", true)]
    [InlineData("dbo", "sysdiagrams2", false)]
    [InlineData("dbo", "sysdiagram", false)]
    [InlineData("app", "sysdiagrams", false)]
    [InlineData("dbo", "Orders", false)]
    public void IsSsmsDiagramTable_MatchesOnlyDboSysdiagrams(string schema, string name, bool expected) {
        SqlDataPackIdentifier.IsSsmsDiagramTable(new TableName(schema, name)).ShouldBe(expected);
    }

    [Fact]
    public void ValidateSqliteDataTableNamesUnique_PunctuationOnlyDifference_Throws() {
        var tables = BuildTables(new TableName("dbo", "Order-Items"), new TableName("dbo", "Order_Items"));

        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesUnique(tables));

        // The whole message is the value of this check, so assert all of it.
        exception.Message.ShouldBe(
            "Source tables 'dbo.Order-Items', 'dbo.Order_Items' map to the same SQLite table name 'dbo__order_items'. " +
            "SQLite table names are lowercased with every character that is not a letter or digit replaced by '_', " +
            "so source tables differing only in case or punctuation collide. " +
            "Exclude all but one of these tables from the export scope.");
    }

    [Fact]
    public void ValidateSqliteDataTableNamesUnique_CaseOnlyDifference_Throws() {
        var tables = BuildTables(new TableName("dbo", "Orders"), new TableName("dbo", "ORDERS"));

        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesUnique(tables));

        exception.Message.ShouldContain("'dbo.Orders'");
        exception.Message.ShouldContain("'dbo.ORDERS'");
        exception.Message.ShouldContain("'dbo__orders'");
        exception.Message.ShouldContain("Exclude all but one of these tables from the export scope.");
    }

    [Fact]
    public void ValidateSqliteDataTableNamesUnique_MoreThanTwoCollidingTables_NamesEveryTable() {
        var tables = BuildTables(new TableName("dbo", "Order-Items"), new TableName("dbo", "Order_Items"), new TableName("dbo", "Order.Items"));

        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesUnique(tables));

        exception.Message.ShouldContain("'dbo.Order-Items'");
        exception.Message.ShouldContain("'dbo.Order.Items'");
        exception.Message.ShouldContain("'dbo.Order_Items'");
    }

    [Fact]
    public void ValidateSqliteDataTableNamesUnique_DistinctNames_DoesNotThrow() {
        var tables = BuildTables(
            new TableName("dbo", "Orders"),
            new TableName("archive", "Orders"),
            new TableName("dbo", "OrderItems"),
            new TableName("dbo", "_AccountsBackup"),
            new TableName("dbo", "__AccountsBackup"));

        Should.NotThrow(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesUnique(tables));
    }

    [Theory]
    [InlineData("sqlite", "Orders", "sqlite__orders", "SQLite reserves")]
    [InlineData("SQLite", "Orders", "sqlite__orders", "SQLite reserves")]
    [InlineData("zsdp", "Orders", "zsdp__orders", "SqlDataPack reserves")]
    public void ValidateSqliteDataTableNamesNotReserved_ReservedSchemaName_Throws(string schema, string name, string generated, string reason) {
        var tables = BuildTables(new TableName(schema, name));

        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesNotReserved(tables));

        exception.Message.ShouldContain($"'{schema}.{name}'");
        exception.Message.ShouldContain($"'{generated}'");
        exception.Message.ShouldContain(reason);
        exception.Message.ShouldContain("Set DataTablePrefix");
    }

    // A reserved prefix puts every table in the export into the reserved namespace at once.
    [Theory]
    [InlineData("sqlite", "SQLite reserves")]
    [InlineData("zsdp", "SqlDataPack reserves")]
    public void ValidateSqliteDataTableNamesNotReserved_ReservedDataTablePrefix_Throws(string prefix, string reason) {
        var tables = BuildTables(prefix, new TableName("dbo", "Orders"));

        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesNotReserved(tables));

        exception.Message.ShouldContain("'dbo.Orders'");
        exception.Message.ShouldContain($"'{prefix}_dbo__orders'");
        exception.Message.ShouldContain(reason);
        exception.Message.ShouldContain("Set DataTablePrefix");
    }

    // The remediation the reserved-name error recommends has to actually work.
    [Fact]
    public void ValidateSqliteDataTableNamesNotReserved_ReservedNameBehindPrefix_DoesNotThrow() {
        var tables = BuildTables("pack", new TableName("sqlite", "Orders"), new TableName("zsdp", "Orders"));

        Should.NotThrow(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesNotReserved(tables));
    }

    // sqlitex and zsdpx pin the trailing underscore: a StartsWith("sqlite") check without it would reject them.
    [Fact]
    public void ValidateSqliteDataTableNamesNotReserved_OrdinaryNames_DoNotThrow() {
        var tables = BuildTables(
            new TableName("dbo", "Orders"),
            new TableName("sales", "Orders"),
            new TableName("dbo", "__AccountsBackup"),
            new TableName("sqlitex", "Orders"),
            new TableName("zsdpx", "Orders"));

        Should.NotThrow(() => SqlDataPackIdentifier.ValidateSqliteDataTableNamesNotReserved(tables));
    }

    [Fact]
    public void ParseColumnPath_ValidPath_ReturnsParts() {
        var result = SqlDataPackIdentifier.ParseColumnPath("dbo.Orders.Total");

        result.Schema.ShouldBe("dbo");
        result.Table.ShouldBe("Orders");
        result.Column.ShouldBe("Total");
    }

    [Theory]
    [InlineData("")]
    [InlineData("dbo")]
    [InlineData("dbo.Orders")]
    [InlineData("dbo.Orders.Total.Extra")]
    [InlineData(".Orders.Total")]
    [InlineData("dbo.Orders.")]
    [InlineData("dbo..Total")]
    public void ParseColumnPath_InvalidPath_Throws(string value) {
        var exception = Should.Throw<SqlDataPackException>(() => SqlDataPackIdentifier.ParseColumnPath(value));

        exception.Message.ShouldContain("<schema>.<table>.<column>");
    }

    private static TableMetadata[] BuildTables(params TableName[] names) {
        return BuildTables(null, names);
    }

    private static TableMetadata[] BuildTables(string? dataTablePrefix, params TableName[] names) {
        return names.Select(name => new TableMetadata(name, SqlDataPackIdentifier.ToSqliteDataTableName(name, dataTablePrefix), [])).ToArray();
    }
}
