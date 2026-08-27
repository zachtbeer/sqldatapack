<p align="center">
  <img src="https://raw.githubusercontent.com/zachtbeer/sqldatapack/main/logo.png" alt="SqlDataPack" width="140">
</p>

<h1 align="center">SqlDataPack</h1>

<p align="center">
<a href="https://github.com/zachtbeer/sqldatapack/actions/workflows/ci.yml"><img src="https://github.com/zachtbeer/sqldatapack/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
<a href="https://github.com/zachtbeer/sqldatapack/actions/workflows/codeql.yml"><img src="https://github.com/zachtbeer/sqldatapack/actions/workflows/codeql.yml/badge.svg" alt="CodeQL"></a>
<a href="https://securityscorecards.dev/viewer/?uri=github.com/zachtbeer/sqldatapack"><img src="https://api.securityscorecards.dev/projects/github.com/zachtbeer/sqldatapack/badge" alt="OpenSSF Scorecard"></a>
<a href="https://www.nuget.org/packages/SqlDataPack"><img src="https://img.shields.io/nuget/v/SqlDataPack.svg" alt="NuGet"></a>
<a href="https://zachtbeer.github.io/sqldatapack/"><img src="https://img.shields.io/badge/docs-zachtbeer.github.io-blue.svg" alt="Docs"></a>
<a href="https://github.com/zachtbeer/sqldatapack/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License: MIT"></a>
<a href="https://github.com/zachtbeer/sqldatapack/blob/main/src/SqlDataPack/SqlDataPack.csproj"><img src="https://img.shields.io/badge/targets-net8.0%20%7C%20net10.0-512bd4.svg" alt="Target frameworks"></a>
</p>

SqlDataPack is a .NET library that exports a slice of a SQL Server database into a single SQLite file, and imports it back into SQL Server if and when you want. Not a backup: you pick what comes out, and you can edit it before it goes back in.

Have you ever needed a copy of a SQL Server database on your machine, but not all the tables, and only some of the rows? Or needed a database with just the data that reproduces a bug, so you can hand it to someone? That is what this does.

You choose which tables, columns, and rows come out. What you get is an ordinary SQLite file, so you can edit it in any SQLite tool and `UPDATE`, `DELETE`, or `INSERT` data before you ever import it back into SQL Server. Then import it into a dev database, or send the file to someone else. Nothing is installed on the SQL Server: it is a NuGet package you call from the application that already holds the connection string.

If you have used `.bacpac`, think of it the same way, except the file it produces is plain SQLite.

Full documentation, including recipes and known limitations: **https://zachtbeer.github.io/sqldatapack/**

## Install

There are two ways to use this: a command you run, or a package you call.

**The command.** Nothing to install first, no .NET needed on the machine:

```powershell
winget install zachtbeer.SqlDataPack
```

```bash
dotnet tool install -g SqlDataPack.Cli    # if you already have the SDK
```

Or download a single executable for Windows, Linux or macOS from [the releases page](https://github.com/zachtbeer/sqldatapack/releases). See [Command line](https://zachtbeer.github.io/sqldatapack/cli).

**The library:**

```bash
dotnet add package SqlDataPack
```

Targets `net8.0` and `net10.0`.

## Export, from the command line

```bash
sqldatapack export \
  --connection "Server=.;Database=Northwind;Integrated Security=true" \
  --out dev-slice.sqlite \
  --tables dbo.Customers,dbo.Orders,dbo.Invoices \
  --exclude-column dbo.Customers.NationalId \
  --global-where "CustomerId:CustomerId = 42"

sqldatapack import dev-slice.sqlite \
  --connection "Server=.;Database=NorthwindDev;Integrated Security=true"
```

That is the same slice the C# below produces. Options the flags do not cover go in a JSON file passed with `--options`, which is meant to be committed and therefore refuses to hold a connection string.

## Export, from code

The same three tables, only the rows for one customer, and the `NationalId` column left out of the file entirely:

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

For a column that has to keep carrying something — an email address a support tool still needs to look like an email address — bind a transformer instead of dropping it. The value it returns is what lands in the package; the original never does:

```csharp
using SqlDataPack.Transformations;

options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
options.Transformations.Add("dbo.Customers.Phone", new PhoneMasker());
options.Transformations.Add("dbo.Customers.LastName", new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" }));
options.Transformations.Add("dbo.Customers.InternalCode", new CustomTransformer((context, value) => $"TEST-{value}"));
```

Built-in pseudonymizers are consistent within one export, so the same address still matches across tables, and differ between exports. Transformation fails the export rather than falling back to the original value or truncating a result that does not fit. It is masking and pseudonymization, not a guarantee of irreversible anonymization: prefer `ExcludeColumns` where a column can simply go. See [Export transformations](https://zachtbeer.github.io/sqldatapack/transformations).

## Edit it

The file is plain SQLite. Any tool, no SQL Server:

```text
$ sqlite3 dev-slice.sqlite ".tables"
dbo__customers        zsdp_columns          zsdp_import_plan      zsdp_table_stats
dbo__invoices         zsdp_exclusions       zsdp_schema_packages  zsdp_tables
dbo__orders           zsdp_export_runs      zsdp_transformations  zsdp_warnings

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
- [Command line](https://zachtbeer.github.io/sqldatapack/cli): every flag, the options file, exit codes
- [Recipes](https://zachtbeer.github.io/sqldatapack/masked-slice-for-dev): masked dev slices, bug repros, schema capture, agent handoff
- [Options](https://zachtbeer.github.io/sqldatapack/options): table, column, and row filtering, tuning, dacpac
- [Troubleshooting](https://zachtbeer.github.io/sqldatapack/troubleshooting): common failures and fixes
- [Known limitations](https://zachtbeer.github.io/sqldatapack/known-limitations): the hard edges
- [Versioning](https://zachtbeer.github.io/sqldatapack/versioning): compatibility policy
- [Minimal sample](https://github.com/zachtbeer/sqldatapack/tree/main/samples/SqlDataPack.Sample) and [workflow sample](https://github.com/zachtbeer/sqldatapack/tree/main/samples/SqlDataPack.WorkflowSample)

## Supply chain and security

This has been taken seriously since the first commit: no telemetry, no outbound connection except to the SQL Server in your connection string, and every release carries signed build provenance and an SBOM. The [FAQ](https://zachtbeer.github.io/sqldatapack/faq) has the full list, and [SECURITY.md](https://github.com/zachtbeer/sqldatapack/blob/main/SECURITY.md) covers reporting a vulnerability privately.

## Contributing

Issues and pull requests welcome. See [CONTRIBUTING.md](https://github.com/zachtbeer/sqldatapack/blob/main/CONTRIBUTING.md) and the [Code of Conduct](https://github.com/zachtbeer/sqldatapack/blob/main/CODE_OF_CONDUCT.md).

```bash
dotnet test SqlDataPack.slnx
```

Integration tests require Docker for SQL Server Testcontainers.

MIT licensed.
