using System.Collections.Frozen;
using System.Data.SqlTypes;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlTypes;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Covers the per-cell type mapping: which SQLite storage class each SQL Server type gets, which types are
/// rejected outright, and the write/read pair for every kind whose text form has to survive a round trip
/// (decimal, guid, date, xml, json, vector). The vector test also pins <see cref="SqliteCoercingDataReader"/>
/// against the converter, because bulk copy binds by <c>GetFieldType</c> and reads by <c>GetValue</c> and the
/// two have to agree on the float16 discriminator.
/// </summary>
public sealed class ValueConverterTests {
    [Theory]
    [InlineData("bigint", "INTEGER")]
    [InlineData("int", "INTEGER")]
    [InlineData("smallint", "INTEGER")]
    [InlineData("tinyint", "INTEGER")]
    [InlineData("bit", "INTEGER")]
    [InlineData("float", "REAL")]
    [InlineData("real", "REAL")]
    [InlineData("char", "TEXT")]
    [InlineData("varchar", "TEXT")]
    [InlineData("nchar", "TEXT")]
    [InlineData("nvarchar", "TEXT")]
    [InlineData("decimal", "TEXT")]
    [InlineData("numeric", "TEXT")]
    [InlineData("money", "TEXT")]
    [InlineData("smallmoney", "TEXT")]
    [InlineData("date", "TEXT")]
    [InlineData("datetime", "TEXT")]
    [InlineData("datetime2", "TEXT")]
    [InlineData("datetimeoffset", "TEXT")]
    [InlineData("time", "TEXT")]
    [InlineData("uniqueidentifier", "TEXT")]
    [InlineData("xml", "TEXT")]
    [InlineData("json", "TEXT")]
    [InlineData("vector", "TEXT")]
    [InlineData("binary", "BLOB")]
    [InlineData("varbinary", "BLOB")]
    [InlineData("timestamp", "BLOB")]
    [InlineData("rowversion", "BLOB")]
    // Legacy synonyms the type map also accepts; they need a recorded storage class like everything else.
    [InlineData("text", "TEXT")]
    [InlineData("ntext", "TEXT")]
    [InlineData("smalldatetime", "TEXT")]
    [InlineData("image", "BLOB")]
    public void SqliteTypeFor_SupportedTypes_ReturnsExpectedStorageType(string sqlServerType, string expectedSqliteType) {
        var column = Column(sqlServerType);

        ValueConverter.SqliteTypeFor(column).ShouldBe(expectedSqliteType);
    }

