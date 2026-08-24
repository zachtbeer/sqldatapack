namespace SqlDataPack.Internal;

internal sealed record ColumnMetadata(TableName Table, string Name, int Ordinal, string SqlServerTypeName, short MaxLength, byte Precision, byte Scale, bool IsNullable, bool IsIdentity, bool IsComputed, string? CollationName, bool IsExcluded, int? VectorBaseType = null, int? VectorDimensions = null) {
    public bool IsExported => !IsComputed && !IsExcluded;

    /// <summary>
    /// The conversion behaviour this column's SQL Server type maps to. Computed rather than cached in
    /// a field: <c>SqlServerSchemaReader</c> rebuilds columns with <c>with</c> expressions to attach
    /// vector metadata, and a cached field would survive that copy while the type name it was derived
    /// from could not be assumed unchanged. The lookup behind it is allocation-free.
    /// </summary>
    public ColumnKind Kind => ValueConverter.KindFor(SqlServerTypeName);

    // sys.columns.vector_base_type == 1 is the preview float16 base type; null/0 is the GA float32 type.
    public bool IsFloat16Vector => Kind == ColumnKind.Vector && VectorBaseType == 1;
}

internal sealed record TableMetadata(TableName Name, string SqliteTableName, IReadOnlyList<ColumnMetadata> Columns, long EstimatedSourceRowCount = 0, long EstimatedSourceBytes = 0, int ExportBatchSize = 0, IReadOnlyList<string>? AppliedWhereClauses = null) {
    public IReadOnlyList<ColumnMetadata> ExportedColumns => Columns.Where(c => c.IsExported).OrderBy(c => c.Ordinal).ToArray();

    public IReadOnlyList<string> WhereClauses => AppliedWhereClauses ?? [];
}

internal sealed record ForeignKeyMetadata(TableName ParentTable, TableName ReferencedTable);

internal sealed record ExportPlan(IReadOnlyList<TableMetadata> Tables, IReadOnlyList<ForeignKeyMetadata> ForeignKeys, IReadOnlyList<TableName> ImportOrder, IReadOnlyList<string> Warnings, IReadOnlyList<string> SkippedTables, IReadOnlyList<string> SkippedColumns, string SchemaHash);
