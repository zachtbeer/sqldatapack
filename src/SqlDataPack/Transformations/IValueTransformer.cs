using SqlDataPack.Internal;

namespace SqlDataPack.Transformations;

/// <summary>
/// Transforms one source cell during export, before the value is converted and written to the SQLite package.
/// The original value never reaches the package for a column a transformer is bound to.
/// </summary>
/// <remarks>
/// <para>
/// Transformation is synchronous and value-oriented: an implementation sees one value plus the destination
/// column's contract, and nothing else. There is deliberately no access to the rest of the row, to either
/// connection, or to the export's internal secret.
/// </para>
/// <para>
/// A transformer is never called for a source <see langword="null"/>; NULL stays NULL. Returning
/// <see langword="null"/> is allowed for a nullable column and fails the export for a non-nullable one.
/// Any exception an implementation throws fails the export — transformation never falls back to the
/// original value.
/// </para>
/// <para>
/// An implementation may keep its own state, but SqlDataPack neither manages nor guarantees it: one
/// instance may be called for many rows of many tables, on the export's thread.
/// </para>
/// </remarks>
public interface IValueTransformer {
    /// <summary>
    /// Transforms one non-null source value.
    /// </summary>
    /// <param name="context">The destination column's contract: name, SQL Server type, nullability, and size.</param>
    /// <param name="value">The source value, never <see langword="null"/> and never <see cref="DBNull"/>.</param>
    /// <returns>The value to write, or <see langword="null"/> to write NULL (nullable columns only).</returns>
    object? Transform(TransformContext context, object value);
}

/// <summary>
/// The destination column a value is being transformed for. One instance is built per column per export and
/// reused for every cell of that column, so an implementation must treat it as read-only shared state.
/// </summary>
public sealed class TransformContext {
    /// <summary>
    /// Initializes a new <see cref="TransformContext"/>. Exports build their own; this constructor exists so a
    /// custom transformer can be unit tested without running an export.
    /// </summary>
    /// <param name="schema">The source schema name.</param>
    /// <param name="table">The source table name.</param>
    /// <param name="column">The source column name.</param>
    /// <param name="sqlServerTypeName">The column's SQL Server type name, for example <c>nvarchar</c>.</param>
    /// <param name="isNullable">Whether the column accepts NULL.</param>
    /// <param name="maxLength">The column's maximum length — characters for string types, bytes for binary types — or <see langword="null"/> when the type has no length or is a <c>max</c> type.</param>
    /// <param name="precision">The column's numeric precision, or <c>0</c> when the type has none.</param>
    /// <param name="scale">The column's numeric scale, or <c>0</c> when the type has none.</param>
    public TransformContext(string schema, string table, string column, string sqlServerTypeName, bool isNullable, int? maxLength, byte precision, byte scale) : this(schema, table, column, sqlServerTypeName, isNullable, maxLength, precision, scale, secret: null) {
    }

    internal TransformContext(string schema, string table, string column, string sqlServerTypeName, bool isNullable, int? maxLength, byte precision, byte scale, ExportSecret? secret) {
        Schema = schema;
        Table = table;
        Column = column;
        SqlServerTypeName = sqlServerTypeName;
        IsNullable = isNullable;
        MaxLength = maxLength;
        Precision = precision;
        Scale = scale;
        Secret = secret;
        ColumnPath = $"{schema}.{table}.{column}";
    }

    /// <summary>The source schema name.</summary>
    public string Schema { get; }

    /// <summary>The source table name.</summary>
    public string Table { get; }

    /// <summary>The source column name.</summary>
    public string Column { get; }

    /// <summary>The fully qualified <c>schema.table.column</c> path.</summary>
    public string ColumnPath { get; }

    /// <summary>The column's SQL Server type name, for example <c>nvarchar</c> or <c>uniqueidentifier</c>.</summary>
    public string SqlServerTypeName { get; }

    /// <summary>Whether the column accepts NULL. Returning <see langword="null"/> from a transformer bound to a column where this is <see langword="false"/> fails the export.</summary>
    public bool IsNullable { get; }

    /// <summary>
    /// The column's maximum length — characters for string types, bytes for binary types — or
    /// <see langword="null"/> when the type carries no length (numbers, dates, GUIDs) or is a <c>max</c> type.
    /// A transformed value longer than this fails the export; it is never truncated.
    /// </summary>
    public int? MaxLength { get; }

    /// <summary>The column's numeric precision, or <c>0</c> when the type has none.</summary>
    public byte Precision { get; }

    /// <summary>The column's numeric scale, or <c>0</c> when the type has none.</summary>
    public byte Scale { get; }

    /// <summary>
    /// The export-scoped secret behind the built-in pseudonymizers. Deliberately internal: it exists only for
    /// the running export and is never handed to a custom transformer or written to the package.
    /// </summary>
    internal ExportSecret? Secret { get; }
}

/// <summary>
/// Wraps a delegate as an <see cref="IValueTransformer"/>, so a one-off transformation does not need its own type.
/// </summary>
/// <remarks>
/// The delegate is called once per non-null cell of the bound column and must be safe to call repeatedly.
/// It is recorded in the package as <c>Custom</c>; SqlDataPack does not try to name it.
/// </remarks>
public sealed class CustomTransformer : IValueTransformer {
    private readonly Func<TransformContext, object, object?> _transform;

    /// <summary>
    /// Initializes a new <see cref="CustomTransformer"/>.
    /// </summary>
    /// <param name="transform">The transformation to apply to each non-null source value.</param>
    public CustomTransformer(Func<TransformContext, object, object?> transform) {
        ArgumentNullException.ThrowIfNull(transform);
        _transform = transform;
    }

    /// <inheritdoc />
    public object? Transform(TransformContext context, object value) => _transform(context, value);
}
