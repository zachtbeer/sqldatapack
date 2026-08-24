using BenchmarkDotNet.Running;

namespace SqlDataPack.Benchmarks;

// Run everything:      dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*'
// Run one class:       dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*ValueConvert*'
// Quick smoke check:   dotnet run -c Release --project tests/SqlDataPack.Benchmarks -- --filter '*' --job Dry
internal static class Program {
    private static void Main(string[] args) {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
