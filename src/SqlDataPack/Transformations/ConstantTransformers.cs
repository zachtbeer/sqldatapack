namespace SqlDataPack.Transformations;

/// <summary>
/// Replaces every value with NULL. The blunt option for a column whose contents are not worth keeping and not
/// worth shaping: a free-form notes or payload column, say.
/// </summary>
/// <remarks>
/// The column has to be nullable. Binding this to a NOT NULL column fails the export naming the column; use
/// <see cref="EmptyStringTransformer"/> there instead. A source NULL never reaches a transformer, so this is a
/// no-op on rows that are already NULL.
/// </remarks>
public sealed class NullTransformer : BuiltInTransformer {
    /// <summary>Initializes a new <see cref="NullTransformer"/>.</summary>
    public NullTransformer() {
    }

    internal override string Configuration => string.Empty;

    /// <inheritdoc />
    public override object? Transform(TransformContext context, object value) => null;
}

/// <summary>
/// Replaces every value with the empty string. The NOT NULL counterpart to <see cref="NullTransformer"/>.
/// </summary>
/// <remarks>
/// The column has to be a text column: the empty string is not a value an <c>int</c> or a <c>datetime2</c> can
/// hold, and binding it to one fails the export naming the column. A fixed-length <c>char(n)</c> column stores
/// the result padded, which is how SQL Server stores an empty string in one anyway.
/// </remarks>
public sealed class EmptyStringTransformer : BuiltInTransformer {
    /// <summary>Initializes a new <see cref="EmptyStringTransformer"/>.</summary>
    public EmptyStringTransformer() {
    }

    internal override string Configuration => string.Empty;

    /// <inheritdoc />
    public override object Transform(TransformContext context, object value) => string.Empty;
}
