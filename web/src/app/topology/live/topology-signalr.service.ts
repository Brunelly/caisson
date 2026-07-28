// Live topology updates over the story-9 hub, to the letter of docs/live-topology-events.md (written
// as this service's spec): accessTokenFactory (WS handshake carries ?access_token=, matching
// Program.cs), a custom 1s->2s->4s->8s->cap-30s(+jitter) backoff, per-rack subscribe/unsubscribe,
// idempotent event application (applyIfNewer), a 30s watchdog reset by any inbound message including
// Heartbeat, and graceful degradation to REST polling when the hub is unavailable.
import { Injectable, InjectionToken, inject } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  type IRetryPolicy,
  type RetryContext,
} from '@microsoft/signalr';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TelemetryService } from '../../core/telemetry/telemetry.service';
import { deriveTopologyGraph } from '../model/topology-graph-model';
import { DiscoveryStatusService } from '../services/discovery-status.service';
import { TopologySnapshotService } from '../services/topology-snapshot.service';
import { TopologyStateService } from '../state/topology-state.service';
import {
  type WatermarkStore,
  applyIfNewer,
  jobStreamKey,
  snapshotStreamKey,
} from './apply-if-newer';

/** The documented ladder: 1s, 2s, 4s, 8s, then capped at 30s — with jitter, and never gives up (a
 * conforming client keeps retrying in the background even after falling back to polling). */
export class TopologyReconnectPolicy implements IRetryPolicy {
  private static readonly LADDER_MS = [1000, 2000, 4000, 8000];
  private static readonly CAP_MS = 30000;

  nextRetryDelayInMilliseconds(retryContext: RetryContext): number {
    const base =
      retryContext.previousRetryCount < TopologyReconnectPolicy.LADDER_MS.length
        ? TopologyReconnectPolicy.LADDER_MS[retryContext.previousRetryCount]
        : TopologyReconnectPolicy.CAP_MS;
    const jitter = base * 0.2 * Math.random();
    return Math.min(base + jitter, TopologyReconnectPolicy.CAP_MS);
  }
}

interface SnapshotUpdatedEvent {
  eventId: string;
  rackId: string;
  jobId: string | null;
  snapshotId: string;
  version: number;
  seq: number;
  correlationId: string;
}

interface DiscoveryJobStatusChangedEvent {
  eventId: string;
  rackId: string;
  jobId: string;
  status: string;
  seq: number;
  correlationId: string;
}

const WATCHDOG_MS = 30000;
const POLL_INTERVAL_MS = 15000;
const INITIAL_CONNECT_RETRY_MS = 30000;

/**
 * A DI seam for constructing the hub connection. Real usage builds an actual `HubConnection` (the
 * default factory below); tests override this token with a fake instead of mocking the
 * `@microsoft/signalr` module — module-mocking one spec file can otherwise leak into others under
 * Vitest's shared-worker (`isolate: false`) execution model, since all spec files in a run can share
 * one module registry.
 */
export const HUB_CONNECTION_FACTORY = new InjectionToken<
  (url: string, accessTokenFactory: () => string | Promise<string>) => HubConnection
>('HUB_CONNECTION_FACTORY', {
  providedIn: 'root',
  factory: () => (url, accessTokenFactory) =>
    new HubConnectionBuilder()
      .withUrl(url, { accessTokenFactory })
      .withAutomaticReconnect(new TopologyReconnectPolicy())
      .build(),
});

@Injectable({ providedIn: 'root' })
export class TopologySignalRService {
  private readonly createConnection = inject(HUB_CONNECTION_FACTORY);
  private readonly oidc = inject(OidcSecurityService);
  private readonly snapshots = inject(TopologySnapshotService);
  private readonly discoveryStatus = inject(DiscoveryStatusService);
  private readonly state = inject(TopologyStateService);
  private readonly telemetry = inject(TelemetryService);

  private connection: HubConnection | null = null;
  private currentRackId: string | null = null;
  private readonly watermarks: WatermarkStore = new Map();
  private watchdogTimer: ReturnType<typeof setTimeout> | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private initialConnectRetryTimer: ReturnType<typeof setTimeout> | null = null;

  /** Connects (or, if already connected, switches rack subscription) and subscribes to `rackId`. */
  connect(rackId: string): void {
    this.currentRackId = rackId;

    if (!this.connection) {
      this.connection = this.buildConnection();
    }

    if (this.connection.state === HubConnectionState.Disconnected) {
      this.startConnection();
    } else if (this.connection.state === HubConnectionState.Connected) {
      void this.connection.invoke('SubscribeToRack', rackId).catch(() => undefined);
    }
  }

  /** Unsubscribes and tears down the connection (page/component destroy). */
  disconnect(): void {
    const rackId = this.currentRackId;
    this.stopPolling();
    this.clearWatchdog();
    this.clearInitialConnectRetry();
    this.currentRackId = null;

    if (this.connection?.state === HubConnectionState.Connected && rackId) {
      void this.connection.invoke('UnsubscribeFromRack', rackId).catch(() => undefined);
    }

    void this.connection?.stop();
    this.connection = null;
    this.telemetry.disconnect(rackId);
  }

