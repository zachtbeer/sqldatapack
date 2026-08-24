---
title: Versioning
sidebar_label: Versioning
---

SqlDataPack is at `1.0.0-rc`. The public API and the SQLite package format are frozen for 1.0: barring a critical fix, they will not change between the release candidate and the stable `1.0.0` release. Pin a specific version and review the release notes before upgrading. Once `1.0.0` ships, the NuGet package follows Semantic Versioning, and the policies below describe how that works.

## Public API

Public types and members in the `SqlDataPack` namespace are the intended supported API surface. The `1.0.0-rc` surface is considered final for 1.0. From 1.0.0 onward, breaking API changes require a new major version.

One deliberate exception was taken during the release-candidate window. In `1.0.0-rc.12`, `GlobalWhereClause` changed from a positional record over a single `ColumnName` to a type carrying `ColumnNames`, so a predicate can be gated on a set of columns. The `new GlobalWhereClause(columnName, whereClause)` constructor still works and still behaves identically; code that read the `ColumnName` property or deconstructed the record needs updating. This is the last planned public API change before `1.0.0`.

Internal types, metadata table implementation details, and test harness APIs are not public API.

## Package Format

SQLite packages include a package format version. Export writes one version; import accepts a range. Every build knows the newest format it writes and the oldest format it can read, and checks the package against that range before copying data.

Packages produced by `1.0.0-rc` are expected to import unchanged on `1.0.0`. From the first stable release onward:

- Patch and minor releases within a major version read packages produced by any earlier release of that major version. The oldest readable format only moves in a new major version.
- A package written by a newer release fails with a `SqlDataPackException` that tells you to upgrade SqlDataPack.
- A package older than the oldest readable format fails with a `SqlDataPackException` that tells you to re-export it, or to import it with a version that still reads it.
- Package metadata may gain additive fields in minor releases when existing import behavior remains compatible.
- Breaking package format changes require a new major version.

## Target Frameworks

The package currently targets:

- `net8.0`
- `net10.0`

Framework support may be expanded in minor releases when it does not break existing users. Dropping a supported target framework requires a new major version unless the framework itself is out of support and continued support is impractical.

## Dependencies

Dependency updates may happen in patch or minor releases when they preserve the public API and expected behavior. Dependency changes that force application code changes are treated as breaking changes.

## Release Versions

Every released version corresponds to a git tag of the same name, prefixed with `v`. The release workflow passes one version number into the build, the pack, and the tag, so what is on nuget.org, what is tagged in the repository, and what the GitHub release says are the same number by construction.

The size of each bump is chosen by a person when the change is reviewed, not derived from commit messages. A pull request that changes the package carries a label saying whether it is a major, minor, or patch change, or that it should not be released at all, and merging it publishes accordingly.

There is no preview feed. Every change to the package ships a real version, and every other change leaves the package identical.

### The earlier package id

This project was previously published as `Zachtbeer.SqlDataBridge`, up to `1.0.0-rc.12`. That package id is retired and receives no further releases.

Its release candidates were numbered from a build counter rather than a candidate counter, so there is no `rc.1` through `rc.9` on nuget.org, and `rc.10` and `rc.11` are the same code published twice. If you go looking for an `rc.1` there, that is why it does not exist.

None of this carries over. `SqlDataPack` starts at `1.0.0`.
