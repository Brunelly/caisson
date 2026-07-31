import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../topology/model/topology-contracts';
import {
  type ApiResult,
  apiResultFromError,
  toApiResult,
} from '../../topology/services/api-result';
import type {
  ApplyDriftCorrectionRequest,
  ApplyDriftCorrectionResponse,
  DriftApplyJobDetailDto,
  DriftApplyJobStatus,
  DriftApplyJobSummaryDto,
} from '../model/drift-contracts';

/** POST /drift/apply's success path is 201 (new job created) vs 202 (an active job already exists for
 * this drift item, ADR 0032's idempotent-create) — a distinction toApiResult()/ApiResult<T> can't
 * express since it treats every 2xx as the single 'ok' case. `Exclude<ApiResult<never>, {kind:'ok'}>`
 * reuses the exact same error-branch mapping (401/403/404/422/429/error) without duplicating it. */
export type ApplyDriftCorrectionResult =
  | { kind: 'created'; jobId: string }
  | { kind: 'existingJob'; jobId: string }
  // 409 (story #173): the merged-apply gate rejected the request; reasonCode is 'PrNotMerged'/'NoPrLinked'.
  | { kind: 'prGateBlocked'; reasonCode: string | null }
  | Exclude<ApiResult<never>, { kind: 'ok' }>;

export interface DriftApplyJobFilters {
  state?: DriftApplyJobStatus;
  cursor?: string;
  pageSize?: number;
}

/** Typed client for DriftApplyController (write, RBAC: DriftApply — the first write endpoint the SPA
 * calls) and DriftApplyJobController (read, RBAC: TopologyRead — any recognised role, not apply-gated).
 * Percent-encodes every route-driven id segment, matching TopologySnapshotService/DriftReportService. */
@Injectable({ providedIn: 'root' })
export class DriftApplyService {
  private readonly http = inject(HttpClient);

  private rackUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}`;
  }

  applyCorrection(rackId: string, driftItemId: string): Observable<ApplyDriftCorrectionResult> {
    const request: ApplyDriftCorrectionRequest = { driftItemId };
    return this.http
      .post<ApplyDriftCorrectionResponse>(`${this.rackUrl(rackId)}/drift/apply`, request, {
        observe: 'response',
      })
      .pipe(
        map((response): ApplyDriftCorrectionResult => {
          const jobId = (response.body as ApplyDriftCorrectionResponse).jobId;
          return response.status === 201
            ? { kind: 'created', jobId }
            : { kind: 'existingJob', jobId };
        }),
        catchError((error: unknown) => of(this.errorFrom(error))),
      );
  }

  // The shared apiResultFromError has no 409 branch (it falls through to a generic 'error'); the merged-apply
  // gate (story #173) returns 409 with a ProblemDetails reasonCode, mapped to its own branch here — the same
  // custom-errorFrom precedent network-intent.service.ts set for its 409.
  private errorFrom(
    error: unknown,
  ): Exclude<ApplyDriftCorrectionResult, { kind: 'created' | 'existingJob' }> {
    if (error instanceof HttpErrorResponse && error.status === 409) {
      const body = error.error as { reasonCode?: unknown } | null;
      return {
        kind: 'prGateBlocked',
        reasonCode: body && typeof body.reasonCode === 'string' ? body.reasonCode : null,
      };
    }
    return apiResultFromError<never>(error);
  }

  getJob(rackId: string, jobId: string): Observable<ApiResult<DriftApplyJobDetailDto>> {
    return toApiResult(
      this.http.get<DriftApplyJobDetailDto>(
        `${this.rackUrl(rackId)}/jobs/${encodeURIComponent(jobId)}`,
      ),
    );
  }

  getJobs(
    rackId: string,
    filters: DriftApplyJobFilters = {},
  ): Observable<ApiResult<PagedResult<DriftApplyJobSummaryDto>>> {
    let params = new HttpParams();
    if (filters.state) {
      params = params.set('state', filters.state);
    }
    if (filters.cursor) {
      params = params.set('cursor', filters.cursor);
    }
    if (filters.pageSize) {
      params = params.set('pageSize', filters.pageSize);
    }
    return toApiResult(
      this.http.get<PagedResult<DriftApplyJobSummaryDto>>(`${this.rackUrl(rackId)}/jobs`, {
        params,
      }),
    );
  }
}
