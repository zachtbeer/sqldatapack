# API Reference

SqlDataPack generates API reference metadata from XML comments with DocFX.

Start with these public entry points:

- `SqlDataPackExporter` exports SQL Server table data into a local data package.
- `SqlDataPackImporter` validates and imports a data package into SQL Server.
- `SqlData` provides static `ExportAsync` and `ImportAsync` helpers for simple calls.

Support types live in `SqlDataPack.Models`:

- `SqlDataPackReader` reads package manifests without importing rows.
- `ExportOptions` and `ImportOptions` configure table selection, export filters, batching, progress, timeouts, data table naming, and optional dacpac behavior.

`SqlDataPack.Models.ExportOptions.DataTablePrefix` controls the prefix used for exported SQLite data tables. It defaults to `null` (no prefix), so `dbo.Customers` becomes `dbo__customers`; set a value to group data tables behind a prefix. Metadata tables are always `zsdp_*`, and both `zsdp_` and `sqlite_` are reserved for generated data table names.

`SqlDataPack.Models.ExportOptions.Tables` supports exact source table names and `*` wildcards, for example `dbo.Customers`, `Customers`, `dbo.zz*`, and `*.zz*`. `TableSelection` controls whether those patterns are included (`Only`) or excluded (`AllExcept`, the default).

To regenerate the API metadata locally:

```bash
dotnet tool restore
dotnet tool run docfx metadata docfx.json
```

The generated metadata is written to [docs/api](api/). Other public models include:

- `ExportTableSelectionMode`
- `GlobalWhereClause`
- `PerTableWhereClause`
- `SqlDataPackResult`
- `SqlDataPackPreflightResult`
- `SqlDataPackManifest`
- `SqlDataPackProgress`
