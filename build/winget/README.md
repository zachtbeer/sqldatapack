# winget manifests

`winget install zachtbeer.SqlDataPack` is the channel most people use, so this is worth getting right.

These three files are the manifest for the [Windows Package Manager Community Repository](https://github.com/microsoft/winget-pkgs). They are templates: `VERSION`, `RELEASE_DATE`, `SHA256_X64` and `SHA256_ARM64` are placeholders.

## The first submission is manual

microsoft/winget-pkgs reviews a new package identifier by hand before it will take automated updates, so the first release has to be submitted as a pull request. After that `release.yml` does it.

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

## After that

`release.yml` runs `wingetcreate update` on every stable release. It needs a repository secret named `WINGET_TOKEN`: a PAT with `public_repo` scope on an account that has forked `microsoft/winget-pkgs`. Without the secret the job warns and skips rather than failing a release that already succeeded.

## Why portable

The published build is a single self-contained file, so `InstallerType: portable` can point straight at the `.exe`. No archive, no `NestedInstallerFiles`. winget puts the file under `%LOCALAPPDATA%\Microsoft\WinGet\Packages` and symlinks `sqldatapack` onto `PATH`. `--scope machine` installs under `%PROGRAMFILES%` instead, which is what you want on a shared server.
