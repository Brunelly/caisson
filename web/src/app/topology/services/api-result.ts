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
  | { kind: 'error'; status: number };

export function toApiResult<T>(source: Observable<T>): Observable<ApiResult<T>> {
  return source.pipe(
    map((value): ApiResult<T> => ({ kind: 'ok', value })),
    catchError((error: unknown) => of(apiResultFromError<T>(error))),
  );
}

function apiResultFromError<T>(error: unknown): ApiResult<T> {
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
    default:
      return { kind: 'error', status: error.status };
  }
}
