# Cutting a Release

You do not cut a release. Merging does.

Every pull request that changes the package carries one label saying how big the change is, and merging it to `main` computes the next version, tags it, and publishes to nuget.org.

| Label | Effect |
| --- | --- |
| `semver:major` | `1.4.2` becomes `2.0.0` |
| `semver:minor` | `1.4.2` becomes `1.5.0` |
| `semver:patch` | `1.4.2` becomes `1.4.3` |
| `semver:none` | Nothing is published |

`version-label.yml` fails the pull request if it changes the package and carries no label, or carries a label and changes nothing in the package. A change counts as a package change when it touches `src/SqlDataPack/`, `src/SqlDataPack.Cli/`, `build/winget/`, `Directory.Build.props` or `Directory.Packages.props`.

The library and the CLI release in lockstep off one tag and one version number. There is no way to ship a CLI fix without also publishing the library at that version, and that is deliberate: a CLI-only change still gets a label, and the library gets a version that changed nothing. Cheaper than two version lines that drift apart.

Use `semver:none` for a change nobody consuming the package could observe: a comment, a private refactor, a test-only edit that happens to live under `src/`.

## What happens on merge

`publish.yml` finds the most recent stable tag, walks the commits since it, reads the label off each merged pull request, takes the highest bump, and computes the version with `build/NextVersion.cs`. It runs `build/VersionGuard.cs`, pushes the tag, and hands over to `release.yml`, which builds, tests, packs, attests provenance, generates an SBOM, publishes to nuget.org, and creates the GitHub release with notes generated from the pull requests.

If the highest bump is `none`, nothing is published and the run summary says so. This is the ordinary outcome for a documentation or CI merge.

## The first release

With no tag to bump from, the version is computed against `0.0.0`, so the label picks the starting number like it picks every other one:

| Label | First release |
| --- | --- |
| `semver:major` | `1.0.0` |
| `semver:minor` | `0.1.0` |
| `semver:patch` | `0.0.1` |

Going straight to `1.0.0` means labelling that pull request `semver:major`. Once the project is on a `0.x` version, `semver:major` is what moves it to `1.0.0`; there is no separate 0.x bump convention here.

## The release notes

The GitHub release notes are generated from the pull requests in the release. Your pull request title and body are what readers see, so write them for a reader.

The readable account of the project is `website/docs/changelog.md`, which deploys with the docs site and is not tied to a version. Edit it whenever you like. `CHANGELOG.md` at the repository root is a pointer to both and holds nothing else.

## Releasing a prerelease

The automatic path only ever produces `X.Y.Z`. Previews come from `prerelease.yml`, which you run by hand:

**Actions → Prerelease → Run workflow**, then type the full version, for example `1.0.0-preview.1`.

You pick the number. The workflow builds, tests and packs first, then tags, publishes to nuget.org, and creates a GitHub release marked as a prerelease. Tick `dry_run` to build and validate without tagging or publishing anything.

Three things stop a mistake:

- A version with no prerelease part is rejected. This workflow cannot publish a stable release, whatever you type.
- `VersionGuard` checks the SemVer form and that the version sorts strictly above everything on nuget.org.
- A version that is already tagged is refused, so you cannot overwrite a candidate.

The tag is created after the build passes, so a failed run leaves nothing behind to clean up.

`publish.yml` ignores prerelease tags when looking for the last release, so a preview never becomes the base for the next automatic bump. That also means the eventual stable release computes from the last *stable* tag: after `1.0.0-preview.3` with no stable tag before it, the label on the finalizing pull request has to be `semver:major` to produce `1.0.0`.

`prerelease.yml` is self-contained rather than calling `release.yml`, so nuget.org sees an OIDC token issued for `prerelease.yml`. It needs its own trusted publishing policy naming that file.

## When it goes wrong

**A merge published the wrong version number.** The label was wrong. nuget.org supports no permanent deletion, so the published version stays. Unlist it on nuget.org, then publish the version you meant next. A version that sorts below one already published cannot be used, which is what `VersionGuard` checks for.

**A merge published nothing and should have.** The pull request carried `semver:none`, or it reached `main` without a pull request, which the run summary names. Open a pull request with the right label; an empty commit is enough if there is nothing else to change.

**The publish job failed after the tag was pushed.** The tag exists and some or all of the release did not happen. `gh release create` is the last step, so if the run got as far as pushing a package there is still no GitHub release. Fix the cause, then re-run `release.yml` manually against that tag with `dry_run` cleared: `--skip-duplicate` makes an already-published push a no-op, the failed push retries, and the GitHub release is created for the first time.

That manual re-run needs a trusted publishing policy on nuget.org naming `release.yml`. nuget.org matches the top-level workflow file, not the reusable one, so the policy covering `publish.yml` does not cover a direct run of `release.yml`: without its own policy the token exchange returns 401 with "Workflow mismatch" and the recovery path does not work. Same for publishing a preview by pushing a `v*` tag by hand.

**One package published and the other did not.** The pushes are separate steps, CLI first, so this means the CLI landed and the library did not. Re-run the job as above. `VersionGuard` checks both ids, so it will refuse to reuse that version on a later tag.

**The packed version does not match the tag.** `release.yml` asserts this and stops before publishing. The version is passed into both `dotnet build` and `dotnet pack`, so a mismatch means one of those two lost its `-p:Version=`.
