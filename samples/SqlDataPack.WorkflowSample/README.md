# SqlDataPack.WorkflowSample

This sample shows a production-style export/import workflow for a portable SQL Server data snapshot.

Use it when you want to see the pieces around the basic copy operation:

- export preflight
- progress reporting
- export to a local package file
- manifest inspection
- import preflight
- import into SQL Server
- warning and error output

Set connection strings with environment variables:

```bash
export ZSB_SOURCE_SQLSERVER="Server=localhost;Database=SourceDb;User Id=sa;Password=...;TrustServerCertificate=True"
export ZSB_TARGET_SQLSERVER="Server=localhost;Database=TargetDb;User Id=sa;Password=...;TrustServerCertificate=True"
export ZSB_PACKAGE_PATH="workflow-sample.sqlite"
dotnet run --project samples/SqlDataPack.WorkflowSample/SqlDataPack.WorkflowSample.csproj
```

The target SQL Server tables must already exist and be empty before import unless you add dacpac capture/deployment options.
