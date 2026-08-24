using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using Microsoft.Data.Sqlite;
using SqlDataPack.Internal;

namespace SqlDataPack.Benchmarks;

/// <summary>
/// Pins one invocation per iteration so <c>[IterationSetup]</c> runs exactly once per measured call.
/// Without this the default unroll factor batches many invocations behind a single setup, and every
/// invocation after the first would write into an already-populated package.
/// </summary>
internal sealed class OneInvocationPerIterationConfig : ManualConfig {
    public OneInvocationPerIterationConfig() {
        AddJob(Job.Default.WithStrategy(RunStrategy.Monitoring).WithInvocationCount(1).WithUnrollFactor(1).WithWarmupCount(2).WithIterationCount(10));
    }
}

/// <summary>
/// Measures the SQLite write half of the export path — <see cref="SqlitePackage.InitializeAsync"/>
/// followed by <see cref="SqlitePackageWriter.WriteTableAsync"/> — with a synthetic source reader
/// standing in for SQL Server. No database or container required.
/// </summary>
/// <remarks>
/// Each iteration writes to a fresh package file, so the numbers include real file I/O. That is the
/// point: the PRAGMA settings this benchmark exists to evaluate only show up against a real file.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(OneInvocationPerIterationConfig))]
public class ExportWriteBenchmarks {
    private string _directory = string.Empty;
    private string _packagePath = string.Empty;
    private SqliteConnection? _connection;
    private TableMetadata _table = null!;
    private SyntheticSourceReader _reader = null!;

    [Params(TableShape.NarrowInt, TableShape.WideText, TableShape.MixedTypes, TableShape.BlobHeavy)]
    public TableShape Shape { get; set; }

    [Params(10_000)] public int RowCount { get; set; }

    /// <summary>Matches <c>ExportOptions.Default.BatchSize</c>.</summary>
    [Params(1_000)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup() {
        _directory = Path.Combine(Path.GetTempPath(), "zsdp-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _table = BenchmarkFixtures.CreateTable(Shape);
    }

    [GlobalCleanup]
    public void GlobalCleanup() {
        try {
            Directory.Delete(_directory, recursive: true);
        }
        catch {
            /* best effort */
        }
    }

    [IterationSetup]
    public void IterationSetup() {
        _packagePath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".sqlite");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _packagePath }.ConnectionString);
        _connection.Open();

        var plan = new ExportPlan([_table], [], [_table.Name], [], [], [], "benchmark");
        SqlitePackage.InitializeAsync(_connection, plan, CancellationToken.None).GetAwaiter().GetResult();

        _reader = new SyntheticSourceReader(RowCount, _table.ExportedColumns.Count, BenchmarkFixtures.ValueFactoryFor(Shape));
    }

    [IterationCleanup]
    public void IterationCleanup() {
        if (_connection is not null) {
            _connection.Close();
            // Pooling keeps the sqlite3 file handle alive past Close/Dispose on Windows.
            SqliteConnection.ClearPool(_connection);
            _connection.Dispose();
            _connection = null;
        }

        try {
            File.Delete(_packagePath);
        }
        catch {
            /* best effort */
        }
    }

    [Benchmark]
    public async Task<long> WriteTable() {
        return await SqlitePackageWriter.WriteTableAsync(_connection!, _reader, _table, BatchSize, progress: null, CancellationToken.None);
    }
}
