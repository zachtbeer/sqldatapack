using System.Data;
using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using SqlDataPack.Transformations;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Transformation as the export actually performs it: the production package initializer and the production
/// write loop, with an in-memory reader standing in for SQL Server. That is what makes the NULL bypass, the
/// untouched columns, and the recorded metadata assertions about the shipped path rather than about a copy.
/// </summary>
public sealed class TransformationExportTests {
    private static readonly TableName Customers = new("dbo", "Customers");
    private static readonly TableName Orders = new("dbo", "Orders");

    [Fact]
    public async Task Export_TransformedColumn_WritesNoOriginalValue() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());

        using var package = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"), ("john.roe@contoso.com", "Denver"));

        var emails = package.Column("dbo__customers", "Email");
        emails.ShouldNotContain("jane.doe@contoso.com");
        emails.ShouldNotContain("john.roe@contoso.com");
        emails.ShouldAllBe(email => email!.EndsWith("@example.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_UnconfiguredColumn_IsUnchanged() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());

        using var package = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));

        package.Column("dbo__customers", "City").ShouldBe(["Seattle"]);
    }

    [Fact]
    public async Task Export_SourceNull_BypassesTheTransformerAndStaysNull() {
        var calls = 0;
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new CustomTransformer((_, value) => {
            calls++;
            return $"seen-{value}";
        }));

        using var package = await ExportAsync(options, (null, "Seattle"), ("jane.doe@contoso.com", "Denver"));

        package.Column("dbo__customers", "Email").ShouldBe([null, "seen-jane.doe@contoso.com"]);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Export_CustomTransformerReturningNullForANullableColumn_WritesNull() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new CustomTransformer((_, _) => null));

        using var package = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));

        package.Column("dbo__customers", "Email").ShouldBe([null]);
    }

    [Fact]
    public async Task Export_CustomTransformerReturningNullForANonNullableColumn_FailsTheExport() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.City", new CustomTransformer((_, _) => null));

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => ExportAsync(options, ("jane.doe@contoso.com", "Seattle")));

        exception.Message.ShouldContain("dbo.Customers.City, which is not nullable");
    }

    [Fact]
    public async Task Export_TransformerThatThrows_FailsTheExport() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new CustomTransformer((_, _) => throw new InvalidOperationException("boom")));

        await Should.ThrowAsync<SqlDataPackException>(() => ExportAsync(options, ("jane.doe@contoso.com", "Seattle")));
    }

    [Fact]
    public async Task Export_TheSameValueInTwoTables_PseudonymizesIdentically() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
        options.Transformations.Add("dbo.Orders.ContactEmail", new EmailPseudonymizer());

        using var package = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));

        package.Column("dbo__customers", "Email").Single().ShouldBe(package.Column("dbo__orders", "ContactEmail").Single());
    }

    [Fact]
    public async Task Export_RunTwice_ProducesDifferentPseudonyms() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());

        using var first = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));
        using var second = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));

        second.Column("dbo__customers", "Email").Single().ShouldNotBe(first.Column("dbo__customers", "Email").Single());
    }

    [Fact]
    public async Task Export_RecordsWhichColumnsWereTransformed() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
        options.Transformations.Add("dbo.Customers.City", new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" }));
        options.Transformations.Add("dbo.Orders.ContactEmail", new CustomTransformer((_, value) => $"TEST-{value}"));

        using var package = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));
        var manifest = await SqlitePackage.ReadManifestAsync(package.Connection, CancellationToken.None);

        manifest.Transformations.Select(t => $"{t.ColumnPath}|{t.TransformerType}|{t.Configuration}").ShouldBe([
            "dbo.Customers.City|NameMasker|PreserveCharacters=2;Suffix=test",
            "dbo.Customers.Email|EmailPseudonymizer|Domain=example.invalid;PreserveDomain=False",
            "dbo.Orders.ContactEmail|Custom|"
        ]);
    }

    [Fact]
    public async Task Export_TransformationMetadata_CarriesNoOriginalValues() {
        var options = ExportOptions.Default;
        options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());

        using var package = await ExportAsync(options, ("jane.doe@contoso.com", "Seattle"));

        var recorded = string.Join("\n", await package.QueryAsync("SELECT source_schema, source_table, column_name, transformer_type, IFNULL(configuration, '') FROM zsdp_transformations"));
        recorded.ShouldNotContain("jane.doe");
        recorded.ShouldBe("dbo|Customers|Email|EmailPseudonymizer|Domain=example.invalid;PreserveDomain=False");
    }

    [Fact]
    public async Task Export_WithoutTransformations_RecordsNoneAndCopiesValuesThrough() {
        using var package = await ExportAsync(ExportOptions.Default, ("jane.doe@contoso.com", "Seattle"));
        var manifest = await SqlitePackage.ReadManifestAsync(package.Connection, CancellationToken.None);

        manifest.Transformations.ShouldBeEmpty();
        package.Column("dbo__customers", "Email").ShouldBe(["jane.doe@contoso.com"]);
    }

    private static async Task<Package> ExportAsync(ExportOptions options, params (string? Email, string City)[] rows) {
        var customers = new TableMetadata(Customers, "dbo__customers", [
            Text(Customers, "Email", 1, isNullable: true),
            Text(Customers, "City", 2, isNullable: false)
        ]);
        var orders = new TableMetadata(Orders, "dbo__orders", [Text(Orders, "ContactEmail", 1, isNullable: true)]);
        var tables = new[] { customers, orders };

        var transformations = TransformationBinder.Validate(tables, options);
        var plan = new ExportPlan(tables, [], [Customers, Orders], [], [], [], new string('a', 64), transformations);

        var package = new Package();
        await package.OpenAsync();
        await SqlitePackage.InitializeAsync(package.Connection, plan, CancellationToken.None);

        // One secret per export, exactly as SqlDataPackExporter creates it.
        var secret = options.Transformations.Count == 0 ? null : ExportSecret.Create();
        var byColumn = TransformationBinder.Normalize(options);

        try {
            await WriteAsync(package.Connection, customers, byColumn, secret, rows.Select(row => new object?[] { row.Email, row.City }));
            await WriteAsync(package.Connection, orders, byColumn, secret, rows.Select(row => new object?[] { row.Email }));
        }
        catch {
            package.Dispose();
            throw;
        }

        return package;
    }

    private static async Task WriteAsync(SqliteConnection connection, TableMetadata table, IReadOnlyDictionary<string, IValueTransformer> byColumn, ExportSecret? secret, IEnumerable<object?[]> rows) {
        using var source = new DataTable();
        foreach (var column in table.ExportedColumns) {
            source.Columns.Add(column.Name, typeof(string));
        }

        foreach (var row in rows) {
            source.Rows.Add(row.Select(value => value ?? (object)DBNull.Value).ToArray());
        }

        using var reader = source.CreateDataReader();
        var transforms = TransformationBinder.CreateForTable(table, byColumn, secret);
        await SqlitePackageWriter.WriteTableAsync(connection, reader, table, batchSize: 100, progress: null, CancellationToken.None, transforms);
        await SqlitePackage.RecordTableStatsAsync(connection, table, source.Rows.Count, exportBatchSize: 100, CancellationToken.None);
    }

    private static ColumnMetadata Text(TableName table, string name, int ordinal, bool isNullable) =>
        new(table, name, ordinal, "nvarchar", 400, 0, 0, isNullable, IsIdentity: false, IsComputed: false, CollationName: null, IsExcluded: false);

    private sealed class Package : IDisposable {
        private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sqldatapack-transform-{Guid.NewGuid():N}.sqlite");

        public SqliteConnection Connection { get; private set; } = null!;

        public async Task OpenAsync() {
            Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            await Connection.OpenAsync();
        }

        public IReadOnlyList<string?> Column(string table, string column) {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT \"{column}\" FROM \"{table}\"";
            using var reader = command.ExecuteReader();
            var values = new List<string?>();
            while (reader.Read()) {
                values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }

            return values;
        }

        public async Task<IReadOnlyList<string>> QueryAsync(string sql) {
            await using var command = Connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync()) {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
            }

            return rows;
        }

        public void Dispose() {
            Connection.Dispose();
            SqliteConnection.ClearAllPools();
            try {
                File.Delete(path);
            }
            catch (IOException) {
                /* best effort */
            }
        }
    }
}
