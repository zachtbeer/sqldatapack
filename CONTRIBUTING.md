# Contributing

Thanks for helping improve SqlDataPack.

## Reporting Bugs / Requesting Features

Use [GitHub Issues](https://github.com/zachtbeer/sqldatapack/issues). For bugs, include:

- What you expected to happen.
- What actually happened.
- SQL Server version and .NET version.
- A minimal reproduction if possible.

## Development Setup

- Install the .NET SDKs needed by the solution.
- Install Docker for integration tests.
- Restore and build with:

```bash
dotnet restore SqlDataPack.slnx
dotnet build SqlDataPack.slnx
```

## Dependencies

Package versions are managed centrally in `Directory.Packages.props`. Add or change a version there, and keep `PackageReference` items version-less:

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Some.Package" Version="1.2.3" />

<!-- YourProject.csproj -->
<PackageReference Include="Some.Package" />
```

Restore uses lock files with `--locked-mode` in CI, so after any version change run `dotnet restore SqlDataPack.slnx --force-evaluate` and commit the updated `packages.lock.json` files alongside your change.

## Versioning

The version is not written down anywhere. No project file carries a `<Version>`, so a local build produces `1.0.0`, which is a placeholder rather than a real version. That is expected and nothing depends on it.

The real version is decided when your pull request merges. Every pull request that changes the package carries exactly one label saying how big the change is:

| Label | Effect |
| --- | --- |
| `semver:major` | `1.4.2` becomes `2.0.0` |
| `semver:minor` | `1.4.2` becomes `1.5.0` |
| `semver:patch` | `1.4.2` becomes `1.4.3` |
| `semver:none` | Nothing is published |

CI fails your pull request if it changes the package without one of these, or carries one without changing the package. A change counts as a package change when it touches `src/SqlDataPack/`, `Directory.Build.props` or `Directory.Packages.props`.

Use `semver:none` for a change nobody consuming the package could observe: a comment, a private refactor, a test-only edit that happens to live under `src/`.

Merging then computes the version, tags it, and publishes to nuget.org. There is no preview feed: every package change ships a real version, and every other change leaves the package identical. See [docs/RELEASE.md](docs/RELEASE.md).

## API Compatibility

`PublicApiContractTests` pins the intended public API shape. There is currently no automated comparison against the last published package: Package Validation is off until `1.0.0` is published, tracked in issue #10. Until then, whether a change breaks compatibility is decided by review.

## Releasing

Maintainers only. The tag is the version, and pushing it publishes:

```bash
dotnet run build/VersionGuard.cs -- 1.0.0   # tells you what is missing
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

`build/VersionGuard.cs` runs the same checks as the Release workflow, so a version that passes locally will publish. Full checklist in [docs/RELEASE.md](docs/RELEASE.md).

## Tests

Run the full suite with:

```bash
dotnet test SqlDataPack.slnx
```

Integration tests use Testcontainers to start SQL Server. If Docker is unavailable, run the unit test project directly:

```bash
dotnet test tests/SqlDataPack.Tests/SqlDataPack.Tests.csproj
```

Every test project targets both `net8.0` and `net10.0`, matching the library's shipping frameworks, so each suite runs twice. Pass `-f net10.0` to narrow it while iterating, but make sure both frameworks pass before opening a pull request.

## Benchmarks

`tests/SqlDataPack.Benchmarks` measures the export write loop and per-cell type conversion with [BenchmarkDotNet](https://benchmarkdotnet.org/). No SQL Server or Docker required.

```bash
dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*'
```

Run it before and after changes to `SqlitePackageWriter`, `ValueConverter`, `SqliteCoercingDataReader`, or the SQLite PRAGMA setup, and include before/after numbers in the pull request. Benchmarks do not run in CI — shared runners are too noisy. See the project [README](tests/SqlDataPack.Benchmarks/README.md) for how to read the results.

## Fuzz / Property Tests

`tests/SqlDataPack.Fuzzing` uses [FsCheck](https://fscheck.github.io/FsCheck/) to throw randomized input at the untrusted-input surface — the package reader, value converter, and identifier/pattern matching. These are the paths that handle packages received from elsewhere, so they must fail as a documented `SqlDataPackException` rather than crash, hang, or leak a raw framework exception. No Docker or SQL Server is required.

```bash
dotnet test tests/SqlDataPack.Fuzzing/SqlDataPack.Fuzzing.csproj
```

Each property runs 200 iterations by default (the `[FuzzProperty]` attribute). Fuzz harder locally by raising the iteration count:

```bash
FUZZ_MAXTEST=3000 dotnet test tests/SqlDataPack.Fuzzing/SqlDataPack.Fuzzing.csproj
```

When a property fails, FsCheck prints a shrunk counterexample. To replay a specific run exactly, set the seed on the attribute, for example `[Property(Replay = "1234,5678")]`.

These run nightly in CI via `.github/workflows/fuzz.yml`, not on every PR. The suite also keeps the project's [OpenSSF Scorecard](https://securityscorecards.dev/) **Fuzzing** check satisfied — Scorecard recognizes FsCheck as a property-based fuzzing framework.

## API Docs

Generate API reference metadata with:

```bash
dotnet tool restore
dotnet tool run docfx metadata docfx.json
```

## Pull Requests

- Keep public API changes deliberate and covered by `PublicApiContractTests`.
- Add integration coverage for SQL Server behavior changes.
- Describe what you changed and why.
- Make sure CI passes before requesting review.
- Update `README.md` for user-visible changes.
- Do not commit generated packages from `bin`, `obj`, or `packages`.
- Keep issues and pull requests focused on one behavior or scenario.

## Code Style

`.editorconfig` at the repository root defines the style. Rider reads it directly, and the formatter — JetBrains `jb cleanupcode`, wrapped in `build/cleanup.sh` — reads the same file, so there is nothing to configure per editor.

Set the hook up once per clone and formatting happens on commit:

```bash
dotnet tool restore
git config core.hooksPath .githooks
```

The `pre-commit` hook reformats the staged C# files and restages them. Commits that touch no C# skip the tool entirely; commits that do touch C# take roughly 20 seconds, because `jb` loads the whole solution however few files are staged. `SKIP_CLEANUP=1 git commit` bypasses it. If that cost annoys you in practice, moving the hook to `pre-push` is a reasonable local change — CI checks the same thing either way.

To reformat by hand:

```bash
build/cleanup.sh                    # whole solution
build/cleanup.sh src/Foo.cs         # only these files
```

CI runs `build/cleanup.sh` and then `git diff --exit-code`, so a clean local run is a green gate.

Rules at `warning` severity — using placement and ordering, file-scoped namespaces, braces, naming — are fixed for you. Rules at `suggestion` are editor guidance, not gates. Braces are K&R (`void Foo() {`), with `else`, `catch` and `finally` starting their own line.

Two things no single formatter rule expresses, so they are also review conventions:

- Function parameters, constructor arguments, and record definitions should stay on one line when practical.
- Deliberate one-liners such as `try { SqliteConnection.ClearPool(sqlite); } catch { /* best effort */ }` stay as they are.

The `resharper_csharp_keep_existing_*` block in `.editorconfig` is what makes cleanup honour both. Do not remove it, and do not add `resharper_substitution_for_cleanup_profile` — that key silently turns cleanup into a no-op while still reporting success.

Keep things simple. This is a focused library, not a framework.
