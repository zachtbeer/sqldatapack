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

## What breaks the import

**Deleting rows breaks the import.** At export, SqlDataPack records how many rows it wrote for each table as `exported_row_count` in `zsdp_table_stats`. At import, it compares the rows it actually copies from the package against that stored count, and throws on a mismatch:

```text
Imported row count for 'dbo.Customers' was 4102, expected 4213.
```

That check is a stored-count comparison, not a live `COUNT(*)` against the package: import counts rows as it copies them and checks the running total against the number recorded at export time. It exists so a scrubbing script with a bad `WHERE` clause, or a `DELETE` that went further than intended, cannot quietly ship a partial dataset. If you genuinely need fewer rows, filter them out at export instead of deleting them from the package.

A row-count match is not evidence that a scrub actually worked. The check catches `DELETE`. It does not catch an `UPDATE` that matched zero rows, a scrub applied to one table while a related table still holds every real value, or a column you forgot existed. Verify the scrub itself, separately, before the package travels.

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
| Data tables (every bare-named table, e.g. `dbo__customers`) | Yes. This is the whole point: rewrite values freely. |
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
