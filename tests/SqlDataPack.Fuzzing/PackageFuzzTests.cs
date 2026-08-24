using FsCheck;
using FsCheck.Fluent;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Shouldly;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Fuzzing;

/// <summary>
/// Fuzzes the untrusted package file: the headline attack surface, since packages are emailed,
/// attached to tickets, and moved between machines. Whatever is in the file, the caller gets a
/// <see cref="SqlDataPackException"/> and gets it promptly; never a framework exception, never a hang.
/// </summary>
public sealed class PackageFuzzTests {
    // Syntactically valid, nothing listening. A corrupt package must be rejected before the importer
    // ever reaches this, so a SqlException here means the package was accepted as valid.
    private const string UnreachableTarget = "Server=127.0.0.1,1;Database=SqlDataPackFuzzTarget;User ID=sa;Password=NotUsed1!;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True";

    // The fixture package holds exactly one table and one column; a good error message names one of them.
    private const string FixtureTable = "Customers";

    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    private static readonly Lazy<byte[]> RealPackageBytes = new(BuildPackageBytes);

    [FuzzProperty]
    public Property ReadManifest_ArbitraryFile_FailsCleanlyWithoutHanging() =>
        Prop.ForAll(ArbitraryFiles.ToArbitrary(), bytes => {
            var path = NewTempPackagePath();
            try {
                File.WriteAllBytes(path, bytes);

                var read = Task.Run(() => new SqlDataPackReader().ReadManifestAsync(path));
                var finished = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
                ReferenceEquals(finished, read).ShouldBeTrue($"ReadManifestAsync did not return within 10s for a {bytes.Length}-byte file.");

                var thrown = Record.Exception(() => {
                    read.GetAwaiter().GetResult();
                });
                if (thrown is not null) {
                    ShouldNotBeAFrameworkException(thrown, $"{bytes.Length}-byte arbitrary file");
                    thrown.ShouldBeOfType<SqlDataPackException>();
                }

                // Not cleanup: a leaked sqlite3 file handle makes this throw on Windows, and then the
                // caller cannot delete or replace the package they just tried to read.
                File.Delete(path);
            }
            finally {
                TryDelete(path);
            }

            return true;
        });

    /// <summary>
    /// A metadata cell the package itself can prove wrong: an integer outside the range of the type
    /// production reads it into, or a table identity that no longer matches the rest of the package.
    /// Reached through the real import call sequence, which is where the gap is: <c>ReadTablesAsync</c>
    /// runs outside the corrupt-package wrapper that <c>ValidateForImportAsync</c> and
    /// <c>ReadManifestAsync</c> sit behind.
    /// </summary>
    [FuzzProperty]
    public Property ImportAsync_TamperedMetadataCell_FailsAsSqlDataPackException() =>
        Prop.ForAll(UnmeaningfulCells.ToArbitrary(), corruption => {
            var thrown = ImportPackage(corruption);

            thrown.ShouldNotBeNull($"{corruption} was accepted as a valid package.");
            ShouldNotBeAFrameworkException(thrown!, corruption.ToString());
            thrown!.ShouldNotBeOfType<SqlException>($"{corruption} reached the target server instead of being rejected while reading the package.");
            thrown.ShouldBeOfType<SqlDataPackException>();
            thrown.Message.Contains(FixtureTable, StringComparison.OrdinalIgnoreCase).ShouldBeTrue($"{corruption} was rejected with '{thrown.Message}', which names neither the table nor the column at fault.");

            return true;
        });

    /// <summary>
    /// The text cells the package cannot validate on its own. Any string is a plausible column name,
    /// type name, or collation, and only the target database can say otherwise. Weaker property than
    /// the one above: reaching the target is a legitimate outcome here, a framework exception is not.
    /// </summary>
    [FuzzProperty]
    public Property ImportAsync_TamperedColumnTextCell_NeverSurfacesFrameworkException() =>
        Prop.ForAll(UnverifiableTextCells.ToArbitrary(), corruption => {
            var thrown = ImportPackage(corruption);

            thrown.ShouldNotBeNull($"{corruption} imported into a target that does not exist.");
            ShouldNotBeAFrameworkException(thrown!, corruption.ToString());
            (thrown is SqlDataPackException or SqlException).ShouldBeTrue($"{corruption} surfaced {thrown!.GetType().Name}: {thrown.Message}");

            return true;
        });

    /// <summary>
    /// Guards the property above from going vacuous. The tampered-cell properties only mean anything if
    /// an untampered package clears package validation, so this pins that it does. The fuzz package is
    /// rejected on nothing but the corruption under test.
    /// </summary>
    [Fact]
    public void ImportAsync_UntamperedPackage_ClearsPackageValidationBeforeContactingTarget() {
        var thrown = ImportPackage(null);

        thrown.ShouldBeOfType<SqlException>($"An untampered fuzz package was rejected before the target was contacted: {thrown?.Message}");
    }

    /// <summary>Writes a fuzz package, optionally corrupting one cell, and imports it. Null means untampered.</summary>
    private static Exception? ImportPackage(Corruption? corruption) {
        var path = NewTempPackagePath();
        try {
            WritePackage(path, corruption);
            return Record.Exception(() => {
                new SqlDataPackImporter().ImportAsync(path, UnreachableTarget).GetAwaiter().GetResult();
            });
        }
        finally {
            TryDelete(path);
        }
    }

