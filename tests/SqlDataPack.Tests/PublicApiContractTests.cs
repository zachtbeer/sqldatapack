using Shouldly;
using SqlDataPack;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

// Deliberately outside the SqlDataPack.* tree. Inside it the enclosing namespace resolves the
// facade for free, which is exactly what hid the SqlDataPack/SqlDataPack collision: a consumer
// writing `SqlDataPack.ExportAsync` bound the namespace and got CS0234, while every reflection
// assertion kept passing. This file compiling is the assertion.
namespace ConsumerCompilationGuard;

/// <summary>
/// Guards the two things a consumer sees and reflection cannot: that every public entry point binds
/// by name and exact signature from outside the library's namespace, and that the option presets hold
/// their documented values and hand out fresh instances.
/// </summary>
public sealed class PublicApiContractTests {
    [Fact]
    public void EveryPublicEntryPoint_BindsFromAConsumerNamespace() {
        // Method groups into explicitly typed delegates, so the compiler checks parameter order too.
        // Nothing is invoked; no connection is opened.
        Func<string, string, ExportOptions?, CancellationToken, Task<SqlDataPackResult>> export = SqlData.ExportAsync;
        Func<string, string, ImportOptions?, CancellationToken, Task<SqlDataPackResult>> import = SqlData.ImportAsync;

        var exporter = new SqlDataPackExporter();
        Func<string, string, ExportOptions?, CancellationToken, Task<SqlDataPackResult>> exporterExport = exporter.ExportAsync;
        Func<string, ExportOptions?, CancellationToken, Task<SqlDataPackPreflightResult>> exporterPreflight = exporter.PreflightAsync;

        var importer = new SqlDataPackImporter();
        Func<string, string, ImportOptions?, CancellationToken, Task<SqlDataPackResult>> importerImport = importer.ImportAsync;
        Func<string, string, ImportOptions?, CancellationToken, Task<SqlDataPackPreflightResult>> importerPreflight = importer.PreflightAsync;

        Func<string, CancellationToken, Task<SqlDataPackManifest>> readManifest = new SqlDataPackReader().ReadManifestAsync;

        export.ShouldNotBeNull();
        import.ShouldNotBeNull();
        exporterExport.ShouldNotBeNull();
        exporterPreflight.ShouldNotBeNull();
        importerImport.ShouldNotBeNull();
        importerPreflight.ShouldNotBeNull();
        readManifest.ShouldNotBeNull();
    }

    [Fact]
    public void PublicModelRecords_DoNotExposeDeconstruct() {
        // A generated Deconstruct freezes a member list at the moment it is written. The first time a
        // manifest gains a field, deconstruction silently omits it, and extending the positional
        // parameter list instead is a constructor break that needs a major version. The public records
        // therefore declare an explicit constructor and init properties. Keep it that way: new members
        // go on as init properties, which is additive in a minor release.
        var models = typeof(SqlDataPackResult).Assembly.GetExportedTypes().Where(type => type.Namespace == "SqlDataPack.Models").ToArray();

        // Guards against the scan silently matching nothing if the namespace is ever renamed.
        models.ShouldContain(typeof(SqlDataPackManifest));
        models.ShouldContain(typeof(PerTableWhereClause));

        var offenders = models.Where(type => type.GetMethod("Deconstruct") is not null).Select(type => type.Name).Order(StringComparer.Ordinal).ToArray();

        offenders.ShouldBeEmpty($"Positional record(s) reintroduced in SqlDataPack.Models: {string.Join(", ", offenders)}. " + "Give them an explicit constructor and { get; init; } properties instead.");
    }

