import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TelemetryService } from './telemetry.service';

describe('TelemetryService', () => {
  let service: TelemetryService;
  let debugSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    service = new TelemetryService();
    debugSpy = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
  });

  afterEach(() => {
    debugSpy.mockRestore();
  });

  it('records an http.correlation event carrying only the (already-redacted) url, never a raw body', () => {
    service.recordCorrelation('corr-1', '/api/racks/rack-1/topology/entities/Nic/:stableKey');

    expect(service.recent()).toEqual([
      expect.objectContaining({
        type: 'http.correlation',
        correlationId: 'corr-1',
        detail: { url: '/api/racks/rack-1/topology/entities/Nic/:stableKey' },
      }),
    ]);
  });

  it('records SignalR lifecycle events with only rack/job ids and status strings', () => {
    service.connect('rack-1');
    service.reconnecting('rack-1');
    service.reconnected('rack-1');
    service.snapshotApplied('rack-1', 7, 'corr-2');
    service.disconnect('rack-1');
    service.error('connect', 'boom');

    expect(service.recent().map((e) => e.type)).toEqual([
      'signalr.connect',
      'signalr.reconnecting',
      'signalr.reconnected',
      'signalr.snapshot-applied',
      'signalr.disconnect',
      'signalr.error',
    ]);
  });

  it('records drift-apply lifecycle events with only job/rack/item ids and status strings', () => {
    service.driftApplyRequested('rack-1', 'item-1', 'corr-3');
    service.driftApplyJobStatusChanged('job-1', 'Executing', 'corr-3');
    service.driftApplyOutcome('job-1', 'Completed', 'corr-3');
    service.driftApplyError('apply', 'boom', 'corr-3', 'job-1');

    expect(service.recent().map((e) => e.type)).toEqual([
      'drift.apply.requested',
      'drift.apply.job-status-changed',
      'drift.apply.outcome',
      'drift.apply.error',
    ]);
    expect(service.recent().every((e) => e.correlationId === 'corr-3')).toBe(true);
  });

  it('accumulates events in recent() in recording order', () => {
    service.record('a', null);
    service.record('b', null);

    expect(service.recent().map((e) => e.type)).toEqual(['a', 'b']);
  });

  // NFR3 ("client logs contain no ... MACs unless explicitly in debug build") is enforced by gating
  // console output on Angular's own isDevMode()/ngDevMode, the same build-time-stripped flag
  // auth.config.ts already keys its OIDC log level off; Vitest's default JIT test environment runs as
  // a dev build, so the console sink fires here exactly as it would for a real non-prod build.
  it('still writes to the console sink under the (dev-mode) test environment', () => {
    service.record('http.correlation', 'corr-1', { url: '/api/racks/rack-1/topology' });

    expect(debugSpy).toHaveBeenCalledWith(
      '[telemetry]',
      expect.objectContaining({ type: 'http.correlation' }),
    );
  });
});
