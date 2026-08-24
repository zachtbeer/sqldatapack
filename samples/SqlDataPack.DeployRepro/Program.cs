using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.SqlServer.Dac;
using Testcontainers.MsSql;
using SqlDataPack.Internal;
using SqlDataPack.Models;

// Diagnostic console reproducer for Azure-extracted dacpac -> on-prem SQL Server deploys.
// See README.md for full flag documentation.

var options = ReproOptions.Parse(args);
if (options is null) {
    return 64;
}

PrintBanner("parse");
options.PrintTo(Console.Out);

PrintBanner("fixture");
if (!File.Exists(options.DbPath)) {
    Console.Error.WriteLine($"Fixture not found: {options.DbPath}");
    return 66;
}

Console.WriteLine($"Reading SchemaPackage from {options.DbPath}");
var basePackage = await ReadSchemaPackageAsync(options.DbPath);
var package = basePackage with { SourceEngineEdition = options.SourceEngineEdition };
Console.WriteLine($"  package_name      : {package.PackageName}");
Console.WriteLine($"  package_type      : {package.PackageType}");
Console.WriteLine($"  schema_scope      : {package.SchemaScope}");
Console.WriteLine($"  payload bytes     : {package.Payload.Length}");
Console.WriteLine($"  source_database   : {package.SourceDatabaseName}");
Console.WriteLine($"  dacfx_version     : {package.DacFxVersion}");
Console.WriteLine($"  source_engine_ed. : {package.SourceEngineEdition} (forced to {options.SourceEngineEdition} via flag)");

PrintBanner("container");
Console.WriteLine($"Starting MsSql container with image '{options.Image}' …");
var container = new MsSqlBuilder(options.Image).WithPassword("Your_strong_Password123").Build();

var stopwatch = Stopwatch.StartNew();
await container.StartAsync();
Console.WriteLine($"Container ready in {stopwatch.Elapsed.TotalSeconds:F1} s. ID = {container.Id[..12]}");

