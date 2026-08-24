using System.Text;
using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal sealed record TableName(string Schema, string Name) {
    public string FullName => $"{Schema}.{Name}";
}

internal static class SqlDataPackIdentifier {
    public static string QuoteSqlServerName(string value) {
        return $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static string QuoteSqlServerTable(TableName table) {
        return $"{QuoteSqlServerName(table.Schema)}.{QuoteSqlServerName(table.Name)}";
    }

    public static string QuoteSqliteName(string value) {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string ToSqliteDataTableName(TableName table, string? dataTablePrefix = null) {
        var name = Sanitize(table.Schema) + "__" + Sanitize(table.Name);
        var prefix = NormalizeSqliteDataTablePrefix(dataTablePrefix);
        return prefix.Length == 0 ? name : prefix + "_" + name;
    }

    public static string NormalizeSqliteDataTablePrefix(string? value) {
        var prefix = value?.Trim() ?? string.Empty;
        if (prefix.Length == 0) {
            return string.Empty;
        }

        if (prefix.Any(ch => !IsAsciiLetterOrDigit(ch) && ch != '_')) {
            throw new SqlDataPackException("DataTablePrefix can contain only letters, digits, and underscores.");
        }

        return prefix;
    }

    /// <summary>Wall-clock ceiling for one wildcard pattern match, so a pathological pattern cannot hang an export.</summary>
    public static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromSeconds(1);

    public static bool MatchesPattern(TableName table, string pattern) {
        var normalized = table.FullName;
        if (!pattern.Contains('*', StringComparison.Ordinal)) {
            return string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase) || string.Equals(table.Name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        try {
            return System.Text.RegularExpressions.Regex.IsMatch(normalized, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, PatternMatchTimeout);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex) {
            throw new SqlDataPackException($"Table pattern '{pattern}' is too complex to evaluate.", ex);
        }
    }

    /// <summary>
    /// Identifies the table SSMS's "Database Diagrams" feature creates (<c>dbo.sysdiagrams</c>). It is a regular
    /// user table (<c>is_ms_shipped = 0</c>), so the baseline system-table filter does not exclude it.
    /// </summary>
    public static bool IsSsmsDiagramTable(TableName table) => string.Equals(table.Schema, "dbo", StringComparison.OrdinalIgnoreCase) && string.Equals(table.Name, "sysdiagrams", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects a planned table set in which two source tables map to the same SQLite data table name.
    /// <see cref="Sanitize"/> lowercases and folds every non-alphanumeric character to <c>_</c>, so
    /// <c>dbo.Order-Items</c> and <c>dbo.Order_Items</c> both become <c>dbo__order_items</c>, as do
    /// <c>dbo.Orders</c> and <c>dbo.ORDERS</c> under a case-sensitive server collation. Without this check the
    /// export reaches <c>CREATE TABLE</c> and fails with a raw SQLite "table already exists" error that names
    /// neither source table.
    /// </summary>
    public static void ValidateSqliteDataTableNamesUnique(IReadOnlyList<TableMetadata> tables) {
        // SQLite folds only ASCII A-Z when it compares table names, so FoldSqliteIdentifier does the same and the
        // grouping itself is ordinal. StringComparer.OrdinalIgnoreCase would be wrong: it applies full invariant
        // case folding, and generated names keep any non-ASCII letter the source name had (Sanitize drops only
        // non-alphanumeric characters), so it would report a collision for a pair like 'ſ' and 's' that SQLite
        // treats as two distinct tables.
        var collision = tables.GroupBy(t => FoldSqliteIdentifier(t.SqliteTableName), StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (collision is null) {
            return;
        }

        var colliding = collision.OrderBy(t => t.Name.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
        var sources = string.Join(", ", colliding.Select(t => $"'{t.Name.FullName}'"));
        throw new SqlDataPackException($"Source tables {sources} map to the same SQLite table name '{colliding[0].SqliteTableName}'. " + "SQLite table names are lowercased with every character that is not a letter or digit replaced by '_', " + "so source tables differing only in case or punctuation collide. " + "Exclude all but one of these tables from the export scope.");
    }

    /// <summary>
    /// Rejects a planned table set in which a generated SQLite data table name lands in a reserved namespace.
    /// SQLite refuses <c>CREATE TABLE</c> for any name beginning <c>sqlite_</c>, and the package keeps <c>zsdp_</c>
    /// for its own metadata tables. Both are reachable: with no <see cref="ExportOptions.DataTablePrefix"/>
    /// a source schema named <c>sqlite</c> or <c>zsdp</c> produces <c>sqlite__customers</c> or
    /// <c>zsdp__customers</c>, and a caller-supplied prefix of <c>"sqlite"</c> or <c>"zsdp"</c> does the same for
    /// every table. Without this check the export reaches <c>CREATE TABLE</c> and fails with a raw SQLite
    /// "object name reserved for internal use" or "table already exists" error that names no source table.
    /// </summary>
    /// <remarks>
    /// The check runs on the final generated name rather than on the prefix, because the schema and table names
    /// contribute to it just as the prefix does and either half can reach a reserved namespace on its own.
    /// </remarks>
    public static void ValidateSqliteDataTableNamesNotReserved(IReadOnlyList<TableMetadata> tables) {
        foreach (var table in tables) {
            // Folded the same way as ValidateSqliteDataTableNamesUnique, so 'SQLite_' is caught alongside 'sqlite_'.
            var folded = FoldSqliteIdentifier(table.SqliteTableName);
            var reserved = folded.StartsWith("sqlite_", StringComparison.Ordinal) ? "sqlite_" : folded.StartsWith("zsdp_", StringComparison.Ordinal) ? "zsdp_" : null;
            if (reserved is null) {
                continue;
            }

            var reason = reserved == "sqlite_" ? "SQLite reserves table names beginning 'sqlite_' for internal use" : "SqlDataPack reserves table names beginning 'zsdp_' for the package's own metadata tables";
            throw new SqlDataPackException($"Source table '{table.Name.FullName}' maps to the SQLite table name '{table.SqliteTableName}', which is reserved. {reason}. " + "Set DataTablePrefix to move exported data tables out of the reserved namespace, or exclude this table from the export scope.");
        }
    }

    public static (string Schema, string Table, string Column) ParseColumnPath(string value) {
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) {
            throw new SqlDataPackException($"Column exclusion '{value}' is invalid. Use '<schema>.<table>.<column>', for example 'dbo.Customers.LegacyColumn'.");
        }

        return (parts[0], parts[1], parts[2]);
    }

    private static string Sanitize(string value) {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value) {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString().ToLowerInvariant();
    }

    private static string FoldSqliteIdentifier(string value) {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value) {
            builder.Append(ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch);
        }

        return builder.ToString();
    }

    private static bool IsAsciiLetterOrDigit(char value) {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}
