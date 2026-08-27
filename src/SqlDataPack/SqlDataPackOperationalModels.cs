namespace SqlDataPack.Models;

// The record types in this file are deliberately not positional. A positional record generates a
// Deconstruct whose member list is frozen at the moment it is written, so the first time a manifest
// gains a field, deconstruction silently omits it — and extending the positional parameter list
// instead is a constructor break that needs a major version. Declaring an explicit constructor with
// { get; init; } properties keeps value equality and `with` expressions while leaving no Deconstruct
// to drift. New members go on as init properties, which is additive in a minor release.
// PublicApiContractTests.PublicModelRecords_DoNotExposeDeconstruct enforces this.
//
// Constructor parameters stay PascalCase: they are the names the positional form generated, and
// callers pass them as named arguments, so renaming them to camelCase would be a source break.

/// <summary>
/// Describes the kind of progress reported by an export or import operation.
/// </summary>
public enum SqlDataPackProgressKind {
    /// <summary>
    /// The operation has started.
    /// </summary>
    OperationStarted = 0,

    /// <summary>
    /// A table transfer has started.
    /// </summary>
    TableStarted = 1,

    /// <summary>
    /// Rows have been copied for the current table.
    /// </summary>
    RowsCopied = 2,

    /// <summary>
    /// A table transfer has completed.
    /// </summary>
    TableCompleted = 3,

    /// <summary>
    /// A non-fatal warning was produced.
    /// </summary>
    Warning = 4,

    /// <summary>
    /// The operation has completed.
    /// </summary>
    OperationCompleted = 5
}

/// <summary>
/// Reports progress from an export or import operation.
/// </summary>
public sealed record SqlDataPackProgress {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackProgress"/> record.
    /// </summary>
    /// <param name="Kind">The progress event kind.</param>
    /// <param name="TableName">The source table full name when the event is table-specific.</param>
    /// <param name="RowsProcessed">The rows processed for the table or operation.</param>
    /// <param name="TotalRows">The expected rows when known.</param>
    /// <param name="Message">An optional human-readable message.</param>
    public SqlDataPackProgress(SqlDataPackProgressKind Kind, string? TableName = null, long RowsProcessed = 0, long? TotalRows = null, string? Message = null) {
        this.Kind = Kind;
        this.TableName = TableName;
        this.RowsProcessed = RowsProcessed;
        this.TotalRows = TotalRows;
        this.Message = Message;
    }

    /// <summary>
    /// The progress event kind.
    /// </summary>
    public SqlDataPackProgressKind Kind { get; init; }

    /// <summary>
    /// The source table full name when the event is table-specific.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// The rows processed for the table or operation.
    /// </summary>
    public long RowsProcessed { get; init; }

    /// <summary>
    /// The expected rows when known.
    /// </summary>
    public long? TotalRows { get; init; }

    /// <summary>
    /// An optional human-readable message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Describes a column stored in a SQLite package.
/// </summary>
public sealed record SqlDataPackColumnManifest {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackColumnManifest"/> record.
    /// </summary>
    /// <param name="Name">The source column name.</param>
    /// <param name="Ordinal">The source column position, as reported by <c>sys.columns.column_id</c>.</param>
    /// <param name="SqlServerTypeName">The source SQL Server type name, such as <c>nvarchar</c> or <c>vector</c>.</param>
    /// <param name="MaxLength">The source column length in bytes, as reported by <c>sys.columns.max_length</c>.</param>
    /// <param name="Precision">The source column numeric precision.</param>
    /// <param name="Scale">The source column numeric scale.</param>
    /// <param name="IsNullable">Whether the source column allows nulls.</param>
    /// <param name="IsIdentity">Whether the source column is an identity column.</param>
    /// <param name="IsComputed">Whether the source column is computed.</param>
    /// <param name="IsExcluded">Whether the column was excluded from the export by configuration.</param>
    /// <param name="CollationName">The source column collation, or <see langword="null"/> when the column has none.</param>
    /// <param name="VectorBaseType">The <c>vector</c> element type, as reported by <c>sys.columns.vector_base_type</c>.</param>
    /// <param name="VectorDimensions">The declared dimension count of a <c>vector</c> column.</param>
    public SqlDataPackColumnManifest(string Name, int Ordinal, string SqlServerTypeName, short MaxLength, byte Precision, byte Scale, bool IsNullable, bool IsIdentity, bool IsComputed, bool IsExcluded, string? CollationName, int? VectorBaseType = null, int? VectorDimensions = null) {
        this.Name = Name;
        this.Ordinal = Ordinal;
        this.SqlServerTypeName = SqlServerTypeName;
        this.MaxLength = MaxLength;
        this.Precision = Precision;
        this.Scale = Scale;
        this.IsNullable = IsNullable;
        this.IsIdentity = IsIdentity;
        this.IsComputed = IsComputed;
        this.IsExcluded = IsExcluded;
        this.CollationName = CollationName;
        this.VectorBaseType = VectorBaseType;
        this.VectorDimensions = VectorDimensions;
    }

