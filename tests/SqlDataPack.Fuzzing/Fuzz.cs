using System.Globalization;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Data.SqlTypes;
using SqlDataPack.Internal;

namespace SqlDataPack.Fuzzing;

/// <summary>A supported SQL Server type paired with a valid CLR value of that type.</summary>
internal readonly record struct TypedValue(string Type, object Value, int? VectorBaseType = null);

/// <summary>A SQL Server type whose SQLite representation is text, paired with a rendering of it.</summary>
internal readonly record struct TypedText(string Type, int? VectorBaseType, string Text);

/// <summary>
/// xUnit property attribute for the fuzz suite. Each property runs <c>FUZZ_MAXTEST</c> iterations
/// (default 200), so CI can fuzz harder than a local run by setting the environment variable.
/// </summary>
public sealed class FuzzPropertyAttribute : PropertyAttribute {
    public FuzzPropertyAttribute() {
        MaxTest = int.TryParse(Environment.GetEnvironmentVariable("FUZZ_MAXTEST"), out var n) && n > 0 ? n : 200;
    }
}

/// <summary>Shared FsCheck generators and factories for the property-based fuzz suite.</summary>
internal static class Fuzz {
    /// <summary>
    /// Arbitrary non-null text — control chars, digits, punctuation, empty — the kind of value a
    /// tampered package or option might carry.
    /// </summary>
    public static readonly Gen<string> Garbage = ArbMap.Default.GeneratorFor<string>().Where(s => s is not null);

    /// <summary>Any Unicode scalar value, so generated text carries surrogate pairs, not just BMP chars.</summary>
    private static readonly Gen<string> UnicodeScalar = Gen.Choose(0, 0x10FFFF).Where(cp => cp is < 0xD800 or > 0xDFFF).Select(char.ConvertFromUtf32);

    public static readonly Gen<string> UnicodeText = Gen.Choose(0, 24).SelectMany(n => Gen.ArrayOf(UnicodeScalar, n)).Select(parts => string.Concat(parts));

    private static readonly Gen<byte> Byte = ArbMap.Default.GeneratorFor<byte>();

    public static readonly Gen<byte[]> Blob = Gen.Choose(0, 48).SelectMany(n => Gen.ArrayOf(Byte, n));

    public static readonly Gen<byte[]> RowVersionBytes = Gen.ArrayOf(Byte, 8);

    /// <summary>Text that needs no escaping between XML tags.</summary>
    private static readonly Gen<string> XmlText = Gen.Choose(0, 12).SelectMany(n => Gen.ArrayOf(Gen.Elements("a", "Z", "9", " ", ".", "-", "é", "\U0001F600"), n)).Select(parts => string.Concat(parts));

    private static readonly Gen<string> XmlElement =
        from name in Gen.Elements("a", "b", "value", "item")
        from id in Gen.Choose(0, 999)
        from text in XmlText
        select $"<{name} id=\"{id}\">{text}</{name}>";

    public static readonly Gen<string> WellFormedXml =
        from root in Gen.Elements("root", "doc", "items")
        from count in Gen.Choose(0, 4)
        from children in Gen.ArrayOf(XmlElement, count)
        select $"<{root}>{string.Concat(children)}</{root}>";

    private static readonly Gen<object?> JsonScalar = Gen.OneOf(
        ArbMap.Default.GeneratorFor<int>().Select(v => (object?)v),
        ArbMap.Default.GeneratorFor<bool>().Select(v => (object?)v),
        UnicodeText.Select(v => (object?)v),
        Gen.Constant<object?>(null));

    public static readonly Gen<string> ValidJsonDocument =
        from count in Gen.Choose(0, 5)
        from values in Gen.ArrayOf(JsonScalar, count)
        select JsonSerializer.Serialize(values.Select((v, i) => new KeyValuePair<string, object?>($"f{i}", v)).ToDictionary(p => p.Key, p => p.Value));

    /// <summary>Vector components. JSON has no literal for NaN or the infinities, so they are excluded.</summary>
    private static readonly Gen<float[]> VectorComponents = Gen.Choose(1, 8).SelectMany(n => Gen.ArrayOf(ArbMap.Default.GeneratorFor<float>().Where(float.IsFinite), n));

    public static readonly Gen<SqlVector<float>> Float32Vector = VectorComponents.Select(v => new SqlVector<float>(v));

    /// <summary>float16 vectors reach the library as the varchar(max) JSON string the driver hands over.</summary>
    public static readonly Gen<string> Float16VectorJson = VectorComponents.Select(v => JsonSerializer.Serialize(v));

    /// <summary>Builds a single-column <see cref="ColumnMetadata"/> for the given SQL Server type.</summary>
    public static ColumnMetadata Column(string sqlServerType, int? vectorBaseType = null) => new(new TableName("dbo", "Sample"), "Value", 1, sqlServerType, 0, 0, 0, true, false, false, null, false, vectorBaseType);

