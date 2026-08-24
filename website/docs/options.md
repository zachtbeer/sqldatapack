---
title: Options
sidebar_label: Options
---

```csharp
using SqlDataPack.Models;

var options = ExportOptions.Default;
options.TableSelection = ExportTableSelectionMode.Only;
options.Tables = ["dbo.Customers"];
```

This page covers the options you're most likely to set on `ExportAsync` and `ImportAsync`, with the default value each one falls back to when you leave it alone.

## Default and Latest

Every options type exposes a static `Default` property: `ExportOptions.Default`, `ImportOptions.Default`, `DacpacCaptureOptions.Default`, `DacpacDeploymentOptions.Default`. Each call returns a fresh, mutable instance already set to the documented defaults; mutating what you get back never affects the next caller.

`ExportOptions` and `ImportOptions` also expose a static `Latest` property. `DacpacCaptureOptions` and `DacpacDeploymentOptions` do not; they only have `Default`. The difference between `Default` and `Latest` is about stability, not quality:

- **`Default`** is value-stable. The values it returns are part of the library's contract and will not change across releases. Use it when you need behavior that stays identical on upgrade: reproducible repros, golden-file tests, anything you do not want shifting under you.
- **`Latest`** tracks the library's current best-throughput tuning and may change in any minor release.

As of this release, `Latest` raises `BatchSize` to 5,000 and `MaxBatchBytes` to 8 MiB, compared to `Default`'s `BatchSize` of 1,000 and `MaxBatchBytes` of 4 MiB. The large-table safety net (`LargeTableBatchSize` and its thresholds) stays at the conservative defaults on both, so a genuinely huge table stays memory-safe either way.

```csharp
// Conservative and frozen. Never shifts on upgrade.
var stable = ExportOptions.Default;

// Best current tuning. Values may improve in future minor releases.
var tuned = ImportOptions.Latest;
```

## Choosing tables

`TableSelection` controls how `Tables` is read. It defaults to `ExportTableSelectionMode.AllExcept`, so `Tables` acts as an exclusion list against a full export. Set it to `ExportTableSelectionMode.Only` to make `Tables` an inclusion list instead.

`Tables` defaults to an empty list. Under the default `AllExcept` mode, an empty list exports every user table.

Patterns support four forms:

| Pattern | Matches |
| --- | --- |
| `dbo.Customers` | The exact table `Customers` in schema `dbo` |
| `Customers` | A table named `Customers` in any schema |
| `dbo.zz*` | Any table starting with `zz` in schema `dbo` |
| `*.zz*` | Any table starting with `zz` in any schema |

```csharp
options.TableSelection = ExportTableSelectionMode.Only;
options.Tables = ["dbo.Customers", "dbo.Invoices", "dbo.InvoiceLineItems"];
```

### SSMS diagram rows

`ExcludeSsmsDiagrams` defaults to `true`. SQL Server Management Studio's "Database Diagrams" feature creates `dbo.sysdiagrams` as a regular user table, so without this setting it would export like any other table even though its rows are just editor metadata.

The exclusion is unconditional: naming `dbo.sysdiagrams` explicitly in `Tables` still exports zero rows for it. Set the option to `false` to export its rows. This affects only the exported data; when dacpac schema capture is enabled, the diagram table, its helper procedures, and its function are still captured as part of the schema either way.

```csharp
options.ExcludeSsmsDiagrams = false; // include dbo.sysdiagrams rows in the package
```

## Excluding columns

`ExcludeColumns` takes fully qualified `schema.table.column` paths and omits those columns from the exported package while keeping the rest of the table. Defaults to an empty list, so every column of every selected table is exported.

```csharp
options.ExcludeColumns = ["dbo.Customers.Ssn", "dbo.SupportCases.InternalNotes"];
```

## Filtering rows

`GlobalWhereClauses` applies a SQL Server WHERE predicate to every selected table that has the clause's named column or columns. `GlobalWhereClause` has two constructors:

- `new GlobalWhereClause(columnName, whereClause)`: gated on a single column. The predicate applies independently to any selected table that has that column.
- `new GlobalWhereClause(columnNames, whereClause)`: gated on a set of columns, taking an `IEnumerable<string>`. A table must have **every** named column for the predicate to apply; a table missing even one of them is exported unfiltered.

```csharp
options.GlobalWhereClauses =
[
    new GlobalWhereClause("TenantId", "TenantId = 123"),
    new GlobalWhereClause(["TenantId", "IsDeleted"], "TenantId = 123 AND IsDeleted = 0")
];
```

Defaults to an empty list.

`PerTableWhereClauses` applies a predicate to one named table only, and does not fail open onto other tables:

```csharp
options.PerTableWhereClauses = [new PerTableWhereClause("dbo.Orders", "Status = 'Open'")];
```

Defaults to an empty list.

:::warning Global predicates fail open
A table that matches some, but not all, of a clause's named columns is not filtered by that clause: it is exported **unfiltered**, every row. That is deliberate, so a shared lookup table is not silently truncated by a predicate written for tenant-scoped tables. But it cuts both ways: if your soft-delete column is called `Deleted` on one table and `IsDeleted` everywhere else, that table exports in full. You get a warning for it rather than silence — one per unmatched table, naming the clause, the table and the columns it was missing, stored in the package alongside the rest of the export warnings. It is still a warning, not an error: a clause only errors when it matches no selected table at all.

Gating looks at the table's source columns, not the exported ones, so a predicate may reference a column that `ExcludeColumns` removes from the output. Gating and filtering both read the source columns.

When a predicate must never fail open, do not rely on a global clause. Use `PerTableWhereClauses` for exact tables, or narrow `TableSelection` so unmatched tables cannot be exported at all.
:::

## Naming

`DataTablePrefix` is prepended to every exported data table inside the SQLite package. It defaults to `null`, which writes data tables under their bare generated name, so `dbo.Customers` becomes `dbo__customers`. Set a value to group data tables behind a prefix instead — `"app"` writes `app_dbo__customers`. The package's own metadata tables are always named `zsdp_*` and are unaffected.

`zsdp_` and `sqlite_` are reserved. A generated name landing in either namespace fails the export with an error naming the source table — reachable both through a prefix (`DataTablePrefix = "sqlite"`) and, with no prefix, through a source schema named `sqlite` or `zsdp`.

## Throughput

These properties exist, with the same names and the same defaults, on both `ExportOptions` and `ImportOptions`.

- **`BatchSize`**: row count per write batch (bulk-copy batch on import) for normal-sized tables. Defaults to `1000`.
- **`AdaptiveBatchingEnabled`**: enables the large-table planner that shrinks batch sizes for tables crossing a threshold below. Defaults to `true`; set to `false` to always use `BatchSize`.
- **`LargeTableThresholdBytes`**: estimated table size, in bytes, at or above which a table is treated as large and switched to `LargeTableBatchSize`. Defaults to 50 MiB.
- **`LargeTableRowThreshold`**: estimated row count at or above which a table is treated as large when size metadata is unavailable. Defaults to `100,000` rows.
- **`LargeTableBatchSize`**: row count per batch once a table crosses either large-table threshold. Defaults to `250`.
- **`MaxBatchBytes`**: approximate upper bound, in bytes, on the in-memory size of a single batch, capping memory regardless of `BatchSize`. Defaults to 4 MiB.