var masterConnectionString = container.GetConnectionString();
try {
    PrintBanner("server");
    await DescribeServerAsync(masterConnectionString);

    if (options.EnableContainedAuth) {
        Console.WriteLine("Enabling contained database authentication via sp_configure …");
        await ExecuteMasterSqlAsync(masterConnectionString, "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; " + "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");
        await DescribeContainedAuthAsync(masterConnectionString);
    }

    PrintBanner("target");
    var targetDbName = $"repro_{Guid.NewGuid():N}";
    Console.WriteLine($"Creating empty target database '{targetDbName}' …");
    var targetConnectionString = await CreateDatabaseAsync(masterConnectionString, targetDbName);

    PrintBanner("options");
    var deployOptions = BuildDeployOptions(options);
    PrintDeployOptions(deployOptions, options);

    var dacpacPath = await WriteDacpacToTempAsync(package.Payload);
    try {
        if (options.DumpScript) {
            PrintBanner("script");
            try {
                var script = GenerateDeployScript(dacpacPath, targetDbName, targetConnectionString, deployOptions);
                var scriptPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DbPath))!, Path.GetFileNameWithoutExtension(options.DbPath) + ".repro-script.sql");
                await File.WriteAllTextAsync(scriptPath, script);
                Console.WriteLine($"Wrote deploy script ({script.Length} chars) to {scriptPath}");

                Console.WriteLine();
                Console.WriteLine("--- script head (first 30 lines) ---");
                foreach (var line in script.Split('\n').Take(30)) {
                    Console.WriteLine(line.TrimEnd('\r'));
                }

                Console.WriteLine("--- end head ---");

                Console.WriteLine();
                Console.WriteLine("--- smoking-gun lines (CONTAINMENT / contained database / Msg 12824) ---");
                var smokingGuns = script.Split('\n').Select((line, idx) => (Line: line.TrimEnd('\r'), Number: idx + 1)).Where(t => t.Line.Contains("CONTAINMENT", StringComparison.OrdinalIgnoreCase) || t.Line.Contains("contained database", StringComparison.OrdinalIgnoreCase) || t.Line.Contains("12824", StringComparison.Ordinal)).ToList();
                if (smokingGuns.Count == 0) {
                    Console.WriteLine("(none found — DacFx is not scripting a SET CONTAINMENT prerequisite for this config)");
                }
                else {
                    foreach (var (line, number) in smokingGuns) {
                        Console.WriteLine($"  L{number}: {line}");
                    }
                }

                Console.WriteLine("--- end smoking-gun lines ---");
            }
            catch (Exception scriptException) {
                Console.Error.WriteLine($"GenerateDeployScript failed: {scriptException.GetType().Name}: {scriptException.Message}");
                if (!options.NoDeploy) {
                    Console.Error.WriteLine("Continuing to deploy step regardless.");
                }
            }
        }

        if (options.NoDeploy) {
            PrintBanner("done");
            Console.WriteLine("--no-deploy specified; exiting after script dump without invoking DacFx Deploy.");
            return 0;
        }

        PrintBanner("deploy");
        Console.WriteLine($"Calling DacpacSchemaManager.DeployAsync against '{targetDbName}' …");
        var deployStopwatch = Stopwatch.StartNew();
        try {
            // Replicate the production deploy path by going through DacpacSchemaManager rather than
            // calling DacFx directly. The PackagePayload-based SchemaPackage flows through the same
            // NeutralizeForNonAzureSqlTarget gate the importer uses.
            await DacpacSchemaManager.DeployAsync(targetConnectionString, package, deployOptions, allowDacpacObjectDrops: false, CancellationToken.None);

            deployStopwatch.Stop();
            Console.WriteLine($"Deploy succeeded in {deployStopwatch.Elapsed.TotalSeconds:F1} s.");

            PrintBanner("verify");
            await using var connection = new SqlConnection(targetConnectionString);
            await connection.OpenAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;";
            verify.CommandTimeout = 60;
            var tables = Convert.ToInt32(await verify.ExecuteScalarAsync());
            Console.WriteLine($"User tables on target: {tables}");

            return 0;
        }
        catch (Exception exception) {
            deployStopwatch.Stop();
            PrintBanner($"FAILURE after {deployStopwatch.Elapsed.TotalSeconds:F1} s");
            DescribeExceptionChain(exception);
            return 1;
        }
    }
    finally {
        TryDelete(dacpacPath);
    }
}
finally {
    if (options.KeepContainer) {
        Console.WriteLine();
        Console.WriteLine($"[teardown] --keep-container set; leaving container {container.Id[..12]} running.");
        Console.WriteLine($"  Connection string: {masterConnectionString}");
    }
    else {
        await container.DisposeAsync();
    }
}

// ---------------------------------------------------------------------------
// helpers

static void PrintBanner(string section) {
    Console.WriteLine();
    Console.WriteLine($"=== [{section}] ===");
}

static async Task<SchemaPackage> ReadSchemaPackageAsync(string sqlitePath) {
    var connectionString = new SqliteConnectionStringBuilder {
        DataSource = sqlitePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private
    }.ToString();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    // Read the legacy 8-column shape; the source_engine_edition column may not exist on a pre-v4
    // export. The caller forces SourceEngineEdition via `with` so the value here doesn't matter.
    command.CommandText = """
                          SELECT package_type, package_name, package_sha256, created_at_utc,
                                 source_database_name, dacfx_version, schema_scope, payload
                          FROM zsdp_schema_packages
                          WHERE id = 1
                          """;

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
        throw new InvalidOperationException($"Fixture '{sqlitePath}' has no row in zsdp_schema_packages.");
    }

    var scope = reader.IsDBNull(6) ? DacpacSchemaScope.Database : Enum.TryParse<DacpacSchemaScope>(reader.GetString(6), ignoreCase: true, out var parsed) ? parsed : DacpacSchemaScope.Database;

    return new SchemaPackage(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), scope, (byte[])reader.GetValue(7));
}

