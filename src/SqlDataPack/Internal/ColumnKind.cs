namespace SqlDataPack.Internal;

/// <summary>
/// The conversion behaviour a SQL Server type maps to. Synonyms that are treated identically
/// everywhere collapse to one member — <c>decimal</c>/<c>numeric</c>/<c>money</c>/<c>smallmoney</c>
/// to <see cref="Decimal"/>, the four whole-date types to <see cref="Date"/>, and so on — so every
/// switch over this enum is exhaustive and adding a type is a compile-time conversation.
/// </summary>
/// <remarks>
/// This exists to get the type name off the per-cell hot path. Resolving the behaviour used to mean
/// a chain of a dozen <c>string.Equals</c> calls plus a <c>ToLowerInvariant()</c> allocation for
/// every value of every column; it is now one allocation-free lookup. See
/// <see cref="ValueConverter.KindFor"/>.
/// </remarks>
internal enum ColumnKind {
    /// <summary>A type name the library does not recognise. Values pass through untouched.</summary>
    Unknown = 0,

    /// <summary><c>sql_variant</c>, <c>geography</c>, <c>geometry</c>, <c>hierarchyid</c>.</summary>
    Unsupported,

    TinyInt,
    SmallInt,
    Int,
    BigInt,
    Bit,

    Real,
    Float,

    /// <summary><c>decimal</c>, <c>numeric</c>, <c>money</c>, <c>smallmoney</c>.</summary>
    Decimal,

    /// <summary><c>char</c>, <c>varchar</c>, <c>text</c>, <c>nchar</c>, <c>nvarchar</c>, <c>ntext</c>.</summary>
    Text,

    /// <summary><c>date</c>, <c>datetime</c>, <c>datetime2</c>, <c>smalldatetime</c>.</summary>
    Date,

    DateTimeOffset,
    Time,
    UniqueIdentifier,
    Xml,
    Json,
    Vector,

    /// <summary><c>binary</c>, <c>varbinary</c>, <c>image</c>.</summary>
    Binary,

    /// <summary><c>timestamp</c>, <c>rowversion</c> — server-generated, never imported.</summary>
    RowVersion
}
