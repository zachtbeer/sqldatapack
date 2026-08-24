---
title: Importing
sidebar_label: Importing
---

Check an import before it copies anything, then run it:

```csharp
using SqlDataPack;

var preflight = await new SqlDataPackImporter().PreflightAsync(
    "support-snapshot.sqlite",
    targetSqlServerConnectionString);

if (!preflight.IsValid)
{
    foreach (var error in preflight.Errors)
    {
        Console.Error.WriteLine(error);
    }
}
else
{
    var result = await SqlData.ImportAsync(
        "support-snapshot.sqlite",
        targetSqlServerConnectionString);

    Console.WriteLine($"Imported {result.RowCount} rows into {result.TableCount} tables.");
}
```

## Before you import

Import is intentionally strict:

- target tables must already exist, unless dacpac deployment creates them
- target tables included in the package must be empty
- every exported column must exist in the target table
- extra target columns must be nullable, computed, identity, or have a default
- constraints stay enabled, and are re-checked for violations after load; a violation the package cannot explain away fails the import even though the rows already landed

Identity values are preserved with `SqlBulkCopyOptions.KeepIdentity`, so parent/child relationships roundtrip when the target schema is compatible.

## Preflight

`PreflightAsync` runs the same validation import would run, without deploying a dacpac or copying a single row. It returns a `SqlDataPackPreflightResult`:

- `IsValid`: whether the package and target passed validation.
- `Errors`: what failed, when `IsValid` is `false`.
- `Warnings`: non-fatal issues worth knowing about even when the import would succeed.
- `Manifest`: the package manifest, when it could be read.

Both `SqlDataPackExporter` and `SqlDataPackImporter` expose a `PreflightAsync`, so you can check an export before it writes a file and check an import before it copies rows, using the same result shape either way.

## Order

Export builds the import order while it plans: it reads SQL Server foreign keys among the selected tables and orders them so referenced tables import before the tables that depend on them. That order is stored in `zsdp_import_plan` and applied automatically; you do not choose it at import time. Cycles and more complex constraint scenarios are not resolved automatically. Prepare the target schema for those cases (for example, disable or defer the constraint) before import.

## Verification

After each table copies, import compares the rows it actually copied against `exported_row_count`, the count recorded in `zsdp_table_stats` at export time, not a live `COUNT(*)` against the package. A mismatch throws:

```text
Imported row count for 'dbo.Customers' was 4102, expected 4213.
```

That check exists so a partial copy, whatever the cause, fails loudly instead of leaving a target that looks complete but is not.

## Progress and warnings

Report progress during a long import the same way you would for export:

```csharp
var progress = new Progress<SqlDataPackProgress>(p =>
{
    Console.WriteLine($"{p.Kind}: {p.TableName} {p.RowsProcessed}/{p.TotalRows}");
});

var options = ImportOptions.Default;
options.Progress = progress;

await SqlData.ImportAsync("support-snapshot.sqlite", targetSqlServerConnectionString, options);
```

Import also reads `zsdp_warnings`, the warnings recorded during export, and folds them into its own warning list alongside anything it discovers itself (adaptive batching, temporal table handling, columns whose values are skipped in favor of SQL Server generating fresh ones). Every warning is reported live as a `SqlDataPackProgress` with `Kind == SqlDataPackProgressKind.Warning`, and the full, de-duplicated list comes back on `SqlDataPackResult.Warnings` once the import finishes. An empty list means every table copied clean and every row count matched.

## Dacpac deployment safety

When `SchemaDeploymentMode.DeployDacpac` is set, deployment defaults are conservative around destructive changes. DacFx blocks possible data loss, does not drop target objects missing from the package schema, and excludes database files, filegroups, users, logins, permissions, and role membership unless you opt in. Incompatible SQL Server platforms are rejected by default too: `AllowIncompatiblePlatform` defaults to `false`, so a deploy across incompatible platforms, an on-premises dacpac against Azure SQL, for example, fails fast rather than attempting a deployment DacFx cannot guarantee. Set it to `true` only once you have confirmed the mismatch is safe for your schema. See [Slice with schema](/slice-with-schema) for the full deploy sequence and what to set when a deploy refuses.

## When it fails

For the exact errors these checks produce and how to work through them, see [Troubleshooting](/troubleshooting).
