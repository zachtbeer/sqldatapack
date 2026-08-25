# GitHub Actions Guidelines

Scope: everything under `.github/`. The repository-wide rules in the root [AGENTS.md](../AGENTS.md) still apply.

## Workflows

| File | Trigger | What it does |
| --- | --- | --- |
| `ci.yml` | push/PR to `main` | Formatting gate, build, test matrix. |
| `release.yml` | push of a `v*` tag, manual | Validates the version, builds the six standalone binaries, tests, packs, attests, pushes to NuGet.org, creates a GitHub release, submits the winget manifest. |
| `codeql.yml` | push to `main`, weekly | CodeQL analysis for C#. |
| `docs.yml` | push/PR to `main` | Builds the docs site; deploys it on `main`. |
| `fuzz.yml` | nightly, manual | Property-based fuzzing of the untrusted-input surface. |
| `scorecard.yml` | weekly, branch protection changes | OpenSSF Scorecard. |

`release.yml` is the only file that can reach `dotnet nuget push`, and that is a constraint to preserve, not an accident. nuget.org trusted publishing authenticates a workflow file, so one publishing file means one policy. A second publishing workflow, or reaching this one through `workflow_call`, means another policy and another thing to keep pointed at the right file. The repository previously had three and every one of them was misconfigured at some point. Add a trigger to `release.yml` rather than a workflow beside it.

A merge to `main` publishes nothing.

## The version comes from the tag

Nothing passes a version into an ordinary build, and no csproj holds a `<Version>`. Local builds produce `1.0.0`, which is a placeholder and not a real version.

At release time the version is read off the tag and passed into both `dotnet build` and `dotnet pack`. It has to reach the build as well as the pack: packing with `--no-build` and a version the build never saw produces a package whose assembly and nupkg disagree.

`build/VersionGuard.cs` runs before the tag is pushed and again in CI. It checks the SemVer form, that the version sorts strictly above everything published for both package ids, and that the tag is on `HEAD`. Do not reason about prerelease ordering by hand; let the guard decide.

```bash
dotnet run build/VersionGuard.cs -- 1.1.0
git tag -a v1.1.0 -m "v1.1.0"
git push origin v1.1.0
```

A preview is the same, with a prerelease version. `release.yml` derives prerelease-ness from the string, marks the GitHub release accordingly, and skips winget.

Everything that can fail runs before the first `dotnet nuget push`. Keep it that way when adding steps: a failed step after a push leaves a version on nuget.org that cannot be taken back.

To rehearse without publishing, run `release.yml` manually with `dry_run` set. It builds, packs, validates and prints the release notes, and skips every publish step. It cannot tell you whether nuget.org will accept the credential, because it stops before the login.

Full history matters in `release.yml`, which reads tags. It checks out with `fetch-depth: 0` for that reason. No other job needs it.

## API compatibility

Nothing currently checks the public API. Package Validation is off: the baseline half needs a published `SqlDataPack` package to compare against and there is not one yet, and the framework half was switched off with it. Both are tracked to be turned on after `1.0.0`, in issues #10 and #11.

`PublicApiContractTests` still pins the intended public shape, and is the only thing standing between a change and an unnoticed API break until those land.

## Conventions

Third-party actions are pinned to a commit SHA with the version in a trailing comment. Keep that form when adding or updating one; Scorecard's Pinned-Dependencies check depends on it.
