// PR status pill (story #173, Task #215). A small standalone badge that renders text + glyph + an aria-label
// for the PR lifecycle/checks state (never colour alone, NFR5). The shared status-badge is keyed on domain
// "kinds" and has no info/disabled tone, so this bespoke pill maps state→the design's four tones (Open→info,
// Checks-running→warning, Merged→success, Closed→disabled) using only --cds-* tokens, matching the panel design.
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ChecksConclusion, PrState } from './pr-status-contracts';

type PillTone = 'info' | 'warning' | 'success' | 'disabled';

@Component({
  selector: 'app-pr-status-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="pr-badge" [class]="'pr-badge--' + tone()" [attr.aria-label]="ariaLabel()">
      <span class="pr-badge__glyph" aria-hidden="true">{{ glyph() }}</span>
      <span class="pr-badge__label">{{ label() }}</span>
    </span>
  `,
  styles: [
    `
      .pr-badge {
        display: inline-flex;
        align-items: center;
        gap: 0.4375rem;
        padding: 0.25rem 0.625rem;
        border: 1px solid currentColor;
        border-radius: var(--cds-radius-full);
        font-family: var(--cds-font-mono);
        font-size: var(--cds-fs-2xs);
        font-weight: 600;
        white-space: nowrap;
      }
      .pr-badge__glyph {
        display: inline-grid;
        place-items: center;
        width: 0.875rem;
        height: 0.875rem;
      }
      .pr-badge--info {
        background: var(--cds-info-bg);
        color: var(--cds-info-fg);
      }
      .pr-badge--warning {
        background: var(--cds-warning-bg);
        color: var(--cds-warning-fg);
      }
      .pr-badge--success {
        background: var(--cds-success-bg);
        color: var(--cds-success-fg);
      }
      .pr-badge--disabled {
        background: var(--cds-surface-sunken);
        color: var(--cds-text-secondary);
      }
    `,
  ],
})
export class PrStatusBadgeComponent {
  readonly state = input<PrState | null>(null);
  readonly checksConclusion = input<ChecksConclusion>('Unknown');

  protected readonly tone = computed<PillTone>(() => {
    switch (this.state()) {
      case 'Merged':
        return 'success';
      case 'Closed':
        return 'disabled';
      default:
        return this.checksConclusion() === 'Pending' ? 'warning' : 'info';
    }
  });

  protected readonly label = computed(() => {
    switch (this.state()) {
      case 'Merged':
        return 'Merged';
      case 'Closed':
        return 'Closed';
      case 'Open':
        return this.checksConclusion() === 'Pending' ? 'Checks running' : 'Open';
      default:
        return 'No pull request';
    }
  });

  protected readonly glyph = computed(() => {
    switch (this.tone()) {
      case 'success':
        return '✓';
      case 'warning':
        return '…';
      case 'disabled':
        return '—';
      default:
        return '○';
    }
  });

  protected readonly ariaLabel = computed(() => `Pull request status: ${this.label()}`);
}