    /// <summary>
    /// The source column name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// The source column position, as reported by <c>sys.columns.column_id</c>.
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// The source SQL Server type name, such as <c>nvarchar</c> or <c>vector</c>.
    /// </summary>
    public string SqlServerTypeName { get; init; }

    /// <summary>
    /// The source column length in bytes, as reported by <c>sys.columns.max_length</c>.
    /// </summary>
    public short MaxLength { get; init; }

    /// <summary>
    /// The source column numeric precision.
    /// </summary>
    public byte Precision { get; init; }

    /// <summary>
    /// The source column numeric scale.
    /// </summary>
    public byte Scale { get; init; }

    /// <summary>
    /// Whether the source column allows nulls.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Whether the source column is an identity column.
    /// </summary>
    public bool IsIdentity { get; init; }

    /// <summary>
    /// Whether the source column is computed. Computed columns are described here but their values are not exported.
    /// </summary>
    public bool IsComputed { get; init; }

    /// <summary>
    /// Whether the column was excluded from the export by configuration.
    /// </summary>
    public bool IsExcluded { get; init; }

    /// <summary>
    /// The source column collation, or <see langword="null"/> when the column has none.
    /// </summary>
    public string? CollationName { get; init; }

    /// <summary>
    /// The <c>vector</c> element type, as reported by <c>sys.columns.vector_base_type</c>: <c>1</c> is the preview
    /// <c>float16</c> base type, and <c>0</c> or <see langword="null"/> is the GA <c>float32</c> base type. Always
    /// <see langword="null"/> for a column that is not a <c>vector</c>.
    /// </summary>
    public int? VectorBaseType { get; init; }

    /// <summary>
    /// The declared dimension count of a <c>vector</c> column. Always <see langword="null"/> for a column that is
    /// not a <c>vector</c>.
    /// </summary>
    public int? VectorDimensions { get; init; }
}

/// <summary>
/// Describes a table stored in or planned for a SQLite package.
/// </summary>
public sealed record SqlDataPackTableManifest {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackTableManifest"/> record.
    /// </summary>
    /// <param name="SourceSchema">The source table's SQL Server schema name.</param>
    /// <param name="SourceTable">The source table name.</param>
    /// <param name="SqliteTable">The name of the data table this maps to inside the SQLite package.</param>
    /// <param name="ExportedRowCount">The rows actually written to the package, or <c>0</c> for a planned manifest.</param>
    /// <param name="EstimatedSourceRowCount">The source row count SQL Server estimated at plan time.</param>
    /// <param name="EstimatedSourceBytes">The source table size SQL Server estimated at plan time.</param>
    /// <param name="ExportBatchSize">The batch size the export used, or planned to use, for this table.</param>
    /// <param name="Columns">The table's columns, ordered by source ordinal.</param>
    public SqlDataPackTableManifest(string SourceSchema, string SourceTable, string SqliteTable, long ExportedRowCount, long EstimatedSourceRowCount, long EstimatedSourceBytes, int ExportBatchSize, IReadOnlyList<SqlDataPackColumnManifest> Columns) {
        this.SourceSchema = SourceSchema;
        this.SourceTable = SourceTable;
        this.SqliteTable = SqliteTable;
        this.ExportedRowCount = ExportedRowCount;
        this.EstimatedSourceRowCount = EstimatedSourceRowCount;
        this.EstimatedSourceBytes = EstimatedSourceBytes;
        this.ExportBatchSize = ExportBatchSize;
        this.Columns = Columns;
    }

    /// <summary>
    /// The source table's SQL Server schema name.
    /// </summary>
    public string SourceSchema { get; init; }

    /// <summary>
    /// The source table name.
    /// </summary>
    public string SourceTable { get; init; }

    /// <summary>
    /// The name of the data table this maps to inside the SQLite package.
    /// </summary>
    public string SqliteTable { get; init; }

