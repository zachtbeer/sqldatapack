using System.Globalization;
using Microsoft.Data.SqlTypes;
using SqlDataPack.Models;
using SqlDataPack.Transformations;

namespace SqlDataPack.Internal;

/// <summary>
/// One configured transformer bound to one column of one export. Everything that can be resolved per column —
/// the context, the column's kind, its length limit, the error text's column path — is resolved here, once,
/// so the per-cell path is an interface call plus the output checks.
/// </summary>
internal sealed class ColumnTransform {
    private readonly IValueTransformer transformer;
    private readonly TransformContext context;
    private readonly ColumnMetadata column;
    private readonly ColumnKind kind;
    private readonly int? maxLength;
    private readonly string columnPath;
    private readonly string transformerName;

    public ColumnTransform(IValueTransformer transformer, ColumnMetadata column, ExportSecret? secret) {
        this.transformer = transformer;
        this.column = column;
        kind = column.Kind;
        maxLength = MaxLengthOf(column);
        columnPath = $"{column.Table.FullName}.{column.Name}";
        transformerName = TransformationNaming.TypeNameFor(transformer);
        context = new TransformContext(column.Table.Schema, column.Table.Name, column.Name, column.SqlServerTypeName, column.IsNullable, maxLength, column.Precision, column.Scale, secret);
    }

    /// <summary>
    /// Transforms one non-null source value and validates the result against the destination column's
    /// contract. Every failure path throws: transformation never falls back to the source value, never
    /// substitutes NULL, and never truncates an oversized result.
    /// </summary>
    public object Apply(object value) {
        object? result;
        try {
            result = transformer.Transform(context, value);
        }
        catch (SqlDataPackException) {
            throw;
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new SqlDataPackException($"Transformer '{transformerName}' failed on {columnPath}: {ex.Message}", ex);
        }

        if (result is null or DBNull) {
            if (!column.IsNullable) {
                throw new SqlDataPackException($"Transformer '{transformerName}' returned NULL for {columnPath}, which is not nullable. Return a value the column can hold, or exclude the column from the export.");
            }

            return DBNull.Value;
        }

        return Validate(result);
    }

    /// <summary>
    /// Checks the transformed value against the destination column contract: type, length, precision, and
    /// scale. Returns the value to write, normalizing only where the destination is unambiguous.
    /// </summary>
    private object Validate(object result) {
        switch (kind) {
            case ColumnKind.Text:
            case ColumnKind.Xml:
            case ColumnKind.Json:
                var text = result as string ?? throw TypeMismatch(result, "a string");
                if (maxLength is { } limit && text.Length > limit) {
                    throw new SqlDataPackException($"Transformer '{transformerName}' returned {text.Length} characters for {columnPath}, which holds at most {limit}. SqlDataPack does not truncate a transformed value; shorten it in the transformer or widen the column.");
                }

                return text;

            case ColumnKind.Binary:
            case ColumnKind.RowVersion:
                var bytes = result as byte[] ?? throw TypeMismatch(result, "a byte[]");
                if (maxLength is { } byteLimit && bytes.Length > byteLimit) {
                    throw new SqlDataPackException($"Transformer '{transformerName}' returned {bytes.Length} bytes for {columnPath}, which holds at most {byteLimit}. SqlDataPack does not truncate a transformed value; shorten it in the transformer or widen the column.");
                }

                return bytes;

            case ColumnKind.Bit:
                return result switch {
                    bool => result,
                    _ when TryAsInt64(result, out var bit) && bit is 0 or 1 => bit == 1,
                    _ => throw TypeMismatch(result, "a bool, 0, or 1")
                };

            case ColumnKind.TinyInt:
                return (byte)RequireInRange(result, byte.MinValue, byte.MaxValue);

            case ColumnKind.SmallInt:
                return (short)RequireInRange(result, short.MinValue, short.MaxValue);

            case ColumnKind.Int:
                return (int)RequireInRange(result, int.MinValue, int.MaxValue);

            case ColumnKind.BigInt:
                return RequireInRange(result, long.MinValue, long.MaxValue);

            case ColumnKind.Decimal:
                return RequireDecimal(result);

            case ColumnKind.Real:
                return RequireReal(result);

            case ColumnKind.Float:
                return RequireFloat(result);

            case ColumnKind.Date:
                return result as DateTime? ?? throw TypeMismatch(result, "a DateTime");

            case ColumnKind.DateTimeOffset:
                return result as DateTimeOffset? ?? throw TypeMismatch(result, "a DateTimeOffset");

            case ColumnKind.Time:
                return result as TimeSpan? ?? throw TypeMismatch(result, "a TimeSpan");

            case ColumnKind.UniqueIdentifier:
                return result switch {
                    Guid => result,
                    string candidate when Guid.TryParse(candidate, out var parsed) => parsed,
                    _ => throw TypeMismatch(result, "a Guid")
                };

            case ColumnKind.Vector:
                return result is string or SqlVector<float> ? result : throw TypeMismatch(result, "a string or SqlVector<float>");

            default:
                // Unknown types pass through untransformed on the normal path too; Unsupported ones never
                // reach here, because the export plan rejects them before any row is read.
                return result;
        }
    }

