// Static, data-driven legend explaining the graph's visual states (AC4): confirmed/ambiguous/unmapped
// mapping states plus the High/Medium/Low confidence bands, keyed off the same vocabulary/badge the
// graph and details panel use so the three never drift.
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { BadgeKind } from '../../shared/badge/status-badge.component';

interface LegendEntry {
  kind: BadgeKind;
  description: string;
}

const MAPPING_STATE_ENTRIES: LegendEntry[] = [
  { kind: 'confirmed', description: 'Exactly one candidate mapping — solid edge.' },
  {
    kind: 'ambiguous',
    description: 'Multiple candidates; only the top one is drawn here — see details for the rest.',
  },
  { kind: 'unmapped', description: 'No candidate mapping was found.' },
];

const CONFIDENCE_BAND_ENTRIES: LegendEntry[] = [
  { kind: 'High', description: 'Confidence ≥ 0.8' },
  { kind: 'Medium', description: 'Confidence 0.5 – 0.79' },
  { kind: 'Low', description: 'Confidence < 0.5' },
];

@Component({
  selector: 'app-topology-legend',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadgeComponent],
  template: `
    <section class="topology-legend" aria-label="Topology graph legend">
      <h2 class="topology-legend__heading">Legend</h2>
      <ul class="topology-legend__list">
        @for (entry of mappingStates; track entry.kind) {
          <li>
            <app-status-badge [kind]="entry.kind" />
            <span>{{ entry.description }}</span>
          </li>
        }
      </ul>
      <ul class="topology-legend__list">
        @for (entry of confidenceBands; track entry.kind) {
          <li>
            <app-status-badge [kind]="entry.kind" />
            <span>{{ entry.description }}</span>
          </li>
        }
      </ul>
    </section>
  `,
  styles: [
    `
      @use '../../shared/styles/cds-mixins';

      // Task #133/#134: floated over the canvas as a frosted-glass card (matching the design mock's
      // \`.legend\`) instead of the old flush bottom strip — margin keeps it clear of the canvas edge,
      // and topology-page.component.scss positions it absolutely within \`.topology-shell\`.
      .topology-legend {
        @include cds-mixins.cds-glass-surface;
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
        padding: 0.75rem 1rem;
        border-radius: var(--cds-radius-lg);
        box-shadow: var(--cds-shadow-lg);
        font-size: 0.8125rem;
      }

      .topology-legend__heading {
        margin: 0;
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--cds-text-secondary);
      }

      .topology-legend__list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-wrap: wrap;
        gap: 0.75rem 1.25rem;
      }

      .topology-legend__list li {
        display: flex;
        align-items: center;
        gap: 0.375rem;
      }
    `,
  ],
})
export class TopologyLegendComponent {
  protected readonly mappingStates = MAPPING_STATE_ENTRIES;
  protected readonly confidenceBands = CONFIDENCE_BAND_ENTRIES;
}
