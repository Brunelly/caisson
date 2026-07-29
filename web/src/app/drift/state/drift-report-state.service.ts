// Single injectable page-state service (signals, deliberately no NgRx — ADR 0015, mirroring
// TopologyStateService) owning the drift-reports-list page's state: the resolved current report, its
// (server-filtered, keyset-paginated) items, and the derived apply-status join (ADR 0033) — DriftItemDto
// carries no status field, so the status column is built by separately fetching the drift-apply job
// list and indexing the newest job per driftItemId.
import { Injectable, inject, signal } from '@angular/core';
import type {
  DriftApplyJobStatus,
  DriftItemDto,
  DriftReportSummaryDto,
  DriftSeverity,
  DriftType,
} from '../model/drift-contracts';

export interface DriftItemJobStatus {
  jobId: string;
  status: DriftApplyJobStatus;
}
import { DriftApplyService } from '../services/drift-apply.service';
import { DriftReportService } from '../services/drift-report.service';

export type DriftLoadError =
  'unauthorized' | 'forbidden' | 'notFound' | 'unprocessable' | 'rateLimited' | 'error';

export interface DriftReportFilters {
  severity: DriftSeverity | null;
  driftType: DriftType | null;
  actionable: boolean | null;
}

export const EMPTY_DRIFT_FILTERS: DriftReportFilters = {
  severity: null,
  driftType: null,
  actionable: null,
};

const DEFAULT_PAGE_SIZE = 50;

@Injectable({ providedIn: 'root' })
export class DriftReportStateService {
  private readonly reportService = inject(DriftReportService);
  private readonly applyService = inject(DriftApplyService);

  private readonly _rackId = signal<string | null>(null);
  private readonly _report = signal<DriftReportSummaryDto | null>(null);
  private readonly _items = signal<DriftItemDto[]>([]);
  private readonly _nextCursor = signal<string | null>(null);
  private readonly _filters = signal<DriftReportFilters>(EMPTY_DRIFT_FILTERS);
  private readonly _jobStatusByDriftItemId = signal<ReadonlyMap<string, DriftItemJobStatus>>(
    new Map(),
  );
  private readonly _loading = signal(false);
  private readonly _loadingMore = signal(false);
  private readonly _error = signal<DriftLoadError | null>(null);

  readonly rackId = this._rackId.asReadonly();
  readonly report = this._report.asReadonly();
  readonly items = this._items.asReadonly();
  readonly nextCursor = this._nextCursor.asReadonly();
  readonly filters = this._filters.asReadonly();
  readonly jobStatusByDriftItemId = this._jobStatusByDriftItemId.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly loadingMore = this._loadingMore.asReadonly();
  readonly error = this._error.asReadonly();

  /** Resolves the rack's current report, then drives item loading through getReportById(reportId,
   * filters) — filtering is always a server re-fetch, never a client-side array filter, so keyset
   * pagination stays correct (ADR 0033). Call again (with new filters) on every filter/navigation
   * change; the query-param <-> filters round trip lives in the list component. */
  loadRackDrift(rackId: string, filters: DriftReportFilters = EMPTY_DRIFT_FILTERS): void {
    const isRackChange = this._rackId() !== null && this._rackId() !== rackId;
    this._rackId.set(rackId);
    this._filters.set(filters);
    this._loading.set(true);
    this._error.set(null);
    if (isRackChange) {
      this._report.set(null);
      this._items.set([]);
      this._nextCursor.set(null);
      this._jobStatusByDriftItemId.set(new Map());
    }

    this.reportService.getLatest(rackId).subscribe((latest) => {
      if (latest.kind !== 'ok') {
        this._loading.set(false);
        this._error.set(latest.kind);
        return;
      }
      this.fetchReport(rackId, latest.value.report.driftReportId, filters);
    });
  }

  /** Re-drives the current report's item list with new filters (server-side, AC2). */
  setFilters(filters: DriftReportFilters): void {
    const rackId = this._rackId();
    const reportId = this._report()?.driftReportId;
    if (!rackId || !reportId) {
      return;
    }
    this._filters.set(filters);
    this.fetchReport(rackId, reportId, filters);
  }

  loadMore(): void {
    const rackId = this._rackId();
    const reportId = this._report()?.driftReportId;
    const cursor = this._nextCursor();
    if (!rackId || !reportId || !cursor || this._loadingMore()) {
      return;
    }
    this._loadingMore.set(true);
    this.reportService
      .getReportById(rackId, reportId, { ...this.toApiFilters(this._filters()), cursor })
      .subscribe((result) => {
        this._loadingMore.set(false);
        if (result.kind !== 'ok') {
          this._error.set(result.kind);
          return;
        }
        this._items.set([...this._items(), ...result.value.items.items]);
        this._nextCursor.set(result.value.items.nextCursor);
      });
  }

  /** Re-fetches the drift-apply job list and refreshes the derived status join — called after a
   * successful apply submission so the list's status column reflects the new job without a full
   * page reload. */
  refreshJobStatuses(): void {
    const rackId = this._rackId();
    if (rackId) {
      this.loadJobStatuses(rackId);
    }
  }

  private fetchReport(rackId: string, reportId: string, filters: DriftReportFilters): void {
    this._loading.set(true);
    this.reportService
      .getReportById(rackId, reportId, {
        ...this.toApiFilters(filters),
        pageSize: DEFAULT_PAGE_SIZE,
      })
      .subscribe((result) => {
        this._loading.set(false);
        if (result.kind !== 'ok') {
          this._error.set(result.kind);
          return;
        }
        this._report.set(result.value.report);
        this._items.set(result.value.items.items);
        this._nextCursor.set(result.value.items.nextCursor);
        this.loadJobStatuses(rackId);
      });
  }

  private loadJobStatuses(rackId: string): void {
    this.applyService.getJobs(rackId, { pageSize: 200 }).subscribe((result) => {
      if (result.kind !== 'ok') {
        // Non-fatal: the item list itself loaded fine, only the derived status column is unavailable.
        return;
      }
      const byDriftItemId = new Map<string, DriftItemJobStatus>();
      const newestFirst = [...result.value.items].sort(
        (a, b) => Date.parse(b.requestedAt) - Date.parse(a.requestedAt),
      );
      for (const job of newestFirst) {
        if (!byDriftItemId.has(job.driftItemId)) {
          byDriftItemId.set(job.driftItemId, { jobId: job.jobId, status: job.status });
        }
      }
      this._jobStatusByDriftItemId.set(byDriftItemId);
    });
  }

  private toApiFilters(filters: DriftReportFilters): {
    severity?: DriftSeverity;
    driftType?: DriftType;
    actionable?: boolean;
  } {
    return {
      severity: filters.severity ?? undefined,
      driftType: filters.driftType ?? undefined,
      actionable: filters.actionable ?? undefined,
    };
  }
}
