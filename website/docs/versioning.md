---
title: Versioning
sidebar_label: Versioning
---

SqlDataPack has not been published yet. The first release is `1.0.0-preview.1`. The public API and the SQLite package format are frozen for 1.0: barring a critical fix, they will not change between the previews and the stable `1.0.0` release. Pin a specific version and review the release notes before upgrading. Once `1.0.0` ships, the NuGet package follows Semantic Versioning, and the policies below describe how that works.

## Public API

Public types and members in the `SqlDataPack` namespace are the intended supported API surface. The `1.0.0-preview.1` surface is considered final for 1.0. From 1.0.0 onward, breaking API changes require a new major version.

Internal types, metadata table implementation details, and test harness APIs are not public API.

## Package Format

SQLite packages include a package format version. Export writes one version; import accepts a range. Every build knows the newest format it writes and the oldest format it can read, and checks the package against that range before copying data.

Packages produced by the `1.0.0` previews are expected to import unchanged on `1.0.0`. From the first stable release onward:

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

There is no nightly or per-commit feed. Every change to the package ships a real version, and every other change leaves the package identical.

### Previews

Releases before `1.0.0` are numbered `1.0.0-preview.1`, `1.0.0-preview.2`, and so on. The number is a count of previews, chosen by hand when each one is published; it is not a build counter and it does not skip.

The dot before the number matters. SemVer compares a numeric identifier as a number, so `preview.10` sorts above `preview.2`. Written without the dot, `preview10` is one alphanumeric identifier compared character by character, and would sort below `preview2` on nuget.org, where nothing can be deleted. `VersionGuard` checks the ordering before any tag is pushed.