    [Fact]
    public void ExportOptions_Default_HasFrozenStableValues() {
        // Literals on purpose: .Default is a documented value-stability promise, so this fails if it drifts.
        var options = ExportOptions.Default;

        options.TableSelection.ShouldBe(ExportTableSelectionMode.AllExcept);
        options.Tables.ShouldBeEmpty();
        options.ExcludeColumns.ShouldBeEmpty();
        options.GlobalWhereClauses.ShouldBeEmpty();
        options.PerTableWhereClauses.ShouldBeEmpty();
        options.DataTablePrefix.ShouldBeNull();
        options.BatchSize.ShouldBe(1_000);
        options.AdaptiveBatchingEnabled.ShouldBeTrue();
        options.LargeTableThresholdBytes.ShouldBe(50L * 1024 * 1024);
        options.LargeTableRowThreshold.ShouldBe(100_000);
        options.LargeTableBatchSize.ShouldBe(250);
        options.MaxBatchBytes.ShouldBe(4L * 1024 * 1024);
        options.CommandTimeout.ShouldBeNull();
        options.Progress.ShouldBeNull();
        options.Logger.ShouldBeNull();
        options.OverwriteExistingPackage.ShouldBeFalse();
        options.ExcludeSsmsDiagrams.ShouldBeTrue();
        options.SchemaCaptureMode.ShouldBe(SchemaCaptureMode.None);
        options.DacpacCaptureOptions.ShouldNotBeNull();
    }

    [Fact]
    public void ImportOptions_Default_HasFrozenStableValues() {
        var options = ImportOptions.Default;

        options.BatchSize.ShouldBe(1_000);
        options.AdaptiveBatchingEnabled.ShouldBeTrue();
        options.LargeTableThresholdBytes.ShouldBe(50L * 1024 * 1024);
        options.LargeTableRowThreshold.ShouldBe(100_000);
        options.LargeTableBatchSize.ShouldBe(250);
        options.MaxBatchBytes.ShouldBe(4L * 1024 * 1024);
        options.ValidationCommandTimeout.ShouldBeNull();
        options.FailOnLossyTypeMismatch.ShouldBeFalse();
        options.BulkCopyTimeout.ShouldBeNull();
        options.Progress.ShouldBeNull();
        options.Logger.ShouldBeNull();
        options.SchemaDeploymentMode.ShouldBe(SchemaDeploymentMode.None);
        options.DacpacDeploymentOptions.ShouldNotBeNull();

        // These two are the only thing keeping a Default caller from silently wrong AS OF results.
        options.SuspendTemporalSystemVersioning.ShouldBeTrue();
        options.TemporalDataConsistencyCheck.ShouldBeTrue();
    }

    [Fact]
    public void DacpacOptions_Default_HaveFrozenStableValues() {
        // Scope is persisted by name, not by ordinal, so the numbers themselves are not part of the
        // format. What is contractual is which member sits at zero: an unset or default-constructed
        // enum must land on the mode that captures nothing, deploys nothing, and narrows nothing.
        default(SchemaCaptureMode).ShouldBe(SchemaCaptureMode.None);
        default(SchemaDeploymentMode).ShouldBe(SchemaDeploymentMode.None);
        default(DacpacSchemaScope).ShouldBe(DacpacSchemaScope.Database);

        var capture = DacpacCaptureOptions.Default;
        capture.SchemaScope.ShouldBe(DacpacSchemaScope.Database);
        capture.ExtractReferencedServerScopedElements.ShouldBeFalse();
        capture.ExtractApplicationScopedObjectsOnly.ShouldBeFalse();
        capture.IgnorePermissions.ShouldBeTrue();
        capture.IgnoreUserLoginMappings.ShouldBeTrue();
        capture.VerifyExtraction.ShouldBeFalse();

        var deployment = DacpacDeploymentOptions.Default;
        deployment.AllowIncompatiblePlatform.ShouldBeFalse();
        deployment.BlockOnPossibleDataLoss.ShouldBeTrue();
        deployment.AllowObjectDrops.ShouldBeFalse();
        deployment.DeployUsers.ShouldBeFalse();
        deployment.DeployLogins.ShouldBeFalse();
        deployment.DeployPermissions.ShouldBeFalse();
        deployment.DeployRoleMembership.ShouldBeFalse();
        deployment.DeployDatabaseFiles.ShouldBeFalse();
        deployment.DeployDatabaseOptions.ShouldBeFalse();
        deployment.AdaptAzureSourceForOnPremTarget.ShouldBeTrue();
        deployment.VerifyDeployment.ShouldBeTrue();
    }

