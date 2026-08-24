using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal static class DacpacSchemaManager {
    public static async Task<SchemaPackage> ExtractAsync(string connectionString, ExportPlan plan, DacpacCaptureOptions options, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        var databaseName = GetDatabaseName(connectionString, "capture schema");
        var path = CreateTemporaryDacpacPath();
        var reducedPath = options.SchemaScope == DacpacSchemaScope.SelectedExportTables ? CreateTemporaryDacpacPath() : null;

        try {
            var services = new DacServices(connectionString);
            var applicationVersion = typeof(SqlDataPackVersion).Assembly.GetName().Version ?? new Version(1, 0, 0);
            var extractOptions = CreateExtractOptions(options);

            // Source-side platform stamp. Soft-fails to null so an unusual provider doesn't kill the export;
            // import-side decision tree treats null as "unknown" and falls back to the conservative path
            // (= no model rewrite, deploy as-is).
            int? sourceEngineEdition = null;
            try {
                sourceEngineEdition = await ReadEngineEditionAsync(connectionString, cancellationToken);
            }
            catch (Exception probeException) when (probeException is not OperationCanceledException) {
                // Swallow — leaving the stamp null is preferable to failing the whole export over a
                // metadata probe.
            }

            await Task.Run(() => services.Extract(path, databaseName, databaseName, applicationVersion, "Extracted by SqlDataPack.", tables: null, extractOptions: extractOptions, cancellationToken: cancellationToken), cancellationToken);

            var packagePath = options.SchemaScope switch {
                DacpacSchemaScope.Database => path,
                DacpacSchemaScope.SelectedExportTables => await BuildSelectedTablesPackageAsync(path, reducedPath ?? throw new InvalidOperationException("Reduced dacpac path was not created."), databaseName, applicationVersion, plan, cancellationToken),
                _ => throw new SqlDataPackException($"Dacpac schema scope '{options.SchemaScope}' is not supported.")
            };

            var payload = await File.ReadAllBytesAsync(packagePath, cancellationToken);
            return new SchemaPackage("dacpac", $"{databaseName}.dacpac", Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), DateTimeOffset.UtcNow, databaseName, typeof(DacServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(DacServices).Assembly.GetName().Version?.ToString(), options.SchemaScope, payload, sourceEngineEdition);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SqlDataPackException) {
            throw new SqlDataPackException($"Failed to extract dacpac schema from source database '{databaseName}'.", exception);
        }
        finally {
            DeleteTemporaryFile(path);
            if (reducedPath is not null) {
                DeleteTemporaryFile(reducedPath);
            }
        }
    }

    public static async Task DeployAsync(string connectionString, SchemaPackage package, DacpacDeploymentOptions options, bool allowDacpacObjectDrops, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.Equals(package.PackageType, "dacpac", StringComparison.OrdinalIgnoreCase)) {
            throw new SqlDataPackException($"Schema package type '{package.PackageType}' is not supported for dacpac deployment.");
        }

        var targetDatabaseName = GetDatabaseName(connectionString, "deploy dacpac schema");
        var actualHash = Convert.ToHexString(SHA256.HashData(package.Payload)).ToLowerInvariant();
        if (!string.Equals(actualHash, package.PackageSha256, StringComparison.OrdinalIgnoreCase)) {
            throw new SqlDataPackException("SQLite package is invalid: stored dacpac payload hash does not match metadata.");
        }

        if (package.SchemaScope == DacpacSchemaScope.SelectedExportTables && options.AllowObjectDrops) {
            throw new SqlDataPackException("DacpacDeploymentOptions.AllowObjectDrops cannot be used with a selected-table dacpac schema package because unrelated target objects would be compared against a reduced source model.");
        }

        var path = CreateTemporaryDacpacPath();
        try {
            await File.WriteAllBytesAsync(path, package.Payload, cancellationToken);

            if (!options.DeployDatabaseOptions && options.AdaptAzureSourceForOnPremTarget) {
                int targetEngineEdition;
                try {
                    targetEngineEdition = await ReadEngineEditionAsync(connectionString, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not SqlDataPackException) {
                    throw new SqlDataPackException($"Failed to probe target SQL Server engine edition before dacpac deploy to '{targetDatabaseName}'. " + "Set DacpacDeploymentOptions.AdaptAzureSourceForOnPremTarget = false (or DeployDatabaseOptions = true) to skip the probe and deploy the source model as-is.", exception);
                }

                if (ShouldAdaptAzureSourceForOnPremTarget(package.SourceEngineEdition, targetEngineEdition)) {
                    NeutralizeForNonAzureSqlTarget(path);
                }
            }

            var services = new DacServices(connectionString);
            using var dacpac = DacPackage.Load(path);
            var deployOptions = CreateDeployOptions(options, allowDacpacObjectDrops);

            await Task.Run(() => services.Deploy(dacpac, targetDatabaseName, upgradeExisting: true, options: deployOptions, cancellationToken: cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SqlDataPackException) {
            throw new SqlDataPackException($"Failed to deploy dacpac schema to target database '{targetDatabaseName}'.", exception);
        }
        finally {
            DeleteTemporaryFile(path);
        }
    }

    private static Task<string> BuildSelectedTablesPackageAsync(string sourcePath, string destinationPath, string databaseName, Version applicationVersion, ExportPlan plan, CancellationToken cancellationToken) {
        return Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested();
            using var sourceModel = new TSqlModel(sourcePath, DacSchemaModelStorageType.Memory, cancellationToken);
            using var selectedModel = new TSqlModel(sourceModel.Version, sourceModel.CopyModelOptions());
            var selectedTableNames = plan.Tables.Select(t => t.Name.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scripts = new List<(string Name, string Script)>();

            foreach (var schema in plan.Tables.Select(t => t.Name.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) {
                var schemaObject = sourceModel.GetObject(ModelSchema.Schema, new ObjectIdentifier([schema]), DacQueryScopes.UserDefined);
                if (TryGetScript(schemaObject, out var schemaScript)) {
                    scripts.Add(($"schema:{schema}", schemaScript));
                }
            }

            foreach (var table in plan.Tables.OrderBy(t => t.Name.FullName, StringComparer.OrdinalIgnoreCase)) {
                cancellationToken.ThrowIfCancellationRequested();
                var tableObject = sourceModel.GetObject(ModelSchema.Table, new ObjectIdentifier([table.Name.Schema, table.Name.Name]), DacQueryScopes.UserDefined);
                if (tableObject is null) {
                    throw new SqlDataPackException($"Failed to build selected-table dacpac because source model table '{table.Name.FullName}' was not found.");
                }

                AddScriptableDependencies(tableObject, selectedTableNames, scripts);
                AddScriptableChildDependencies(tableObject, selectedTableNames, scripts);
                if (!tableObject.TryGetScript(out var tableScript)) {
                    throw new SqlDataPackException($"Failed to build selected-table dacpac because source model table '{table.Name.FullName}' could not be scripted.");
                }

                scripts.Add(($"table:{table.Name.FullName}", tableScript));
                AddScriptableChildren(tableObject, selectedTableNames, scripts);
            }

            foreach (var script in scripts.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) {
                selectedModel.AddObjects(script.Script);
            }

            var metadata = new PackageMetadata {
                Name = databaseName,
                Version = applicationVersion.ToString(),
                Description = "Selected export-table schema extracted by SqlDataPack."
            };
            DacPackageExtensions.BuildPackage(destinationPath, selectedModel, metadata);
            return destinationPath;
        }, cancellationToken);
    }

    private static void AddScriptableDependencies(TSqlObject tableObject, ISet<string> selectedTableNames, List<(string Name, string Script)> scripts) {
        foreach (var dependency in tableObject.GetReferenced(DacQueryScopes.UserDefined)) {
            if (dependency.ObjectType == ModelSchema.Table) {
                var tableName = ToFullName(dependency.Name);
                if (selectedTableNames.Contains(tableName)) {
                    continue;
                }
            }

            if (dependency.ObjectType == ModelSchema.Schema) {
                continue;
            }

            if (TryGetScript(dependency, out var script)) {
                scripts.Add(($"{dependency.ObjectType}:{dependency.Name}", script));
            }
        }
    }

    private static void AddScriptableChildDependencies(TSqlObject tableObject, ISet<string> selectedTableNames, List<(string Name, string Script)> scripts) {
        foreach (var child in tableObject.GetChildren(DacQueryScopes.UserDefined)) {
            if (child.ObjectType == ModelSchema.ForeignKeyConstraint && ReferencesUnselectedTable(child, selectedTableNames)) {
                continue;
            }

            AddScriptableDependencies(child, selectedTableNames, scripts);
        }
    }

    private static void AddScriptableChildren(TSqlObject tableObject, ISet<string> selectedTableNames, List<(string Name, string Script)> scripts) {
        foreach (var child in tableObject.GetChildren(DacQueryScopes.UserDefined)) {
            if (child.ObjectType == ModelSchema.ForeignKeyConstraint && ReferencesUnselectedTable(child, selectedTableNames)) {
                continue;
            }

            if (TryGetScript(child, out var script)) {
                scripts.Add(($"{child.ObjectType}:{child.Name}", script));
            }
        }
    }

    private static bool ReferencesUnselectedTable(TSqlObject sqlObject, ISet<string> selectedTableNames) {
        return sqlObject.GetReferenced(DacQueryScopes.UserDefined).Where(referenced => referenced.ObjectType == ModelSchema.Table).Select(referenced => ToFullName(referenced.Name)).Any(tableName => !selectedTableNames.Contains(tableName));
    }

    private static bool TryGetScript(TSqlObject? sqlObject, out string script) {
        script = string.Empty;
        return sqlObject is not null && sqlObject.TryGetScript(out script) && !string.IsNullOrWhiteSpace(script);
    }

    private static string ToFullName(ObjectIdentifier identifier) {
        return string.Join(".", identifier.Parts.Select(p => p.Trim('[', ']')));
    }

    internal static DacExtractOptions CreateExtractOptions(DacpacCaptureOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        return new DacExtractOptions {
            ExtractAllTableData = false,
            ExtractReferencedServerScopedElements = options.ExtractReferencedServerScopedElements,
            ExtractApplicationScopedObjectsOnly = options.ExtractApplicationScopedObjectsOnly,
            IgnorePermissions = options.IgnorePermissions,
            IgnoreUserLoginMappings = options.IgnoreUserLoginMappings,
            VerifyExtraction = options.VerifyExtraction
        };
    }

    internal static DacDeployOptions CreateDeployOptions(DacpacDeploymentOptions options, bool allowDacpacObjectDrops = false) {
        ArgumentNullException.ThrowIfNull(options);

        var excludedObjectTypes = new List<ObjectType>();
        if (!options.DeployUsers) {
            excludedObjectTypes.Add(ObjectType.Users);
        }

        if (!options.DeployLogins) {
            excludedObjectTypes.Add(ObjectType.Logins);
            excludedObjectTypes.Add(ObjectType.LinkedServerLogins);
        }

        if (!options.DeployPermissions) {
            excludedObjectTypes.Add(ObjectType.Permissions);
        }

        if (!options.DeployRoleMembership) {
            excludedObjectTypes.Add(ObjectType.RoleMembership);
            excludedObjectTypes.Add(ObjectType.ServerRoleMembership);
        }

        if (!options.DeployDatabaseFiles) {
            excludedObjectTypes.Add(ObjectType.Files);
            excludedObjectTypes.Add(ObjectType.Filegroups);
        }

        // DacFx defaults the per-object-type drop flags to true, so leaving them alone would drop
        // indexes, constraints, triggers and statistics on tables the package does contain, even
        // with AllowObjectDrops off. Tie them all to the one flag the caller actually set.
        var allowDrops = options.AllowObjectDrops || allowDacpacObjectDrops;

        return new DacDeployOptions {
            AllowIncompatiblePlatform = options.AllowIncompatiblePlatform,
            BlockOnPossibleDataLoss = options.BlockOnPossibleDataLoss,
            DropConstraintsNotInSource = allowDrops,
            DropDmlTriggersNotInSource = allowDrops,
            DropExtendedPropertiesNotInSource = allowDrops,
            DropIndexesNotInSource = allowDrops,
            DropObjectsNotInSource = allowDrops,
            DropStatisticsNotInSource = allowDrops,
            ExcludeObjectTypes = excludedObjectTypes.ToArray(),
            IgnoreFileAndLogFilePath = !options.DeployDatabaseFiles,
            IgnoreFilegroupPlacement = !options.DeployDatabaseFiles,
            IgnoreFileSize = !options.DeployDatabaseFiles,
            IncludeTransactionalScripts = true,
            ScriptDatabaseOptions = options.DeployDatabaseOptions,
            VerifyDeployment = options.VerifyDeployment
        };
    }

    private static string GetDatabaseName(string connectionString, string operation) {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog)) {
            throw new SqlDataPackException($"Cannot {operation} because the SQL Server connection string does not specify a database.");
        }

        return builder.InitialCatalog;
    }

    private static string CreateTemporaryDacpacPath() {
        return Path.Combine(Path.GetTempPath(), $"zsdp-schema-{Guid.NewGuid():N}.dacpac");
    }

    private static void DeleteTemporaryFile(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch {
            // Best effort cleanup only; preserve the operation failure.
        }
    }

    private static async Task<int> ReadEngineEditionAsync(string connectionString, CancellationToken cancellationToken) {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int value ? value : throw new SqlDataPackException("Failed to determine SQL Server engine edition; SERVERPROPERTY('EngineEdition') returned no value.");
    }

    // EngineEdition values that map to Azure-hosted SQL platforms whose extracts can carry contained users
    // and CONTAINMENT = PARTIAL implications that on-prem SQL Server cannot satisfy without sp_configure
    // tweaks. 5 = Azure SQL Database, 8 = Azure SQL Managed Instance, 11 = Azure SQL Edge,
    // 12 = Azure Synapse Analytics (SQL pool).
    private static bool IsAzureSqlEngineEdition(int engineEdition) => engineEdition is 5 or 8 or 11 or 12;

    // Decision tree for the cross-platform model rewrite. Pure function so it can be unit-tested without
    // DacFx or a live server.
    //   - Unknown source (null) => fall back to "target is non-Azure => rewrite". This preserves the
    //     pre-format-v4 behaviour for legacy callers that construct SchemaPackage directly without a
    //     source stamp (tests, in-process API users).
    //   - Known Azure source + non-Azure target => rewrite.
    //   - Any other combination => skip (Azure->Azure is fine as-is; on-prem source never needs the rewrite).
    internal static bool ShouldAdaptAzureSourceForOnPremTarget(int? sourceEngineEdition, int targetEngineEdition) {
        var targetIsAzure = IsAzureSqlEngineEdition(targetEngineEdition);
        if (targetIsAzure) {
            return false;
        }

        if (sourceEngineEdition is null) {
            return true;
        }

        return IsAzureSqlEngineEdition(sourceEngineEdition.Value);
    }

    // Azure SQL DB-sourced dacpacs trigger DacFx to emit an ALTER DATABASE ... SET CONTAINMENT = PARTIAL
    // prerequisite at the start of the deploy script. That ALTER fails with Msg 12824 on targets where
    // 'contained database authentication' sp_configure is 0 (the default for on-prem / containerised
    // SQL Server). ScriptDatabaseOptions=false / ExcludeObjectTypes do not suppress it because the
    // prerequisite is derived from the *model contents*, not from comparison-driven options.
    //
    // Two model-level conditions can drive that inference:
    //   1. SqlDatabaseOptions has an explicit Containment property (older Azure SQL extracts).
    //   2. The model contains SqlUser elements with AuthenticationType = 2 (contained password user)
    //      or 4 (External provider / Entra ID), even when ObjectType.Users is excluded from deploy.
    //
    // Both are rewritten on the temp deploy copy so DacFx no longer scripts the prerequisite. Contained /
    // external SqlUser elements are converted to WITHOUT LOGIN rather than removed so any inbound
    // references (Permissions, RoleMembership, etc.) in the model remain valid during DacFx load.
    // DacpacEditor recomputes the model.xml checksum in Origin.xml so the package still verifies.
    private static void NeutralizeForNonAzureSqlTarget(string dacpacPath) {
        DacpacEditor.Edit(dacpacPath, context => {
            context.MutateXml("model.xml", document => {
                var removedContainment = TryRemoveDatabaseContainmentProperty(document);
                var rewroteUsers = TryRewriteContainedUsersAsWithoutLogin(document);
                return removedContainment || rewroteUsers;
            });
        });
    }

    private static bool TryRemoveDatabaseContainmentProperty(XDocument modelDocument) {
        var containmentProperties = modelDocument.Descendants().Where(e => e.Name.LocalName == "Element" && string.Equals((string?)e.Attribute("Type"), "SqlDatabaseOptions", StringComparison.Ordinal)).SelectMany(e => e.Elements()).Where(p => p.Name.LocalName == "Property" && string.Equals((string?)p.Attribute("Name"), "Containment", StringComparison.Ordinal)).ToList();

        if (containmentProperties.Count == 0) {
            return false;
        }

        foreach (var property in containmentProperties) {
            property.Remove();
        }

        return true;
    }

    private static bool TryRewriteContainedUsersAsWithoutLogin(XDocument modelDocument) {
        var ns = modelDocument.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var changed = false;

        var userElements = modelDocument.Descendants().Where(e => e.Name.LocalName == "Element" && string.Equals((string?)e.Attribute("Type"), "SqlUser", StringComparison.Ordinal)).ToList();

        foreach (var user in userElements) {
            var properties = user.Elements().Where(p => p.Name.LocalName == "Property").ToList();

            var authType = properties.FirstOrDefault(p => string.Equals((string?)p.Attribute("Name"), "AuthenticationType", StringComparison.Ordinal));
            if (authType is null) {
                continue;
            }

            // AuthenticationType: 1 = Windows, 2 = Password (contained), 3 = Asymmetric Key, 4 = External (Entra).
            // Both 2 and 4 drive DacFx to require CONTAINMENT = PARTIAL on the target. Windows / Asymmetric
            // are fine on on-prem SQL so we leave them alone.
            var value = (string?)authType.Attribute("Value");
            if (!string.Equals(value, "2", StringComparison.Ordinal) && !string.Equals(value, "4", StringComparison.Ordinal)) {
                continue;
            }

            authType.Remove();
            foreach (var prop in properties) {
                var propName = (string?)prop.Attribute("Name");
                if (string.Equals(propName, "Password", StringComparison.Ordinal) || string.Equals(propName, "Sid", StringComparison.Ordinal)) {
                    prop.Remove();
                }
            }

            var hasWithoutLogin = user.Elements().Any(p => p.Name.LocalName == "Property" && string.Equals((string?)p.Attribute("Name"), "IsWithoutLogin", StringComparison.Ordinal));
            if (!hasWithoutLogin) {
                user.AddFirst(new XElement(ns + "Property", new XAttribute("Name", "IsWithoutLogin"), new XAttribute("Value", "True")));
            }

            changed = true;
        }

        return changed;
    }
}
