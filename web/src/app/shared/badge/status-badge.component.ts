// Shared, data-driven badge for the confirmed/ambiguous/unmapped mapping-state vocabulary, the
// High/Medium/Low confidence bands (AC4), the drift severity vocabulary (story #67), and (story #67
// step 5 / #120) the drift-apply job-status vocabulary. Reused by the graph, legend, details panel and
// drift views so the same labels/colours never drift between components. Token-driven only — never a
// hard-coded colour.
//
// Severity kinds are deliberately namespaced ('severity-high'/'severity-medium'/'severity-low') rather
// than reusing the bare 'High'/'Medium'/'Low' confidence-band kinds: confidence High renders green
// ("good"), but drift severity High is the opposite polarity ("bad") — see ADR 0033. Prefer
// drift/shared/drift-severity-badge.component.ts over binding `kind` to a severity string directly.
//
// Job-status kinds ('job-pending'/'job-success'/'job-error') are likewise namespaced rather than reused
// from mapping-state/confidence: pending/success/failure is a different axis entirely from mapping
// confidence or drift severity (a job can be "success" while its subject is "unmapped"). Prefer
// drift/apply/job-status-badge.component.ts over binding `kind` to a job-status string directly.
import { Component, computed, input } from '@angular/core';
import type { ConfidenceBand, MappingState } from '../../topology/model/topology-graph-model';

export type SeverityBadgeKind = 'severity-high' | 'severity-medium' | 'severity-low';
export type JobStatusBadgeKind = 'job-pending' | 'job-success' | 'job-error';
export type BadgeKind = MappingState | ConfidenceBand | SeverityBadgeKind | JobStatusBadgeKind;

const LABELS: Record<BadgeKind, string> = {
  confirmed: 'Confirmed',
  ambiguous: 'Ambiguous',
  unmapped: 'Unmapped',
  High: 'High confidence',
  Medium: 'Medium confidence',
  Low: 'Low confidence',
  'severity-high': 'High severity',
  'severity-medium': 'Medium severity',
  'severity-low': 'Low severity',
  // Always overridden via `labelText` at the job-status-badge call site (its own 8-value
  // DriftApplyJobStatus label set) — these three exist only so LABELS stays a total map.
  'job-pending': 'Pending',
  'job-success': 'Completed',
  'job-error': 'Failed',
};

// NFR5: status is never colour-only — every kind also carries a glyph. Reuses the exact glyphs
// topology's edge badges already use for the same-coloured mapping/severity states, so the same icon
// always means the same thing across the app; confirmed/High reuse job-status-badge's existing
// success glyph for the same reason.
const ICONS: Partial<Record<BadgeKind, string>> = {
  confirmed: '✓',
  ambiguous: '▲',
  unmapped: '✕',
  High: '✓',
  Medium: '▲',
  Low: '✕',
  'severity-high': '✕',
  'severity-medium': '▲',
  'severity-low': 'ℹ',
  'job-pending': '…',
  'job-success': '✓',
  'job-error': '✕',
};

@Component({
  selector: 'app-status-badge',
  standalone: true,
  template: `
    <span class="status-badge" [class]="cssClass()">
      @if (icon(); as ic) {
        <span class="status-badge__icon" aria-hidden="true">{{ ic }}</span>
      }
      {{ label() }}
    </span>
  `,
  styles: [
    `
      // Story #122 (Task #137): DS pill treatment — full-round radius, --cds-fs-xs, token spacing.
      // Purely a token/shape refresh; the kind -> colour/glyph/label maps below are untouched, so
      // every badge (mapping-state, confidence, drift severity, job status) keeps its exact meaning.
      .status-badge {
        display: inline-flex;
        align-items: center;
        gap: var(--cds-sp-1);
        border-radius: var(--cds-radius-full);
        padding: var(--cds-sp-0-5) var(--cds-sp-2-5);
        font-size: var(--cds-fs-xs);
        font-weight: 600;
        line-height: 1.4;
        white-space: nowrap;
        // Task #129/#130: some labels fold in a technical value (e.g. a confidence percentage, see
        // topology-details-panel's candidate list) — tabular numerals only (not the full identifier
        // monospace stack, which would also affect the surrounding human-readable label text).
        font-variant-numeric: tabular-nums;
        font-feature-settings: 'tnum' 1;
      }

      .status-badge__icon {
        font-size: 0.75em;
      }

      .status-badge--confirmed,
      .status-badge--high,
      .status-badge--job-success {
        background: var(--cds-success-bg);
        color: var(--cds-success-fg);
      }

      .status-badge--ambiguous,
      .status-badge--medium,
      .status-badge--severity-medium {
        background: var(--cds-warning-bg);
        color: var(--cds-warning-fg);
      }

      .status-badge--unmapped,
      .status-badge--low,
      .status-badge--severity-high,
      .status-badge--job-error {
        background: var(--cds-error-bg);
        color: var(--cds-error-fg);
      }

      .status-badge--severity-low,
      .status-badge--job-pending {
        background: var(--cds-surface-elevated);
        color: var(--cds-text-secondary);
      }
    `,
  ],
})
export class StatusBadgeComponent {
  readonly kind = input.required<BadgeKind>();
  /** Overrides the default label (e.g. to append a confidence percentage). */
  readonly labelText = input<string | undefined>(undefined);
  readonly label = computed(() => this.labelText() ?? LABELS[this.kind()]);
  /** Overrides the default per-kind glyph — job-status-badge uses this to keep its own more granular
   * per-DriftApplyJobStatus icon (e.g. a distinct "in progress" glyph) even though several statuses
   * share one job-pending/-success/-error `kind` bucket for colour/label purposes. */
  readonly iconOverride = input<string | undefined>(undefined);
  protected readonly icon = computed(() => this.iconOverride() ?? ICONS[this.kind()]);

  protected readonly cssClass = computed(() => `status-badge--${this.kind().toLowerCase()}`);
}