static async Task DescribeServerAsync(string masterConnectionString) {
    await using var connection = new SqlConnection(masterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
                          SELECT
                              CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
                              CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version,
                              CONVERT(nvarchar(128), SERVERPROPERTY('Edition')) AS edition_name,
                              CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')) AS product_level;
                          """;
    command.CommandTimeout = 30;
    await using var reader = await command.ExecuteReaderAsync();
    if (await reader.ReadAsync()) {
        Console.WriteLine($"  EngineEdition  : {reader.GetInt32(0)}  ({DescribeEngineEdition(reader.GetInt32(0))})");
        Console.WriteLine($"  ProductVersion : {reader.GetString(1)}");
        Console.WriteLine($"  Edition        : {reader.GetString(2)}");
        Console.WriteLine($"  ProductLevel   : {reader.GetString(3)}");
    }

    await reader.CloseAsync();
    await DescribeContainedAuthAsync(masterConnectionString);
}

static async Task DescribeContainedAuthAsync(string masterConnectionString) {
    await using var connection = new SqlConnection(masterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'contained database authentication';";
    command.CommandTimeout = 30;
    var value = Convert.ToInt32(await command.ExecuteScalarAsync());
    Console.WriteLine($"  sp_configure 'contained database authentication' = {value}");
}

static async Task ExecuteMasterSqlAsync(string masterConnectionString, string sql) {
    await using var connection = new SqlConnection(masterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 120;
    await command.ExecuteNonQueryAsync();
}

static async Task<string> CreateDatabaseAsync(string masterConnectionString, string databaseName) {
    await using var connection = new SqlConnection(masterConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"CREATE DATABASE [{databaseName}]";
    command.CommandTimeout = 120;
    await command.ExecuteNonQueryAsync();

    return new SqlConnectionStringBuilder(masterConnectionString) {
        InitialCatalog = databaseName
    }.ConnectionString;
}

static DacpacDeploymentOptions BuildDeployOptions(ReproOptions options) {
    var d = DacpacDeploymentOptions.Default;
    d.AllowIncompatiblePlatform = options.AllowIncompatiblePlatform;
    d.AdaptAzureSourceForOnPremTarget = options.AdaptAzure;
    d.DeployUsers = options.DeployUsers;
    d.DeployLogins = options.DeployLogins;
    d.DeployPermissions = options.DeployPermissions;
    d.DeployRoleMembership = options.DeployRoleMembership;
    d.DeployDatabaseOptions = options.DeployDatabaseOptions;
    d.AllowObjectDrops = false;
    d.BlockOnPossibleDataLoss = false;
    d.VerifyDeployment = true;
    return d;
}

static void PrintDeployOptions(DacpacDeploymentOptions d, ReproOptions opt) {
    Console.WriteLine($"  AllowIncompatiblePlatform       : {d.AllowIncompatiblePlatform}");
    Console.WriteLine($"  AdaptAzureSourceForOnPremTarget : {d.AdaptAzureSourceForOnPremTarget}");
    Console.WriteLine($"  DeployUsers                     : {d.DeployUsers}");
    Console.WriteLine($"  DeployLogins                    : {d.DeployLogins}");
    Console.WriteLine($"  DeployPermissions               : {d.DeployPermissions}");
    Console.WriteLine($"  DeployRoleMembership            : {d.DeployRoleMembership}");
    Console.WriteLine($"  DeployDatabaseOptions           : {d.DeployDatabaseOptions}");
    Console.WriteLine($"  BlockOnPossibleDataLoss         : {d.BlockOnPossibleDataLoss}");
    Console.WriteLine($"  AllowObjectDrops                : {d.AllowObjectDrops}");
    Console.WriteLine($"  VerifyDeployment                : {d.VerifyDeployment}");
    Console.WriteLine($"  SourceEngineEdition (override)  : {opt.SourceEngineEdition}");
}

static async Task<string> WriteDacpacToTempAsync(byte[] payload) {
    var path = Path.Combine(Path.GetTempPath(), $"zsdp-deployrepro-{Guid.NewGuid():N}.dacpac");
    await File.WriteAllBytesAsync(path, payload);
    return path;
}

static string GenerateDeployScript(string dacpacPath, string targetDbName, string targetConnectionString, DacpacDeploymentOptions options) {
    using var package = DacPackage.Load(dacpacPath);
    var dacOptions = DacpacSchemaManager.CreateDeployOptions(options, allowDacpacObjectDrops: false);
    // Use the real target connection so DacFx compares against the actual (empty) target DB shape;
    // that is the script it would emit on a real deploy. Generated offline against (local) can drift
    // from what would run in production.
    var services = new DacServices(targetConnectionString);
    services.Message += (_, e) => Console.WriteLine($"  [dacfx-script] {e.Message.MessageType}: {e.Message.Message}");
    return services.GenerateDeployScript(package, targetDbName, dacOptions);
}

static void DescribeExceptionChain(Exception exception) {
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exception chain (outermost first):");
    var current = exception;
    var depth = 0;
    var seenContainedAuth = false;
    while (current is not null) {
        Console.Error.WriteLine($"  [{depth}] {current.GetType().FullName}: {current.Message}");
        if (current is SqlException sqlException) {
            foreach (SqlError error in sqlException.Errors) {
                Console.Error.WriteLine($"        SqlError Number={error.Number} Class={error.Class} State={error.State} " + $"Line={error.LineNumber} Procedure='{error.Procedure}' Source='{error.Source}'");
                Console.Error.WriteLine($"          Message: {error.Message}");
                if (error.Number == 12824) {
                    seenContainedAuth = true;
                }
            }
        }

        current = current.InnerException;
        depth++;
    }

    if (seenContainedAuth) {
        Console.Error.WriteLine();
        Console.Error.WriteLine("##################################################################");
        Console.Error.WriteLine("# Msg 12824 'contained database authentication' detected in chain.");
        Console.Error.WriteLine("# DacFx scripted SET CONTAINMENT = PARTIAL despite the configured");
        Console.Error.WriteLine("# AdaptAzureSourceForOnPremTarget setting. Re-run with --dump-script");
        Console.Error.WriteLine("# --no-deploy to inspect which model element drove that decision.");
        Console.Error.WriteLine("##################################################################");
    }
}

static string DescribeEngineEdition(int n) => n switch {
    1 => "Personal (deprecated)",
    2 => "Standard",
    3 => "Enterprise",
    4 => "Express",
    5 => "Azure SQL Database",
    6 => "APS / PDW",
    8 => "Azure SQL Managed Instance",
    9 => "Azure SQL Edge (deprecated id)",
    11 => "Azure SQL Edge",
    12 => "Azure Synapse Analytics (SQL pool)",
    _ => $"unknown ({n})"
};

static void TryDelete(string path) {
    try {
        if (File.Exists(path)) {
            File.Delete(path);
        }
    }
    catch {
    }
}

// ---------------------------------------------------------------------------
// options

internal sealed class ReproOptions {
    public required string DbPath { get; init; }
    public required string Image { get; init; }
    public bool EnableContainedAuth { get; init; }
    public bool AdaptAzure { get; init; } = true;
    public bool DeployUsers { get; init; }
    public bool DeployLogins { get; init; }
    public bool DeployPermissions { get; init; }
    public bool DeployRoleMembership { get; init; }
    public bool DeployDatabaseOptions { get; init; }
    public bool AllowIncompatiblePlatform { get; init; } = true;
    public int SourceEngineEdition { get; init; } = 5;
    public bool DumpScript { get; init; }
    public bool NoDeploy { get; init; }
    public bool KeepContainer { get; init; }

    public static ReproOptions? Parse(string[] args) {
        string? db = null;
        string? image = Environment.GetEnvironmentVariable("SQLDATAPACK_SQLSERVER_IMAGE");
        var enableContainedAuth = false;
        var adaptAzure = true;
        var deployUsers = false;
        var deployLogins = false;
        var deployPermissions = false;
        var deployRoleMembership = false;
        var deployDatabaseOptions = false;
        var allowIncompatiblePlatform = true;
        var sourceEngineEdition = 5;
        var dumpScript = false;
        var noDeploy = false;
        var keepContainer = false;

        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--db": db = args[++i]; break;
                case "--image": image = args[++i]; break;
                case "--enable-contained-auth": enableContainedAuth = true; break;
                case "--adapt-azure": adaptAzure = ParseBool(args[++i]); break;
                case "--deploy-users": deployUsers = ParseBool(args[++i]); break;
                case "--deploy-logins": deployLogins = ParseBool(args[++i]); break;
                case "--deploy-permissions": deployPermissions = ParseBool(args[++i]); break;
                case "--deploy-role-membership": deployRoleMembership = ParseBool(args[++i]); break;
                case "--deploy-database-options": deployDatabaseOptions = ParseBool(args[++i]); break;
                case "--allow-incompatible-platform": allowIncompatiblePlatform = ParseBool(args[++i]); break;
                case "--source-engine-edition": sourceEngineEdition = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--dump-script": dumpScript = true; break;
                case "--no-deploy": noDeploy = true; break;
                case "--keep-container": keepContainer = true; break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return null;
            }
        }

        db ??= LocateDefaultFixture();
        image ??= "mcr.microsoft.com/mssql/server:2025-latest";

        return new ReproOptions {
            DbPath = db,
            Image = image,
            EnableContainedAuth = enableContainedAuth,
            AdaptAzure = adaptAzure,
            DeployUsers = deployUsers,
            DeployLogins = deployLogins,
            DeployPermissions = deployPermissions,
            DeployRoleMembership = deployRoleMembership,
            DeployDatabaseOptions = deployDatabaseOptions,
            AllowIncompatiblePlatform = allowIncompatiblePlatform,
            SourceEngineEdition = sourceEngineEdition,
            DumpScript = dumpScript,
            NoDeploy = noDeploy,
            KeepContainer = keepContainer
        };
    }

    public void PrintTo(TextWriter writer) {
        writer.WriteLine($"  --db                              : {DbPath}");
        writer.WriteLine($"  --image                           : {Image}");
        writer.WriteLine($"  --enable-contained-auth           : {EnableContainedAuth}");
        writer.WriteLine($"  --adapt-azure                     : {AdaptAzure}");
        writer.WriteLine($"  --deploy-users                    : {DeployUsers}");
        writer.WriteLine($"  --deploy-logins                   : {DeployLogins}");
        writer.WriteLine($"  --deploy-permissions              : {DeployPermissions}");
        writer.WriteLine($"  --deploy-role-membership          : {DeployRoleMembership}");
        writer.WriteLine($"  --deploy-database-options         : {DeployDatabaseOptions}");
        writer.WriteLine($"  --allow-incompatible-platform     : {AllowIncompatiblePlatform}");
        writer.WriteLine($"  --source-engine-edition           : {SourceEngineEdition}");
        writer.WriteLine($"  --dump-script                     : {DumpScript}");
        writer.WriteLine($"  --no-deploy                       : {NoDeploy}");
        writer.WriteLine($"  --keep-container                  : {KeepContainer}");
    }

    private static bool ParseBool(string raw) => raw.ToLowerInvariant() switch {
        "true" or "1" or "yes" or "y" or "on" => true,
        "false" or "0" or "no" or "n" or "off" => false,
        _ => throw new ArgumentException($"Cannot parse boolean: '{raw}'")
    };

    private static string LocateDefaultFixture() {
        // Walk up from the executable directory looking for the fixture path used by the integration tests.
        var assemblyDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, "tests", "SqlDataPack.IntegrationTests", "Fixtures", "realworld.db");
            if (File.Exists(candidate)) {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine("tests", "SqlDataPack.IntegrationTests", "Fixtures", "realworld.db");
    }

    private static void PrintHelp() {
        Console.Error.WriteLine("Usage: dotnet run --project samples/SqlDataPack.DeployRepro -- [flags]");
        Console.Error.WriteLine("See samples/SqlDataPack.DeployRepro/README.md for full flag documentation.");
    }
}
