---
title: Masked slice for dev
sidebar_label: Masked slice for dev
---

Put a usable slice of production on a developer laptop with no real customer data in it.

```csharp
using SqlDataPack;
using SqlDataPack.Models;

var options = ExportOptions.Default;
options.TableSelection = ExportTableSelectionMode.Only;
options.Tables =
[
    "dbo.Customers",
    "dbo.Orders",
    "dbo.Invoices"
];
options.ExcludeColumns =
[
    "dbo.Customers.NationalId"
];
options.GlobalWhereClauses =
[
    new GlobalWhereClause("TenantId", "TenantId = 123")
];

var result = await SqlData.ExportAsync(
    sourceSqlServerConnectionString,
    "dev-slice.sqlite",
    options);
```

## Pick the tables

`TableSelection = ExportTableSelectionMode.Only` turns `Tables` into an inclusion list: only the named tables export, nothing else comes along. That is the opposite of the default, `AllExcept`, where `Tables` is an exclusion list against a full export.

`Tables` supports exact source table names and `*` wildcards. Use schema-qualified names such as `dbo.Customers`, table names such as `Customers`, or wildcard patterns such as `dbo.zz*` and `*.zz*`.

## Drop the columns you do not want

`ExcludeColumns` keeps a column at the source. It never leaves SQL Server, so it cannot show up in the file, and there is nothing later to forget to scrub:

```csharp
options.ExcludeColumns =
[
    "dbo.Customers.NationalId"
];
```

For genuinely sensitive columns (credentials, tokens, government identifiers, and GDPR special-category data such as health, biometric, or ethnicity fields) prefer `ExcludeColumns` over scrubbing later. A column that never leaves the source cannot be leaked by a scrubbing script that missed a case.

Free-text columns deserve the same treatment. You cannot pattern-match your way to confidence about what someone typed into a notes field, so exclude them by default rather than scrubbing them.

## Scrub the columns you have to keep

Some columns you cannot simply drop — a support tool needs an email address to be an email address, a report needs a name in the name column. `Transformations` replaces those values during the export, so the original never reaches the file:

```csharp
using SqlDataPack.Transformations;

options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
options.Transformations.Add("dbo.Customers.Phone", new PhoneMasker());
options.Transformations.Add("dbo.Customers.LastName", new NameMasker(new NameMaskerOptions {
    PreserveCharacters = 2,
    Suffix = "test"
}));
```

Pseudonymizers are consistent within one export, so the same address in `dbo.Customers.Email` and `dbo.Orders.ContactEmail` still matches, and different between exports. Transformation fails the export rather than falling back to the original value or silently truncating a result that does not fit. Full details, the built-in list, and custom transformers are in [Export transformations](/transformations).

Prefer `ExcludeColumns` where you can, and reach for a transformer only where the column has to keep carrying something. A value that never leaves SQL Server is safer than a scrubbed one.

## Filter the rows

`GlobalWhereClauses` applies a SQL Server WHERE predicate to any selected table that has a matching source column. Several single-column clauses apply independently, each wherever its own column exists:

```csharp
options.GlobalWhereClauses =
[
    new GlobalWhereClause("TenantId", "TenantId = 123"),
    new GlobalWhereClause("Active", "Active = 1")
];
```

To require a combination of columns instead, name them all in one clause. It then applies only to tables that have every one of them:

```csharp
options.GlobalWhereClauses =
[
    new GlobalWhereClause(["TenantId", "IsDeleted"], "TenantId = 123 AND IsDeleted = 0")
];
```

That is a different clause from writing `new GlobalWhereClause("TenantId", ...)` and `new GlobalWhereClause("IsDeleted", ...)` separately. Two single-column clauses each apply on their own, independently, to any table carrying that one column. One multi-column clause applies only to a table that carries all of the named columns together; a table with just one of them is not touched by it at all.

:::warning Global predicates fail open
A table that has `TenantId` but no `IsDeleted` column does not match the clause above and is exported **unfiltered**: every row, every tenant. That behaviour is deliberate, so a shared lookup table is not silently truncated by a predicate written for tenant-scoped tables. But it cuts both ways: if your soft-delete column is called `Deleted` on one table and `IsDeleted` everywhere else, that table exports in full, and **no warning is emitted**. A clause only errors when it matches no selected table at all.

When a predicate must never fail open, do not rely on a global clause. Use `PerTableWhereClauses` for exact tables, or narrow `TableSelection` so unmatched tables cannot be exported in the first place.
:::

## Scrub what is left

The package is a plain SQLite file, so the edit step is ordinary SQL. It runs on a laptop, needs nothing installed, and you can re-run it until it is right without going back to the source. Getting an `UPDATE` wrong costs nothing, because you edit the local file again, not the source database.

Resolve the physical SQLite table name from the manifest rather than hardcoding it, since `DataTablePrefix` is configurable:

```csharp
var manifest = await new SqlDataPackReader().ReadManifestAsync("dev-slice.sqlite");
var customers = manifest.Tables.Single(t => t.FullName == "dbo.Customers").SqliteTable;
```

Then run ordinary SQL against the data tables. Any SQLite tool works: `sqlite3`, a GUI, a Python script, an EF Core context, whatever you already use.

```sql
UPDATE dbo__customers
SET Email     = 'customer-' || hex(randomblob(8)) || '@example.invalid',
    Phone     = NULL,
    FirstName = 'First-' || hex(randomblob(4)),
    LastName  = 'Last-'  || hex(randomblob(4));

UPDATE dbo__invoices
SET BillingAddress = '1 Example Street';
```

:::caution Do not delete rows
Rewriting values in place roundtrips freely: change an email, a name, an address, whatever needs scrubbing. Deleting rows works too. Import compares what the package holds against `exported_row_count`, recorded in `zsdp_table_stats` at export time, and reports a difference as a warning rather than failing, so dropping every row for one customer is a supported way to scrub. Set `RowCountDrift.Fail` if a moved count should stop the import instead. See [Editing the package](/editing-the-package) for the full list of what you can and cannot change.
:::

## Compact the file

SQLite does not overwrite freed page content, so an in-place `UPDATE` that replaces a real email with a shorter synthetic one can leave the original bytes readable in free pages. `VACUUM` rewrites the database and drops them. Run it before the package leaves your environment:

```bash
sqlite3 dev-slice.sqlite 'VACUUM;'
```

This is not optional. A package that looks scrubbed can still be carved for the original values with a hex editor until you run it.

## Import it

```csharp
var result = await SqlData.ImportAsync("dev-slice.sqlite", devSqlServerConnectionString);
Console.WriteLine($"Imported {result.RowCount} rows into {result.TableCount} tables.");
```

Check `result.Warnings` for anything the import wants you to know about, including any table whose row count no longer matches what was recorded at export. An empty list means every table copied clean and nothing in the package had changed since export.

## Why this beats restoring a backup and cleaning up

Restoring a full backup to a dev box and then cleaning it up puts the two steps in the wrong order: the target already has every customer's real data before you start deleting or masking it. Anyone with access to that box during the gap between restore and cleanup sees production data, and cleanup itself is a follow-up task someone has to remember to finish, verify, and not skip under deadline pressure.

Filtering at export means the target never receives the data at all. There is no gap, and no cleanup step to forget, because the rows that should not be there were never copied out of SQL Server in the first place.
