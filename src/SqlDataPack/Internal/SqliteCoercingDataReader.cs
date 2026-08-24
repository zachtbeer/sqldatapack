using System.Collections;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlTypes;

namespace SqlDataPack.Internal;

internal sealed class SqliteCoercingDataReader : IDataReader {
    private readonly SqliteDataReader _inner;
    private readonly IReadOnlyList<ColumnMetadata> _columns;
    private readonly ColumnKind[] _kinds;
    private readonly Action _onRowRead;

    public SqliteCoercingDataReader(SqliteDataReader inner, IReadOnlyList<ColumnMetadata> columns, Action onRowRead) {
        _inner = inner;
        _columns = columns;
        _onRowRead = onRowRead;

        // Resolved once per column rather than once per cell; see ValueConverter.FromSqliteValue.
        _kinds = new ColumnKind[columns.Count];
        for (var i = 0; i < columns.Count; i++) {
            _kinds[i] = columns[i].Kind;
        }
    }

    public int FieldCount => _columns.Count;
    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));
    public int Depth => _inner.Depth;
    public bool IsClosed => _inner.IsClosed;
    public int RecordsAffected => _inner.RecordsAffected;

    public bool Read() {
        var hasRow = _inner.Read();
        if (hasRow) {
            _onRowRead();
        }

        return hasRow;
    }

    public object GetValue(int i) {
        var value = _inner.GetValue(i);
        return ValueConverter.FromSqliteValue(value, _columns[i], _kinds[i]) ?? DBNull.Value;
    }

    public int GetValues(object[] values) {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) {
            values[i] = GetValue(i);
        }

        return count;
    }

    public string GetName(int i) => _columns[i].Name;

    public int GetOrdinal(string name) {
        for (var i = 0; i < _columns.Count; i++) {
            if (string.Equals(_columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }

        return -1;
    }

    public bool IsDBNull(int i) => _inner.IsDBNull(i);
    public string GetDataTypeName(int i) => _columns[i].SqlServerTypeName;

    public Type GetFieldType(int i) {
        return _kinds[i] switch {
            ColumnKind.Bit => typeof(bool),
            ColumnKind.TinyInt => typeof(byte),
            ColumnKind.SmallInt => typeof(short),
            ColumnKind.Int => typeof(int),
            ColumnKind.BigInt => typeof(long),
            ColumnKind.Real => typeof(float),
            ColumnKind.Float => typeof(double),
            ColumnKind.Decimal => typeof(decimal),
            ColumnKind.Date => typeof(DateTime),
            ColumnKind.DateTimeOffset => typeof(DateTimeOffset),
            ColumnKind.Time => typeof(TimeSpan),
            ColumnKind.UniqueIdentifier => typeof(Guid),
            ColumnKind.Binary or ColumnKind.RowVersion => typeof(byte[]),
            // float32 vectors bulk-copy as native SqlVector<float>; float16 vectors (preview) flow as the
            // varchar(max) JSON string the server exposes them as.
            ColumnKind.Vector => _columns[i].VectorBaseType == 1 ? typeof(string) : typeof(SqlVector<float>),
            _ => typeof(string)
        };
    }

    public DataTable? GetSchemaTable() => null;
    public bool NextResult() => false;
    public void Close() => _inner.Close();
    public void Dispose() => _inner.Dispose();

    public bool GetBoolean(int i) => Convert.ToBoolean(GetValue(i));
    public byte GetByte(int i) => Convert.ToByte(GetValue(i));
    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => _inner.GetBytes(i, fieldOffset, buffer, bufferoffset, length);
    public char GetChar(int i) => Convert.ToChar(GetValue(i));
    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
    public IDataReader GetData(int i) => throw new NotSupportedException();
    public DateTime GetDateTime(int i) => Convert.ToDateTime(GetValue(i));
    public decimal GetDecimal(int i) => Convert.ToDecimal(GetValue(i));
    public double GetDouble(int i) => Convert.ToDouble(GetValue(i));
    public float GetFloat(int i) => Convert.ToSingle(GetValue(i));
    public Guid GetGuid(int i) => (Guid)GetValue(i);
    public short GetInt16(int i) => Convert.ToInt16(GetValue(i));
    public int GetInt32(int i) => Convert.ToInt32(GetValue(i));
    public long GetInt64(int i) => Convert.ToInt64(GetValue(i));
    public string GetString(int i) => Convert.ToString(GetValue(i))!;

    public IEnumerator GetEnumerator() {
        while (Read()) {
            yield return this;
        }
    }
}
