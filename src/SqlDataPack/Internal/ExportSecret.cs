using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace SqlDataPack.Internal;

/// <summary>
/// The random key behind the built-in deterministic pseudonymizers. One is generated per export and thrown
/// away with it: it is never written to the package, never handed to a custom transformer, and never
/// reproduced across exports, so the same source database exported twice yields different pseudonyms.
/// </summary>
/// <remarks>
/// HMAC rather than a bare hash on purpose. An unkeyed digest of an email address or a national identifier is
/// trivially reversible by dictionary attack, and the package is the artefact that leaves the building.
/// </remarks>
internal sealed class ExportSecret {
    /// <summary>The number of bytes every derivation produces (HMAC-SHA256).</summary>
    public const int HashLength = 32;

    private const int StackBufferSize = 256;

    private readonly byte[] _key;

    private ExportSecret(byte[] key) => _key = key;

    public static ExportSecret Create() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Derives <see cref="HashLength"/> bytes from a namespace and a source value. The namespace is what keeps
    /// two differently configured transformers in different deterministic spaces while the same transformer
    /// stays consistent across tables and columns.
    /// </summary>
    public void ComputeHash(string transformerNamespace, string value, Span<byte> destination) {
        // The unit separator cannot appear in a namespace (built from a type name and a rendered
        // configuration), so no pair of namespace/value can collide with a different pair.
        var maxBytes = Encoding.UTF8.GetMaxByteCount(transformerNamespace.Length + 1 + value.Length);
        byte[]? rented = maxBytes > StackBufferSize ? ArrayPool<byte>.Shared.Rent(maxBytes) : null;
        Span<byte> buffer = rented ?? stackalloc byte[StackBufferSize];
        try {
            var written = Encoding.UTF8.GetBytes(transformerNamespace, buffer);
            buffer[written++] = 0x1f;
            written += Encoding.UTF8.GetBytes(value, buffer[written..]);
            HMACSHA256.HashData(_key, buffer[..written], destination);
        }
        finally {
            if (rented is not null) {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
