// Typed client for DesiredStateImpactPreviewController (story #171): POST impact-preview (compute/cache) and
// GET by candidate id. Modelled on DesiredStateRoundTripService: an ApiResult-shaped base with dedicated
// `validationError` (400 path+line/column) and `missingBaseline` (409 reasonCode) branches the UI switches
// on — the shared ApiResult<T> union has no 400/409 discriminant.
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../../environments/environment';
import { type ApiResult, apiResultFromError } from '../../topology/services/api-result';
import type {
  ImpactPreviewIssue,
  ImpactPreviewResponse,
  MissingBaselineResponse,
} from '../model/impact-preview-contracts';

/** The impact-preview outcome: the shared union plus 400 (validationError) and 409 (missingBaseline). */
export type ImpactPreviewResult =
  | { kind: 'ok'; value: ImpactPreviewResponse }
  | { kind: 'validationError'; issues: ImpactPreviewIssue[] }
  | { kind: 'missingBaseline'; reasonCode: string; message: string }
  | Exclude<ApiResult<never>, { kind: 'ok' }>;

@Injectable({ providedIn: 'root' })
export class ImpactPreviewService {
  private readonly http = inject(HttpClient);

  private rackUrl(rackId: string): string {
    return `${environment.apiBaseUrl}/api/racks/${encodeURIComponent(rackId)}/desired-state`;
  }

  /** Computes (or serves from cache) the impact preview for a candidate YAML document. */
  preview(rackId: string, yaml: string): Observable<ImpactPreviewResult> {
    return this.http
      .post<ImpactPreviewResponse>(`${this.rackUrl(rackId)}/impact-preview`, { yaml })
      .pipe(
        map((value): ImpactPreviewResult => ({ kind: 'ok', value })),
        catchError((error: unknown) => of(this.errorFrom(error))),
      );
  }

  /** Resolves a previously-computed preview by its candidate id (the cache row id). */
  getByCandidate(rackId: string, candidateId: string): Observable<ImpactPreviewResult> {
    return this.http
      .get<ImpactPreviewResponse>(
        `${this.rackUrl(rackId)}/candidates/${encodeURIComponent(candidateId)}/impact-preview`,
      )
      .pipe(
        map((value): ImpactPreviewResult => ({ kind: 'ok', value })),
        catchError((error: unknown) => of(this.errorFrom(error))),
      );
  }

  private errorFrom(error: unknown): Exclude<ImpactPreviewResult, { kind: 'ok' }> {
    if (!(error instanceof HttpErrorResponse)) {
      throw error;
    }
    if (error.status === 400) {
      return { kind: 'validationError', issues: extractIssues(error) };
    }
    if (error.status === 409) {
      const body = error.error as MissingBaselineResponse | null;
      return {
        kind: 'missingBaseline',
        reasonCode: body?.reasonCode ?? 'DESIRED_STATE_BASELINE_MISSING',
        message: body?.message ?? 'This rack has no ingested desired-state revision yet.',
      };
    }
    return apiResultFromError<never>(error);
  }
}

/** Reads the richer `issues` extension the controller attaches to its ValidationProblem, falling back to the
 * standard `{ errors: { [path]: string[] } }` dictionary when the extension is absent. */
function extractIssues(error: HttpErrorResponse): ImpactPreviewIssue[] {
  const body = error.error as {
    issues?: ImpactPreviewIssue[];
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
