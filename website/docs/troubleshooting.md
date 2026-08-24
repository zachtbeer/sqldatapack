---
title: Troubleshooting
sidebar_label: Troubleshooting
---

This page lists common SqlDataPack failures and the action that usually fixes them.

## Import failed: target table is not empty

Import only writes into empty target tables. If you see an error that a target table must be empty, truncate or recreate the target table before import.

SqlDataPack does this intentionally so partial merges, duplicate rows, and identity conflicts are explicit application decisions instead of implicit library behavior.

## Import failed: target table does not exist

SqlDataPack does not create SQL Server schemas. Create or migrate the target schema before import, then run import again.

The target table names and schemas must match the exported source metadata.

## Import failed: target column does not exist

Every exported column must exist in the target table. Either add the missing target column or exclude the source column during export:

```csharp
var options = new ExportOptions
{
    ExcludeColumns = ["dbo.Customers.LegacyCode"]
};
```

## Import failed: extra target column is required

Extra target columns are allowed only when they are nullable, computed, identity, or have a default. If import fails on an extra target column, make that column nullable, add a default constraint, or exclude the table from the export scope.

## Import failed: cannot insert rows in a temporal history table (Msg 13560 / 13536)

System-versioned temporal tables reject direct inserts into their history table (Msg 13560, "Cannot insert rows in a temporal history table") and into the `GENERATED ALWAYS` period columns of the current table (Msg 13536). By default SqlDataPack handles this automatically: it discovers the temporal tables on the target, temporarily sets `SYSTEM_VERSIONING = OFF`, drops the `SYSTEM_TIME` period, loads the current and history rows with their original `ValidFrom`/`ValidTo` values, then re-adds the period and re-enables system versioning.

You should only see these errors if you disabled the behavior:

```csharp
var options = ImportOptions.Default;
options.SuspendTemporalSystemVersioning = false; // leave at the default `true` to import temporal tables
```

If re-enabling system versioning fails with a consistency error, the current and history rows are inconsistent, most often because the temporal table or its history was filtered with a WHERE clause, a period column was excluded, or the source changed between reading the two tables. Re-export the full table pair, or skip the validation (at the cost of possibly incorrect `AS OF` results):

```csharp
var options = ImportOptions.Default;
options.TemporalDataConsistencyCheck = false;
```

## Export failed: unsupported included type

Export fails during preflight when an included column uses an unsupported SQL Server type. Exclude the column or exclude the table:

```csharp
var options = new ExportOptions
{
    ExcludeColumns =
    [
        "dbo.Payloads.PayloadVariant"
    ]
};
```

Unsupported types are listed in [Supported types](/supported-types).

`xml` columns are supported as text-preserved values. If a package is edited and an XML value becomes invalid, import fails with SQL Server's XML conversion error.

Native SQL Server `json` columns are supported as text-preserved values on SQL Server 2025 and Azure SQL. Existing JSON stored in text columns is handled as ordinary text. If a package is edited and a native JSON value becomes invalid, import fails with SQL Server's JSON validation error.

Native SQL Server `vector` columns are supported as `TEXT` JSON arrays. `float32` vectors (the GA base type) round-trip bit-for-bit via the native `SqlVector<float>` binary transport; the preview `float16` base type round-trips via its JSON representation. The matching `vector(N[, float16])` column must already exist on the import target. A `float16` target additionally requires `ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;`. Export emits a warning when a `float16` column is present.

## Export failed: package file already exists

Export does not replace an existing SQLite package by default. Delete the file yourself or opt in to replacement:

```csharp
var options = ExportOptions.Default;
options.OverwriteExistingPackage = true;
```

`ExportOptions.Default` returns a fresh instance with the documented defaults; tweak only the fields you need.

Replacement happens only after a successful export. Failed exports should not replace the previous package.

## Import failed: package format is not supported

Import validates the package format version before reading data. It accepts every format from the oldest this build can read up to the one it writes, so a newer release still imports packages written by older releases of the same major version.

Two cases fail, and the message says which one you hit:

- The package was written by a newer version of SqlDataPack. Upgrade SqlDataPack to the version that produced the package, or newer.
- The package format is older than this build reads. Re-export it with your current version, or import it with a SqlDataPack version that still reads that format.

