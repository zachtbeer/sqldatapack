using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using SqlDataPack.Transformations;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Binding a transformer to a column and enforcing the destination column contract on what it returns.
/// Both run without a catalog: <c>TransformationBinder.Validate</c> reads a planned table list, and
/// <c>ColumnTransform</c> is handed one column's metadata.
/// </summary>
public sealed class TransformationBindingTests {
    private static readonly TableName Customers = new("dbo", "Customers");

    [Fact]
    public void Validate_BindsToTheExactColumn_IgnoringCase() {
        var options = OptionsWith("DBO.customers.EMAIL", new EmailMasker());

        var transformations = TransformationBinder.Validate([Table()], options);

        transformations.Count.ShouldBe(1);
        transformations[0].Table.ShouldBe(Customers);
        transformations[0].Column.ShouldBe("Email");
        transformations[0].TransformerType.ShouldBe("EmailMasker");
    }

    [Fact]
    public void Validate_UnknownColumn_Throws() {
        var options = OptionsWith("dbo.Customers.Nope", new EmailMasker());

        var exception = Should.Throw<SqlDataPackException>(() => TransformationBinder.Validate([Table()], options));

        exception.Message.ShouldContain("references a column that does not exist");
    }

    [Fact]
    public void Validate_TableOutsideExportScope_Throws() {
        var options = OptionsWith("dbo.Orders.Email", new EmailMasker());

        var exception = Should.Throw<SqlDataPackException>(() => TransformationBinder.Validate([Table()], options));

        exception.Message.ShouldContain("outside the selected export scope");
    }

    [Fact]
    public void Validate_MalformedPath_NamesTheSetting() {
        var options = OptionsWith("dbo.Customers", new EmailMasker());

        var exception = Should.Throw<SqlDataPackException>(() => TransformationBinder.Validate([Table()], options));

        exception.Message.ShouldContain("Transformation 'dbo.Customers' is invalid");
    }

    [Fact]
    public void Validate_ExcludedColumn_Throws() {
        var table = Table(Column("Email", "nvarchar", 100, isExcluded: true));
        var options = OptionsWith("dbo.Customers.Email", new EmailMasker());

        var exception = Should.Throw<SqlDataPackException>(() => TransformationBinder.Validate([table], options));

        exception.Message.ShouldContain("excluded from the export");
    }

