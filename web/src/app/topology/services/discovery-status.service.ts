import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { DiscoveryStatusDto } from '../model/topology-contracts';
import { type ApiResult, toApiResult } from './api-result';

/** Typed client for RackDiscoveryStatusController — the rack's at-a-glance discovery status (AC1). */
@Injectable({ providedIn: 'root' })
export class DiscoveryStatusService {
  private readonly http = inject(HttpClient);

  getStatus(rackId: string): Observable<ApiResult<DiscoveryStatusDto>> {
    // Finding #19: rackId is route-driven, not an app-controlled constant — percent-encode it (mirrors
    // TopologySnapshotService.topologyUrl) so a value containing `/`, `?`, or `#` can't reshape the
    // request path or inject query parameters/fragments instead of just 404ing on the id as typed.
    return toApiResult(
      this.http.get<DiscoveryStatusDto>(
        `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/discovery-status`,
      ),
    );
  }
}
