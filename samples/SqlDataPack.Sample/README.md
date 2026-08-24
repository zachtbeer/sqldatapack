# SqlDataPack.Sample

Minimal console sample for copying selected SQL Server data into a local package, then importing that package into a prepared target SQL Server schema.

Use this sample when you want the smallest end-to-end example of a SQL Server data roundtrip.

Set connection strings with environment variables:

```bash
export ZSB_SOURCE_SQLSERVER="Server=localhost;Database=SourceDb;User Id=sa;Password=...;TrustServerCertificate=True"
export ZSB_TARGET_SQLSERVER="Server=localhost;Database=TargetDb;User Id=sa;Password=...;TrustServerCertificate=True"
export ZSB_PACKAGE_PATH="package.sqlite"
dotnet run --project samples/SqlDataPack.Sample/SqlDataPack.Sample.csproj
```

The target schema must already exist, and target tables in the package scope must be empty before import.
