using SqlDataPack.Internal;
using SqlDataPack.Models;

namespace SqlDataPack.Transformations;

/// <summary>
/// The base class the transformers shipped with SqlDataPack derive from. It cannot be derived from outside the
/// library — write an <see cref="IValueTransformer"/> (or use <see cref="CustomTransformer"/>) instead.
/// </summary>
/// <remarks>
/// <para>
/// Built-ins are synchronous and hold no per-row state. The pseudonymizers among them are deterministic
/// <em>within one export</em>: the same source value, transformer type, and configuration produce the same
/// result in every table and column of that export, and a different result in the next export.
/// </para>
/// <para>
/// Built-in pseudonymizers are designed to minimize collisions, but SqlDataPack does not guarantee that unique
/// constraints survive transformation, and built-in maskers deliberately map many source values onto one
/// output. Use a custom transformer when uniqueness has to hold.
/// </para>
/// </remarks>
public abstract class BuiltInTransformer : IValueTransformer {
    private string? cachedNamespace;

    // Internal so the type cannot be derived from outside the library: one transformation concept, one
    // extension point, and the export secret stays reachable only by transformers we ship.
    internal BuiltInTransformer() {
    }

    /// <inheritdoc />
    public abstract object? Transform(TransformContext context, object value);

    /// <summary>
    /// The non-secret configuration recorded in the package, rendered as <c>Name=value;Name=value</c>.
    /// Empty when the transformer has nothing to configure.
    /// </summary>
    internal abstract string Configuration { get; }

    /// <summary>
    /// The deterministic namespace a pseudonymizer derives in. Two instances of the same type with the same
    /// configuration share it; a different configuration is a different namespace on purpose.
    /// </summary>
    private string Namespace => cachedNamespace ??= GetType().Name + "|" + Configuration;

    /// <summary>Derives 32 bytes for a source value in this transformer's deterministic namespace.</summary>
    private protected void ComputeHash(TransformContext context, string value, Span<byte> destination) {
        var secret = context.Secret ?? throw new SqlDataPackException($"Built-in transformer '{GetType().Name}' can only run inside an export: it derives pseudonyms from the export-scoped secret, which the supplied TransformContext does not carry.");
        secret.ComputeHash(Namespace, value, destination);
    }

    private protected static string AsText(object value) => value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Renders one configuration entry, skipping the ones left unset.</summary>
    private protected static string Describe(params (string Name, object? Value)[] entries) {
        return string.Join(";", entries.Where(entry => entry.Value is not null).Select(entry => $"{entry.Name}={entry.Value}"));
    }
}
