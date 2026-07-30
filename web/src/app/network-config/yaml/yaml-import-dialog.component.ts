// The YAML import dialog (story #169, AC2/AC4) — a CDK Dialog panel (ADR 0034), the same primitive
// discard-changes-dialog.component.ts / vlan-form-dialog.component.ts use, so focus-trap, Escape,
// backdrop-click, focus-return and role="dialog"/aria-modal all come from the shared CDK overlay rather
// than a hand-rolled one. The server owns all YAML work: this dialog only POSTs the pasted/uploaded text
// to the parse endpoint. On success it atomically replaces the draft via applyImportedEnvelope and closes;
// on error it routes the failing paths into the validation summary and applies NOTHING.
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TelemetryService } from '../../core/telemetry/telemetry.service';
import { ToastService } from '../../shared/toast/toast.service';
import type { DesiredStateImportIssueDto } from '../model/network-intent-contracts';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { ValidationSummaryComponent } from './validation-summary.component';

export interface YamlImportDialogData {
  rackId: string;
}

/** Closed with a summary of what was imported (for the shell's toast), or `undefined` on cancel/backdrop/Escape. */
export type YamlImportDialogResult =
  { warnings: string[]; vlanCount: number; portIntentCount: number } | undefined;

@Component({
  selector: 'app-yaml-import-dialog',
  standalone: true,
  imports: [ValidationSummaryComponent],
  styleUrl: './yaml-import-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="yaml-import-dialog">
      <h2 id="yaml-import-dialog-heading" class="yaml-import-dialog__heading">
        Import desired-state YAML
      </h2>
      <p class="yaml-import-dialog__body">
        Paste a desired-state YAML document, or choose a <code>.yaml</code>/<code>.yml</code> file.
        The server parses and validates it; unknown sections are preserved and re-emitted on export.
      </p>

      <div class="yaml-import-dialog__field">
        <label for="yaml-import-file">Upload a file (optional)</label>
        <input
          id="yaml-import-file"
          type="file"
          accept=".yaml,.yml,text/yaml,application/x-yaml"
          class="yaml-import-dialog__file"
          (change)="onFileSelected($event)"
        />
      </div>

      <div class="yaml-import-dialog__field">
        <label for="yaml-import-textarea">YAML</label>
        <textarea
          id="yaml-import-textarea"
          class="yaml-import-dialog__textarea"
          rows="14"
          spellcheck="false"
          autocapitalize="off"
          autocomplete="off"
          [attr.aria-invalid]="issues().length > 0 ? 'true' : null"
          [value]="yaml()"
          (input)="onYamlInput($event)"
        ></textarea>
      </div>

      @if (serverError(); as message) {
        <p class="yaml-import-dialog__server-error" role="alert">{{ message }}</p>
      }

      <app-validation-summary [errors]="issues()" />

      <div class="yaml-import-dialog__actions">
        <button type="button" class="yaml-import-dialog__cancel" (click)="cancel()">Cancel</button>
        <button
          type="button"
          class="yaml-import-dialog__import"
          [disabled]="importing() || yaml().trim().length === 0"
          (click)="onImport()"
        >
          {{ importing() ? 'Importing…' : 'Import' }}
        </button>
      </div>
    </div>
  `,
})
export class YamlImportDialogComponent {
  private readonly data = inject<YamlImportDialogData>(DIALOG_DATA);
  private readonly dialogRef = inject(DialogRef<YamlImportDialogResult>);
  private readonly service = inject(DesiredStateRoundTripService);
  private readonly state = inject(NetworkIntentStateService);
  private readonly toast = inject(ToastService);
  private readonly telemetry = inject(TelemetryService);

  protected readonly yaml = signal('');
  protected readonly issues = signal<DesiredStateImportIssueDto[]>([]);
  protected readonly serverError = signal<string | null>(null);
  protected readonly importing = signal(false);

  protected onYamlInput(event: Event): void {
    this.yaml.set((event.target as HTMLTextAreaElement).value);
  }

  protected async onFileSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }
    this.yaml.set(await file.text());
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }

  protected onImport(): void {
    if (this.importing() || this.yaml().trim().length === 0) {
      return;
    }

    this.importing.set(true);
    this.issues.set([]);
    this.serverError.set(null);

    this.service.parse(this.data.rackId, this.yaml()).subscribe((result) => {
      this.importing.set(false);
      switch (result.kind) {
        case 'ok':
          this.state.applyImportedEnvelope(result.value);
          this.telemetry.desiredStateImportOutcome(this.data.rackId, 'success');
          this.dialogRef.close({
            warnings: result.value.warnings,
            vlanCount: result.value.supportedModel.vlanCatalogue.length,
            portIntentCount: result.value.supportedModel.portIntents.length,
          });
          break;
        case 'validationError':
          this.issues.set(result.issues);
          this.telemetry.desiredStateImportOutcome(this.data.rackId, 'validationError');
          break;
        case 'forbidden':
          this.serverError.set('You do not have the NetworkConfigAuthor permission.');
          this.telemetry.desiredStateImportOutcome(this.data.rackId, 'forbidden');
          break;
        case 'unauthorized':
          this.serverError.set('Your session has expired. Sign in again.');
          this.telemetry.desiredStateImportOutcome(this.data.rackId, 'unauthorized');
          break;
        case 'notFound':
          this.serverError.set('This rack could not be found.');
          this.telemetry.desiredStateImportOutcome(this.data.rackId, 'notFound');
          break;
        case 'rateLimited':
          this.serverError.set('Too many requests. Wait a moment and try again.');
          this.telemetry.desiredStateImportOutcome(
            this.data.rackId,
            'rateLimited',
            result.correlationId,
          );
          break;
        case 'unprocessable':
          this.serverError.set('This YAML could not be processed.');
          this.telemetry.desiredStateImportOutcome(
            this.data.rackId,
            `unprocessable:${result.reasonCode ?? 'unknown'}`,
          );
          break;
        default:
          this.serverError.set('Something went wrong importing this YAML.');
          this.telemetry.desiredStateImportOutcome(
            this.data.rackId,
            `error:${result.status}`,
            result.correlationId,
          );
      }
    });
  }
}
