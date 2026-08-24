using BenchmarkDotNet.Attributes;
using SqlDataPack.Internal;

namespace SqlDataPack.Benchmarks;

/// <summary>
/// Measures the per-cell type conversion that runs on every value of every column: the export
/// direction (<see cref="ValueConverter.ToSqliteValue"/>) and the import direction
/// (<see cref="ValueConverter.FromSqliteValue"/>, which every row of every bulk copy goes through
/// via <see cref="SqliteCoercingDataReader"/>).
/// </summary>
/// <remarks>
/// Each benchmark converts <see cref="CellCount"/> cells so allocation figures read as
/// "bytes per N cells"; divide by <see cref="CellCount"/> for per-cell cost.
/// </remarks>
[MemoryDiagnoser]
public class ValueConvertBenchmarks {
    private const int CellCount = 10_000;

    private ColumnMetadata _intColumn = null!;
    private ColumnMetadata _bigIntColumn = null!;
    private ColumnMetadata _bitColumn = null!;
    private ColumnMetadata _floatColumn = null!;
    private ColumnMetadata _textColumn = null!;
    private ColumnMetadata _decimalColumn = null!;
    private ColumnMetadata _dateTimeColumn = null!;
    private ColumnMetadata _guidColumn = null!;

    private object[] _sqlServerInts = null!;
    private object[] _sqlServerDecimals = null!;
    private object[] _sqlServerDateTimes = null!;
    private object[] _sqlServerGuids = null!;
    private object[] _sqlServerText = null!;

    private object[] _sqliteInts = null!;
    private object[] _sqliteLongs = null!;
    private object[] _sqliteDoubles = null!;
    private object[] _sqliteDecimalText = null!;
    private object[] _sqliteDateTimeText = null!;
    private object[] _sqliteGuidText = null!;
    private object[] _sqliteText = null!;

    [GlobalSetup]
    public void GlobalSetup() {
        var table = new TableName("dbo", "Bench");

        _intColumn = Column(table, 0, "Id", "int");
        _bigIntColumn = Column(table, 1, "Quantity", "bigint");
        _bitColumn = Column(table, 2, "IsActive", "bit");
        _floatColumn = Column(table, 3, "Ratio", "float");
        _textColumn = Column(table, 4, "Name", "nvarchar", maxLength: 400);
        _decimalColumn = Column(table, 5, "Amount", "decimal", precision: 19, scale: 4);
        _dateTimeColumn = Column(table, 6, "CreatedAt", "datetime2", scale: 7);
        _guidColumn = Column(table, 7, "ExternalId", "uniqueidentifier");

        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _sqlServerInts = Fill(i => i);
        _sqlServerDecimals = Fill(i => decimal.Divide(i, 100m));
        _sqlServerDateTimes = Fill(i => epoch.AddSeconds(i));
        _sqlServerGuids = Fill(i => new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        _sqlServerText = Fill(i => "value-" + i);

        // Values as Microsoft.Data.Sqlite hands them back: INTEGER columns arrive as long,
        // REAL as double, and everything else as the stored TEXT.
        _sqliteInts = Fill(i => (long)i);
        _sqliteLongs = Fill(i => (long)i * 3);
        _sqliteDoubles = Fill(i => i * 1.5d);
        _sqliteDecimalText = Fill(i => decimal.Divide(i, 100m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sqliteDateTimeText = Fill(i => epoch.AddSeconds(i).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _sqliteGuidText = Fill(i => new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString("D"));
        _sqliteText = Fill(i => "value-" + i);
    }

    private static object[] Fill(Func<int, object> factory) {
        var values = new object[CellCount];
        for (var i = 0; i < values.Length; i++) {
            values[i] = factory(i);
        }

        return values;
    }

    private static ColumnMetadata Column(TableName table, int ordinal, string name, string sqlServerTypeName, short maxLength = 0, byte precision = 0, byte scale = 0) {
        return new ColumnMetadata(table, name, ordinal, sqlServerTypeName, maxLength, precision, scale, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
    }

    // ---- Export direction ----

    [Benchmark]
    [BenchmarkCategory("ToSqlite")]
    public object? ToSqlite_Int() => ConvertAll(_sqlServerInts, _intColumn);

    [Benchmark]
    [BenchmarkCategory("ToSqlite")]
    public object? ToSqlite_Text() => ConvertAll(_sqlServerText, _textColumn);

    [Benchmark]
    [BenchmarkCategory("ToSqlite")]
    public object? ToSqlite_Decimal() => ConvertAll(_sqlServerDecimals, _decimalColumn);

    [Benchmark]
    [BenchmarkCategory("ToSqlite")]
    public object? ToSqlite_DateTime() => ConvertAll(_sqlServerDateTimes, _dateTimeColumn);

    [Benchmark]
    [BenchmarkCategory("ToSqlite")]
    public object? ToSqlite_Guid() => ConvertAll(_sqlServerGuids, _guidColumn);

    // ---- Import direction ----

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_Int() => ConvertAllBack(_sqliteInts, _intColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_BigInt() => ConvertAllBack(_sqliteLongs, _bigIntColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_Bit() => ConvertAllBack(_sqliteInts, _bitColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_Float() => ConvertAllBack(_sqliteDoubles, _floatColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_Text() => ConvertAllBack(_sqliteText, _textColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_Decimal() => ConvertAllBack(_sqliteDecimalText, _decimalColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_DateTime() => ConvertAllBack(_sqliteDateTimeText, _dateTimeColumn);

    [Benchmark]
    [BenchmarkCategory("FromSqlite")]
    public object? FromSqlite_Guid() => ConvertAllBack(_sqliteGuidText, _guidColumn);

    // The kind is hoisted out of the loop because that is exactly what the production callers do:
    // SqlitePackageWriter resolves it once per column before the row loop, and
    // SqliteCoercingDataReader caches it in its constructor. Measuring the per-cell overload here
    // would measure an API the hot paths deliberately do not use.
    private static object? ConvertAll(object[] values, ColumnMetadata column) {
        var kind = column.Kind;
        object? last = null;
        for (var i = 0; i < values.Length; i++) {
            last = ValueConverter.ToSqliteValue(values[i], column, kind);
        }

        return last;
    }

    private static object? ConvertAllBack(object[] values, ColumnMetadata column) {
        var kind = column.Kind;
        object? last = null;
        for (var i = 0; i < values.Length; i++) {
            last = ValueConverter.FromSqliteValue(values[i], column, kind);
        }

        return last;
    }
}
