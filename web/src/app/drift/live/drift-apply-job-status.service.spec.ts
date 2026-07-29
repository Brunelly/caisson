import { describe, expect, it } from 'vitest';
import type {
  DriftApplyJobDetailDto,
  DriftApplyJobStatusChangedEvent,
} from '../model/drift-contracts';
import { DriftApplyJobStatusService } from './drift-apply-job-status.service';

function event(
  overrides: Partial<DriftApplyJobStatusChangedEvent> = {},
): DriftApplyJobStatusChangedEvent {
  return {
    rackId: 'rack-1',
    jobId: 'job-1',
    status: 'Executing',
    previousStatus: 'Revalidating',
    currentStep: 'DeviceApply',
    reasonCode: null,
    errorCode: null,
    timestamp: '2026-01-01T00:00:00Z',
    seq: 3,
    correlationId: 'corr-1',
    ...overrides,
  };
}

function detail(overrides: Partial<DriftApplyJobDetailDto> = {}): DriftApplyJobDetailDto {
  return {
    jobId: 'job-1',
    rackId: 'rack-1',
    driftItemId: 'item-1',
    status: 'Completed',
    requestedAt: '2026-01-01T00:00:00Z',
    claimedAt: null,
    finishedAt: '2026-01-01T00:01:00Z',
    requestedBy: 'operator@example.com',
    actorType: 'User',
    correlationId: 'corr-2',
    attemptCount: 1,
    currentStep: null,
    switchDeviceKey: 'sw-01',
    portName: 'ether5',
    desiredVlanId: 200,
    deviceReasonCode: null,
    deviceConfirmed: true,
    beforeState: '100',
    afterState: '200',
    errorCategory: null,
    errorCode: null,
    errorMessage: null,
    steps: [],
    ...overrides,
  };
}

describe('DriftApplyJobStatusService', () => {
  it('returns null for an unknown jobId', () => {
    const service = new DriftApplyJobStatusService();
    expect(service.statusFor('unknown')).toBeNull();
  });

  it('applyEvent stores a normalized snapshot from a live hub event', () => {
    const service = new DriftApplyJobStatusService();

    service.applyEvent(event({ status: 'Executing', currentStep: 'DeviceApply' }));

    expect(service.statusFor('job-1')).toEqual({
      jobId: 'job-1',
      status: 'Executing',
      currentStep: 'DeviceApply',
      reasonCode: null,
      errorCode: null,
      correlationId: 'corr-1',
    });
  });

  it('applyPolledDetail stores a normalized snapshot from a REST getJob response', () => {
    const service = new DriftApplyJobStatusService();

    service.applyPolledDetail(detail({ status: 'Completed', deviceReasonCode: 'Confirmed' }));

    expect(service.statusFor('job-1')).toEqual({
      jobId: 'job-1',
      status: 'Completed',
      currentStep: null,
      reasonCode: 'Confirmed',
      errorCode: null,
      correlationId: 'corr-2',
    });
  });

  it('a later update (event or poll) always overwrites the prior snapshot for the same jobId', () => {
    const service = new DriftApplyJobStatusService();

    service.applyEvent(event({ status: 'Executing' }));
    service.applyPolledDetail(detail({ status: 'Completed' }));

    expect(service.statusFor('job-1')?.status).toBe('Completed');
  });

  it('tracks independent snapshots per jobId', () => {
    const service = new DriftApplyJobStatusService();

    service.applyEvent(event({ jobId: 'job-1', status: 'Executing' }));
    service.applyEvent(event({ jobId: 'job-2', status: 'Pending' }));

    expect(service.statusFor('job-1')?.status).toBe('Executing');
    expect(service.statusFor('job-2')?.status).toBe('Pending');
  });
});
