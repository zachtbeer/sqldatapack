# Changelog

Per-version release notes are on the [releases page](https://github.com/zachtbeer/sqldatapack/releases), generated from the pull requests in each release.

The readable account of what changed and why is on the documentation site, from [website/docs/changelog.md](website/docs/changelog.md). It is not tied to a version and can be updated at any time.

The entries below are kept for the record. They describe versions published under the previous package id `Zachtbeer.SqlDataBridge`, which is retired. Nothing named `SqlDataPack` had been published when they were written.

## [1.0.0-rc.12]

Release-candidate hardening ahead of `1.0.0`. No SQLite package format change: packages written by `1.0.0-rc.10` import unchanged.

### Added

- `GlobalWhereClause` now accepts a set of columns. A clause applies to a selected table only when that table has **every** column it names, which makes tenant and soft-delete filtering expressible in one predicate:

  ```csharp
  new GlobalWhereClause(["TenantId", "IsDeleted"], "TenantId = 123 AND IsDeleted = 0")
  ```

  Tables missing any named column are exported unfiltered. This differs from listing several single-column clauses, which apply independently wherever each column happens to exist. The single-column constructor is unchanged.
- `tests/SqlDataBridge.Benchmarks`, a BenchmarkDotNet harness covering the export write loop and per-cell type conversion. Requires no SQL Server or Docker. Not run in CI: see its [README](tests/SqlDataBridge.Benchmarks/README.md).
- A root `.editorconfig` defining the project's style, enforced by a new CI formatting job (`dotnet format whitespace`/`style --verify-no-changes`), plus a `.gitattributes` normalizing line endings.

### Changed

- **Export is 4–10× faster.** The package connection is now configured for the write-once bulk load it performs (`journal_mode = MEMORY`, `synchronous = OFF`, `temp_store = MEMORY`, 8 KiB pages, larger cache), and the per-row SQLite write no longer allocates an async state machine per row. Relaxing durability is safe because an export already writes to a temporary file and only moves it into place on success, deleting it on failure; a half-written package was never observable. The produced package is an ordinary SQLite file; none of these settings are visible to consumers.

  Measured with `ExportWriteBenchmarks`, 10 000 rows per table shape:

  | Table shape | Before | After |
  | --- | ---: | ---: |
  | Narrow integer columns | 117.9 ms | 12.1 ms |
  | Wide text columns | 381.1 ms | 58.6 ms |
  | Blob-heavy rows | 441.1 ms | 107.6 ms |
  | Mixed types | 135.2 ms | 26.6 ms |

  Run-to-run variance also collapsed (standard deviation on the narrow-integer shape fell from 29 ms to under 2 ms) because commits no longer wait on an fsync.

- **Import allocates roughly 55% less per cell on integer, bit, and floating-point columns.** `ValueConverter` resolves a column's conversion behaviour through an allocation-free lookup instead of lowercasing the type name for every value, and the string form of a value is now materialized only by the branches that parse from one. Both hot paths resolve the behaviour once per column rather than once per cell.

  Measured with `ValueConvertBenchmarks`, per 10 000 cells:

  | Conversion | Before | After | Allocated before → after |
  | --- | ---: | ---: | --- |
  | `int` from package | 111.1 µs | 62.5 µs | 550 KB → 240 KB |
  | `bigint` from package | 119.2 µs | 62.1 µs | 557 KB → 240 KB |
  | `bit` from package | 108.8 µs | 55.5 µs | 550 KB → 240 KB |
  | `float` from package | 519.9 µs | 41.4 µs | 597 KB → 240 KB |
  | text from package | 53.4 µs | 25.4 µs | 0 → 0 |
  | `int` to package | 58.2 µs | 14.6 µs | 0 → 0 |
  | text to package | 56.4 µs | 16.6 µs | 0 → 0 |

  Conversions dominated by string formatting rather than dispatch (`decimal`, `datetime2`, `uniqueidentifier`) are unchanged in both time and allocation, within run-to-run noise.

- Test projects target `net8.0` and `net10.0`, matching the library. The `net8.0` build was previously compile-verified but never executed by any test. The CI integration matrix gained a framework dimension, running each SQL Server version against both.
- The export row-writing loop moved from a private method on `SqlDataBridgeExporter` into an internal `SqlitePackageWriter`, so benchmarks exercise the shipping code rather than a copy of it. No public API change.

### Fixed

- Corrected indentation in `SqlDataBridgeImporter.PreflightAsync`, where a block sat one level shallower than its enclosing `try`.

## [1.0.0-rc.10]

First public release candidate. The public API and SQLite package format are frozen for 1.0.

See the [GitHub releases](https://github.com/zachtbeer/sqldatapack/releases) for the full history prior to this changelog.

[1.0.0-rc.12]: https://github.com/zachtbeer/sqldatapack/compare/v1.0.0-rc.10...v1.0.0-rc.12
[1.0.0-rc.10]: https://github.com/zachtbeer/sqldatapack/compare/v0.0.4...v1.0.0-rc.10
