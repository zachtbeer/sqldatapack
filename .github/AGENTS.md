# GitHub Actions Guidelines

Scope: everything under `.github/`. The repository-wide rules in the root [AGENTS.md](../AGENTS.md) still apply.

## Workflows

| File | Trigger | What it does |
| --- | --- | --- |
| `ci.yml` | push/PR to `main` | Formatting gate, build, test matrix. |
| `version-label.yml` | PR to `main` | Fails a pull request that changes the package without exactly one `semver:` label, or carries a label without changing the package. |
| `publish.yml` | push to `main` | Reads the `semver:` labels since the last tag, computes and pushes the version tag, then calls `release.yml`. |
| `release.yml` | push of a `v*` tag, or called by `publish.yml` | Validates the version, builds, tests, packs, attests, pushes to NuGet.org, creates a GitHub release. |
| `codeql.yml` | push/PR to `main`, weekly | CodeQL analysis for C#. |
| `fuzz.yml` | nightly, manual | Property-based fuzzing of the untrusted-input surface. |
| `scorecard.yml` | push to `main`, weekly, branch protection changes | OpenSSF Scorecard. |

Only `release.yml` publishes to NuGet.org. There is no nightly or per-commit feed: every package change ships a real version, and every change that is not a package change leaves the package identical.

## The version comes from a label on the pull request

Nothing passes a version into an ordinary build, and no csproj holds a `<Version>`. Local builds produce `1.0.0`, which is a placeholder and not a real version.

At release time `publish.yml` computes the version from the last stable tag and the highest `semver:` label since it, using `build/NextVersion.cs`, and passes it into both `dotnet build` and `dotnet pack`. It has to reach the build as well as the pack: packing with `--no-build` and a version the build never saw produces a package whose assembly and nupkg disagree.

`build/NextVersion.cs --self-test` runs in CI. `build/VersionGuard.cs` runs before every tag is pushed and checks the SemVer form, that the version sorts strictly above everything on nuget.org, and that the tag is on `HEAD`. Do not reason about prerelease ordering by hand; let the guard decide.

A preview is still tagged by hand, which triggers `release.yml` directly:

```bash
dotnet run build/VersionGuard.cs -- 1.1.0-preview.1
git tag -a v1.1.0-preview.1 -m "v1.1.0-preview.1"
git push origin v1.1.0-preview.1
```

`publish.yml` ignores prerelease tags when looking for the last release, so a hand-pushed preview never becomes the base for the next automatic bump.

To rehearse without publishing, run `release.yml` manually with `dry_run` set. It builds, packs, validates and prints the release notes, and skips every publish step.

Full history matters in `publish.yml` and `release.yml`, which read tags. Both check out with `fetch-depth: 0` for that reason. No other job needs it.

## API compatibility

Nothing currently checks the public API. Package Validation is off: the baseline half needs a published `SqlDataPack` package to compare against and there is not one yet, and the framework half was switched off with it. Both are tracked to be turned on after `1.0.0`, in issues #10 and #11.

`PublicApiContractTests` still pins the intended public shape, and is the only thing standing between a change and an unnoticed API break until those land.

## Conventions

Third-party actions are pinned to a commit SHA with the version in a trailing comment. Keep that form when adding or updating one; Scorecard's Pinned-Dependencies check depends on it.
