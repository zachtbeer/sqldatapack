---
title: Slice with schema
sidebar_label: Slice with schema
---

Send a slice to an environment where the target database does not exist yet, tables included.

```csharp
using SqlDataPack;
using SqlDataPack.Models;

var exportOptions = ExportOptions.Default;
exportOptions.SchemaCaptureMode = SchemaCaptureMode.Dacpac;
exportOptions.DacpacCaptureOptions.SchemaScope = DacpacSchemaScope.SelectedExportTables;
exportOptions.TableSelection = ExportTableSelectionMode.Only;
exportOptions.Tables =
[
    "dbo.Customers",
    "dbo.Orders",
    "dbo.Invoices"
];

await SqlData.ExportAsync(
    sourceSqlServerConnectionString,
    "new-env-slice.sqlite",
    exportOptions);

var importOptions = ImportOptions.Default;
importOptions.SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac;

await SqlData.ImportAsync(
    "new-env-slice.sqlite",
    targetSqlServerConnectionString,
    importOptions);
```

No table needs to exist on the target beforehand. The dacpac inside the package creates them.

## Capture the schema

By default, SqlDataPack exports data and metadata, not schema. Set `SchemaCaptureMode = SchemaCaptureMode.Dacpac` on `ExportOptions` and the export also extracts the source database as a dacpac and embeds it in the SQLite file.

By default, that dacpac carries the *entire* source database schema, not just the tables you selected. For a slice, that is usually more than you want to carry around, so scope it down:

```csharp
exportOptions.DacpacCaptureOptions.SchemaScope = DacpacSchemaScope.SelectedExportTables;
```

`DacpacSchemaScope.SelectedExportTables` captures only the tables chosen by the export plan, plus whatever DacFx needs to script them: a smaller, plan-scoped dacpac instead of the whole database model. `DacpacCaptureOptions` is already initialized on a fresh `ExportOptions`, so you set its fields directly without constructing a new one.

## Deploy it on import

On the import side, set `SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac` on `ImportOptions` and import deploys the embedded dacpac against the target before it copies any rows. `DacpacDeploymentOptions` is likewise already initialized on a fresh `ImportOptions`:

```csharp
importOptions.SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac;
importOptions.DacpacDeploymentOptions.AllowIncompatiblePlatform = false;
```

That is the whole sequence: capture with `SchemaCaptureMode.Dacpac` at export, deploy with `SchemaDeploymentMode.DeployDacpac` at import. Nothing else about the export or import call changes.

:::note VerifyExtraction defaults to false
Dacpac capture does not run DacFx's model verification by default (`DacpacCaptureOptions.VerifyExtraction = false`). That is deliberate: DacFx's static validator is stricter than SQL Server's own binder, and it rejects things that run fine in production, an ambiguous unqualified column in a multi-table join, a cross-database or three-part name, a temp table. With verification off, that functional-but-imperfect legacy schema still captures cleanly.

Set `VerifyExtraction = true` and capture validates the extracted model and fails the export early on a genuinely broken reference, instead of you finding out at deploy time. The cost: verification has no per-rule suppression, so turning it on re-enables every model-validation rule at once, and one benign false positive blocks the whole export. See [SQL71501 unresolved reference](/troubleshooting#export-failed-sql71501-unresolved-reference) for what that failure looks like and how to read it.
:::

## When deployment refuses

Dacpac deployment is conservative by default, and it refuses in two specific places.

**Possible data loss.** `DacpacDeploymentOptions.BlockOnPossibleDataLoss` defaults to `true`. If deploying the captured schema would drop a populated column or otherwise lose existing target data, deployment stops before it happens. Only set it to `false` for a migration you already know is destructive, and only after you have reviewed the target. Object drops are a separate, narrower switch (`AllowObjectDrops`, also `false` by default): even with data loss allowed, target objects absent from the package schema are left alone unless you opt in.

**Platform mismatch.** `DacpacDeploymentOptions.AllowIncompatiblePlatform` defaults to `false`, so a deploy across incompatible SQL platforms, an on-premises dacpac against Azure SQL, for example, fails fast rather than attempting a deployment DacFx cannot guarantee. Set it to `true` only once you have confirmed the mismatch is safe for your schema.

Both are read from the target at deploy time, so the failure shows up on the `ImportAsync` call, not the export. See [Troubleshooting](/troubleshooting) for the exact errors and how to work through them.

## What does not travel

The dacpac carries tables, columns, keys, and the other objects DacFx scripts for the objects in scope. It does not carry the source's users, logins, permissions, or role membership: `DacpacDeploymentOptions.DeployUsers`, `DeployLogins`, `DeployPermissions`, and `DeployRoleMembership` all default to `false`, so a deploy leaves target security exactly as it was. Turn them on only when the environment receiving the package should actually inherit that security setup from the source, most environments should not.

Full details, including the deploy errors you get if you try to deploy on top of an incompatible target, are in [Troubleshooting](/troubleshooting).
