using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// Identifier quoting under names that break naive string interpolation. Three quoting schemes run over the
/// same names in one round trip: SQL Server bracket doubling on the read side, SQLite double-quote doubling on
/// the write side, and the temporal manager's single-quote doubling inside its <c>OBJECT_ID(N'...')</c>
/// literals. Every name assertion compares strings ordinally so a stripped or escaped-away character fails
/// rather than quietly matching.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class AdversarialIdentifierTests {
    private const string Fixture = "adversarial-identifiers.sql";

    // Names as SQL Server stores them; every comparison below is ordinal.
    private const string Schema = "Facturación";
    private const string BracketTable = "Envío]Detalle";
    private const string SemicolonTable = "Cliente;Referencia";
    private const string VersionedTable = "Tarifa's Log";
    private const string HistoryTable = "Tarifa's Log_Archive";
    private const string LimitTable = "ShipmentReceiptReconciliationArchiveRecordAtTheSysnameLimit_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

    private const string BracketFullName = Schema + "." + BracketTable;
    private const string SemicolonFullName = Schema + "." + SemicolonTable;
    private const string VersionedFullName = Schema + "." + VersionedTable;
    private const string HistoryFullName = Schema + "." + HistoryTable;
    private const string LimitFullName = "dbo." + LimitTable;

    private const string QuotedColumn = "Recipient \"Name\"";
    private const string ApostropheColumn = "Note's";
    private const string NewlineColumn = "Line\nBreak";
    private const string LimitColumn = "OriginatingLegacySystemReferenceCodeAtTheSysnameLimitColumn_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

    // Expected generated SQLite data table names, spelled out rather than recomputed through the production
    // helper: recomputing them would assert nothing about what the export actually created.
    private const string BracketDataTable = "facturación__envío_detalle";
    private const string SemicolonDataTable = "facturación__cliente_referencia";
    private const string VersionedDataTable = "facturación__tarifa_s_log";
    private const string HistoryDataTable = "facturación__tarifa_s_log_archive";
    private const string LimitDataTable = "dbo__shipmentreceiptreconciliationarchiverecordatthesysnamelimit_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    // Unit separator: joins the parts of a catalog row into one comparable key. A name here can contain a
    // semicolon, a bracket, a quote or a newline, so no printable separator is safe.
    private const char Unit = (char)31;

    private static readonly string[] AllTables = [BracketFullName, SemicolonFullName, VersionedFullName, HistoryFullName, LimitFullName];

    private readonly SqlServerContainerFixture _fixture;

    public AdversarialIdentifierTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task RoundTrip_AdversarialNames_ExportsAndImportsEndToEnd() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, Fixture);
        await using var sqlite = new SqliteTempFileHarness();

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath);

        exportResult.TableCount.ShouldBe(5);
        exportResult.RowCount.ShouldBe(8);

        await using (var connection = await sqlite.OpenConnectionAsync()) {
            var tables = await ReadOrderedAsync(connection, "SELECT source_schema || char(31) || source_table FROM zsdp_tables");
            tables.ShouldBe(Ordered(
                Key(Schema, BracketTable),
                Key(Schema, SemicolonTable),
                Key(Schema, VersionedTable),
                Key(Schema, HistoryTable),
                Key("dbo", LimitTable)));

            // Five distinct generated names, each of which is a real table in the package.
            var dataTables = await ReadOrderedAsync(connection, "SELECT sqlite_table FROM zsdp_tables");
            dataTables.ShouldBe(Ordered(BracketDataTable, SemicolonDataTable, VersionedDataTable, HistoryDataTable, LimitDataTable));
            foreach (var dataTable in dataTables) {
                (await connection.TableExistsAsync(dataTable)).ShouldBeTrue($"Expected SQLite data table '{dataTable}' to exist.");
            }

            var columns = await ReadOrderedAsync(connection, """
                                                             SELECT t.source_schema || char(31) || t.source_table || char(31) || c.column_name
                                                             FROM zsdp_columns c
                                                             INNER JOIN zsdp_tables t ON t.id = c.table_id
                                                             """);
            columns.ShouldBe(Ordered(
                Key(Schema, BracketTable, "EnvioId"),
                Key(Schema, BracketTable, QuotedColumn),
                Key(Schema, BracketTable, ApostropheColumn),
                Key(Schema, BracketTable, NewlineColumn),
                Key(Schema, SemicolonTable, "ClienteReferenciaId"),
                Key(Schema, SemicolonTable, "Codigo"),
                Key(Schema, VersionedTable, "TarifaId"),
                Key(Schema, VersionedTable, "TarifaName"),
                Key(Schema, VersionedTable, "ValidFrom"),
                Key(Schema, VersionedTable, "ValidTo"),
                Key(Schema, HistoryTable, "TarifaId"),
                Key(Schema, HistoryTable, "TarifaName"),
                Key(Schema, HistoryTable, "ValidFrom"),
                Key(Schema, HistoryTable, "ValidTo"),
                Key("dbo", LimitTable, "Id"),
                Key("dbo", LimitTable, LimitColumn)));

            // The data table's own columns, not just the metadata copy: this is where SQLite double-quote
            // quoting was applied at CREATE TABLE time.
            foreach (var column in new[] { "EnvioId", QuotedColumn, ApostropheColumn, NewlineColumn }) {
                (await connection.TableColumnExistsAsync(BracketDataTable, column)).ShouldBeTrue($"Expected column '{column}' in SQLite table '{BracketDataTable}'.");
            }

            (await connection.ScalarStringAsync($"SELECT \"Line\nBreak\" FROM \"{BracketDataTable}\" WHERE \"Recipient \"\"Name\"\"\" LIKE 'Alex%'")).ShouldBe("first\nsecond");
        }

        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(5);
        importResult.RowCount.ShouldBe(8);

        foreach (var table in AllTables) {
            await CrossDatabaseCompare.AssertTablesIdenticalAsync(source, target, table);
        }
    }

    [Fact]
    public async Task RoundTrip_AdversarialNames_TemporalPairSurvivesSuspendAndRestore() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, Fixture);
        await using var sqlite = new SqliteTempFileHarness();

        await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [VersionedFullName, HistoryFullName]
        });
        var importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);

        importResult.TableCount.ShouldBe(2);
        importResult.RowCount.ShouldBe(3);
        // Import warnings carry the export's own advisory too, and its wording also contains the table name
        // and "system versioning is temporarily suspended" -- so match the import-side warning's exact shape
        // (TemporalTableManager.DescribeSuspend), which is the only one naming the history table in parentheses.
        importResult.Warnings.ShouldContain(w =>
            w.StartsWith($"Temporal table '{VersionedFullName}': system versioning is temporarily suspended", StringComparison.Ordinal)
            && w.Contains($"('{HistoryFullName}')", StringComparison.Ordinal));

        // The suspend/restore ceremony builds OBJECT_ID(N'[Facturación].[Tarifa''s Log]') literals: bracket
        // doubling for the identifier, single-quote doubling for the literal wrapping it.
        (await TemporalAssertions.IsSystemVersionedAsync(target, VersionedFullName)).ShouldBeTrue();
        (await TemporalAssertions.ReadHistoryTableNameAsync(target, VersionedFullName)).ShouldBe(HistoryFullName);

        var targetDump = await TemporalAssertions.DumpSystemVersionedAsync(target, VersionedFullName, "ValidFrom", "ValidTo");
        targetDump.ShouldBe(await TemporalAssertions.DumpSystemVersionedAsync(source, VersionedFullName, "ValidFrom", "ValidTo"));
    }

    [Fact]
    public async Task Export_AdversarialColumnName_UsedAsFilterAndExclusion() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var sqlite = new SqliteTempFileHarness();

        // Same column in both roles: dropped from the package, and the predicate that decides which rows are
        // exported at all. The predicate is where the table name reaches raw interpolation.
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = [BracketFullName],
            ExcludeColumns = [$"{BracketFullName}.{QuotedColumn}"],
            PerTableWhereClauses = [new PerTableWhereClause(BracketFullName, "[Recipient \"Name\"] LIKE N'Alex%'")]
        };

        var exportResult = await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);

        exportResult.TableCount.ShouldBe(1);
        exportResult.RowCount.ShouldBe(1);

        await using var connection = await sqlite.OpenConnectionAsync();
        await SqlitePackageAssertions.HasColumnMetadataAsync(connection, BracketFullName, QuotedColumn, isExcluded: true);
        await SqlitePackageAssertions.HasExclusionAsync(connection, "column", $"{BracketFullName}.{QuotedColumn}");
        await SqlitePackageAssertions.HasTableRowCountAsync(connection, BracketFullName, 1);

        (await connection.TableColumnExistsAsync(BracketDataTable, QuotedColumn)).ShouldBeFalse($"Excluded column '{QuotedColumn}' must not exist in SQLite table '{BracketDataTable}'.");
        (await connection.TableColumnExistsAsync(BracketDataTable, ApostropheColumn)).ShouldBeTrue();
        (await connection.TableColumnExistsAsync(BracketDataTable, NewlineColumn)).ShouldBeTrue();

        (await connection.ScalarIntAsync($"SELECT COUNT(*) FROM \"{BracketDataTable}\"")).ShouldBe(1);
        (await connection.ScalarStringAsync($"SELECT \"Note's\" FROM \"{BracketDataTable}\"")).ShouldBe("ships Tuesday");
    }

    private static async Task<IReadOnlyList<string>> ReadOrderedAsync(SqliteConnection connection, string sql) {
        return (await connection.ReadStringsAsync(sql)).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Key(params string[] parts) {
        return string.Join(Unit, parts);
    }

    private static string[] Ordered(params string[] values) {
        return values.Order(StringComparer.Ordinal).ToArray();
    }
}
