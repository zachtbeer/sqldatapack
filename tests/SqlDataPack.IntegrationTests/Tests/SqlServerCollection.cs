using SqlDataPack.IntegrationTests.Harness;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

[CollectionDefinition(nameof(SqlServerCollection))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture> {
}
