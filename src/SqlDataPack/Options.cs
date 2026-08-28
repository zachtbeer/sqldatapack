using Microsoft.Extensions.Logging;
using SqlDataPack.Internal;
using SqlDataPack.Transformations;

namespace SqlDataPack.Models;

/// <summary>
/// Selects whether export captures SQL Server schema alongside data. Defaults to <see cref="None"/> (data-only).
/// </summary>
public enum SchemaCaptureMode {
    /// <summary>
    /// Skips schema capture; the package contains data only. This is the default.
    /// </summary>
    None = 0,

    /// <summary>
    /// Extracts the source database schema as a dacpac and embeds it in the SQLite package, so import can recreate the schema.
    /// </summary>
    Dacpac = 1
}

/// <summary>
/// Selects whether import deploys the package's schema before loading data. Defaults to <see cref="None"/> (assume schema already exists).
/// </summary>
public enum SchemaDeploymentMode {
    /// <summary>
    /// Skips schema deployment; the target database must already contain matching tables. This is the default.
    /// </summary>
    None = 0,

    /// <summary>
    /// Deploys the dacpac embedded in the SQLite package against the target before importing data.
    /// </summary>
    DeployDacpac = 1
}

/// <summary>
/// Selects which schema objects an export-time dacpac extraction includes. Defaults to <see cref="Database"/> (whole database model).
/// </summary>
public enum DacpacSchemaScope {
    /// <summary>
    /// Captures the entire source database schema model. This is the default and matches DacFx's standard extract behavior.
    /// </summary>
    Database = 0,

    /// <summary>
    /// Captures only the tables chosen by the export plan plus the dependencies DacFx needs to script them — produces a smaller, plan-scoped dacpac.
    /// </summary>
    SelectedExportTables = 1
}

/// <summary>
/// Selects how <see cref="ExportOptions.Tables"/> patterns filter the export. Defaults to <see cref="AllExcept"/>.
/// </summary>
public enum ExportTableSelectionMode {
    /// <summary>
    /// Exports every user table except those matching <see cref="ExportOptions.Tables"/> (i.e. patterns act as an exclusion list). This is the default.
    /// </summary>
    AllExcept = 0,

    /// <summary>
    /// Exports only the user tables matching <see cref="ExportOptions.Tables"/> (i.e. patterns act as an inclusion list).
    /// </summary>
    Only = 1
}

/// <summary>
/// Selects what an import does when a package holds a different number of rows than the export recorded. Defaults to <see cref="Warn"/> (import the package as it stands and report the difference).
/// </summary>
public enum RowCountDrift {
    /// <summary>
    /// Imports the rows the package actually holds and records a warning naming each table whose count moved. This is the default.
    /// </summary>
    Warn = 0,

    /// <summary>
    /// Rejects the package before any row is written, naming every table whose count moved.
    /// </summary>
    Fail = 1
}

/// <summary>
/// Tunes how the dacpac is extracted when <see cref="ExportOptions.SchemaCaptureMode"/> is <see cref="SchemaCaptureMode.Dacpac"/>.
/// </summary>
public sealed class DacpacCaptureOptions {
    /// <summary>
    /// Returns a new <see cref="DacpacCaptureOptions"/> populated with the documented defaults — a convenient starting point to tweak.
    /// Each access returns a fresh instance, so mutating the returned object never affects subsequent callers.
    /// Value-identical to <c>new DacpacCaptureOptions()</c>; the property initializers below are the single source of truth.
    /// </summary>
    public static DacpacCaptureOptions Default => new();

    /// <summary>
    /// Controls which schema objects are extracted into the dacpac. Defaults to <see cref="DacpacSchemaScope.Database"/> (full database model).
    /// </summary>
    public DacpacSchemaScope SchemaScope { get; set; } = DacpacSchemaScope.Database;

    /// <summary>
    /// Includes server-scoped objects referenced by the database (e.g. logins) in the extracted dacpac. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ExtractReferencedServerScopedElements { get; set; }

    /// <summary>
    /// Restricts extraction to objects owned by the source application, skipping shared or system objects. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ExtractApplicationScopedObjectsOnly { get; set; }

    /// <summary>
    /// Strips GRANT/DENY/REVOKE statements from the extracted dacpac so the captured schema does not carry environment-specific permissions. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IgnorePermissions { get; set; } = true;

