// A typed result for API calls so 401/403/404 are ordinary values, not thrown exceptions — every
// consumer (route guard, state service, components) has one place to branch on "access denied" rather
// than each wiring its own try/catch, and AC6 (no leaked identifiers/backend detail) is satisfied by
// construction: only the discriminant and HTTP status survive into the result, never response bodies.
import { HttpErrorResponse } from '@angular/common/http';
import { catchError, map, type Observable, of } from 'rxjs';

export type ApiResult<T> =
  | { kind: 'ok'; value: T }
  | { kind: 'unauthorized' }
  | { kind: 'forbidden' }
  | { kind: 'notFound' }
  // 422: the drift-apply write path's "unsupported drift type" / stale-revalidation rejection (ADR
  // 0032/0033) — reasonCode is read straight off the ProblemDetails extension, never parsed further.
  | { kind: 'unprocessable'; reasonCode: string | null }
  // 429: the drift-apply endpoint's fixed-window rate-limit policy (Caisson.Api.Security.
  // RateLimitPolicies.DriftApply) short-circuits before the action runs, so there is no response body.
  | { kind: 'rateLimited' }
  | { kind: 'error'; status: number };

export function toApiResult<T>(source: Observable<T>): Observable<ApiResult<T>> {
  return source.pipe(
    map((value): ApiResult<T> => ({ kind: 'ok', value })),
    catchError((error: unknown) => of(apiResultFromError<T>(error))),
  );
}

// Exported so services that can't route a whole response through toApiResult() — e.g. the drift-apply
// POST, which needs `observe: 'response'` to distinguish 201 from 202 on the success path — can still
// reuse the exact same error-branch mapping instead of duplicating it. The return type deliberately
// excludes the 'ok' case (this function only ever runs from an error/catch path) so callers composing
// their own success variants (see ApplyDriftCorrectionResult) don't have to re-exclude it themselves.
export function apiResultFromError<T>(error: unknown): Exclude<ApiResult<T>, { kind: 'ok' }> {
  if (!(error instanceof HttpErrorResponse)) {
    throw error;
  }

  switch (error.status) {
    case 401:
      return { kind: 'unauthorized' };
    case 403:
      return { kind: 'forbidden' };
    case 404:
      return { kind: 'notFound' };
    case 422:
      return { kind: 'unprocessable', reasonCode: extractReasonCode(error) };
    case 429:
      return { kind: 'rateLimited' };
    default:
      return { kind: 'error', status: error.status };
  }
}

function extractReasonCode(error: HttpErrorResponse): string | null {
  const body = error.error as { reasonCode?: unknown } | null;
  return body && typeof body.reasonCode === 'string' ? body.reasonCode : null;
}
