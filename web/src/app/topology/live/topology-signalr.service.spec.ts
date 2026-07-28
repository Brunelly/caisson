import { TestBed } from '@angular/core/testing';
import type { HubConnection } from '@microsoft/signalr';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TelemetryService } from '../../core/telemetry/telemetry.service';
import type { DiscoveryStatusDto, SnapshotDetailDto } from '../model/topology-contracts';
import { DiscoveryStatusService } from '../services/discovery-status.service';
import { TopologySnapshotService } from '../services/topology-snapshot.service';
import { TopologyStateService } from '../state/topology-state.service';
import { HUB_CONNECTION_FACTORY, TopologySignalRService } from './topology-signalr.service';

type EventHandler = (payload: unknown) => void;

class FakeHubConnection {
  state: 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting' = 'Disconnected';
  startBehavior: 'resolve' | 'reject' = 'resolve';
  invokeCalls: { method: string; args: unknown[] }[] = [];
  private readonly handlers = new Map<string, EventHandler>();
  private reconnectingCb?: () => void;
  private reconnectedCb?: () => void;
  private closeCb?: () => void;

  on(method: string, cb: EventHandler): void {
    this.handlers.set(method, cb);
  }

  start(): Promise<void> {
    if (this.startBehavior === 'reject') {
      return Promise.reject(new Error('connection refused'));
    }
    this.state = 'Connected';
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.state = 'Disconnected';
    return Promise.resolve();
  }

  invoke(method: string, ...args: unknown[]): Promise<void> {
    this.invokeCalls.push({ method, args });
    return Promise.resolve();
  }

  onreconnecting(cb: () => void): void {
    this.reconnectingCb = cb;
  }

  onreconnected(cb: () => void): void {
    this.reconnectedCb = cb;
  }

  onclose(cb: () => void): void {
    this.closeCb = cb;
  }

  // --- test helpers, not part of the real HubConnection surface ---
  emit(method: string, payload: unknown): void {
    this.handlers.get(method)?.(payload);
  }

  triggerReconnecting(): void {
    this.state = 'Reconnecting';
    this.reconnectingCb?.();
  }

  triggerReconnected(): void {
    this.state = 'Connected';
    this.reconnectedCb?.();
  }

  triggerClose(): void {
    this.state = 'Disconnected';
    this.closeCb?.();
  }
}

let lastConnection: FakeHubConnection | null = null;
let nextStartBehavior: 'resolve' | 'reject' = 'resolve';

/** DI-overridden in place of the real hub-connection factory (see HUB_CONNECTION_FACTORY) — avoids
 * mocking the `@microsoft/signalr` module, which under Vitest's shared-worker execution model can leak
 * across spec files instead of staying scoped to this one. */
function fakeHubConnectionFactory(): HubConnection {
  lastConnection = new FakeHubConnection();
  lastConnection.startBehavior = nextStartBehavior;
  return lastConnection as unknown as HubConnection;
}

function snapshotDetail(version: number): SnapshotDetailDto {
  return {
    snapshot: {
      snapshotId: `snap-${version}`,
      version,
      triggerType: 'OnDemand',
      createdBy: 'system',
      source: 'chr',
      sourceVersion: null,
      createdAt: '2026-01-01T00:00:00Z',
      startedAt: null,
      completedAt: null,
      correlationId: 'corr-1',
      status: 'Completed',
      diffSummary: null,
    },
    graph: {
      snapshotId: `snap-${version}`,
      version,
      correlationId: 'corr-1',
      servers: [],
      unmappedPorts: [],
    },
  };
}