    /// <summary>
    /// Strips user-to-login mappings from the extracted dacpac so the captured schema is portable across servers. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IgnoreUserLoginMappings { get; set; } = true;

    /// <summary>
    /// Runs DacFx's post-extraction model verification. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification only <em>validates</em> the model DacFx has already built — it does not change which objects are extracted or the resulting dacpac bytes, and it is independent of the deploy-side <see cref="DacpacDeploymentOptions.VerifyDeployment"/> check. Leaving it off therefore captures exactly the same schema while avoiding <c>SQL71501</c> "unresolved reference" failures on procedures, views, and functions that work in the live source database but defeat DacFx's stricter static validator — ambiguous unqualified columns in multi-table joins, cross-database or three-part names, temp tables, and other deferred-resolvable references. These are common in real, legacy databases and create and run correctly on the target via SQL Server's own binding / deferred name resolution.
    /// </para>
    /// <para>
    /// Set to <see langword="true"/> to fail the export early when a captured object has a genuinely unresolvable reference (for example, a column that no longer exists on an existing table), rather than discovering it at deploy time. Note that verification has no per-rule suppression: enabling it re-enables <em>all</em> model-validation rules, so a single benign false positive will block the whole export.
    /// </para>
    /// </remarks>
    public bool VerifyExtraction { get; set; }
}

/// <summary>
/// Tunes how the package's embedded dacpac is deployed when <see cref="ImportOptions.SchemaDeploymentMode"/> is <see cref="SchemaDeploymentMode.DeployDacpac"/>.
/// </summary>
public sealed class DacpacDeploymentOptions {
    /// <summary>
    /// Returns a new <see cref="DacpacDeploymentOptions"/> populated with the documented defaults — a convenient starting point to tweak.
    /// Each access returns a fresh instance, so mutating the returned object never affects subsequent callers.
    /// Value-identical to <c>new DacpacDeploymentOptions()</c>; the property initializers below are the single source of truth.
    /// </summary>
    public static DacpacDeploymentOptions Default => new();

    /// <summary>
    /// Allows DacFx to deploy even when the source and target SQL platforms differ (e.g. on-prem dacpac to Azure SQL). Defaults to <see langword="false"/> (fail-fast on platform mismatch).
    /// </summary>
    public bool AllowIncompatiblePlatform { get; set; }

    /// <summary>
    /// Blocks deployment when DacFx detects an operation that could lose existing data (e.g. dropping a populated column). Defaults to <see langword="true"/>; set to <see langword="false"/> only for known-destructive migrations.
    /// </summary>
    public bool BlockOnPossibleDataLoss { get; set; } = true;

    /// <summary>
    /// Permits DacFx to drop target objects that are absent from the package schema. Defaults to <see langword="false"/> (extra target objects are preserved).
    /// </summary>
    public bool AllowObjectDrops { get; set; }

    /// <summary>
    /// Deploys database users from the dacpac. Defaults to <see langword="false"/> because users are usually environment-specific.
    /// </summary>
    public bool DeployUsers { get; set; }

    /// <summary>
    /// Deploys server logins and their user mappings from the dacpac, when present. Defaults to <see langword="false"/>; only useful when the captured dacpac actually carries login info.
    /// </summary>
    public bool DeployLogins { get; set; }

    /// <summary>
    /// Deploys GRANT/DENY/REVOKE statements from the dacpac. Defaults to <see langword="false"/>; turn on only when the source permissions should follow the schema.
    /// </summary>
    public bool DeployPermissions { get; set; }

    /// <summary>
    /// Deploys role membership assignments from the dacpac. Defaults to <see langword="false"/>.
    /// </summary>
    public bool DeployRoleMembership { get; set; }

    /// <summary>
    /// Deploys database file and filegroup definitions from the dacpac. Defaults to <see langword="false"/> because storage layout is usually managed per-environment.
    /// </summary>
    public bool DeployDatabaseFiles { get; set; }

    /// <summary>
    /// Applies the source database's <c>ALTER DATABASE</c> property scripts (containment, recovery model, compatibility-adjacent options, etc.) to the target. Defaults to <see langword="false"/> because these settings are usually environment-specific.
    /// When <see langword="false"/>, cross-platform model adaptation is delegated to <see cref="AdaptAzureSourceForOnPremTarget"/>; set this to <see langword="true"/> only when you genuinely want the source database options applied verbatim to the target.
    /// </summary>
    public bool DeployDatabaseOptions { get; set; }

