// Routed at /racks/:rackId/drift (AC2). Rack is fixed by the route (shown as a static header — no
// cross-rack filter, since no API supports one). Resolves the rack's current report via
// DriftReportStateService.loadRackDrift, then drives severity/driftType/actionable filtering entirely
// server-side (getReportById re-fetch, never a client-side array filter) so keyset pagination stays
// correct. Filters are query-param-bound so filter state survives navigation/back-forward.
import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { ParamMap } from '@angular/router';
import { combineLatest } from 'rxjs';
import type { DriftItemDto, DriftSeverity, DriftType } from '../model/drift-contracts';
import { JobStatusBadgeComponent } from '../apply/job-status-badge.component';
import { DriftSeverityBadgeComponent } from '../shared/drift-severity-badge.component';
import type { DriftItemJobStatus, DriftReportFilters } from '../state/drift-report-state.service';
import { DriftReportStateService, EMPTY_DRIFT_FILTERS } from '../state/drift-report-state.service';

const SEVERITY_OPTIONS: readonly DriftSeverity[] = ['High', 'Medium', 'Low'];
const DRIFT_TYPE_OPTIONS: readonly DriftType[] = [
  'MissingDesiredEntity',
  'ExtraObservedEntity',
  'AccessVlanMismatch',
  'UnexpectedTrunkConfig',
  'UnexpectedNeighbour',
  'UnknownTopologyMapping',
];

/** Parses the severity/driftType/actionable query params into DriftReportFilters. Unknown/invalid
 * values (e.g. a hand-edited URL) fall back to "no filter" rather than erroring. */
export function driftFiltersFromQueryParamMap(query: ParamMap): DriftReportFilters {
  const severity = query.get('severity');
  const driftType = query.get('driftType');
  const actionable = query.get('actionable');
  return {
    severity:
      severity && (SEVERITY_OPTIONS as readonly string[]).includes(severity)
        ? (severity as DriftSeverity)
        : null,
    driftType:
      driftType && (DRIFT_TYPE_OPTIONS as readonly string[]).includes(driftType)
        ? (driftType as DriftType)
        : null,
    actionable: actionable === 'true' ? true : actionable === 'false' ? false : null,
  };
}

/** The inverse of driftFiltersFromQueryParamMap — `null` removes a param when merged via the router's
 * `queryParamsHandling: 'merge'`. */
export function driftFiltersToQueryParams(
  filters: DriftReportFilters,
): Record<string, string | null> {
  return {
    severity: filters.severity,
    driftType: filters.driftType,
    actionable: filters.actionable === null ? null : String(filters.actionable),
  };
}

@Component({
  selector: 'app-drift-reports-list',
  standalone: true,
  imports: [DatePipe, RouterLink, DriftSeverityBadgeComponent, JobStatusBadgeComponent],
  styleUrl: './drift-reports-list.component.scss',
  template: `
    <section class="drift-list" role="main">
      <header class="drift-list__header">
        <h1>Drift report — rack {{ state.rackId() }}</h1>

        <div class="drift-list__filters">
          <div class="drift-list__filter">
            <label for="drift-filter-severity">Severity</label>
            <select
              id="drift-filter-severity"
              [value]="state.filters().severity ?? ''"
              (change)="onSeverityChange($event)"
            >
              <option value="">Any severity</option>
              @for (option of severityOptions; track option) {
                <option [value]="option">{{ option }}</option>
              }
            </select>
          </div>

          <div class="drift-list__filter">
            <label for="drift-filter-type">Drift type</label>
            <select
              id="drift-filter-type"
              [value]="state.filters().driftType ?? ''"
              (change)="onDriftTypeChange($event)"
            >
              <option value="">Any type</option>
              @for (option of driftTypeOptions; track option) {
                <option [value]="option">{{ option }}</option>
              }
            </select>
          </div>

          <div class="drift-list__filter">
            <label for="drift-filter-actionable">Actionable</label>
            <select
              id="drift-filter-actionable"
              [value]="actionableSelectValue()"
              (change)="onActionableChange($event)"
            >
              <option value="">Any</option>
              <option value="true">Actionable only</option>
              <option value="false">Not actionable</option>
            </select>
          </div>
        </div>
      </header>

      @if (state.loading() && state.items().length === 0) {
        <p role="status">Loading drift report…</p>
      } @else if (state.error() === 'notFound') {
        <p role="status">This rack has no drift report yet.</p>
      } @else if (state.error()) {
        <p role="alert">Something went wrong loading drift for this rack. Try again shortly.</p>
      } @else if (state.items().length === 0) {
        <p role="status">No drift items match the current filters.</p>
      } @else {
        <table class="drift-list__table">
          <thead>
            <tr>
              <th scope="col">Subject</th>
              <th scope="col">Drift type</th>
              <th scope="col">Severity</th>
              <th scope="col">Detected</th>
              <th scope="col">Actionable</th>
              <th scope="col">Status</th>
            </tr>
          </thead>
          <tbody>
            @for (item of state.items(); track item.driftItemId) {
              <tr>
                <td>
                  <a
                    class="drift-list__subject-link"
                    [routerLink]="['/racks', state.rackId(), 'drift', 'items', item.driftItemId]"
                  >
                    {{ item.subjectType }}:
                    <span class="drift-list__identifier">{{ item.subjectKey }}</span>
                  </a>
                </td>
                <td>{{ item.driftType }}</td>
                <td><app-drift-severity-badge [severity]="item.severity" /></td>
                <td>{{ item.createdAt | date: 'medium' }}</td>
                <td>{{ item.actionable ? 'Yes' : 'No' }}</td>
                <td>
                  @if (jobFor(item); as job) {
                    <a
                      class="drift-list__status-link"
                      [routerLink]="['/racks', state.rackId(), 'drift', 'jobs', job.jobId]"
                    >
                      <app-job-status-badge [status]="job.status" />
                    </a>
                  } @else {
                    —
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>

        @if (state.nextCursor()) {
          <button
            type="button"
            class="drift-list__load-more"
            [disabled]="state.loadingMore()"
            (click)="state.loadMore()"
          >
            {{ state.loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        }
      }
    </section>
  `,
})
export class DriftReportsListComponent {
  protected readonly state = inject(DriftReportStateService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly severityOptions = SEVERITY_OPTIONS;
  protected readonly driftTypeOptions = DRIFT_TYPE_OPTIONS;

  constructor() {
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        const rackId = params.get('rackId');
        if (rackId) {
          this.state.loadRackDrift(rackId, driftFiltersFromQueryParamMap(query));
        }
      });
  }

  protected jobFor(item: DriftItemDto): DriftItemJobStatus | null {
    return this.state.jobStatusByDriftItemId().get(item.driftItemId) ?? null;
  }

  protected actionableSelectValue(): string {
    const actionable = this.state.filters().actionable;
    return actionable === null ? '' : String(actionable);
  }

  protected onSeverityChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.applyFilters({ severity: (value || null) as DriftSeverity | null });
  }

  protected onDriftTypeChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.applyFilters({ driftType: (value || null) as DriftType | null });
  }

  protected onActionableChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.applyFilters({ actionable: value === '' ? null : value === 'true' });
  }

  private applyFilters(partial: Partial<DriftReportFilters>): void {
    const next: DriftReportFilters = {
      ...(this.state.filters() ?? EMPTY_DRIFT_FILTERS),
      ...partial,
    };
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: driftFiltersToQueryParams(next),
      queryParamsHandling: 'merge',
    });
  }
}
