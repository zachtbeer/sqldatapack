using SqlDataPack.Models;
using SqlDataPack.Transformations;

namespace SqlDataPack.Internal;

/// <summary>
/// Records that a column was transformed. Deliberately thin: the column, the transformer type, and the
/// transformer's own non-secret configuration. No secrets, no keys, no original values.
/// </summary>
internal sealed record TransformationMetadata(TableName Table, string Column, string TransformerType, string? Configuration);

internal static class TransformationNaming {
    /// <summary>
    /// Names a transformer for the package. Built-ins are recorded by type name; everything else is
    /// <c>Custom</c>. No lambda-name reflection, and a custom transformer is never asked for an identity.
    /// </summary>
    public static string TypeNameFor(IValueTransformer transformer) => transformer is BuiltInTransformer ? transformer.GetType().Name : "Custom";

    public static string? ConfigurationFor(IValueTransformer transformer) {
        return transformer is BuiltInTransformer builtIn && builtIn.Configuration.Length > 0 ? builtIn.Configuration : null;
    }
}

/// <summary>
/// Resolves <see cref="ExportOptions.Transformations"/> against the export plan: validates every configured
/// path once, and hands the writer a per-column array for the tables that have any.
/// </summary>
internal static class TransformationBinder {
    /// <summary>
    /// Validates the configured column paths against the planned tables and returns what the package should
    /// record. Runs at plan time, so preflight rejects a typo before a single row is read.
    /// </summary>
    public static IReadOnlyList<TransformationMetadata> Validate(IReadOnlyList<TableMetadata> tables, ExportOptions options) {
        if (options.Transformations.Count == 0) {
            return [];
        }

        var tablesByName = tables.ToDictionary(t => t.Name.FullName, StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TransformationMetadata>();

        foreach (var (path, transformer) in options.Transformations) {
            if (transformer is null) {
                throw new SqlDataPackException($"Transformation '{path}' has no transformer. Assign an IValueTransformer, for example new EmailPseudonymizer().");
            }

            var parsed = SqlDataPackIdentifier.ParseColumnPath(path, "Transformation");
            var fullTableName = $"{parsed.Schema}.{parsed.Table}";
            if (!tablesByName.TryGetValue(fullTableName, out var table)) {
                throw new SqlDataPackException($"Transformation '{path}' references a table outside the selected export scope.");
            }

            var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, parsed.Column, StringComparison.OrdinalIgnoreCase))
                         ?? throw new SqlDataPackException($"Transformation '{path}' references a column that does not exist on '{fullTableName}'.");

            if (!column.IsExported) {
                var reason = column.IsComputed ? "is computed, so it is never exported" : "is excluded from the export";
                throw new SqlDataPackException($"Transformation '{path}' targets a column that {reason}. Remove the transformation or stop excluding the column.");
            }

            var key = $"{table.Name.FullName}.{column.Name}";
            if (seen.TryGetValue(key, out var existing)) {
                throw new SqlDataPackException($"Transformations '{existing}' and '{path}' both target column '{key}'. A column can have one transformer only.");
            }

            seen.Add(key, path);
            result.Add(new TransformationMetadata(table.Name, column.Name, TransformationNaming.TypeNameFor(transformer), TransformationNaming.ConfigurationFor(transformer)));
        }

        return result;
    }

    /// <summary>
    /// Normalizes the configured paths into one <c>schema.table.column</c> lookup, so binding a table costs a
    /// dictionary hit per column instead of re-parsing every configured path.
    /// </summary>
    public static IReadOnlyDictionary<string, IValueTransformer> Normalize(ExportOptions options) {
        if (options.Transformations.Count == 0) {
            return ReadOnlyEmpty;
        }

        var byColumn = new Dictionary<string, IValueTransformer>(options.Transformations.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, transformer) in options.Transformations) {
            var parsed = SqlDataPackIdentifier.ParseColumnPath(path, "Transformation");
            byColumn[$"{parsed.Schema}.{parsed.Table}.{parsed.Column}"] = transformer;
        }

        return byColumn;
    }

    /// <summary>
    /// Builds the per-column transform array for one table, or <see langword="null"/> when nothing on the
    /// table is transformed — which is what keeps an export with no transformations on its original path.
    /// </summary>
    public static ColumnTransform?[]? CreateForTable(TableMetadata table, IReadOnlyDictionary<string, IValueTransformer> byColumn, ExportSecret? secret) {
        if (byColumn.Count == 0) {
            return null;
        }

        var columns = table.ExportedColumns;
        ColumnTransform?[]? transforms = null;
        for (var i = 0; i < columns.Count; i++) {
            if (!byColumn.TryGetValue($"{table.Name.FullName}.{columns[i].Name}", out var transformer)) {
                continue;
            }

            transforms ??= new ColumnTransform?[columns.Count];
            transforms[i] = new ColumnTransform(transformer, columns[i], secret);
        }

        return transforms;
    }

    private static readonly Dictionary<string, IValueTransformer> ReadOnlyEmpty = new(StringComparer.OrdinalIgnoreCase);
}