    /// <summary>
    /// Rewrites Azure SQL-specific model elements on a temp copy of the dacpac when deploying an Azure-source extract to an on-prem (non-Azure) target, so DacFx no longer scripts prerequisites the target cannot satisfy. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="true"/>, the deploy probes <c>SERVERPROPERTY('EngineEdition')</c> on the target and — if the source package was stamped as Azure SQL (edition 5 / 8 / 11 / 12) and the target is non-Azure — performs the following model.xml mutations before invoking DacFx:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Removes the optional <c>Containment</c> property from <c>SqlDatabaseOptions</c>.</description></item>
    /// <item><description>Rewrites <c>SqlUser</c> elements whose <c>AuthenticationType</c> is 2 (contained password user) or 4 (Entra / external provider) to <c>IsWithoutLogin=True</c>, dropping the <c>Password</c> / <c>Sid</c> properties. The element is kept (not deleted) so model-internal references stay valid.</description></item>
    /// </list>
    /// <para>
    /// Without this rewrite, DacFx emits <c>ALTER DATABASE ... SET CONTAINMENT = PARTIAL</c> as a deploy-script prerequisite, which fails with Msg 12824 on targets where <c>sp_configure 'contained database authentication'</c> is 0.
    /// </para>
    /// <para>
    /// Set this to <see langword="false"/> to deploy the source model verbatim. Useful when you want the deploy to fail loudly so you can fix the source dacpac upstream, or when the operator has already configured <c>contained database authentication = 1</c> on the target and wants the original users preserved.
    /// </para>
    /// <para>
    /// The source-platform signal travels in the SQLite package (column <c>source_engine_edition</c> on <c>zsdp_schema_packages</c>). When it says the source was not Azure SQL the rewrite is skipped outright and the target is never probed. When it says Azure SQL, or the package predates the stamp, the target is probed: a target that cannot be connected to fails the deploy with a connection error, while a target that connects but does not answer <c>SERVERPROPERTY('EngineEdition')</c> raises a warning and is assumed to be non-Azure.
    /// </para>
    /// </remarks>
    public bool AdaptAzureSourceForOnPremTarget { get; set; } = true;

    /// <summary>
    /// Runs DacFx's deployment-plan verification before applying changes; disable to skip the pre-flight check at the cost of catching issues only at apply time. Defaults to <see langword="true"/>.
    /// </summary>
    public bool VerifyDeployment { get; set; } = true;
}

/// <summary>
/// Configures a SQL Server WHERE predicate that applies to every selected export table containing a
/// given set of columns.
/// </summary>
/// <remarks>
/// <para>
/// The named columns gate the predicate: a table is filtered only when it has <em>every</em> column in
/// <see cref="ColumnNames"/>. Tables missing any of them are exported unfiltered. This is what makes a
/// multi-column clause different from several single-column ones — the latter apply independently
/// wherever each column happens to exist.
/// </para>
/// <code>
/// // Applies only to tables that have BOTH TenantId and IsDeleted.
/// new GlobalWhereClause(["TenantId", "IsDeleted"], "TenantId = 123 AND IsDeleted = 0")
/// </code>
/// <para>
/// Gating looks at the table's source columns, so a predicate may reference a column excluded from the
/// exported output via <see cref="ExportOptions.ExcludeColumns"/>.
/// </para>
/// </remarks>
public sealed record GlobalWhereClause {
    /// <summary>
    /// Initializes a clause gated on a single column.
    /// </summary>
    /// <param name="columnName">The source column name that gates this predicate.</param>
    /// <param name="whereClause">The raw SQL Server predicate to append to matching table exports.</param>
    public GlobalWhereClause(string columnName, string whereClause) : this([columnName], whereClause) {
    }

    /// <summary>
    /// Initializes a clause gated on a set of columns that a table must have in full.
    /// </summary>
    /// <param name="columnNames">The source column names that together gate this predicate.</param>
    /// <param name="whereClause">The raw SQL Server predicate to append to matching table exports.</param>
    public GlobalWhereClause(IEnumerable<string> columnNames, string whereClause) {
        ArgumentNullException.ThrowIfNull(columnNames);
        ColumnNames = columnNames.ToArray();
        WhereClause = whereClause;
    }

    /// <summary>
    /// The source column names that gate this predicate. A selected table is filtered only when it has
    /// all of them.
    /// </summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>
    /// The raw SQL Server predicate appended to the export of every table carrying all of
    /// <see cref="ColumnNames"/>.
    /// </summary>
    public string WhereClause { get; }

