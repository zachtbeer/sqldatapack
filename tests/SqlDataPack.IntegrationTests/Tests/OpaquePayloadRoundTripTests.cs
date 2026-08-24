using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// xml, native json and vector values: the types that land in SQLite as TEXT rather than as a scalar. xml and
/// json are stored verbatim; a vector is re-serialized as a JSON array and rebuilt on the way back. Each test
/// asserts the SQLite storage shape and the captured metadata in the same pass as the round trip, so one
/// container run covers export and import for a subject.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class OpaquePayloadRoundTripTests {
    private const string TypeVaultFixture = "type-vault.sql";

    private static readonly string[] XmlPayloadNames = ["element-attribute", "namespaced", "mixed-content"];

    private readonly SqlServerContainerFixture _fixture;

    public OpaquePayloadRoundTripTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task RoundTrip_XmlColumn_PreservesDocumentsExactly() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVaultFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVaultFixture);
        await using var sqlite = new SqliteTempFileHarness();

        var sourceDocuments = new Dictionary<string, string>();
        foreach (var name in XmlPayloadNames) {
            sourceDocuments[name] = await source.ScalarHexAsync($"SELECT CONVERT(VARBINARY(MAX), CONVERT(NVARCHAR(MAX), PayloadXml)) FROM dbo.DocumentPayloads WHERE PayloadName = N'{name}'");
        }

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.DocumentPayloads"));

        exportResult.TableCount.ShouldBe(1);
        exportResult.RowCount.ShouldBe(4);

        await using (var package = await sqlite.OpenConnectionAsync()) {
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.DocumentPayloads", "PayloadXml", typeName: "xml", isNullable: true);
            (await package.ScalarStringAsync("SELECT type FROM pragma_table_info('dbo__documentpayloads') WHERE name = 'PayloadXml'")).ShouldBe("TEXT");

            foreach (var name in XmlPayloadNames) {
                (await package.ScalarStringAsync($"SELECT typeof(PayloadXml) FROM dbo__documentpayloads WHERE PayloadName = '{name}'")).ShouldBe("text");
                var stored = await SqlitePackageAssertions.ReadHexAsync(package, $"SELECT PayloadXml FROM dbo__documentpayloads WHERE PayloadName = '{name}'");
                stored.ShouldBe(sourceDocuments[name], $"'{name}': the packaged xml is not the source document's own serialization.");
            }

            (await package.ScalarIntAsync("SELECT COUNT(*) FROM dbo__documentpayloads WHERE PayloadName = 'null-payload' AND PayloadXml IS NULL")).ShouldBe(1);
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(4);

        foreach (var name in XmlPayloadNames) {
            var imported = await target.ScalarHexAsync($"SELECT CONVERT(VARBINARY(MAX), CONVERT(NVARCHAR(MAX), PayloadXml)) FROM dbo.DocumentPayloads WHERE PayloadName = N'{name}'");
            imported.ShouldBe(sourceDocuments[name], $"'{name}': the imported xml is not byte-identical to the source document.");
        }

        // The hex compare above proves target == source. These read the target through SQL Server's own XML
        // methods to pin what the documents actually are, so a fixture edit that drops a namespace binding or
        // collapses mixed content fails on a named XPath instead of quietly weakening the round trip.
        (await target.ScalarIntAsync("""
                                     SELECT COUNT(*)
                                     FROM dbo.DocumentPayloads
                                     WHERE PayloadName = N'element-attribute'
                                       AND PayloadXml.value('(/root/item/@id)[1]', 'int') = 1
                                       AND PayloadXml.value('(/root/item)[1]', 'nvarchar(20)') = N'alpha'
                                     """)).ShouldBe(1);
        (await target.ScalarIntAsync("""
                                     SELECT COUNT(*)
                                     FROM dbo.DocumentPayloads
                                     WHERE PayloadName = N'namespaced'
                                       AND PayloadXml.value('declare namespace ns="urn:test"; (/ns:root/ns:item)[1]', 'nvarchar(20)') = N'value'
                                       AND PayloadXml.value('declare namespace ns="urn:test"; (/ns:root/ns:item/@name)[1]', 'nvarchar(20)') = N'beta'
                                     """)).ShouldBe(1);
        (await target.ScalarIntAsync("""
                                     SELECT COUNT(*)
                                     FROM dbo.DocumentPayloads
                                     WHERE PayloadName = N'mixed-content'
                                       AND PayloadXml.value('(/root/text()[1])[1]', 'nvarchar(20)') = N'leading '
                                       AND PayloadXml.value('(/root/b)[1]', 'nvarchar(20)') = N'bold'
                                       AND PayloadXml.value('(/root/text()[2])[1]', 'nvarchar(20)') = N' trailing'
                                     """)).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DocumentPayloads WHERE PayloadName = N'null-payload' AND PayloadXml IS NULL")).ShouldBe(1);
    }

    /// <summary>
    /// Native json takes its own branch in the value converter (<c>SqlJson</c>, next to xml's <c>SqlXml</c>)
    /// and only exists on SQL Server 2025, so it is the half of the text-payload path the xml test cannot
    /// reach.
    /// </summary>
    [SkippableFact]
    public async Task RoundTrip_NativeJsonColumn_PreservesDocumentsExactly() {
        // Native json is the 2025 gate (SupportsNativeJson is defined as it); Requires names the image and
        // the env var in the skip reason.
        Requires.SqlServer2025(_fixture);

        const string jsonProjection = """
                                      SELECT PayloadName,
                                             JSON_VALUE(PayloadJson, '$.id'),
                                             JSON_QUERY(PayloadJson, '$.tags'),
                                             JSON_VALUE(PayloadJson, '$.tags[1]'),
                                             JSON_QUERY(PayloadJson, '$.profile'),
                                             JSON_VALUE(PayloadJson, '$.profile.active'),
                                             JSON_VALUE(PayloadJson, '$.profile.score')
                                      FROM dbo.DocumentPayloads
                                      ORDER BY PayloadName
                                      """;

        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVaultFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVaultFixture);
        await using var sqlite = new SqliteTempFileHarness();

        var sourceProjection = await source.ReadRowsAsync(jsonProjection);
        var sourceDocuments = new Dictionary<string, string>();
        foreach (var name in new[] { "element-attribute", "namespaced" }) {
            sourceDocuments[name] = await source.ScalarHexAsync($"SELECT CONVERT(VARBINARY(MAX), CONVERT(NVARCHAR(MAX), PayloadJson)) FROM dbo.DocumentPayloads WHERE PayloadName = N'{name}'");
        }

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.DocumentPayloads"));

        exportResult.TableCount.ShouldBe(1);
        exportResult.RowCount.ShouldBe(4);

        await using (var package = await sqlite.OpenConnectionAsync()) {
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.DocumentPayloads", "PayloadJson", typeName: "json", isNullable: true);
            (await package.ScalarStringAsync("SELECT type FROM pragma_table_info('dbo__documentpayloads') WHERE name = 'PayloadJson'")).ShouldBe("TEXT");

            foreach (var (name, expected) in sourceDocuments) {
                (await package.ScalarStringAsync($"SELECT typeof(PayloadJson) FROM dbo__documentpayloads WHERE PayloadName = '{name}'")).ShouldBe("text");
                var stored = await SqlitePackageAssertions.ReadHexAsync(package, $"SELECT PayloadJson FROM dbo__documentpayloads WHERE PayloadName = '{name}'");
                stored.ShouldBe(expected, $"'{name}': the packaged json is not the source document's own text.");
            }

            (await package.ScalarIntAsync("SELECT COUNT(*) FROM dbo__documentpayloads WHERE PayloadJson IS NULL")).ShouldBe(2);
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(4);
        (await target.ReadRowsAsync(jsonProjection)).ShouldBe(sourceProjection);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.DocumentPayloads WHERE PayloadJson IS NULL")).ShouldBe(2);

        foreach (var (name, expected) in sourceDocuments) {
            var imported = await target.ScalarHexAsync($"SELECT CONVERT(VARBINARY(MAX), CONVERT(NVARCHAR(MAX), PayloadJson)) FROM dbo.DocumentPayloads WHERE PayloadName = N'{name}'");
            imported.ShouldBe(expected, $"'{name}': the imported json is not byte-identical to the source document.");
        }
    }

    [SkippableFact]
    public async Task RoundTrip_Float32Vector_IsBitExact() {
        Requires.Vector(_fixture);

        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVaultFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVaultFixture);
        await using var sqlite = new SqliteTempFileHarness();

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.VectorSamples"));

        exportResult.TableCount.ShouldBe(1);
        exportResult.RowCount.ShouldBe(4);

        await using (var package = await sqlite.OpenConnectionAsync()) {
            // vector_base_type 0 is float32 (1 is float16). Import rebuilds the SqlVector from the stored JSON
            // array's own length, so vector_dimensions is pure captured metadata -- it reaches the manifest and
            // the schema hash, and 0 or NULL here would be indistinguishable from a non-vector column.
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.VectorSamples", "Embedding", typeName: "vector", isNullable: true, vectorBaseType: 0, vectorDimensions: 3);
            (await package.ScalarStringAsync("SELECT type FROM pragma_table_info('dbo__vectorsamples') WHERE name = 'Embedding'")).ShouldBe("TEXT");

            foreach (var label in new[] { "unit", "triple", "frac" }) {
                (await package.ScalarStringAsync($"SELECT typeof(Embedding) FROM dbo__vectorsamples WHERE Label = '{label}'")).ShouldBe("text");
                (await package.ScalarIntAsync($"SELECT json_array_length(Embedding) FROM dbo__vectorsamples WHERE Label = '{label}'")).ShouldBe(3);
            }

            (await package.ScalarIntAsync("SELECT COUNT(*) FROM dbo__vectorsamples WHERE Label = 'null-vector' AND Embedding IS NULL")).ShouldBe(1);
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(4);
        // Euclidean distance is exactly zero only for bit-identical vectors; a single altered mantissa bit
        // makes it positive. Nothing else surfaces a corrupted embedding -- similarity search just gets
        // quietly worse answers.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'unit' AND VECTOR_DISTANCE('euclidean', Embedding, CAST('[1,0,0]' AS VECTOR(3))) = 0")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'triple' AND VECTOR_DISTANCE('euclidean', Embedding, CAST('[2,-3,4]' AS VECTOR(3))) = 0")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'frac' AND VECTOR_DISTANCE('euclidean', Embedding, CAST('[0.5,0.25,-0.125]' AS VECTOR(3))) = 0")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'null-vector' AND Embedding IS NULL")).ShouldBe(1);
    }

    /// <summary>
    /// float16 vectors are transported as their varchar(max) JSON array rather than as a native
    /// <c>SqlVector&lt;float&gt;</c>, so they take a different branch in both the reader's field-type mapping
    /// and the value converter. An implementation that only knows float32 corrupts these and nothing else
    /// notices.
    /// </summary>
    [SkippableFact]
    public async Task RoundTrip_Float16Vector_IsBitExact() {
        Requires.VectorFloat16(_fixture);

        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(TypeVaultFixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, TypeVaultFixture);
        await using var sqlite = new SqliteTempFileHarness();

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, OnlyTable("dbo.VectorSamples"));

        exportResult.TableCount.ShouldBe(1);
        exportResult.RowCount.ShouldBe(4);

        await using (var package = await sqlite.OpenConnectionAsync()) {
            // The two columns must carry different discriminators: both the reader's GetFieldType and the
            // value converter branch on this one value, and they can only agree if it is captured correctly.
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.VectorSamples", "EmbeddingFloat16", typeName: "vector", isNullable: true, vectorBaseType: 1, vectorDimensions: 3);
            await SqlitePackageAssertions.HasColumnMetadataAsync(package, "dbo.VectorSamples", "Embedding", vectorBaseType: 0);
            (await package.ScalarStringAsync("SELECT type FROM pragma_table_info('dbo__vectorsamples') WHERE name = 'EmbeddingFloat16'")).ShouldBe("TEXT");

            foreach (var label in new[] { "unit", "triple" }) {
                (await package.ScalarStringAsync($"SELECT typeof(EmbeddingFloat16) FROM dbo__vectorsamples WHERE Label = '{label}'")).ShouldBe("text");
                (await package.ScalarIntAsync($"SELECT json_array_length(EmbeddingFloat16) FROM dbo__vectorsamples WHERE Label = '{label}'")).ShouldBe(3);
            }

            (await package.ScalarIntAsync("SELECT COUNT(*) FROM dbo__vectorsamples WHERE Label = 'null-vector' AND EmbeddingFloat16 IS NULL")).ShouldBe(1);
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(1);
        importResult.RowCount.ShouldBe(4);
        // VECTOR_DISTANCE refuses mixed base types, so the comparison literal has to be float16 as well.
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'unit' AND VECTOR_DISTANCE('euclidean', EmbeddingFloat16, CAST('[1,0,0]' AS VECTOR(3, float16))) = 0")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'triple' AND VECTOR_DISTANCE('euclidean', EmbeddingFloat16, CAST('[2,-3,4]' AS VECTOR(3, float16))) = 0")).ShouldBe(1);
        (await target.ScalarIntAsync("SELECT COUNT(*) FROM dbo.VectorSamples WHERE Label = N'null-vector' AND EmbeddingFloat16 IS NULL")).ShouldBe(1);
    }

    /// <summary>
    /// type-vault.sql deliberately holds tables that must fail export (LedgerAmounts, the unsupported-type
    /// hazards), so every test here selects the one table it is about. Table selection is applied before
    /// column-type validation, so a hazard table left out of the selection is never validated.
    /// </summary>
    private static ExportOptions OnlyTable(string fullName) {
        return new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [fullName]
        };
    }
}
