using Microsoft.Data.Sqlite;

namespace SqlDataPack.IntegrationTests.Harness;

internal sealed class SqliteTempFileHarness : IAsyncDisposable {
    public string FilePath { get; }

    public SqliteTempFileHarness() {
        FilePath = Path.Combine(Path.GetTempPath(), $"zsdp-{Guid.NewGuid():N}.sqlite");
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) {
        var connection = new SqliteConnection($"Data Source={FilePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public ValueTask DisposeAsync() {
        // Tests may open a SqliteConnection against FilePath to inspect the package.
        // Microsoft.Data.Sqlite pools that connection, keeping a file handle alive
        // past Dispose. Evict pooled handles before deleting so Windows lets go.
        SqliteConnection.ClearAllPools();

        if (File.Exists(FilePath)) {
            File.Delete(FilePath);
        }

        return ValueTask.CompletedTask;
    }
}
