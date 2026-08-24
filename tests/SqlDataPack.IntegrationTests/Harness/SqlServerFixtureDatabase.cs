using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlDataPack.IntegrationTests.Harness;

internal sealed class SqlServerFixtureDatabase : IAsyncDisposable {
    private readonly string _masterConnectionString;

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    public SqlServerFixtureDatabase(string masterConnectionString, string databaseName, string connectionString) {
        _masterConnectionString = masterConnectionString;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public static async Task<SqlServerFixtureDatabase> CreateAsync(SqlServerContainerFixture fixture) {
        return await CreateAsync(fixture, previewFeatures: false);
    }

    /// <summary>
    /// Creates a database, optionally with <c>PREVIEW_FEATURES = ON</c> so preview types (the float16
    /// vector column) can be created at all.
    /// </summary>
    public static async Task<SqlServerFixtureDatabase> CreateAsync(SqlServerContainerFixture fixture, bool previewFeatures) {
        var databaseName = $"zsdp_{Guid.NewGuid():N}";
        var connectionString = await fixture.CreateDatabaseAsync(databaseName, previewFeatures);
        return new SqlServerFixtureDatabase(fixture.MasterConnectionString, databaseName, connectionString);
    }

    /// <summary>Creates a database with <c>CONTAINMENT = PARTIAL</c>. See azure-partial-containment.sql.</summary>
    public static async Task<SqlServerFixtureDatabase> CreateContainedAsync(SqlServerContainerFixture fixture) {
        var databaseName = $"zsdp_{Guid.NewGuid():N}";
        var connectionString = await fixture.CreateContainedDatabaseAsync(databaseName);
        return new SqlServerFixtureDatabase(fixture.MasterConnectionString, databaseName, connectionString);
    }

    /// <summary>
    /// Runs a script, splitting it into batches on lines that are exactly <c>GO</c>. Batches run in order on
    /// one connection, so a CREATE SCHEMA / CREATE LOGIN / ALTER DATABASE that has to land before the
    /// statements depending on it actually does.
    /// </summary>
    public async Task ExecuteSqlAsync(string sql) {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var batch in SqlScriptLoader.SplitBatches(sql)) {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<int> ScalarIntAsync(string sql) {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<string> ScalarStringAsync(string sql) {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToString(result)!;
    }

    /// <summary>
    /// First column of the first row as uppercase hex, for byte-exact comparisons (rowversion, the binary
    /// rendering of an nvarchar value, a period column). Binary values are rendered as-is; an nvarchar value
    /// is rendered as its UTF-16LE bytes, matching <c>CONVERT(VARBINARY(MAX), col)</c> on the server.
    /// </summary>
    public async Task<string> ScalarHexAsync(string sql) {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        return ToHex(await command.ExecuteScalarAsync());
    }

    /// <summary>Every row's first column as a string, <c>&lt;NULL&gt;</c> for nulls. Deterministic only if the query orders.</summary>
    public async Task<IReadOnlyList<string>> ReadStringsAsync(string sql) {
        var rows = new List<string>();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            rows.Add(reader.IsDBNull(0) ? "<NULL>" : Convert.ToString(reader.GetValue(0))!);
        }

        return rows;
    }

    /// <summary>Every row rendered as its columns joined by <c>" | "</c>, nulls as <c>&lt;NULL&gt;</c>.</summary>
    public async Task<IReadOnlyList<string>> ReadRowsAsync(string sql) {
        var records = await ReadRecordsAsync(sql);
        return records.Select(values => string.Join(" | ", values)).ToArray();
    }

    /// <summary>
    /// Every row as its column values, nulls as <c>&lt;NULL&gt;</c>. Use this rather than
    /// <see cref="ReadRowsAsync"/> when a value can itself contain the join separator -- catalog reads over
    /// adversarial identifiers do.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<string>>> ReadRecordsAsync(string sql) {
        var rows = new List<IReadOnlyList<string>>();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) {
                values[i] = reader.IsDBNull(i) ? "<NULL>" : Convert.ToString(reader.GetValue(i))!;
            }

            rows.Add(values);
        }

        return rows;
    }

    public async ValueTask DisposeAsync() {
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();

        await using (var singleUser = master.CreateCommand()) {
            singleUser.CommandText = $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE";
            singleUser.CommandTimeout = 120;
            await singleUser.ExecuteNonQueryAsync();
        }

        await using (var drop = master.CreateCommand()) {
            drop.CommandText = $"DROP DATABASE [{DatabaseName}]";
            drop.CommandTimeout = 120;
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static string ToHex(object? value) {
        var bytes = value switch {
            null or DBNull => throw new InvalidOperationException("Query returned NULL; there are no bytes to compare."),
            byte[] blob => blob,
            string text => Encoding.Unicode.GetBytes(text),
            _ => throw new InvalidOperationException($"Value of type '{value.GetType().Name}' has no byte-exact rendering; CONVERT it to VARBINARY in the query.")
        };

        return Convert.ToHexString(bytes);
    }
}
