// Routed at /racks/:rackId/drift/items/:driftItemId — a stable, shareable URL (linked from the list's
// subject column and, once story #67 step 5 lands, from the apply job's Pending/Running state).
// Renders a single DriftItemDto: why (summary), drift type + severity badges, subject, before/after
// (expectedValue -> actualValue), and the free-form `details` bag as labelled key/value pairs — never
// a raw dictionary dump of anything not present on the DTO. Hosts the Apply action slot wired by step 4
// (ApplyActionComponent).
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ApplyActionComponent } from '../apply/apply-action.component';
import type { DriftItemDto } from '../model/drift-contracts';
import { DriftReportService } from '../services/drift-report.service';
import { DriftSeverityBadgeComponent } from '../shared/drift-severity-badge.component';

export type DriftItemLoadError =
  'unauthorized' | 'forbidden' | 'notFound' | 'unprocessable' | 'rateLimited' | 'error';

interface DetailEntry {
  key: string;
  value: string;
}

@Component({
  selector: 'app-drift-report-details',
  standalone: true,
  imports: [DatePipe, DriftSeverityBadgeComponent, ApplyActionComponent],
  styleUrl: './drift-report-details.component.scss',
  template: `
    <section class="drift-detail" role="main">
      @if (loading()) {
        <p role="status">Loading drift item…</p>
      } @else if (error() === 'notFound') {
        <p role="status">This drift item could not be found.</p>
      } @else if (error()) {
        <p role="alert">Something went wrong loading this drift item. Try again shortly.</p>
      } @else if (item(); as driftItem) {
        <header class="drift-detail__header">
          <h1>{{ driftItem.subjectType }}: {{ driftItem.subjectKey }}</h1>
          <div class="drift-detail__badges">
            <span class="drift-detail__type">{{ driftItem.driftType }}</span>
            <app-drift-severity-badge [severity]="driftItem.severity" />
          </div>
        </header>

        <p class="drift-detail__why">{{ driftItem.why }}</p>
        <p class="drift-detail__detected">Detected {{ driftItem.createdAt | date: 'medium' }}</p>

        <dl class="drift-detail__before-after">
          <dt>Current value</dt>
          <dd>{{ driftItem.actualValue ?? '—' }}</dd>
          <dt>Expected value</dt>
          <dd>{{ driftItem.expectedValue ?? '—' }}</dd>
        </dl>

        <p class="drift-detail__actionable">
          {{
            driftItem.actionable
              ? 'This drift item is actionable.'
              : 'This drift item is not actionable.'
          }}
        </p>

        @if (detailEntries().length > 0) {
          <section class="drift-detail__section">
            <h2>Details</h2>
            <dl class="drift-detail__details">
              @for (entry of detailEntries(); track entry.key) {
                <dt>{{ entry.key }}</dt>
                <dd>{{ entry.value }}</dd>
              }
            </dl>
          </section>
        }

        <section class="drift-detail__apply-slot" aria-label="Apply action">
          <app-apply-action
            [item]="driftItem"
            [rackId]="currentRackId() ?? ''"
            (refreshRequested)="refresh()"
          />
        </section>
      }
    </section>
  `,
})
export class DriftReportDetailsComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly reportService = inject(DriftReportService);

  protected readonly currentRackId = signal<string | null>(null);
  protected readonly currentDriftItemId = signal<string | null>(null);
  protected readonly item = signal<DriftItemDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<DriftItemLoadError | null>(null);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const rackId = params.get('rackId');
      const driftItemId = params.get('driftItemId');
      if (rackId && driftItemId) {
        this.currentRackId.set(rackId);
        this.currentDriftItemId.set(driftItemId);
        this.load(rackId, driftItemId);
      }
    });
  }

  /** Manual refresh affordance for the stale-drift 422 path (step 4): re-fetches the item so a
   * disabled Apply button and updated actionable/why text reflect the latest server state. */
  refresh(): void {
    const rackId = this.currentRackId();
    const driftItemId = this.currentDriftItemId();
    if (rackId && driftItemId) {
      this.load(rackId, driftItemId);
    }
  }

  protected detailEntries(): DetailEntry[] {
    const details = this.item()?.details;
    if (!details || typeof details !== 'object') {
      return [];
    }
    return Object.entries(details as Record<string, unknown>).map(([key, value]) => ({
      key,
      value: value === null || value === undefined ? '—' : String(value),
    }));
  }

  private load(rackId: string, driftItemId: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.reportService.getItemById(rackId, driftItemId).subscribe((result) => {
      this.loading.set(false);
      if (result.kind !== 'ok') {
        this.error.set(result.kind);
        this.item.set(null);
        return;
      }
      this.item.set(result.value);
    });
  }
}
