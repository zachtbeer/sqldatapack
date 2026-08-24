using SqlDataPack;
using SqlDataPack.Models;

var sourceConnectionString = RequireEnvironmentVariable("ZSB_SOURCE_SQLSERVER");
var targetConnectionString = RequireEnvironmentVariable("ZSB_TARGET_SQLSERVER");
var packagePath = Environment.GetEnvironmentVariable("ZSB_PACKAGE_PATH") ?? "sample-package.sqlite";

var exportOptions = new ExportOptions {
    TableSelection = ExportTableSelectionMode.Only,
    Tables = ["dbo.*"],
    // Add entries like "dbo.SomeTable.LegacyPayload" when specific columns should not be exported.
    ExcludeColumns = [],
    OverwriteExistingPackage = false,
    BatchSize = 1_000
};

try {
    var exportResult = await new SqlDataPackExporter().ExportAsync(sourceConnectionString, packagePath, exportOptions);

    Console.WriteLine($"Exported {exportResult.RowCount} rows from {exportResult.TableCount} tables to {packagePath}.");

    var importResult = await new SqlDataPackImporter().ImportAsync(packagePath, targetConnectionString, new ImportOptions { BatchSize = 1_000 });

    Console.WriteLine($"Imported {importResult.RowCount} rows into {importResult.TableCount} target tables.");
}
catch (SqlDataPackException exception) {
    Console.Error.WriteLine("SqlDataPack failed:");
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}
catch (InvalidOperationException exception) {
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

static string RequireEnvironmentVariable(string name) {
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) {
        throw new InvalidOperationException($"Set the {name} environment variable before running the sample.");
    }

    return value;
}
