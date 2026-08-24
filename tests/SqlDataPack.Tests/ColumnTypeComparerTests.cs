using Shouldly;
using SqlDataPack.Internal;
using Xunit;

namespace SqlDataPack.Tests;

public sealed class ColumnTypeComparerTests {
    private static ColumnMetadata Source(string typeName, short maxLength = 0, byte precision = 0, byte scale = 0, string? collation = null) {
        return new ColumnMetadata(new TableName("dbo", "T"), "C", 1, typeName, maxLength, precision, scale, IsNullable: true, IsIdentity: false, IsComputed: false, collation, IsExcluded: false);
    }

    [Fact]
    public void Compare_IdenticalColumns_ReturnsNone() {
        ColumnTypeComparer.Compare(Source("nvarchar", 100), "nvarchar", 100, 0, 0, null).ShouldBe(TypeDifference.None);
    }

    [Fact]
    public void Compare_NarrowerNvarchar_IsLossy() {
        ColumnTypeComparer.Compare(Source("nvarchar", 200), "nvarchar", 100, 0, 0, null).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_WiderNvarchar_IsWidening() {
        ColumnTypeComparer.Compare(Source("nvarchar", 100), "nvarchar", 200, 0, 0, null).ShouldBe(TypeDifference.Widening);
    }

    [Fact]
    public void Compare_MaxIntoFixedLength_IsLossy() {
        ColumnTypeComparer.Compare(Source("nvarchar", -1), "nvarchar", 4000, 0, 0, null).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_FixedLengthIntoMax_IsWidening() {
        ColumnTypeComparer.Compare(Source("nvarchar", 4000), "nvarchar", -1, 0, 0, null).ShouldBe(TypeDifference.Widening);
    }

    [Fact]
    public void Compare_NarrowerDecimalScale_IsLossy() {
        ColumnTypeComparer.Compare(Source("decimal", 9, 18, 4), "decimal", 9, 18, 2).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_NarrowerDecimalPrecision_IsLossy() {
        ColumnTypeComparer.Compare(Source("decimal", 9, 18, 2), "decimal", 5, 9, 2).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_NarrowerDatetime2Scale_IsLossy() {
        ColumnTypeComparer.Compare(Source("datetime2", 8, 27, 7), "datetime2", 6, 24, 3).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_DifferentCollationOnly_IsWideningNotLossy() {
        ColumnTypeComparer.Compare(Source("nvarchar", 100, collation: "SQL_Latin1_General_CP1_CI_AS"), "nvarchar", 100, 0, 0, "Latin1_General_100_CS_AS").ShouldBe(TypeDifference.Widening);
    }

    [Fact]
    public void Compare_NvarcharIntoWiderVarchar_IsLossyOnEncodingNotLength() {
        // nvarchar(50) source: max_length 100 (bytes). varchar(80) target: max_length 80 (bytes = chars).
        // 50 chars fit in 80 chars, so length is fine, but nvarchar -> varchar still mangles any
        // character outside the target's code page, so this is Lossy anyway.
        ColumnTypeComparer.Compare(Source("nvarchar", 100), "varchar", 80, 0, 0, null).ShouldBe(TypeDifference.Lossy);

        var message = ColumnTypeComparer.Describe(Source("nvarchar", 100), "varchar", 80, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("code page");
        message.ShouldNotContain("truncated");
    }

    [Fact]
    public void Compare_NvarcharIntoWiderNvarchar_IsWidening() {
        // Same width class both ways (nvarchar -> nvarchar), so no encoding loss, and the target is
        // wider. Pins that the encoding rule doesn't make every nvarchar comparison Lossy.
        ColumnTypeComparer.Compare(Source("nvarchar", 100), "nvarchar", 160, 0, 0, null).ShouldBe(TypeDifference.Widening);
    }

    [Fact]
    public void Compare_VarcharIntoNarrowerNvarchar_IsLossy() {
        // varchar(200) source: max_length 200 (bytes = chars). nvarchar(100) target: max_length 200 (bytes).
        // Target only holds 100 characters, so a byte-for-byte comparison wrongly reads as equal-length.
        ColumnTypeComparer.Compare(Source("varchar", 200), "nvarchar", 200, 0, 0, null).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_NvarcharIntoSameLengthVarchar_IsLossyOnEncoding() {
        // Same character length both ways, but nvarchar -> varchar drops any character outside the
        // target's code page. That's an encoding problem, not a length problem.
        ColumnTypeComparer.Compare(Source("nvarchar", 200), "varchar", 100, 0, 0, null).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_NarrowerNvarcharIntoVarchar_IsLossyOnLengthAndEncoding() {
        ColumnTypeComparer.Compare(Source("nvarchar", 400), "varchar", 100, 0, 0, null).ShouldBe(TypeDifference.Lossy);
    }

    [Fact]
    public void Compare_VarcharIntoSameLengthNvarchar_IsNotLossy() {
        // The reverse direction is safe: every varchar value fits in nvarchar.
        ColumnTypeComparer.Compare(Source("varchar", 100), "nvarchar", 200, 0, 0, null).ShouldBe(TypeDifference.Widening);
    }

    [Fact]
    public void Compare_IntIntoBigint_IsWidening() {
        ColumnTypeComparer.Compare(Source("int", 4, 10, 0), "bigint", 8, 19, 0).ShouldBe(TypeDifference.Widening);
    }

    [Fact]
    public void Describe_LossyColumn_NamesBothTypesAndTheColumn() {
        var message = ColumnTypeComparer.Describe(Source("nvarchar", 200), "nvarchar", 100, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("dbo.T");
        message.ShouldContain("C");
        message.ShouldContain("nvarchar(100)");
    }

    [Fact]
    public void Describe_Nvarchar_RendersCharacterLengthNotByteLength() {
        // sys.columns.max_length is bytes for nvarchar (2 bytes/char): a column declared NVARCHAR(100)
        // has MaxLength 200, and NVARCHAR(200) has MaxLength 400. The message must read like the DDL the
        // user wrote, not the raw catalog value.
        var message = ColumnTypeComparer.Describe(Source("nvarchar", 400), "nvarchar", 200, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("nvarchar(100)");
        message.ShouldContain("nvarchar(200)");
        message.ShouldNotContain("nvarchar(400)");
    }

    [Fact]
    public void Describe_Varchar_RendersByteLengthUnhalved() {
        // varchar is one byte per character, so its MaxLength is already the declared length -- only
        // nchar/nvarchar get halved for display.
        var message = ColumnTypeComparer.Describe(Source("varchar", 101), "varchar", 50, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("varchar(50)");
        message.ShouldContain("varchar(101)");
    }

    [Fact]
    public void Describe_NvarcharIntoSameLengthVarchar_NamesEncodingNotTruncation() {
        var message = ColumnTypeComparer.Describe(Source("nvarchar", 200), "varchar", 100, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("varchar(100)");
        message.ShouldContain("nvarchar(100)");
        message.ShouldContain("code page");
        message.ShouldNotContain("truncated");
    }

    [Fact]
    public void Describe_NarrowerNvarcharIntoVarchar_NamesLengthAndEncoding() {
        var message = ColumnTypeComparer.Describe(Source("nvarchar", 400), "varchar", 100, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("truncated");
        message.ShouldContain("code page");
    }

    [Fact]
    public void Describe_NarrowerNvarchar_StillNamesTruncation() {
        // Pins the plain length-only case: both sides double-byte, so no encoding wording is added.
        var message = ColumnTypeComparer.Describe(Source("nvarchar", 200), "nvarchar", 100, 0, 0, null, TypeDifference.Lossy);
        message.ShouldContain("truncated");
        message.ShouldNotContain("code page");
    }
}
