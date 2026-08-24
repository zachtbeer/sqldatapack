using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// The package as an artifact: what it records about itself, and how it lands on disk. Nothing here is
/// about moving rows into a target -- these tests read the package the way a consumer inspecting one
/// without importing would, and watch the destination file across a successful and a failed export.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class PackageManifestAndFileTests {
    private const string CoreCommerceFixture = "core-commerce.sql";
    private const string LegacyFlagsColumn = "dbo.CustomerProfiles.LegacyFlags";

    /// <summary>
    /// Whole-database core-commerce, foreign-key-safe. Parents first, alphabetical within a dependency
    /// level -- ImportPlanner emits levels in that order, so the sequence is exact, not merely plausible.
    /// </summary>
    private static readonly string[] WholeDatabaseImportOrder = [
        "dbo.Countries",
        "dbo.Currencies",
        "dbo.GlobalSettings",
        "tenant.Customers",
        "tenant.Partners",
        "dbo.Customers",
        "dbo.CustomerDocuments",
        "dbo.CustomerProfiles",
        "dbo.Orders",
        "dbo.OrderLines"
    ];

    private readonly SqlServerContainerFixture _fixture;

    public PackageManifestAndFileTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    /// <summary>
    /// The baseline. Every metadata table present, a run row that describes this build of the library, the
    /// full table set, the import plan, and a per-table row count that agrees with both the source and the
    /// physical data table. This is the first thing to fail on a broad regression.
    /// </summary>
    [Fact]
    public async Task Export_WritesCompletePackage() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerceFixture));
        await using var sqlite = new SqliteTempFileHarness();
        var startedAt = DateTimeOffset.UtcNow;
        var options = new ExportOptions { ExcludeColumns = [LegacyFlagsColumn] };

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        result.TableCount.ShouldBe(10);

        await using var package = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasRequiredMetadataTablesAsync(package);
        // Covers package_format_version against SqlDataPackVersion.PackageFormatVersion, a non-empty
        // application version, a non-empty timestamp and a 64-character schema hash.
        await SqlitePackageAssertions.HasRunMetadataAsync(package);

        var exportedAtText = await package.ScalarStringAsync("SELECT exported_at_utc FROM zsdp_export_runs WHERE id = 1");
        DateTimeOffset.TryParse(exportedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var exportedAt)
            .ShouldBeTrue($"exported_at_utc '{exportedAtText}' is not a parseable timestamp.");
        exportedAt.Offset.ShouldBe(TimeSpan.Zero, $"exported_at_utc '{exportedAtText}' is not UTC.");
        exportedAt.ShouldBeInRange(startedAt.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        var schemaHash = await package.ScalarStringAsync("SELECT source_schema_hash FROM zsdp_export_runs WHERE id = 1");
        schemaHash.ShouldBe(schemaHash.ToLowerInvariant());
        schemaHash.All(Uri.IsHexDigit).ShouldBeTrue($"source_schema_hash '{schemaHash}' is not hex.");

        await SqlitePackageAssertions.HasExportedTablesAsync(package, WholeDatabaseImportOrder);
        await SqlitePackageAssertions.HasImportPlanAsync(package, WholeDatabaseImportOrder);

        long totalRows = 0;
        foreach (var (fullName, sqliteTable) in await ReadDataTableMapAsync(package)) {
            var sourceRows = await source.ScalarIntAsync($"SELECT COUNT(*) FROM {QuoteSqlServer(fullName)}");
            await SqlitePackageAssertions.HasTableRowCountAsync(package, fullName, sourceRows);
            (await package.ScalarIntAsync($"SELECT COUNT(*) FROM {QuoteSqlite(sqliteTable)}"))
                .ShouldBe(sourceRows, $"'{fullName}': the recorded row count and the rows in '{sqliteTable}' disagree.");
            totalRows += sourceRows;
        }

        result.RowCount.ShouldBe(totalRows);
    }

    /// <summary>
    /// The tooling contract. A consumer that inspects a package without importing reads it through
    /// SqlDataPackReader, and every field the writer stores has to come back unchanged -- wrong row counts
    /// or a wrong engine edition here are what the Azure containment rewrite decision gets made on.
    /// </summary>
    [Fact]
    public async Task ReadManifest_ReturnsEveryStoredField() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerceFixture));
        await using var sqlite = new SqliteTempFileHarness();
        var options = new ExportOptions {
            // AllExcept so one table is deliberately dropped and dbo.sysdiagrams is dropped by default,
            // which is also what puts a warning in the package.
            Tables = ["dbo.GlobalSettings"],
            ExcludeColumns = [LegacyFlagsColumn, "dbo.OrderLines.Notes"],
            CommandTimeout = 120,
            SchemaCaptureMode = SchemaCaptureMode.Dacpac,
            DacpacCaptureOptions = new DacpacCaptureOptions { SchemaScope = DacpacSchemaScope.SelectedExportTables }
        };

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        var manifest = await new SqlDataPackReader().ReadManifestAsync(sqlite.FilePath);

        manifest.PackageFormatVersion.ShouldBe(SqlDataPackVersion.PackageFormatVersion);
        manifest.ApplicationVersion.ShouldNotBeNullOrWhiteSpace();
        manifest.ExportedAtUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(-1));
        manifest.ExportedAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        manifest.SourceSchemaHash.Length.ShouldBe(64);
        manifest.ImportOrder.ShouldBe(WholeDatabaseImportOrder.Where(t => t != "dbo.GlobalSettings").ToArray());
        manifest.Exclusions.ShouldBe([
            "column:dbo.CustomerProfiles.LegacyFlags",
            "column:dbo.OrderLines.ExtendedPrice",
            "column:dbo.OrderLines.Notes",
            "table:dbo.GlobalSettings",
            "table:dbo.sysdiagrams"
        ]);
        manifest.Warnings.ShouldBe(result.Warnings, "The warnings stored in the package are not the warnings the export reported.");
        manifest.Warnings.ShouldContain(w => w.Contains("sysdiagrams", StringComparison.Ordinal));
        manifest.ContainsDacpac.ShouldBeTrue();
        manifest.DacpacSchemaScope.ShouldBe(DacpacSchemaScope.SelectedExportTables);
        manifest.SourceEngineEdition.ShouldNotBeNull();
        manifest.SourceEngineEdition!.Value.ShouldBeGreaterThan(0);

        manifest.Tables.Select(t => t.FullName).Order(StringComparer.Ordinal).ShouldBe(manifest.ImportOrder.Order(StringComparer.Ordinal));
        manifest.Tables.Sum(t => t.ExportedRowCount).ShouldBe(result.RowCount);

        var customers = manifest.Tables.Single(t => t.FullName == "dbo.Customers");
        customers.SourceSchema.ShouldBe("dbo");
        customers.SourceTable.ShouldBe("Customers");
        customers.SqliteTable.ShouldBe("dbo__customers");
        customers.ExportedRowCount.ShouldBe(await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers"));
        customers.EstimatedSourceRowCount.ShouldBeGreaterThanOrEqualTo(0);
        customers.EstimatedSourceBytes.ShouldBeGreaterThanOrEqualTo(0);
        customers.ExportBatchSize.ShouldBeGreaterThan(0);
        customers.Columns.Select(c => c.Ordinal).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9]);

        var customerId = customers.Columns.Single(c => c.Name == "CustomerId");
        customerId.SqlServerTypeName.ShouldBe("int");
        ((int)customerId.MaxLength).ShouldBe(4);
        ((int)customerId.Precision).ShouldBe(10);
        ((int)customerId.Scale).ShouldBe(0);
        customerId.IsNullable.ShouldBeFalse();
        customerId.IsIdentity.ShouldBeTrue();
        customerId.IsComputed.ShouldBeFalse();
        customerId.IsExcluded.ShouldBeFalse();
        customerId.CollationName.ShouldBeNull();

        var creditLimit = customers.Columns.Single(c => c.Name == "CreditLimit");
        creditLimit.SqlServerTypeName.ShouldBe("decimal");
        ((int)creditLimit.Precision).ShouldBe(18);
        ((int)creditLimit.Scale).ShouldBe(2);
        ((int)creditLimit.MaxLength).ShouldBe(9);
        creditLimit.IsNullable.ShouldBeFalse();

        var name = customers.Columns.Single(c => c.Name == "Name");
        name.SqlServerTypeName.ShouldBe("nvarchar");
        // sys.columns.max_length is bytes, so nvarchar(100) is 200. Import rebuilds column widths from
        // this, and halving or doubling it silently truncates or over-allocates.
        ((int)name.MaxLength).ShouldBe(200);
        name.CollationName.ShouldNotBeNullOrWhiteSpace();

        var notes = customers.Columns.Single(c => c.Name == "Notes");
        ((int)notes.MaxLength).ShouldBe(400);
        notes.IsNullable.ShouldBeTrue();

        var legacyFlags = manifest.Tables.Single(t => t.FullName == "dbo.CustomerProfiles").Columns.Single(c => c.Name == "LegacyFlags");
        legacyFlags.SqlServerTypeName.ShouldBe("sql_variant");
        legacyFlags.IsExcluded.ShouldBeTrue();

        var extendedPrice = manifest.Tables.Single(t => t.FullName == "dbo.OrderLines").Columns.Single(c => c.Name == "ExtendedPrice");
        extendedPrice.SqlServerTypeName.ShouldBe("decimal");
        extendedPrice.IsComputed.ShouldBeTrue();
        extendedPrice.IsExcluded.ShouldBeFalse();

        // NULL, not 0: downstream a vector_base_type of 0 means float32 and 0 dimensions means an empty
        // vector, so a non-vector column reporting 0 is indistinguishable from a real vector column.
        foreach (var column in manifest.Tables.SelectMany(t => t.Columns)) {
            column.VectorBaseType.ShouldBeNull($"{column.Name} is not a vector column but reports a vector base type.");
            column.VectorDimensions.ShouldBeNull($"{column.Name} is not a vector column but reports vector dimensions.");
        }

        // core-commerce carries no vector column, so add one for the second export. Conditional rather
        // than a Skip: everything above holds on an engine without the type and should still run there.
        if (_fixture.SupportsVector) {
            await source.ExecuteSqlAsync("ALTER TABLE dbo.CustomerProfiles ADD Embedding VECTOR(3) NULL");
        }

        // Second, smaller export, no dacpac: the engine-edition stamp lives on the schema package, so
        // without one it has to read back as null rather than as 0.
        await using var dataOnly = new SqliteTempFileHarness();
        var dataOnlyOptions = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = ["dbo.CustomerProfiles"],
            ExcludeColumns = [LegacyFlagsColumn]
        };

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, dataOnly.FilePath, dataOnlyOptions);
        var dataOnlyManifest = await new SqlDataPackReader().ReadManifestAsync(dataOnly.FilePath);

        dataOnlyManifest.ContainsDacpac.ShouldBeFalse();
        dataOnlyManifest.DacpacSchemaScope.ShouldBeNull();
        dataOnlyManifest.SourceEngineEdition.ShouldBeNull();

        if (_fixture.SupportsVector) {
            var embedding = dataOnlyManifest.Tables.Single().Columns.Single(c => c.Name == "Embedding");
            embedding.SqlServerTypeName.ShouldBe("vector");
            embedding.VectorBaseType.ShouldBe(0);
            embedding.VectorDimensions.ShouldBe(3);
            dataOnlyManifest.Tables.Single().Columns.Single(c => c.Name == "DisplayName").VectorDimensions.ShouldBeNull();
        }
    }

    /// <summary>
    /// SourceSchemaHash is stable for an unchanged schema and moves when the schema moves -- and import
    /// never looks at it. The field reads like a safety gate; it is not one, and that gap is pinned here
    /// rather than discovered when a package is loaded into a target it no longer matches.
    /// </summary>
    [Fact]
    public async Task Export_SchemaHash_IsStableAndChangesWithSchema() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerceFixture));
        await using var package = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, package.FilePath, OnlyTables("dbo.GlobalSettings"));
        var packageHash = (await new SqlDataPackReader().ReadManifestAsync(package.FilePath)).SourceSchemaHash;
        packageHash.Length.ShouldBe(64);

        var repeated = await ExportSchemaHashAsync(source, "dbo.GlobalSettings");
        repeated.ShouldBe(packageHash, "Two exports of an unchanged schema produced different hashes.");

        await source.ExecuteSqlAsync("ALTER TABLE dbo.GlobalSettings ALTER COLUMN SettingValue NVARCHAR(400) NULL");
        var afterTypeChange = await ExportSchemaHashAsync(source, "dbo.GlobalSettings");
        afterTypeChange.ShouldNotBe(packageHash, "Widening a column's type left the schema hash unchanged.");

        await source.ExecuteSqlAsync("ALTER TABLE dbo.GlobalSettings ADD Category NVARCHAR(50) NULL");
        var afterAddedColumn = await ExportSchemaHashAsync(source, "dbo.GlobalSettings");
        afterAddedColumn.ShouldNotBe(packageHash, "Adding a column left the schema hash unchanged.");
        afterAddedColumn.ShouldNotBe(afterTypeChange);

        // Vector columns carry base type and dimensions on top of the shape every column has. Two
        // databases rather than an ALTER: dropping and re-adding a column moves its column_id, which
        // would change the hash for a reason that has nothing to do with the vector declaration.
        // VECTOR(3) -> VECTOR(4) also moves max_length, so this proves the declaration reaches the hash
        // without proving which field carries it; isolating that needs a unit test over ComputeSchemaHash.
        if (_fixture.SupportsVector) {
            await using var threeDimensions = await SqlServerFixtureDatabase.CreateAsync(_fixture);
            await threeDimensions.ExecuteSqlAsync("CREATE TABLE dbo.Embeddings (EmbeddingId INT IDENTITY(1,1) PRIMARY KEY, Embedding VECTOR(3) NULL)");
            await using var fourDimensions = await SqlServerFixtureDatabase.CreateAsync(_fixture);
            await fourDimensions.ExecuteSqlAsync("CREATE TABLE dbo.Embeddings (EmbeddingId INT IDENTITY(1,1) PRIMARY KEY, Embedding VECTOR(4) NULL)");

            var threeHash = await ExportSchemaHashAsync(threeDimensions, "dbo.Embeddings");
            var fourHash = await ExportSchemaHashAsync(fourDimensions, "dbo.Embeddings");
            fourHash.ShouldNotBe(threeHash, "A vector column's declared dimensions do not reach the schema hash.");
        }

        // The gap: a target whose schema hashes differently from the package still imports.
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, CoreCommerceFixture);
        await target.ExecuteSqlAsync("ALTER TABLE dbo.GlobalSettings ALTER COLUMN SettingValue NVARCHAR(400) NULL");
        var targetHash = await ExportSchemaHashAsync(target, "dbo.GlobalSettings");
        targetHash.ShouldNotBe(packageHash);

        var importResult = await new SqlDataPackImporter().ImportAsync(package.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(3);
        importResult.Warnings.ShouldNotContain(w => w.Contains("hash", StringComparison.OrdinalIgnoreCase));
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.GlobalSettings")).ShouldBe(3);
    }

    /// <summary>
    /// A planned manifest reports zero exported rows for tables that do have rows. That is the only thing
    /// separating a preflight result from a real export result, and a preflight that quietly counted rows
    /// would read as a completed export.
    /// </summary>
    [Fact]
    public async Task ExportPreflight_ReturnsPlannedManifestWithZeroRows() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerceFixture));
        (await source.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Customers")).ShouldBeGreaterThan(0);

        var result = await new SqlDataPackExporter().PreflightAsync(source.ConnectionString, OnlyTables("dbo.Customers", "dbo.Countries"));

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        result.Manifest.ShouldNotBeNull();
        result.Manifest!.PackageFormatVersion.ShouldBe(SqlDataPackVersion.PackageFormatVersion);
        result.Manifest.ImportOrder.ShouldBe(["dbo.Countries", "dbo.Customers"]);
        result.Manifest.Tables.Select(t => t.FullName).Order(StringComparer.Ordinal).ShouldBe(["dbo.Countries", "dbo.Customers"]);
        result.Manifest.Tables.ShouldAllBe(t => t.ExportedRowCount == 0);
        result.Manifest.Tables.ShouldAllBe(t => t.ExportBatchSize > 0);
        result.Manifest.Tables.Single(t => t.FullName == "dbo.Customers").Columns.Select(c => c.Name).ShouldContain("CreditLimit");
    }

    /// <summary>
    /// Overwrite is a replace-after-success promise, both halves. The success half runs the real
    /// temp-file-then-move including Windows handle pooling -- a move that silently fails ships a corrupt
    /// package with no exception. The failure halves have to leave the previous package byte-identical and
    /// leave nothing behind next to it.
    /// </summary>
    [Fact]
    public async Task Export_OverwriteExistingPackage_ReplacesOnlyAfterSuccess() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(CoreCommerceFixture));
        var previousBytes = Encoding.UTF8.GetBytes("previous package contents");

        await using var replaced = new SqliteTempFileHarness();
        await File.WriteAllBytesAsync(replaced.FilePath, previousBytes);
        var overwriteOptions = OnlyTables("dbo.GlobalSettings");
        overwriteOptions.OverwriteExistingPackage = true;

        var result = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, replaced.FilePath, overwriteOptions);

        result.TableCount.ShouldBe(1);
        result.RowCount.ShouldBe(3);
        var writtenBytes = await File.ReadAllBytesAsync(replaced.FilePath);
        writtenBytes.ShouldNotBe(previousBytes);
        // The move is the step that can silently fail; a package that is not readable end to end here is
        // one that shipped without an exception.
        var replacedManifest = await new SqlDataPackReader().ReadManifestAsync(replaced.FilePath);
        replacedManifest.Tables.Select(t => t.FullName).ShouldBe(["dbo.GlobalSettings"]);
        replacedManifest.Tables[0].ExportedRowCount.ShouldBe(3);
        LeftoverTemporaryPackages(replaced.FilePath).ShouldBeEmpty();

        // Failure before any rows are read: the sql_variant column blocks the plan.
        await using var planFailure = new SqliteTempFileHarness();
        await File.WriteAllBytesAsync(planFailure.FilePath, previousBytes);
        var planFailureOptions = OnlyTables("dbo.CustomerProfiles");
        planFailureOptions.OverwriteExistingPackage = true;

        var planException = await Should.ThrowAsync<SqlDataPackException>(() => new SqlDataPackExporter().ExportAsync(source.ConnectionString, planFailure.FilePath, planFailureOptions));

        planException.Message.ShouldContain("Unsupported included type");
        (await File.ReadAllBytesAsync(planFailure.FilePath)).ShouldBe(previousBytes);
        AssertNoLeftoverTemporaryPackages(planFailure.FilePath);

        // Failure while rows are being written, which is the only path that reaches the temp file. The
        // export's own progress stream cancels it at the first batch boundary, so the SQLite connection
        // is still open when the cleanup path runs.
        await using var copyFailure = new SqliteTempFileHarness();
        await File.WriteAllBytesAsync(copyFailure.FilePath, previousBytes);
        using var cancellation = new CancellationTokenSource();
        var copyFailureOptions = OnlyTables("dbo.Orders");
        copyFailureOptions.OverwriteExistingPackage = true;
        copyFailureOptions.BatchSize = 50;
        copyFailureOptions.Progress = new CancelOnFirstBatch(cancellation);

        var copyException = await Record.ExceptionAsync(() => new SqlDataPackExporter().ExportAsync(source.ConnectionString, copyFailure.FilePath, copyFailureOptions, cancellation.Token));

        copyException.ShouldBeAssignableTo<OperationCanceledException>();
        (await File.ReadAllBytesAsync(copyFailure.FilePath)).ShouldBe(previousBytes);
        AssertNoLeftoverTemporaryPackages(copyFailure.FilePath);
    }

    private static ExportOptions OnlyTables(params string[] tables) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = tables
        };
    }

    private static async Task<string> ExportSchemaHashAsync(SqlServerFixtureDatabase db, params string[] tables) {
        await using var sqlite = new SqliteTempFileHarness();
        await new SqlDataPackExporter().ExportAsync(db.ConnectionString, sqlite.FilePath, OnlyTables(tables));
        return (await new SqlDataPackReader().ReadManifestAsync(sqlite.FilePath)).SourceSchemaHash;
    }

    private static async Task<IReadOnlyList<(string FullName, string SqliteTable)>> ReadDataTableMapAsync(SqliteConnection package) {
        var rows = await package.ReadStringsAsync("SELECT source_schema || '.' || source_table || char(9) || sqlite_table FROM zsdp_tables ORDER BY id");
        return rows.Select(row => row.Split('\t')).Select(parts => (parts[0], parts[1])).ToArray();
    }

    private static string QuoteSqlServer(string fullName) {
        var parts = fullName.Split('.', 2);
        return $"[{parts[0]}].[{parts[1]}]";
    }

    private static string QuoteSqlite(string name) {
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void AssertNoLeftoverTemporaryPackages(string destinationPath) {
        var leftovers = LeftoverTemporaryPackages(destinationPath);
        foreach (var path in leftovers) {
            try {
                File.Delete(path);
            }
            catch (IOException) {
                // Still held open; the assertion below is what reports it.
            }
        }

        leftovers.ShouldBeEmpty($"A failed export left a temporary package next to the destination: {string.Join(", ", leftovers)}");
    }

    private static IReadOnlyList<string> LeftoverTemporaryPackages(string destinationPath) {
        SqliteConnection.ClearAllPools();
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        return Directory.GetFiles(directory, $".{Path.GetFileName(fullPath)}.*.tmp");
    }

    /// <summary>Cancels the export from inside its own progress stream, at the first committed batch.</summary>
    private sealed class CancelOnFirstBatch : IProgress<SqlDataPackProgress> {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnFirstBatch(CancellationTokenSource cancellation) {
            _cancellation = cancellation;
        }

        public void Report(SqlDataPackProgress value) {
            if (value.Kind == SqlDataPackProgressKind.RowsCopied) {
                _cancellation.Cancel();
            }
        }
    }
}
