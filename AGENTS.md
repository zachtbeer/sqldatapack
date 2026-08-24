# Repository Guidelines

For agentic coding tools working in this repository. Prefer repo-grounded changes over generic .NET advice. Nothing is published yet; the first release is `1.0.0-preview.1`. The public API and SQLite package format are frozen for 1.0, so treat a change to either as a breaking decision that needs maintainer sign-off. Contributor-level detail on setup, dependencies, and style lives in [CONTRIBUTING.md](CONTRIBUTING.md).

## Where things live

Export runs `SqlDataPackExporter.cs` → `Internal/SqlServerSchemaReader.cs` (metadata discovery, export plan, table/column filtering, WHERE handling) → `Internal/BatchPlanner.cs` → `Internal/SqlitePackageWriter.cs` (the row-streaming loop, split out of the exporter so benchmarks can drive it through a `DbDataReader` seam) → `Internal/SqlitePackage.cs`.

Import runs `SqlDataPackImporter.cs` → `SqlitePackage.cs` → `SqlServerSchemaReader.ValidateImportTargetAsync` → `Internal/SqliteCoercingDataReader.cs` → `Internal/ImportPlanner.cs` (dependency ordering).

- `Options.cs` — public options, defaults, enums. Default changes are product decisions.
- `SqlDataPackOperationalModels.cs` — public result, manifest, progress, and exception types.
- `SqlDataPackReader.cs` — the supported read-only view of a package. Don't point consumers at the internal `zsdp_*` tables.
- `Internal/SqlitePackage.cs` — package schema, manifest, validation, import order, warnings, dacpac payload. Metadata tables are named `zsdp_*`; that prefix and `sqlite_` are both reserved against generated data table names by `SqlDataPackIdentifier.ValidateSqliteDataTableNamesNotReserved`.

### Package format version

`SqlDataPackVersion.PackageFormatVersion` and `SqlDataPackVersion.MinimumSupportedPackageFormatVersion` (bottom of `Internal/SqlitePackage.cs`) must **never** be changed without explicit confirmation from a human contributor. Ask; do not infer it from the change you are making. Bumping the write version silently makes every package unreadable by released builds, and raising the minimum drops the ability to read older ones — neither is recoverable from the package itself, and neither is a judgement call an agent should make on its own. Note that the versions do not gate metadata *table names*: a renamed metadata table fails the required-table check before any version check runs, so it cannot produce a useful error.
- `Internal/DacpacSchemaManager.cs` — DacFx extract/deploy and schema-scope safety. When changing deployment, check that a selected-table schema package can't enable object drops against unrelated target objects. Covered by `tests/SqlDataPack.IntegrationTests/Tests/DacpacScopeAndDeployTests.cs`.
- `Internal/ValueConverter.cs` + `ColumnKind.cs` — type conversion. `ColumnKind` is the per-cell dispatch key: adding a SQL Server type means adding it to `KindsByTypeName` *and* to every switch over the enum.
- `Internal/SqlDataPackIdentifier.cs` — SQL identifier quoting.

## Public API changes

Update `Options.cs`, `SqlDataPackOperationalModels.cs`, XML docs, `tests/SqlDataPack.Tests/PublicApiContractTests.cs`, README examples, and `website/docs/options.md`. The package ID and namespace are spelled `SqlDataPack`.

Public records in `SqlDataPack.Models` are deliberately not positional: explicit constructor, `{ get; init; }` properties, no generated `Deconstruct`. A `Deconstruct` freezes a member list that drifts the moment a manifest gains a field, and growing the positional list is a constructor break. Add new members as `init` properties. `PublicApiContractTests.PublicModelRecords_DoNotExposeDeconstruct` enforces it.

## Changelog

`CHANGELOG.md` is a pointer, not a log. Don't add entries to it. Per-version notes are generated from pull requests at release time, so the pull request title and body are what readers eventually see: write those for a reader, not for you.

The narrative changelog is `website/docs/changelog.md` and can be edited freely as part of ordinary work. It is not tied to a version and gates nothing, so a change there ships when the docs site next deploys. `docs/RELEASE.md` covers how a release happens.

## Commands

