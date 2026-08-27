using SqlDataPack.Internal;

namespace SqlDataPack.Transformations;

/// <summary>
/// Configures <see cref="EmailMasker"/>.
/// </summary>
public sealed class EmailMaskerOptions {
    /// <summary>Leading characters of the local part to keep. Defaults to <c>1</c>; at least one character is always masked.</summary>
    public int PreserveCharacters { get; set; } = 1;

    /// <summary>The character masked positions are replaced with. Defaults to <c>'*'</c>.</summary>
    public char MaskCharacter { get; set; } = '*';

    /// <summary>Replacement domain, for example <c>example.invalid</c>. Defaults to <see langword="null"/>, which keeps the source domain.</summary>
    public string? Domain { get; set; }
}

/// <summary>
/// Masks an email address, keeping it email-shaped: <c>jane.doe@contoso.com</c> becomes <c>j*******@contoso.com</c>.
/// </summary>
/// <remarks>
/// Masking is lossy on purpose — many addresses map to one output — so it does not preserve uniqueness. A value
/// that is not email-shaped is still masked and given an <c>example.invalid</c> domain rather than passed through.
/// </remarks>
public sealed class EmailMasker : BuiltInTransformer {
    private const string FallbackDomain = "example.invalid";

    private readonly int preserveCharacters;
    private readonly char maskCharacter;
    private readonly string? domain;

    /// <summary>Initializes a new <see cref="EmailMasker"/> with the default configuration.</summary>
    public EmailMasker() : this(new EmailMaskerOptions()) {
    }

    /// <summary>Initializes a new <see cref="EmailMasker"/>.</summary>
    /// <param name="options">The masking configuration. Its values are copied; later edits to the object have no effect.</param>
    public EmailMasker(EmailMaskerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreserveCharacters);
        preserveCharacters = options.PreserveCharacters;
        maskCharacter = options.MaskCharacter;
        domain = string.IsNullOrWhiteSpace(options.Domain) ? null : options.Domain.Trim();
    }

    internal override string Configuration => Describe(("PreserveCharacters", preserveCharacters), ("MaskCharacter", maskCharacter), ("Domain", domain));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        var (local, sourceDomain) = EmailParts.Split(text);
        var preserved = Math.Clamp(preserveCharacters, 0, Math.Max(local.Length - 1, 0));
        var masked = local.Length == 0 ? new string(maskCharacter, 1) : string.Concat(local.AsSpan(0, preserved), new string(maskCharacter, local.Length - preserved));
        return masked + "@" + (domain ?? sourceDomain ?? FallbackDomain);
    }
}

/// <summary>
/// Configures <see cref="EmailPseudonymizer"/>.
/// </summary>
public sealed class EmailPseudonymizerOptions {
    /// <summary>The domain pseudonymized addresses are given. Defaults to <c>example.invalid</c>, which is guaranteed never to resolve.</summary>
    public string Domain { get; set; } = "example.invalid";

    /// <summary>Keeps the source address's own domain instead of <see cref="Domain"/>. Defaults to <see langword="false"/>.</summary>
    public bool PreserveDomain { get; set; }
}

/// <summary>
/// Replaces an email address with a deterministic pseudonym: <c>jane.doe@contoso.com</c> becomes something like
/// <c>u3f19c0a84be7d215@example.invalid</c>.
/// </summary>
/// <remarks>
/// Deterministic within one export — the same address maps to the same pseudonym in every table and column, so
/// joins across <c>Customers.Email</c> and <c>Orders.ContactEmail</c> survive — and different across exports.
/// Collisions are unlikely but not impossible; uniqueness is not guaranteed.
/// </remarks>
public sealed class EmailPseudonymizer : BuiltInTransformer {
    private const int DefaultTokenLength = 16;
    private const int MinimumTokenLength = 8;

    private readonly string domain;
    private readonly bool preserveDomain;

    /// <summary>Initializes a new <see cref="EmailPseudonymizer"/> with the default configuration.</summary>
    public EmailPseudonymizer() : this(new EmailPseudonymizerOptions()) {
    }

    /// <summary>Initializes a new <see cref="EmailPseudonymizer"/>.</summary>
    /// <param name="options">The pseudonymization configuration. Its values are copied; later edits to the object have no effect.</param>
    public EmailPseudonymizer(EmailPseudonymizerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Domain)) {
            throw new ArgumentException("EmailPseudonymizer needs a domain.", nameof(options));
        }

        domain = options.Domain.Trim();
        preserveDomain = options.PreserveDomain;
    }

    internal override string Configuration => Describe(("Domain", domain), ("PreserveDomain", preserveDomain));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, text, hash);

        var (_, sourceDomain) = EmailParts.Split(text);
        var resolvedDomain = (preserveDomain ? sourceDomain : null) ?? domain;

        // Shrink the token rather than overflow a narrow column; the output validator still rejects
        // anything that cannot be made to fit, instead of truncating it.
        var available = context.MaxLength is { } max ? max - resolvedDomain.Length - 2 : DefaultTokenLength;
        var tokenLength = Math.Clamp(available, MinimumTokenLength, DefaultTokenLength);
        return "u" + DeterministicValues.Hex(hash, tokenLength) + "@" + resolvedDomain;
    }
}

internal static class EmailParts {
    /// <summary>
    /// Splits at the last <c>@</c>. Anything without a usable local part and domain is treated as a local part
    /// with no domain, so a malformed value is still transformed rather than returned as it arrived.
    /// </summary>
    public static (string Local, string? Domain) Split(string text) {
        var at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1) {
            return (text, null);
        }

        return (text[..at], text[(at + 1)..]);
    }
}
