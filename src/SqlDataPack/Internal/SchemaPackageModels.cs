using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal sealed record SchemaPackage(string PackageType, string PackageName, string PackageSha256, DateTimeOffset CreatedAtUtc, string? SourceDatabaseName, string? DacFxVersion, DacpacSchemaScope SchemaScope, byte[] Payload, int? SourceEngineEdition = null);
