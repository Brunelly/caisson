// PR status panel (story #173, Task #215): renders the rack's live GitHub PR state, checks rollup, and the
// apply gate banner, with SignalR-driven live updates + REST fallback polling (both via TopologySignalRService)
// and a Refresh that re-reads the PERSISTED status only (never forcing a GitHub call — respects NFR1). Standalone,
// OnPush, --cds-* tokens only; fixed row heights keep live updates patching in place with no layout shift.
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { LiveConnectionStatusBarComponent } from '../../shared/connection-status/live-connection-status-bar.component';
import { ToastService } from '../../shared/toast/toast.service';
import { TopologyStateService } from '../../topology/state/topology-state.service';
import { TopologySignalRService } from '../../topology/live/topology-signalr.service';
import { PrStatusBadgeComponent } from './pr-status-badge.component';
import { parseChecksRollup } from './pr-status-contracts';
import { PrStatusService } from './pr-status.service';
import { PrStatusStateService } from './pr-status-state.service';

@Component({
  selector: 'app-pull-request-status-panel',
  standalone: true,
  imports: [PrStatusBadgeComponent, LiveConnectionStatusBarComponent],
  styleUrl: './pull-request-status-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pr-panel" aria-labelledby="pr-panel-title">
      <header class="pr-panel__head">
        @if (status()?.pullRequestNumber; as number) {
          <code class="pr-panel__number">PR #{{ number }}</code>
        } @else {
          <code class="pr-panel__number">Pull request</code>
        }
        <h2 class="pr-panel__title" id="pr-panel-title">
          @if (status()?.pullRequestUrl; as url) {
            <a
              class="pr-panel__title-link"
              [href]="url"
              target="_blank"
              rel="noopener noreferrer"
              [attr.aria-label]="
                'Open pull request #' +
                status()?.pullRequestNumber +
                ' on the provider (opens in a new tab)'
              "
            >
              Pull request #{{ status()?.pullRequestNumber }}
              <svg
                class="pr-panel__external-icon"
                width="15"
                height="15"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="1.75"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path
                  d="M15 3h6v6M10 14 21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"
                />
              </svg>
            </a>
          } @else {
            <span>No pull request linked</span>
          }
        </h2>
        @if (status()?.hasPullRequest) {
          <app-pr-status-badge
            [state]="status()!.state"
            [checksConclusion]="status()!.checksConclusion"
          />
        }
      </header>

      <!-- Polite live region: announces the status/checks change without re-reading the whole check list. -->
      <p class="pr-panel__sr-only" role="status" aria-live="polite">{{ announcement() }}</p>

      <div class="pr-panel__body">
        @if (rollup(); as r) {
          <h3 class="pr-panel__label">
            Checks · {{ r.checks.length }}{{ r.truncated ? '+ (truncated)' : '' }}
          </h3>
          <div class="pr-panel__checks">
            @for (check of r.checks; track check.name) {
              <div class="pr-panel__check">
                <span
                  class="pr-panel__glyph"
                  [class.pr-panel__glyph--ok]="isOk(check.conclusion)"
                  [class.pr-panel__glyph--fail]="isFail(check.conclusion)"
                  [class.pr-panel__glyph--running]="isRunning(check.status, check.conclusion)"
                  [class.pr-panel__glyph--unknown]="isUnknown(check)"
                  aria-hidden="true"
                  >{{ glyphFor(check) }}</span
                >
                <span class="pr-panel__check-name">{{ check.name }}</span>
                <code class="pr-panel__check-duration">{{ durationFor(check) }}</code>
                @if (isFail(check.conclusion)) {
                  <div class="pr-panel__failure">{{ check.name }}: {{ check.conclusion }}</div>
                }
              </div>
            }
          </div>
        }

        <div
          class="pr-panel__gate"
          [class.pr-panel__gate--info]="!canApply()"
          [class.pr-panel__gate--success]="canApply()"
        >
          <svg
            width="20"
            height="20"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            @if (canApply()) {
              <path d="M20 6 9 17l-5-5" />
            } @else {
              <rect x="3" y="11" width="18" height="10" rx="2" />
              <path d="M7 11V7a5 5 0 0 1 10 0v4" />
            }
          </svg>
          <div>
            <strong class="pr-panel__gate-title">{{ gateTitle() }}</strong>
            <span class="pr-panel__gate-text">{{ gateText() }}</span>
          </div>
        </div>
      </div>

      <app-live-connection-status-bar
        variant="banner"
        [status]="connectionStatus()"
        detail="showing the last known pull request status."
      />

      <footer class="pr-panel__foot">
        <span class="pr-panel__last-checked">
          Last checked
          @if (status()?.lastChecked; as lastChecked) {
            <time [attr.datetime]="lastChecked">{{ relative(lastChecked) }}</time>
          } @else {
            never
          }
        </span>
        <button
          type="button"
          class="pr-panel__refresh"
          [disabled]="refreshing()"
          (click)="onRefresh()"
        >
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M21 12a9 9 0 1 1-2.64-6.36L21 8M21 3v5h-5" />
          </svg>
          {{ refreshing() ? 'Refreshing…' : 'Refresh' }}
        </button>
      </footer>
    </section>
  `,
})
export class PullRequestStatusPanelComponent {
  readonly rackId = input.required<string>();

  private readonly prStatusState = inject(PrStatusStateService);
  private readonly prStatus = inject(PrStatusService);
  private readonly signalr = inject(TopologySignalRService);
  private readonly topologyState = inject(TopologyStateService);
  private readonly toast = inject(ToastService);

  protected readonly refreshing = signal(false);

  protected readonly status = computed(() => this.prStatusState.statusFor(this.rackId()));
  protected readonly rollup = computed(() => parseChecksRollup(this.status()?.checksSummary));
  protected readonly canApply = computed(() => this.status()?.canApply ?? false);
  protected readonly connectionStatus = computed(() => this.topologyState.connectionStatus());

  protected readonly announcement = computed(() => {
    const s = this.status();
    if (!s?.hasPullRequest) {
      return 'No pull request is linked to this rack.';
    }
    return `Pull request #${s.pullRequestNumber} is ${s.state}. Checks: ${s.checksConclusion}.`;
  });

  protected readonly gateTitle = computed(() =>
    this.canApply() ? 'Ready to apply' : 'Apply is blocked',
  );

  protected readonly gateText = computed(() => {
    const s = this.status();
    if (!s?.hasPullRequest) {
      return 'A pull request must be created first.';
    }
    return this.canApply()
      ? `Pull request #${s.pullRequestNumber} is merged; this desired state can be applied.`
      : `This desired state can be applied after pull request #${s.pullRequestNumber} is merged.`;
  });

  constructor() {
    // Register the rack for live updates + REST fallback polling and fetch the current status once.
    effect(() => {
      const rackId = this.rackId();
      if (rackId) {
        this.signalr.trackPrStatus(rackId);
      }
    });
  }

  /** Re-reads the PERSISTED status via the API only — never forces a GitHub call (NFR1). */
  protected onRefresh(): void {
    const rackId = this.rackId();
    if (!rackId || this.refreshing()) {
      return;
    }
    this.refreshing.set(true);
    this.prStatus.getStatus(rackId).subscribe((result) => {
      this.refreshing.set(false);
      if (result.kind === 'ok') {
        this.prStatusState.applyPolledStatus(rackId, result.value);
      } else if (result.kind !== 'notFound') {
        this.toast.error('Could not refresh the pull request status.');
      }
    });
  }

  protected isOk(conclusion: string): boolean {
    return conclusion === 'Success' || conclusion === 'Neutral' || conclusion === 'Skipped';
  }

  protected isFail(conclusion: string): boolean {
    return (
      conclusion === 'Failure' ||
      conclusion === 'TimedOut' ||
      conclusion === 'Cancelled' ||
      conclusion === 'ActionRequired'
    );
  }

  protected isRunning(status: string, conclusion: string): boolean {
    return conclusion === 'Pending' || status !== 'completed';
  }

  protected isUnknown(check: { status: string; conclusion: string }): boolean {
    return (
      !this.isOk(check.conclusion) &&
      !this.isFail(check.conclusion) &&
      !this.isRunning(check.status, check.conclusion)
    );
  }

  protected glyphFor(check: { status: string; conclusion: string }): string {
    if (this.isFail(check.conclusion)) {
      return '×';
    }
    if (this.isOk(check.conclusion)) {
      return '✓';
    }
    if (this.isRunning(check.status, check.conclusion)) {
      return '…';
    }
    return '?';
  }

  protected durationFor(check: { started?: string; completed?: string }): string {
    if (!check.started || !check.completed) {
      return '—';
    }
    const ms = Date.parse(check.completed) - Date.parse(check.started);
    if (!Number.isFinite(ms) || ms < 0) {
      return '—';
    }
    const seconds = Math.round(ms / 1000);
    const minutes = Math.floor(seconds / 60);
    return minutes > 0 ? `${minutes}m ${seconds % 60}s` : `${seconds}s`;
  }

  protected relative(iso: string): string {
    const then = Date.parse(iso);
    if (!Number.isFinite(then)) {
      return iso;
    }
    const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
    if (seconds < 60) {
      return `${seconds} second${seconds === 1 ? '' : 's'} ago`;
    }
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) {
      return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
    }
    const hours = Math.floor(minutes / 60);
    return `${hours} hour${hours === 1 ? '' : 's'} ago`;
  }
}
