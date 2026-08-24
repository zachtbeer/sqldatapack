---
id: changelog
title: Changelog
sidebar_label: Changelog
---

What has changed in SqlDataPack, in the order it happened, written to be read rather than to be diffed.

Every published version also has generated notes on the [releases page](https://github.com/zachtbeer/sqldatapack/releases), listing the pull requests it contains. This page is the account of what those changes were for.

## Unreleased

Nothing published yet under the `SqlDataPack` package id. The first release will be `1.0.0`.

Deleting and inserting rows in a package now works. Import used to compare the rows it copied against the count recorded at export and throw on any difference, from inside the per-table load loop, so a package you had deleted rows from failed halfway through and left a target that had to be emptied by hand before you could retry. The comparison now runs before anything is written, against the package's own contents, and a difference is reported as a warning per table instead of failing. The new `ImportOptions.RowCountDrift` restores the old strictness with `RowCountDrift.Fail`, which rejects the package up front rather than partway through. The separate check that a bulk copy landed every row it read is unchanged and still cannot be switched off.
