using System.Collections.Frozen;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlTypes;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal static class ValueConverter {
    private static readonly FrozenDictionary<string, ColumnKind> KindsByTypeName = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase) {
        ["tinyint"] = ColumnKind.TinyInt,
        ["smallint"] = ColumnKind.SmallInt,
        ["int"] = ColumnKind.Int,
        ["bigint"] = ColumnKind.BigInt,
        ["bit"] = ColumnKind.Bit,

        ["real"] = ColumnKind.Real,
        ["float"] = ColumnKind.Float,

        ["decimal"] = ColumnKind.Decimal,
        ["numeric"] = ColumnKind.Decimal,
        ["money"] = ColumnKind.Decimal,
        ["smallmoney"] = ColumnKind.Decimal,

        ["char"] = ColumnKind.Text,
        ["varchar"] = ColumnKind.Text,
        ["text"] = ColumnKind.Text,
        ["nchar"] = ColumnKind.Text,
        ["nvarchar"] = ColumnKind.Text,
        ["ntext"] = ColumnKind.Text,

        ["date"] = ColumnKind.Date,
        ["datetime"] = ColumnKind.Date,
        ["datetime2"] = ColumnKind.Date,
        ["smalldatetime"] = ColumnKind.Date,
        ["datetimeoffset"] = ColumnKind.DateTimeOffset,
        ["time"] = ColumnKind.Time,

        ["uniqueidentifier"] = ColumnKind.UniqueIdentifier,
        ["xml"] = ColumnKind.Xml,
        ["json"] = ColumnKind.Json,
        ["vector"] = ColumnKind.Vector,

        ["binary"] = ColumnKind.Binary,
        ["varbinary"] = ColumnKind.Binary,
        ["image"] = ColumnKind.Binary,

        ["timestamp"] = ColumnKind.RowVersion,
        ["rowversion"] = ColumnKind.RowVersion,

        ["sql_variant"] = ColumnKind.Unsupported,
        ["geography"] = ColumnKind.Unsupported,
        ["geometry"] = ColumnKind.Unsupported,
        ["hierarchyid"] = ColumnKind.Unsupported
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the conversion behaviour for a SQL Server type name. Allocation-free, and the only
    /// place a type name is inspected on the per-cell path.
    /// </summary>
    public static ColumnKind KindFor(string typeName) {
        return KindsByTypeName.TryGetValue(typeName, out var kind) ? kind : ColumnKind.Unknown;
    }

    public static bool IsUnsupported(string typeName) {
        return KindFor(typeName) == ColumnKind.Unsupported;
    }

    public static bool IsServerGenerated(string typeName) {
        return KindFor(typeName) == ColumnKind.RowVersion;
    }

    public static string SqliteTypeFor(ColumnMetadata column) {
        return column.Kind switch {
            ColumnKind.TinyInt or ColumnKind.SmallInt or ColumnKind.Int or ColumnKind.BigInt or ColumnKind.Bit => "INTEGER",
            ColumnKind.Real or ColumnKind.Float => "REAL",
            ColumnKind.Binary or ColumnKind.RowVersion => "BLOB",
            ColumnKind.Decimal or ColumnKind.Text or ColumnKind.Date or ColumnKind.DateTimeOffset or ColumnKind.Time or ColumnKind.UniqueIdentifier or ColumnKind.Xml or ColumnKind.Json or ColumnKind.Vector => "TEXT",
            _ => throw new SqlDataPackException($"Unsupported SQL Server type '{column.SqlServerTypeName}' on {column.Table.FullName}.{column.Name}.")
        };
    }

    public static object? ToSqliteValue(object value, ColumnMetadata column) {
        return ToSqliteValue(value, column, column.Kind);
    }

    /// <summary>
    /// Converts one cell, with the column's kind supplied by the caller.
    /// </summary>
    /// <remarks>
    /// Callers on the per-cell hot path resolve <paramref name="kind"/> once per column and pass it in.
    /// Letting each cell go through <see cref="ColumnMetadata.Kind"/> instead costs a case-insensitive
    /// hash of the type name every time, which measurably outweighed the conversion itself for short
    /// values — around 6 ns per cell for a name as long as <c>uniqueidentifier</c>.
    /// </remarks>
    public static object? ToSqliteValue(object value, ColumnMetadata column, ColumnKind kind) {
        if (value is DBNull) {
            return DBNull.Value;
        }

        switch (kind) {
            case ColumnKind.Bit:
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0;

            case ColumnKind.UniqueIdentifier:
                return value is Guid guid ? guid.ToString("D") : Convert.ToString(value, CultureInfo.InvariantCulture);

            case ColumnKind.Xml:
                return value is SqlXml sqlXml ? sqlXml.Value : Convert.ToString(value, CultureInfo.InvariantCulture);

            case ColumnKind.Json:
                return value is SqlJson sqlJson ? sqlJson.Value : Convert.ToString(value, CultureInfo.InvariantCulture);

            case ColumnKind.Decimal:
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

            case ColumnKind.Date:
                return Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture);

            case ColumnKind.DateTimeOffset:
                return value is DateTimeOffset dto ? dto.ToString("O", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture);

            case ColumnKind.Time:
                return value is TimeSpan span ? span.ToString("c", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture);

            case ColumnKind.Vector:
                // float32 vectors arrive as SqlVector<float> (binary TDS); serialize the exact float bits as a
                // round-trippable JSON array. float16 vectors (preview) arrive as a varchar(max) JSON string,
                // which is already the canonical representation and is stored verbatim.
                return value is SqlVector<float> sqlVector ? JsonSerializer.Serialize(sqlVector.Memory.ToArray()) : Convert.ToString(value, CultureInfo.InvariantCulture);

            default:
                return value;
        }
    }

    public static object? FromSqliteValue(object? value, ColumnMetadata column) {
        return FromSqliteValue(value, column, column.Kind);
    }

    /// <summary>
    /// Converts one cell back to its SQL Server representation, with the column's kind supplied by the
    /// caller. See <see cref="ToSqliteValue(object, ColumnMetadata, ColumnKind)"/> for why the kind is
    /// hoisted rather than resolved per cell.
    /// </summary>
    public static object? FromSqliteValue(object? value, ColumnMetadata column, ColumnKind kind) {
        if (value is null || value is DBNull) {
            return DBNull.Value;
        }

        // The string form is materialised only by the branches that parse from one. Computing it up
        // front cost an allocation on every cell of every integer, float, and blob column for nothing.
        try {
            return kind switch {
                ColumnKind.TinyInt => Convert.ToByte(value, CultureInfo.InvariantCulture),
                ColumnKind.SmallInt => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                ColumnKind.Int => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                ColumnKind.BigInt => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ColumnKind.Bit => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
                ColumnKind.Real => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                ColumnKind.Float => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                ColumnKind.UniqueIdentifier => Guid.Parse(AsText(value)!),
                ColumnKind.Decimal => decimal.Parse(AsText(value)!, CultureInfo.InvariantCulture),
                ColumnKind.Date => DateTime.Parse(AsText(value)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ColumnKind.DateTimeOffset => DateTimeOffset.Parse(AsText(value)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ColumnKind.Time => TimeSpan.Parse(AsText(value)!, CultureInfo.InvariantCulture),
                ColumnKind.Xml => AsText(value),
                ColumnKind.Json => AsText(value),
                // float32 vectors are reconstructed as SqlVector<float> for native binary bulk copy.
                // float16 vectors (preview) round-trip as their varchar(max) JSON string.
                ColumnKind.Vector => column.VectorBaseType == 1 ? AsText(value) : new SqlVector<float>(JsonSerializer.Deserialize<float[]>(AsText(value)!) ?? Array.Empty<float>()),
                _ => value
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException or JsonException) {
            throw new SqlDataPackException($"SQLite package is invalid: value '{AsText(value)}' for {column.Table.FullName}.{column.Name} is not a valid {column.SqlServerTypeName}.", ex);
        }
    }

    private static string? AsText(object value) => Convert.ToString(value, CultureInfo.InvariantCulture);

    public static void BindSqliteParameter(SqliteParameter parameter, object? value) {
        parameter.Value = value ?? DBNull.Value;
    }
}
