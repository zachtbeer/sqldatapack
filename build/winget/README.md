# winget manifests

`winget install zachtbeer.SqlDataPack` is the channel most people use, so this is worth getting right.

These three files are the manifest for the [Windows Package Manager Community Repository](https://github.com/microsoft/winget-pkgs). They are templates: `VERSION`, `RELEASE_DATE`, `SHA256_X64` and `SHA256_ARM64` are placeholders.

## The first submission is manual

microsoft/winget-pkgs reviews a new package identifier by hand before it will take automated updates, so the first release has to be submitted as a pull request. After that `release.yml` does it.

**Do not set `WINGET_TOKEN` or `WINGET_PACKAGE_LIVE` until step 6.** `wingetcreate update` can only update an identifier winget-pkgs already knows about, so turning the automated job on before the manual pull request merges just fails the release at its last job.

1. Publish the release. Take the two hashes from the `SHA256SUMS` asset.
2. Copy these three files to `manifests/z/zachtbeer/SqlDataPack/<version>/` in a fork of `microsoft/winget-pkgs`, keeping the file names.
3. Replace the placeholders. `RELEASE_DATE` is `YYYY-MM-DD`.
4. Validate and test locally:

   ```powershell
   winget validate --manifest <folder>
   winget settings --enable LocalManifestFiles
   winget install --manifest <folder>
   ```

5. Open the pull request. Review usually takes a few days.
6. Once it merges, set both of these on the repository:
   - secret `WINGET_TOKEN`: a PAT with `public_repo` scope on an account that has forked `microsoft/winget-pkgs`
   - variable `WINGET_PACKAGE_LIVE`: `true`

## After that

`release.yml` runs `wingetcreate update` on every stable release.

The two settings do different jobs. `WINGET_PACKAGE_LIVE` says the identifier exists in winget-pkgs; while it is unset the job skips with a warning, which is the bootstrap state above. Once it is set, a missing `WINGET_TOKEN` fails the job instead of skipping: winget is the primary channel, and a silently skipped submission leaves it on an old version until somebody notices. A PAT expiring is the realistic way that happens. Failing there is safe, everything else in the release has already published.

## Why portable

The published build is a single self-contained file, so `InstallerType: portable` can point straight at the `.exe`. No archive, no `NestedInstallerFiles`. winget puts the file under `%LOCALAPPDATA%\Microsoft\WinGet\Packages` and symlinks `sqldatapack` onto `PATH`. `--scope machine` installs under `%PROGRAMFILES%` instead, which is what you want on a shared server.
