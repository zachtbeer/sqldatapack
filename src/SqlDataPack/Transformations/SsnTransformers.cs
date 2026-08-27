using SqlDataPack.Internal;

namespace SqlDataPack.Transformations;

/// <summary>
/// Configures <see cref="SsnMasker"/>.
/// </summary>
public sealed class SsnMaskerOptions {
    /// <summary>Trailing digits to keep, for example <c>4</c> for a recognisable last four. Defaults to <c>0</c>; at least one digit is always masked.</summary>
    public int PreserveLastDigits { get; set; }

    /// <summary>The character masked digits are replaced with. Defaults to <c>'X'</c>.</summary>
    public char MaskCharacter { get; set; } = 'X';
}

/// <summary>
/// Masks a US Social Security Number, keeping its punctuation: <c>123-45-6789</c> becomes <c>XXX-XX-XXXX</c>
/// and <c>123456789</c> becomes <c>XXXXXXXXX</c>.
/// </summary>
/// <remarks>
/// Nothing here validates whether the number was ever issued. Masking maps many numbers onto one output and
/// does not preserve uniqueness. For a column this sensitive, consider excluding it from the export entirely
/// rather than scrubbing it.
/// </remarks>
public sealed class SsnMasker : BuiltInTransformer {
    private readonly int preserveLastDigits;
    private readonly char maskCharacter;

    /// <summary>Initializes a new <see cref="SsnMasker"/> with the default configuration.</summary>
    public SsnMasker() : this(new SsnMaskerOptions()) {
    }

    /// <summary>Initializes a new <see cref="SsnMasker"/>.</summary>
    /// <param name="options">The masking configuration. Its values are copied; later edits to the object have no effect.</param>
    public SsnMasker(SsnMaskerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreserveLastDigits);
        preserveLastDigits = options.PreserveLastDigits;
        maskCharacter = options.MaskCharacter;
    }

    internal override string Configuration => Describe(("PreserveLastDigits", preserveLastDigits), ("MaskCharacter", maskCharacter));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) => DigitFormatting.Mask(AsText(value), preserveLastDigits, maskCharacter);
}

/// <summary>
/// Replaces the digits of a US Social Security Number with deterministic ones, keeping the punctuation:
/// <c>123-45-6789</c> becomes something like <c>508-21-9374</c>, and <c>123456789</c> stays unpunctuated.
/// </summary>
/// <remarks>
/// Deterministic within one export. Nothing here validates whether either the source or the result was ever a
/// legitimately issued number. A value with no digits is replaced with a nine-digit pseudonym rather than
/// returned unchanged. Uniqueness is not guaranteed.
/// </remarks>
public sealed class SsnPseudonymizer : BuiltInTransformer {
    private const int SsnDigitCount = 9;

    /// <summary>Initializes a new <see cref="SsnPseudonymizer"/>.</summary>
    public SsnPseudonymizer() {
    }

    internal override string Configuration => string.Empty;

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, text, hash);
        Span<char> digits = stackalloc char[ExportSecret.HashLength];
        DeterministicValues.Digits(hash, digits);

        if (DigitFormatting.CountDigits(text) == 0) {
            return new string(digits[..Math.Min(SsnDigitCount, context.MaxLength ?? SsnDigitCount)]);
        }

        return DigitFormatting.Replace(text, digits, preserveLeadingDigits: 0);
    }
}
