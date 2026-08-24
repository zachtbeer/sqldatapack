---
title: Repro a customer bug
sidebar_label: Repro a customer bug
---

Pull one customer's data out of a large database, small enough to attach to a bug report, and hand it to someone with no database access.

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
options.PerTableWhereClauses =
[
    new PerTableWhereClause("dbo.Customers", "CustomerId = 4821"),
    new PerTableWhereClause("dbo.Orders", "CustomerId = 4821"),
    new PerTableWhereClause("dbo.Invoices", "CustomerId = 4821")
];

var result = await SqlData.ExportAsync(
    sourceSqlServerConnectionString,
    "customer-4821-repro.sqlite",
    options);
```

## Narrow to the tables that matter

`TableSelection = ExportTableSelectionMode.Only` turns `Tables` into an inclusion list: only the named tables export, nothing else comes along. That is the opposite of the default, `AllExcept`, where `Tables` is an exclusion list against a full export.

`Tables` supports exact source table names and `*` wildcards. Use schema-qualified names such as `dbo.Customers`, table names such as `Customers`, or wildcard patterns such as `dbo.zz*` and `*.zz*`. For a bug repro, name the exact tables the bug touches so the file stays small: here, `dbo.Customers`, `dbo.Orders`, and `dbo.Invoices`.

## Filter to one customer

`PerTableWhereClauses` applies a SQL Server WHERE predicate to one exact table:

```csharp
options.PerTableWhereClauses =
[
    new PerTableWhereClause("dbo.Customers", "CustomerId = 4821"),
    new PerTableWhereClause("dbo.Orders", "CustomerId = 4821"),
    new PerTableWhereClause("dbo.Invoices", "CustomerId = 4821")
];
```

A per-table clause names its table exactly, so it applies there and nowhere else. That is the right tool here, and a global clause is not. A `GlobalWhereClause` [fails open](/masked-slice-for-dev) on any selected table that lacks its gating column, which means the wrong column name on one table silently exports every row of that table, every customer. `PerTableWhereClauses` has no such gap: name the wrong table and the export just fails to find it, it does not quietly export unfiltered data.

## Check the size before you send it

Before it goes into a bug report, look at what actually got exported:

```text
$ sqlite3 customer-4821-repro.sqlite "SELECT 'dbo.Customers', COUNT(*) FROM dbo__customers UNION ALL SELECT 'dbo.Orders', COUNT(*) FROM dbo__orders UNION ALL SELECT 'dbo.Invoices', COUNT(*) FROM dbo__invoices;"
dbo.Customers|1
dbo.Orders|14
dbo.Invoices|9
```

One customer row, a handful of orders and invoices. That is small enough to attach to a ticket and small enough for the recipient to read every row while debugging.

## The other side

The person you hand this to runs one call, into their own empty schema, with no credentials for your database and no network calls beyond their own SQL Server:

```csharp
var result = await SqlData.ImportAsync(
    "customer-4821-repro.sqlite",
    theirOwnSqlServerConnectionString);

Console.WriteLine($"Imported {result.RowCount} rows into {result.TableCount} tables.");
```

The target tables must already exist and be empty. Nothing about this step touches your source database, the connection string in it, or your network. The file carries everything the recipient needs to reproduce the bug locally.

## Combining global and per-table filters

Global and per-table predicates stack. Add a global clause for something that applies broadly, such as a tenant, and a per-table clause for something that only makes sense on one table:

```csharp
var options = ExportOptions.Default;
options.GlobalWhereClauses =
[
    new GlobalWhereClause("TenantId", "TenantId = 123")
];
options.PerTableWhereClauses =
[
    new PerTableWhereClause("dbo.Orders", "Status = 'Open'")
];

var result = await SqlData.ExportAsync(
    sourceSqlServerConnectionString,
    "tenant-snapshot.sqlite",
    options);
```

Every selected table with a `TenantId` column is scoped to tenant 123 by the global clause, and `dbo.Orders` gets the extra `Status = 'Open'` restriction on top, from the per-table clause. Both predicates are ANDed together on the tables where both apply.
