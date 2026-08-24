using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SqlDataPack.Tests")]
[assembly: InternalsVisibleTo("SqlDataPack.IntegrationTests")]
[assembly: InternalsVisibleTo("SqlDataPack.Fuzzing")]
[assembly: InternalsVisibleTo("SqlDataPack.DeployRepro")]
// Unprefixed: BenchmarkDotNet requires the benchmark assembly name to match its .csproj file name.
[assembly: InternalsVisibleTo("SqlDataPack.Benchmarks")]
