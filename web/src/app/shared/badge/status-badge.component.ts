// Shared, data-driven badge for the confirmed/ambiguous/unmapped mapping-state vocabulary and the
// High/Medium/Low confidence bands (AC4). Reused by the graph, legend and details panel so the same
// five labels/colours never drift between components. Token-driven only — never a hard-coded colour.
import { Component, computed, input } from '@angular/core';
import type { ConfidenceBand, MappingState } from '../../topology/model/topology-graph-model';

export type BadgeKind = MappingState | ConfidenceBand;

const LABELS: Record<BadgeKind, string> = {
  confirmed: 'Confirmed',
  ambiguous: 'Ambiguous',
  unmapped: 'Unmapped',
  High: 'High confidence',
  Medium: 'Medium confidence',
  Low: 'Low confidence',
};

@Component({
  selector: 'app-status-badge',
  standalone: true,
  template: ` <span class="status-badge" [class]="cssClass()">{{ label() }}</span> `,
  styles: [
    `
      .status-badge {
        display: inline-flex;
        align-items: center;
        border-radius: 999px;
        padding: 0.125rem 0.625rem;
        font-size: 0.75rem;
        font-weight: 600;
        line-height: 1.4;
        white-space: nowrap;
      }

      .status-badge--confirmed,
      .status-badge--high {
        background: var(--color-status-confirmed-bg);
        color: var(--color-status-confirmed);
      }

      .status-badge--ambiguous,
      .status-badge--medium {
        background: var(--color-status-ambiguous-bg);
        color: var(--color-status-ambiguous);
      }

      .status-badge--unmapped,
      .status-badge--low {
        background: var(--color-status-unmapped-bg);
        color: var(--color-status-unmapped);
      }
    `,
  ],
})
export class StatusBadgeComponent {
  readonly kind = input.required<BadgeKind>();
  /** Overrides the default label (e.g. to append a confidence percentage). */
  readonly labelText = input<string | undefined>(undefined);
  readonly label = computed(() => this.labelText() ?? LABELS[this.kind()]);

  protected readonly cssClass = computed(() => `status-badge--${this.kind().toLowerCase()}`);
}
