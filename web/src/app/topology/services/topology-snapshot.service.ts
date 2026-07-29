import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  PagedResult,
  SnapshotDetailDto,
  SnapshotDiffDto,
  SnapshotMetadataDto,
  TopologyGraphDto,
} from '../model/topology-contracts';
import { type ApiResult, toApiResult } from './api-result';

/** Typed client for RackTopologyController — snapshot/graph/diff reads (AC1/AC3). */
@Injectable({ providedIn: 'root' })
export class TopologySnapshotService {
  private readonly http = inject(HttpClient);

  // Finding #19: rackId/snapshotId are route/query-driven, not app-controlled constants, so — like
  // encodeStableKeyPath in topology-entity.service.ts — every path segment built from them must be
  // percent-encoded. An unencoded value containing `/`, `?`, or `#` would otherwise silently reshape the
  // request path or inject query parameters/fragments into it (e.g. a rackId of `abc?x=1` truncating the
  // intended path at the `?`), rather than causing an honest 404 for the id that was actually typed.
  private topologyUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/topology`;
  }

  getLatest(rackId: string): Observable<ApiResult<SnapshotDetailDto>> {
    return toApiResult(
      this.http.get<SnapshotDetailDto>(`${this.topologyUrl(rackId)}/snapshots/latest`),
    );
  }

  getById(rackId: string, snapshotId: string): Observable<ApiResult<SnapshotDetailDto>> {
    return toApiResult(
      this.http.get<SnapshotDetailDto>(
        `${this.topologyUrl(rackId)}/snapshots/${encodeURIComponent(snapshotId)}`,
      ),
    );
  }

  getHistory(
    rackId: string,
    cursor?: string,
    pageSize?: number,
  ): Observable<ApiResult<PagedResult<SnapshotMetadataDto>>> {
    let params = new HttpParams();
    if (cursor) {
      params = params.set('cursor', cursor);
    }
    if (pageSize) {
      params = params.set('pageSize', pageSize);
    }

    return toApiResult(
      this.http.get<PagedResult<SnapshotMetadataDto>>(`${this.topologyUrl(rackId)}/snapshots`, {
        params,
      }),
    );
  }

  /** The topology graph for the latest snapshot, or a specific one when `snapshotId` is given. */
  getGraph(rackId: string, snapshotId?: string): Observable<ApiResult<TopologyGraphDto>> {
    const path = snapshotId
      ? `snapshots/${encodeURIComponent(snapshotId)}/graph`
      : 'snapshots/latest/graph';
    return toApiResult(this.http.get<TopologyGraphDto>(`${this.topologyUrl(rackId)}/${path}`));
  }

  getDiff(rackId: string, from: string, to: string): Observable<ApiResult<SnapshotDiffDto>> {
    const params = new HttpParams().set('from', from).set('to', to);
    return toApiResult(
      this.http.get<SnapshotDiffDto>(`${this.topologyUrl(rackId)}/diff`, { params }),
    );
  }
}
