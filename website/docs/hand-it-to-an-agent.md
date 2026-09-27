---
title: Hand it to an agent
sidebar_label: Hand it to an agent
---

Give a coding agent real data to work against without giving it a database or a connection string.

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

await SqlData.ExportAsync(
    sourceSqlServerConnectionString,
    "agent-handoff.sqlite",
    options);

var packagePath = "agent-handoff.sqlite"; // hand this to the agent, not a connection string
```

## What the agent sees

One SQLite file, openable with any SQLite tool, no SQL Server and no driver required:

```text
$ sqlite3 agent-handoff.sqlite ".tables"
dbo__customers        zsdp_columns          zsdp_import_plan      zsdp_tables
dbo__invoices         zsdp_exclusions       zsdp_schema_packages  zsdp_warnings
dbo__orders           zsdp_export_runs      zsdp_table_stats

$ sqlite3 agent-handoff.sqlite "SELECT CustomerId, Name FROM dbo__customers LIMIT 3;"
1|Northwind Traders
2|Contoso Ltd
3|Fabrikam, Inc.
```

The bare-named tables hold the actual rows, one SQLite table per exported SQL Server table. The `zsdp_*` tables are metadata: source names, types, row counts, import order, warnings. That prefix is the whole rule: `zsdp_` is bookkeeping, everything else is data. An agent with ordinary SQLite tooling (`sqlite3`, a Python script, whatever it already has) can query, read, and reason about all of it without ever touching SQL Server.

## What it may change

The agent can rewrite values freely: fix a typo, correct a data type mismatch, regenerate a bad row, whatever the task calls for. It can also delete and insert rows, which is usually what an instruction like "drop everything for this customer" turns into. What it cannot do is change the shape of the package: adding, renaming or dropping columns and tables breaks the import. See [Editing the package](/editing-the-package) for the full list of what is safe to change and what is not.

## The guardrails

An editable, self-describing SQLite file is a good handoff format for an agent: it's local, inspectable, and needs no live database or credentials to hand over. Editable also means an agent can break something, so import checks its work.

Import compares the row count the package holds now against what export recorded, so a scrubbing script that accidentally drops rows, an overzealous `DELETE`, or an agent told to drop rows on purpose all show up the same way: a warning per table on `SqlDataPackResult.Warnings` naming both counts, with the import still loading the rows that are there. Set `RowCountDrift.Fail` if a moved count should stop the import instead of just warning about it. Either way, the check only tells you the count changed; it can't tell a deliberate delete from a mistake.

For metadata reads, point the agent at `SqlDataPackReader` rather than the internal `zsdp_*` tables directly; the reader is what's supported, the table layout is not.

## What to hand over with it

Hand over the package and let the agent read its manifest before it touches anything:

```csharp
var manifest = await new SqlDataPackReader().ReadManifestAsync("agent-handoff.sqlite");

foreach (var table in manifest.Tables)
    Console.WriteLine($"{table.FullName}: {table.ExportedRowCount} rows");
// dbo.Customers: 1,204 rows
// dbo.Orders: 22,109 rows
// dbo.Invoices: 18,755 rows

Console.WriteLine(string.Join(" -> ", manifest.ImportOrder));
```

The manifest carries each table's original SQL Server type metadata (`SqlServerTypeName` on every column), the row count recorded at export, and the foreign-key-based order tables need to import in. An agent that reads the manifest first knows what it is looking at before it writes a single `UPDATE`.

:::caution Scrub before you hand it over, not after
Everything above assumes the package was already scoped and scrubbed before the agent saw it. If the source data is sensitive, do that at export, not as a step you ask the agent to do for you: see [Masked slice for dev](/masked-slice-for-dev) for excluding columns and filtering rows before anything leaves SQL Server. Handing over a package instead of database access removes credential and network exposure, but it does not by itself make the data inside it safe to hand to a hosted agent.
:::
