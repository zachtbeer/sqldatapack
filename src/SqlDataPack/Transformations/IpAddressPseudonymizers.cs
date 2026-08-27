using System.Net;
using System.Net.Sockets;
using SqlDataPack.Internal;

namespace SqlDataPack.Transformations;

/// <summary>
/// Configures <see cref="IPv4Pseudonymizer"/>.
/// </summary>
public sealed class IPv4PseudonymizerOptions {
    /// <summary>Leading octets of the source address to keep, <c>0</c>–<c>3</c>. Defaults to <c>0</c>. Keeping octets preserves rough network locality at the cost of narrowing the pseudonym space.</summary>
    public int PreserveLeadingOctets { get; set; }
}

/// <summary>
/// Replaces an IPv4 address with a deterministic, syntactically valid IPv4 address:
/// <c>203.0.113.42</c> becomes something like <c>91.184.27.6</c>.
/// </summary>
/// <remarks>
/// Deterministic within one export, so the same address maps consistently across tables and columns. A value
/// that is not a parseable IPv4 address is replaced with a derived address rather than returned unchanged.
/// Uniqueness is not guaranteed.
/// </remarks>
public sealed class IPv4Pseudonymizer : BuiltInTransformer {
    private readonly int preserveLeadingOctets;

    /// <summary>Initializes a new <see cref="IPv4Pseudonymizer"/> with the default configuration.</summary>
    public IPv4Pseudonymizer() : this(new IPv4PseudonymizerOptions()) {
    }

    /// <summary>Initializes a new <see cref="IPv4Pseudonymizer"/>.</summary>
    /// <param name="options">The pseudonymization configuration. Its values are copied; later edits to the object have no effect.</param>
    public IPv4Pseudonymizer(IPv4PseudonymizerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PreserveLeadingOctets is < 0 or > 3) {
            throw new ArgumentException("IPv4Pseudonymizer PreserveLeadingOctets must be between 0 and 3.", nameof(options));
        }

        preserveLeadingOctets = options.PreserveLeadingOctets;
    }

    internal override string Configuration => Describe(("PreserveLeadingOctets", preserveLeadingOctets));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, text, hash);

        Span<byte> octets = stackalloc byte[4];
        hash[..4].CopyTo(octets);
        // 0 and 255 in the leading octet produce addresses no one would recognise as an address.
        octets[0] = (byte)(hash[0] % 254 + 1);

        var parsed = IpAddressParsing.Parse(text, AddressFamily.InterNetwork);
        if (parsed is not null) {
            var source = parsed.GetAddressBytes();
            for (var i = 0; i < preserveLeadingOctets; i++) {
                octets[i] = source[i];
            }
        }

        return new IPAddress(octets).ToString();
    }
}

/// <summary>
/// Configures <see cref="IPv6Pseudonymizer"/>.
/// </summary>
public sealed class IPv6PseudonymizerOptions {
    /// <summary>Leading 16-bit groups of the source address to keep, <c>0</c>–<c>7</c>. Defaults to <c>0</c>.</summary>
    public int PreserveLeadingGroups { get; set; }
}

/// <summary>
/// Replaces an IPv6 address with a deterministic, syntactically valid IPv6 address:
/// <c>2001:db8::8a2e:370:7334</c> becomes something like <c>9f2c:41b7:a5d3:e68f:...</c>.
/// </summary>
/// <remarks>
/// Deterministic within one export. A value that is not a parseable IPv6 address is replaced with a derived
/// address rather than returned unchanged. Output is rendered in .NET's canonical compressed form, so it may
/// be spelled differently from the source even where structure is preserved. Uniqueness is not guaranteed.
/// </remarks>
public sealed class IPv6Pseudonymizer : BuiltInTransformer {
    private readonly int preserveLeadingGroups;

    /// <summary>Initializes a new <see cref="IPv6Pseudonymizer"/> with the default configuration.</summary>
    public IPv6Pseudonymizer() : this(new IPv6PseudonymizerOptions()) {
    }

    /// <summary>Initializes a new <see cref="IPv6Pseudonymizer"/>.</summary>
    /// <param name="options">The pseudonymization configuration. Its values are copied; later edits to the object have no effect.</param>
    public IPv6Pseudonymizer(IPv6PseudonymizerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PreserveLeadingGroups is < 0 or > 7) {
            throw new ArgumentException("IPv6Pseudonymizer PreserveLeadingGroups must be between 0 and 7.", nameof(options));
        }

        preserveLeadingGroups = options.PreserveLeadingGroups;
    }

    internal override string Configuration => Describe(("PreserveLeadingGroups", preserveLeadingGroups));

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) {
        var text = AsText(value);
        Span<byte> hash = stackalloc byte[ExportSecret.HashLength];
        ComputeHash(context, text, hash);

        Span<byte> groups = stackalloc byte[16];
        hash[..16].CopyTo(groups);

        var parsed = IpAddressParsing.Parse(text, AddressFamily.InterNetworkV6);
        if (parsed is not null) {
            var source = parsed.GetAddressBytes();
            source.AsSpan(0, preserveLeadingGroups * 2).CopyTo(groups);
        }

        return new IPAddress(groups).ToString();
    }
}

internal static class IpAddressParsing {
    /// <summary>Parses an address of the wanted family, or returns <see langword="null"/> for anything else.</summary>
    public static IPAddress? Parse(string text, AddressFamily family) {
        return IPAddress.TryParse(text.Trim(), out var address) && address.AddressFamily == family ? address : null;
    }
}
