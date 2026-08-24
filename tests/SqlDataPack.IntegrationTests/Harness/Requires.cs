using Xunit;

namespace SqlDataPack.IntegrationTests.Harness;

/// <summary>
/// Version gates for tests that need engine features the container image may not have. These skip the test
/// with a reason naming the image env var; a bare <c>return</c> reports green having asserted nothing, which
/// is how a whole suite can pass on an image that cannot run half of it.
/// <para>
/// A test calling any of these must be a <c>[SkippableFact]</c> / <c>[SkippableTheory]</c> -- on a plain
/// <c>[Fact]</c> the skip surfaces as a failure.
/// </para>
/// </summary>
internal static class Requires {
    public static void SqlServer2025(SqlServerContainerFixture fixture) {
        Skip.IfNot(fixture.IsSqlServer2025OrLater, Reason(fixture, "SQL Server 2025 (major version 17) or later"));
    }

    public static void Vector(SqlServerContainerFixture fixture) {
        Skip.IfNot(fixture.SupportsVector, Reason(fixture, "the VECTOR type (SQL Server 2025 or later)"));
    }

    /// <summary>
    /// float16 vectors also need the database to have been created with preview features on --
    /// <c>SqlServerFixtureDatabase.CreateAsync(fixture, previewFeatures: true)</c>.
    /// </summary>
    public static void VectorFloat16(SqlServerContainerFixture fixture) {
        Skip.IfNot(fixture.SupportsVectorFloat16, Reason(fixture, "float16 vectors (SQL Server 2025 or later, PREVIEW_FEATURES = ON)"));
    }

    private static string Reason(SqlServerContainerFixture fixture, string requirement) {
        return $"Requires {requirement}. Running against image '{fixture.ImageName}' (major version {fixture.ServerMajorVersion?.ToString() ?? "unknown"}); set {SqlServerContainerFixture.SqlServerImageEnvironmentVariable} to an image that has it.";
    }
}
