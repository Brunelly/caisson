// Route shell for the Network Config authoring area (story #168, AC4): a persistent tab bar (VLAN
// Catalogue | Port Intent are real routed tabs; Routing/Firewall are non-interactive "Coming soon"
// placeholders, matching sidebar-navigation.component.ts's aria-disabled treatment) plus the single
// Save action that persists the COMBINED draft (both tabs edit the same NetworkIntentStateService —
// the backend has one saved state per rack, story Q3). Save/reload orchestration (and all
// toast/telemetry feedback) lives here, not in the state service, matching apply-action.component.ts's
// separation: state services own signals/HTTP-fetch, components own user-facing feedback.
import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injector, computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  ActivatedRoute,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { TelemetryService } from '../core/telemetry/telemetry.service';
import { ToastService } from '../shared/toast/toast.service';
import type { ValidationIssue } from './model/preflight-validation-contracts';
import { PreflightValidationService } from './services/preflight-validation.service';
import { PrService } from './services/pr.service';
import { NetworkConfigPermissionService } from './services/network-config-permission.service';
import { NetworkIntentStateService } from './state/network-intent-state.service';
import {
  CreatePrDialogComponent,
  type CreatePrDialogData,
  type CreatePrDialogResult,
} from './pr/create-pr-dialog.component';
import { ValidationIssuesPanelComponent } from './validation/validation-issues-panel.component';
import {
  YamlImportDialogComponent,
  type YamlImportDialogData,
  type YamlImportDialogResult,
} from './yaml/yaml-import-dialog.component';
import { YamlPreviewComponent } from './yaml/yaml-preview.component';
import { ValidationSummaryComponent } from './yaml/validation-summary.component';