    private long RequireInRange(object result, long minimum, long maximum) {
        if (!TryAsInt64(result, out var number)) {
            throw TypeMismatch(result, "an integer");
        }

        if (number < minimum || number > maximum) {
            throw new SqlDataPackException($"Transformer '{transformerName}' returned {number.ToString(CultureInfo.InvariantCulture)} for {columnPath}, which is outside the range of '{column.SqlServerTypeName}' ({minimum} to {maximum}).");
        }

        return number;
    }

    private object RequireDecimal(object result) {
        decimal number;
        switch (result) {
            case decimal value:
                number = value;
                break;
            case double or float:
                throw TypeMismatch(result, "a decimal");
            default:
                if (!TryAsInt64(result, out var integral)) {
                    throw TypeMismatch(result, "a decimal");
                }

                number = integral;
                break;
        }

        var scale = (byte)((decimal.GetBits(number)[3] >> 16) & 0xff);
        if (scale > column.Scale) {
            throw new SqlDataPackException($"Transformer '{transformerName}' returned {number.ToString(CultureInfo.InvariantCulture)} for {columnPath}, which has scale {column.Scale}. SqlDataPack does not round a transformed value; return one with at most {column.Scale} decimal places.");
        }

        var integralDigits = Math.Max(column.Precision - column.Scale, 0);
        // 10^integralDigits as a decimal: the first magnitude the column cannot hold.
        var ceiling = 1m;
        for (var i = 0; i < integralDigits; i++) {
            ceiling *= 10m;
        }

        if (Math.Abs(number) >= ceiling) {
            throw new SqlDataPackException($"Transformer '{transformerName}' returned {number.ToString(CultureInfo.InvariantCulture)} for {columnPath}, which is '{column.SqlServerTypeName}({column.Precision},{column.Scale})' and holds at most {integralDigits} digits before the decimal point.");
        }

        return number;
    }

    private object RequireReal(object result) {
        var number = result switch {
            float value => value,
            double value => (float)value,
            _ when TryAsInt64(result, out var integral) => integral,
            _ => throw TypeMismatch(result, "a float or double")
        };

        if (float.IsNaN(number) || float.IsInfinity(number)) {
            throw new SqlDataPackException($"Transformer '{transformerName}' returned {number.ToString(CultureInfo.InvariantCulture)} for {columnPath}, which 'real' cannot hold.");
        }

        return number;
    }

    private object RequireFloat(object result) {
        var number = result switch {
            double value => value,
            float value => value,
            _ when TryAsInt64(result, out var integral) => integral,
            _ => throw TypeMismatch(result, "a float or double")
        };

        if (double.IsNaN(number) || double.IsInfinity(number)) {
            throw new SqlDataPackException($"Transformer '{transformerName}' returned {number.ToString(CultureInfo.InvariantCulture)} for {columnPath}, which 'float' cannot hold.");
        }

        return number;
    }

    private static bool TryAsInt64(object value, out long number) {
        switch (value) {
            case sbyte or byte or short or ushort or int or uint or long:
                number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case ulong unsigned when unsigned <= long.MaxValue:
                number = (long)unsigned;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private SqlDataPackException TypeMismatch(object result, string expected) {
        return new SqlDataPackException($"Transformer '{transformerName}' returned a {result.GetType().Name} for {columnPath}, which is '{column.SqlServerTypeName}' and needs {expected}.");
    }

    /// <summary>
    /// The column's length limit — characters for string types, bytes for binary — or <see langword="null"/>
    /// when there is none. <c>max</c> types report <c>-1</c>; the legacy LOB types (<c>text</c>, <c>ntext</c>,
    /// <c>image</c>) report the 16-byte pointer size, which is not a value limit at all.
    /// </summary>
    internal static int? MaxLengthOf(ColumnMetadata column) {
        if (column.MaxLength < 0) {
            return null;
        }

        return column.SqlServerTypeName.ToLowerInvariant() switch {
            "char" or "varchar" or "binary" or "varbinary" => column.MaxLength,
            "nchar" or "nvarchar" => column.MaxLength / 2,
            _ => null
        };
    }
}
