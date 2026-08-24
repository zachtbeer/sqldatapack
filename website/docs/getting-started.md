---
title: Getting started
sidebar_label: Getting started
---

```bash
dotnet add package SqlDataPack --prerelease
```

This walks through the whole path once, start to finish: export a database, look at the file, read what is in it, then import it into an empty target. Nothing here is filtered, every table and every row comes along. Filtering starts in the recipes linked at the bottom of this page.

## Requirements

| Area | Support |
| --- | --- |
| .NET targets | `net8.0`, `net10.0` |
| SQL Server | Versions that support the system catalog and `sys.dm_db_partition_stats` queries used by export planning |
| Operating systems | Local development on macOS, Linux, or Windows; CI runs on `ubuntu-latest` |

See the [support matrix](/support-matrix) for the full list, including test coverage and SQLite file details.

## Export a database

`SqlData.ExportAsync` copies data from SQL Server into a SQLite file and hands back a `SqlDataPackResult` with the row and table counts:

```csharp
using SqlDataPack;

var result = await SqlData.ExportAsync(sourceSqlServerConnectionString, "billing-repro.sqlite");
Console.WriteLine($"Exported {result.RowCount} rows from {result.TableCount} tables.");
// Exported 48,213 rows from 7 tables.
```

## Look inside the file

The result is one ordinary SQLite file. You can open it with any SQLite tool, no SQL Server involved in this step at all:

```text
$ sqlite3 billing-repro.sqlite ".tables"
dbo__customers        zsdp_columns          zsdp_import_plan      zsdp_tables
dbo__invoices         zsdp_exclusions       zsdp_schema_packages  zsdp_warnings
dbo__orders           zsdp_export_runs      zsdp_table_stats

$ sqlite3 billing-repro.sqlite "SELECT CustomerId, Name FROM dbo__customers LIMIT 3;"
1|Northwind Traders
2|Contoso Ltd
3|Fabrikam, Inc.
```

## Read the manifest

`SqlDataPackReader().ReadManifestAsync` reads row counts, source types, and foreign-key import order straight out of the file, without importing anything:

```csharp
var manifest = await new SqlDataPackReader().ReadManifestAsync("billing-repro.sqlite");
foreach (var table in manifest.Tables)
    Console.WriteLine($"{table.FullName}: {table.ExportedRowCount} rows");
// dbo.Customers: 1,204 rows
// dbo.Invoices: 18,755 rows
// dbo.Orders: 22,109 rows
```

## Import it

```csharp
await SqlData.ImportAsync("billing-repro.sqlite", targetSqlServerConnectionString);
```

Two rules catch first-time imports:

- The target tables must already exist.
- Those target tables must be empty.

Import will not create tables or write into ones that already hold rows. See [Importing](/importing) for the preflight checks that catch this before you run it. If you want the SQLite file to carry the schema too, so the target tables get created for you, see [Slice with schema](/slice-with-schema).

## What just happened

- Every selected table was copied, since this run had no filtering.
- Identity values were kept: SqlDataPack imports with `SqlBulkCopyOptions.KeepIdentity`, so parent/child relationships stay intact when the target schema is compatible.
- Imported row counts were checked against exported row counts.
- Nothing left the machine except the connection to the SQL Server you named. No other network calls, no telemetry.

## Next

- [Masked slice for dev](/masked-slice-for-dev): scrub sensitive columns before the file reaches a developer.
- [Options](/options): every option `ExportAsync` and `ImportAsync` accept.
- [Minimal sample](https://github.com/zachtbeer/sqldatapack/tree/main/samples/SqlDataPack.Sample): export a data package and import it into a prepared target SQL Server schema.
- [Workflow sample](https://github.com/zachtbeer/sqldatapack/tree/main/samples/SqlDataPack.WorkflowSample): run preflight checks, report progress, inspect the manifest, import rows, and print warnings/errors.