    /// <summary>
    /// The rows actually written to the package, or <c>0</c> for a planned manifest.
    /// </summary>
    public long ExportedRowCount { get; init; }

    /// <summary>
    /// The source row count SQL Server estimated at plan time.
    /// </summary>
    public long EstimatedSourceRowCount { get; init; }

    /// <summary>
    /// The source table size, in bytes, SQL Server estimated at plan time.
    /// </summary>
    public long EstimatedSourceBytes { get; init; }

    /// <summary>
    /// The batch size the export used, or planned to use, for this table.
    /// </summary>
    public int ExportBatchSize { get; init; }

    /// <summary>
    /// The table's columns, ordered by source ordinal.
    /// </summary>
    public IReadOnlyList<SqlDataPackColumnManifest> Columns { get; init; }

    /// <summary>
    /// Gets the source SQL Server table full name.
    /// </summary>
    public string FullName => $"{SourceSchema}.{SourceTable}";
}

/// <summary>
/// Describes a SQLite package or planned package.
/// </summary>
public sealed record SqlDataPackManifest {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackManifest"/> record.
    /// </summary>
    /// <param name="PackageFormatVersion">The SQLite package format version the package was written with.</param>
    /// <param name="ApplicationVersion">The SqlDataPack version that produced the package.</param>
    /// <param name="ExportedAtUtc">When the export ran.</param>
    /// <param name="SourceSchemaHash">A hash of the source schema the export was planned against.</param>
    /// <param name="Tables">The tables carried by the package.</param>
    /// <param name="ImportOrder">The source table full names in foreign-key-safe import order.</param>
    /// <param name="Exclusions">The tables and columns the export left out, as <c>table:</c> and <c>column:</c> entries.</param>
    /// <param name="Warnings">The warnings recorded during export.</param>
    /// <param name="ContainsDacpac">Whether the package embeds a dacpac schema package.</param>
    /// <param name="DacpacSchemaScope">The scope the embedded dacpac was extracted at, or <see langword="null"/> when there is none.</param>
    /// <param name="SourceEngineEdition">The source server's <c>SERVERPROPERTY('EngineEdition')</c>, or <see langword="null"/> when the package predates that stamp.</param>
    public SqlDataPackManifest(int PackageFormatVersion, string ApplicationVersion, DateTimeOffset ExportedAtUtc, string SourceSchemaHash, IReadOnlyList<SqlDataPackTableManifest> Tables, IReadOnlyList<string> ImportOrder, IReadOnlyList<string> Exclusions, IReadOnlyList<string> Warnings, bool ContainsDacpac, DacpacSchemaScope? DacpacSchemaScope, int? SourceEngineEdition = null) {
        this.PackageFormatVersion = PackageFormatVersion;
        this.ApplicationVersion = ApplicationVersion;
        this.ExportedAtUtc = ExportedAtUtc;
        this.SourceSchemaHash = SourceSchemaHash;
        this.Tables = Tables;
        this.ImportOrder = ImportOrder;
        this.Exclusions = Exclusions;
        this.Warnings = Warnings;
        this.ContainsDacpac = ContainsDacpac;
        this.DacpacSchemaScope = DacpacSchemaScope;
        this.SourceEngineEdition = SourceEngineEdition;
    }

    /// <summary>
    /// The SQLite package format version the package was written with.
    /// </summary>
    public int PackageFormatVersion { get; init; }

    /// <summary>
    /// The SqlDataPack version that produced the package.
    /// </summary>
    public string ApplicationVersion { get; init; }

    /// <summary>
    /// When the export ran.
    /// </summary>
    public DateTimeOffset ExportedAtUtc { get; init; }

    /// <summary>
    /// A hash of the source schema the export was planned against.
    /// </summary>
    public string SourceSchemaHash { get; init; }

    /// <summary>
    /// The tables carried by the package.
    /// </summary>
    public IReadOnlyList<SqlDataPackTableManifest> Tables { get; init; }

    /// <summary>
    /// The source table full names in foreign-key-safe import order.
    /// </summary>
    public IReadOnlyList<string> ImportOrder { get; init; }

    /// <summary>
    /// The columns a transformer was applied to during export. Empty when nothing was transformed.
    /// </summary>
    public IReadOnlyList<SqlDataPackTransformationManifest> Transformations { get; init; } = [];

    /// <summary>
    /// The tables and columns the export left out, as <c>table:</c> and <c>column:</c> entries.
    /// </summary>
    public IReadOnlyList<string> Exclusions { get; init; }

