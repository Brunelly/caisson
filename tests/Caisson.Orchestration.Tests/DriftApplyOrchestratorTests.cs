using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Orchestration.DriftApply;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Caisson.Orchestration.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// DB-free unit tests for the two-step drift-apply pipeline (story #65, AC3/AC4/NFR2) — a fake
/// <see cref="IDriftApplyJobStore"/>/<see cref="ISwitchMutatingDriverRegistry"/> stand in for the real
/// DB-touching/driver-resolving pieces.
/// </summary>
public sealed class DriftApplyOrchestratorTests
{
    private static readonly Guid RackId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeDriftApplyJobStore _store = new();
    private readonly FakeDriftComputationService _driftComputation = new();
    private readonly FakeRackDefinitionProvider _rackDefinitions = new() { Definition = Definition() };
    private readonly FakeSwitchMutatingDriver _driver = new();
    private readonly FakeSwitchMutatingDriverRegistry _registry = new();
    private readonly RecordingDriftRecomputeSignal _driftRecompute = new();

    public DriftApplyOrchestratorTests()
    {
        _registry.Factory = new FakeSwitchMutatingDriverFactory(_driver);
    }

    [Theory]
    [InlineData(SwitchChangeReasonCode.Applied, DriftApplyJobStatus.Completed)]
    [InlineData(SwitchChangeReasonCode.NoOpAlreadyDesiredState, DriftApplyJobStatus.Completed)]
    [InlineData(SwitchChangeReasonCode.AutoRolledBack, DriftApplyJobStatus.Failed)]
    [InlineData(SwitchChangeReasonCode.VerificationFailed, DriftApplyJobStatus.Failed)]
    [InlineData(SwitchChangeReasonCode.InvalidVlanId, DriftApplyJobStatus.Failed)]
    [InlineData(SwitchChangeReasonCode.VlanNotConfigured, DriftApplyJobStatus.Failed)]
    [InlineData(SwitchChangeReasonCode.PortNotFound, DriftApplyJobStatus.Failed)]
    [InlineData(SwitchChangeReasonCode.AmbiguousPort, DriftApplyJobStatus.Failed)]
    public async Task Each_reason_code_maps_to_the_correct_terminal_status(
        SwitchChangeReasonCode reasonCode, DriftApplyJobStatus expectedStatus)
    {
        var job = NewJob();
        _store.ItemBehavior = (_, _) => CurrentItem(job);
        _driver.Behavior = _ => DriverResult<SetAccessVlanOutcome>.Ok(
            FakeSwitchMutatingDriver.Outcome(reasonCode), TimeSpan.FromMilliseconds(5));

        await Run(job);

        job.Status.Should().Be(expectedStatus);
        job.DeviceReasonCode.Should().Be(reasonCode.ToString());
        _driver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Successful_completion_enqueues_a_drift_recompute_signal()
    {
        var job = NewJob();
        _store.ItemBehavior = (_, _) => CurrentItem(job);
        _driver.Behavior = _ => DriverResult<SetAccessVlanOutcome>.Ok(
            FakeSwitchMutatingDriver.Outcome(SwitchChangeReasonCode.Applied), TimeSpan.FromMilliseconds(5));

        await Run(job);

        _driftRecompute.EnqueuedRackIds.Should().ContainSingle().Which.Should().Be(job.RackId);
    }

    [Fact]
    public async Task Withheld_confirmation_fails_the_job_with_the_reason_code_and_makes_exactly_one_device_call()
    {
        var job = NewJob();
        _store.ItemBehavior = (_, _) => CurrentItem(job);
        _driver.Behavior = _ => DriverResult<SetAccessVlanOutcome>.Ok(
            FakeSwitchMutatingDriver.Outcome(SwitchChangeReasonCode.AutoRolledBack, confirmed: false), TimeSpan.FromMilliseconds(5));

        await Run(job);

        job.Status.Should().Be(DriftApplyJobStatus.Failed);
        job.ErrorCategory.Should().Be(DriftApplyErrorCategories.DeviceRejected);
        job.DeviceReasonCode.Should().Be(SwitchChangeReasonCode.AutoRolledBack.ToString());
        job.DeviceConfirmed.Should().BeFalse();
        _driver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Missing_drift_item_marks_stale_drift_and_never_calls_the_driver()
    {
        var job = NewJob();
        _store.ItemBehavior = (_, _) => null; // recompute found nothing for this DriftItemId (AC3)

        await Run(job);

        job.Status.Should().Be(DriftApplyJobStatus.StaleDrift);
        job.ErrorCode.Should().Be(DriftApplyErrorCodes.DriftItemGone);
        job.SwitchDeviceKey.Should().BeNull("revalidation must never resolve a target for stale drift");
        _driver.CallCount.Should().Be(0);
        _driftComputation.ComputedRackIds.Should().ContainSingle().Which.Should().Be(job.RackId);
    }

    [Fact]
    public async Task Changed_anchors_mark_stale_drift_and_never_call_the_driver()
    {
        var job = NewJob(); // anchors: before=10, after=20
        // Revalidation now observes a DIFFERENT actual/expected pair than the anchors captured at request time.
        _store.ItemBehavior = (_, _) => BuildItem(job, expectedValue: "30", actualValue: "10");

        await Run(job);

        job.Status.Should().Be(DriftApplyJobStatus.StaleDrift);
        job.ErrorCode.Should().Be(DriftApplyErrorCodes.DriftAnchorsMismatched);
        _driver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Crash_after_recording_device_outcome_but_before_finalize_resumes_without_a_second_device_call()
    {
        // Simulates a process crash between RecordDeviceOutcome (persisted) and the terminal
        // Complete/Fail transition: the job is still Executing, but DeviceReasonCode is already set.
        var job = NewJob();
        job.SeedSteps(Guid.NewGuid);
        job.ResolveTarget("sw1", "ether1", 20);
        job.MarkExecuting(Now.UtcDateTime);
        job.RecordDeviceOutcome(SwitchChangeReasonCode.Applied.ToString(), confirmed: true, "{\"pvid\":10}", "{\"pvid\":20}");
        _driver.Behavior = _ => throw new InvalidOperationException("the driver must never be called on resume");

        await Run(job);

        job.Status.Should().Be(DriftApplyJobStatus.Completed);
        _driver.CallCount.Should().Be(0, "the crash-resume guard must skip the driver once an outcome is already recorded");
    }

    [Fact]
    public async Task Resolved_target_is_not_re_revalidated_on_resume()
    {
        // Revalidation already succeeded on a prior attempt (SwitchDeviceKey is set); the fake item
        // lookup would return null if called again, which would incorrectly mark the job stale if
        // RunAsync re-ran revalidation instead of resuming straight into DeviceApply.
        var job = NewJob();
        job.SeedSteps(Guid.NewGuid);
        job.ResolveTarget("sw1", "ether1", 20);
        _store.ItemBehavior = (_, _) => null;
        _driver.Behavior = _ => DriverResult<SetAccessVlanOutcome>.Ok(
            FakeSwitchMutatingDriver.Outcome(SwitchChangeReasonCode.Applied), TimeSpan.FromMilliseconds(5));

        await Run(job);

        job.Status.Should().Be(DriftApplyJobStatus.Completed);
        _driver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Unconfigured_switch_fails_the_job_without_calling_the_driver()
    {
        var job = NewJob();
        _store.ItemBehavior = (_, _) => CurrentItem(job);
        _rackDefinitions.Definition = Definition(deviceKey: "some-other-switch");

        await Run(job);

        job.Status.Should().Be(DriftApplyJobStatus.Failed);
        job.ErrorCode.Should().Be(DriftApplyErrorCodes.SwitchNotConfigured);
        _driver.CallCount.Should().Be(0);
    }

    private async Task Run(DriftApplyJob job)
    {
        var orchestrator = new DriftApplyOrchestrator(
            _store,
            _driftComputation,
            _rackDefinitions,
            _registry,
            _driftRecompute,
            new NoOpTopologyEventPublisher(),
            new InProcessTopologyEventSequencer(),
            new TestTimeProvider(Now),
            Microsoft.Extensions.Options.Options.Create(new DriftApplyOrchestrationOptions { RetryBaseDelayMs = 0, MaxStepAttempts = 2 }),
            NullLogger<DriftApplyOrchestrator>.Instance);

        await orchestrator.RunAsync(job, default);
    }

    private static DriftApplyJob NewJob(int? expectedBeforeVlan = 10, int expectedAfterVlan = 20)
    {
        var job = new DriftApplyJob(
            Guid.NewGuid(), RackId, Guid.NewGuid(), "operator@example.com", ActorType.User,
            Guid.NewGuid(), Now.UtcDateTime, Guid.NewGuid(), expectedBeforeVlan, expectedAfterVlan);
        job.SeedSteps(Guid.NewGuid);
        return job;
    }

    private static DriftItem CurrentItem(DriftApplyJob job)
        => BuildItem(job, job.ExpectedAfterVlan.ToString(), job.ExpectedBeforeVlan?.ToString());

    private static DriftItem BuildItem(DriftApplyJob job, string? expectedValue, string? actualValue)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), job.DriftItemId, job.RackId, DriftType.AccessVlanMismatch,
            DriftSeverity.High, actionable: true, DriftSubjectType.SwitchPort, "v1|rack|sw1|ether1",
            expectedValue, actualValue, "why", DateTime.UtcNow,
            "{\"switchName\":\"sw1\",\"portName\":\"ether1\"}");

    private static RackDefinition Definition(string deviceKey = "sw1")
        => new(
            RackId, "rack-key",
            new[]
            {
                new DeviceDefinition(
                    deviceKey, "MikroTik", null, DriverConnectionKind.Ssh, "10.0.0.1", null,
                    TimeSpan.FromSeconds(2), "kv://switch/ref"),
            },
            Array.Empty<DeviceDefinition>());
}
