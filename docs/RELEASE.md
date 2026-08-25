# Cutting a Release

Push a tag. That is the whole process.

```
dotnet run build/VersionGuard.cs -- 1.1.0
git tag -a v1.1.0 -m "v1.1.0"
git push origin v1.1.0
```

`VersionGuard` prints those last two lines for you when its checks pass, so in practice you run the first command and paste what it gives you.

Nothing else publishes. `release.yml` is the only workflow in the repository that can reach `dotnet nuget push`, and a merge to `main` never releases anything.

## What the tag starts

`release.yml` runs one guard job, then six standalone builds, then everything else:

1. **Guard.** `build/VersionGuard.cs` again, in CI this time, including the check that the tag is on `HEAD`.
2. **Binaries.** `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, each self-contained and asserted to be exactly one file, each smoke-tested where the runner can execute what it built.
3. **Publish.** Build, unit tests, pack both packages, verify the packed versions match the tag, checksums, provenance attestation, SBOMs, release notes, then push to nuget.org and create the GitHub release with the binaries, `SHA256SUMS` and SBOMs attached.

The order matters: everything that can fail runs before the first `dotnet nuget push`. A nuget version cannot be taken back, so nothing reaches it until the rest has already worked.

## Previews

Same three commands, with a prerelease version:

```
dotnet run build/VersionGuard.cs -- 1.1.0-preview.1
git tag -a v1.1.0-preview.1 -m "v1.1.0-preview.1"
git push origin v1.1.0-preview.1
```

There is no separate preview workflow. `release.yml` reads the prerelease part off the version string, marks the GitHub release as a prerelease so it does not show as "Latest", and skips the winget submission.

A preview of the CLI installs with `dotnet tool install -g SqlDataPack.Cli --prerelease`. Previews are not published to winget.

## Rehearsing

**Actions → Release → Run workflow** with `dry_run` ticked. It builds, tests, packs and validates against an existing tag and publishes nothing.

`dry_run` stops before the nuget push, so it proves the build and the packaging but not the push itself. It cannot tell you whether nuget.org will accept a credential.

## Picking the number

You pick it. The library and the CLI release in lockstep off one tag and one version, so there is no way to ship a CLI fix without also publishing the library at that version. That is deliberate. A CLI-only change still cuts a library version that contains nothing new, which is cheaper than two version lines that drift apart.

`VersionGuard` will not let you publish a version that sorts at or below something already on nuget.org, and it checks both `SqlDataPack` and `SqlDataPack.Cli`. Do not reason about prerelease ordering by hand; let the guard decide.

## The release notes

Generated from the pull requests since the previous tag, so your pull request title is what readers see. Write it for a reader.

The readable account of the project is `website/docs/changelog.md`, which deploys with the docs site and is not tied to a version. Edit it whenever you like. `CHANGELOG.md` at the repository root points at both and holds nothing else.

## Trusted publishing

nuget.org authenticates the workflow, not a stored key, so there is nothing to rotate. It needs exactly one policy:

| Field | Value |
| --- | --- |
| Repository Owner | `zachtbeer` |
| Repository | `sqldatapack` |
| Workflow File | `release.yml` |
| Environment | empty |
| Scopes | publish new packages and publish new versions, glob `SqlDataPack*` |

One policy because one file publishes. If you ever add a second workflow that pushes to nuget.org, or call this one with `workflow_call`, that is a second policy and a second thing to keep pointed at the right file. The repository had three of those and every one of them was misconfigured at some point. Add a trigger to `release.yml` instead.

The glob is `SqlDataPack*`, not `SqlDataPack.*`: nuget treats the period literally, so the dotted form matches `SqlDataPack.Cli` but misses the bare `SqlDataPack`.

## When it goes wrong

**Failed before any nuget push.** The common case: build, tests, binaries, SBOM, attestation, notes. Nothing is burned. Delete the tag, fix, re-tag the same version:

```
git push origin :refs/tags/v1.1.0
git tag -d v1.1.0
```

**Failed after a nuget push.** That version is gone on whichever package ids made it through. Do not delete the tag. Re-run the failed jobs against the same tag:

```
gh run rerun <run-id> --failed
```

`--skip-duplicate` makes the already-published pushes no-ops, the failed push retries, and the GitHub release gets created. Use `gh run rerun` rather than a fresh manual run: a manual run resolves the version by describing `HEAD` on a branch, which only works if that branch's `HEAD` happens to be the tagged commit.

**One package published and the other did not.** The pushes are separate steps with the CLI first, so this means the CLI landed and the library did not. Re-run as above. `VersionGuard` checks both ids, so it will refuse to reuse that version on a later tag.

**The packed version does not match the tag.** `release.yml` asserts this and stops before publishing. The version is passed into both `dotnet build` and `dotnet pack`, so a mismatch means one of those lost its `-p:Version=`.

**A published version is wrong.** nuget.org supports no permanent deletion. Unlist it, then publish the version you meant next. You cannot reuse or undercut the number.
