using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.Data.SqlTypes;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;

namespace SqlDataPack.Fuzzing;

/// <summary>
/// The two properties the per-cell converter has to hold: a value that cannot be parsed back out of
/// the package fails as a documented <see cref="SqlDataPackException"/> naming the column, and a value
/// that can be parsed back comes out bit-for-bit identical.
/// </summary>
public sealed class ValueConverterFuzzTests {
    private enum Mutation {
        Truncate,
        DropSeparator,
        InsertLetter,
        DigitToSymbol,
        OverflowField,
        FlipSign
    }

    private static readonly Gen<TypedText> NearlyValidText =
        from typed in Fuzz.TextPreserved
        from mutation in Gen.Elements(Enum.GetValues<Mutation>())
        from position in Gen.Choose(0, 4093)
        from symbol in Gen.Elements('?', '#', '*', '@', '~', '/', ' ', '\0')
        select typed with { Text = Mutate(typed.Text, mutation, position, symbol) };

    /// <summary>
    /// A hand-edited or version-mismatched package must not throw a raw <see cref="FormatException"/>,
    /// <see cref="OverflowException"/> or <see cref="JsonException"/> past a caller that only catches
    /// the library's exception, and the failure has to say which column and type it was.
    /// </summary>
    [FuzzProperty]
    public Property FromSqliteValue_MalformedText_FailsAsSqlDataPackException() =>
        Prop.ForAll(NearlyValidText.ToArbitrary(), c => {
            var column = Fuzz.Column(c.Type, c.VectorBaseType);

            Exception? escaped = null;
            try {
                ValueConverter.FromSqliteValue(c.Text, column);
            }
            catch (Exception ex) {
                escaped = ex;
            }

            // A mutation that happens to stay parseable is fine; only the failure path is under test.
            if (escaped is null) {
                return true;
            }

            var context = $"'{c.Text}' as {c.Type} (vector base type {c.VectorBaseType?.ToString() ?? "none"}) threw {escaped.GetType().FullName}: {escaped.Message}";
            escaped.ShouldBeOfType<SqlDataPackException>(context);
            escaped.Message.ShouldContain("dbo.Sample.Value", customMessage: context);
            escaped.Message.ShouldContain(c.Type, customMessage: context);

            // The raw framework exception is allowed to exist, but only wrapped.
            var inner = escaped.InnerException.ShouldNotBeNull(context);
            (inner is FormatException or OverflowException or ArgumentException or InvalidCastException or JsonException).ShouldBeTrue(context);
            return true;
        });

    /// <summary>
    /// Storing a value in the package and reading it back must not change it, for every
    /// <see cref="ColumnKind"/> the library supports.
    /// </summary>
    [FuzzProperty]
    public Property ToThenFromSqliteValue_RoundTripsEverySupportedType() =>
        Prop.ForAll(Fuzz.RoundTrippable.ToArbitrary(), tv => {
            var column = Fuzz.Column(tv.Type, tv.VectorBaseType);

            var stored = ValueConverter.ToSqliteValue(tv.Value, column);
            var restored = ValueConverter.FromSqliteValue(stored, column);

            ShouldMatch(tv, stored, restored);
            return true;
        });

    private static void ShouldMatch(TypedValue expected, object? stored, object? restored) {
        var context = $"{expected.Type} value stored as '{stored}'";

        switch (expected.Value) {
            case byte[] bytes:
                restored.ShouldBeOfType<byte[]>(context).SequenceEqual(bytes).ShouldBeTrue(context);
                break;

            case SqlVector<float> vector:
                restored.ShouldBeOfType<SqlVector<float>>(context).Memory.ToArray().SequenceEqual(vector.Memory.ToArray()).ShouldBeTrue(context);
                break;

            case string text:
                string.Equals(restored as string, text, StringComparison.Ordinal).ShouldBeTrue(context);
                break;

            case decimal number:
                // GetBits carries the scale, so this fails on 1.10 coming back as 1.1.
                decimal.GetBits(restored.ShouldBeOfType<decimal>(context)).ShouldBe(decimal.GetBits(number), context);
                break;

            case DateTime dateTime: {
                var back = restored.ShouldBeOfType<DateTime>(context);
                back.Ticks.ShouldBe(dateTime.Ticks, context);
                back.Kind.ShouldBe(dateTime.Kind, context);
                break;
            }

            case DateTimeOffset offset: {
                var back = restored.ShouldBeOfType<DateTimeOffset>(context);
                back.Ticks.ShouldBe(offset.Ticks, context);
                back.Offset.ShouldBe(offset.Offset, context);
                break;
            }

            case TimeSpan time:
                restored.ShouldBeOfType<TimeSpan>(context).Ticks.ShouldBe(time.Ticks, context);
                break;

            default:
                restored.ShouldBe(expected.Value, context);
                break;
        }
    }

    /// <summary>
    /// Damages one valid rendering just enough to break the parse. Pure random text almost never
    /// reaches the parsing code, so every case here starts from something the type would accept.
    /// </summary>
    private static string Mutate(string text, Mutation mutation, int position, char symbol) {
        if (text.Length == 0) {
            return symbol.ToString();
        }

        var index = position % text.Length;

        switch (mutation) {
            case Mutation.Truncate:
                return text[..index];

            case Mutation.DropSeparator:
                return text.Remove(PickIndex(text, c => !char.IsLetterOrDigit(c), position, index), 1);

            case Mutation.InsertLetter:
                return text.Insert(index, "q");

            case Mutation.DigitToSymbol: {
                var chars = text.ToCharArray();
                chars[PickIndex(text, char.IsDigit, position, index)] = symbol;
                return new string(chars);
            }

            case Mutation.OverflowField:
                return text.Insert(index, "99999999999999999999");

            case Mutation.FlipSign:
                return text.StartsWith('-') ? text[1..] : "-" + text;

            default:
                return text;
        }
    }

    private static int PickIndex(string text, Func<char, bool> predicate, int position, int fallback) {
        var matches = new List<int>();
        for (var i = 0; i < text.Length; i++) {
            if (predicate(text[i])) {
                matches.Add(i);
            }
        }

        return matches.Count > 0 ? matches[position % matches.Count] : fallback;
    }
}