    /// <summary>
    /// The warnings recorded during export.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Whether the package embeds a dacpac schema package.
    /// </summary>
    public bool ContainsDacpac { get; init; }

    /// <summary>
    /// The scope the embedded dacpac was extracted at, or <see langword="null"/> when the package carries none.
    /// </summary>
    public DacpacSchemaScope? DacpacSchemaScope { get; init; }

    /// <summary>
    /// The source server's <c>SERVERPROPERTY('EngineEdition')</c>, or <see langword="null"/> when the package
    /// was produced before that value was stamped.
    /// </summary>
    public int? SourceEngineEdition { get; init; }
}

/// <summary>
/// Summarizes the tables, rows, and non-fatal warnings produced by an export or import operation.
/// </summary>
public sealed record SqlDataPackResult {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackResult"/> record.
    /// </summary>
    /// <param name="TableCount">The number of tables processed.</param>
    /// <param name="RowCount">The number of rows processed across all tables.</param>
    /// <param name="Warnings">The warning messages produced by the operation.</param>
    public SqlDataPackResult(int TableCount, long RowCount, IReadOnlyList<string> Warnings) {
        this.TableCount = TableCount;
        this.RowCount = RowCount;
        this.Warnings = Warnings;
    }

    /// <summary>
    /// The number of tables processed.
    /// </summary>
    public int TableCount { get; init; }

    /// <summary>
    /// The number of rows processed across all tables.
    /// </summary>
    public long RowCount { get; init; }

    /// <summary>
    /// The warning messages produced by the operation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Summarizes validation performed before an export or import copies rows.
/// </summary>
public sealed record SqlDataPackPreflightResult {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackPreflightResult"/> record.
    /// </summary>
    /// <param name="IsValid">Whether the operation can proceed.</param>
    /// <param name="Errors">The problems that would fail the operation.</param>
    /// <param name="Warnings">The non-fatal findings.</param>
    /// <param name="Manifest">The package manifest, actual or planned, or <see langword="null"/> when it could not be built.</param>
    public SqlDataPackPreflightResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, SqlDataPackManifest? Manifest) {
        this.IsValid = IsValid;
        this.Errors = Errors;
        this.Warnings = Warnings;
        this.Manifest = Manifest;
    }

    /// <summary>
    /// Whether the operation can proceed.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The problems that would fail the operation.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// The non-fatal findings.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// The package manifest, actual or planned, or <see langword="null"/> when it could not be built.
    /// </summary>
    public SqlDataPackManifest? Manifest { get; init; }
}

/// <summary>
/// One transformed column, as recorded in the package.
/// </summary>
/// <remarks>
/// The package records that a column was transformed and how it was configured, never the export secret,
/// any key, or any original value. A transformer that is not one of SqlDataPack's built-ins is recorded as
/// <c>Custom</c>.
/// </remarks>
public sealed record SqlDataPackTransformationManifest {
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataPackTransformationManifest"/> record.
    /// </summary>
    /// <param name="Schema">The source schema name.</param>
    /// <param name="Table">The source table name.</param>
    /// <param name="Column">The source column name.</param>
    /// <param name="TransformerType">The built-in transformer's type name, or <c>Custom</c>.</param>
    /// <param name="Configuration">The built-in transformer's non-secret configuration, or <see langword="null"/>.</param>
    public SqlDataPackTransformationManifest(string Schema, string Table, string Column, string TransformerType, string? Configuration) {
        this.Schema = Schema;
        this.Table = Table;
        this.Column = Column;
        this.TransformerType = TransformerType;
        this.Configuration = Configuration;
    }

    /// <summary>The source schema name.</summary>
    public string Schema { get; init; }

    /// <summary>The source table name.</summary>
    public string Table { get; init; }

    /// <summary>The source column name.</summary>
    public string Column { get; init; }

    /// <summary>The fully qualified <c>schema.table.column</c> path the transformer was bound to.</summary>
    public string ColumnPath => $"{Schema}.{Table}.{Column}";

    /// <summary>The built-in transformer's type name, for example <c>EmailPseudonymizer</c>, or <c>Custom</c> for anything else.</summary>
    public string TransformerType { get; init; }

    /// <summary>The built-in transformer's non-secret configuration, rendered as <c>Name=value;Name=value</c>, or <see langword="null"/> when it has none.</summary>
    public string? Configuration { get; init; }
}
