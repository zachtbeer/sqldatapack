using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal static class ImportPlanner {
    public static IReadOnlyList<TableName> BuildImportOrder(IReadOnlyList<TableName> tables, IReadOnlyList<ForeignKeyMetadata> foreignKeys) {
        var tableByName = tables.ToDictionary(t => t.FullName, t => t, StringComparer.OrdinalIgnoreCase);
        var dependencies = tableByName.Keys.ToDictionary(name => name, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var foreignKey in foreignKeys) {
            if (!tableByName.ContainsKey(foreignKey.ParentTable.FullName) || !tableByName.ContainsKey(foreignKey.ReferencedTable.FullName)) {
                continue;
            }

            if (string.Equals(foreignKey.ParentTable.FullName, foreignKey.ReferencedTable.FullName, StringComparison.OrdinalIgnoreCase)) {
                throw new SqlDataPackException($"Import plan cannot be generated because selected table '{foreignKey.ParentTable.FullName}' has a self-referencing foreign key. Exclude the table or prepare the target schema before import.");
            }

            dependencies[foreignKey.ParentTable.FullName].Add(foreignKey.ReferencedTable.FullName);
        }

        var remaining = new Dictionary<string, TableName>(tableByName, StringComparer.OrdinalIgnoreCase);
        var result = new List<TableName>();

        while (remaining.Count > 0) {
            var ready = remaining.Values.Where(t => dependencies[t.FullName].All(dependency => !remaining.ContainsKey(dependency))).OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToArray();

            if (ready.Length == 0) {
                var cycleTables = string.Join(", ", FindCycle(remaining, dependencies).Select(t => t.FullName).Order(StringComparer.OrdinalIgnoreCase));
                throw new SqlDataPackException($"Import plan cannot be generated because selected tables contain a foreign-key cycle: {cycleTables}. Exclude one or more tables from the cycle.");
            }

            foreach (var table in ready) {
                result.Add(table);
                remaining.Remove(table.FullName);
            }
        }

        return result;
    }

    private static IReadOnlyList<TableName> FindCycle(IReadOnlyDictionary<string, TableName> remaining, IReadOnlyDictionary<string, HashSet<string>> dependencies) {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var table in remaining.Values.OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)) {
            var cycle = Visit(table.FullName, remaining, dependencies, visiting, visited, path);
            if (cycle.Count > 0) {
                return cycle.Select(name => remaining[name]).ToArray();
            }
        }

        return remaining.Values.OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> Visit(string tableName, IReadOnlyDictionary<string, TableName> remaining, IReadOnlyDictionary<string, HashSet<string>> dependencies, ISet<string> visiting, ISet<string> visited, List<string> path) {
        if (visited.Contains(tableName)) {
            return [];
        }

        if (visiting.Contains(tableName)) {
            var start = path.FindIndex(name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase));
            return path.Skip(start).ToArray();
        }

        visiting.Add(tableName);
        path.Add(tableName);

        foreach (var dependency in dependencies[tableName].Where(remaining.ContainsKey).Order(StringComparer.OrdinalIgnoreCase)) {
            var cycle = Visit(dependency, remaining, dependencies, visiting, visited, path);
            if (cycle.Count > 0) {
                return cycle;
            }
        }

        visiting.Remove(tableName);
        visited.Add(tableName);
        path.RemoveAt(path.Count - 1);
        return [];
    }
}
