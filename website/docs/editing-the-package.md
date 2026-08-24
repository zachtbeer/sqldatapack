---
title: Editing the package
sidebar_label: Editing the package
---

The package is a plain SQLite file, so the edit step is ordinary SQL:

```sql
UPDATE dbo__customers
SET Email = 'customer-' || hex(randomblob(8)) || '@example.invalid'
WHERE CustomerId = 42;
```

Any SQLite tool works: `sqlite3`, a GUI, a Python script, an EF Core context, whatever you already use. Resolve the table name from the manifest rather than hardcoding it, since `DataTablePrefix` is configurable; see [Package format](/package-format).

## What you may change

**Change values freely.** Rewriting values in place is fully supported and roundtrips through import: fix a typo, replace a real email with a synthetic one, correct a bad row, whatever the task calls for.

## Adding and removing rows

Supported. `DELETE` and `INSERT` against a data table both roundtrip through import.

At export, SqlDataPack records how many rows it wrote for each table as `exported_row_count` in `zsdp_table_stats`. Before it writes anything to the target, import compares that number against a live `COUNT(*)` on the package's own data table. A difference is reported rather than refused: the rows the package actually holds are imported, and every table whose count moved produces a warning on `SqlDataPackResult.Warnings`.

```text
Table 'dbo.Customers' holds 4102 rows but the export recorded 4213. Importing the 4102 rows the package holds.
```

If you would rather a moved count stopped the import, set `RowCountDrift.Fail`:

```csharp
var options = new ImportOptions { RowCountDrift = RowCountDrift.Fail };
```

That rejects the package before a single row is written, naming every table whose count moved, so the target is left untouched and you can fix the package and run again. It is the setting for an unattended pipeline that scrubs packages on a schedule, where a count that moved means a bug in the scrub script rather than someone's deliberate edit.

This comparison only ever answers whether the file changed since export. It is not the check that proves an import loaded everything: that one compares the rows read out of the package against the rows that landed in the target, it runs on every import, and it cannot be switched off.

A row-count match is also not evidence that a scrub actually worked. It does not catch an `UPDATE` that matched zero rows, a scrub applied to one table while a related table still holds every real value, or a column you forgot existed. Verify the scrub itself, separately, before the package travels.

## What breaks the import

**Values must stay valid for their target SQL Server type.** Import converts each SQLite value back into what `SqlBulkCopy` expects for the column's original type, and an edited value has to survive that conversion:

- `xml` columns must stay well-formed XML
- native `json` columns must stay valid JSON
- `decimal`, `numeric`, `money`, and `smallmoney` are text-preserved and must stay parseable
- `vector` columns are stored as JSON arrays and must keep their dimension count
- `uniqueidentifier` must stay a well-formed GUID

If an edit breaks one of these, import fails with SQL Server's own conversion error.

**Do not add, rename, or drop columns or tables.** Import expects every exported column to exist in the target and reads the package structure from its manifest. Reshaping the schema is a job for the source query or a dacpac, not for the package.

## Which parts you may edit

| Package area | Safe to edit |
| --- | --- |
| Data tables (every bare-named table, e.g. `dbo__customers`) | Yes. This is the whole point: rewrite values, delete rows, insert rows. |
| Metadata tables (`zsdp_*`: manifests, stats, warnings, the import plan) | No. Internal to SqlDataPack. Read them through `SqlDataPackReader`, do not write to them. |

## The package is unsealed on purpose

There is no signature and no tamper check on import. `SourceSchemaHash` is written into the manifest at export time and is readable through `SqlDataPackReader`, but it is never validated on import: nothing compares it against the target, and nothing rejects a package because the hash looks wrong.

That is a design decision, not an oversight. A checked hash would make the edit step this page describes impossible, since a hash computed at export would no longer match a package you deliberately changed afterward. The tradeoff is that a package carries no evidence of who produced it or what was done to it. Treat a package you did not produce yourself as untrusted input, and import it only into an environment you are willing to have it modify. The reader is fuzz-hardened against corrupt and hostile packages, so a malformed one fails as a `SqlDataPackException` rather than crashing, but a well-formed package containing wrong data imports cleanly, by design.

## Compacting

SQLite does not overwrite freed page content, and `PRAGMA secure_delete` is off in the builds `Microsoft.Data.Sqlite` ships. An in-place `UPDATE` that replaces a real value with a shorter one leaves the original bytes readable in free pages. A package that looks edited can still be carved for the original values with a hex editor.

`VACUUM` rewrites the database and drops them. Run it before the package leaves your environment, then confirm:

```bash
sqlite3 customer-snapshot.sqlite 'VACUUM;'
grep -a 'a.real.address@customer.com' customer-snapshot.sqlite && echo "STILL PRESENT" || echo "clean"
```

This is not optional if the edit removed anything sensitive. If your editing tool left a `-wal` or `-journal` sidecar next to the package, delete it too, since it can hold pre-update page images. SqlDataPack itself writes with `journal_mode = MEMORY` and leaves no sidecar, but your tooling may differ.
