using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace SqlDataPack.IntegrationTests.Harness;

public sealed class SqlServerContainerFixture : IAsyncLifetime {
    public const string SqlServerImageEnvironmentVariable = "SQLDATAPACK_SQLSERVER_IMAGE";
    public const string DefaultSqlServerImage = "mcr.microsoft.com/mssql/server:2025-latest";

    private readonly MsSqlContainer _container;
    private readonly SemaphoreSlim _containmentGate = new(1, 1);
    private bool _containedAuthenticationEnabled;

    public SqlServerContainerFixture() {
        var configuredImage = Environment.GetEnvironmentVariable(SqlServerImageEnvironmentVariable);
        ImageName = string.IsNullOrWhiteSpace(configuredImage) ? DefaultSqlServerImage : configuredImage;

        _container = new MsSqlBuilder(ImageName).WithPassword("Your_strong_Password123").Build();
    }

    public string MasterConnectionString => _container.GetConnectionString();
    public string ImageName { get; }
    public int? ServerMajorVersion { get; private set; }
    public bool IsSqlServer2025OrLater => ServerMajorVersion >= 17 || ImageName.Contains(":2025", StringComparison.OrdinalIgnoreCase);
    public bool SupportsNativeJson => IsSqlServer2025OrLater;
    public bool SupportsVector => IsSqlServer2025OrLater;

    /// <summary>
    /// float16 vectors are a preview feature on top of the 2025 vector type: the database also needs
    /// <c>PREVIEW_FEATURES = ON</c>, which <see cref="CreateDatabaseAsync(string?, bool)"/> sets.
    /// </summary>
    public bool SupportsVectorFloat16 => IsSqlServer2025OrLater;

    public async Task InitializeAsync() {
        await _container.StartAsync();
        ServerMajorVersion = await ReadServerMajorVersionAsync();
    }

    public async Task DisposeAsync() {
        _containmentGate.Dispose();
        await _container.DisposeAsync();
    }

    public Task<string> CreateDatabaseAsync(string? databaseName = null) {
        return CreateDatabaseAsync(databaseName, previewFeatures: false);
    }

    /// <summary>
    /// Creates a database and returns its connection string. With <paramref name="previewFeatures"/> the
    /// database gets <c>PREVIEW_FEATURES = ON</c>, without which a <c>VECTOR(n, float16)</c> column cannot be
    /// created at all.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string? databaseName, bool previewFeatures) {
        databaseName ??= $"zsdp_{Guid.NewGuid():N}";

        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();

        await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}]");

        if (previewFeatures) {
            await ExecuteAsync(master, $"ALTER DATABASE [{databaseName}] SET PREVIEW_FEATURES = ON");
        }

        return WithCatalog(databaseName);
    }

    /// <summary>
    /// Creates a database with <c>CONTAINMENT = PARTIAL</c>, enabling contained database authentication on the
    /// server first (at most once per container). Needed by azure-partial-containment.sql: a contained user
    /// cannot be created without it.
    /// </summary>
    public async Task<string> CreateContainedDatabaseAsync(string? databaseName = null) {
        databaseName ??= $"zsdp_{Guid.NewGuid():N}";

        await EnsureContainedAuthenticationAsync();

        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();

        await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}]");
        await ExecuteAsync(master, $"ALTER DATABASE [{databaseName}] SET CONTAINMENT = PARTIAL WITH ROLLBACK IMMEDIATE");

        return WithCatalog(databaseName);
    }

    /// <summary>
    /// Creates a fresh login and database user that is <c>db_owner</c> minus the given DENYs, and returns a
    /// connection string authenticating as it. A DENY is what makes a permission failure deterministic, and a
    /// DENY does nothing when the connection is <c>sa</c> -- so any test asserting that an operation fails on
    /// permissions has to connect as this principal or it proves nothing.
    /// </summary>
    /// <param name="denyStatements">
    /// T-SQL DENY statements with <c>{principal}</c> where the user name goes, e.g.
    /// <c>"DENY ALTER ON OBJECT::dbo.Sectors TO {principal}"</c>. A statement that already names a principal is
    /// used as written.
    /// </param>
    internal async Task<string> CreateRestrictedPrincipalAsync(SqlServerFixtureDatabase db, IEnumerable<string> denyStatements) {
        const string password = "R3str1cted!Principal_2026";
        var principal = $"zsdp_restricted_{Guid.NewGuid():N}";

        await using (var master = new SqlConnection(MasterConnectionString)) {
            await master.OpenAsync();
            await ExecuteAsync(master, $"CREATE LOGIN [{principal}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF");
        }

        await using (var database = new SqlConnection(db.ConnectionString)) {
            await database.OpenAsync();
            await ExecuteAsync(database, $"CREATE USER [{principal}] FOR LOGIN [{principal}]");
            await ExecuteAsync(database, $"ALTER ROLE db_owner ADD MEMBER [{principal}]");

            foreach (var deny in denyStatements) {
                await ExecuteAsync(database, deny.Replace("{principal}", $"[{principal}]", StringComparison.Ordinal));
            }
        }

        return new SqlConnectionStringBuilder(MasterConnectionString) {
            InitialCatalog = db.DatabaseName,
            UserID = principal,
            Password = password,
            IntegratedSecurity = false
        }.ConnectionString;
    }

    /// <summary>
    /// A connection string for a login a fixture script created itself (temporal-suite.sql creates one), so a
    /// test that must not run as <c>sa</c> can connect as it.
    /// </summary>
    internal string ConnectionStringFor(SqlServerFixtureDatabase db, string login, string password) {
        return new SqlConnectionStringBuilder(MasterConnectionString) {
            InitialCatalog = db.DatabaseName,
            UserID = login,
            Password = password,
            IntegratedSecurity = false
        }.ConnectionString;
    }

    private async Task EnsureContainedAuthenticationAsync() {
        await _containmentGate.WaitAsync();
        try {
            if (_containedAuthenticationEnabled) {
                return;
            }

            await using var master = new SqlConnection(MasterConnectionString);
            await master.OpenAsync();
            await ExecuteAsync(master, "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");

            _containedAuthenticationEnabled = true;
        }
        finally {
            _containmentGate.Release();
        }
    }

    private string WithCatalog(string databaseName) {
        return new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = databaseName }.ConnectionString;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ReadServerMajorVersionAsync() {
        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();

        await using var command = master.CreateCommand();
        command.CommandText = "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))";
        command.CommandTimeout = 120;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
