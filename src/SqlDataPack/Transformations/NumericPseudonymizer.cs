using System.Globalization;
using SqlDataPack.Internal;
using SqlDataPack.Models;

namespace SqlDataPack.Transformations;

/// <summary>
/// Replaces a numeric value with a deterministic one that fits the destination column's type, range,
/// precision, and scale.
/// </summary>
/// <remarks>
/// <para>
/// Values are non-negative and stay inside the column's own range: <c>tinyint</c> yields <c>0</c>–<c>255</c>,
/// <c>decimal(9,2)</c> yields at most seven integral digits and exactly two decimals. Bind it to an integer,
/// decimal, money, or floating-point column; anything else fails the export rather than guessing.
/// </para>
/// <para>
/// Deterministic within one export, so the same source number pseudonymizes identically across tables and
/// columns of the same type. Narrow types collide readily by construction — <c>tinyint</c> has 256 possible
/// outputs — so uniqueness is not guaranteed.
/// </para>
/// </remarks>
public sealed class NumericPseudonymizer : BuiltInTransformer {
    /// <summary>Initializes a new <see cref="NumericPseudonymizer"/>.</summary>
    public NumericPseudonymizer() {
    }

    internal override string Configuration => string.Empty;

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, hash);

        return ValueConverter.KindFor(context.SqlServerTypeName) switch {
            ColumnKind.TinyInt => (byte)DeterministicValues.Below(hash, byte.MaxValue + 1UL),
            ColumnKind.SmallInt => (short)DeterministicValues.Below(hash, (ulong)short.MaxValue + 1),
            ColumnKind.Int => (int)DeterministicValues.Below(hash, (ulong)int.MaxValue + 1),
            ColumnKind.BigInt => (long)DeterministicValues.Below(hash, long.MaxValue),
            ColumnKind.Decimal => DeterministicValues.Decimal(hash, context.Precision, context.Scale),
            // Three decimals below a million: comfortably inside float's exact-integer range, so the
            // real and float cases agree on the same source value.
            ColumnKind.Real => (float)(DeterministicValues.Below(hash, 1_000_000_000) / 1000d),
            ColumnKind.Float => DeterministicValues.Below(hash, 1_000_000_000) / 1000d,
            _ => throw new SqlDataPackException($"NumericPseudonymizer cannot transform {context.ColumnPath}: '{context.SqlServerTypeName}' is not a numeric SQL Server type. Use a string transformer or a custom transformer for this column.")
        };
    }
}

/// <summary>
/// Replaces a <c>uniqueidentifier</c> with a deterministic GUID.
/// </summary>
/// <remarks>
/// Deterministic within one export: the same source GUID maps to the same replacement everywhere it appears,
/// so a key and the rows referencing it stay joined. Output is a well-formed version 4 GUID. Collisions are
/// vanishingly unlikely but not guaranteed impossible.
/// </remarks>
public sealed class GuidPseudonymizer : BuiltInTransformer {
    /// <summary>Initializes a new <see cref="GuidPseudonymizer"/>.</summary>
    public GuidPseudonymizer() {
    }

    internal override string Configuration => string.Empty;

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        // Normalized so a GUID that arrives as a string and one that arrives as a Guid agree.
        var text = value is Guid guid ? guid.ToString("D") : AsText(value);
        if (Guid.TryParse(text, out var parsed)) {
            text = parsed.ToString("D");
        }

        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, text, hash);
        var pseudonym = DeterministicValues.Guid(hash);
        // A GUID kept in a char/varchar column is common enough to be worth handling here rather than
        // failing the export on a type the destination cannot hold.
        return ValueConverter.KindFor(context.SqlServerTypeName) == ColumnKind.Text ? pseudonym.ToString("D") : pseudonym;
    }
}
