// Typed client for RackPullRequestController (story #173, Task #215): GET the rack's current PR status and the
// PR transition history. Modelled on `network-config/services/pr.service.ts` — URL from environment.apiBaseUrl,
// returns the shared discriminated-union `ApiResult`, percent-encoding every route segment.
import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../topology/model/topology-contracts';
import { type ApiResult, toApiResult } from '../../topology/services/api-result';
import type { PrStatusDto, PrStatusEventDto } from './pr-status-contracts';

@Injectable({ providedIn: 'root' })
export class PrStatusService {
  private readonly http = inject(HttpClient);

  private gitUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/git`;
  }

  /** Reads the rack's current persisted PR status (no forced GitHub call — respects the poll-rate NFR). */
  getStatus(rackId: string): Observable<ApiResult<PrStatusDto>> {
    return toApiResult(this.http.get<PrStatusDto>(`${this.gitUrl(rackId)}/pull-request`));
  }

  /** Reads the rack's PR status transition history, newest-first, keyset-paginated. */
  getEvents(
    rackId: string,
    cursor?: string,
    pageSize?: number,
  ): Observable<ApiResult<PagedResult<PrStatusEventDto>>> {
    let params = new HttpParams();
    if (cursor) {
      params = params.set('cursor', cursor);
    }
    if (pageSize) {
      params = params.set('pageSize', pageSize);
    }
    return toApiResult(
      this.http.get<PagedResult<PrStatusEventDto>>(`${this.gitUrl(rackId)}/pull-request/events`, {
        params,
      }),
    );
  }
}
