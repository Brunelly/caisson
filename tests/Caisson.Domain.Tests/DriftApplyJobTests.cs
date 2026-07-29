using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Constructor guards, state transitions, bound/scrub behaviour, and the crash-resume idempotency
/// invariant for <see cref="DriftApplyJob"/>/<see cref="DriftApplyJobStep"/> (story #65, AC4/NFR2).
/// Mirrors <see cref="DriftGuardTests"/>'s per-field style.
/// </summary>
public sealed class DriftApplyJobTests
{
    private static DriftApplyJob NewJob(int? expectedBeforeVlan = 10, int expectedAfterVlan = 20)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "v1|rack|sw1|ether1", "operator@example.com", ActorType.User,
            Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), expectedBeforeVlan, expectedAfterVlan);

    [Fact]
    public void New_job_starts_pending_with_no_steps()
    {
        var job = NewJob();

        job.Status.Should().Be(DriftApplyJobStatus.Pending);
        job.Steps.Should().BeEmpty();
        job.AttemptCount.Should().Be(0);
        job.SwitchDeviceKey.Should().BeNull();
        job.DeviceReasonCode.Should().BeNull();
    }

    [Fact]
    public void Requested_by_is_required()
    {
        var act = () => new DriftApplyJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "v1|rack|sw1|ether1", "", ActorType.User,
            Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), 10, 20);

        act.Should().Throw<ArgumentException>().WithParameterName("requestedBy");
    }

    [Fact]
    public void Requested_by_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftApplyJob.MaxActorLength + 1);

        var act = () => new DriftApplyJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "v1|rack|sw1|ether1", oversized, ActorType.User,
            Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), 10, 20);

        act.Should().Throw<ArgumentException>().WithParameterName("requestedBy");
    }

    [Fact]
    public void Subject_key_is_required()
    {
        var act = () => new DriftApplyJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "operator@example.com", ActorType.User,
            Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), 10, 20);

        act.Should().Throw<ArgumentException>().WithParameterName("subjectKey");
    }

    [Fact]
    public void Subject_key_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftApplyJob.MaxSubjectKeyLength + 1);

        var act = () => new DriftApplyJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), oversized, "operator@example.com", ActorType.User,
            Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), 10, 20);

        act.Should().Throw<ArgumentException>().WithParameterName("subjectKey");
    }

    [Fact]
    public void SeedSteps_attaches_revalidation_and_device_apply_in_declaration_order()
    {
        var job = NewJob();

        job.SeedSteps(Guid.NewGuid);

        job.Steps.Should().HaveCount(2);
        job.Steps.Select(s => s.StepName).Should().ContainInOrder(
            DriftApplyStepName.Revalidation, DriftApplyStepName.DeviceApply);
        job.Steps.Should().OnlyContain(s => s.Status == DriftApplyStepStatus.Pending);
    }

    [Fact]
    public void MarkClaimed_sets_claimed_state_and_increments_attempt_count()
    {
        var job = NewJob();
        var at = DateTime.UtcNow;

        job.MarkClaimed("instance-1", at);

        job.Status.Should().Be(DriftApplyJobStatus.Claimed);
        job.ClaimedByInstanceId.Should().Be("instance-1");
        job.ClaimedAtUtc.Should().Be(at);
        job.LastHeartbeatAtUtc.Should().Be(at);
        job.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void MarkClaimed_does_not_overwrite_claimed_at_on_a_second_call()
    {
        var job = NewJob();
        var first = DateTime.UtcNow;
        var second = first.AddMinutes(5);

        job.MarkClaimed("instance-1", first);
        job.MarkClaimed("instance-2", second);

        job.ClaimedAtUtc.Should().Be(first);
        job.LastHeartbeatAtUtc.Should().Be(second);
        job.AttemptCount.Should().Be(2);
    }

    [Fact]
    public void MarkClaimed_claimed_by_instance_id_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxActorLength + 1);

        var act = () => job.MarkClaimed(oversized, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>().WithParameterName("claimedByInstanceId");
    }

    [Fact]
    public void MarkRevalidating_transitions_status_and_refreshes_heartbeat()
    {
        var job = NewJob();
        var at = DateTime.UtcNow;

        job.MarkRevalidating(at);

        job.Status.Should().Be(DriftApplyJobStatus.Revalidating);
        job.LastHeartbeatAtUtc.Should().Be(at);
    }

    [Fact]
    public void MarkExecuting_transitions_status_and_refreshes_heartbeat()
    {
        var job = NewJob();
        var at = DateTime.UtcNow;

        job.MarkExecuting(at);

        job.Status.Should().Be(DriftApplyJobStatus.Executing);
        job.LastHeartbeatAtUtc.Should().Be(at);
    }

    [Fact]
    public void ResolveTarget_persists_switch_port_and_desired_vlan()
    {
        var job = NewJob();

        job.ResolveTarget("sw1", "ether1", 20);

        job.SwitchDeviceKey.Should().Be("sw1");
        job.PortName.Should().Be("ether1");
        job.DesiredVlanId.Should().Be(20);
    }

    [Fact]
    public void ResolveTarget_switch_device_key_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxSwitchDeviceKeyLength + 1);

        var act = () => job.ResolveTarget(oversized, "ether1", 20);

        act.Should().Throw<ArgumentException>().WithParameterName("switchDeviceKey");
    }

    [Fact]
    public void ResolveTarget_port_name_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxPortNameLength + 1);

        var act = () => job.ResolveTarget("sw1", oversized, 20);

        act.Should().Throw<ArgumentException>().WithParameterName("portName");
    }

    [Fact]
    public void RecordDeviceOutcome_persists_reason_code_confirmation_and_state()
    {
        var job = NewJob();

        job.RecordDeviceOutcome("Applied", confirmed: true, "{\"pvid\":10}", "{\"pvid\":20}");

        job.DeviceReasonCode.Should().Be("Applied");
        job.DeviceConfirmed.Should().BeTrue();
        job.BeforeStateJson.Should().Be("{\"pvid\":10}");
        job.AfterStateJson.Should().Be("{\"pvid\":20}");
    }

    [Fact]
    public void RecordDeviceOutcome_reason_code_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxErrorCodeLength + 1);

        var act = () => job.RecordDeviceOutcome(oversized, confirmed: true, null, null);

        act.Should().Throw<ArgumentException>().WithParameterName("reasonCode");
    }

    [Fact]
    public void RecordDeviceOutcome_before_state_json_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxStateJsonLength + 1);

        var act = () => job.RecordDeviceOutcome("Applied", confirmed: true, oversized, null);

        act.Should().Throw<ArgumentException>().WithParameterName("beforeStateJson");
    }

    [Fact]
    public void RecordDeviceOutcome_after_state_json_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxStateJsonLength + 1);

        var act = () => job.RecordDeviceOutcome("Applied", confirmed: true, null, oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("afterStateJson");
    }

    [Fact]
    public void RecordDeviceOutcome_called_twice_throws_the_crash_resume_guard()
    {
        var job = NewJob();
        job.RecordDeviceOutcome("Applied", confirmed: true, null, null);

        var act = () => job.RecordDeviceOutcome("AutoRolledBack", confirmed: false, null, null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at most once*");
        // The FIRST recorded outcome must survive the rejected second call untouched.
        job.DeviceReasonCode.Should().Be("Applied");
        job.DeviceConfirmed.Should().BeTrue();
    }

    [Fact]
    public void Complete_clears_any_prior_error_fields()
    {
        var job = NewJob();
        job.Fail(DateTime.UtcNow, "Infrastructure", "SOME_ERROR", "boom");

        job.Complete(DateTime.UtcNow.AddSeconds(1));

        job.Status.Should().Be(DriftApplyJobStatus.Completed);
        job.ErrorCategory.Should().BeNull();
        job.ErrorCode.Should().BeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Fail_sets_category_code_and_scrubbed_message()
    {
        var job = NewJob();

        job.Fail(DateTime.UtcNow, "Infrastructure", "DEVICE_CALL_FAILED",
            "connect failed: postgres://admin:hunter2@db.internal:5432/caisson");

        job.Status.Should().Be(DriftApplyJobStatus.Failed);
        job.ErrorCategory.Should().Be("Infrastructure");
        job.ErrorCode.Should().Be("DEVICE_CALL_FAILED");
        job.ErrorMessage.Should().NotContain("hunter2");
        job.ErrorMessage.Should().Contain("[REDACTED]");
        job.FinishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Fail_message_over_the_bound_is_truncated_not_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxErrorMessageLength + 100);

        job.Fail(DateTime.UtcNow, "Infrastructure", "UNEXPECTED_ERROR", oversized);

        job.ErrorMessage.Should().HaveLength(DriftApplyJob.MaxErrorMessageLength);
    }

    [Fact]
    public void Fail_error_category_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxErrorCategoryLength + 1);

        var act = () => job.Fail(DateTime.UtcNow, oversized, "SOME_ERROR", "boom");

        act.Should().Throw<ArgumentException>().WithParameterName("errorCategory");
    }

    [Fact]
    public void Fail_error_code_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxErrorCodeLength + 1);

        var act = () => job.Fail(DateTime.UtcNow, "Infrastructure", oversized, "boom");

        act.Should().Throw<ArgumentException>().WithParameterName("errorCode");
    }

    [Fact]
    public void MarkStaleDrift_sets_stale_drift_terminal_state_with_reason_and_details()
    {
        var job = NewJob();

        job.MarkStaleDrift(DateTime.UtcNow, "DRIFT_ITEM_GONE", "no longer current",
            "{\"comparedDriftReportId\":null}");

        job.Status.Should().Be(DriftApplyJobStatus.StaleDrift);
        job.ErrorCategory.Should().Be("StaleDrift");
        job.ErrorCode.Should().Be("DRIFT_ITEM_GONE");
        job.ErrorDetailsJson.Should().Contain("comparedDriftReportId");
        job.FinishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void MarkStaleDrift_reason_code_over_the_bound_is_rejected()
    {
        var job = NewJob();
        var oversized = new string('a', DriftApplyJob.MaxErrorCodeLength + 1);

        var act = () => job.MarkStaleDrift(DateTime.UtcNow, oversized, "no longer current");

        act.Should().Throw<ArgumentException>().WithParameterName("reasonCode");
    }

    [Fact]
    public void Step_begin_attempt_succeed_and_fail_transitions_persist_timing()
    {
        var job = NewJob();
        job.SeedSteps(Guid.NewGuid);
        var step = job.Steps.First(s => s.StepName == DriftApplyStepName.Revalidation);
        var start = DateTime.UtcNow;

        step.BeginAttempt(start);
        step.Succeed(start.AddSeconds(2), "{\"current\":true}");

        step.Status.Should().Be(DriftApplyStepStatus.Succeeded);
        step.AttemptCount.Should().Be(1);
        step.DurationMs.Should().Be(2000);
        step.ResultSummaryJson.Should().Be("{\"current\":true}");
    }

    [Fact]
    public void Step_fail_truncates_message_and_records_error_code()
    {
        var job = NewJob();
        job.SeedSteps(Guid.NewGuid);
        var step = job.Steps.First(s => s.StepName == DriftApplyStepName.DeviceApply);
        var oversized = new string('a', DriftApplyJobStep.MaxErrorMessageLength + 50);

        step.BeginAttempt(DateTime.UtcNow);
        step.Fail(DateTime.UtcNow.AddSeconds(1), "DEVICE_CALL_FAILED", oversized);

        step.Status.Should().Be(DriftApplyStepStatus.Failed);
        step.ErrorCode.Should().Be("DEVICE_CALL_FAILED");
        step.ErrorMessage.Should().HaveLength(DriftApplyJobStep.MaxErrorMessageLength);
    }

    [Fact]
    public void Step_skip_transitions_to_skipped()
    {
        var job = NewJob();
        job.SeedSteps(Guid.NewGuid);
        var step = job.Steps.First(s => s.StepName == DriftApplyStepName.DeviceApply);

        step.Skip(DateTime.UtcNow);

        step.Status.Should().Be(DriftApplyStepStatus.Skipped);
    }
}