    [Fact]
    public void Validate_TwoPathsForOneColumn_Throws() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailMasker());
        options.Transformations.Add(" dbo.Customers.email ", new EmailPseudonymizer());

        var exception = Should.Throw<SqlDataPackException>(() => TransformationBinder.Validate([Table()], options));

        exception.Message.ShouldContain("A column can have one transformer only");
    }

    [Fact]
    public void Validate_KeyAndIdentityColumns_AreAllowed() {
        var table = Table(new ColumnMetadata(Customers, "Id", 1, "int", 4, 10, 0, IsNullable: false, IsIdentity: true, IsComputed: false, CollationName: null, IsExcluded: false));
        var options = OptionsWith("dbo.Customers.Id", new NumericPseudonymizer());

        var transformations = TransformationBinder.Validate([table], options);

        transformations.Single().TransformerType.ShouldBe("NumericPseudonymizer");
    }

    [Fact]
    public void Validate_RecordsBuiltInConfigurationAndNamesCustomTransformersCustom() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.LastName", new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" }));
        options.Transformations.Add("dbo.Customers.Email", new CustomTransformer((_, value) => $"TEST-{value}"));

        var transformations = TransformationBinder.Validate([Table(Column("Email", "nvarchar", 100), Column("LastName", "nvarchar", 50))], options);

        var name = transformations.Single(t => t.Column == "LastName");
        name.TransformerType.ShouldBe("NameMasker");
        name.Configuration.ShouldBe("PreserveCharacters=2;Suffix=test");

        var custom = transformations.Single(t => t.Column == "Email");
        custom.TransformerType.ShouldBe("Custom");
        custom.Configuration.ShouldBeNull();
    }

    [Fact]
    public void CreateForTable_LeavesUnconfiguredColumnsUnbound() {
        var table = Table(Column("Email", "nvarchar", 100), Column("City", "nvarchar", 50));
        var options = OptionsWith("dbo.Customers.Email", new EmailMasker());

        var transforms = TransformationBinder.CreateForTable(table, TransformationBinder.Normalize(options), ExportSecret.Create());

        transforms.ShouldNotBeNull();
        transforms[0].ShouldNotBeNull();
        transforms[1].ShouldBeNull();
    }

    [Fact]
    public void CreateForTable_NoTransformationsConfigured_ReturnsNull() {
        var options = ExportOptions.Default;

        TransformationBinder.CreateForTable(Table(), TransformationBinder.Normalize(options), secret: null).ShouldBeNull();
    }

    [Fact]
    public void Apply_NullFromANullableColumn_WritesNull() {
        var transform = TransformFor(Column("Email", "nvarchar", 100), new CustomTransformer((_, _) => null));

        transform.Apply("someone@contoso.com").ShouldBe(DBNull.Value);
    }

    [Fact]
    public void Apply_NullFromANonNullableColumn_FailsTheExport() {
        var transform = TransformFor(Column("Email", "nvarchar", 100, isNullable: false), new CustomTransformer((_, _) => null));

        var exception = Should.Throw<SqlDataPackException>(() => transform.Apply("someone@contoso.com"));

        exception.Message.ShouldContain("returned NULL for dbo.Customers.Email, which is not nullable");
    }

    [Fact]
    public void Apply_ResultLongerThanTheColumn_FailsRatherThanTruncating() {
        var transform = TransformFor(Column("Email", "nvarchar", 20), new CustomTransformer((_, _) => new string('x', 21)));

        var exception = Should.Throw<SqlDataPackException>(() => transform.Apply("someone@contoso.com"));

        exception.Message.ShouldContain("returned 21 characters");
        exception.Message.ShouldContain("holds at most 10");
    }

    [Fact]
    public void Apply_BinaryLongerThanTheColumn_Fails() {
        var transform = TransformFor(Column("Photo", "varbinary", 8), new CustomTransformer((_, _) => new byte[9]));

        Should.Throw<SqlDataPackException>(() => transform.Apply(new byte[1])).Message.ShouldContain("returned 9 bytes");
    }

    [Fact]
    public void Apply_MaxLengthColumn_AcceptsAnyLength() {
        var transform = TransformFor(Column("Notes", "nvarchar", -1), new CustomTransformer((_, _) => new string('x', 5_000)));

        transform.Apply("note").ShouldBe(new string('x', 5_000));
    }

    [Fact]
    public void Apply_TooManyDecimalPlaces_Fails() {
        var column = new ColumnMetadata(Customers, "Balance", 1, "decimal", 9, 9, 2, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var transform = TransformFor(column, new CustomTransformer((_, _) => 1.234m));

        Should.Throw<SqlDataPackException>(() => transform.Apply(1m)).Message.ShouldContain("which has scale 2");
    }

    [Fact]
    public void Apply_TooManyIntegralDigits_Fails() {
        var column = new ColumnMetadata(Customers, "Balance", 1, "decimal", 9, 5, 2, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var transform = TransformFor(column, new CustomTransformer((_, _) => 1234.56m));

        Should.Throw<SqlDataPackException>(() => transform.Apply(1m)).Message.ShouldContain("at most 3 digits before the decimal point");
    }

    [Fact]
    public void Apply_ValueOutsideTheColumnRange_Fails() {
        var column = new ColumnMetadata(Customers, "Age", 1, "tinyint", 1, 3, 0, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var transform = TransformFor(column, new CustomTransformer((_, _) => 256));

        Should.Throw<SqlDataPackException>(() => transform.Apply(1)).Message.ShouldContain("outside the range of 'tinyint'");
    }

    [Fact]
    public void Apply_WrongClrTypeForTheColumn_Fails() {
        var column = new ColumnMetadata(Customers, "Age", 1, "int", 4, 10, 0, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var transform = TransformFor(column, new CustomTransformer((_, _) => "twelve"));

        Should.Throw<SqlDataPackException>(() => transform.Apply(12)).Message.ShouldContain("returned a String for dbo.Customers.Age");
    }

    [Fact]
    public void Apply_TransformerThrows_FailsTheExportNamingTheColumn() {
        var transform = TransformFor(Column("Email", "nvarchar", 100), new CustomTransformer((_, _) => throw new InvalidOperationException("boom")));

        var exception = Should.Throw<SqlDataPackException>(() => transform.Apply("someone@contoso.com"));

        exception.Message.ShouldContain("Transformer 'Custom' failed on dbo.Customers.Email: boom");
        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Apply_GuidColumnGivenAParseableString_NormalizesToGuid() {
        var column = new ColumnMetadata(Customers, "PublicId", 1, "uniqueidentifier", 16, 0, 0, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var value = Guid.NewGuid();
        var transform = TransformFor(column, new CustomTransformer((_, _) => value.ToString("D")));

        transform.Apply(Guid.NewGuid()).ShouldBe(value);
    }

    [Fact]
    public void Context_DescribesTheDestinationColumn() {
        TransformContext? seen = null;
        var column = new ColumnMetadata(Customers, "Balance", 3, "decimal", 9, 9, 2, IsNullable: false, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var transform = TransformFor(column, new CustomTransformer((context, _) => {
            seen = context;
            return 1.5m;
        }));

        transform.Apply(1m);

        seen.ShouldNotBeNull();
        seen.ColumnPath.ShouldBe("dbo.Customers.Balance");
        seen.SqlServerTypeName.ShouldBe("decimal");
        seen.IsNullable.ShouldBeFalse();
        seen.Precision.ShouldBe((byte)9);
        seen.Scale.ShouldBe((byte)2);
        seen.MaxLength.ShouldBeNull();
        // A custom transformer sees the column contract and nothing else: no secret, no connection, no row.
        typeof(TransformContext).GetProperties().Select(p => p.Name).ShouldNotContain("Secret");
    }

    [Theory]
    [InlineData("nvarchar", 100, 50)]
    [InlineData("varchar", 100, 100)]
    [InlineData("nchar", 20, 10)]
    [InlineData("binary", 16, 16)]
    [InlineData("ntext", 16, null)]
    [InlineData("int", 4, null)]
    public void Context_MaxLength_IsCharactersForTextAndBytesForBinary(string typeName, short maxLength, int? expected) {
        ColumnTransform.MaxLengthOf(new ColumnMetadata(Customers, "Value", 1, typeName, maxLength, 0, 0, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false)).ShouldBe(expected);
    }

    [Fact]
    public void Apply_NullTransformer_OnANullableColumn_WritesNull() {
        var transform = TransformFor(Column("Notes", "nvarchar", -1), new NullTransformer());

        transform.Apply("a long free-form note").ShouldBe(DBNull.Value);
    }

    [Fact]
    public void Apply_NullTransformer_OnANotNullColumn_FailsNamingTheColumn() {
        var transform = TransformFor(Column("Notes", "nvarchar", -1, isNullable: false), new NullTransformer());

        Should.Throw<SqlDataPackException>(() => transform.Apply("a long free-form note"))
            .Message.ShouldContain("returned NULL for dbo.Customers.Notes, which is not nullable");
    }

    [Fact]
    public void Apply_EmptyStringTransformer_OnANotNullTextColumn_WritesTheEmptyString() {
        var transform = TransformFor(Column("Notes", "nvarchar", -1, isNullable: false), new EmptyStringTransformer());

        transform.Apply("a long free-form note").ShouldBe(string.Empty);
    }

    [Fact]
    public void Apply_EmptyStringTransformer_OnANonTextColumn_Fails() {
        var column = new ColumnMetadata(Customers, "Age", 1, "int", 4, 10, 0, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
        var transform = TransformFor(column, new EmptyStringTransformer());

        Should.Throw<SqlDataPackException>(() => transform.Apply(12)).Message.ShouldContain("returned a String for dbo.Customers.Age");
    }

    [Fact]
    public void Validate_ConstantTransformers_RecordAnEmptyConfiguration() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Notes", new NullTransformer());
        options.Transformations.Add("dbo.Customers.Email", new EmptyStringTransformer());

        var transformations = TransformationBinder.Validate([Table(Column("Email", "nvarchar", 100), Column("Notes", "nvarchar", -1))], options);

        transformations.Single(t => t.Column == "Notes").TransformerType.ShouldBe("NullTransformer");
        transformations.Single(t => t.Column == "Notes").Configuration.ShouldBeNull();
        transformations.Single(t => t.Column == "Email").TransformerType.ShouldBe("EmptyStringTransformer");
        transformations.Single(t => t.Column == "Email").Configuration.ShouldBeNull();
    }

    private static ColumnTransform TransformFor(ColumnMetadata column, IValueTransformer transformer) => new(transformer, column, ExportSecret.Create());

    private static ExportOptions OptionsWith(string path, IValueTransformer transformer) {
        var options = ExportOptions.Default;
        options.Transformations.Add(path, transformer);
        return options;
    }

    private static TableMetadata Table(params ColumnMetadata[] columns) {
        var resolved = columns.Length == 0 ? [Column("Email", "nvarchar", 100)] : columns;
        return new TableMetadata(Customers, SqlDataPackIdentifier.ToSqliteDataTableName(Customers), resolved);
    }

    private static ColumnMetadata Column(string name, string typeName, short maxLength, bool isNullable = true, bool isExcluded = false) =>
        new(Customers, name, 1, typeName, maxLength, 0, 0, isNullable, IsIdentity: false, IsComputed: false, CollationName: null, isExcluded);
}
