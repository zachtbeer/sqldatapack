using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace SqlDataPack.Internal;

internal static class DacpacEditor {
    public static void Edit(string dacpacPath, Action<DacpacEditContext> mutate) {
        ArgumentNullException.ThrowIfNull(mutate);

        using var archive = ZipFile.Open(dacpacPath, ZipArchiveMode.Update);
        var context = new DacpacEditContext(archive);
        mutate(context);
        context.Commit();
    }
}

internal sealed class DacpacEditContext {
    private const string OriginEntryName = "Origin.xml";

    private readonly ZipArchive _archive;
    private readonly Dictionary<string, XDocument> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    internal DacpacEditContext(ZipArchive archive) {
        _archive = archive;
    }

    public bool MutateXml(string entryName, Func<XDocument, bool> mutate) {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        ArgumentNullException.ThrowIfNull(mutate);

        var document = LoadXml(entryName);
        if (document is null) {
            return false;
        }

        if (!mutate(document)) {
            return false;
        }

        _dirty.Add(entryName);
        return true;
    }

    internal void Commit() {
        if (_dirty.Count == 0) {
            return;
        }

        var newChecksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entryName in _dirty.ToList()) {
            if (string.Equals(entryName, OriginEntryName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var bytes = SerializeXml(_loaded[entryName]);
            ReplaceEntry(entryName, bytes);
            newChecksums[entryName] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        if (newChecksums.Count > 0) {
            var originDocument = LoadXml(OriginEntryName);
            if (originDocument is not null && UpdateOriginChecksums(originDocument, newChecksums)) {
                _dirty.Add(OriginEntryName);
            }
        }

        if (_dirty.Contains(OriginEntryName) && _loaded.TryGetValue(OriginEntryName, out var origin)) {
            var originBytes = SerializeXml(origin);
            ReplaceEntry(OriginEntryName, originBytes);
        }
    }

    private XDocument? LoadXml(string entryName) {
        if (_loaded.TryGetValue(entryName, out var cached)) {
            return cached;
        }

        var entry = _archive.GetEntry(entryName);
        if (entry is null) {
            return null;
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        _loaded[entryName] = document;
        return document;
    }

    private void ReplaceEntry(string entryName, byte[] bytes) {
        _archive.GetEntry(entryName)?.Delete();
        var entry = _archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] SerializeXml(XDocument document) {
        using var stream = new MemoryStream();
        document.Save(stream, SaveOptions.DisableFormatting);
        return stream.ToArray();
    }

    private static bool UpdateOriginChecksums(XDocument originDocument, IReadOnlyDictionary<string, string> newChecksums) {
        var changed = false;
        foreach (var (entryName, checksum) in newChecksums) {
            var uri = "/" + entryName;
            var elements = originDocument.Descendants().Where(element => element.Name.LocalName == "Checksum" && string.Equals((string?)element.Attribute("Uri"), uri, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var element in elements) {
                if (!string.Equals(element.Value, checksum, StringComparison.OrdinalIgnoreCase)) {
                    element.SetValue(checksum);
                    changed = true;
                }
            }
        }

        return changed;
    }
}
