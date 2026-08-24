![SqlDataPack](https://raw.githubusercontent.com/zachtbeer/sqldatapack/main/logo.png)

# SqlDataPack

[![CI](https://github.com/zachtbeer/sqldatapack/actions/workflows/ci.yml/badge.svg)](https://github.com/zachtbeer/sqldatapack/actions/workflows/ci.yml)
[![CodeQL](https://github.com/zachtbeer/sqldatapack/actions/workflows/codeql.yml/badge.svg)](https://github.com/zachtbeer/sqldatapack/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/zachtbeer/sqldatapack/badge)](https://securityscorecards.dev/viewer/?uri=github.com/zachtbeer/sqldatapack)
[![NuGet](https://img.shields.io/nuget/vpre/SqlDataPack.svg)](https://www.nuget.org/packages/SqlDataPack)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/zachtbeer/sqldatapack/blob/main/LICENSE)
[![Target frameworks](https://img.shields.io/badge/targets-net8.0%20%7C%20net10.0-512bd4.svg)](https://github.com/zachtbeer/sqldatapack/blob/main/src/SqlDataPack/SqlDataPack.csproj)

SqlDataPack is a .NET library that exports a slice of a SQL Server database into a single SQLite file, and imports it back into SQL Server if and when you want. Not a backup: you pick what comes out, and you can edit it before it goes back in.

Have you ever needed a copy of a SQL Server database on your machine, but not all the tables, and only some of the rows? Or needed a database with just the data that reproduces a bug, so you can hand it to someone? That is what this does.

You choose which tables, columns, and rows come out. What you get is an ordinary SQLite file, so you can edit it in any SQLite tool and `UPDATE`, `DELETE`, or `INSERT` data before you ever import it back into SQL Server. Then import it into a dev database, or send the file to someone else. Nothing is installed on the SQL Server: it is a NuGet package you call from the application that already holds the connection string.

If you have used `.bacpac`, think of it the same way, except the file it produces is plain SQLite.

## Install

```bash
dotnet add package SqlDataPack --prerelease
```

Targets `net8.0` and `net10.0`. The first published version is `1.0.0-preview.1`, so `--prerelease` is required until 1.0.0 is out.

## Export

This exports three tables, keeps only the rows for one customer, and leaves the `NationalId` column out of the file entirely:

```csharp
using SqlDataPack;
using SqlDataPack.Models;

var options = ExportOptions.Default;
options.TableSelection = ExportTableSelectionMode.Only;
options.Tables = ["dbo.Customers", "dbo.Orders", "dbo.Invoices"];
options.ExcludeColumns = ["dbo.Customers.NationalId"];
options.GlobalWhereClauses = [new GlobalWhereClause("CustomerId", "CustomerId = 42")];

var result = await SqlData.ExportAsync(sourceConnectionString, "dev-slice.sqlite", options);
Console.WriteLine($"Exported {result.RowCount:N0} rows from {result.TableCount} tables.");
// Exported 1,847 rows from 3 tables.
```

An excluded column is never read from SQL Server, never written to the file, and never sits on your disk. That is different from exporting everything and deleting the column afterwards, where the data already crossed the wire once and you are trusting a cleanup step to get it right every time. A global `WHERE` clause applies to every selected table that has the named column, and it fails open: a table without that column exports unfiltered, with a warning recorded per table in the package. See [Options](https://zachtbeer.github.io/sqldatapack/options).

## Edit it

The file is plain SQLite. Any tool, no SQL Server:

```text
$ sqlite3 dev-slice.sqlite ".tables"
dbo__customers        zsdp_columns          zsdp_import_plan      zsdp_tables
dbo__invoices         zsdp_exclusions       zsdp_schema_packages  zsdp_warnings
dbo__orders           zsdp_export_runs      zsdp_table_stats

$ sqlite3 dev-slice.sqlite "UPDATE dbo__customers SET Email = 'user' || CustomerId || '@example.invalid';"
```

That replaces every real address with a fake one, in the file, before anything reaches SQL Server.

Deleting rows works the same way. Drop every row for one customer and the import loads what is left, warning you per table that the count moved rather than refusing the package. Set `ImportOptions.RowCountDrift` to `RowCountDrift.Fail` if you would rather a moved count stopped the import, which is what an unattended scrubbing pipeline wants. See [Editing the package](https://zachtbeer.github.io/sqldatapack/editing-the-package).

Or read the manifest (row counts, source types, foreign-key import order) without importing anything:

```csharp
using SqlDataPack.Models;

var manifest = await new SqlDataPackReader().ReadManifestAsync("dev-slice.sqlite");
foreach (var table in manifest.Tables){
    Console.WriteLine($"{table.FullName}: {table.ExportedRowCount:N0}");
}
// dbo.Customers: 1
// dbo.Invoices: 1,043
// dbo.Orders: 803
```

## Import

```csharp
await SqlData.ImportAsync("dev-slice.sqlite", targetConnectionString);
```

Import keeps identity values (`SqlBulkCopyOptions.KeepIdentity`), loads tables in foreign-key order, and checks imported counts against the counts recorded at export. The target tables must already exist and be empty, unless you captured the schema as a dacpac at export and deploy it at import. See [Importing](https://zachtbeer.github.io/sqldatapack/importing).

## Compared to backup, `.bacpac`, and `bcp`

Backups and `.bacpac` files are whole-row, whole-table by nature: neither drops a column, applies a `WHERE` clause, or gives you a stage between extract and load. `bcp` does all three from a query, at one command and one loose file per table, with the transform living back on production.

[Compared to the alternatives](https://zachtbeer.github.io/sqldatapack/comparison) is the full table, including the rows SqlDataPack loses.

## Documentation

Full documentation: **https://zachtbeer.github.io/sqldatapack/**

- [Getting started](https://zachtbeer.github.io/sqldatapack/getting-started): install, export, inspect, import
- [Recipes](https://zachtbeer.github.io/sqldatapack/masked-slice-for-dev): masked dev slices, bug repros, schema capture, agent handoff
- [Options](https://zachtbeer.github.io/sqldatapack/options): table, column, and row filtering, tuning, dacpac
- [Troubleshooting](https://zachtbeer.github.io/sqldatapack/troubleshooting): common failures and fixes
- [Known limitations](https://zachtbeer.github.io/sqldatapack/known-limitations): the hard edges
- [Versioning](https://zachtbeer.github.io/sqldatapack/versioning): compatibility policy
- [Minimal sample](https://github.com/zachtbeer/sqldatapack/tree/main/samples/SqlDataPack.Sample) and [workflow sample](https://github.com/zachtbeer/sqldatapack/tree/main/samples/SqlDataPack.WorkflowSample)

## Supply chain and security

No telemetry, no analytics, no update or license checks, and no outbound connection except to the SQL Server in your connection string, with a CI test that watches sockets during a real export and import to keep it that way. Releases carry signed build provenance and a CycloneDX SBOM, builds are deterministic with SourceLink and published symbols, and CodeQL, Scorecard, Dependabot, commit-pinned actions, and locked-mode NuGet restore are on. Details in the [FAQ](https://zachtbeer.github.io/sqldatapack/faq).

Report a vulnerability through [private vulnerability reporting](https://github.com/zachtbeer/sqldatapack/security/advisories/new), not a public issue. See [SECURITY.md](https://github.com/zachtbeer/sqldatapack/blob/main/SECURITY.md).

## Contributing

Issues and pull requests welcome. See [CONTRIBUTING.md](https://github.com/zachtbeer/sqldatapack/blob/main/CONTRIBUTING.md) and the [Code of Conduct](https://github.com/zachtbeer/sqldatapack/blob/main/CODE_OF_CONDUCT.md).

```bash
dotnet test SqlDataPack.slnx
```

Integration tests require Docker for SQL Server Testcontainers.

MIT licensed.
