// Shared, data-driven badge for the confirmed/ambiguous/unmapped mapping-state vocabulary, the
// High/Medium/Low confidence bands (AC4), and (story #67) the drift severity vocabulary. Reused by the
// graph, legend, details panel and drift views so the same labels/colours never drift between
// components. Token-driven only — never a hard-coded colour.
//
// Severity kinds are deliberately namespaced ('severity-high'/'severity-medium'/'severity-low') rather
// than reusing the bare 'High'/'Medium'/'Low' confidence-band kinds: confidence High renders green
// ("good"), but drift severity High is the opposite polarity ("bad") — see ADR 0033. Prefer
// drift/shared/drift-severity-badge.component.ts over binding `kind` to a severity string directly.
import { Component, computed, input } from '@angular/core';
import type { ConfidenceBand, MappingState } from '../../topology/model/topology-graph-model';

export type SeverityBadgeKind = 'severity-high' | 'severity-medium' | 'severity-low';
export type BadgeKind = MappingState | ConfidenceBand | SeverityBadgeKind;

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
};

// NFR5: severity is never colour-only — each severity kind also carries a glyph. Reuses the exact
// glyphs topology's edge badges already use for the same-coloured states (ambiguous/unmapped), so the
// same icon always means the same thing across the app.
const ICONS: Partial<Record<BadgeKind, string>> = {
  'severity-high': '✕',
  'severity-medium': '▲',
  'severity-low': 'ℹ',
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
      .status-badge {
        display: inline-flex;
        align-items: center;
        gap: 0.25rem;
        border-radius: 999px;
        padding: 0.125rem 0.625rem;
        font-size: 0.75rem;
        font-weight: 600;
        line-height: 1.4;
        white-space: nowrap;
      }

      .status-badge__icon {
        font-size: 0.75em;
      }

      .status-badge--confirmed,
      .status-badge--high {
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
      .status-badge--severity-high {
        background: var(--cds-error-bg);
        color: var(--cds-error-fg);
      }

      .status-badge--severity-low {
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
  protected readonly icon = computed(() => ICONS[this.kind()]);

  protected readonly cssClass = computed(() => `status-badge--${this.kind().toLowerCase()}`);
}
