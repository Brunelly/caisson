// Shared, presentational live-connection status indicator (Story #119 Task #127, per the
// LiveConnectionStatusBar design). Two variants: `badge` (compact pill, always visible in the top bar)
// and `banner` (the degraded-connection notice previously duplicated inline in
// topology-page.component.ts and apply-action.component.ts — see Task #127 step 5, which replaces both
// with this component, unchanged text/behaviour). Never colour-only (NFR): every state renders a text
// label, colour is a reinforcement, not the only signal.
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ConnectionStatus } from '../../topology/state/topology-state.service';

const STATE_LABEL: Record<ConnectionStatus, string> = {
  connecting: 'Connecting',
  live: 'Live',
  stale: 'Stale',
  disconnected: 'Disconnected',
};

const BANNER_TEXT: Record<ConnectionStatus, string | null> = {
  connecting: null,
  live: null,
  stale: 'Live updates are stale',
  disconnected: 'Live updates disconnected',
};

@Component({
  selector: 'app-live-connection-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './live-connection-status-bar.component.scss',
  template: `
    @if (variant() === 'badge') {
      <span
        class="lcsb-badge"
        [class]="'lcsb-badge--' + status()"
        [attr.role]="status() === 'disconnected' ? 'alert' : 'status'"
      >
        <span class="lcsb-badge__dot" aria-hidden="true"></span>
        <span class="lcsb-badge__label">{{ label() }}</span>
      </span>
    } @else if (bannerText(); as text) {
      <p class="lcsb-banner" [class]="'lcsb-banner--' + status()" role="status">
        {{ text }}
        @if (detail()) {
          — {{ detail() }}
        }
      </p>
    }
  `,
})
export class LiveConnectionStatusBarComponent {
  readonly status = input.required<ConnectionStatus>();
  readonly variant = input<'badge' | 'banner'>('banner');
  /** Trailing context appended after the base banner text (e.g. "showing the last known snapshot."),
   * since the exact trailing copy differs between the topology and drift-apply call sites. */
  readonly detail = input<string | null>(null);

  protected label(): string {
    return STATE_LABEL[this.status()];
  }

  protected bannerText(): string | null {
    return BANNER_TEXT[this.status()];
  }
}