    [Fact]
    public void Latest_RaisesThroughputKnobsAndKeepsTheLargeTableSafetyNet() {
        ExportOptions.Latest.BatchSize.ShouldBeGreaterThan(ExportOptions.Default.BatchSize);
        ExportOptions.Latest.MaxBatchBytes.ShouldBeGreaterThan(ExportOptions.Default.MaxBatchBytes);
        ImportOptions.Latest.BatchSize.ShouldBeGreaterThan(ImportOptions.Default.BatchSize);
        ImportOptions.Latest.MaxBatchBytes.ShouldBeGreaterThan(ImportOptions.Default.MaxBatchBytes);

        // The large-table safety net stays exactly where Default leaves it, so huge tables remain
        // memory-safe under Latest. Read from the constants, never a duplicated literal.
        var export = ExportOptions.Latest;
        export.AdaptiveBatchingEnabled.ShouldBeTrue();
        export.LargeTableThresholdBytes.ShouldBe(BatchPlanner.DefaultLargeTableThresholdBytes);
        export.LargeTableRowThreshold.ShouldBe(BatchPlanner.DefaultLargeTableRowThreshold);
        export.LargeTableBatchSize.ShouldBe(BatchPlanner.DefaultLargeTableBatchSize);
        export.ExcludeSsmsDiagrams.ShouldBeTrue();

        var import = ImportOptions.Latest;
        import.AdaptiveBatchingEnabled.ShouldBeTrue();
        import.LargeTableThresholdBytes.ShouldBe(BatchPlanner.DefaultLargeTableThresholdBytes);
        import.LargeTableRowThreshold.ShouldBe(BatchPlanner.DefaultLargeTableRowThreshold);
        import.LargeTableBatchSize.ShouldBe(BatchPlanner.DefaultLargeTableBatchSize);
        import.SuspendTemporalSystemVersioning.ShouldBeTrue();
        import.TemporalDataConsistencyCheck.ShouldBeTrue();
    }

    [Fact]
    public void Presets_ReturnFreshInstancesThatDoNotLeakMutations() {
        var firstExport = ExportOptions.Default;
        var secondExport = ExportOptions.Default;
        firstExport.ShouldNotBeSameAs(secondExport);
        firstExport.BatchSize = 42;
        firstExport.Tables.Add("dbo.Mutated");
        firstExport.ExcludeColumns.Add("dbo.Mutated.Secret");
        firstExport.GlobalWhereClauses.Add(new GlobalWhereClause("TenantId", "TenantId = 1"));
        secondExport.BatchSize.ShouldNotBe(42);
        secondExport.Tables.ShouldBeEmpty();
        secondExport.ExcludeColumns.ShouldBeEmpty();
        secondExport.GlobalWhereClauses.ShouldBeEmpty();

        // ImportOptions carries no collections; the nested dacpac options are the shareable state.
        var firstImport = ImportOptions.Default;
        var secondImport = ImportOptions.Default;
        firstImport.ShouldNotBeSameAs(secondImport);
        firstImport.BatchSize = 42;
        firstImport.SuspendTemporalSystemVersioning = false;
        firstImport.DacpacDeploymentOptions.BlockOnPossibleDataLoss = false;
        secondImport.BatchSize.ShouldNotBe(42);
        secondImport.SuspendTemporalSystemVersioning.ShouldBeTrue();
        secondImport.DacpacDeploymentOptions.BlockOnPossibleDataLoss.ShouldBeTrue();

        var firstCapture = DacpacCaptureOptions.Default;
        var secondCapture = DacpacCaptureOptions.Default;
        firstCapture.ShouldNotBeSameAs(secondCapture);
        firstCapture.IgnorePermissions = false;
        secondCapture.IgnorePermissions.ShouldBeTrue();

        var firstDeployment = DacpacDeploymentOptions.Default;
        var secondDeployment = DacpacDeploymentOptions.Default;
        firstDeployment.ShouldNotBeSameAs(secondDeployment);
        firstDeployment.AllowObjectDrops = true;
        secondDeployment.AllowObjectDrops.ShouldBeFalse();
    }
}