If format metadata is missing, recreate the package with SqlDataPack v1.0 or later.

## Dacpac deployment blocks possible data loss

Dacpac deployment blocks possible data loss by default. Review the target database and only opt out when that risk is acceptable:

```csharp
var options = ImportOptions.Default;
options.SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac;
options.DacpacDeploymentOptions.BlockOnPossibleDataLoss = false;
```

Object drops are still disabled unless `AllowObjectDrops` is also set.

## Dacpac deployment skips users or permissions

Users, logins, permissions, and role membership are excluded by default so imports do not unexpectedly change target security. Opt in only when the target environment should receive those objects from the dacpac:

```csharp
var options = new ImportOptions
{
    SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac,
    DacpacDeploymentOptions = new DacpacDeploymentOptions
    {
        DeployUsers = true,
        DeployPermissions = true,
        DeployRoleMembership = true
    }
};
```

## Dacpac deployment rejects incompatible platform

Dacpac deployment rejects a source and target platform mismatch by default. `DacpacDeploymentOptions.AllowIncompatiblePlatform` defaults to `false`, so a deploy across incompatible SQL platforms, an on-premises dacpac against Azure SQL, for example, fails fast rather than attempting a deployment DacFx cannot guarantee. If your environment needs to deploy across platforms on purpose, opt in once you have confirmed the mismatch is safe for your schema:

```csharp
var options = new ImportOptions
{
    SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac,
    DacpacDeploymentOptions = new DacpacDeploymentOptions
    {
        AllowIncompatiblePlatform = true
    }
};
```

## Export failed: SQL71501 unresolved reference

Dacpac capture no longer runs DacFx model verification by default, so this should not block a normal export. If you have opted into verification (`DacpacCaptureOptions.VerifyExtraction = true`) you may see an error like:

```text
SQL71501: ... Procedure [dbo].[SomeProcedure] contains an unresolved reference to an object.
Either the object does not exist or the reference is ambiguous because it could refer to any of
the following objects: [dbo].[TableA].[SharedKey], [dbo].[TableB].[SharedKey] ...
```

This comes from DacFx's static model validator, which is stricter than SQL Server's actual binder. The most common triggers (an ambiguous unqualified column in a multi-table join, a cross-database or three-part name, or a temp table) describe objects that create and run perfectly well on the target. SqlDataPack therefore leaves verification off by default so functional-but-imperfect legacy schema captures cleanly.

Turn verification back on only when you want the export to fail early on a genuinely broken object:

```csharp
var exportOptions = ExportOptions.Default;
exportOptions.SchemaCaptureMode = SchemaCaptureMode.Dacpac;
exportOptions.DacpacCaptureOptions.VerifyExtraction = true; // fail export early on broken refs
```

Verification has no per-rule suppression, so enabling it re-enables every model-validation rule; a single benign false positive will block the whole export.

A reference that is *genuinely* unresolvable (for example, a procedure reading a column that no longer exists on an existing table) is not caught with verification off. It instead fails at deploy time with `Msg 207, Invalid column name`, and because dacpac deployment runs transactionally the whole deploy rolls back. Fix the source object or remove it from the capture scope.

## SQL Server certificate or trust errors

Local and containerized SQL Server instances often require `TrustServerCertificate=True` in development connection strings:

```text
Server=localhost;Database=MyDb;User Id=sa;Password=...;TrustServerCertificate=True
```

Use production-appropriate certificate validation for real deployments.

## Testcontainers or Docker failures

Integration tests require Docker and use SQL Server Testcontainers. Check that:

- Docker is running.
- The current user can start containers.
- Enough memory is available for SQL Server.
- No corporate proxy or registry policy blocks container image pulls.

If Docker is unavailable, run only the unit test project:

```bash
dotnet test tests/SqlDataPack.Tests/SqlDataPack.Tests.csproj
```

## NuGet vulnerability-data warnings

Warnings like `NU1900` can appear when the NuGet vulnerability service cannot be reached. They do not necessarily mean restore failed. If restore or build fails with `NU1301`, confirm network access to `https://api.nuget.org/v3/index.json`.