    /// <summary>
    /// A supported SQL Server type paired with a valid, in-range CLR value that must round-trip
    /// through <c>ToSqliteValue</c> then <c>FromSqliteValue</c> without loss. Covers every
    /// <see cref="ColumnKind"/> the library supports.
    /// </summary>
    public static readonly Gen<TypedValue> RoundTrippable = Gen.OneOf(new[] {
        ArbMap.Default.GeneratorFor<int>().Select(v => new TypedValue("int", v)),
        ArbMap.Default.GeneratorFor<long>().Select(v => new TypedValue("bigint", v)),
        ArbMap.Default.GeneratorFor<short>().Select(v => new TypedValue("smallint", v)),
        ArbMap.Default.GeneratorFor<byte>().Select(v => new TypedValue("tinyint", v)),
        ArbMap.Default.GeneratorFor<bool>().Select(v => new TypedValue("bit", v)),
        ArbMap.Default.GeneratorFor<float>().Select(v => new TypedValue("real", v)),
        ArbMap.Default.GeneratorFor<double>().Select(v => new TypedValue("float", v)),
        // NaN and the infinities are legal real/float values, so they get their own draw instead of
        // being left to chance in the generic float generator.
        Gen.Elements(float.NaN, float.PositiveInfinity, float.NegativeInfinity).Select(v => new TypedValue("real", v)),
        Gen.Elements(double.NaN, double.PositiveInfinity, double.NegativeInfinity).Select(v => new TypedValue("float", v)),
        ArbMap.Default.GeneratorFor<Guid>().Select(v => new TypedValue("uniqueidentifier", v)),
        ArbMap.Default.GeneratorFor<decimal>().Select(v => new TypedValue("decimal", v)),
        ArbMap.Default.GeneratorFor<decimal>().Select(v => new TypedValue("numeric", v)),
        ArbMap.Default.GeneratorFor<DateTime>().Select(v => new TypedValue("datetime2", DateTime.SpecifyKind(v, DateTimeKind.Unspecified))),
        ArbMap.Default.GeneratorFor<DateTimeOffset>().Select(v => new TypedValue("datetimeoffset", v)),
        ArbMap.Default.GeneratorFor<long>().Select(l => new TypedValue("time", new TimeSpan(Math.Abs(l % TimeSpan.TicksPerDay)))),
        from type in Gen.Elements("nvarchar", "varchar", "char", "nchar") from text in UnicodeText select new TypedValue(type, text),
        WellFormedXml.Select(x => new TypedValue("xml", x)),
        ValidJsonDocument.Select(j => new TypedValue("json", j)),
        Float32Vector.Select(v => new TypedValue("vector", v, 0)),
        Float16VectorJson.Select(v => new TypedValue("vector", v, 1)),
        Blob.Select(b => new TypedValue("varbinary", b)),
        Blob.Select(b => new TypedValue("binary", b)),
        RowVersionBytes.Select(b => new TypedValue("rowversion", b))
    });

    /// <summary>
    /// A valid rendering of a value for every SQL Server type whose SQLite form is text — the set
    /// <c>FromSqliteValue</c> parses back, and therefore the set that can fail on a tampered package.
    /// </summary>
    public static readonly Gen<TypedText> TextPreserved = Gen.OneOf(new[] {
        DecimalText("decimal"), DecimalText("numeric"), DecimalText("money"), DecimalText("smallmoney"),
        DateText("date"), DateText("datetime"), DateText("datetime2"), DateText("smalldatetime"),
        ArbMap.Default.GeneratorFor<DateTimeOffset>().Select(v => new TypedText("datetimeoffset", null, v.ToString("O", CultureInfo.InvariantCulture))),
        ArbMap.Default.GeneratorFor<long>().Select(l => new TypedText("time", null, new TimeSpan(Math.Abs(l % TimeSpan.TicksPerDay)).ToString("c", CultureInfo.InvariantCulture))),
        ArbMap.Default.GeneratorFor<Guid>().Select(g => new TypedText("uniqueidentifier", null, g.ToString("D"))),
        WellFormedXml.Select(x => new TypedText("xml", null, x)),
        ValidJsonDocument.Select(j => new TypedText("json", null, j)),
        Float16VectorJson.Select(v => new TypedText("vector", 0, v)),
        Float16VectorJson.Select(v => new TypedText("vector", 1, v))
    });

    private static Gen<TypedText> DecimalText(string type) => ArbMap.Default.GeneratorFor<decimal>().Select(d => new TypedText(type, null, d.ToString(CultureInfo.InvariantCulture)));

    private static Gen<TypedText> DateText(string type) => ArbMap.Default.GeneratorFor<DateTime>().Select(d => new TypedText(type, null, d.ToString("O", CultureInfo.InvariantCulture)));
}
