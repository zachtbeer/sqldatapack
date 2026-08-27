namespace SqlDataPack.Transformations;

/// <summary>
/// Configures <see cref="NameMasker"/>.
/// </summary>
public sealed class NameMaskerOptions {
    /// <summary>Leading characters of the source name to keep. Defaults to <c>1</c>.</summary>
    public int PreserveCharacters { get; set; } = 1;

    /// <summary>Text placed before the preserved characters. Defaults to <see langword="null"/>.</summary>
    public string? Prefix { get; set; }

    /// <summary>Text placed after the preserved characters. Defaults to <c>***</c>. Must be non-empty when <see cref="Prefix"/> is not set.</summary>
    public string? Suffix { get; set; } = "***";
}

/// <summary>
/// Masks a personal name by keeping a few leading characters and replacing the rest:
/// with <c>PreserveCharacters = 2</c> and <c>Suffix = "test"</c>, <c>John</c> becomes <c>Jotest</c> and
/// <c>McCain</c> becomes <c>Mctest</c>.
/// </summary>
/// <remarks>
/// One masker covers first names, last names, and full names — the difference is which column you bind it to,
/// not what the masking does. Masking maps many names onto one output and does not preserve uniqueness.
/// </remarks>
public sealed class NameMasker : BuiltInTransformer {
    private readonly int preserveCharacters;
    private readonly string prefix;
    private readonly string suffix;

    /// <summary>Initializes a new <see cref="NameMasker"/> with the default configuration.</summary>
    public NameMasker() : this(new NameMaskerOptions()) {
    }

    /// <summary>Initializes a new <see cref="NameMasker"/>.</summary>
    /// <param name="options">The masking configuration. Its values are copied; later edits to the object have no effect.</param>
    public NameMasker(NameMaskerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreserveCharacters);
        prefix = options.Prefix ?? string.Empty;
        suffix = options.Suffix ?? string.Empty;
        if (prefix.Length == 0 && suffix.Length == 0) {
            // Without either, masking a name that is short enough would hand back the name itself.
            throw new ArgumentException("NameMasker needs a Prefix or a Suffix.", nameof(options));
        }

        preserveCharacters = options.PreserveCharacters;
    }

    internal override string Configuration => Describe(("PreserveCharacters", preserveCharacters), ("Prefix", prefix.Length == 0 ? null : prefix), ("Suffix", suffix.Length == 0 ? null : suffix));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        // Walk the preserved count down rather than ever return the source: 'Jotest' masked with
        // PreserveCharacters = 2 and Suffix = 'test' would otherwise reproduce itself exactly.
        for (var preserved = Math.Min(preserveCharacters, text.Length); preserved >= 0; preserved--) {
            var masked = string.Concat(prefix, text.AsSpan(0, preserved), suffix);
            if (!string.Equals(masked, text, StringComparison.Ordinal)) {
                return masked;
            }
        }

        return prefix + suffix;
    }
}
