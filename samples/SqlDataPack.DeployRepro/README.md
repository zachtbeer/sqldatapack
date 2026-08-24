# SqlDataPack.DeployRepro

> Diagnostic harness for Azure-extracted dacpac → on-prem SQL Server deploys. Designed to be driven
> by an LLM agent for autonomous failure-mode triage.

## What this is

A .NET 10 console app that:

1. Reads the dacpac payload out of a SqlDataPack SQLite package (`zsdp_schema_packages` row).
2. Spins up a fresh SQL Server in a Testcontainers Docker image.
3. Creates an empty target database.
4. Optionally generates the DacFx deploy script (via `DacServices.GenerateDeployScript`) and dumps
   it for inspection.
5. Invokes the **production** `SqlDataPack.Internal.DacpacSchemaManager.DeployAsync`
   method — the same one `SqlDataPackImporter` calls when `SchemaDeploymentMode.DeployDacpac` is
   active — so the harness exercises the real code path, not a parallel reimplementation.
6. Prints structured banners, the script's smoking-gun lines, and the full exception chain on
   failure.

**Source files**: `Program.cs` (CLI + harness), backed by `src/SqlDataPack/Internal/DacpacSchemaManager.cs`
(the production deploy method, including `NeutralizeForNonAzureSqlTarget`).

---

## Prerequisites (verify before invoking)

Run from the repo root (`D:\code\sqldatabridge` or equivalent).

| Requirement | Check command | Pass criterion |
|---|---|---|
| Docker daemon reachable | `docker info` | Exit 0 |
| .NET 10 SDK installed | `dotnet --list-sdks` | A line beginning with `10.` |
| Real-world fixture present | `Test-Path tests/SqlDataPack.IntegrationTests/Fixtures/realworld.db` (PowerShell) or `ls tests/SqlDataPack.IntegrationTests/Fixtures/realworld.db` (bash) | File exists. The fixture is gitignored — drop your own copy there. |

If Docker is not running, **stop**: every command in this README requires it. The repro reports
container failures clearly but the agent will still burn ~30 s discovering that fact.

---

## Self-test (run these first, in order)

| Step | Command | Expected stdout substring | Expected exit code | Budget |
|---|---|---|---|---|
| 1. CLI parses | `dotnet run --project samples/SqlDataPack.DeployRepro -- --help` | `Usage: dotnet run` | 64 | 30 s |
| 2. Full pipeline works (no deploy) | `dotnet run --project samples/SqlDataPack.DeployRepro -- --dump-script --no-deploy` | `=== [done] ===` | 0 | 60 s |

If step 2 reports `=== [done] ===` and exit 0, the harness is fully wired and you can move on to
real diagnostic work. If it fails on `=== [fixture] ===`, the fixture file isn't where the app
looks — pass `--db <path>` explicitly.

---

## Task → invocation

Copy-paste these one-liners verbatim. "Success signal" is the substring to grep for in **stdout**
to confirm the goal was met; "Failure signal" is what indicates the goal wasn't.

| Goal | Command | Success signal | Failure signal |
|---|---|---|---|
| Deploy the bundled fixture with defaults (mirrors the integration-test happy path) | `dotnet run --project samples/SqlDataPack.DeployRepro` | `Deploy succeeded in` followed by `User tables on target:` with a positive number | Exit 1 + `=== [FAILURE after` banner |
| See only the T-SQL DacFx would run (no deploy) | `dotnet run --project samples/SqlDataPack.DeployRepro -- --dump-script --no-deploy` | `Wrote deploy script` + the smoking-gun block | Stderr `GenerateDeployScript failed:` |
| Confirm whether `SET CONTAINMENT = PARTIAL` is in the deploy script | `dotnet run --project samples/SqlDataPack.DeployRepro -- --dump-script --no-deploy` then grep the smoking-gun block | `(none found` → DacFx is fine | `L<N>: ... CONTAINMENT ...` lines → DacFx is scripting the prerequisite |
| Reproduce the original failure mode (deploy without the Azure→on-prem rewrite) | `dotnet run --project samples/SqlDataPack.DeployRepro -- --adapt-azure false --dump-script` | `Msg 12824 'contained database authentication' detected in chain` | Deploy succeeded anyway → DacFx 170.x isn't scripting `SET CONTAINMENT` for this dacpac; the rewrite is over-cautious for this config |
| Try the deploy against a SQL Server 2022 image instead of 2025 | `dotnet run --project samples/SqlDataPack.DeployRepro -- --image mcr.microsoft.com/mssql/server:2022-latest` | `Deploy succeeded in` | Exit 1 + check `=== [server] ===` for `ProductVersion` and adjust expectations |
| Compare: does enabling contained DB auth on the container change the outcome? | `dotnet run --project samples/SqlDataPack.DeployRepro -- --enable-contained-auth --adapt-azure false` | If this succeeds but `--adapt-azure false` alone fails, the only blocker was Msg 12824 | If this still fails, the failure is something other than 12824 |
| Push more options into the deploy (closer to "deploy users + database options" prod configs) | `dotnet run --project samples/SqlDataPack.DeployRepro -- --deploy-users true --deploy-database-options true --dump-script` | Same as defaults | Script will show more `ALTER DATABASE` / `CREATE USER` lines — grep for any new error numbers |
| Keep the container running afterwards so you can poke at the target DB | `dotnet run --project samples/SqlDataPack.DeployRepro -- --keep-container` | `[teardown] --keep-container set; leaving container <id> running` plus the connection string | Container disposed anyway → check the trailing logs for an early exception |

