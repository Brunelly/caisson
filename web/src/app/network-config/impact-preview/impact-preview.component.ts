// Impact-preview screen (story #171, AC3): renders the server-computed structured summary (grouped VLAN/port
// changes with topology deep links) and the raw unified diff for the current draft against the rack's latest
// ingested baseline. Reachable for every role (Read Only included); it has no mutating controls (previewing
// is TopologyRead-gated), so no @if(canAuthor) guard is needed. The preview goes stale on every draft edit
// (via NetworkIntentStateService.clearValidation) — a fresh preview must be re-run after any change.
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { ImpactChangeBadgeKind } from '../../shared/badge/status-badge.component';
import { ToastService } from '../../shared/toast/toast.service';
import { portNodeId, switchNodeId, vlanNodeId } from '../../topology/model/topology-graph-model';
import type {
  ImpactChange,
  ImpactPreviewIssue,
  ImpactPreviewResponse,
} from '../model/impact-preview-contracts';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import { ImpactPreviewService } from '../services/impact-preview.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { DesiredStateDiffViewerComponent } from './desired-state-diff-viewer.component';

type PreviewState =
  'idle' | 'loading' | 'ready' | 'missingBaseline' | 'validationError' | 'forbidden' | 'error';
type ChangeFilter = 'all' | 'vlan-added' | 'vlan-removed' | 'vlan-modified' | 'port';