```bash
# fast unit + API shape
dotnet test tests/SqlDataPack.Tests/SqlDataPack.Tests.csproj

# FsCheck properties over the untrusted-input surface, no Docker
dotnet test tests/SqlDataPack.Fuzzing/SqlDataPack.Fuzzing.csproj

# needs Docker; CI runs the wider SQL Server version matrix
SQLDATAPACK_SQLSERVER_IMAGE=mcr.microsoft.com/mssql/server:2025-latest \
  dotnet test tests/SqlDataPack.IntegrationTests/SqlDataPack.IntegrationTests.csproj

dotnet test SqlDataPack.slnx                                    # everything

# reformat with jb cleanupcode; CI runs the same script and then `git diff --exit-code`
dotnet tool restore && build/cleanup.sh          # whole solution, ~30s
build/cleanup.sh path/to/Changed.cs              # just these files, ~20s

# after public API or XML doc changes
dotnet tool restore && dotnet tool run docfx metadata docfx.json
```

- The three test projects each target `net8.0` and `net10.0`, so a plain `dotnet test` runs every suite twice. `-f net10.0` is fine while iterating, but don't call a change good until net8.0 has run: it's the one nobody exercises by habit.
- Don't reach for `dotnet format`; the repo no longer uses it. `build/cleanup.sh` is the formatter, and it has to generate a throwaway `.sln` because `jb cleanupcode` reads a `.slnx` as zero files and still exits 0.
- After a package version change in `Directory.Packages.props`, run `dotnet restore SqlDataPack.slnx --force-evaluate` and commit the refreshed `packages.lock.json` files. CI restores with `--locked-mode` and fails otherwise.

## Benchmarks

`tests/SqlDataPack.Benchmarks` (BenchmarkDotNet, no Docker) covers the two hot paths: the export write loop in `SqlitePackageWriter` and per-cell conversion in `ValueConverter`. Run it before and after touching those, `SqliteCoercingDataReader`, or `SqlitePackage.ConfigureForBulkWriteAsync`:

```bash
dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*'
```

Two things before you act on a number. Results are noisy on a loaded machine: a change here once looked like a 39% regression across two runs and turned out to be noise on the third, so re-run, and be suspicious of a "regression" whose allocation is unchanged. And `ValueConverter` has both a convenience overload and one taking a pre-resolved `ColumnKind`; the hot paths resolve the kind once per column, so benchmark the latter or you're measuring an API production never calls. See the project [README](tests/SqlDataPack.Benchmarks/README.md).

## Cloud/web sessions (Claude Code on the web)

Remote containers start with no .NET SDK. `.claude/hooks/session-start.sh` (registered in `.claude/settings.json`) installs the .NET 8 and 10 SDKs into `~/.dotnet`, restores with `--locked-mode`, runs `dotnet tool restore`, and exports `DOTNET_ROOT`, `PATH`, and `SQLDATAPACK_SQLSERVER_IMAGE`. It's idempotent and a no-op outside `CLAUDE_CODE_REMOTE`.

These containers have the Docker CLI but no daemon, so the integration tests can't run there. Verify with `SqlDataPack.Tests` and rely on GitHub Actions for the SQL Server matrix.

## Code style

`.editorconfig` is the single source of truth: Rider, `jb cleanupcode` and the analyzers all read it, and CI enforces it by reformatting and diffing. Braces are K&R (`void Foo() {`) with `else`/`catch`/`finally` on their own line. `warning`-severity rules (usings, file-scoped namespaces, braces, naming) are gates. `suggestion` rules (expression bodies, `var`, pattern matching) are editor guidance and deliberately not enforced, so hand-tuned layout survives.

Two conventions no rule expresses on its own: keep function parameters, constructor arguments, and record definitions on one line when practical, and leave deliberate one-liners such as `try { SqliteConnection.ClearPool(sqlite); } catch { /* best effort */ }` intact. The `resharper_csharp_keep_existing_*` block in `.editorconfig` is what makes cleanup respect both — without it, cleanup collapses and re-wraps hand-tuned layout.

Never set `resharper_substitution_for_cleanup_profile` in `.editorconfig`. It silently disables cleanup: `jb` reports success, reports the profile it is using, and changes nothing at all.

Name tests by behavior, e.g. `Export_ExistingPackageWithoutOverwrite_FailsWithoutReplacingPackage`. Add integration coverage for SQL Server, DacFx, type fidelity, schema deployment, and round-trip changes.

Read [.github/AGENTS.md](.github/AGENTS.md) before editing anything under `.github/`.
