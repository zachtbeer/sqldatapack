# SqlDataPack.Benchmarks

Performance measurements for the two hot paths in the library. Neither benchmark needs SQL Server, Docker, or a network — they run anywhere the SDK does, including Claude Code on the web.

```bash
# everything
dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*'

# one class
dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*ValueConvert*'

# harness smoke check, seconds rather than minutes, numbers are meaningless
dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*' --job Dry
```

Release configuration is mandatory — BenchmarkDotNet refuses to run a Debug build.

## What is measured

**`ExportWriteBenchmarks`** drives `SqlitePackage.InitializeAsync` plus `SqlitePackageWriter.WriteTableAsync` — the real production write loop — with `SyntheticSourceReader` standing in for the SQL Server reader. Each iteration writes to a fresh package file, so results include real file I/O. That is deliberate: the PRAGMA settings this benchmark exists to evaluate only show up against a real file. Four table shapes (`NarrowInt`, `WideText`, `BlobHeavy`, `MixedTypes`) are measured separately so a regression can be attributed to a shape rather than averaged away.

**`ValueConvertBenchmarks`** measures per-cell type conversion in both directions across the type matrix. It hoists `ColumnKind` out of the loop because that is what the production callers do: `SqlitePackageWriter` resolves it once per column before the row loop, and `SqliteCoercingDataReader` caches it in its constructor. Benchmarking the convenience overload instead would measure an API the hot paths deliberately avoid.

## Why the write loop lives in `SqlitePackageWriter`

It was originally a private method inside `SqlDataPackExporter`, which no benchmark could reach. Rather than benchmark a copy of it — which would measure the copy, not the shipping code — the loop moved behind a `DbDataReader`-shaped seam. `SqlDataPackExporter` still builds the SQL Server query and owns the connection; only the row-streaming half is shared.

## Interpreting results

`ExportWriteBenchmarks` uses `RunStrategy.Monitoring` with one invocation per iteration so `[IterationSetup]` runs exactly once per measured call. That strategy is noisier than the default; treat differences under roughly 10% as inconclusive and re-run.

Both classes were noticeably affected by background load on a workstation during development. When a number surprises you, run it again before acting on it — one of the changes in this project's history was reverted-then-reinstated on exactly that basis.

## Not run in CI

Benchmarks are not part of `dotnet test SqlDataPack.slnx` (the project has no test SDK reference, so the test runner skips it) and no workflow invokes them. Shared CI runners are too noisy for the numbers to mean anything. Run them locally when changing the export write loop, `ValueConverter`, `SqliteCoercingDataReader`, or the SQLite PRAGMA configuration.
