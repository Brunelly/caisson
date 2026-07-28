using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>Shares one hosted API + seeded database across the DB-backed API test classes.</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CaissonApiFactory>
{
    public const string Name = "api";
}