  private buildConnection(): HubConnection {
    const connection = this.createConnection(environment.hubUrl, () =>
      firstValueFrom(this.oidc.getAccessToken()),
    );

    connection.on('SnapshotUpdated', (event: SnapshotUpdatedEvent) =>
      this.onSnapshotUpdated(event),
    );
    connection.on('DiscoveryJobStatusChanged', (event: DiscoveryJobStatusChangedEvent) =>
      this.onDiscoveryJobStatusChanged(event),
    );
    connection.on('Heartbeat', () => this.resetWatchdog());

    connection.onreconnecting(() => {
      this.state.setConnectionStatus('disconnected');
      this.telemetry.reconnecting(this.currentRackId);
    });

    connection.onreconnected(() => {
      this.state.setConnectionStatus('live');
      this.telemetry.reconnected(this.currentRackId);
      this.resetWatchdog();
      this.stopPolling();
      if (this.currentRackId) {
        void connection.invoke('SubscribeToRack', this.currentRackId).catch(() => undefined);
        this.reconcile(this.currentRackId); // exactly one forced reconcile fetch on reconnect
      }
    });

    connection.onclose(() => {
      this.clearWatchdog();
      this.state.setConnectionStatus('disconnected');
      this.telemetry.disconnect(this.currentRackId);
      this.startPolling(); // graceful degradation while the auto-reconnect policy keeps retrying
    });

    return connection;
  }

  private startConnection(): void {
    const connection = this.connection;
    if (!connection) {
      return;
    }

    connection
      .start()
      .then(() => {
        this.telemetry.connect(this.currentRackId ?? '');
        this.state.setConnectionStatus('live');
        this.resetWatchdog();
        this.stopPolling();
        if (this.currentRackId) {
          return connection.invoke('SubscribeToRack', this.currentRackId);
        }
        return undefined;
      })
      .catch((error: unknown) => {
        this.telemetry.error('connect', String(error));
        this.state.setConnectionStatus('disconnected');
        this.startPolling();
        this.scheduleInitialConnectRetry();
      });
  }

  private scheduleInitialConnectRetry(): void {
    if (this.initialConnectRetryTimer) {
      return;
    }
    this.initialConnectRetryTimer = setTimeout(() => {
      this.initialConnectRetryTimer = null;
      if (this.currentRackId) {
        this.startConnection();
      }
    }, INITIAL_CONNECT_RETRY_MS);
  }

  private clearInitialConnectRetry(): void {
    if (this.initialConnectRetryTimer) {
      clearTimeout(this.initialConnectRetryTimer);
      this.initialConnectRetryTimer = null;
    }
  }

  private onSnapshotUpdated(event: SnapshotUpdatedEvent): void {
    this.resetWatchdog(); // any inbound message resets staleness, even a dropped duplicate
    if (event.rackId !== this.currentRackId) {
      return;
    }

    const accepted = applyIfNewer(this.watermarks, snapshotStreamKey(event.rackId), {
      seq: event.seq,
      eventId: event.eventId,
    });
    if (!accepted) {
      return;
    }

    this.reconcile(event.rackId, event.correlationId);
  }

  private onDiscoveryJobStatusChanged(event: DiscoveryJobStatusChangedEvent): void {
    this.resetWatchdog();
    if (event.rackId !== this.currentRackId) {
      return;
    }

    const accepted = applyIfNewer(this.watermarks, jobStreamKey(event.jobId), {
      seq: event.seq,
      eventId: event.eventId,
    });
    if (!accepted) {
      return;
    }

    this.discoveryStatus.getStatus(event.rackId).subscribe((result) => {
      if (result.kind === 'ok') {
        this.state.setDiscoveryStatus(result.value);
      }
    });
  }

  /** Never trusts the event summary as authoritative (docs/live-topology-events.md rule 2): always
   * refetches the latest snapshot + graph via REST and patches the (already-bound) derived graph. */
  private reconcile(rackId: string, correlationId: string | null = null): void {
    this.snapshots.getLatest(rackId).subscribe((result) => {
      if (result.kind !== 'ok') {
        return;
      }
      const graph = deriveTopologyGraph(result.value.graph);
      this.state.applyRefreshedSnapshot(result.value.snapshot, graph);
      this.telemetry.snapshotApplied(rackId, result.value.snapshot.version, correlationId);
    });
  }

  private startPolling(): void {
    if (this.pollTimer || !this.currentRackId) {
      return;
    }
    const rackId = this.currentRackId;
    this.pollTimer = setInterval(() => this.reconcile(rackId), POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private resetWatchdog(): void {
    this.clearWatchdog();
    this.watchdogTimer = setTimeout(() => this.state.setConnectionStatus('stale'), WATCHDOG_MS);
  }

  private clearWatchdog(): void {
    if (this.watchdogTimer) {
      clearTimeout(this.watchdogTimer);
      this.watchdogTimer = null;
    }
  }
}
