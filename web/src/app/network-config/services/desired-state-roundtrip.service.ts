// Typed client for DesiredStateRoundTripController (story #169): POST parse and POST render, both
// NetworkConfigAuthor-gated. Modelled directly on NetworkIntentService: an ApiResult-shaped base with a
// dedicated `validationError` branch so the 400 field/line-column errors are a distinct outcome the UI
// can switch on — the shared ApiResult<T> union has no 400 discriminant (it would fall into `error`).
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../../environments/environment';
import { type ApiResult, apiResultFromError } from '../../topology/services/api-result';
import type {
  DesiredStateImportIssueDto,
  DesiredStateRenderRequest,
  DesiredStateRenderResponse,
  DesiredStateRoundTripEnvelopeDto,
} from '../model/network-intent-contracts';

/** The parse/render 400 body: ValidationProblem `{ errors }` plus a richer `issues` extension (path+line+column). */
export type DesiredStateValidationResult =
  | { kind: 'validationError'; issues: DesiredStateImportIssueDto[] }
  | Exclude<ApiResult<never>, { kind: 'ok' }>;

export type DesiredStateParseResult =
  { kind: 'ok'; value: DesiredStateRoundTripEnvelopeDto } | DesiredStateValidationResult;

export type DesiredStateRenderResult =
  { kind: 'ok'; value: DesiredStateRenderResponse } | DesiredStateValidationResult;

@Injectable({ providedIn: 'root' })
export class DesiredStateRoundTripService {
  private readonly http = inject(HttpClient);

  private rackUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/desired-state`;
  }

  parse(rackId: string, yaml: string): Observable<DesiredStateParseResult> {
    return this.http
      .post<DesiredStateRoundTripEnvelopeDto>(`${this.rackUrl(rackId)}/parse`, { yaml })
      .pipe(
        map((value): DesiredStateParseResult => ({ kind: 'ok', value })),
        catchError((error: unknown) => of(this.errorFrom(error))),
      );
  }

  render(rackId: string, request: DesiredStateRenderRequest): Observable<DesiredStateRenderResult> {
    return this.http
      .post<DesiredStateRenderResponse>(`${this.rackUrl(rackId)}/render`, request)
      .pipe(
        map((value): DesiredStateRenderResult => ({ kind: 'ok', value })),
        catchError((error: unknown) => of(this.errorFrom(error))),
      );
  }

  private errorFrom(error: unknown): DesiredStateValidationResult {
    if (!(error instanceof HttpErrorResponse)) {
      throw error;
    }
    if (error.status === 400) {
      return { kind: 'validationError', issues: extractIssues(error) };
    }
    return apiResultFromError<never>(error);
  }
}

/** Reads the richer `issues` extension the round-trip controller attaches to its ValidationProblem, falling
 * back to the standard `{ errors: { [path]: string[] } }` dictionary when the extension is absent. */
function extractIssues(error: HttpErrorResponse): DesiredStateImportIssueDto[] {
  const body = error.error as {
    issues?: DesiredStateImportIssueDto[];
    errors?: Record<string, string[]>;
  } | null;
  if (body?.issues && Array.isArray(body.issues)) {
    return body.issues;
  }
  if (body?.errors) {
    return Object.entries(body.errors).flatMap(([path, messages]) =>
      messages.map((message) => ({ path, message, line: null, column: null })),
    );
  }
  return [];
}
