using SqlDataPack.Internal;

namespace SqlDataPack.Transformations;

/// <summary>
/// Configures <see cref="StringMasker"/>.
/// </summary>
public sealed class StringMaskerOptions {
    /// <summary>Leading characters of the source value to keep. Defaults to <c>0</c>.</summary>
    public int PreserveCharacters { get; set; }

    /// <summary>The character masked positions are replaced with. Defaults to <c>'*'</c>.</summary>
    public char MaskCharacter { get; set; } = '*';

    /// <summary>Fixed number of mask characters to emit. Defaults to <see langword="null"/>, which masks each remaining source character one for one.</summary>
    public int? MaskLength { get; set; }
}

/// <summary>
/// Masks any string value: <c>ACME-1234</c> becomes <c>*********</c>, or <c>AC*******</c> with
/// <c>PreserveCharacters = 2</c>.
/// </summary>
/// <remarks>
/// The generic fallback for a sensitive column with no semantic structure worth keeping. It maps many values
/// onto one output and does not preserve uniqueness. Note that a mask which preserves the source length still
/// reveals that length.
/// </remarks>
public sealed class StringMasker : BuiltInTransformer {
    private readonly int preserveCharacters;
    private readonly char maskCharacter;
    private readonly int? maskLength;

    /// <summary>Initializes a new <see cref="StringMasker"/> with the default configuration.</summary>
    public StringMasker() : this(new StringMaskerOptions()) {
    }

    /// <summary>Initializes a new <see cref="StringMasker"/>.</summary>
    /// <param name="options">The masking configuration. Its values are copied; later edits to the object have no effect.</param>
    public StringMasker(StringMaskerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreserveCharacters);
        if (options.MaskLength is <= 0) {
            throw new ArgumentException("StringMasker MaskLength must be greater than zero when set.", nameof(options));
        }

        preserveCharacters = options.PreserveCharacters;
        maskCharacter = options.MaskCharacter;
        maskLength = options.MaskLength;
    }

    internal override string Configuration => Describe(("PreserveCharacters", preserveCharacters), ("MaskCharacter", maskCharacter), ("MaskLength", maskLength));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        // Never preserve the whole value: one masked character is the floor.
        var preserved = Math.Clamp(preserveCharacters, 0, Math.Max(text.Length - 1, 0));
        var masks = maskLength ?? Math.Max(text.Length - preserved, 1);
        return string.Concat(text.AsSpan(0, preserved), new string(maskCharacter, masks));
    }
}

/// <summary>
/// Configures <see cref="StringPseudonymizer"/>.
/// </summary>
public sealed class StringPseudonymizerOptions {
    /// <summary>Length of the generated hexadecimal token. Defaults to <c>16</c>.</summary>
    public int Length { get; set; } = 16;

    /// <summary>Text placed before the token, for example <c>TEST-</c>. Defaults to <see langword="null"/>.</summary>
    public string? Prefix { get; set; }
}

/// <summary>
/// Replaces any string value with a deterministic token: <c>ACME-1234</c> becomes something like
/// <c>9f2c41b70a5d3e68</c>.
/// </summary>
/// <remarks>
/// Deterministic within one export, so a value that appears in several tables keeps lining up. Collisions are
/// unlikely at the default length but not impossible; uniqueness is not guaranteed.
/// </remarks>
public sealed class StringPseudonymizer : BuiltInTransformer {
    private const int MaximumLength = 256;

    private readonly int length;
    private readonly string prefix;

    /// <summary>Initializes a new <see cref="StringPseudonymizer"/> with the default configuration.</summary>
    public StringPseudonymizer() : this(new StringPseudonymizerOptions()) {
    }

    /// <summary>Initializes a new <see cref="StringPseudonymizer"/>.</summary>
    /// <param name="options">The pseudonymization configuration. Its values are copied; later edits to the object have no effect.</param>
    public StringPseudonymizer(StringPseudonymizerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Length, MaximumLength);
        length = options.Length;
        prefix = options.Prefix ?? string.Empty;
    }

    internal override string Configuration => Describe(("Length", length), ("Prefix", prefix.Length == 0 ? null : prefix));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, AsText(value), hash);
        return prefix + DeterministicValues.Hex(hash, length);
    }
}
