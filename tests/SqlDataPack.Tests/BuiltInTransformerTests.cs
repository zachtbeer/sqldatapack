using System.Net;
using System.Net.Sockets;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using SqlDataPack.Transformations;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// The transformers shipped with the library: what they produce for representative and malformed input, and
/// the determinism contract — consistent within one export across tables and columns, different across
/// exports, and different again for a different configuration.
/// </summary>
/// <remarks>
/// Every assertion that matters here is "the original never comes back", not "the output looks nice". The
/// built-ins are best-effort about preserving structure and absolute about not returning the source value.
/// </remarks>
public sealed class BuiltInTransformerTests {
    private static readonly ExportSecret Export = ExportSecret.Create();

    [Theory]
    [InlineData("jane.doe@contoso.com", "j*******@contoso.com")]
    [InlineData("a@contoso.com", "*@contoso.com")]
    [InlineData("UPPER@Contoso.COM", "U****@Contoso.COM")]
    public void EmailMasker_KeepsTheAddressEmailShaped(string source, string expected) {
        new EmailMasker().Transform(Context("Email", "nvarchar", 200), source).ShouldBe(expected);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@contoso.com")]
    [InlineData("someone@")]
    [InlineData("")]
    [InlineData("   ")]
    public void EmailMasker_MalformedInput_NeverReturnsTheSourceValue(string source) {
        var masked = (string)new EmailMasker().Transform(Context("Email", "nvarchar", 200), source);

        masked.ShouldNotBe(source);
        masked.ShouldContain("@");
    }

    [Fact]
    public void EmailMasker_ReplacesTheDomainWhenConfigured() {
        var masker = new EmailMasker(new EmailMaskerOptions { PreserveCharacters = 2, Domain = "example.invalid" });

        masker.Transform(Context("Email", "nvarchar", 200), "jane.doe@contoso.com").ShouldBe("ja******@example.invalid");
    }

    [Fact]
    public void EmailPseudonymizer_ProducesAnEmailThatIsNotTheSource() {
        var pseudonym = (string)new EmailPseudonymizer().Transform(Context("Email", "nvarchar", 200), "jane.doe@contoso.com");

        pseudonym.ShouldEndWith("@example.invalid");
        pseudonym.ShouldNotContain("jane");
        pseudonym.Split('@')[0].Length.ShouldBe(17);
    }

    [Fact]
    public void EmailPseudonymizer_PreservesTheSourceDomainWhenAsked() {
        var pseudonymizer = new EmailPseudonymizer(new EmailPseudonymizerOptions { PreserveDomain = true });

        ((string)pseudonymizer.Transform(Context("Email", "nvarchar", 200), "jane.doe@contoso.com")).ShouldEndWith("@contoso.com");
    }

    [Fact]
    public void EmailPseudonymizer_NarrowColumn_ShrinksTheTokenToFit() {
        var pseudonym = (string)new EmailPseudonymizer().Transform(Context("Email", "nvarchar", 30), "jane.doe@contoso.com");

        pseudonym.Length.ShouldBeLessThanOrEqualTo(30);
        pseudonym.ShouldEndWith("@example.invalid");
    }

    [Theory]
    [InlineData("(206) 555-1212", "(XXX) XXX-XXXX")]
    [InlineData("206-555-1212", "XXX-XXX-XXXX")]
    [InlineData("2065551212", "XXXXXXXXXX")]
    [InlineData("+1 206 555 1212", "+X XXX XXX XXXX")]
    public void PhoneMasker_KeepsPunctuationAndMasksDigits(string source, string expected) {
        new PhoneMasker().Transform(Context("Phone", "varchar", 40), source).ShouldBe(expected);
    }

    [Fact]
    public void PhoneMasker_KeepsTheRequestedTrailingDigits() {
        var masker = new PhoneMasker(new PhoneMaskerOptions { PreserveLastDigits = 4 });

        masker.Transform(Context("Phone", "varchar", 40), "(206) 555-1212").ShouldBe("(XXX) XXX-1212");
    }

    [Theory]
    [InlineData("ext. unknown")]
    [InlineData("")]
    public void PhoneMasker_NoDigits_StillMasks(string source) {
        ((string)new PhoneMasker().Transform(Context("Phone", "varchar", 40), source)).ShouldNotBe(source);
    }

    [Theory]
    [InlineData("(206) 555-1212")]
    [InlineData("206-555-1212")]
    [InlineData("2065551212")]
    public void PhonePseudonymizer_KeepsTheShapeAndChangesTheDigits(string source) {
        var pseudonym = (string)new PhonePseudonymizer().Transform(Context("Phone", "varchar", 40), source);

        pseudonym.Length.ShouldBe(source.Length);
        pseudonym.ShouldNotBe(source);
        for (var i = 0; i < source.Length; i++) {
            char.IsAsciiDigit(pseudonym[i]).ShouldBe(char.IsAsciiDigit(source[i]));
        }
    }

    [Fact]
    public void PhonePseudonymizer_KeepsTheRequestedLeadingDigits() {
        var pseudonymizer = new PhonePseudonymizer(new PhonePseudonymizerOptions { PreserveLeadingDigits = 3 });

        ((string)pseudonymizer.Transform(Context("Phone", "varchar", 40), "2065551212")).ShouldStartWith("206");
    }

    [Fact]
    public void PhonePseudonymizer_NoDigits_ReturnsADerivedNumber() {
        var pseudonym = (string)new PhonePseudonymizer().Transform(Context("Phone", "varchar", 40), "unlisted");

        pseudonym.ShouldNotBe("unlisted");
        pseudonym.Length.ShouldBe(10);
        pseudonym.ShouldAllBe(character => char.IsAsciiDigit(character));
    }

    [Theory]
    [InlineData("John", "Jotest")]
    [InlineData("McCain", "Mctest")]
    [InlineData("Li", "Litest")]
    [InlineData("X", "Xtest")]
    public void NameMasker_KeepsTheLeadingCharactersAndAppendsTheSuffix(string source, string expected) {
        var masker = new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" });

        masker.Transform(Context("LastName", "nvarchar", 100), source).ShouldBe(expected);
    }

    [Fact]
    public void NameMasker_AValueThatWouldMaskToItself_MasksFurther() {
        var masker = new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" });

        masker.Transform(Context("LastName", "nvarchar", 100), "Jotest").ShouldBe("Jtest");
    }

    [Fact]
    public void NameMasker_SupportsAPrefix() {
        var masker = new NameMasker(new NameMaskerOptions { PreserveCharacters = 1, Prefix = "anon-", Suffix = null });

        masker.Transform(Context("FirstName", "nvarchar", 100), "John").ShouldBe("anon-J");
    }

    [Fact]
    public void NameMasker_WithNeitherPrefixNorSuffix_IsRejected() {
        Should.Throw<ArgumentException>(() => new NameMasker(new NameMaskerOptions { Prefix = null, Suffix = null }));
    }

    [Fact]
    public void StringMasker_MasksEverythingByDefault() {
        new StringMasker().Transform(Context("Code", "varchar", 40), "ACME-1234").ShouldBe("*********");
    }

    [Fact]
    public void StringMasker_KeepsALeadingPrefixAndCanUseAFixedWidth() {
        var masker = new StringMasker(new StringMaskerOptions { PreserveCharacters = 2, MaskLength = 4 });

        masker.Transform(Context("Code", "varchar", 40), "ACME-1234").ShouldBe("AC****");
    }

    [Fact]
    public void StringPseudonymizer_ProducesAFixedWidthToken() {
        var pseudonymizer = new StringPseudonymizer(new StringPseudonymizerOptions { Length = 8, Prefix = "TEST-" });

        var token = (string)pseudonymizer.Transform(Context("Code", "varchar", 40), "ACME-1234");

        token.ShouldStartWith("TEST-");
        token.Length.ShouldBe(13);
    }

    [Theory]
    [InlineData("tinyint", (byte)0, (byte)255)]
    [InlineData("smallint", (short)0, short.MaxValue)]
    [InlineData("int", 0, int.MaxValue)]
    public void NumericPseudonymizer_StaysInsideTheColumnRange(string typeName, object minimum, object maximum) {
        var pseudonymizer = new NumericPseudonymizer();

        // Many sources, because a single draw proves nothing about a range.
        for (var i = 0; i < 200; i++) {
            var result = Convert.ToInt64(pseudonymizer.Transform(Context("Value", typeName, -1), i));

            result.ShouldBeGreaterThanOrEqualTo(Convert.ToInt64(minimum));
            result.ShouldBeLessThanOrEqualTo(Convert.ToInt64(maximum));
        }
    }

    [Fact]
    public void NumericPseudonymizer_RespectsDecimalPrecisionAndScale() {
        var pseudonymizer = new NumericPseudonymizer();
        var context = Context("Balance", "decimal", -1, precision: 7, scale: 2);

        for (var i = 0; i < 200; i++) {
            var result = (decimal)pseudonymizer.Transform(context, i * 3.75m);

            Math.Abs(result).ShouldBeLessThan(100_000m);
            decimal.Round(result, 2).ShouldBe(result);
        }
    }

    [Fact]
    public void NumericPseudonymizer_OnANonNumericColumn_Fails() {
        Should.Throw<SqlDataPackException>(() => new NumericPseudonymizer().Transform(Context("Email", "nvarchar", 100), "someone@contoso.com"))
            .Message.ShouldContain("is not a numeric SQL Server type");
    }

    [Fact]
    public void GuidPseudonymizer_ProducesADifferentWellFormedGuid() {
        var source = Guid.NewGuid();

        var pseudonym = (Guid)new GuidPseudonymizer().Transform(Context("PublicId", "uniqueidentifier", -1), source);

        pseudonym.ShouldNotBe(source);
        pseudonym.ShouldNotBe(Guid.Empty);
        pseudonym.ToString("D").Length.ShouldBe(36);
    }

    [Fact]
    public void GuidPseudonymizer_AGuidHeldInTextAgreesWithTheSameGuidHeldNatively() {
        var source = Guid.NewGuid();
        var pseudonymizer = new GuidPseudonymizer();

        var fromGuid = (Guid)pseudonymizer.Transform(Context("PublicId", "uniqueidentifier", -1), source);
        var fromText = (string)pseudonymizer.Transform(Context("PublicId", "varchar", 36), source.ToString("D").ToUpperInvariant());

        fromText.ShouldBe(fromGuid.ToString("D"));
    }

    [Theory]
    [InlineData("203.0.113.42")]
    [InlineData("10.0.0.1")]
    [InlineData("not an address")]
    public void IPv4Pseudonymizer_ProducesAValidIPv4Address(string source) {
        var pseudonym = (string)new IPv4Pseudonymizer().Transform(Context("ClientIp", "varchar", 45), source);

        pseudonym.ShouldNotBe(source);
        IPAddress.TryParse(pseudonym, out var parsed).ShouldBeTrue();
        parsed!.AddressFamily.ShouldBe(AddressFamily.InterNetwork);
    }

    [Fact]
    public void IPv4Pseudonymizer_KeepsTheRequestedLeadingOctets() {
        var pseudonymizer = new IPv4Pseudonymizer(new IPv4PseudonymizerOptions { PreserveLeadingOctets = 2 });

        ((string)pseudonymizer.Transform(Context("ClientIp", "varchar", 45), "203.0.113.42")).ShouldStartWith("203.0.");
    }

    [Theory]
    [InlineData("2001:db8::8a2e:370:7334")]
    [InlineData("::1")]
    [InlineData("garbage")]
    public void IPv6Pseudonymizer_ProducesAValidIPv6Address(string source) {
        var pseudonym = (string)new IPv6Pseudonymizer().Transform(Context("ClientIp", "varchar", 45), source);

        pseudonym.ShouldNotBe(source);
        IPAddress.TryParse(pseudonym, out var parsed).ShouldBeTrue();
        parsed!.AddressFamily.ShouldBe(AddressFamily.InterNetworkV6);
    }

    [Theory]
    [InlineData("123-45-6789", "XXX-XX-XXXX")]
    [InlineData("123456789", "XXXXXXXXX")]
    [InlineData("123 45 6789", "XXX XX XXXX")]
    public void SsnMasker_KeepsFormattingAndMasksDigits(string source, string expected) {
        new SsnMasker().Transform(Context("Ssn", "char", 11), source).ShouldBe(expected);
    }

    [Theory]
    [InlineData("123-45-6789")]
    [InlineData("123456789")]
    public void SsnPseudonymizer_KeepsFormattingAndReplacesEveryDigit(string source) {
        var pseudonym = (string)new SsnPseudonymizer().Transform(Context("Ssn", "char", 11), source);

        pseudonym.Length.ShouldBe(source.Length);
        pseudonym.ShouldNotBe(source);
        for (var i = 0; i < source.Length; i++) {
            char.IsAsciiDigit(pseudonym[i]).ShouldBe(char.IsAsciiDigit(source[i]));
        }
    }

    [Fact]
    public void SsnPseudonymizer_NoDigits_ReturnsADerivedNumber() {
        var pseudonym = (string)new SsnPseudonymizer().Transform(Context("Ssn", "char", 11), "unknown");

        pseudonym.ShouldNotBe("unknown");
        pseudonym.Length.ShouldBe(9);
    }

    [Fact]
    public void Pseudonymizers_AreConsistentAcrossTablesAndColumnsWithinOneExport() {
        var pseudonymizer = new EmailPseudonymizer();

        var fromCustomers = pseudonymizer.Transform(Context("Email", "nvarchar", 200, table: "Customers"), "jane.doe@contoso.com");
        var fromOrders = pseudonymizer.Transform(Context("ContactEmail", "nvarchar", 200, table: "Orders"), "jane.doe@contoso.com");

        fromOrders.ShouldBe(fromCustomers);
    }

    [Fact]
    public void Pseudonymizers_WithTheSameConfiguration_AgreeAcrossInstances() {
        var context = Context("Email", "nvarchar", 200);

        var first = new EmailPseudonymizer(new EmailPseudonymizerOptions { Domain = "example.invalid" }).Transform(context, "jane.doe@contoso.com");
        var second = new EmailPseudonymizer(new EmailPseudonymizerOptions { Domain = "example.invalid" }).Transform(context, "jane.doe@contoso.com");

        second.ShouldBe(first);
    }

    [Fact]
    public void Pseudonymizers_WithADifferentConfiguration_AreADifferentDeterministicNamespace() {
        var context = Context("Email", "nvarchar", 200);

        var defaultDomain = (string)new EmailPseudonymizer().Transform(context, "jane.doe@contoso.com");
        var otherDomain = (string)new EmailPseudonymizer(new EmailPseudonymizerOptions { Domain = "example.test" }).Transform(context, "jane.doe@contoso.com");

        otherDomain.Split('@')[0].ShouldNotBe(defaultDomain.Split('@')[0]);
    }

    [Fact]
    public void Pseudonymizers_InADifferentExport_ProduceDifferentValues() {
        var pseudonymizer = new EmailPseudonymizer();

        var first = pseudonymizer.Transform(Context("Email", "nvarchar", 200), "jane.doe@contoso.com");
        var second = pseudonymizer.Transform(Context("Email", "nvarchar", 200, secret: ExportSecret.Create()), "jane.doe@contoso.com");

        second.ShouldNotBe(first);
    }

    [Fact]
    public void Pseudonymizers_ForDistinctSources_CollideRarely() {
        // Not a uniqueness guarantee, and the documentation says so. This only asserts the built-ins are not
        // mapping everything onto a handful of values.
        var pseudonymizer = new EmailPseudonymizer();
        var context = Context("Email", "nvarchar", 200);

        var results = Enumerable.Range(0, 5_000).Select(i => (string)pseudonymizer.Transform(context, $"user{i}@contoso.com")).ToHashSet(StringComparer.Ordinal);

        results.Count.ShouldBe(5_000);
    }

    [Fact]
    public void BuiltIns_OutsideAnExport_FailRatherThanDeriveFromNothing() {
        var context = new TransformContext("dbo", "Customers", "Email", "nvarchar", true, 200, 0, 0);

        Should.Throw<SqlDataPackException>(() => new EmailPseudonymizer().Transform(context, "jane.doe@contoso.com"))
            .Message.ShouldContain("can only run inside an export");
    }

    private static TransformContext Context(string column, string typeName, int maxLength, string table = "Customers", byte precision = 0, byte scale = 0, ExportSecret? secret = null) =>
        new("dbo", table, column, typeName, true, maxLength < 0 ? null : maxLength, precision, scale, secret ?? Export);
}
