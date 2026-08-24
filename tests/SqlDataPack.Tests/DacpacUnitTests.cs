using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.SqlServer.Dac;
using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Dacpac plumbing that needs no SQL Server: the DacFx option mapping, the AllowObjectDrops guard,
/// the zip-level dacpac editor and the Azure-source decision function.
/// </summary>
public sealed class DacpacUnitTests : IDisposable {
    private const string ModelXmlSeed = """<DataSchemaModel><Model><Element Type="SqlDatabaseOptions"><Property Name="Containment" Value="1" /><Property Name="Collation" Value="Latin1_General_CI_AS" /></Element></Model></DataSchemaModel>""";

    private const string MetadataXmlSeed = """<DacType><Name>Fixture</Name><Version>1.0.0.0</Version></DacType>""";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sdp-dacpac-{Guid.NewGuid():N}.zip");

    public void Dispose() {
        if (File.Exists(_path)) {
            File.Delete(_path);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // DacFx option mapping
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DeploymentOptions_ConservativeDefaults_MapToDacFx() {
        var options = DacpacSchemaManager.CreateDeployOptions(DacpacDeploymentOptions.Default);

        options.AllowIncompatiblePlatform.ShouldBeFalse();
        options.BlockOnPossibleDataLoss.ShouldBeTrue();
        options.DropObjectsNotInSource.ShouldBeFalse();
        options.IncludeTransactionalScripts.ShouldBeTrue();
        options.VerifyDeployment.ShouldBeTrue();

        var excluded = options.ExcludeObjectTypes ?? [];
        excluded.ShouldContain(ObjectType.Users);
        excluded.ShouldContain(ObjectType.Logins);
        excluded.ShouldContain(ObjectType.LinkedServerLogins);
        excluded.ShouldContain(ObjectType.Permissions);
        excluded.ShouldContain(ObjectType.RoleMembership);
        excluded.ShouldContain(ObjectType.ServerRoleMembership);
        excluded.ShouldContain(ObjectType.Files);
        excluded.ShouldContain(ObjectType.Filegroups);

        // Database options are suppressed through ScriptDatabaseOptions, not through ExcludeObjectTypes.
        excluded.ShouldNotContain(ObjectType.DatabaseOptions);
        options.ScriptDatabaseOptions.ShouldBeFalse();

        options.IgnoreFileAndLogFilePath.ShouldBeTrue();
        options.IgnoreFilegroupPlacement.ShouldBeTrue();
        options.IgnoreFileSize.ShouldBeTrue();
    }

    [Fact]
    public void DeploymentOptions_OptIns_MapToDacFx() {
        var options = DacpacSchemaManager.CreateDeployOptions(new DacpacDeploymentOptions {
            AllowIncompatiblePlatform = true,
            BlockOnPossibleDataLoss = false,
            AllowObjectDrops = true,
            DeployUsers = true,
            DeployLogins = true,
            DeployPermissions = true,
            DeployRoleMembership = true,
            DeployDatabaseFiles = true,
            DeployDatabaseOptions = true,
            VerifyDeployment = false
        });

        options.AllowIncompatiblePlatform.ShouldBeTrue();
        options.BlockOnPossibleDataLoss.ShouldBeFalse();
        options.DropObjectsNotInSource.ShouldBeTrue();
        options.VerifyDeployment.ShouldBeFalse();
        options.ScriptDatabaseOptions.ShouldBeTrue();

        // Every exclusion the defaults add must be gone once the caller opts in; an inverted condition
        // here strips users a caller explicitly asked for.
        var excluded = options.ExcludeObjectTypes ?? [];
        excluded.ShouldNotContain(ObjectType.Users);
        excluded.ShouldNotContain(ObjectType.Logins);
        excluded.ShouldNotContain(ObjectType.LinkedServerLogins);
        excluded.ShouldNotContain(ObjectType.Permissions);
        excluded.ShouldNotContain(ObjectType.RoleMembership);
        excluded.ShouldNotContain(ObjectType.ServerRoleMembership);
        excluded.ShouldNotContain(ObjectType.Files);
        excluded.ShouldNotContain(ObjectType.Filegroups);

        options.IgnoreFileAndLogFilePath.ShouldBeFalse();
        options.IgnoreFilegroupPlacement.ShouldBeFalse();
        options.IgnoreFileSize.ShouldBeFalse();
    }

    [Fact]
    public void CaptureOptions_MapToDacFx() {
        var conservative = DacpacSchemaManager.CreateExtractOptions(DacpacCaptureOptions.Default);

        conservative.ExtractAllTableData.ShouldBeFalse();
        conservative.IgnorePermissions.ShouldBeTrue();
        conservative.IgnoreUserLoginMappings.ShouldBeTrue();
        conservative.ExtractApplicationScopedObjectsOnly.ShouldBeFalse();
        conservative.ExtractReferencedServerScopedElements.ShouldBeFalse();
        conservative.VerifyExtraction.ShouldBeFalse();

        var optedIn = DacpacSchemaManager.CreateExtractOptions(new DacpacCaptureOptions {
            ExtractReferencedServerScopedElements = true,
            ExtractApplicationScopedObjectsOnly = true,
            IgnorePermissions = false,
            IgnoreUserLoginMappings = false,
            VerifyExtraction = true
        });

        optedIn.IgnorePermissions.ShouldBeFalse();
        optedIn.IgnoreUserLoginMappings.ShouldBeFalse();
        optedIn.ExtractApplicationScopedObjectsOnly.ShouldBeTrue();
        optedIn.ExtractReferencedServerScopedElements.ShouldBeTrue();
        optedIn.VerifyExtraction.ShouldBeTrue();

        // Not caller-controllable: a dacpac carrying table data would duplicate the whole database
        // into the package next to the SQLite tables.
        optedIn.ExtractAllTableData.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeployOptions_ObjectDropFlags_TrackAllowObjectDrops(bool allowObjectDrops) {
        var options = DacpacSchemaManager.CreateDeployOptions(new DacpacDeploymentOptions { AllowObjectDrops = allowObjectDrops });

        options.DropObjectsNotInSource.ShouldBe(allowObjectDrops);

        // v1_todo 2.2: DacFx defaults these to true, which dropped indexes, constraints, DML triggers
        // and statistics on tables that ARE in the package even with AllowObjectDrops off. They are
        // now tied to the one flag the caller set.
        options.DropConstraintsNotInSource.ShouldBe(allowObjectDrops);
        options.DropIndexesNotInSource.ShouldBe(allowObjectDrops);
        options.DropDmlTriggersNotInSource.ShouldBe(allowObjectDrops);
        options.DropStatisticsNotInSource.ShouldBe(allowObjectDrops);
        options.DropExtendedPropertiesNotInSource.ShouldBe(allowObjectDrops);
    }

    // ---------------------------------------------------------------------------------------------
    // AllowObjectDrops guard
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeployAsync_SelectedTableDacpacRejectsObjectDrops() {
        var payload = Array.Empty<byte>();
        var package = new SchemaPackage("dacpac", "test.dacpac", Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), DateTimeOffset.UtcNow, "Target", "test", DacpacSchemaScope.SelectedExportTables, payload);

        // Points at a host that cannot exist, so the guard is the only thing that can produce a clean
        // exception here: anything the connection attempt throws comes back wrapped.
        const string connectionString = "Server=sqldatapack-unreachable.invalid;Database=Target;User Id=u;Password=p;Trust Server Certificate=true;";

        var exception = await Should.ThrowAsync<SqlDataPackException>(() => DacpacSchemaManager.DeployAsync(connectionString, package, new DacpacDeploymentOptions { AllowObjectDrops = true }, allowDacpacObjectDrops: false, CancellationToken.None));

        exception.Message.ShouldContain("AllowObjectDrops cannot be used");
        exception.Message.ShouldContain("selected-table");
        exception.InnerException.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // DacpacEditor
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Edit_MutatorReturnsFalse_LeavesArchiveUntouched() {
        WriteArchive(ModelXmlSeed, MetadataXmlSeed, withOrigin: true);
        var before = File.ReadAllBytes(_path);

        DacpacEditor.Edit(_path, context => { context.MutateXml("model.xml", _ => false).ShouldBeFalse(); });

        File.ReadAllBytes(_path).SequenceEqual(before).ShouldBeTrue("A no-op edit rewrote the archive.");
    }

    [Fact]
    public void Edit_MutatorReturnsTrue_RewritesEntryAndRecomputesChecksum() {
        WriteArchive(ModelXmlSeed, MetadataXmlSeed, withOrigin: true);
        var beforeModel = ReadEntryBytes("model.xml");
        var untouchedMetadataChecksum = ReadChecksumFromOrigin("/DacMetadata.xml");

        DacpacEditor.Edit(_path, context => { context.MutateXml("model.xml", document => RemoveProperty(document, "Containment")).ShouldBeTrue(); });

        var modelBytes = ReadEntryBytes("model.xml");
        modelBytes.SequenceEqual(beforeModel).ShouldBeFalse();
        HasProperty(modelBytes, "Containment").ShouldBeFalse();

        ReadChecksumFromOrigin("/model.xml").ShouldBe(Convert.ToHexString(SHA256.HashData(modelBytes)));
        ReadChecksumFromOrigin("/DacMetadata.xml").ShouldBe(untouchedMetadataChecksum);
    }

    [Fact]
    public void Edit_MultipleMutationsSameEntry_BatchedOnce() {
        WriteArchive(ModelXmlSeed, MetadataXmlSeed, withOrigin: true);

        XDocument? firstSeen = null;
        XDocument? secondSeen = null;
        DacpacEditor.Edit(_path, context => {
            context.MutateXml("model.xml", document => {
                firstSeen = document;
                return RemoveProperty(document, "Containment");
            }).ShouldBeTrue();
            context.MutateXml("model.xml", document => {
                secondSeen = document;
                return RemoveProperty(document, "Collation");
            }).ShouldBeTrue();
        });

        // Same document instance both times: the entry was parsed once and mutations accumulated.
        secondSeen.ShouldBeSameAs(firstSeen);

        var modelBytes = ReadEntryBytes("model.xml");
        HasProperty(modelBytes, "Containment").ShouldBeFalse();
        HasProperty(modelBytes, "Collation").ShouldBeFalse();
        CountEntries("model.xml").ShouldBe(1);
        ReadChecksumFromOrigin("/model.xml").ShouldBe(Convert.ToHexString(SHA256.HashData(modelBytes)));
    }

    [Fact]
    public void Edit_MissingEntry_MutateReturnsFalse() {
        WriteArchive(ModelXmlSeed, MetadataXmlSeed, withOrigin: true);
        var before = File.ReadAllBytes(_path);

        DacpacEditor.Edit(_path, context => { context.MutateXml("PostDeploy.sql.xml", _ => true).ShouldBeFalse(); });

        File.ReadAllBytes(_path).SequenceEqual(before).ShouldBeTrue();
    }

    [Fact]
    public void Edit_NoOriginXml_StillCompletes() {
        WriteArchive(ModelXmlSeed, MetadataXmlSeed, withOrigin: false);

        DacpacEditor.Edit(_path, context => { context.MutateXml("model.xml", document => RemoveProperty(document, "Containment")).ShouldBeTrue(); });

        HasProperty(ReadEntryBytes("model.xml"), "Containment").ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Azure decision function
    // ---------------------------------------------------------------------------------------------

    // EngineEdition cheat-sheet:
    //   2  = Standard / on-prem  | 3  = Enterprise / on-prem | 4  = Express / on-prem
    //   5  = Azure SQL Database  | 8  = Azure SQL MI         | 11 = Azure SQL Edge
    //   12 = Azure Synapse SQL pool
    [Theory]
    // Azure source -> on-prem target: rewrite.
    [InlineData(5, 3, true)]
    [InlineData(8, 3, true)]
    [InlineData(11, 2, true)]
    [InlineData(12, 4, true)]
    // Azure source -> Azure target: leave the contained users alone.
    [InlineData(5, 5, false)]
    [InlineData(8, 5, false)]
    // On-prem source: never needs the rewrite, whichever target.
    [InlineData(3, 3, false)]
    [InlineData(2, 4, false)]
    [InlineData(3, 5, false)]
    // Unknown source (package predates the source stamp) falls back to the target check.
    [InlineData(null, 3, true)]
    [InlineData(null, 5, false)]
    public void ShouldAdaptAzureSourceForOnPremTarget_ReturnsExpected(int? sourceEdition, int targetEdition, bool expected) {
        DacpacSchemaManager.ShouldAdaptAzureSourceForOnPremTarget(sourceEdition, targetEdition).ShouldBe(expected);
    }

    // ---------------------------------------------------------------------------------------------

    private static bool RemoveProperty(XDocument document, string propertyName) {
        var matches = document.Descendants().Where(e => e.Name.LocalName == "Property" && (string?)e.Attribute("Name") == propertyName).ToList();
        if (matches.Count == 0) {
            return false;
        }

        foreach (var match in matches) {
            match.Remove();
        }

        return true;
    }

    private static bool HasProperty(byte[] entryBytes, string propertyName) {
        using var stream = new MemoryStream(entryBytes);
        return XDocument.Load(stream).Descendants().Any(e => e.Name.LocalName == "Property" && (string?)e.Attribute("Name") == propertyName);
    }

    private void WriteArchive(string modelXml, string metadataXml, bool withOrigin) {
        if (File.Exists(_path)) {
            File.Delete(_path);
        }

        var modelBytes = Encoding.UTF8.GetBytes(modelXml);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataXml);

        // Seeded with correct checksums so a stale value after an edit can only come from the editor.
        var originXml = $"""<DacOrigin><Checksums><Checksum Uri="/model.xml">{Convert.ToHexString(SHA256.HashData(modelBytes))}</Checksum><Checksum Uri="/DacMetadata.xml">{Convert.ToHexString(SHA256.HashData(metadataBytes))}</Checksum></Checksums></DacOrigin>""";

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
            WriteEntry(archive, "model.xml", modelBytes);
            WriteEntry(archive, "DacMetadata.xml", metadataBytes);
            if (withOrigin) {
                WriteEntry(archive, "Origin.xml", Encoding.UTF8.GetBytes(originXml));
            }
        }

        File.WriteAllBytes(_path, buffer.ToArray());
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes) {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private byte[] ReadEntryBytes(string name) {
        using var archive = ZipFile.OpenRead(_path);
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Entry '{name}' not found.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private int CountEntries(string name) {
        using var archive = ZipFile.OpenRead(_path);
        return archive.Entries.Count(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
    }

    private string ReadChecksumFromOrigin(string uri) {
        using var archive = ZipFile.OpenRead(_path);
        var entry = archive.GetEntry("Origin.xml") ?? throw new InvalidOperationException("Origin.xml not found.");
        using var stream = entry.Open();
        return XDocument.Load(stream).Descendants().First(e => e.Name.LocalName == "Checksum" && string.Equals((string?)e.Attribute("Uri"), uri, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
