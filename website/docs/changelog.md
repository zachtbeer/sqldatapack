---
id: changelog
title: Changelog
sidebar_label: Changelog
---

What has changed in SqlDataPack, in the order it happened, written to be read rather than to be diffed.

Every published version also has generated notes on the [releases page](https://github.com/zachtbeer/sqldatapack/releases), listing the pull requests it contains. This page is the account of what those changes were for.

## Unreleased

Nothing published yet under the `SqlDataPack` package id. The first release will be `1.0.0`.

Export can now scrub sensitive columns on the way into the package. `ExportOptions.Transformations` binds a transformer to a fully qualified `schema.table.column` path, and the value it returns is what gets written — the original never reaches the file. The library ships maskers and pseudonymizers for email addresses, phone numbers, names, free-form strings, numbers, GUIDs, IPv4 and IPv6 addresses, and US SSNs, and `CustomTransformer` (or your own `IValueTransformer`) covers everything else.

Pseudonymizers are deterministic within one export, so the same address in `dbo.Customers.Email` and `dbo.Orders.ContactEmail` still matches, and each export derives from its own random secret, so two exports of the same database do not agree and the secret is never written to the package. Transformation fails closed: a transformer that throws, one that returns NULL for a non-nullable column, and a result that does not fit the destination column all fail the export rather than falling back to the source value or truncating. A source NULL bypasses the transformer entirely. The package records which columns were transformed and how the built-in was configured, and nothing else. Uniqueness is explicitly not guaranteed — that is what a custom transformer is for.

Deleting and inserting rows in a package now works. Import used to compare the rows it copied against the count recorded at export and throw on any difference, from inside the per-table load loop, so a package you had deleted rows from failed halfway through and left a target that had to be emptied by hand before you could retry. The comparison now runs before anything is written, against the package's own contents, and a difference is reported as a warning per table instead of failing. The new `ImportOptions.RowCountDrift` restores the old strictness with `RowCountDrift.Fail`, which rejects the package up front rather than partway through. The separate check that a bulk copy landed every row it read is unchanged and still cannot be switched off.
