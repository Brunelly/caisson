using System.Diagnostics.CodeAnalysis;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Orchestration.DriftApply;
using Caisson.Orchestration.RackDefinitions;

namespace Caisson.Orchestration.Tests.Fakes;

/// <summary>Scriptable <see cref="IDriftApplyJobStore"/> — mirrors <see cref="FakeDiscoveryJobStore"/>'s shape.</summary>
public sealed class FakeDriftApplyJobStore : IDriftApplyJobStore
{
    public int SaveCount { get; private set; }

    public Func<Guid, string, DriftItem?> ItemBehavior { get; set; } = (_, _) => null;

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<DriftItem?> FindCurrentAccessVlanItemAsync(
        Guid rackId, DriftSubjectType subjectType, string subjectKey, CancellationToken cancellationToken)
        => Task.FromResult(ItemBehavior(rackId, subjectKey));
}

/// <summary>Scriptable, never-throws-by-contract <see cref="IDriftComputationService"/> fake.</summary>
public sealed class FakeDriftComputationService : IDriftComputationService
{
    public List<Guid> ComputedRackIds { get; } = new();

    public Task ComputeAndPersistAsync(Guid rackId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        ComputedRackIds.Add(rackId);
        return Task.CompletedTask;
    }
}

/// <summary>Records every rack id enqueued for recompute (never throws, per the interface's hard contract).</summary>
public sealed class RecordingDriftRecomputeSignal : IDriftRecomputeSignal
{
    public List<Guid> EnqueuedRackIds { get; } = new();

    public void Enqueue(Guid rackId) => EnqueuedRackIds.Add(rackId);
}

/// <summary>Fixed single-rack-definition provider.</summary>
public sealed class FakeRackDefinitionProvider : IRackDefinitionProvider
{
    public RackDefinition? Definition { get; set; }

    public Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken)
        => Definition is null
            ? throw new RackDefinitionMissingException(rackId)
            : Task.FromResult(Definition);
}

/// <summary>A single-entry <see cref="ISwitchMutatingDriverRegistry"/> resolving to a scripted factory.</summary>
public sealed class FakeSwitchMutatingDriverRegistry : ISwitchMutatingDriverRegistry
{
    public ISwitchMutatingDriverFactory? Factory { get; set; }

    public IReadOnlyList<DriverDescriptor> RegisteredDrivers => Factory is null
        ? Array.Empty<DriverDescriptor>()
        : new[] { Factory.Descriptor };

    public bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out ISwitchMutatingDriverFactory? factory)
    {
        factory = Factory;
        return Factory is not null;
    }
}

/// <summary>Delegate-driven mutating-driver factory that always returns the same fake driver instance.</summary>
public sealed class FakeSwitchMutatingDriverFactory : ISwitchMutatingDriverFactory
{
    private readonly FakeSwitchMutatingDriver _driver;

    public FakeSwitchMutatingDriverFactory(FakeSwitchMutatingDriver driver) => _driver = driver;

    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public ISwitchMutatingDriver Create(SwitchMutatingConnectionOptions options) => _driver;
}

/// <summary>
/// Records every <see cref="SetAccessVlanAsync"/> call so tests can assert the driver was (or, for the
/// stale-drift/crash-resume paths, was NOT) invoked — the core assertion behind AC3/AC4/NFR2.
/// </summary>
public sealed class FakeSwitchMutatingDriver : ISwitchMutatingDriver
{
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public int CallCount { get; private set; }

    public Func<SetAccessVlanRequest, DriverResult<SetAccessVlanOutcome>> Behavior { get; set; } =
        _ => throw new InvalidOperationException("FakeSwitchMutatingDriver.Behavior was not configured.");

    public Task<DriverResult<SetAccessVlanOutcome>> SetAccessVlanAsync(SetAccessVlanRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Behavior(request));
    }

    public static SetAccessVlanOutcome Outcome(SwitchChangeReasonCode reasonCode, bool confirmed = true)
    {
        var before = new SwitchAccessVlanState("ether1", 10, Array.Empty<int>());
        var after = new SwitchAccessVlanState("ether1", 20, Array.Empty<int>());
        var verification = new VerificationResult(confirmed, 20, confirmed ? 20 : 10, null);
        var audit = new SwitchChangeAuditRecord(
            Guid.NewGuid(), "10.0.0.1", "ether1", 20, DryRun: false, ConfirmWindowSeconds: 30,
            before, after, reasonCode, verification, DateTimeOffset.UtcNow,
            Caisson.Domain.Enums.ActorType.User, "operator@example.com");

        return new SetAccessVlanOutcome(
            "10.0.0.1", "ether1", 20, Guid.NewGuid(), DryRun: false,
            new SwitchChangePlan(Array.Empty<SwitchChangeStep>()),
            before, after, verification, confirmed, reasonCode, audit);
    }
}
