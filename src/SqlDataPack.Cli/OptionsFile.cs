using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SqlDataPack.Models;

namespace SqlDataPack.Cli;

/// <summary>
/// Loads an <c>--options</c> file into <see cref="ExportOptions"/> or <see cref="ImportOptions"/>.
///
/// The JSON maps straight onto the library's option objects, so the CLI never has to grow a flag
/// per property and the file doubles as a reviewable, checked-in description of a slice. That is
/// also why it refuses to carry a connection string: the file is meant to be committed.
/// </summary>
internal static class OptionsFile {
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static ExportOptions LoadExportOptions(string path) => Load<ExportOptions>(path);

    public static ImportOptions LoadImportOptions(string path) => Load<ImportOptions>(path);

    private static T Load<T>(string path) where T : class {
        if (!File.Exists(path)) {
            throw new CliUsageException($"Options file not found: {path}");
        }

        var json = File.ReadAllText(path);

        JsonDocument document;
        try {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex) {
            throw new CliUsageException($"Options file {path} is not valid JSON: {ex.Message}");
        }

        using (document) {
            RejectConnectionStrings(document.RootElement, path, "$");
        }

        try {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                   ?? throw new CliUsageException($"Options file {path} deserialized to nothing. It should contain a JSON object.");
        }
        catch (JsonException ex) {
            throw new CliUsageException($"Options file {path} could not be read: {ex.Message}");
        }
        catch (NotSupportedException ex) {
            throw new CliUsageException($"Options file {path} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the document looking for anything that smells like a credential. Neither options
    /// object has a connection string property, so without this the key would be reported as an
    /// unknown member, which does not tell anyone why it is refused.
    /// </summary>
    private static void RejectConnectionStrings(JsonElement element, string path, string jsonPath) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) {
                    if (property.Name.Contains("connection", StringComparison.OrdinalIgnoreCase)) {
                        throw ConnectionStringRejected(path, $"{jsonPath}.{property.Name}");
                    }

                    RejectConnectionStrings(property.Value, path, $"{jsonPath}.{property.Name}");
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) {
                    RejectConnectionStrings(item, path, $"{jsonPath}[{index++}]");
                }

                break;

            case JsonValueKind.String:
                if (LooksLikeConnectionString(element.GetString())) {
                    throw ConnectionStringRejected(path, jsonPath);
                }

                break;
        }
    }

    private static bool LooksLikeConnectionString(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return false;
        }

        bool HasKeyword(string keyword) => value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        // A WHERE clause can legitimately mention a column called Password, so a single hit is not
        // enough. Requiring a server or data source alongside it keeps false positives down.
        var hasServer = HasKeyword("server=") || HasKeyword("data source=");
        var hasSecret = HasKeyword("password=") || HasKeyword("integrated security=") || HasKeyword("authentication=");

        return hasServer && (hasSecret || HasKeyword("initial catalog=") || HasKeyword("database="));
    }

    private static CliUsageException ConnectionStringRejected(string path, string jsonPath) =>
        new($"Options file {path} looks like it carries a connection string at {jsonPath}. " +
            "An options file describes a slice and is meant to be committed, so it must not hold credentials. " +
            "Pass the connection string with --connection or set SQLDATAPACK_CONNECTION.");

    private static JsonSerializerOptions CreateSerializerOptions() {
        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // A silently ignored typo in an options file produces a slice that is quietly wrong,
            // which is worse than a failed command.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver {
                Modifiers = { DropNonSerializableProperties }
            }
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new GlobalWhereClauseConverter());
        return options;
    }

    /// <summary>
    /// Progress and Logger are callbacks, not configuration. Leaving them on the contract would
    /// let an options file fail with a confusing serializer error instead of "unknown member".
    /// </summary>
    private static void DropNonSerializableProperties(JsonTypeInfo typeInfo) {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--) {
            var propertyType = typeInfo.Properties[i].PropertyType;
            var isCallback = propertyType == typeof(ILogger)
                             || (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(IProgress<>));

            if (isCallback) {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// <see cref="GlobalWhereClause"/> has two constructors, so the serializer cannot pick one on
    /// its own. Accepts either a single columnName or a columnNames array.
    /// </summary>
    private sealed class GlobalWhereClauseConverter : JsonConverter<GlobalWhereClause> {
        public override GlobalWhereClause Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType != JsonTokenType.StartObject) {
                throw new JsonException("A globalWhereClauses entry must be an object with columnNames and whereClause.");
            }

            List<string> columnNames = [];
            string? whereClause = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                if (reader.TokenType != JsonTokenType.PropertyName) {
                    continue;
                }

                var name = reader.GetString()!;
                reader.Read();

                if (name.Equals("columnName", StringComparison.OrdinalIgnoreCase)) {
                    columnNames.Add(reader.GetString() ?? throw new JsonException("columnName must be a string."));
                }
                else if (name.Equals("columnNames", StringComparison.OrdinalIgnoreCase)) {
                    if (reader.TokenType != JsonTokenType.StartArray) {
                        throw new JsonException("columnNames must be an array of strings.");
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
                        columnNames.Add(reader.GetString() ?? throw new JsonException("columnNames must be an array of strings."));
                    }
                }
                else if (name.Equals("whereClause", StringComparison.OrdinalIgnoreCase)) {
                    whereClause = reader.GetString();
                }
                else {
                    throw new JsonException($"Unknown property '{name}' on a globalWhereClauses entry. Expected columnName or columnNames, and whereClause.");
                }
            }

            if (columnNames.Count == 0) {
                throw new JsonException("A globalWhereClauses entry needs columnName or columnNames.");
            }

            if (string.IsNullOrWhiteSpace(whereClause)) {
                throw new JsonException("A globalWhereClauses entry needs a whereClause.");
            }

            return new GlobalWhereClause(columnNames, whereClause);
        }

        public override void Write(Utf8JsonWriter writer, GlobalWhereClause value, JsonSerializerOptions options) {
            writer.WriteStartObject();
            writer.WriteStartArray("columnNames");
            foreach (var columnName in value.ColumnNames) {
                writer.WriteStringValue(columnName);
            }

            writer.WriteEndArray();
            writer.WriteString("whereClause", value.WhereClause);
            writer.WriteEndObject();
        }
    }
}
