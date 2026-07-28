using Caisson.Correlation;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.RackDefinitions;
using Caisson.Orchestration.Scheduling;

namespace Caisson.Orchestration.Tests.Fakes;

/// <summary>Records the input and returns an empty (or configured) correlation result.</summary>
public sealed class FakeCorrelationEngine : ITopologyCorrelationEngine
{
    public TopologyCorrelationInput? LastInput { get; private set; }

    public TopologyCorrelationResult Result { get; set; } = new(
        Array.Empty<NicPortMapping>(),
        Array.Empty<AmbiguousNicMapping>(),
        Array.Empty<UnmappedNic>(),
        Array.Empty<UnmappedPort>());

    public TopologyCorrelationResult Correlate(TopologyCorrelationInput input)
    {
        LastInput = input;
        return Result;
    }
}

/// <summary>A <see cref="TimeProvider"/> whose "now" is fixed/advanceable for deterministic tests.</summary>
public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A no-op <see cref="IDiscoveryJobStore"/> that counts saves and scripts the cancel flag.</summary>
public sealed class FakeDiscoveryJobStore : IDiscoveryJobStore
{
    public int SaveCount { get; private set; }

    public bool CancellationRequested { get; set; }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken)
        => Task.FromResult(CancellationRequested);
}

/// <summary>Captures ingestion requests and returns a scripted outcome (or throws).</summary>
public sealed class FakeTopologyIngestionService : ITopologySnapshotIngestionService
{
    private readonly Func<TopologyIngestionRequest, SnapshotIngestionOutcome> _behavior;

    public FakeTopologyIngestionService(Func<TopologyIngestionRequest, SnapshotIngestionOutcome>? behavior = null)
        => _behavior = behavior ?? (_ => new SnapshotIngestionOutcome(Guid.NewGuid(), 1, 0));

    public int CallCount { get; private set; }

    public TopologyIngestionRequest? LastRequest { get; private set; }

    public Task<SnapshotIngestionOutcome> IngestAsync(
        TopologyIngestionRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return Task.FromResult(_behavior(request));
    }
}

/// <summary>Returns a configured <see cref="RackDefinition"/> or throws the fail-closed exception.</summary>
public sealed class InMemoryRackDefinitionProvider : IRackDefinitionProvider
{
    private readonly RackDefinition? _definition;

    public InMemoryRackDefinitionProvider(RackDefinition? definition) => _definition = definition;

    public Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken)
        => _definition is null
            ? throw new RackDefinitionMissingException(rackId)
            : Task.FromResult(_definition);
}

/// <summary>A fully controllable <see cref="IDeviceDiscoveryService"/> for orchestrator tests.</summary>
public sealed class FakeDeviceDiscoveryService : IDeviceDiscoveryService
{
    public Func<int, SwitchDiscoveryOutcome> SwitchBehavior { get; set; } =
        _ => new SwitchDiscoveryOutcome(Array.Empty<SwitchTopologySnapshot>(), 0, 0);

    public Func<int, ServerDiscoveryOutcome> ServerBehavior { get; set; } =
        _ => new ServerDiscoveryOutcome(Array.Empty<ServerNicSnapshot>(), 0, 0);

    /// <summary>Invoked each time a switch discovery runs (e.g. to simulate a concurrent cancel).</summary>
    public Action<int>? OnSwitchCall { get; set; }

    public int SwitchCallCount { get; private set; }

    public int ServerCallCount { get; private set; }

    public Task<SwitchDiscoveryOutcome> DiscoverSwitchesAsync(
        RackDefinition definition, DeviceDiscoveryContext context, CancellationToken cancellationToken)
    {
        SwitchCallCount++;
        OnSwitchCall?.Invoke(SwitchCallCount);
        return Task.FromResult(SwitchBehavior(SwitchCallCount));
    }

    public Task<ServerDiscoveryOutcome> DiscoverServersAsync(
        RackDefinition definition, DeviceDiscoveryContext context, CancellationToken cancellationToken)
    {
        ServerCallCount++;
        return Task.FromResult(ServerBehavior(ServerCallCount));
    }
}

/// <summary>A deterministic <see cref="IJitterSource"/> returning a fixed value.</summary>
public sealed class FixedJitterSource : IJitterSource
{
    private readonly int _value;

    public FixedJitterSource(int value) => _value = value;

    public int NextJitterSeconds(int maxJitterSeconds) => Math.Min(_value, maxJitterSeconds);
}
