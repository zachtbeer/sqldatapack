namespace SqlDataPack.Internal;

internal enum TypeDifference {
    None,
    Widening,
    Lossy
}

/// <summary>
/// Compares a source column's type to the target column's catalog type. A pure function of two
/// column descriptions, kept out of the async validator so it can be unit tested without a database.
/// </summary>
internal static class ColumnTypeComparer {
    private static readonly HashSet<string> CharacterTypes = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar" };
    private static readonly HashSet<string> DoubleByteCharacterTypes = new(StringComparer.OrdinalIgnoreCase) { "nchar", "nvarchar" };
    private static readonly HashSet<string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase) { "binary", "varbinary" };
    private static readonly HashSet<string> DecimalTypes = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };
    private static readonly HashSet<string> TimeScaledTypes = new(StringComparer.OrdinalIgnoreCase) { "datetime2", "datetimeoffset", "time" };

    public static TypeDifference Compare(ColumnMetadata source, string targetTypeName, short targetMaxLength, byte targetPrecision, byte targetScale, string? targetCollationName = null) {
        var sameType = string.Equals(source.SqlServerTypeName, targetTypeName, StringComparison.OrdinalIgnoreCase);
        var sameCollation = string.Equals(source.CollationName, targetCollationName, StringComparison.OrdinalIgnoreCase);

        // Length, precision/scale rules compare across the category (nvarchar -> varchar counts), not just
        // matching type names. Only the "everything matches" None result needs the type names to be equal.
        if (IsCategory(CharacterTypes, source.SqlServerTypeName, targetTypeName)) {
            // max_length is bytes (2/char for nchar/nvarchar, 1/char for char/varchar), but the category mixes
            // both, so compare in characters or a varchar->nvarchar move reads as same-length when it isn't.
            var lengthDifference = CompareLength(ToCharacterLength(source.SqlServerTypeName, source.MaxLength), ToCharacterLength(targetTypeName, targetMaxLength));
            if (lengthDifference == TypeDifference.Lossy || IsNarrowingEncoding(source.SqlServerTypeName, targetTypeName)) {
                return TypeDifference.Lossy;
            }

            return sameType && lengthDifference == TypeDifference.None && sameCollation ? TypeDifference.None : TypeDifference.Widening;
        }

        if (IsCategory(BinaryTypes, source.SqlServerTypeName, targetTypeName)) {
            var lengthDifference = CompareLength(source.MaxLength, targetMaxLength);
            if (lengthDifference == TypeDifference.Lossy) {
                return TypeDifference.Lossy;
            }

            return sameType && lengthDifference == TypeDifference.None && sameCollation ? TypeDifference.None : TypeDifference.Widening;
        }

        if (IsCategory(DecimalTypes, source.SqlServerTypeName, targetTypeName)) {
            if (targetPrecision < source.Precision || targetScale < source.Scale) {
                return TypeDifference.Lossy;
            }

            return sameType && targetPrecision == source.Precision && targetScale == source.Scale ? TypeDifference.None : TypeDifference.Widening;
        }

        if (IsCategory(TimeScaledTypes, source.SqlServerTypeName, targetTypeName)) {
            if (targetScale < source.Scale) {
                return TypeDifference.Lossy;
            }

            return sameType && targetScale == source.Scale && sameCollation ? TypeDifference.None : TypeDifference.Widening;
        }

        if (sameType) {
            return sameCollation ? TypeDifference.None : TypeDifference.Widening;
        }

        return TypeDifference.Widening;
    }

    private static bool IsCategory(HashSet<string> category, string sourceTypeName, string targetTypeName) {
        return category.Contains(sourceTypeName) && category.Contains(targetTypeName);
    }

    // nvarchar/nchar into varchar/char mangles any character outside the target's code page,
    // no matter how the lengths compare. The reverse direction is safe: varchar always fits in nvarchar.
    private static bool IsNarrowingEncoding(string sourceTypeName, string targetTypeName) {
        return DoubleByteCharacterTypes.Contains(sourceTypeName) && !DoubleByteCharacterTypes.Contains(targetTypeName);
    }

    // -1 is (max); keep it as -1 rather than halving so it still reads as wider than any positive length.
    private static short ToCharacterLength(string typeName, short maxLength) {
        return maxLength != -1 && DoubleByteCharacterTypes.Contains(typeName) ? (short)(maxLength / 2) : maxLength;
    }

    // -1 is (max), wider than any positive length.
    private static TypeDifference CompareLength(short sourceLength, short targetLength) {
        if (sourceLength == targetLength) {
            return TypeDifference.None;
        }

        if (sourceLength == -1) {
            return TypeDifference.Lossy;
        }

        if (targetLength == -1) {
            return TypeDifference.Widening;
        }

        return targetLength < sourceLength ? TypeDifference.Lossy : TypeDifference.Widening;
    }

    public static string Describe(ColumnMetadata source, string targetTypeName, short targetMaxLength, byte targetPrecision, byte targetScale, string? targetCollationName, TypeDifference difference) {
        var sourceType = RenderType(source.SqlServerTypeName, source.MaxLength, source.Precision, source.Scale);
        var targetType = RenderType(targetTypeName, targetMaxLength, targetPrecision, targetScale);
        var message = $"Target column '{source.Table.FullName}.{source.Name}' is {targetType} but the package holds {sourceType}.";

        if (difference != TypeDifference.Lossy) {
            return message;
        }

        if (CharacterTypes.Contains(targetTypeName)) {
            var lengthLossy = CompareLength(ToCharacterLength(source.SqlServerTypeName, source.MaxLength), ToCharacterLength(targetTypeName, targetMaxLength)) == TypeDifference.Lossy;
            var encodingLossy = IsNarrowingEncoding(source.SqlServerTypeName, targetTypeName);

            if (lengthLossy && encodingLossy) {
                return $"{message} Values longer than the target allows will be truncated, and characters outside the target's code page will be replaced or lost.";
            }

            if (encodingLossy) {
                return $"{message} Characters outside the target's code page will be replaced or lost.";
            }

            return $"{message} Values longer than the target allows will be truncated.";
        }

        if (BinaryTypes.Contains(targetTypeName)) {
            return $"{message} Values longer than the target allows will be truncated.";
        }

        if (DecimalTypes.Contains(targetTypeName)) {
            return $"{message} Values with more digits or decimal places than the target allows will be rounded.";
        }

        if (TimeScaledTypes.Contains(targetTypeName)) {
            return $"{message} Fractional seconds beyond the target's scale will be truncated.";
        }

        return message;
    }

    private static string RenderType(string typeName, short maxLength, byte precision, byte scale) {
        if (CharacterTypes.Contains(typeName) || BinaryTypes.Contains(typeName)) {
            if (maxLength == -1) {
                return $"{typeName}(max)";
            }

            // Display only: nchar/nvarchar store 2 bytes per character, so halve maxLength here to match
            // the length the user actually declared (NVARCHAR(100), not NVARCHAR(200)). Compare normalizes
            // separately through ToCharacterLength and never calls RenderType; keeping the two apart means a
            // change to what is displayed cannot silently move what is compared. Binary types are 1:1 and
            // stay unhalved on both paths.
            var displayLength = DoubleByteCharacterTypes.Contains(typeName) ? maxLength / 2 : maxLength;
            return $"{typeName}({displayLength})";
        }

        if (DecimalTypes.Contains(typeName)) {
            return $"{typeName}({precision},{scale})";
        }

        if (TimeScaledTypes.Contains(typeName)) {
            return $"{typeName}({scale})";
        }

        return typeName;
    }
}
