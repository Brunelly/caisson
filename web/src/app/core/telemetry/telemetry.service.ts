// Client-side observability (NFR6): structured events for troubleshooting API calls and (from story
// #10 step 6) SignalR connect/disconnect/reconnect/snapshot-applied/error, correlated by correlation
// id. Logs no MAC/host/credential data — only ids, urls (never bodies) and status.
import { Injectable } from '@angular/core';

export interface TelemetryEvent {
  type: string;
  correlationId: string | null;
  timestamp: string;
  detail?: Record<string, string | number | boolean | null>;
}

@Injectable({ providedIn: 'root' })
export class TelemetryService {
  private readonly events: TelemetryEvent[] = [];

  /** Records an API call's correlation id (echoed back from the response) against its request URL. */
  recordCorrelation(correlationId: string, requestUrl: string): void {
    this.record('http.correlation', correlationId, { url: requestUrl });
  }

  record(type: string, correlationId: string | null, detail?: TelemetryEvent['detail']): void {
    const event: TelemetryEvent = {
      type,
      correlationId,
      timestamp: new Date().toISOString(),
      detail,
    };
    this.events.push(event);
    // The only sink for M0; a real backend/telemetry-pipeline sink is a future story.
    console.debug('[telemetry]', event);
  }

  /** Recent events, most-recent-last — for tests and any future in-app diagnostics view. */
  recent(): readonly TelemetryEvent[] {
    return this.events;
  }
}
