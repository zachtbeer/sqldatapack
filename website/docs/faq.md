---
title: FAQ
sidebar_label: FAQ
---

## Do I need SQL Server to open the file?

No. The package is an ordinary SQLite file, so any SQLite tool works: `sqlite3`, a GUI, a Python script, an EF Core context. SQL Server is only needed for the export and import calls themselves, not for inspecting or editing what sits between them. See [Package format](/package-format) for what the file holds.

## Can I edit the data? Can I delete rows?

You can edit data freely. Ordinary `UPDATE` statements against the package roundtrip through import. You cannot delete rows without also touching internal state, because import compares the rows it copies against `exported_row_count`, recorded in `zsdp_table_stats` at export time, and fails on a mismatch. Filter rows out at export instead. See [Editing the package](/editing-the-package) for the full rules on what you can and cannot change.

## Does it send anything anywhere?

No. SqlDataPack makes no outbound network calls beyond the SQL Server connection you supply, and it has no telemetry, analytics, or update checks. Nothing else leaves the machine during export or import. See [Getting started](/getting-started) for what a run actually touches.

## Does the target need the same schema?

The target tables must already exist and be empty, unless you capture the schema as a dacpac at export and deploy it at import. Without dacpac deployment, SqlDataPack writes rows into existing structure; it does not create tables or columns for you. See [Importing](/importing) for the exact preconditions import checks.

## Are identity values preserved?

Yes. Import writes with `SqlBulkCopyOptions.KeepIdentity`, so identity columns keep their original values instead of getting new ones, and parent/child relationships stay intact when the target schema is compatible. See [Importing](/importing) for the rest of what import guarantees.

## Does it work with temporal tables?

Yes. SqlDataPack imports both the current rows and the history rows of a system-versioned temporal table, preserving their original `ValidFrom`/`ValidTo` period values instead of letting SQL Server generate new ones. See [Troubleshooting](/troubleshooting) for how this works and what can go wrong.

## Is the package format stable?

Frozen for 1.0. Barring a critical fix, a package produced by the `1.0.0-rc` format imports unchanged on `1.0.0`, and the format follows the same versioning policy as the rest of the library from there. See [Versioning](/versioning) for the full compatibility policy.

## Why is my table exported unfiltered?

Global WHERE predicates fail open: a table missing even one of a predicate's named columns is exported in full. That is deliberate, so a shared lookup table is not silently truncated by a predicate written for tenant-scoped tables. The export warns once per unmatched table, naming the clause, the table and the columns it was missing, and stores that warning in the package, so a mismatched column name shows up in the export result rather than passing silently. See [Options](/options) for how to scope a predicate so it cannot fail open.

## Can I use it in CI?

Yes. It is one NuGet package called from your own test or pipeline code, with nothing extra to install on the CI server, no separate service, and no license check. See [Support matrix](/support-matrix) for the frameworks and environments it is tested against.
