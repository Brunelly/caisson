// Typed client for NetworkIntentController (story #168/#176): GET (TopologyRead — any recognised role),
// PUT save and POST validate (both NetworkConfigAuthor). Modelled directly on DriftApplyService: an
// ApiResult-shaped GET, and a dedicated result type for the write path so its non-generic-'error'
// outcomes (400 field errors, 409 stale-concurrency conflict) are distinct branches the UI can switch on
// — the shared ApiResult<T> union has no dedicated 400/409 discriminants (they'd otherwise both fall
// into the generic `{ kind: 'error' }` case).
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  type ApiResult,
  apiResultFromError,
  toApiResult,
} from '../../topology/services/api-result';
import type {
  NetworkIntentDto,
  NetworkIntentSaveRequest,
  NetworkIntentValidationResponse,
} from '../model/network-intent-contracts';

const ETAG_HEADER = 'ETag';
const IF_MATCH_HEADER = 'If-Match';

/** A loaded/saved network intent together with the ETag (xmin token) it was read at — the client's
 * only handle for the optimistic-concurrency check on the next save. */
export interface NetworkIntentEnvelope {
  intent: NetworkIntentDto;
  etag: string | null;
}

export interface NetworkIntentFieldError {
  field: string;
  messages: string[];
}

export type NetworkIntentSaveResult =
  | { kind: 'ok'; value: NetworkIntentEnvelope }
  | { kind: 'validationError'; errors: NetworkIntentFieldError[] }
  // 409: someone else saved since this client last loaded/saved — surfaced by the UI as "changed
  // elsewhere, reload and reapply" (story #176).
  | { kind: 'conflict' }
  | Exclude<ApiResult<never>, { kind: 'ok' }>;

@Injectable({ providedIn: 'root' })
export class NetworkIntentService {
  private readonly http = inject(HttpClient);

  private rackUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/network-intent`;
  }

  getIntent(rackId: string): Observable<ApiResult<NetworkIntentEnvelope>> {
    return this.http.get<NetworkIntentDto>(this.rackUrl(rackId), { observe: 'response' }).pipe(
      map((response): ApiResult<NetworkIntentEnvelope> => ({
        kind: 'ok',
        value: {
          intent: response.body as NetworkIntentDto,
          etag: response.headers.get(ETAG_HEADER),
        },
      })),
      catchError((error: unknown) => of(apiResultFromError<NetworkIntentEnvelope>(error))),
    );
  }

  /** `ifMatch` is the ETag from the last GET/save this client saw — `null` only for a rack's first-ever
   * save (no prior state to conflict with). */
  saveIntent(
    rackId: string,
    request: NetworkIntentSaveRequest,
    ifMatch: string | null,
  ): Observable<NetworkIntentSaveResult> {
    const headers = ifMatch ? { [IF_MATCH_HEADER]: ifMatch } : undefined;
    return this.http
      .put<NetworkIntentDto>(this.rackUrl(rackId), request, { headers, observe: 'response' })
      .pipe(
        map((response): NetworkIntentSaveResult => ({
          kind: 'ok',
          value: {
            intent: response.body as NetworkIntentDto,
            etag: response.headers.get(ETAG_HEADER),
          },
        })),
        catchError((error: unknown) => of(this.saveErrorFrom(error))),
      );
  }

  /** The server intent-validation stub (story #176): identical rules to the PUT save path, persists
   * nothing. Full pre-flight validation against live discovered inventory is story #170. */
  validate(
    rackId: string,
    request: NetworkIntentSaveRequest,
  ): Observable<ApiResult<NetworkIntentValidationResponse>> {
    return toApiResult(
      this.http.post<NetworkIntentValidationResponse>(`${this.rackUrl(rackId)}/validate`, request),
    );
  }

  private saveErrorFrom(error: unknown): Exclude<NetworkIntentSaveResult, { kind: 'ok' }> {
    if (!(error instanceof HttpErrorResponse)) {
      throw error;
    }
    if (error.status === 400) {
      return { kind: 'validationError', errors: extractFieldErrors(error) };
    }
    if (error.status === 409) {
      return { kind: 'conflict' };
    }
    return apiResultFromError<never>(error);
  }
}

/** ASP.NET Core's ValidationProblem(ModelState) shape: `{ errors: { [field]: string[] } }`. */
function extractFieldErrors(error: HttpErrorResponse): NetworkIntentFieldError[] {
  const body = error.error as { errors?: Record<string, string[]> } | null;
  if (!body?.errors) {
    return [];
  }
  return Object.entries(body.errors).map(([field, messages]) => ({ field, messages }));
}