For more selective behaviour or unusual fixtures, use the [Flag reference](#flag-reference) below
as a lookup.

---

## Output grammar (parse contract)

The app emits **stdout** in a predictable structure. Agents should pattern-match against these,
not against incidental prose.

### Section banners

Top-level progress is delimited by banners of the form:

```
=== [<section>] ===
```

Known section names in normal order:

```
parse → fixture → container → server → target → options → script (optional) → deploy → verify → done
```

On the unhappy path, `deploy` is followed by:

```
=== [FAILURE after <N.N> s] ===
```

instead of `verify` + `done`. Exit code in that case is **1**.

### DacFx script-generation messages

During `--dump-script`, lines emitted by DacFx are prefixed with `  [dacfx-script] ` (two-space
indent, then the prefix in brackets). The portion after the colon is `<MessageType>: <Message>`.
These appear inside the `script` section only — no DacFx messages stream during the actual deploy
(the production wrapper doesn't expose `DacServices.Message`).

### Smoking-gun block

Inside `=== [script] ===`, after `Wrote deploy script (… chars) to …`:

```
--- smoking-gun lines (CONTAINMENT / contained database / Msg 12824) ---
(none found — DacFx is not scripting a SET CONTAINMENT prerequisite for this config)
--- end smoking-gun lines ---
```

OR, on a configuration that triggers the prerequisite:

```
--- smoking-gun lines (CONTAINMENT / contained database / Msg 12824) ---
  L<line-number>: <verbatim script line containing one of the patterns>
  L<line-number>: <…>
--- end smoking-gun lines ---
```

An agent can branch on whether the body equals `(none found …)` to determine the next step.

### Exception chain (failure path)

Printed to **stderr**, not stdout. Format per exception in the chain:

```
  [<depth>] <FullTypeName>: <Message>
```

For each `SqlException` in the chain, every `SqlError` is dumped on two indented lines:

```
        SqlError Number=<n> Class=<c> State=<s> Line=<l> Procedure='<p>' Source='<s>'
          Message: <text>
```

If any `SqlError.Number == 12824` is seen, a hash-rule banner block is appended:

```
##################################################################
# Msg 12824 'contained database authentication' detected in chain.
# DacFx scripted SET CONTAINMENT = PARTIAL despite the configured
# AdaptAzureSourceForOnPremTarget setting. Re-run with --dump-script
# --no-deploy to inspect which model element drove that decision.
##################################################################
```

### Exit codes

| Code | Meaning | Action |
|---|---|---|
| 0 | Deploy succeeded, or `--no-deploy` exited cleanly after `--dump-script` | None |
| 1 | Deploy failed; exception chain on stderr | Read stderr; pattern-match against the [Symptom → action](#symptom--action) table |
| 64 | CLI parse error (unknown flag, missing value, bad boolean) | Read stderr; fix the command |
| 66 | Fixture file not found | Pass `--db <path>` or drop a `realworld.db` at the default location |

---

## Symptom → action

Pattern-match output against the left column to pick the next experiment.

| What you saw | What it means | Try next |
|---|---|---|
| Exit 1 + the Msg 12824 banner block | `SET CONTAINMENT = PARTIAL` was scripted and ran against a container with `contained database authentication = 0` | (a) Re-run with `--adapt-azure true` — if that succeeds, the rewrite IS load-bearing for your config. (b) Re-run with `--dump-script --no-deploy` to see which model element pulled the containment requirement in. |
| Exit 1, exception chain present, **no** Msg 12824 banner | A different SQL error is fatal; the contained-auth path is not the issue | Identify the top `SqlError Number=` on stderr; look it up. If it mentions an `ALTER DATABASE` setting, try `--deploy-database-options true`. If it mentions an unsupported feature on the target, try a newer `--image`. |
| Smoking-gun block says `(none found …)` AND exit 0 | DacFx is happy with this config; the rewrite (if engaged) successfully neutralised what would have failed | If you were trying to reproduce a user-reported failure, this config doesn't reproduce it — change one flag at a time and rerun |
| Smoking-gun block has `L<n>: ... SET CONTAINMENT ...` AND `--adapt-azure` was `true` | The rewrite did not engage (or engaged but didn't cover the path that pulled the requirement in) | Inspect the offending line in `<fixture>.repro-script.sql`. Look at the surrounding context: which model element drove the prerequisite? Open `src/SqlDataPack/Internal/DacpacSchemaManager.cs` → `NeutralizeForNonAzureSqlTarget` and extend it. |
| `=== [server] ===` shows `EngineEdition : 5` | You are deploying against an Azure SQL container — the rewrite intentionally skips for Azure targets | Use a different `--image` (the default `mcr.microsoft.com/mssql/server:2025-latest` is on-prem and reports EngineEdition 3) |
| Stderr `GenerateDeployScript failed: …` | DacFx couldn't generate the script (usually means the fixture's dacpac itself is malformed) | Check the inner exception; verify the fixture with `unzip -l <db>`-style inspection. If `--no-deploy` was set, the app exits here; without it, deploy is attempted anyway and may surface a more actionable error. |
| Exit 66 | Fixture missing | Run `Test-Path tests/SqlDataPack.IntegrationTests/Fixtures/realworld.db`. If false, drop the file or pass `--db <other-path>` |
| Exit 64 | CLI parse error | Match the unknown-arg line against `ReproOptions.Parse` in `Program.cs` — most likely a typo or missing value after a boolean flag |
| Run hangs in `[container] Starting MsSql container …` for > 60 s on first invocation | Image is being pulled | Run `docker pull mcr.microsoft.com/mssql/server:2025-latest` once outside the harness, then retry. Cold-pull time can exceed 5 minutes on slow links. |
| Run aborts with `Docker container <id> did not start` | Container failed health check | Check `docker logs <id>` for SQL Server startup errors. Most common: insufficient memory (the image needs ~2 GB). Increase Docker Desktop's RAM allocation or use a lighter image. |

---

## Cost model (agent timeout budgets)

| Phase | Typical wall-clock | Notes |
|---|---|---|
| `--help` exit | < 5 s | First `dotnet run` may rebuild |
| Image pull (cold) | 60 s – 5+ min | Pre-pull via `docker pull` to take this off the critical path |
| Container startup | 10 – 15 s | Includes the sqlcmd-based readiness probe |
| `GenerateDeployScript` | 5 – 10 s | Linear in dacpac size |
| Full `DacpacSchemaManager.DeployAsync` | 15 – 30 s | For the bundled real-world dacpac (157 user tables) against an empty target |
| Container teardown | 3 – 5 s | Skipped with `--keep-container` |

**Recommended agent tool timeouts:**

- `--help` / parse smoke: **30 000 ms**
- `--dump-script --no-deploy`: **180 000 ms**
- Full deploy (any invocation without `--no-deploy`): **300 000 ms**

If you expect a cold image pull, bump the deploy timeout to 600 000 ms or pre-pull.

---

## Flag reference

All boolean flags accept `true`/`false`, `1`/`0`, `yes`/`no`, `y`/`n`, `on`/`off`.

| Flag | Default | What it does | Combine with | Avoid with |
|---|---|---|---|---|
| `--db <path>` | walks up from the binary looking for `tests/SqlDataPack.IntegrationTests/Fixtures/realworld.db` | SQLite package containing the dacpac payload | Any other flag | — |
| `--image <name>` | `mcr.microsoft.com/mssql/server:2025-latest` (or env `SQLDATAPACK_SQLSERVER_IMAGE`) | Container image. Use 2019/2022/2025/Azure SQL Edge images to A/B DacFx behaviour | — | Setting both this and the env var is fine; the flag wins |
| `--enable-contained-auth` | off | `sp_configure 'contained database authentication', 1; RECONFIGURE` on the container before deploy | `--adapt-azure false` (to test whether 12824 is the ONLY blocker) | `--adapt-azure true` (the rewrite makes the sp_configure irrelevant) |
| `--adapt-azure <true\|false>` | `true` | Sets `DacpacDeploymentOptions.AdaptAzureSourceForOnPremTarget`. Default-true enables the model rewrite (`Containment` strip + contained / external `SqlUser` → `WITHOUT LOGIN`) | `--dump-script` to see whether the rewrite eliminated `SET CONTAINMENT` from the script | `--deploy-database-options true` (that flag deploys options verbatim, ignoring the rewrite — combination is misleading) |
| `--deploy-users <true\|false>` | `false` | Sets `DacpacDeploymentOptions.DeployUsers`. With `false`, `ObjectType.Users` is excluded from the diff | `--deploy-logins true`, `--deploy-permissions true`, `--deploy-role-membership true` for a complete "deploy identity" config | — |
| `--deploy-logins <true\|false>` | `false` | Sets `DeployLogins` | `--deploy-users true` (logins without users is usually meaningless) | — |
| `--deploy-permissions <true\|false>` | `false` | Sets `DeployPermissions` | `--deploy-users true` | — |
| `--deploy-role-membership <true\|false>` | `false` | Sets `DeployRoleMembership` | `--deploy-users true` | — |
| `--deploy-database-options <true\|false>` | `false` | Sets `DeployDatabaseOptions`. When `true`, source database options are applied verbatim — the rewrite is **skipped** even if `--adapt-azure true` | Use solo when you want to see the "no rewrite, no exclusion" deploy DacFx would emit | `--adapt-azure true` (rewrite is bypassed; setting both is misleading) |
| `--allow-incompatible-platform <true\|false>` | `true` | Sets `AllowIncompatiblePlatform`. Required for Azure → on-prem deploys (different DSPs) | Any Azure-source deploy | Setting to `false` will hard-fail immediately on platform mismatch — useful only to confirm DacFx detects the mismatch |
| `--source-engine-edition <n>` | `5` | Forces `SchemaPackage.SourceEngineEdition`. The bundled fixture predates the column so the override matters | `5` = Azure SQL DB, `8` = Azure SQL MI, `11` = Azure SQL Edge, `12` = Azure Synapse, `2/3/4` = on-prem | Cannot be `null` via CLI — pass a real int |
| `--dump-script` | off | Generate the deploy script via `DacServices.GenerateDeployScript`, write to `<fixture-dir>/<fixture-name>.repro-script.sql`, print head and smoking-gun lines | `--no-deploy` for inspection-only runs | — |
| `--no-deploy` | off | Stop after the optional script dump; don't invoke DacFx Deploy | `--dump-script` (always) | Solo (spins up the container for no useful work) |
| `--keep-container` | off | Skip `container.DisposeAsync()` at exit. Connection string and container ID printed to stdout | Manual inspection workflows | CI / automated runs (you'll leak containers) |

---

## How it relates to the production code path

This harness invokes the same internal method the production importer uses:
`SqlDataPack.Internal.DacpacSchemaManager.DeployAsync` (declared in
`src/SqlDataPack/Internal/DacpacSchemaManager.cs`, called by
`src/SqlDataPack/SqlDataPackImporter.cs` when `SchemaDeploymentMode.DeployDacpac` is active).
The `AdaptAzureSourceForOnPremTarget` decision tree and the `NeutralizeForNonAzureSqlTarget`
rewrite (containment-property strip + contained-user → `WITHOUT LOGIN`) therefore run unchanged —
any symptom you reproduce here maps directly back to those source locations.

---

## Caveats / anti-patterns

- `--source-engine-edition` cannot be `null` via CLI. Use `5` (Azure SQL DB) for the bundled
  fixture, or `3` (on-prem Enterprise) when simulating an on-prem source.
- `--no-deploy` without `--dump-script` is wasteful — the container starts, the empty target is
  created, then the app exits without doing anything useful.
- The 288 MB `realworld.db` fixture is gitignored; CI / fresh clones will hit exit 66 unless
  somebody drops the file in place. There is no fallback / synthesised dacpac.
- Data import (the ~200 MB of rows in the SQLite tables) is NOT exercised by this harness.
  Schema deploy is the only thing under test. Use the full `SqlDataPackImporter.ImportAsync`
  pipeline elsewhere if you need data validation.
- DacFx `DacServices.Message` events stream during `--dump-script` but **not** during the actual
  deploy (the production wrapper doesn't expose them). If you need DacFx's deploy-time messages,
  read the exception chain — DacFx surfaces fatal errors there.
