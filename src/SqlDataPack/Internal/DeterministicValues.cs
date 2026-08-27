using System.Globalization;

namespace SqlDataPack.Internal;

/// <summary>
/// Turns the bytes an <see cref="ExportSecret"/> derives into the shapes the built-in pseudonymizers need.
/// Pure functions of the hash, so two calls with the same hash always agree.
/// </summary>
internal static class DeterministicValues {
    private const string HexDigits = "0123456789abcdef";
    private const int StackLimit = 256;

    public static void Hex(ReadOnlySpan<byte> hash, Span<char> destination) {
        for (var i = 0; i < destination.Length; i++) {
            // Two characters per byte, wrapping if a caller asks for more than the hash holds.
            var value = hash[i / 2 % hash.Length];
            destination[i] = HexDigits[i % 2 == 0 ? value >> 4 : value & 0x0f];
        }
    }

    public static string Hex(ReadOnlySpan<byte> hash, int length) {
        // Never stackalloc a caller-supplied length: a StackOverflowException would take the process down
        // rather than fail the export.
        Span<char> stack = stackalloc char[StackLimit];
        Span<char> buffer = length <= StackLimit ? stack[..length] : new char[length];
        Hex(hash, buffer);
        return new string(buffer);
    }

    public static void Digits(ReadOnlySpan<byte> hash, Span<char> destination) {
        for (var i = 0; i < destination.Length; i++) {
            destination[i] = (char)('0' + hash[i % hash.Length] % 10);
        }
    }

    public static ulong UInt64(ReadOnlySpan<byte> hash) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash);

    /// <summary>Maps the hash into <c>[0, exclusiveMaximum)</c>. The modulo bias is irrelevant here: the range is tiny next to 2^64.</summary>
    public static ulong Below(ReadOnlySpan<byte> hash, ulong exclusiveMaximum) => exclusiveMaximum == 0 ? 0 : UInt64(hash) % exclusiveMaximum;

    public static Guid Guid(ReadOnlySpan<byte> hash) {
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x40); // version 4
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    /// <summary>
    /// Produces a non-negative decimal that fits <c>decimal(precision, scale)</c> exactly: at most
    /// <c>precision - scale</c> integral digits and exactly <paramref name="scale"/> fractional ones.
    /// </summary>
    public static decimal Decimal(ReadOnlySpan<byte> hash, byte precision, byte scale) {
        var integralDigits = Math.Max(0, precision - scale);
        var integral = integralDigits == 0 ? 0UL : Below(hash, Pow10(Math.Min(integralDigits, 18)));
        if (scale == 0) {
            return integral;
        }

        var fractionDigits = Math.Min(scale, (byte)18);
        var fraction = Below(hash[8..], Pow10(fractionDigits));
        var text = string.Create(CultureInfo.InvariantCulture, $"{integral}.{fraction.ToString(CultureInfo.InvariantCulture).PadLeft(fractionDigits, '0')}");
        return decimal.Parse(text, CultureInfo.InvariantCulture);
    }

    private static ulong Pow10(int exponent) {
        var result = 1UL;
        for (var i = 0; i < exponent; i++) {
            result *= 10;
        }

        return result;
    }
}
