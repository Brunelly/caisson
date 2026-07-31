// Typed client for DesiredStatePrController (story #170): POST prs, NetworkConfigAuthor-gated. The endpoint
// re-validates server-side and blocks with a structured 422 (reasonCode + the full grouped issue set) on a
// run-id mismatch, any error, or an unacknowledged/stale warning code — a distinct `gateRejected` branch so
// the UI can route the fresh issue set back into the panel; a 202 is the gate-passed success (stub publisher).
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../../environments/environment';
import { type ApiResult, apiResultFromError } from '../../topology/services/api-result';
import type { PortAccessIntentDto, VlanCatalogueEntryDto } from '../model/network-intent-contracts';
import type {
  CreatePrResponse,
  PreflightValidationResponse,
} from '../model/preflight-validation-contracts';

export type CreatePrResult =
  | { kind: 'ok'; value: CreatePrResponse }
  // 422: the gate rejected the request; `response` is the freshly re-validated issue set to re-render.
  | {
      kind: 'gateRejected';
      reasonCode: string | null;
      response: PreflightValidationResponse | null;
    }
  | Exclude<ApiResult<never>, { kind: 'ok' }>;

@Injectable({ providedIn: 'root' })
export class PrService {
  private readonly http = inject(HttpClient);

  private rackUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/desired-state`;
  }

  createPullRequest(
    rackId: string,
    validationRunId: string,
    acknowledgedWarningCodes: string[],
    vlanCatalogue: VlanCatalogueEntryDto[],
    portIntents: PortAccessIntentDto[],
  ): Observable<CreatePrResult> {
    return this.http
      .post<CreatePrResponse>(`${this.rackUrl(rackId)}/prs`, {
        validationRunId,
        acknowledgedWarningCodes,
        vlanCatalogue,
        portIntents,
      })
      .pipe(
        map((value): CreatePrResult => ({ kind: 'ok', value })),
        catchError((error: unknown) => of(this.errorFrom(error))),
      );
  }

  private errorFrom(error: unknown): Exclude<CreatePrResult, { kind: 'ok' }> {
    if (!(error instanceof HttpErrorResponse)) {
      throw error;
    }
    if (error.status === 422) {
      const body = error.error as {
        reasonCode?: unknown;
        issues?: PreflightValidationResponse;
      } | null;
      return {
        kind: 'gateRejected',
        reasonCode: body && typeof body.reasonCode === 'string' ? body.reasonCode : null,
        response: body?.issues ?? null,
      };
    }
    return apiResultFromError<never>(error);
  }
}