@Component({
  selector: 'app-impact-preview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadgeComponent, DesiredStateDiffViewerComponent],
  styleUrl: './impact-preview.component.scss',
  template: `
    <section class="impact-preview" aria-labelledby="impact-preview-title">
      <header class="impact-preview__header">
        <div>
          <h2 id="impact-preview-title" class="impact-preview__title">Impact preview</h2>
          <p class="impact-preview__subtitle">
            Proposed changes against the rack's latest ingested desired-state revision.
          </p>
        </div>
        <button
          type="button"
          class="impact-preview__run"
          (click)="runPreview()"
          [disabled]="previewState() === 'loading' || !state.rackId()"
        >
          {{ hasResult() ? 'Refresh preview' : 'Run preview' }}
        </button>
      </header>

      @if (hasResult() && !state.previewFresh()) {
        <p class="impact-preview__stale" role="status">
          The draft changed since this preview was computed — refresh for up-to-date results.
        </p>
      }

      <p class="impact-preview__live" role="status" aria-live="polite">{{ liveMessage() }}</p>

      @switch (previewState()) {
        @case ('loading') {
          <p class="impact-preview__status" role="status">Computing impact preview…</p>
        }
        @case ('missingBaseline') {
          <div class="impact-preview__notice impact-preview__notice--warning" role="status">
            {{ missingBaselineMessage() }}
          </div>
        }
        @case ('validationError') {
          <div class="impact-preview__notice impact-preview__notice--error" role="alert">
            <p>The candidate YAML is invalid and could not be previewed:</p>
            <ul class="impact-preview__issues">
              @for (issue of issues(); track issue.path + issue.message) {
                <li>
                  <code>{{ issue.path }}</code> — {{ issue.message }}
                  @if (issue.line !== null) {
                    <span class="impact-preview__issue-pos"
                      >(line {{ issue.line }}, column {{ issue.column }})</span
                    >
                  }
                </li>
              }
            </ul>
          </div>
        }
        @case ('forbidden') {
          <div class="impact-preview__notice impact-preview__notice--error" role="alert">
            You do not have access to preview changes for this rack.
          </div>
        }
        @case ('error') {
          <div class="impact-preview__notice impact-preview__notice--error" role="alert">
            The impact preview could not be computed. Please try again.
          </div>
        }
        @case ('ready') {
          @if (response(); as preview) {
            <div class="impact-preview__summary" #summary>
              <span class="impact-preview__summary-label">Impact summary</span>
              @for (chip of chips(); track chip.filter) {
                <button
                  type="button"
                  class="impact-preview__chip"
                  [class]="'impact-preview__chip--' + chip.tone"
                  [class.impact-preview__chip--active]="activeFilter() === chip.filter"
                  [attr.aria-pressed]="activeFilter() === chip.filter"
                  (click)="toggleFilter(chip.filter)"
                >
                  <b>{{ chip.value }}</b> {{ chip.label }}
                </button>
              }
              <span class="impact-preview__devices"
                >Affects {{ affectedDeviceCount() }} devices</span
              >
            </div>

            @if (visibleChanges().length === 0) {
              <p class="impact-preview__empty" role="status">
                No semantic changes in this candidate.
              </p>
            } @else {
              <ul class="impact-preview__changes">
                @for (change of visibleChanges(); track change.changeId) {
                  <li class="impact-preview__change">
                    <app-status-badge [kind]="badgeKind(change)" [labelText]="badgeLabel(change)" />
                    <div class="impact-preview__change-body">
                      <span class="impact-preview__change-summary">{{ change.summary }}</span>
                      <code class="impact-preview__change-id">{{ identifierOf(change) }}</code>
                    </div>
                    @if (change.existsInTopology) {
                      <button
                        type="button"
                        class="impact-preview__deeplink"
                        (click)="openInTopology(change)"
                        [attr.aria-label]="'View ' + change.summary + ' in the topology graph'"
                      >
                        View in topology <span aria-hidden="true">↗</span>
                      </button>
                    } @else {
                      <span
                        class="impact-preview__notfound"
                        aria-label="Not found in the observed topology"
                      >
                        <span aria-hidden="true">⚠</span> Not found in topology
                      </span>
                    }
                  </li>
                }
              </ul>
            }

            <app-desired-state-diff-viewer [diff]="preview.rawUnifiedDiff" />
          }
        }
        @default {
          <p class="impact-preview__status">Run a preview to see the proposed changes.</p>
        }
      }
    </section>
  `,
})
export class ImpactPreviewComponent {
  protected readonly state = inject(NetworkIntentStateService);
  private readonly roundTrip = inject(DesiredStateRoundTripService);
  private readonly impact = inject(ImpactPreviewService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly summaryEl = viewChild<ElementRef<HTMLElement>>('summary');

  protected readonly previewState = signal<PreviewState>('idle');
  protected readonly response = signal<ImpactPreviewResponse | null>(null);
  protected readonly issues = signal<ImpactPreviewIssue[]>([]);
  protected readonly missingBaselineMessage = signal('');
  protected readonly activeFilter = signal<ChangeFilter>('all');

  protected readonly hasResult = computed(
    () => this.response() !== null && this.previewState() === 'ready',
  );

  protected readonly chips = computed(() => {
    const preview = this.response();
    if (!preview) {
      return [];
    }
    const vlanAdded = preview.vlanChanges.filter((c) => c.kind === 'Added').length;
    const vlanRemoved = preview.vlanChanges.filter((c) => c.kind === 'Removed').length;
    const vlanModified = preview.vlanChanges.filter((c) => c.kind === 'Modified').length;
    return [
      { filter: 'vlan-added' as const, tone: 'add', value: `+${vlanAdded}`, label: 'VLANs added' },
      {
        filter: 'vlan-removed' as const,
        tone: 'remove',
        value: `−${vlanRemoved}`,
        label: 'VLANs removed',
      },
      {
        filter: 'vlan-modified' as const,
        tone: 'change',
        value: `~${vlanModified}`,
        label: 'VLANs changed',
      },
      {
        filter: 'port' as const,
        tone: 'change',
        value: `${preview.portChanges.length}`,
        label: 'ports re-assigned',
      },
    ];
  });

  protected readonly affectedDeviceCount = computed(() => {
    const preview = this.response();
    if (!preview) {
      return 0;
    }
    const switches = new Set(
      preview.portChanges.map((c) => c.entityRef.switchStableKey).filter((k): k is string => !!k),
    );
    return switches.size;
  });

  protected readonly visibleChanges = computed<ImpactChange[]>(() => {
    const preview = this.response();
    if (!preview) {
      return [];
    }
    const all = [...preview.vlanChanges, ...preview.portChanges];
    switch (this.activeFilter()) {
      case 'vlan-added':
        return preview.vlanChanges.filter((c) => c.kind === 'Added');
      case 'vlan-removed':
        return preview.vlanChanges.filter((c) => c.kind === 'Removed');
      case 'vlan-modified':
        return preview.vlanChanges.filter((c) => c.kind === 'Modified');
      case 'port':
        return preview.portChanges;
      default:
        return all;
    }
  });

  protected readonly liveMessage = computed(() => {
    switch (this.previewState()) {
      case 'ready': {
        const preview = this.response();
        const count = preview ? preview.vlanChanges.length + preview.portChanges.length : 0;
        return `Impact preview ready: ${count} change${count === 1 ? '' : 's'}${
          preview?.cacheHit ? ' (from cache)' : ''
        }.`;
      }
      case 'loading':
        return 'Computing impact preview.';
      case 'missingBaseline':
        return 'No baseline revision exists for this rack.';
      case 'validationError':
        return 'The candidate YAML is invalid.';
      default:
        return '';
    }
  });

  constructor() {
    // Move focus to the summary once a fresh preview renders (NFR5 keyboard flow).
    effect(() => {
      if (this.previewState() === 'ready') {
        queueMicrotask(() =>
          this.summaryEl()
            ?.nativeElement.querySelector<HTMLButtonElement>('.impact-preview__chip')
            ?.focus(),
        );
      }
    });
  }

  protected runPreview(): void {
    const rackId = this.state.rackId();
    if (!rackId) {
      return;
    }
    this.previewState.set('loading');
    this.issues.set([]);

    this.roundTrip.render(rackId, this.state.renderRequest()).subscribe((rendered) => {
      if (rendered.kind !== 'ok') {
        if (rendered.kind === 'validationError') {
          this.issues.set(rendered.issues);
          this.previewState.set('validationError');
        } else if (rendered.kind === 'forbidden' || rendered.kind === 'unauthorized') {
          this.previewState.set('forbidden');
        } else {
          this.previewState.set('error');
        }
        return;
      }

      this.impact.preview(rackId, rendered.value.yaml).subscribe((result) => {
        switch (result.kind) {
          case 'ok':
            this.response.set(result.value);
            this.state.applyPreview(result.value.candidateId);
            this.activeFilter.set('all');
            this.previewState.set('ready');
            break;
          case 'validationError':
            this.issues.set(result.issues);
            this.previewState.set('validationError');
            break;
          case 'missingBaseline':
            this.missingBaselineMessage.set(result.message);
            this.previewState.set('missingBaseline');
            break;
          case 'forbidden':
          case 'unauthorized':
            this.previewState.set('forbidden');
            break;
          default:
            this.previewState.set('error');
            this.toast.error('The impact preview could not be computed.');
            break;
        }
      });
    });
  }

  protected toggleFilter(filter: ChangeFilter): void {
    this.activeFilter.set(this.activeFilter() === filter ? 'all' : filter);
  }

  protected badgeKind(change: ImpactChange): ImpactChangeBadgeKind {
    switch (change.kind) {
      case 'Added':
        return 'change-added';
      case 'Removed':
        return 'change-removed';
      default:
        return 'change-modified';
    }
  }

  protected badgeLabel(change: ImpactChange): string {
    return `${change.category} ${change.kind.toLowerCase()}`;
  }

  protected identifierOf(change: ImpactChange): string {
    const ref = change.entityRef;
    if (ref.kind === 'vlan' && ref.vlanId !== null) {
      return `vlan:${ref.vlanId}`;
    }
    if (ref.kind === 'port' && ref.switchStableKey && ref.portName) {
      return `${ref.switchStableKey}/${ref.portName}`;
    }
    return ref.switchStableKey ?? ref.kind;
  }

  /** Navigates to the topology page with the change's entity focused (AC3). */
  protected openInTopology(change: ImpactChange): void {
    const rackId = this.state.rackId();
    const focus = focusNodeId(change);
    if (!rackId || !focus) {
      return;
    }
    void this.router.navigate(['/racks', rackId, 'topology'], { queryParams: { focus } });
  }
}

/** Builds the topology graph node id for a change's entity via the shared node-id helpers (AC3). */
function focusNodeId(change: ImpactChange): string | null {
  const ref = change.entityRef;
  if (ref.kind === 'vlan' && ref.vlanId !== null) {
    return vlanNodeId(ref.vlanId);
  }
  if (ref.kind === 'port' && ref.switchStableKey && ref.portName) {
    return portNodeId(ref.switchStableKey, ref.portName);
  }
  if (ref.kind === 'switch' && ref.switchStableKey) {
    return switchNodeId(ref.switchStableKey);
  }
  return null;
}