@Component({
  selector: 'app-network-config-shell',
  standalone: true,
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    ValidationSummaryComponent,
    ValidationIssuesPanelComponent,
  ],
  styleUrl: './network-config-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- A plain <section> (no accessible name, so it is not itself exposed as a landmark) rather than a
         bare <div> — without SOME article/aside/main/nav/section ancestor, this component's own
         <header> below would get the implicit "banner" role and collide with the persistent app
         shell's RackSelectorTopBar <header role="banner"> (axe: "at most one banner landmark"). -->
    <section class="network-config-shell">
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
            <button type="button" class="network-config-shell__import" (click)="onImportClick()">
              Import YAML
            </button>
            <button type="button" class="network-config-shell__preview" (click)="onExportClick()">
              Preview / Export
            </button>
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

      <!-- Persistent, polite live-region banner for round-trip warnings (e.g. comments-not-preserved),
           which stay visible after the import dialog closes until the next load/reload. -->
      <app-validation-summary [warnings]="state.warnings()" />

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
        <a
          class="network-config-shell__tab"
          routerLink="impact-preview"
          routerLinkActive="network-config-shell__tab--active"
        >
          Impact Preview
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

      <div class="network-config-shell__panes">
        <div class="network-config-shell__editor">
          <router-outlet />
        </div>
        <aside class="network-config-shell__validation" aria-label="Validation and preview">
          <app-validation-issues-panel
            [canRevalidate]="permission.canAuthorNetworkConfig()"
            (revalidate)="onValidateClick()"
            (issueSelected)="onIssueSelected($event)"
          />
        </aside>
      </div>

      <div class="network-config-shell__action-bar">
        @if (permission.canAuthorNetworkConfig()) {
          <button
            type="button"
            class="network-config-shell__validate"
            [disabled]="state.validating()"
            (click)="onValidateClick()"
          >
            {{ state.validating() ? 'Validating…' : 'Validate' }}
          </button>
          <button
            type="button"
            class="network-config-shell__preview-action"
            (click)="onExportClick()"
          >
            Preview changes
          </button>
          <button
            type="button"
            class="network-config-shell__create-pr"
            [disabled]="!canOpenPr()"
            (click)="onCreatePrClick()"
          >
            Create pull request
          </button>
        }
      </div>
    </section>
  `,
})
export class NetworkConfigShellComponent {
  protected readonly state = inject(NetworkIntentStateService);
  protected readonly permission = inject(NetworkConfigPermissionService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly telemetry = inject(TelemetryService);
  private readonly preflight = inject(PreflightValidationService);
  private readonly pr = inject(PrService);
  private readonly dialog = inject(Dialog);
  // The dialogs inject NetworkIntentStateService/DesiredStateRoundTripService; passing THIS component's
  // injector makes the CDK overlay resolve them from the network-config route's injector (in the dev
  // harness, that is where the fakes live) rather than the root injector.
  private readonly injector = inject(Injector);

  /** Whether the Create-PR dialog can be opened: a fresh validation run exists with no blocking errors.
   * (Full gating — including warning acknowledgement — is enforced in the dialog and re-checked server-side.) */
  protected readonly canOpenPr = computed(
    () => this.state.validationRunId() !== null && this.state.issueErrors().length === 0,
  );

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

  /** Opens the YAML import dialog; on a successful parse the dialog has already applied the draft, so this
   * only surfaces the success toast + the comments-not-preserved notice (also shown as a persistent banner). */
  protected onImportClick(): void {
    const rackId = this.state.rackId();
    if (!rackId) {
      return;
    }

    const ref = this.dialog.open<YamlImportDialogResult, YamlImportDialogData>(
      YamlImportDialogComponent,
      {
        data: { rackId },
        injector: this.injector,
        ariaLabelledBy: 'yaml-import-dialog-heading',
        hasBackdrop: true,
        backdropClass: 'cds-overlay-backdrop',
        ariaModal: true,
      },
    );

    ref.closed.subscribe((result) => {
      if (!result) {
        return;
      }
      const suffix = result.warnings.includes('commentsNotPreserved')
        ? ' Comments were not preserved.'
        : '';
      this.toast.success(
        `Imported ${result.vlanCount} VLAN${result.vlanCount === 1 ? '' : 's'} and ` +
          `${result.portIntentCount} port intent${result.portIntentCount === 1 ? '' : 's'}.${suffix}`,
      );
    });
  }

  /** Opens the read-only preview/export dialog, which renders the current draft server-side and offers
   * Copy/Download of the returned UTF-8 YAML. */
  protected onExportClick(): void {
    const rackId = this.state.rackId();
    if (!rackId) {
      return;
    }

    this.dialog.open<void>(YamlPreviewComponent, {
      injector: this.injector,
      ariaLabelledBy: 'yaml-preview-heading',
      hasBackdrop: true,
      backdropClass: 'cds-overlay-backdrop',
      ariaModal: true,
    });
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

  /** Runs pre-flight validation against the current draft. Never mutates or saves the draft (AC3/AC4). */
  protected onValidateClick(): void {
    const rackId = this.state.rackId();
    if (!rackId || this.state.validating()) {
      return;
    }

    this.state.beginValidation();
    this.preflight
      .validate(rackId, this.state.vlanCatalogue(), this.state.portIntents())
      .subscribe((result) => {
        if (result.kind === 'ok') {
          this.state.applyValidation(result.value);
          return;
        }
        this.state.failValidation();
        this.toast.error(
          result.kind === 'forbidden'
            ? 'You do not have the NetworkConfigAuthor permission to run validation.'
            : 'Validation could not be completed. Try again shortly.',
        );
      });
  }

  /** Opens the acknowledgement dialog and, on submit (never on cancel/Escape/backdrop), creates the PR. */
  protected onCreatePrClick(): void {
    const rackId = this.state.rackId();
    const validationRunId = this.state.validationRunId();
    if (!rackId || validationRunId === null) {
      return;
    }

    const ref = this.dialog.open<CreatePrDialogResult, CreatePrDialogData>(
      CreatePrDialogComponent,
      {
        data: { warnings: this.state.issueWarnings() },
        injector: this.injector,
        ariaLabelledBy: 'create-pr-dialog-heading',
        hasBackdrop: true,
        backdropClass: 'cds-overlay-backdrop',
        ariaModal: true,
      },
    );

    ref.closed.subscribe((result) => {
      if (!result) {
        return;
      }
      this.state.setAcknowledgedWarningCodes(result.acknowledgedWarningCodes);
      this.createPullRequest(rackId, validationRunId, result.acknowledgedWarningCodes);
    });
  }

  private createPullRequest(
    rackId: string,
    validationRunId: string,
    acknowledgedWarningCodes: string[],
  ): void {
    this.pr
      .createPullRequest(
        rackId,
        validationRunId,
        acknowledgedWarningCodes,
        this.state.vlanCatalogue(),
        this.state.portIntents(),
      )
      .subscribe((result) => {
        switch (result.kind) {
          case 'ok':
            this.toast.success(result.value.detail);
            break;
          case 'gateRejected':
            // The candidate/topology changed or an ack was stale — re-render the fresh issue set the
            // server returned so the user can act on it, then prompt a re-validate.
            if (result.response) {
              this.state.applyValidation(result.response);
            }
            this.toast.error(
              result.reasonCode === 'revalidate'
                ? 'The configuration changed since it was validated. Re-validate, then try again.'
                : 'Resolve the reported issues and acknowledge all safety warnings, then try again.',
            );
            break;
          case 'forbidden':
            this.toast.error(
              'You do not have the NetworkConfigAuthor permission to create a pull request.',
            );
            break;
          case 'unauthorized':
            this.toast.error('Your session has expired. Sign in again.');
            break;
          default:
            this.toast.error('Something went wrong creating the pull request.');
        }
      });
  }

  /** Navigates to the offending VLAN/port control and asks the routed editor to focus/scroll to it (AC4). */
  protected onIssueSelected(issue: ValidationIssue): void {
    this.state.requestFocus({
      uiPath: issue.uiPath,
      entityRef: issue.entityRef,
      message: issue.message,
    });

    if (issue.entityRef.kind === 'vlan') {
      void this.router.navigate(['vlans'], { relativeTo: this.route });
      return;
    }

    const queryParams =
      issue.entityRef.switchStableKey && issue.entityRef.portName
        ? { switch: issue.entityRef.switchStableKey, port: issue.entityRef.portName }
        : {};
    void this.router.navigate(['ports'], { relativeTo: this.route, queryParams });
  }
}
