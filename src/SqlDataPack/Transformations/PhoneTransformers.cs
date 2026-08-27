using SqlDataPack.Internal;

namespace SqlDataPack.Transformations;

/// <summary>
/// Configures <see cref="PhoneMasker"/>.
/// </summary>
public sealed class PhoneMaskerOptions {
    /// <summary>Trailing digits to keep, for example <c>4</c> to leave a recognisable last four. Defaults to <c>0</c>; at least one digit is always masked.</summary>
    public int PreserveLastDigits { get; set; }

    /// <summary>The character masked digits are replaced with. Defaults to <c>'X'</c>.</summary>
    public char MaskCharacter { get; set; } = 'X';
}

/// <summary>
/// Masks a phone number, keeping its punctuation: <c>(206) 555-1212</c> becomes <c>(XXX) XXX-XXXX</c>.
/// </summary>
/// <remarks>
/// Formatting is preserved by position rather than parsed, so <c>206-555-1212</c> and <c>2065551212</c> keep
/// their own shapes. Masking maps many numbers onto one output and does not preserve uniqueness.
/// </remarks>
public sealed class PhoneMasker : BuiltInTransformer {
    private readonly int preserveLastDigits;
    private readonly char maskCharacter;

    /// <summary>Initializes a new <see cref="PhoneMasker"/> with the default configuration.</summary>
    public PhoneMasker() : this(new PhoneMaskerOptions()) {
    }

    /// <summary>Initializes a new <see cref="PhoneMasker"/>.</summary>
    /// <param name="options">The masking configuration. Its values are copied; later edits to the object have no effect.</param>
    public PhoneMasker(PhoneMaskerOptions options) {
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
/// Configures <see cref="PhonePseudonymizer"/>.
/// </summary>
public sealed class PhonePseudonymizerOptions {
    /// <summary>Leading digits to keep, for example a country or area code. Defaults to <c>0</c>.</summary>
    public int PreserveLeadingDigits { get; set; }
}

/// <summary>
/// Replaces the digits of a phone number with deterministic ones, keeping the punctuation:
/// <c>(206) 555-1212</c> becomes something like <c>(417) 903-2286</c>.
/// </summary>
/// <remarks>
/// Deterministic within one export: the same source number produces the same result in every table and column.
/// A value with no digits at all is replaced with a ten-digit pseudonym rather than returned unchanged.
/// Uniqueness is not guaranteed.
/// </remarks>
public sealed class PhonePseudonymizer : BuiltInTransformer {
    private const int FallbackDigitCount = 10;

    private readonly int preserveLeadingDigits;

    /// <summary>Initializes a new <see cref="PhonePseudonymizer"/> with the default configuration.</summary>
    public PhonePseudonymizer() : this(new PhonePseudonymizerOptions()) {
    }

    /// <summary>Initializes a new <see cref="PhonePseudonymizer"/>.</summary>
    /// <param name="options">The pseudonymization configuration. Its values are copied; later edits to the object have no effect.</param>
    public PhonePseudonymizer(PhonePseudonymizerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreserveLeadingDigits);
        preserveLeadingDigits = options.PreserveLeadingDigits;
    }

    internal override string Configuration => Describe(("PreserveLeadingDigits", preserveLeadingDigits));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, text, hash);
        Span<char> digits = stackalloc char[ExportSecret.HashLength];
        DeterministicValues.Digits(hash, digits);

        var digitCount = DigitFormatting.CountDigits(text);
        if (digitCount == 0) {
            return new string(digits[..Math.Min(FallbackDigitCount, context.MaxLength ?? FallbackDigitCount)]);
        }

        // Never preserve every digit: that would hand back the source number.
        return DigitFormatting.Replace(text, digits, Math.Clamp(preserveLeadingDigits, 0, digitCount - 1));
    }
}