    // Records compare an IReadOnlyList<string> member by reference, which would make two clauses with
    // identical column names unequal. Equality is written out so options objects, and the tests that
    // assert on them, compare by value.
    /// <inheritdoc />
    public bool Equals(GlobalWhereClause? other) => other is not null && string.Equals(WhereClause, other.WhereClause, StringComparison.Ordinal) && ColumnNames.SequenceEqual(other.ColumnNames, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(WhereClause, StringComparer.Ordinal);
        foreach (var columnName in ColumnNames) {
            hash.Add(columnName, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private bool PrintMembers(System.Text.StringBuilder builder) {
        builder.Append("ColumnNames = [").Append(string.Join(", ", ColumnNames)).Append("], ");
        builder.Append("WhereClause = ").Append(WhereClause);
        return true;
    }
}

/// <summary>
/// Configures a SQL Server WHERE predicate that applies to one selected export table.
/// </summary>
/// <remarks>
/// Not a positional record, deliberately; see the note at the top of <c>SqlDataPackOperationalModels.cs</c>.
/// </remarks>
public sealed record PerTableWhereClause {
    /// <summary>
    /// Initializes a clause scoped to one table.
    /// </summary>
    /// <param name="tableName">The source table name formatted as <c>&lt;schema&gt;.&lt;table&gt;</c>.</param>
    /// <param name="whereClause">The raw SQL Server predicate to append to the table export.</param>
    public PerTableWhereClause(string tableName, string whereClause) {
        TableName = tableName;
        WhereClause = whereClause;
    }

    /// <summary>
    /// The source table name formatted as <c>&lt;schema&gt;.&lt;table&gt;</c>.
    /// </summary>
    public string TableName { get; init; }

    /// <summary>
    /// The raw SQL Server predicate to append to the table export.
    /// </summary>
    public string WhereClause { get; init; }
}

/// <summary>
/// Configures a SQL Server to SQLite export.
/// </summary>
public sealed class ExportOptions {
    /// <summary>
    /// Returns a new <see cref="ExportOptions"/> populated with the conservative, <strong>value-stable</strong> defaults — a convenient starting point to tweak.
    /// Each access returns a fresh instance, so mutating the returned object never affects subsequent callers.
    /// Value-identical to <c>new ExportOptions()</c>; the property initializers below are the single source of truth.
    /// </summary>
    /// <remarks>
    /// The values returned here are part of the library's stable contract: they will not change across releases. Pin to
    /// <see cref="Default"/> (or set the relevant properties explicitly) when you need behaviour that stays identical on upgrade.
    /// For tuning that tracks the library's latest recommendations, use <see cref="Latest"/> instead.
    /// </remarks>
    public static ExportOptions Default => new();

    /// <summary>
    /// Returns a new <see cref="ExportOptions"/> populated with the library's current best-throughput tuning. Use this when you
    /// want the export to follow our latest recommendations automatically rather than pinning specific values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Default"/>, the values returned here are <strong>not value-stable</strong>: they may change in any minor
    /// release as the recommended tuning evolves (changes are called out in the release notes). If you need behaviour that stays
    /// byte-for-byte identical across upgrades, use <see cref="Default"/> or set the properties explicitly.
    /// </para>
    /// <para>
    /// As of this release it raises <see cref="BatchSize"/> to <c>5,000</c> and <see cref="MaxBatchBytes"/> to 8 MiB versus
    /// <see cref="Default"/> — faster on typical and narrow tables while the per-batch memory ceiling stays bounded. The
    /// large-table safety net (<see cref="LargeTableBatchSize"/> and its thresholds) is left at the conservative defaults, so
    /// genuinely huge tables stay memory-safe.
    /// </para>
    /// </remarks>
    public static ExportOptions Latest => new() {
        BatchSize = BatchPlanner.LatestBatchSize,
        MaxBatchBytes = BatchPlanner.LatestMaxBatchBytes
    };

    /// <summary>
    /// Controls how <see cref="Tables"/> is interpreted: <see cref="ExportTableSelectionMode.AllExcept"/> uses it as an exclusion list against a full export, <see cref="ExportTableSelectionMode.Only"/> uses it as an inclusion list. Defaults to <see cref="ExportTableSelectionMode.AllExcept"/>.
    /// </summary>
    public ExportTableSelectionMode TableSelection { get; set; } = ExportTableSelectionMode.AllExcept;

    /// <summary>
    /// Table-name patterns that <see cref="TableSelection"/> applies to (exact <c>schema.table</c> names or <c>*</c> wildcards). Defaults to an empty list, which under <see cref="ExportTableSelectionMode.AllExcept"/> means export everything.
    /// </summary>
    public IList<string> Tables { get; set; } = new List<string>();

    /// <summary>
    /// Fully qualified column paths (<c>schema.table.column</c>) to omit from the exported package. Defaults to an empty list (export every column of every selected table).
    /// </summary>
    public IList<string> ExcludeColumns { get; set; } = new List<string>();

    /// <summary>
    /// Transformers bound to fully qualified column paths (<c>schema.table.column</c>), applied to each non-NULL
    /// source value during export so the original never reaches the package. Defaults to an empty dictionary
    /// (no transformation).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A column takes one transformer; there is no chaining, no name matching, and no automatic detection. Bind
    /// a built-in from <c>SqlDataPack.Transformations</c> or your own <see cref="IValueTransformer"/>:
    /// </para>
    /// <code>
    /// options.Transformations.Add("dbo.Customers.Email", new EmailPseudonymizer());
    /// options.Transformations.Add("dbo.Customers.LastName", new NameMasker(new NameMaskerOptions { PreserveCharacters = 2, Suffix = "test" }));
    /// </code>
    /// <para>
    /// A source NULL bypasses the transformer and stays NULL. Everything else fails the export rather than
    /// falling back: a transformer that throws, one that returns NULL for a non-nullable column, and one whose
    /// result does not fit the destination column's type, length, precision, or scale.
    /// </para>
    /// </remarks>
    public IDictionary<string, IValueTransformer> Transformations { get; set; } = new Dictionary<string, IValueTransformer>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SQL Server WHERE predicates applied to every selected table that contains all of a clause's named columns — useful for tenant or soft-delete filtering. Defaults to an empty list. See <see cref="GlobalWhereClause"/>.
    /// </summary>
    public IList<GlobalWhereClause> GlobalWhereClauses { get; set; } = new List<GlobalWhereClause>();

    /// <summary>
    /// SQL Server WHERE predicates applied only to the specific tables they name. Defaults to an empty list.
    /// </summary>
    public IList<PerTableWhereClause> PerTableWhereClauses { get; set; } = new List<PerTableWhereClause>();

    /// <summary>
    /// Prefix prepended to every exported data table inside the SQLite package. Defaults to <see langword="null"/>, which writes data tables under their bare generated name, for example <c>dbo__customers</c>. Set a value to group data tables behind a prefix instead. The package's own metadata tables are always named <c>zsdp_*</c> and are unaffected by this setting.
    /// </summary>
    public string? DataTablePrefix { get; set; }

    /// <summary>
    /// Row count per SQLite write batch for normal-sized tables. Defaults to <c>1000</c>; raise for narrow tables to reduce commit overhead, lower for very wide rows.
    /// </summary>
    public int BatchSize { get; set; } = 1_000;

    /// <summary>
    /// Enables the large-table planner that shrinks batch sizes for tables exceeding <see cref="LargeTableThresholdBytes"/>/<see cref="LargeTableRowThreshold"/>, trading throughput for memory pressure. Defaults to <see langword="true"/>; set to <see langword="false"/> to always use <see cref="BatchSize"/>.
    /// </summary>
    public bool AdaptiveBatchingEnabled { get; set; } = true;

    /// <summary>
    /// Estimated table size, in bytes, at or above which a table is treated as "large" and switched to <see cref="LargeTableBatchSize"/>. Defaults to 50 MiB.
    /// </summary>
    public long LargeTableThresholdBytes { get; set; } = BatchPlanner.DefaultLargeTableThresholdBytes;

    /// <summary>
    /// Estimated row count at or above which a table is treated as "large" when size metadata is unavailable. Defaults to <c>100,000</c> rows.
    /// </summary>
    public long LargeTableRowThreshold { get; set; } = BatchPlanner.DefaultLargeTableRowThreshold;

    /// <summary>
    /// Row count per batch used when a table crosses either large-table threshold. Defaults to <c>250</c>.
    /// </summary>
    public int LargeTableBatchSize { get; set; } = BatchPlanner.DefaultLargeTableBatchSize;

    /// <summary>
    /// Approximate upper bound, in bytes, on the in-memory size of a single batch when size and row-count metadata are both available — caps memory regardless of <see cref="BatchSize"/>. Defaults to 4 MiB.
    /// </summary>
    public long MaxBatchBytes { get; set; } = BatchPlanner.DefaultMaxBatchBytes;

    /// <summary>
    /// SQL Server command timeout, in seconds, for metadata queries and data reads during export. Defaults to <see langword="null"/> (use the provider's default, typically 30 seconds).
    /// </summary>
    public int? CommandTimeout { get; set; }

    /// <summary>
    /// Progress reporter that receives table- and row-level updates as the export runs. Defaults to <see langword="null"/> (no progress reporting).
    /// </summary>
    public IProgress<SqlDataPackProgress>? Progress { get; set; }

    /// <summary>
    /// Logger that receives the same lifecycle, table, row-batch, and warning events as <see cref="Progress"/>, mapped
    /// to log levels (row batches at <see cref="LogLevel.Trace"/>; table and operation events at <see cref="LogLevel.Information"/>;
    /// warnings at <see cref="LogLevel.Warning"/>). Defaults to <see langword="null"/> (no logging). May be set alongside <see cref="Progress"/>.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Allows the export to replace an existing SQLite file at the destination path on successful completion. Defaults to <see langword="false"/>; an existing file otherwise fails the export.
    /// </summary>
    public bool OverwriteExistingPackage { get; set; }

    /// <summary>
    /// Excludes the <c>dbo.sysdiagrams</c> data rows from the export. SQL Server Management Studio's "Database Diagrams"
    /// feature creates this table as a regular user table (<c>is_ms_shipped = 0</c>), so it is otherwise exported like any
    /// other table even though its rows are editor metadata. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// This affects only the exported data rows; the dacpac schema path is unaffected, so the diagram objects (the table,
    /// helper procedures, and function) are still captured at the configured <see cref="DacpacCaptureOptions.SchemaScope"/>.
    /// The exclusion is unconditional when <see langword="true"/>: even if you explicitly select <c>dbo.sysdiagrams</c> via
    /// <see cref="TableSelection"/>/<see cref="Tables"/>, set this to <see langword="false"/> to export its rows.
    /// </remarks>
    public bool ExcludeSsmsDiagrams { get; set; } = true;

    /// <summary>
    /// Selects whether to embed source-schema information in the package. Defaults to <see cref="SchemaCaptureMode.None"/> (data-only package); set to <see cref="SchemaCaptureMode.Dacpac"/> to extract a dacpac during export.
    /// </summary>
    public SchemaCaptureMode SchemaCaptureMode { get; set; } = SchemaCaptureMode.None;

    /// <summary>
    /// Dacpac extraction settings used only when <see cref="SchemaCaptureMode"/> is <see cref="SchemaCaptureMode.Dacpac"/>. Defaults to a new <see cref="DacpacCaptureOptions"/> with its own defaults.
    /// </summary>
    public DacpacCaptureOptions DacpacCaptureOptions { get; set; } = new();
}

/// <summary>
/// Configures a SQLite package import into SQL Server.
/// </summary>
public sealed class ImportOptions {
    /// <summary>
    /// Returns a new <see cref="ImportOptions"/> populated with the conservative, <strong>value-stable</strong> defaults — a convenient starting point to tweak.
    /// Each access returns a fresh instance, so mutating the returned object never affects subsequent callers.
    /// Value-identical to <c>new ImportOptions()</c>; the property initializers below are the single source of truth.
    /// </summary>
    /// <remarks>
    /// The values returned here are part of the library's stable contract: they will not change across releases. Pin to
    /// <see cref="Default"/> (or set the relevant properties explicitly) when you need behaviour that stays identical on upgrade.
    /// For tuning that tracks the library's latest recommendations, use <see cref="Latest"/> instead.
    /// </remarks>
    public static ImportOptions Default => new();

    /// <summary>
    /// Returns a new <see cref="ImportOptions"/> populated with the library's current best-throughput tuning. Use this when you
    /// want the import to follow our latest recommendations automatically rather than pinning specific values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Default"/>, the values returned here are <strong>not value-stable</strong>: they may change in any minor
    /// release as the recommended tuning evolves (changes are called out in the release notes). If you need behaviour that stays
    /// byte-for-byte identical across upgrades, use <see cref="Default"/> or set the properties explicitly.
    /// </para>
    /// <para>
    /// As of this release it raises <see cref="BatchSize"/> to <c>5,000</c> and <see cref="MaxBatchBytes"/> to 8 MiB versus
    /// <see cref="Default"/> — faster bulk-copy on typical and narrow tables while the per-batch memory ceiling stays bounded. The
    /// large-table safety net (<see cref="LargeTableBatchSize"/> and its thresholds) is left at the conservative defaults, so
    /// genuinely huge tables stay memory-safe.
    /// </para>
    /// </remarks>
    public static ImportOptions Latest => new() {
        BatchSize = BatchPlanner.LatestBatchSize,
        MaxBatchBytes = BatchPlanner.LatestMaxBatchBytes
    };

    /// <summary>
    /// Row count per bulk-copy batch for normal-sized tables. Defaults to <c>1000</c>.
    /// </summary>
    public int BatchSize { get; set; } = 1_000;

    /// <summary>
    /// Enables the large-table planner that shrinks bulk-copy batch sizes for tables exceeding <see cref="LargeTableThresholdBytes"/>/<see cref="LargeTableRowThreshold"/>, trading throughput for lower memory pressure. Defaults to <see langword="true"/>; set to <see langword="false"/> to always use <see cref="BatchSize"/>.
    /// </summary>
    public bool AdaptiveBatchingEnabled { get; set; } = true;

    /// <summary>
    /// Estimated table size, in bytes, at or above which a table is treated as "large" and switched to <see cref="LargeTableBatchSize"/>. Defaults to 50 MiB.
    /// </summary>
    public long LargeTableThresholdBytes { get; set; } = BatchPlanner.DefaultLargeTableThresholdBytes;

    /// <summary>
    /// Estimated row count at or above which a table is treated as "large" when size metadata is unavailable. Defaults to <c>100,000</c> rows.
    /// </summary>
    public long LargeTableRowThreshold { get; set; } = BatchPlanner.DefaultLargeTableRowThreshold;

    /// <summary>
    /// Row count per bulk-copy batch used when a table crosses either large-table threshold. Defaults to <c>250</c>.
    /// </summary>
    public int LargeTableBatchSize { get; set; } = BatchPlanner.DefaultLargeTableBatchSize;

    /// <summary>
    /// Approximate upper bound, in bytes, on the in-memory size of a single bulk-copy batch when size and row-count metadata are both available — caps memory regardless of <see cref="BatchSize"/>. Defaults to 4 MiB.
    /// </summary>
    public long MaxBatchBytes { get; set; } = BatchPlanner.DefaultMaxBatchBytes;

    /// <summary>
    /// SQL Server command timeout, in seconds, for the target validation queries run before bulk copy begins. Defaults to <see langword="null"/> (use the provider's default).
    /// </summary>
    public int? ValidationCommandTimeout { get; set; }

    /// <summary>
    /// Fails the import before any row is copied when a target column would lose data relative to the package:
    /// a shorter <c>char</c>, <c>varchar</c>, <c>nchar</c>, <c>nvarchar</c>, <c>binary</c> or <c>varbinary</c>,
    /// a smaller <c>decimal</c> precision or scale, or a smaller <c>datetime2</c>, <c>datetimeoffset</c> or
    /// <c>time</c> scale. Defaults to <see langword="false"/>, in which case every type difference is reported
    /// as a warning on <see cref="SqlDataPackResult.Warnings"/> and the import proceeds.
    /// </summary>
    /// <remarks>
    /// Widening differences and collation differences are always warnings and are never affected by this
    /// setting. A collation difference can mangle non-ASCII text but cannot be judged from catalog metadata
    /// alone, and blocking on it would fail every import into a differently collated server.
    /// </remarks>
    public bool FailOnLossyTypeMismatch { get; set; }

    /// <summary>
    /// Selects what happens when the package holds a different number of rows than the export recorded, which is what deleting or inserting rows in the package produces. Defaults to <see cref="Models.RowCountDrift.Warn"/>, in which case the rows the package holds are imported and the difference is reported per table on <see cref="SqlDataPackResult.Warnings"/>.
    /// </summary>
    /// <remarks>
    /// This compares the count recorded in the package manifest against the package's own contents, so it only ever answers whether the file changed since export. It is not the check that proves a load completed: that one compares the rows read out of the package against the rows that landed in the target, and it is always fatal. Set this to <see cref="Models.RowCountDrift.Fail"/> for unattended pipelines that scrub packages automatically, where a count that moved means a script bug rather than a deliberate edit.
    /// </remarks>
    public RowCountDrift RowCountDrift { get; set; } = RowCountDrift.Warn;

    /// <summary>
    /// Timeout, in seconds, for each <c>SqlBulkCopy</c> operation. Defaults to <see langword="null"/> (use <c>SqlBulkCopy</c>'s default of 30 seconds); raise this for very large or slow tables.
    /// </summary>
    public int? BulkCopyTimeout { get; set; }

    /// <summary>
    /// Progress reporter that receives table- and row-level updates as the import runs. Defaults to <see langword="null"/> (no progress reporting).
    /// </summary>
    public IProgress<SqlDataPackProgress>? Progress { get; set; }

    /// <summary>
    /// Logger that receives the same lifecycle, table, row-batch, and warning events as <see cref="Progress"/>, mapped
    /// to log levels (row batches at <see cref="LogLevel.Trace"/>; table and operation events at <see cref="LogLevel.Information"/>;
    /// warnings at <see cref="LogLevel.Warning"/>). Defaults to <see langword="null"/> (no logging). May be set alongside <see cref="Progress"/>.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Selects whether to deploy the package's embedded schema before loading data. Defaults to <see cref="SchemaDeploymentMode.None"/> (assume target schema already exists); set to <see cref="SchemaDeploymentMode.DeployDacpac"/> to apply the captured dacpac first.
    /// </summary>
    public SchemaDeploymentMode SchemaDeploymentMode { get; set; } = SchemaDeploymentMode.None;

    /// <summary>
    /// Dacpac deployment settings used only when <see cref="SchemaDeploymentMode"/> is <see cref="SchemaDeploymentMode.DeployDacpac"/>. Defaults to a new <see cref="DacpacDeploymentOptions"/> with its own defaults.
    /// </summary>
    public DacpacDeploymentOptions DacpacDeploymentOptions { get; set; } = new();

    /// <summary>
    /// Handles system-versioned temporal tables on the target by temporarily setting <c>SYSTEM_VERSIONING = OFF</c>
    /// and dropping the <c>SYSTEM_TIME</c> period before loading, then re-adding the period and re-enabling versioning.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Without this, importing a temporal table fails: SQL Server rejects direct inserts into the history table
    /// (Msg 13560) and into the <c>GENERATED ALWAYS</c> period columns of the current table (Msg 13536). The
    /// ceremony lets the import reload both the current and history rows with their original
    /// <c>ValidFrom</c>/<c>ValidTo</c> values, and re-applies a finite <c>HISTORY_RETENTION_PERIOD</c> (which
    /// <c>SET SYSTEM_VERSIONING = OFF</c> would otherwise reset to INFINITE). The period is dropped only when the
    /// package actually carries the period columns; when it does not (a non-temporal source loaded into a
    /// temporal target, or period columns excluded during export) versioning stays on and SQL Server
    /// auto-populates the period. Set to <see langword="false"/> to load every target table as-is and let those
    /// inserts fail loudly — useful when the target has no temporal tables and you want to skip the catalog
    /// probe, or when temporal handling is managed externally.
    /// </remarks>
    public bool SuspendTemporalSystemVersioning { get; set; } = true;

    /// <summary>
    /// When re-enabling system versioning after a temporal load, runs SQL Server's <c>DATA_CONSISTENCY_CHECK</c>
    /// to validate that the current and history period ranges do not overlap. Defaults to <see langword="true"/>.
    /// Only applies when <see cref="SuspendTemporalSystemVersioning"/> is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A faithful full-table export passes the check. It fails (and aborts the import with a descriptive
    /// <see cref="SqlDataPackException"/>) when the temporal data is inconsistent — for example when the temporal table
    /// or its history was filtered with a WHERE clause, a period column was excluded, or the source changed
    /// mid-export. Set to <see langword="false"/> to re-enable versioning without validation, at the risk of a
    /// temporal table that returns incorrect <c>AS OF</c> query results.
    /// </remarks>
    public bool TemporalDataConsistencyCheck { get; set; } = true;
}

/// <summary>
/// Represents a validation or export/import operation error that callers can handle explicitly.
/// </summary>
public sealed class SqlDataPackException : Exception {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SqlDataPackException(string message) : base(message) {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackException"/> class with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public SqlDataPackException(string message, Exception innerException) : base(message, innerException) {
    }
}
