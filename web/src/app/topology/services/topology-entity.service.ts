import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { EntityDetailDto, EntityDiffDto } from '../model/topology-contracts';
import { type ApiResult, toApiResult } from './api-result';

/**
 * Encodes a stable key for the `{**stableKey}` catch-all route segment by segment. Stable keys like a
 * switch-port name (`Ethernet1/0/1`) legitimately contain `/`, which the catch-all route relies on to
 * see multiple path segments — so the key is split on `/` first and each segment is percent-encoded
 * independently, rather than encoding the whole key (which would turn its `/` into `%2F` and collapse
 * it into a single segment the catch-all would treat differently) or leaving segments unencoded (which
 * would break on any segment containing its own reserved characters, e.g. a `#` or `?`).
 */
export function encodeStableKeyPath(stableKey: string): string {
  return stableKey.split('/').map(encodeURIComponent).join('/');
}

/** Typed client for TopologyEntitiesController — entity detail + history reads (AC3). */
@Injectable({ providedIn: 'root' })
export class TopologyEntityService {
  private readonly http = inject(HttpClient);

  // Finding #19: rackId is route-driven, not an app-controlled constant (entityType is, but is encoded
  // too for consistency/defense in depth) — percent-encode both so neither can reshape the request path
  // or inject query parameters/fragments instead of just 404ing on the id/type as given.
  private entityUrl(rackId: string, entityType: string, stableKey: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/topology/entities/${encodeURIComponent(entityType)}/${encodeStableKeyPath(stableKey)}`;
  }

  getEntity(
    rackId: string,
    entityType: string,
    stableKey: string,
  ): Observable<ApiResult<EntityDetailDto>> {
    return toApiResult(
      this.http.get<EntityDetailDto>(this.entityUrl(rackId, entityType, stableKey)),
    );
  }

  getEntityHistory(
    rackId: string,
    entityType: string,
    stableKey: string,
  ): Observable<ApiResult<EntityDiffDto[]>> {
    const base = `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/topology/entities/${encodeURIComponent(entityType)}`;
    return toApiResult(
      this.http.get<EntityDiffDto[]>(`${base}/history/${encodeStableKeyPath(stableKey)}`),
    );
  }
}
