using System.Collections;
using System.Data.Common;
using SqlDataPack.Internal;

namespace SqlDataPack.Benchmarks;

/// <summary>
/// Table shapes the export path behaves differently on. Kept small and explicit so a regression
/// can be attributed to a shape rather than to an averaged-out mixture.
/// </summary>
public enum TableShape {
    /// <summary>Three fixed-width integer columns; dominated by per-row statement overhead.</summary>
    NarrowInt,

    /// <summary>Eight nvarchar columns; dominated by string marshalling and parameter binding.</summary>
    WideText,

    /// <summary>An id plus a 4 KiB varbinary payload; dominated by blob copying.</summary>
    BlobHeavy,

    /// <summary>The type-conversion spread: decimal, datetime2, uniqueidentifier, bit, float.</summary>
    MixedTypes
}

internal static class BenchmarkFixtures {
    public static TableMetadata CreateTable(TableShape shape) {
        var name = new TableName("dbo", shape.ToString());
        var columns = shape switch {
            TableShape.NarrowInt => [
                Column(name, 0, "Id", "int"),
                Column(name, 1, "Quantity", "bigint"),
                Column(name, 2, "IsActive", "bit")
            ],
            TableShape.WideText => BuildWideText(name),
            TableShape.BlobHeavy => [
                Column(name, 0, "Id", "int"),
                Column(name, 1, "Payload", "varbinary", maxLength: -1)
            ],
            TableShape.MixedTypes => [
                Column(name, 0, "Id", "int"),
                Column(name, 1, "Amount", "decimal", precision: 19, scale: 4),
                Column(name, 2, "CreatedAt", "datetime2", scale: 7),
                Column(name, 3, "ExternalId", "uniqueidentifier"),
                Column(name, 4, "IsActive", "bit"),
                Column(name, 5, "Ratio", "float")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        return new TableMetadata(name, SqlDataPackIdentifier.ToSqliteDataTableName(name), columns);
    }

    private static ColumnMetadata[] BuildWideText(TableName table) {
        var columns = new ColumnMetadata[8];
        for (var i = 0; i < columns.Length; i++) {
            columns[i] = Column(table, i, "Text" + i, "nvarchar", maxLength: 400);
        }

        return columns;
    }

    private static ColumnMetadata Column(TableName table, int ordinal, string name, string sqlServerTypeName, short maxLength = 0, byte precision = 0, byte scale = 0) {
        return new ColumnMetadata(table, name, ordinal, sqlServerTypeName, maxLength, precision, scale, IsNullable: true, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);
    }

    /// <summary>
    /// Builds the per-column value generator for a shape. Values are deterministic functions of the
    /// row index so every benchmark run sees identical input and results stay comparable across runs.
    /// </summary>
    public static Func<int, int, object> ValueFactoryFor(TableShape shape) {
        var blob = new byte[4096];
        for (var i = 0; i < blob.Length; i++) {
            blob[i] = (byte)i;
        }

        var text = new string('x', 200);

        return shape switch {
            TableShape.NarrowInt => (row, column) => column switch {
                0 => row,
                1 => (long)row * 3,
                _ => row % 2 == 0
            },
            TableShape.WideText => (row, column) => text + row + column,
            TableShape.BlobHeavy => (row, column) => column == 0 ? row : blob,
            TableShape.MixedTypes => (row, column) => column switch {
                0 => row,
                1 => decimal.Divide(row, 100m),
                2 => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(row),
                3 => new Guid(row, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                4 => row % 2 == 0,
                _ => row * 1.5d
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }
}

/// <summary>
/// A minimal in-memory <see cref="DbDataReader"/> that stands in for the SQL Server reader so the
/// export write loop can be benchmarked without a database. Only the members
/// <see cref="SqlitePackageWriter"/> actually calls are implemented; anything else throws loudly
/// rather than silently returning a wrong answer.
/// </summary>
internal sealed class SyntheticSourceReader : DbDataReader {
    private readonly int _rowCount;
    private readonly int _fieldCount;
    private readonly Func<int, int, object> _valueFactory;
    private int _rowIndex = -1;

    public SyntheticSourceReader(int rowCount, int fieldCount, Func<int, int, object> valueFactory) {
        _rowCount = rowCount;
        _fieldCount = fieldCount;
        _valueFactory = valueFactory;
    }

    public override int FieldCount => _fieldCount;
    public override bool HasRows => _rowCount > 0;
    public override bool IsClosed => _rowIndex >= _rowCount;
    public override int RecordsAffected => 0;
    public override int Depth => 0;

    public override bool Read() => ++_rowIndex < _rowCount;

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

    public override bool IsDBNull(int ordinal) => false;

    public override object GetValue(int ordinal) => _valueFactory(_rowIndex, ordinal);

    public override int GetValues(object[] values) {
        var count = Math.Min(values.Length, _fieldCount);
        for (var i = 0; i < count; i++) {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => throw new NotSupportedException();
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override string GetName(int ordinal) => throw new NotSupportedException();
    public override int GetOrdinal(string name) => throw new NotSupportedException();
    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
    public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public void Reset() => _rowIndex = -1;
}
