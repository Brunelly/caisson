import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../topology/model/topology-contracts';
import { type ApiResult, toApiResult } from '../../topology/services/api-result';
import type {
  DriftItemDto,
  DriftReportDetailDto,
  DriftReportSummaryDto,
  DriftSeverity,
  DriftType,
} from '../model/drift-contracts';

/** Server-side filters for `getReportById` — the only drift-list read that supports them (Caisson.Api.
 * Controllers.DriftController's `reports/{driftReportId}` action). `getLatest`/`getHistory` do not. */
export interface DriftReportItemFilters {
  severity?: DriftSeverity;
  driftType?: DriftType;
  actionable?: boolean;
  cursor?: string;
  pageSize?: number;
}

/** Typed client for DriftController — read-only drift report/item queries (RBAC: TopologyRead, any
 * recognised role). Mirrors TopologySnapshotService exactly: percent-encodes every route-driven id
 * segment (rackId/driftReportId/driftItemId are not app-controlled constants) and returns ApiResult
 * values so 401/403/404/422/429 are ordinary data, never thrown. */
@Injectable({ providedIn: 'root' })
export class DriftReportService {
  private readonly http = inject(HttpClient);

  private driftUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/drift`;
  }

  getLatest(rackId: string): Observable<ApiResult<DriftReportDetailDto>> {
    return toApiResult(this.http.get<DriftReportDetailDto>(`${this.driftUrl(rackId)}/latest`));
  }

  getHistory(
    rackId: string,
    cursor?: string,
    pageSize?: number,
  ): Observable<ApiResult<PagedResult<DriftReportSummaryDto>>> {
    let params = new HttpParams();
    if (cursor) {
      params = params.set('cursor', cursor);
    }
    if (pageSize) {
      params = params.set('pageSize', pageSize);
    }
    return toApiResult(
      this.http.get<PagedResult<DriftReportSummaryDto>>(`${this.driftUrl(rackId)}/history`, {
        params,
      }),
    );
  }

  getReportById(
    rackId: string,
    driftReportId: string,
    filters: DriftReportItemFilters = {},
  ): Observable<ApiResult<DriftReportDetailDto>> {
    let params = new HttpParams();
    if (filters.severity) {
      params = params.set('severity', filters.severity);
    }
    if (filters.driftType) {
      params = params.set('driftType', filters.driftType);
    }
    if (filters.actionable !== undefined) {
      params = params.set('actionable', filters.actionable);
    }
    if (filters.cursor) {
      params = params.set('cursor', filters.cursor);
    }
    if (filters.pageSize) {
      params = params.set('pageSize', filters.pageSize);
    }
    return toApiResult(
      this.http.get<DriftReportDetailDto>(
        `${this.driftUrl(rackId)}/reports/${encodeURIComponent(driftReportId)}`,
        { params },
      ),
    );
  }

  getItemById(rackId: string, driftItemId: string): Observable<ApiResult<DriftItemDto>> {
    return toApiResult(
      this.http.get<DriftItemDto>(
        `${this.driftUrl(rackId)}/items/${encodeURIComponent(driftItemId)}`,
      ),
    );
  }
}
