using Microsoft.Data.Sqlite;

namespace SqlDataPack.IntegrationTests.Harness;

internal static class SqliteConnectionTestExtensions {
    public static async Task ExecuteSqlAsync(this SqliteConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<int> ScalarIntAsync(this SqliteConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public static async Task<string> ScalarStringAsync(this SqliteConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToString(result)!;
    }

    public static async Task<IReadOnlyList<string>> ReadStringsAsync(this SqliteConnection connection, string sql) {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public static async Task<bool> TableExistsAsync(this SqliteConnection connection, string tableName) {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    public static async Task<bool> TableColumnExistsAsync(this SqliteConnection connection, string tableName, string columnName) {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info({QuoteSqliteLiteral(tableName)}) WHERE name = $column";
        command.Parameters.AddWithValue("$column", columnName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static string QuoteSqliteLiteral(string value) {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
