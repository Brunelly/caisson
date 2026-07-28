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
    return toApiResult(
      this.http.get<DiscoveryStatusDto>(
        `${environment.apiBaseUrl}/api/racks/${rackId}/discovery-status`,
      ),
    );
  }
}
