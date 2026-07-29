import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { AuditEventDto, PagedResult } from '../model/topology-contracts';
import { type ApiResult, toApiResult } from './api-result';

export interface AuditQuery {
  from?: string;
  to?: string;
  cursor?: string;
  pageSize?: number;
}

/** Typed client for AuditController — the generic rack-scoped audit-event read endpoint (RBAC:
 * TopologyRead, any recognised role). There is no dedicated per-feature audit endpoint (e.g. for
 * drift-apply jobs specifically); consumers filter the returned AuditEventDto[] client-side by
 * targetType/targetId (story #67's audit view, ADR 0033). */
@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);

  getAudit(
    rackId: string,
    query: AuditQuery = {},
  ): Observable<ApiResult<PagedResult<AuditEventDto>>> {
    let params = new HttpParams();
    if (query.from) {
      params = params.set('from', query.from);
    }
    if (query.to) {
      params = params.set('to', query.to);
    }
    if (query.cursor) {
      params = params.set('cursor', query.cursor);
    }
    if (query.pageSize) {
      params = params.set('pageSize', query.pageSize);
    }
    return toApiResult(
      this.http.get<PagedResult<AuditEventDto>>(
        `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/audit`,
        { params },
      ),
    );
  }
}