    private static void WritePackage(string path, Corruption? corruption) {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();
        FuzzPackage.CreateValidMinimalPackageAsync(connection).GetAwaiter().GetResult();

        if (corruption is not null) {
            using var command = connection.CreateCommand();
            command.CommandText = corruption.Sql;
            command.Parameters.AddWithValue("$v", corruption.Value);
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearPool(connection);
    }

    private static byte[] BuildPackageBytes() {
        var path = NewTempPackagePath();
        try {
            WritePackage(path, null);
            return File.ReadAllBytes(path);
        }
        finally {
            TryDelete(path);
        }
    }

    private static void ShouldNotBeAFrameworkException(Exception thrown, string subject) {
        // Spelled out rather than left to ShouldBeOfType: these are the conversions the package read path
        // performs on untrusted cells, so naming them makes the failure say which one was unguarded.
        thrown.ShouldNotBeOfType<OverflowException>($"{subject} surfaced a raw OverflowException from an integer conversion in the package read path.");
        thrown.ShouldNotBeOfType<InvalidCastException>($"{subject} surfaced a raw InvalidCastException.");
        thrown.ShouldNotBeOfType<FormatException>($"{subject} surfaced a raw FormatException.");
        thrown.ShouldNotBeOfType<SqliteException>($"{subject} surfaced a raw SqliteException.");
    }

    private static string NewTempPackagePath() => Path.Combine(Path.GetTempPath(), $"sqldatapack-fuzz-{Guid.NewGuid():N}.sqlite");

    private static void TryDelete(string path) {
        // A generated file whose header claims WAL leaves -wal and -shm behind when SQLite opens it.
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) {
            try {
                File.Delete(file);
            }
            catch {
                /* best-effort temp cleanup */
            }
        }
    }

    private static readonly Gen<byte[]> ArbitraryFiles = Gen.OneOf(new[] {
        ArbMap.Default.GeneratorFor<byte[]>().Select(bytes => bytes ?? []),
        // Passes the header sniff, so SQLite gets far enough to parse a corrupt b-tree.
        ArbMap.Default.GeneratorFor<byte[]>().Select(bytes => SqliteHeader.Concat(bytes ?? []).ToArray()),
        // The realistic accident: a package truncated in transit.
        Gen.Choose(0, RealPackageBytes.Value.Length).Select(length => RealPackageBytes.Value.Take(length).ToArray()),
    });

    private sealed record Corruption(string Table, string Column, object Value) {
        public string Sql => $"UPDATE {Table} SET {Column} = $v";

        public override string ToString() => $"{Table}.{Column} = {(Value is string text ? $"'{text}'" : Value)}";
    }

    private static readonly Gen<long> BeyondInt32 = Gen.OneOf(new[] {
        Gen.Choose(1, 1000).Select(n => int.MaxValue + (long)n),
        Gen.Choose(1, 1000).Select(n => int.MinValue - (long)n),
        Gen.Constant(long.MaxValue),
        Gen.Constant(long.MinValue),
    });

    private static readonly Gen<long> BeyondInt16 = Gen.OneOf(new[] {
        Gen.Choose(short.MaxValue + 1, int.MaxValue).Select(n => (long)n),
        Gen.Choose(int.MinValue, short.MinValue - 1).Select(n => (long)n),
        BeyondInt32,
    });

    private static readonly Gen<long> BeyondByte = Gen.OneOf(new[] {
        Gen.Choose(byte.MaxValue + 1, int.MaxValue).Select(n => (long)n),
        Gen.Choose(int.MinValue, -1).Select(n => (long)n),
        BeyondInt32,
    });

    // Arbitrary text that cannot accidentally re-form the identity it is replacing.
    private static readonly Gen<string> ForeignText = Fuzz.Garbage.Where(text =>
        !string.Equals(text, "dbo", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(text, FixtureTable, StringComparison.OrdinalIgnoreCase)
        && !text.Contains("dbo__customers", StringComparison.OrdinalIgnoreCase));

    private static readonly Gen<Corruption> UnmeaningfulCells = Gen.OneOf(new[] {
        BeyondInt16.Select(value => new Corruption("zsdp_columns", "max_length", value)),
        BeyondByte.Select(value => new Corruption("zsdp_columns", "precision_value", value)),
        BeyondByte.Select(value => new Corruption("zsdp_columns", "scale_value", value)),
        BeyondInt32.Select(value => new Corruption("zsdp_columns", "ordinal", value)),
        BeyondInt32.Select(value => new Corruption("zsdp_columns", "is_nullable", value)),
        BeyondInt32.Select(value => new Corruption("zsdp_columns", "vector_base_type", value)),
        BeyondInt32.Select(value => new Corruption("zsdp_columns", "vector_dimensions", value)),
        BeyondInt32.Select(value => new Corruption("zsdp_table_stats", "export_batch_size", value)),
        ForeignText.Select(value => new Corruption("zsdp_tables", "source_schema", value)),
        ForeignText.Select(value => new Corruption("zsdp_tables", "source_table", value)),
        ForeignText.Select(value => new Corruption("zsdp_tables", "sqlite_table", value)),
    });

    private static readonly Gen<Corruption> UnverifiableTextCells = Gen.OneOf(new[] {
        Fuzz.Garbage.Select(value => new Corruption("zsdp_columns", "column_name", value)),
        Fuzz.Garbage.Select(value => new Corruption("zsdp_columns", "sql_server_type_name", value)),
        Fuzz.Garbage.Select(value => new Corruption("zsdp_columns", "collation_name", value)),
    });
}
