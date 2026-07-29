using Xunit;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Shares one hosted API + simulator set across the <see cref="VirtualRackApiFactory"/>-backed test
/// classes (mirrors <c>Caisson.Api.IntegrationTests.ApiCollection</c>) — <see cref="VirtualRackApiFactory"/>
/// mutates PROCESS-WIDE environment variables (<c>CAISSON_SWITCH_*</c>/<c>CAISSON_BMC_*</c>) during
/// <c>InitializeAsync</c>, so two instances running concurrently (xUnit's default: different test classes
/// without a shared collection run in parallel) would race and corrupt each other's simulator
/// credentials/TLS pinning. A shared collection both serializes the classes and avoids paying simulator
/// startup cost twice.
/// </summary>
[CollectionDefinition(Name)]
public sealed class VirtualRackCollection : ICollectionFixture<VirtualRackApiFactory>
{
    public const string Name = "virtual-rack";
}