    [Fact]
    public void SupportedTypeMap_HasARowForEveryKnownType() {
        var expected = StorageClassTheoryTypeNames()
            .Concat(UnsupportedTypeNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var actual = TypeNameMap().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A type added to the map without a row above ships with no storage-class decision recorded anywhere.
        actual.ShouldBe(expected, ignoreOrder: true);
    }

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("geography")]
    [InlineData("geometry")]
    [InlineData("hierarchyid")]
    public void IsUnsupported_UnsupportedTypes_ReturnsTrue(string sqlServerType) {
        ValueConverter.IsUnsupported(sqlServerType).ShouldBeTrue();
    }

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("geography")]
    [InlineData("geometry")]
    [InlineData("hierarchyid")]
    public void SqliteTypeFor_UnsupportedTypes_Throws(string sqlServerType) {
        var exception = Should.Throw<SqlDataPackException>(() => ValueConverter.SqliteTypeFor(Column(sqlServerType)));

        exception.Message.ShouldContain($"Unsupported SQL Server type '{sqlServerType}'");
        exception.Message.ShouldContain("dbo.Sample.Value");
    }

    [Theory]
    [InlineData("timestamp", true)]
    [InlineData("rowversion", true)]
    [InlineData("TIMESTAMP", true)]
    [InlineData("int", false)]
    [InlineData("varbinary", false)]
    [InlineData("nvarchar", false)]
    [InlineData("decimal", false)]
    [InlineData("xml", false)]
    [InlineData("vector", false)]
    public void IsServerGenerated_ByTypeName(string sqlServerType, bool expected) {
        ValueConverter.IsServerGenerated(sqlServerType).ShouldBe(expected);
    }

    [Fact]
    public void DecimalAndMoneyTypes_RoundTripAsInvariantText() {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try {
            ValueConverter.ToSqliteValue(1234.56m, Column("decimal")).ShouldBe("1234.56");
            ValueConverter.ToSqliteValue(9876.54m, Column("money")).ShouldBe("9876.54");
            ValueConverter.ToSqliteValue(12345.6789m, Column("numeric")).ShouldBe("12345.6789");
            ValueConverter.ToSqliteValue(12.3456m, Column("smallmoney")).ShouldBe("12.3456");

            ValueConverter.FromSqliteValue("1234.56", Column("decimal")).ShouldBe(1234.56m);
            ValueConverter.FromSqliteValue("9876.54", Column("money")).ShouldBe(9876.54m);
            ValueConverter.FromSqliteValue("12345.6789", Column("numeric")).ShouldBe(12345.6789m);
            ValueConverter.FromSqliteValue("12.3456", Column("smallmoney")).ShouldBe(12.3456m);
        }
        finally {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void UniqueIdentifier_RoundTripsThroughCanonicalText() {
        var guid = Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF");

        var text = ValueConverter.ToSqliteValue(guid, Column("uniqueidentifier")).ShouldBeOfType<string>();

        text.ShouldBe("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        ValueConverter.FromSqliteValue(text, Column("uniqueidentifier")).ShouldBe(guid);
    }

    [Fact]
    public void Bit_RoundTripsThroughIntegerRepresentation() {
        var column = Column("bit");

        ValueConverter.ToSqliteValue(true, column).ShouldBe(1);
        ValueConverter.ToSqliteValue(false, column).ShouldBe(0);

        // SQLite hands values back as Int64, so the read path has to convert rather than cast.
        ValueConverter.FromSqliteValue(1L, column).ShouldBe(true);
        ValueConverter.FromSqliteValue(0, column).ShouldBe(false);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xDE, 0xAD, 0xFF })]
    [InlineData(new byte[0])]
    public void Binary_RoundTripsByteIdentical(byte[] bytes) {
        var column = Column("varbinary");

        var stored = ValueConverter.ToSqliteValue(bytes, column).ShouldBeOfType<byte[]>();
        stored.SequenceEqual(bytes).ShouldBeTrue();

        var restored = ValueConverter.FromSqliteValue(stored, column).ShouldBeOfType<byte[]>();
        restored.SequenceEqual(bytes).ShouldBeTrue();
    }

    [Theory]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("datetime2")]
    public void DateTime_RoundTripsThroughIsoText(string sqlServerType) {
        var column = Column(sqlServerType);
        var value = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1_234_567);

        var text = ValueConverter.ToSqliteValue(value, column).ShouldBeOfType<string>();
        text.ShouldBe("2024-02-03T04:05:06.1234567Z");

        var restored = ValueConverter.FromSqliteValue(text, column).ShouldBeOfType<DateTime>();
        restored.Ticks.ShouldBe(value.Ticks);
        restored.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void DateTimeOffset_RoundTripsThroughIsoText() {
        var column = Column("datetimeoffset");
        var value = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.FromHours(-5)).AddTicks(1_234_567);

        var text = ValueConverter.ToSqliteValue(value, column).ShouldBeOfType<string>();
        text.ShouldBe("2024-02-03T04:05:06.1234567-05:00");

        var restored = ValueConverter.FromSqliteValue(text, column).ShouldBeOfType<DateTimeOffset>();
        restored.Ticks.ShouldBe(value.Ticks);
        restored.Offset.ShouldBe(value.Offset);
    }

    [Fact]
    public void Xml_RoundTripsThroughTextRepresentation() {
        const string xml = """<root><value id="1">alpha</value></root>""";
        var column = Column("xml");
        using var reader = XmlReader.Create(new StringReader(xml));

        // The driver hands xml back as SqlXml; a plain string is the other shape it arrives in.
        ValueConverter.ToSqliteValue(new SqlXml(reader), column).ShouldBe(xml);
        ValueConverter.ToSqliteValue(xml, column).ShouldBe(xml);
        ValueConverter.FromSqliteValue(xml, column).ShouldBe(xml);
    }

    [Fact]
    public void Json_RoundTripsThroughTextRepresentation() {
        const string json = """{"id":1,"name":"alpha","tags":["one","two"]}""";
        var column = Column("json");

        ValueConverter.ToSqliteValue(new SqlJson(json), column).ShouldBe(json);
        ValueConverter.ToSqliteValue(json, column).ShouldBe(json);
        ValueConverter.FromSqliteValue(json, column).ShouldBe(json);
    }

    [Fact]
    public void Vector_Float32_RoundTripsLossless() {
        var column = Column("vector", vectorBaseType: 0, vectorDimensions: 4);
        var values = new[] { 1f / 3f, -2.25f, 30.0f, 0.123456f };

        var text = ValueConverter.ToSqliteValue(new SqlVector<float>(values), column).ShouldBeOfType<string>();

        var restored = ValueConverter.FromSqliteValue(text, column).ShouldBeOfType<SqlVector<float>>();
        var elements = restored.Memory.ToArray();
        elements.Length.ShouldBe(values.Length);
        for (var i = 0; i < values.Length; i++) {
            BitConverter.SingleToInt32Bits(elements[i]).ShouldBe(BitConverter.SingleToInt32Bits(values[i]));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Vector_BaseTypeDiscriminator_AgreesAcrossReaderAndConverter(int vectorBaseType) {
        var column = Column("vector", vectorBaseType, vectorDimensions: 2);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT '[1,2]';";
        using var inner = command.ExecuteReader();
        using var reader = new SqliteCoercingDataReader(inner, [column], () => { });

        reader.Read().ShouldBeTrue();

        // Bulk copy binds by GetFieldType and reads by GetValue; if the two branch points disagree on
        // the discriminator the import fails at the wire instead of here.
        reader.GetFieldType(0).ShouldBe(reader.GetValue(0).GetType());
        reader.GetFieldType(0).ShouldBe(ValueConverter.FromSqliteValue("[1,2]", column)!.GetType());
    }

    [Theory]
    [MemberData(nameof(AllColumnKindNames))]
    public void FromSqliteValue_DbNull_ReturnsDbNull(string kindName) {
        var kind = Enum.Parse<ColumnKind>(kindName);
        var column = Column("vector", vectorBaseType: 1);

        ValueConverter.FromSqliteValue(DBNull.Value, column, kind).ShouldBe(DBNull.Value);
        ValueConverter.FromSqliteValue(null, column, kind).ShouldBe(DBNull.Value);
    }

    [Fact]
    public void Vector_Float32_MalformedJson_ThrowsSqlDataPackException() {
        var exception = Should.Throw<SqlDataPackException>(() => ValueConverter.FromSqliteValue("not-json", Column("vector", vectorBaseType: 0)));

        exception.Message.ShouldContain("dbo.Sample.Value");
        exception.Message.ShouldContain("vector");
        exception.InnerException.ShouldBeOfType<JsonException>();
    }

    public static IEnumerable<object[]> AllColumnKindNames() {
        return Enum.GetNames(typeof(ColumnKind)).Select(name => new object[] { name });
    }

    private static readonly string[] UnsupportedTypeNames = ["sql_variant", "geography", "geometry", "hierarchyid"];

    private static IEnumerable<string> StorageClassTheoryTypeNames() {
        var method = typeof(ValueConverterTests).GetMethod(nameof(SqliteTypeFor_SupportedTypes_ReturnsExpectedStorageType))!;

        return method.GetCustomAttributes<InlineDataAttribute>()
            .SelectMany(attribute => attribute.GetData(method))
            .Select(row => (string)row[0]!);
    }

    private static FrozenDictionary<string, ColumnKind> TypeNameMap() {
        var field = typeof(ValueConverter).GetField("KindsByTypeName", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("ValueConverter no longer has a KindsByTypeName map.");

        return (FrozenDictionary<string, ColumnKind>)field.GetValue(null)!;
    }

    private static ColumnMetadata Column(string sqlType, int? vectorBaseType = null, int? vectorDimensions = null) {
        return new ColumnMetadata(new TableName("dbo", "Sample"), "Value", 1, sqlType, 0, 0, 0, true, false, false, null, false, vectorBaseType, vectorDimensions);
    }
}
