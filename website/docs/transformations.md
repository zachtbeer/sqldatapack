---
title: Export transformations
sidebar_label: Export transformations
---

Transformations scrub sensitive values **during** the export, on the way from SQL Server into the package:

```
SQL Server value -> transformer -> SQLite package
```

A column with a transformer bound to it never has its original value written to the file. Bind one per column, by fully qualified `schema.table.column` path:

```csharp
using SqlDataPack;
using SqlDataPack.Models;
using SqlDataPack.Transformations;

var options = ExportOptions.Default;
options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
options.Transformations.Add("dbo.Customers.Phone", new PhoneMasker());
options.Transformations.Add("dbo.Customers.LastName", new NameMasker(new NameMaskerOptions {
    PreserveCharacters = 2,
    Suffix = "test"
}));

await SqlData.ExportAsync(sourceConnectionString, "dev-slice.sqlite", options);
```

That package holds `j*******@contoso.com`-shaped addresses, `(XXX) XXX-XXXX` phone numbers, and `Smtest` in place of `Smith`.

:::note What this is and is not
This is masking and pseudonymization for development and test data — sensitive-data scrubbing, not a guarantee of irreversible anonymization, and not a compliance control. Transformed values still carry structure (formatting, lengths, which rows share a value), and structure can be revealing. For a column you cannot afford to reason about at all — credentials, tokens, government identifiers, free-text notes — prefer [`ExcludeColumns`](/options#excluding-columns): a value that never leaves SQL Server cannot leak from the package.
:::

## The built-ins

| Category | Transformers |
| --- | --- |
| Email | `EmailMasker`, `EmailPseudonymizer` |
| Phone | `PhoneMasker`, `PhonePseudonymizer` |
| Names | `NameMasker` (first name, last name, or full name) |
| Free-form text | `StringMasker`, `StringPseudonymizer` |
| Numbers | `NumericPseudonymizer` |
| GUIDs | `GuidPseudonymizer` |
| IP addresses | `IPv4Pseudonymizer`, `IPv6Pseudonymizer` |
| US SSN | `SsnMasker`, `SsnPseudonymizer` |

Each takes an options object with the same name plus `Options` — `new PhoneMasker(new PhoneMaskerOptions { PreserveLastDigits = 4 })` leaves a recognisable last four. Options are copied at construction, so editing the object afterwards changes nothing.

**Maskers** throw information away: many source values map to one output, on purpose. **Pseudonymizers** replace a value with a derived stand-in that keeps rows lining up.

Built-ins preserve structure where that is cheap — punctuation in a phone number or SSN, the domain of an email address if you ask for it — and they are best-effort about it. They are *not* best-effort about protecting the value: a phone number the transformer cannot make sense of is still transformed, never passed through. There is no telephone or address grammar in the library, and there will not be one.

There are deliberately no built-ins for dates, times, credit cards, or postal addresses. Use a string transformer or a custom transformer for those.

## Deterministic within one export

Within a single export:

> same source value + same transformer type + same configuration = same transformed value

That holds across tables and columns, so a value that appears in several places stays joinable:

```csharp
options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
options.Transformations.Add("dbo.Orders.ContactEmail", new EmailPseudonymizer());
// jane@contoso.com pseudonymizes to the same address in both tables.
```

Two different configurations are two different deterministic namespaces on purpose: `new EmailPseudonymizer()` and `new EmailPseudonymizer(new EmailPseudonymizerOptions { Domain = "example.test" })` do not agree, and are not meant to.

Determinism is scoped to **one export**. Each export generates a random secret, keeps it in memory, uses it to derive pseudonyms (via HMAC, not a bare hash), and throws it away. The secret is never written to the package and never handed to a custom transformer. Exporting the same database twice therefore produces different pseudonyms, and there is no way to reproduce yesterday's package's values.

## Uniqueness is not guaranteed

Built-in pseudonymizers are deterministic within an export and designed to minimize collisions, but SqlDataPack does not guarantee that unique constraints survive transformation. Built-in maskers may intentionally map many source values onto one output. Use a custom transformer when uniqueness has to hold.

Transformations are allowed on primary keys, foreign keys, identity columns, and unique columns. Nothing stops you, and nothing fixes up what breaks: where a deterministic pseudonymizer happens to keep a key and its references consistent, good; where a masker collapses a unique column, that is yours to deal with.

## Fail closed

Transformations never fall back quietly. Any of these fails the whole export:

- the transformer throws;
- it returns `null` for a non-nullable column;
- its result does not fit the destination column — wrong type, longer than the column's length, more digits or decimal places than its precision and scale allow.

An oversized result is never truncated and a failed transformation never writes the original value. The transformed value does not have to resemble the original's length: a 5-character value in `nvarchar(50)` may become a 20-character one.

A source `NULL` is the one thing that bypasses the transformer entirely: NULL stays NULL, and the transformer is not called. A transformer may return `null` for a nullable column.

Configuration mistakes are caught before any row is read, by `PreflightAsync` as well as by the export: an unknown column, a table outside the export scope, two paths pointing at one column, or a transformation on a column you also excluded.

## Custom transformers

Custom transformers are the escape hatch, and the answer to anything the built-ins do not cover. Pass a delegate:

```csharp
options.Transformations.Add("dbo.Customers.InternalCode", new CustomTransformer((context, value) => {
    return $"TEST-{value}";
}));
```

or implement `IValueTransformer` when you want a reusable type:

```csharp
public sealed class TruncatingPostcodeMasker : IValueTransformer {
    public object? Transform(TransformContext context, object value) {
        var postcode = (string)value;
        return postcode.Length <= 3 ? "000" : postcode[..3] + "***";
    }
}

options.Transformations.Add("dbo.Customers.Postcode", new TruncatingPostcodeMasker());
```

Transformation is synchronous and value-oriented. A transformer receives one value plus `TransformContext` — schema, table, column, SQL Server type name, nullability, max length, precision, and scale — and nothing else: no other column of the row, no row, no connection, no export secret. It is called once per non-NULL cell of its column.

A custom transformer may keep its own state, but SqlDataPack neither manages nor guarantees it. If you need cross-export stable identities, a lookup table, or uniqueness, hold that state yourself.

## What the package records

The package records which columns were transformed, and how the built-in was configured — never the export secret, a key, an original value, or an intermediate hash:

```csharp
var manifest = await new SqlDataPackReader().ReadManifestAsync("dev-slice.sqlite");

foreach (var transformation in manifest.Transformations) {
    Console.WriteLine($"{transformation.ColumnPath} {transformation.TransformerType} {transformation.Configuration}");
}

// dbo.Customers.Email    EmailPseudonymizer  Domain=example.invalid;PreserveDomain=False
// dbo.Customers.LastName NameMasker          PreserveCharacters=2;Suffix=test
```

Anything that is not a built-in is recorded as `Custom` with no configuration. SqlDataPack does not ask custom transformers for a name, version, or description.

## Limits in this release

Deferred on purpose, and reachable today with a custom transformer where it matters:

- no automatic PII detection, column-name patterns, or global matching rules — bindings are explicit, one column at a time;
- no chaining: one column, one transformer;
- no async transformers, no row-level or multi-column transformations, no external service calls;
- no cross-export deterministic identity and no configurable consistency groups;
- no uniqueness guarantee, no policy enforcement, no compliance reporting.

The CLI cannot configure transformations: transformers are objects you construct, and an options file naming `transformations` is refused rather than silently ignored. Use the library API for a transformed export.