Command timeouts are named differently depending on which side they apply to, all in seconds, all defaulting to `null` (the provider's own default, typically 30 seconds):

- `ExportOptions.CommandTimeout`: metadata queries and data reads during export.
- `ImportOptions.ValidationCommandTimeout`: target validation queries before bulk copy begins.
- `ImportOptions.BulkCopyTimeout`: each `SqlBulkCopy` operation.

## Validation

`FailOnLossyTypeMismatch` on `ImportOptions` fails the import before any row is copied when a target column would lose data relative to the package: a shorter `char`, `varchar`, `nchar`, `nvarchar`, `binary` or `varbinary`, a smaller `decimal` precision or scale, or a smaller `datetime2`, `datetimeoffset` or `time` scale. Defaults to `false`, in which case every type difference is reported as a warning on `SqlDataPackResult.Warnings` and the import proceeds.

Widening differences and collation differences are always warnings and are never affected by this setting: a collation difference can mangle non-ASCII text but cannot be judged from catalog metadata alone, so blocking on it would fail every import into a differently collated server.

## Progress

`Progress`, an `IProgress<SqlDataPackProgress>`, receives table- and row-level updates as export or import runs. It exists on both `ExportOptions` and `ImportOptions`, and defaults to `null` on both (no progress reporting).

`SqlDataPackProgress` is the record delivered to it:

| Property | Type | Meaning |
| --- | --- | --- |
| `Kind` | `SqlDataPackProgressKind` | The event kind: `OperationStarted`, `TableStarted`, `RowsCopied`, `TableCompleted`, `Warning`, or `OperationCompleted`. |
| `TableName` | `string?` | The source table full name, when the event is table-specific. Defaults to `null`. |
| `RowsProcessed` | `long` | Rows processed so far for the table or the operation. Defaults to `0`. |
| `TotalRows` | `long?` | Expected total rows, when known. Defaults to `null`. |
| `Message` | `string?` | An optional human-readable message. Defaults to `null`. |

```csharp
options.Progress = new Progress<SqlDataPackProgress>(p =>
    Console.WriteLine($"{p.Kind}: {p.TableName} {p.RowsProcessed}/{p.TotalRows}"));
```

## Logging

`Logger`, an `ILogger`, receives the same lifecycle, table, row-batch, and warning events as `Progress`, mapped to log levels. It exists on both `ExportOptions` and `ImportOptions`, and defaults to `null` on both (no logging).

- Row-batch events log at `Trace`, so they stay quiet unless you enable `Trace`.
- Table and operation events log at `Information`.
- Warnings log at `Warning`.

`Progress` and `Logger` can be set together; both receive every event. The logger writes only to the sink your application configures, so setting it does not add a network call.

```csharp
options.Logger = loggerFactory.CreateLogger("SqlDataPack");
```

## Dacpac

`SchemaCaptureMode` on `ExportOptions` selects whether export embeds a dacpac in the package. Defaults to `SchemaCaptureMode.None` (data only). Set it to `SchemaCaptureMode.Dacpac` to extract one.

`SchemaDeploymentMode` on `ImportOptions` selects whether import deploys the package's embedded dacpac before loading data. Defaults to `SchemaDeploymentMode.None` (assume the target schema already exists). Set it to `SchemaDeploymentMode.DeployDacpac` to deploy it first.

`DacpacCaptureOptions`, used only when `SchemaCaptureMode.Dacpac` is set:

| Property | Default |
| --- | --- |
| `SchemaScope` | `DacpacSchemaScope.Database` (the whole database model; `SelectedExportTables` captures only the exported tables plus what DacFx needs to script them) |
| `ExtractReferencedServerScopedElements` | `false` |
| `ExtractApplicationScopedObjectsOnly` | `false` |
| `IgnorePermissions` | `true` |
| `IgnoreUserLoginMappings` | `true` |
| `VerifyExtraction` | `false` |

`DacpacDeploymentOptions`, used only when `SchemaDeploymentMode.DeployDacpac` is set:

| Property | Default |
| --- | --- |
| `AllowIncompatiblePlatform` | `false` (fail fast on a platform mismatch, such as an on-premises dacpac deployed to Azure SQL) |
| `BlockOnPossibleDataLoss` | `true` |
| `AllowObjectDrops` | `false` |
| `DeployUsers` | `false` |
| `DeployLogins` | `false` |
| `DeployPermissions` | `false` |
| `DeployRoleMembership` | `false` |
| `DeployDatabaseFiles` | `false` |
| `DeployDatabaseOptions` | `false` |
| `AdaptAzureSourceForOnPremTarget` | `true` |
| `VerifyDeployment` | `true` |

See [Slice with schema](/slice-with-schema) for the full capture-and-deploy sequence, and what to set when a deploy refuses.