describe('TopologySignalRService', () => {
  let service: TopologySignalRService;
  let getLatest: ReturnType<typeof vi.fn>;
  let getDiscoveryStatus: ReturnType<typeof vi.fn>;
  let applyRefreshedSnapshot: ReturnType<typeof vi.fn>;
  let setConnectionStatus: ReturnType<typeof vi.fn>;
  let setDiscoveryStatus: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    lastConnection = null;
    nextStartBehavior = 'resolve';

    getLatest = vi.fn(() => of({ kind: 'ok' as const, value: snapshotDetail(1) }));
    getDiscoveryStatus = vi.fn(() =>
      of({ kind: 'ok' as const, value: { rackId: 'rack-1' } as unknown as DiscoveryStatusDto }),
    );
    applyRefreshedSnapshot = vi.fn();
    setConnectionStatus = vi.fn();
    setDiscoveryStatus = vi.fn();

    TestBed.configureTestingModule({
      providers: [
        { provide: HUB_CONNECTION_FACTORY, useValue: fakeHubConnectionFactory },
        { provide: OidcSecurityService, useValue: { getAccessToken: () => of('token') } },
        { provide: TopologySnapshotService, useValue: { getLatest } },
        { provide: DiscoveryStatusService, useValue: { getStatus: getDiscoveryStatus } },
        {
          provide: TopologyStateService,
          useValue: { applyRefreshedSnapshot, setConnectionStatus, setDiscoveryStatus },
        },
        { provide: TelemetryService, useValue: new TelemetryService() },
      ],
    });

    service = TestBed.inject(TopologySignalRService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function connectAndFlush(rackId = 'rack-1') {
    service.connect(rackId);
    await Promise.resolve();
    await Promise.resolve();
  }

  it('connects, subscribes to the rack, and marks the connection live', async () => {
    await connectAndFlush();

    expect(lastConnection!.state).toBe('Connected');
    expect(lastConnection!.invokeCalls).toContainEqual({
      method: 'SubscribeToRack',
      args: ['rack-1'],
    });
    expect(setConnectionStatus).toHaveBeenCalledWith('live');
  });

  it('reconciles (refetches latest snapshot/graph) on an accepted SnapshotUpdated event', async () => {
    await connectAndFlush();

    lastConnection!.emit('SnapshotUpdated', {
      eventId: 'e1',
      rackId: 'rack-1',
      jobId: null,
      snapshotId: 'snap-2',
      version: 2,
      seq: 2,
      correlationId: 'corr-2',
    });
    await Promise.resolve();

    expect(getLatest).toHaveBeenCalledWith('rack-1');
    expect(applyRefreshedSnapshot).toHaveBeenCalledTimes(1);
  });

  it('ignores a duplicate/out-of-order SnapshotUpdated event (idempotency, NFR2)', async () => {
    await connectAndFlush();

    const event = {
      eventId: 'e1',
      rackId: 'rack-1',
      jobId: null,
      snapshotId: 'snap-2',
      version: 2,
      seq: 2,
      correlationId: 'corr-2',
    };
    lastConnection!.emit('SnapshotUpdated', event);
    await Promise.resolve();
    lastConnection!.emit('SnapshotUpdated', event); // exact duplicate
    await Promise.resolve();
    lastConnection!.emit('SnapshotUpdated', { ...event, seq: 1, eventId: 'e0' }); // older
    await Promise.resolve();

    expect(applyRefreshedSnapshot).toHaveBeenCalledTimes(1);
  });

  it('ignores events for a rack other than the currently subscribed one', async () => {
    await connectAndFlush('rack-1');

    lastConnection!.emit('SnapshotUpdated', {
      eventId: 'e1',
      rackId: 'rack-other',
      jobId: null,
      snapshotId: 'snap-2',
      version: 2,
      seq: 2,
      correlationId: 'corr-2',
    });
    await Promise.resolve();

    expect(applyRefreshedSnapshot).not.toHaveBeenCalled();
  });

  it('refetches discovery status on an accepted DiscoveryJobStatusChanged event', async () => {
    await connectAndFlush();

    lastConnection!.emit('DiscoveryJobStatusChanged', {
      eventId: 'e1',
      rackId: 'rack-1',
      jobId: 'job-1',
      status: 'InProgress',
      seq: 1,
      correlationId: 'corr-1',
    });
    await Promise.resolve();

    expect(getDiscoveryStatus).toHaveBeenCalledWith('rack-1');
    expect(setDiscoveryStatus).toHaveBeenCalledTimes(1);
  });

  it('flips to stale if no message (incl. Heartbeat) arrives for 30s', async () => {
    await connectAndFlush();
    setConnectionStatus.mockClear();

    await vi.advanceTimersByTimeAsync(29_000);
    expect(setConnectionStatus).not.toHaveBeenCalledWith('stale');

    await vi.advanceTimersByTimeAsync(2_000);
    expect(setConnectionStatus).toHaveBeenCalledWith('stale');
  });

  it('a Heartbeat resets the 30s watchdog', async () => {
    await connectAndFlush();
    setConnectionStatus.mockClear();

    await vi.advanceTimersByTimeAsync(20_000);
    lastConnection!.emit('Heartbeat', { type: 'heartbeat', eventId: 'hb-1', timestamp: 'now' });
    await vi.advanceTimersByTimeAsync(20_000); // 40s total, but only 20s since the heartbeat

    expect(setConnectionStatus).not.toHaveBeenCalledWith('stale');
  });

  it('onreconnecting marks disconnected; onreconnected re-subscribes and forces exactly one reconcile', async () => {
    await connectAndFlush();
    getLatest.mockClear();

    lastConnection!.triggerReconnecting();
    expect(setConnectionStatus).toHaveBeenCalledWith('disconnected');

    lastConnection!.invokeCalls = [];
    lastConnection!.triggerReconnected();
    await Promise.resolve();

    expect(setConnectionStatus).toHaveBeenCalledWith('live');
    expect(lastConnection!.invokeCalls).toContainEqual({
      method: 'SubscribeToRack',
      args: ['rack-1'],
    });
    expect(getLatest).toHaveBeenCalledTimes(1);
  });

  it('onclose marks disconnected and degrades to polling snapshots/latest', async () => {
    await connectAndFlush();
    getLatest.mockClear();

    lastConnection!.triggerClose();
    expect(setConnectionStatus).toHaveBeenCalledWith('disconnected');

    await vi.advanceTimersByTimeAsync(15_000);
    expect(getLatest).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(15_000);
    expect(getLatest).toHaveBeenCalledTimes(2);
  });

  it('falls back to polling and keeps retrying the initial connection when start() fails', async () => {
    nextStartBehavior = 'reject';
    service.connect('rack-1');
    await Promise.resolve();
    await Promise.resolve();

    expect(setConnectionStatus).toHaveBeenCalledWith('disconnected');

    await vi.advanceTimersByTimeAsync(15_000);
    expect(getLatest).toHaveBeenCalledTimes(1); // polling fallback engaged

    lastConnection!.startBehavior = 'resolve';
    await vi.advanceTimersByTimeAsync(30_000); // scheduled initial-connect retry
    await Promise.resolve();
    await Promise.resolve();

    expect(lastConnection!.state).toBe('Connected');
  });

  it('connect() with a different rackId while connected unsubscribes the previous rack first', async () => {
    await connectAndFlush('rack-1');

    lastConnection!.invokeCalls = [];
    service.connect('rack-2');
    await Promise.resolve();

    expect(lastConnection!.invokeCalls).toEqual([
      { method: 'UnsubscribeFromRack', args: ['rack-1'] },
      { method: 'SubscribeToRack', args: ['rack-2'] },
    ]);
  });

  it('connect() with the same rackId is a no-op (no duplicate subscribe)', async () => {
    await connectAndFlush('rack-1');

    lastConnection!.invokeCalls = [];
    service.connect('rack-1');
    await Promise.resolve();

    expect(lastConnection!.invokeCalls).toEqual([]);
  });

  it('disconnect unsubscribes, stops the connection, and clears timers', async () => {
    await connectAndFlush();

    service.disconnect();
    await Promise.resolve();

    expect(lastConnection!.invokeCalls).toContainEqual({
      method: 'UnsubscribeFromRack',
      args: ['rack-1'],
    });
  });
});
