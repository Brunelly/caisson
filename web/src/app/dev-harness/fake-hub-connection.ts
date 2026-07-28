// A test double for @microsoft/signalr's HubConnection, shaped to the subset TopologySignalRService
// actually calls (see HUB_CONNECTION_FACTORY in topology/live/topology-signalr.service.ts — the same DI
// seam its own unit spec uses). Exposes `simulate*` methods so Playwright can drive the real
// reconnect/reconcile/watchdog state machine from outside the page (see dev-harness.providers.ts).
import { HubConnectionState } from '@microsoft/signalr';

type EventHandler = (...args: unknown[]) => void;

export class FakeHubConnection {
  state: HubConnectionState = HubConnectionState.Disconnected;

  private readonly handlers = new Map<string, EventHandler>();
  private reconnectingCb: (() => void) | null = null;
  private reconnectedCb: (() => void) | null = null;
  private closeCb: (() => void) | null = null;
  readonly invocations: { method: string; args: unknown[] }[] = [];

  on(eventName: string, handler: EventHandler): void {
    this.handlers.set(eventName, handler);
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

  start(): Promise<void> {
    this.state = HubConnectionState.Connected;
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
    return Promise.resolve();
  }

  invoke(method: string, ...args: unknown[]): Promise<void> {
    this.invocations.push({ method, args });
    return Promise.resolve();
  }

  // --- Harness-only driver API, called from Playwright via window.__harness__.hub ---

  simulateSnapshotUpdated(event: unknown): void {
    this.handlers.get('SnapshotUpdated')?.(event);
  }

  simulateDiscoveryJobStatusChanged(event: unknown): void {
    this.handlers.get('DiscoveryJobStatusChanged')?.(event);
  }

  simulateHeartbeat(): void {
    this.handlers.get('Heartbeat')?.();
  }

  simulateReconnecting(): void {
    this.state = HubConnectionState.Reconnecting;
    this.reconnectingCb?.();
  }

  simulateReconnected(): void {
    this.state = HubConnectionState.Connected;
    this.reconnectedCb?.();
  }

  simulateClose(): void {
    this.state = HubConnectionState.Disconnected;
    this.closeCb?.();
  }
}
