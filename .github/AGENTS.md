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

Only `release.yml` publishes to NuGet.org. There is no preview feed: every package change ships a real version, and every change that is not a package change leaves the package identical.

## The version comes from a label on the pull request

Nothing passes a version into an ordinary build, and no csproj holds a `<Version>`. Local builds produce `1.0.0`, which is a placeholder and not a real version.

At release time `publish.yml` computes the version from the last stable tag and the highest `semver:` label since it, using `build/NextVersion.cs`, and passes it into both `dotnet build` and `dotnet pack`. It has to reach the build as well as the pack: packing with `--no-build` and a version the build never saw produces a package whose assembly and nupkg disagree.

`build/NextVersion.cs --self-test` runs in CI. `build/VersionGuard.cs` runs before every tag is pushed and checks the SemVer form, that the version sorts strictly above everything on nuget.org, and that the tag is on `HEAD`.

A release candidate is still tagged by hand, which triggers `release.yml` directly:

```bash
dotnet run build/VersionGuard.cs -- 1.1.0-rc.1
git tag -a v1.1.0-rc.1 -m "v1.1.0-rc.1"
git push origin v1.1.0-rc.1
```

`publish.yml` ignores prerelease tags when looking for the last release, so a hand-pushed release candidate never becomes the base for the next automatic bump.

To rehearse without publishing, run `release.yml` manually with `dry_run` set. It builds, packs, validates and prints the release notes, and skips every publish step.

Full history matters in `publish.yml` and `release.yml`, which read tags. Both check out with `fetch-depth: 0` for that reason. No other job needs it.

## Why the version numbers jump from rc.2 to rc.13

Kept because it explains the gap, and because it is the reason the checks above exist.

A preview workflow, since deleted, used to build its version as `${base_version}-${preview_label}.${{ github.run_number }}`. `github.run_number` is the workflow's own lifetime counter: it increments on every run including failed and cancelled ones, and never resets when `base_version` changes. The label number was a build count for the workflow, not a count of previews.

What that produced:

| Intended | Published | Cause |
| --- | --- | --- |
| `1.0.0-rc.1` | `1.0.0-rc.10` | Preview run #10, commit `d98e766` |
| none | `1.0.0-rc.11` | Preview run #11, same commit `d98e766` |
| `1.0.0-rc.2` | `1.0.0-rc.12` | Preview run #12, commit `5378b23` |

The missing `0.0.4-preview.5` is the same mechanism: run #5 was cancelled.

Because SemVer compares numeric prerelease identifiers numerically, `1.0.0-rc.2` sorts *below* `1.0.0-rc.10`. Publishing an intuitively-next `1.0.0-rc.2` would have landed under what was already on nuget.org, and `--prerelease` restore would have kept resolving to the old one. [NuGet supports no permanent deletion](https://learn.microsoft.com/en-us/nuget/nuget-org/policies/deleting-packages), and unlisted versions can still be selected by floating ranges, so none of this was reversible. The next release after `1.0.0-rc.12` therefore has to be `1.0.0-rc.13` or higher.

Those three versions belong to the retired package id `Zachtbeer.SqlDataBridge`. Nothing named `SqlDataPack` has ever been published, so the ordering damage above constrains this package not at all: its first release starts from `0.0.0` and the `semver:` label on the merged pull request decides the number. The history is kept because it is the reason `VersionGuard` checks ordering at all.

`VersionGuard` now checks all of this mechanically. Do not reason about prerelease ordering by hand.

## API compatibility

Nothing currently checks the public API. Package Validation is off: the baseline half needs a published `SqlDataPack` package to compare against and there is not one yet, and the framework half was switched off with it. Both are tracked to be turned on after `1.0.0`, in issues #10 and #11.

`PublicApiContractTests` still pins the intended public shape, and is the only thing standing between a change and an unnoticed API break until those land.

## Conventions

Third-party actions are pinned to a commit SHA with the version in a trailing comment. Keep that form when adding or updating one; Scorecard's Pinned-Dependencies check depends on it.
