---
title: Command line
sidebar_label: Command line
---

`sqldatapack` does what the library does, without a C# project. Export a slice, edit the file, import it back.

This is the right tool when the job is one-shot: cutting a dev slice, handing someone a bug repro, running an export from a scheduled task. If you want the behaviour inside an application, use [the library](/getting-started) instead.

## Install

The Windows builds and the direct downloads carry their own .NET runtime, so nothing needs to be installed first.

```powershell
winget install zachtbeer.SqlDataPack
```

On a shared server, install for every user rather than just yours:

```powershell
winget install zachtbeer.SqlDataPack --scope machine
```

If you already have the .NET SDK:

```bash
dotnet tool install -g SqlDataPack.Cli
```

Or download a single executable from [the releases page](https://github.com/zachtbeer/sqldatapack/releases) for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64`. Each release also carries a `SHA256SUMS` file and a build provenance attestation.

```bash
curl -sSL https://github.com/zachtbeer/sqldatapack/releases/latest/download/sqldatapack-linux-x64.tar.gz | tar xz
./sqldatapack --version
```

## Export

```bash
sqldatapack export \
  --connection "Server=.;Database=Northwind;Integrated Security=true" \
  --out dev-slice.sqlite \
  --tables dbo.Customers,dbo.Orders,dbo.Invoices \
  --exclude-column dbo.Customers.NationalId \
  --global-where "CustomerId:CustomerId = 42"
```

```text
Exported 1,847 rows from 3 tables into dev-slice.sqlite
```

| Flag | What it does |
| --- | --- |
| `--connection`, `-c` | SQL Server connection string. Falls back to `SQLDATAPACK_CONNECTION`. |
| `--out`, `-o` | Path of the SQLite package to write. Required. |
| `--tables` | Export only these tables. Repeat the flag or separate with commas. |
| `--exclude-tables` | Export everything except these. Cannot be combined with `--tables`. |
| `--exclude-column` | Leave a column out entirely, as `schema.Table.Column`. Repeatable. |
| `--global-where` | `"Column:predicate"`, applied to every table that has the column. Repeatable. |
| `--table-where` | `"schema.Table:predicate"`, applied to one table. Repeatable. |
| `--schema` | `none` (default) or `dacpac`, to capture the source schema alongside the data. |
| `--overwrite` | Replace the output file if it exists. |
| `--batch-size` | Rows per batch. Defaults to 1,000. |
| `--timeout` | SQL command timeout in seconds. |
| `--options` | JSON file for everything else. See below. |
| `--quiet`, `-q` | Suppress per-table progress. Warnings still print. |

An excluded column is never read from SQL Server, so the data does not cross the wire at all. A global `WHERE` fails open: a table without that column exports unfiltered and records a warning. See [Options](/options).

The predicate is split from the key on the **first** colon, so a predicate can contain one:

```bash
sqldatapack export -c "..." -o slice.sqlite --global-where "CreatedAt:CreatedAt > '2024-01-01 08:30:00'"
```

## Import

```bash
sqldatapack import dev-slice.sqlite \
  --connection "Server=.;Database=NorthwindDev;Integrated Security=true"
```

| Flag | What it does |
| --- | --- |
| `--connection`, `-c` | SQL Server connection string. Falls back to `SQLDATAPACK_CONNECTION`. |
| `--deploy-schema` | `none` (default) or `dacpac`. Needs a package exported with `--schema dacpac`. |
| `--row-count-drift` | `warn` (default) imports what the file holds; `fail` rejects a package whose counts moved. |
| `--batch-size` | Rows per batch. Defaults to 1,000. |
| `--timeout` | Bulk copy timeout in seconds. |
| `--options` | JSON file for everything else. |
| `--quiet`, `-q` | Suppress per-table progress. |

The target tables must already exist and be empty, unless the package carries a dacpac and you pass `--deploy-schema dacpac`. Editing the file changes its row counts, which is normal; `--row-count-drift fail` is what an unattended scrubbing pipeline wants. See [Importing](/importing).

## Keeping the connection string out of the command

Anything on the command line lands in shell history and in the process list. Use the environment variable instead:

```bash
export SQLDATAPACK_CONNECTION="Server=.;Database=Northwind;Integrated Security=true"
sqldatapack export --out dev-slice.sqlite --tables dbo.Customers
```

## The options file

The flags cover the common path. Everything else lives in a JSON file whose property names are the library's own [option](/options) names:

```json
{
  "tableSelection": "Only",
  "tables": ["dbo.Customers", "dbo.Orders"],
  "excludeColumns": ["dbo.Customers.NationalId"],
  "globalWhereClauses": [
    { "columnName": "CustomerId", "whereClause": "CustomerId = 42" }
  ],
  "schemaCaptureMode": "Dacpac",
  "dacpacCaptureOptions": { "schemaScope": "SelectedExportTables" },
  "batchSize": 5000
}
```

```bash
sqldatapack export -c "..." -o dev-slice.sqlite --options support-slice.json
```

Three things worth knowing:

- **Explicit flags win.** `--batch-size 250` overrides the file. A flag you did not type changes nothing, so a file can describe a slice while one run tweaks a single value.
- **An unknown property is an error**, not something silently ignored. A typo that quietly did nothing would produce a slice that is wrong in a way nobody notices.
- **A connection string in the file is refused.** The file is meant to be committed and reviewed; credentials are not. Keep them on the command line or in `SQLDATAPACK_CONNECTION`.

Comments and trailing commas are allowed.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Done. |
| `1` | SqlDataPack refused the operation. The message prints without a stack trace. |
| `2` | The command line or the options file was wrong. |
| `3` | Something unexpected. Re-run with `--verbose` for a stack trace. |
| `130` | Cancelled with Ctrl+C. |

Progress goes to stderr and the result summary to stdout, so `sqldatapack export ... > result.txt` keeps the two apart.

## Locked-down machines

The downloadable and winget builds are one self-contained file that unpacks itself into `%TEMP%\.net\` (`$TMPDIR` on Linux and macOS) the first time each version runs. That costs a couple of seconds once per machine, and it is why the tool needs no .NET installed.

Where policy blocks execution out of the temp directory, point it somewhere allowed:

```powershell
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = "C:\ProgramData\SqlDataPack\bundle"
sqldatapack --version
```

The binaries are not code signed. `winget` verifies the SHA256 from the manifest, and every release carries a build provenance attestation you can check with the GitHub CLI:

```bash
gh attestation verify sqldatapack-win-x64.exe --repo zachtbeer/sqldatapack
```

## What the CLI does not do

`inspect` and `preflight` are not here yet. To read a package's manifest without touching a database, or to find out what an import would reject before it writes anything, use `SqlDataPackReader.ReadManifestAsync` and `PreflightAsync` from [the library](/importing).
