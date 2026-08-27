namespace SqlDataPack.Transformations;

/// <summary>
/// The shared digit handling behind the phone and SSN transformers: keep the punctuation a human recognises,
/// replace the digits that carry the information.
/// </summary>
/// <remarks>
/// Deliberately format-preserving rather than format-parsing. There is no telephone or identifier grammar in
/// here, and a value that carries no digits at all is still transformed — never returned as it arrived.
/// </remarks>
internal static class DigitFormatting {
    /// <summary>
    /// The characters that carry the value and so must never survive: every Unicode decimal digit rather than
    /// just the ASCII ones, plus surrogate halves. A fullwidth or Arabic-Indic digit treated as punctuation
    /// would be copied through untouched, leaking a real digit of the source.
    /// </summary>
    private static bool CarriesValue(char character) => char.IsDigit(character) || char.IsSurrogate(character);

    public static int CountDigits(string text) {
        var count = 0;
        foreach (var character in text) {
            if (CarriesValue(character)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>Replaces every digit with <paramref name="maskCharacter"/>, keeping the last <paramref name="preserveLastDigits"/> of them.</summary>
    public static string Mask(string text, int preserveLastDigits, char maskCharacter) {
        var digitCount = CountDigits(text);
        if (digitCount == 0) {
            // Nothing digit-shaped to mask, and returning the value unchanged would leak it.
            return new string(maskCharacter, Math.Max(text.Length, 1));
        }

        // Never preserve every digit: a fully preserved value is the original value.
        var preserved = Math.Clamp(preserveLastDigits, 0, digitCount - 1);
        var firstPreservedDigit = digitCount - preserved;

        var buffer = new char[text.Length];
        var seen = 0;
        for (var i = 0; i < text.Length; i++) {
            var character = text[i];
            if (!CarriesValue(character)) {
                buffer[i] = character;
                continue;
            }

            buffer[i] = seen++ < firstPreservedDigit ? maskCharacter : character;
        }

        return new string(buffer);
    }

    /// <summary>
    /// Rewrites the digits of <paramref name="text"/> from <paramref name="replacementDigits"/>, keeping the
    /// first <paramref name="preserveLeadingDigits"/> of the source and every non-digit character in place.
    /// </summary>
    public static string Replace(string text, ReadOnlySpan<char> replacementDigits, int preserveLeadingDigits) {
        var buffer = new char[text.Length];
        var seen = 0;
        for (var i = 0; i < text.Length; i++) {
            var character = text[i];
            if (!CarriesValue(character)) {
                buffer[i] = character;
                continue;
            }

            buffer[i] = seen < preserveLeadingDigits ? character : replacementDigits[seen % replacementDigits.Length];
            seen++;
        }

        return new string(buffer);
    }
}
