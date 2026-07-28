using System.Text.Json;
using Caisson.Correlation.Input;
using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Caisson.Orchestration.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>DB-free unit tests for the four-step discovery pipeline (story #8, AC1/AC5, NFR1).</summary>
public sealed class DiscoveryOrchestratorTests
{
    private static readonly Guid RackId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    private const string SecretHost = "10.9.9.9";
    private const string SecretCredRef = "kv://switch/secret-ref";

    private readonly FakeDeviceDiscoveryService _devices = new();
    private readonly FakeCorrelationEngine _engine = new();
    private FakeTopologyIngestionService _ingestion = new();
    private readonly FakeDiscoveryJobStore _store = new();
    private RackDefinition? _definition = Definition();

    [Fact]
    public async Task Happy_path_runs_four_steps_and_ingests_with_job_context()
    {
        _devices.SwitchBehavior = _ => new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 1, 0);
        _devices.ServerBehavior = _ => new ServerDiscoveryOutcome(new[] { Server("srv1") }, 1, 0);
        var job = NewJob(TriggerType.OnDemand, "operator-1", ActorType.User);

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Succeeded);
        Step(job, DiscoveryStepName.SwitchDiscovery).Status.Should().Be(DiscoveryStepStatus.Succeeded);
        Step(job, DiscoveryStepName.BmcDiscovery).Status.Should().Be(DiscoveryStepStatus.Succeeded);
        Step(job, DiscoveryStepName.Correlation).Status.Should().Be(DiscoveryStepStatus.Succeeded);
        Step(job, DiscoveryStepName.Persistence).Status.Should().Be(DiscoveryStepStatus.Succeeded);

        _ingestion.CallCount.Should().Be(1);
        var request = _ingestion.LastRequest!;
        request.RackId.Should().Be(RackId);
        request.TriggerType.Should().Be(TriggerType.OnDemand);
        request.TriggeredBy.Should().Be("operator-1");
        request.ActorType.Should().Be(ActorType.User);
        request.CorrelationId.Should().Be(job.CorrelationId);
        request.Status.Should().Be(SnapshotStatus.Completed);
        request.Observed.Switches.Should().ContainSingle(s => s.SwitchId == "sw1");
        request.Observed.Servers.Should().ContainSingle(s => s.ServerId == "srv1");
        job.ResultSnapshotId.Should().NotBeNull();
        _engine.LastInput.Should().NotBeNull();
    }

    [Fact]
    public async Task Partial_device_failure_persists_partial_success()
    {
        _devices.SwitchBehavior = _ => new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 2, 1);
        _devices.ServerBehavior = _ => new ServerDiscoveryOutcome(new[] { Server("srv1") }, 1, 0);
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Succeeded);
        _ingestion.LastRequest!.Status.Should().Be(SnapshotStatus.PartialSuccess);
    }

    [Fact]
    public async Task Persistence_is_skipped_when_snapshot_already_recorded()
    {
        _devices.SwitchBehavior = _ => new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 1, 0);
        _devices.ServerBehavior = _ => new ServerDiscoveryOutcome(new[] { Server("srv1") }, 1, 0);
        var job = NewJob();
        job.SetResultSnapshot(Guid.NewGuid()); // a prior run already persisted

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Succeeded);
        _ingestion.CallCount.Should().Be(0); // idempotent: no second ingest
        Step(job, DiscoveryStepName.Persistence).Status.Should().Be(DiscoveryStepStatus.Skipped);
    }

    [Fact]
    public async Task Retryable_failure_then_success_records_attempts_and_succeeds()
    {
        _devices.SwitchBehavior = attempt => attempt < 3
            ? throw new DiscoveryStepException(DiscoveryErrorCodes.SwitchDiscoveryFailed, "unreachable", retryable: true)
            : new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 1, 0);
        _devices.ServerBehavior = _ => new ServerDiscoveryOutcome(new[] { Server("srv1") }, 1, 0);
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Succeeded);
        Step(job, DiscoveryStepName.SwitchDiscovery).AttemptCount.Should().Be(3);
        _devices.SwitchCallCount.Should().Be(3);
    }

    [Fact]
    public async Task Exhausted_retries_fails_step_and_job()
    {
        _devices.SwitchBehavior = _ =>
            throw new DiscoveryStepException(DiscoveryErrorCodes.SwitchDiscoveryFailed, "unreachable", retryable: true);
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        job.ErrorCode.Should().Be(DiscoveryErrorCodes.SwitchDiscoveryFailed);
        Step(job, DiscoveryStepName.SwitchDiscovery).Status.Should().Be(DiscoveryStepStatus.Failed);
        Step(job, DiscoveryStepName.SwitchDiscovery).AttemptCount.Should().Be(3);
        Step(job, DiscoveryStepName.BmcDiscovery).Status.Should().Be(DiscoveryStepStatus.Skipped);
        _ingestion.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Non_retryable_failure_fails_immediately()
    {
        _devices.SwitchBehavior = _ =>
            throw new DiscoveryStepException("AUTH_FAILED", "denied", retryable: false);
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        Step(job, DiscoveryStepName.SwitchDiscovery).AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Missing_definition_fails_closed()
    {
        _definition = null; // provider throws RackDefinitionMissingException
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        job.ErrorCode.Should().Be(DiscoveryErrorCodes.RackDefinitionMissing);
        job.Steps.Should().OnlyContain(s => s.Status == DiscoveryStepStatus.Skipped);
        _ingestion.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Cooperative_cancellation_midrun_skips_remaining_and_cancels_job()
    {
        _devices.SwitchBehavior = _ => new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 1, 0);
        // Simulate a concurrent cancel arriving while switch discovery runs.
        _devices.OnSwitchCall = _ => _store.CancellationRequested = true;
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Canceled);
        Step(job, DiscoveryStepName.SwitchDiscovery).Status.Should().Be(DiscoveryStepStatus.Succeeded);
        Step(job, DiscoveryStepName.BmcDiscovery).Status.Should().Be(DiscoveryStepStatus.Skipped);
        Step(job, DiscoveryStepName.Correlation).Status.Should().Be(DiscoveryStepStatus.Skipped);
        Step(job, DiscoveryStepName.Persistence).Status.Should().Be(DiscoveryStepStatus.Skipped);
        _ingestion.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Generic_step_failure_persists_operator_safe_message_not_raw_exception()
    {
        // A non-DiscoveryStepException whose message carries internal SQL/host detail (OWASP A05).
        const string rawLeak = "npgsql: relation \"discovery_job\" host=10.9.9.9 constraint ux_secret";
        _devices.SwitchBehavior = _ => throw new InvalidOperationException(rawLeak);
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        job.ErrorCode.Should().Be(DiscoveryErrorCodes.UnexpectedError);
        job.ErrorMessage.Should().Be(DiscoveryErrorCodes.MessageFor(DiscoveryErrorCodes.UnexpectedError));
        job.ErrorMessage.Should().NotContain("10.9.9.9").And.NotContain("discovery_job");

        var step = Step(job, DiscoveryStepName.SwitchDiscovery);
        step.Status.Should().Be(DiscoveryStepStatus.Failed);
        step.ErrorMessage.Should().Be(DiscoveryErrorCodes.MessageFor(DiscoveryErrorCodes.UnexpectedError));
    }

    [Fact]
    public async Task Persistence_failure_persists_operator_safe_message_not_raw_exception()
    {
        const string rawLeak = "23505: duplicate key value violates unique constraint host=10.9.9.9";
        _devices.SwitchBehavior = _ => new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 1, 0);
        _devices.ServerBehavior = _ => new ServerDiscoveryOutcome(new[] { Server("srv1") }, 1, 0);
        _ingestion = new FakeTopologyIngestionService(_ => throw new InvalidOperationException(rawLeak));
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        job.ErrorCode.Should().Be(DiscoveryErrorCodes.PersistenceFailed);
        job.ErrorMessage.Should().Be(DiscoveryErrorCodes.MessageFor(DiscoveryErrorCodes.PersistenceFailed));
        job.ErrorMessage.Should().NotContain("10.9.9.9").And.NotContain("constraint");

        Step(job, DiscoveryStepName.Persistence).ErrorMessage
            .Should().Be(DiscoveryErrorCodes.MessageFor(DiscoveryErrorCodes.PersistenceFailed));
    }

    [Fact]
    public async Task Step_failure_message_is_truncated_to_the_column_bound()
    {
        // A DiscoveryStepException whose (hypothetically) long message must be truncated before persist,
        // so the subsequent save can never trip a Postgres 22001 (value too long).
        var longMessage = new string('x', DiscoveryJobStep.MaxErrorMessageLength + 500);
        _devices.SwitchBehavior = _ =>
            throw new DiscoveryStepException(DiscoveryErrorCodes.SwitchDiscoveryFailed, longMessage, retryable: false);
        var job = NewJob();

        await Run(job);

        job.Status.Should().Be(DiscoveryJobStatus.Failed);
        Step(job, DiscoveryStepName.SwitchDiscovery).ErrorMessage!.Length
            .Should().Be(DiscoveryJobStep.MaxErrorMessageLength);
        job.ErrorMessage!.Length.Should().Be(DiscoveryJob.MaxErrorMessageLength);
    }

    [Fact]
    public async Task Persisted_step_summaries_contain_no_host_or_secret_material()
    {
        _devices.SwitchBehavior = _ => new SwitchDiscoveryOutcome(new[] { Switch("sw1") }, 1, 0);
        _devices.ServerBehavior = _ => new ServerDiscoveryOutcome(new[] { Server("srv1") }, 1, 0);
        var job = NewJob();

        await Run(job);

        var serialized = JsonSerializer.Serialize(new
        {
            job.ErrorMessage,
            steps = job.Steps.Select(s => new { s.ResultSummaryJson, s.ErrorMessage }),
        });
        serialized.Should().NotContain(SecretHost);
        serialized.Should().NotContain(SecretCredRef);
        serialized.Should().NotContain("credentialsRef");
    }

    private Task Run(DiscoveryJob job)
    {
        var orchestrator = new DiscoveryOrchestrator(
            _devices,
            new InMemoryRackDefinitionProvider(_definition),
            _engine,
            _ingestion,
            _store,
            new TestTimeProvider(Now),
            Microsoft.Extensions.Options.Options.Create(
                new DiscoveryOrchestrationOptions { MaxStepAttempts = 3, RetryBaseDelayMs = 0 }),
            NullLogger<DiscoveryOrchestrator>.Instance);
        return orchestrator.RunAsync(job, CancellationToken.None);
    }

    private static DiscoveryJob NewJob(
        TriggerType mode = TriggerType.OnDemand, string triggeredBy = "tester", ActorType actorType = ActorType.User)
    {
        var job = new DiscoveryJob(
            Guid.NewGuid(), RackId, mode, triggeredBy, actorType, Guid.NewGuid(), Now.UtcDateTime);
        job.SeedSteps(Guid.NewGuid);
        job.MarkInProgress(Now.UtcDateTime);
        return job;
    }

    private static DiscoveryJobStep Step(DiscoveryJob job, DiscoveryStepName name)
        => job.Steps.Single(s => s.StepName == name);

    private static RackDefinition Definition()
        => new(
            RackId,
            "rack-key",
            new[]
            {
                new DeviceDefinition("sw1", "Mock", null, DriverConnectionKind.Ssh, SecretHost, null,
                    TimeSpan.FromSeconds(5), SecretCredRef),
            },
            new[]
            {
                new DeviceDefinition("srv1", "Mock", null, DriverConnectionKind.Redfish, SecretHost, null,
                    TimeSpan.FromSeconds(5), SecretCredRef),
            });

    private static SwitchTopologySnapshot Switch(string id)
        => new(id, new SwitchDeviceInfo("10.0.0.1", "SER-1", "M", "7.0"),
            Array.Empty<SwitchPortInfo>(), Array.Empty<LldpNeighbourInfo>(),
            Array.Empty<BridgeHostEntry>(), Array.Empty<VlanInfo>());

    private static ServerNicSnapshot Server(string id)
        => new(id, new BmcSystemInventory(BmcType.Redfish, "10.0.1.1", "uuid-1", "host-1"),
            Array.Empty<BmcNetworkInterfaceInfo>());
}
