---
title: Package format
sidebar_label: Package format
---

The export produces one SQLite file. Open it with any SQLite tool, no SQL Server required:

```text
$ sqlite3 billing-repro.sqlite ".tables"
dbo__customers        zsdp_columns          zsdp_import_plan      zsdp_tables
dbo__invoices         zsdp_exclusions       zsdp_schema_packages  zsdp_warnings
dbo__orders           zsdp_export_runs      zsdp_table_stats
```

Two kinds of table live side by side in that list, and the prefix tells them apart: `zsdp_*` tables carry metadata about the export, and every bare-named table carries the rows themselves. `zsdp_` is reserved for the library's own use, so anything without it is your data.

## The metadata tables

| Table | Holds |
| --- | --- |
| `zsdp_tables` | One row per exported SQL Server table: its schema, table name, and the SQLite table it was written to. |
| `zsdp_columns` | One row per exported (and skipped) column: SQL Server type name, max length, precision, scale, nullability, identity, computed, exclusion, collation, and vector base type and dimensions where relevant. |
| `zsdp_table_stats` | Row counts and sizing per table: `exported_row_count` (what import compares the package's current contents against), estimated source row count, estimated source bytes, and the export batch size. |
| `zsdp_exclusions` | Tables and columns that were skipped from export, and why. |
| `zsdp_warnings` | Non-fatal warnings produced during export. |
| `zsdp_import_plan` | The foreign-key-based order tables need to import in. |
| `zsdp_export_runs` | One row describing the export itself: package format version, application version, export timestamp, and the source schema hash. |
| `zsdp_schema_packages` | The embedded dacpac, when schema capture was enabled: package bytes, DacFx version, schema scope, and source engine edition. |

These tables are internal to SqlDataPack. You can query them, but the schema is owned by the library, not a general-purpose contract, so it can change between versions. For anything an application needs to read, use `SqlDataPackReader` instead (see below).

## The data tables

Each exported SQL Server table gets one SQLite table, named `<schema>__<table>`, so `dbo.Customers` becomes `dbo__customers`. Set `DataTablePrefix` to group data tables behind a prefix instead — `DataTablePrefix = "app"` writes `app_dbo__customers`. It defaults to no prefix; metadata tables stay `zsdp_*` either way.

`zsdp_` and `sqlite_` are reserved: a source table whose generated name would land in either namespace fails the export with an error naming the table, rather than colliding with the package's own metadata or SQLite's.

Because the prefix is configurable, do not hardcode data table names in application code. Resolve them from the manifest instead:

```csharp
var manifest = await new SqlDataPackReader().ReadManifestAsync("billing-repro.sqlite");
var customers = manifest.Tables.Single(t => t.FullName == "dbo.Customers").SqliteTable;
```

## Reading the manifest

`SqlDataPackReader.ReadManifestAsync` reads the metadata tables for you and returns a typed manifest, so you do not need to know the `zsdp_*` schema to work with a package:

```csharp
var manifest = await new SqlDataPackReader().ReadManifestAsync("billing-repro.sqlite");
foreach (var table in manifest.Tables)
    Console.WriteLine($"{table.FullName}: {table.ExportedRowCount} rows");
// dbo.Customers: 1,204 rows
// dbo.Invoices: 18,755 rows
// dbo.Orders: 22,109 rows
```

The manifest carries each table's row count, its SQLite table name, its column metadata, the import order, exclusions, and any export warnings. Each column reports its SQL Server type name, and a `vector` column also reports `VectorBaseType` and `VectorDimensions`, both of which are null for every other type. This is the supported way to inspect a package; the `zsdp_*` tables underneath it are not.

## What is preserved

Export keeps more than the row values. Alongside each row, the package stores:

- source schema and table names
- SQL Server type metadata (the original type name, precision, scale)
- nullability
- identity
- computed columns
- collation

That is what lets import put a row back in a form SQL Server accepts, rather than just a value SQLite happened to store.

## Type conversion

Export stores values using SQLite affinities chosen for reliable transport rather than a one-to-one type mapping: integer-like values use `INTEGER`, floating-point values use `REAL`, binary values use `BLOB`, and date/time, decimal, money, GUID, and text values use `TEXT` where that better preserves SQL Server behavior. `vector` values are stored as `TEXT` JSON arrays, with the base type and dimension count recorded in `zsdp_columns` so import can reconstruct the native value.

The original SQL Server type name lives in `zsdp_columns` regardless of which SQLite affinity was used, and import reads it to coerce each value back into something `SqlBulkCopy` accepts. See [Supported types](/supported-types) for the full type list.

## Files on disk

An export touches three things on disk:

- **The SQLite package**, at the path you gave it.
- **A temporary file alongside it during export**, named `.<name>.<guid>.tmp` in the destination directory. It is renamed into place on success and deleted on a best-effort basis on failure. If an export is interrupted, this file can survive, holding the unscrubbed extract under a name your cleanup script will not match. Treat the destination directory, not just the final file, as production-data territory.
- **A temporary `.dacpac` in the system temp directory**, only if you opted into dacpac capture or deployment. Schema only, no rows.

## Why SQLite

SQLite gives the package a single-file format with broad tooling support, transactional writes, simple metadata storage, and enough type flexibility for transport. It also lets humans and AI coding agents inspect exported data without requiring SQL Server access. SqlDataPack uses SQLite as a transport container, not as a replacement for SQL Server semantics.
