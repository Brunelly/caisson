// Route shell for the Network Config authoring area (story #168, AC4): a persistent tab bar (VLAN
// Catalogue | Port Intent are real routed tabs; Routing/Firewall are non-interactive "Coming soon"
// placeholders, matching sidebar-navigation.component.ts's aria-disabled treatment) plus the single
// Save action that persists the COMBINED draft (both tabs edit the same NetworkIntentStateService —
// the backend has one saved state per rack, story Q3). Save/reload orchestration (and all
// toast/telemetry feedback) lives here, not in the state service, matching apply-action.component.ts's
// separation: state services own signals/HTTP-fetch, components own user-facing feedback.
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TelemetryService } from '../core/telemetry/telemetry.service';
import { ToastService } from '../shared/toast/toast.service';
import { NetworkConfigPermissionService } from './services/network-config-permission.service';
import { NetworkIntentStateService } from './state/network-intent-state.service';

@Component({
  selector: 'app-network-config-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  styleUrl: './network-config-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="network-config-shell">
      <header class="network-config-shell__header">
        <h1>Network Config — rack {{ state.rackId() }}</h1>

        <div class="network-config-shell__actions">
          @if (state.dirty()) {
            <span class="network-config-shell__dirty" role="status">Unsaved changes</span>
          }
          <button
            type="button"
            class="network-config-shell__reload"
            [disabled]="state.loading()"
            (click)="onReloadClick()"
          >
            Reload
          </button>
          @if (permission.canAuthorNetworkConfig()) {
            <button
              type="button"
              class="network-config-shell__save"
              [disabled]="!state.dirty() || state.saving()"
              (click)="onSaveClick()"
            >
              {{ state.saving() ? 'Saving…' : 'Save' }}
            </button>
          }
        </div>
      </header>

      <nav class="network-config-shell__tabs" aria-label="Network Config sections">
        <a
          class="network-config-shell__tab"
          routerLink="vlans"
          routerLinkActive="network-config-shell__tab--active"
        >
          VLAN Catalogue
        </a>
        <a
          class="network-config-shell__tab"
          routerLink="ports"
          routerLinkActive="network-config-shell__tab--active"
        >
          Port Intent
        </a>
        <span
          class="network-config-shell__tab network-config-shell__tab--disabled"
          aria-disabled="true"
          title="Coming soon"
        >
          Routing <span class="network-config-shell__soon">Coming soon</span>
        </span>
        <span
          class="network-config-shell__tab network-config-shell__tab--disabled"
          aria-disabled="true"
          title="Coming soon"
        >
          Firewall <span class="network-config-shell__soon">Coming soon</span>
        </span>
      </nav>

      <div class="network-config-shell__body">
        <router-outlet />
      </div>
    </div>
  `,
})
export class NetworkConfigShellComponent {
  protected readonly state = inject(NetworkIntentStateService);
  protected readonly permission = inject(NetworkConfigPermissionService);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);
  private readonly telemetry = inject(TelemetryService);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const rackId = params.get('rackId');
      if (rackId && rackId !== this.state.rackId()) {
        this.state.load(rackId);
      }
    });
  }

  protected onReloadClick(): void {
    const rackId = this.state.rackId();
    if (rackId) {
      this.state.load(rackId);
    }
  }

  protected onSaveClick(): void {
    const rackId = this.state.rackId();
    if (!rackId) {
      return;
    }

    // Synchronous, before the HTTP call — the sole client double-submit guard (state.save() returns
    // null when a save is already in flight).
    const result$ = this.state.save();
    if (!result$) {
      return;
    }

    this.telemetry.networkIntentSaveRequested(
      rackId,
      this.state.vlanCatalogue().length,
      this.state.portIntents().length,
    );

    result$.subscribe((result) => {
      switch (result.kind) {
        case 'ok':
          this.toast.success('Network intent saved.');
          this.telemetry.networkIntentSaveOutcome(rackId, 'success');
          break;
        case 'validationError':
          this.toast.error('Fix the highlighted fields and save again.');
          this.telemetry.networkIntentValidationError(rackId, result.errors.length);
          this.telemetry.networkIntentSaveOutcome(rackId, 'validationError');
          break;
        case 'conflict':
          this.toast.error(
            "This rack's network intent changed elsewhere. Reload to see the latest state, then reapply your changes.",
          );
          this.telemetry.networkIntentSaveOutcome(rackId, 'conflict');
          break;
        case 'forbidden':
          this.toast.error('You do not have the NetworkConfigAuthor permission.');
          this.telemetry.networkIntentSaveOutcome(rackId, 'forbidden');
          break;
        case 'unauthorized':
          this.toast.error('Your session has expired. Sign in again.');
          this.telemetry.networkIntentSaveOutcome(rackId, 'unauthorized');
          break;
        case 'notFound':
          this.toast.error('This rack could not be found.');
          this.telemetry.networkIntentSaveOutcome(rackId, 'notFound');
          break;
        case 'error':
          this.toast.error(
            'Something went wrong saving this network intent.',
            result.correlationId,
          );
          this.telemetry.networkIntentSaveOutcome(
            rackId,
            `error:${result.status}`,
            result.correlationId,
          );
          break;
        default:
          this.toast.error('Something went wrong saving this network intent.');
          this.telemetry.networkIntentSaveOutcome(rackId, result.kind);
      }
    });
  }
}
