// The canonical YAML preview / export dialog (story #169, AC1/AC3) — a CDK Dialog panel (ADR 0034). The
// server renders the YAML deterministically (no client-side serialization): on open this POSTs the current
// draft (including any stashed unknown blocks) to the render endpoint and shows the returned UTF-8 YAML
// read-only, with Copy + Download and a persistent "comments are not preserved in v1" notice.
import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TelemetryService } from '../../core/telemetry/telemetry.service';
import { ToastService } from '../../shared/toast/toast.service';
import type { DesiredStateImportIssueDto } from '../model/network-intent-contracts';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { ValidationSummaryComponent } from './validation-summary.component';

@Component({
  selector: 'app-yaml-preview',
  standalone: true,
  imports: [ValidationSummaryComponent],
  styleUrl: './yaml-preview.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="yaml-preview">
      <h2 id="yaml-preview-heading" class="yaml-preview__heading">
        Preview &amp; export desired-state YAML
      </h2>

      <p class="yaml-preview__notice" role="note">
        Comments are not preserved in v1 — any comments in an imported document are dropped from the
        exported YAML.
      </p>

      <app-validation-summary [errors]="issues()" [warnings]="warnings()" />

      @if (loading()) {
        <p class="yaml-preview__status" role="status">Rendering…</p>
      } @else if (yaml(); as text) {
        <pre
          class="yaml-preview__code"
          tabindex="0"
          aria-label="Rendered desired-state YAML"
        ><code>{{ text }}</code></pre>
      }

      <div class="yaml-preview__actions">
        <button type="button" class="yaml-preview__secondary" (click)="close()">Close</button>
        <button
          type="button"
          class="yaml-preview__secondary"
          [disabled]="!yaml()"
          (click)="onCopy()"
        >
          Copy
        </button>
        <button
          type="button"
          class="yaml-preview__primary"
          [disabled]="!yaml()"
          (click)="onDownload()"
        >
          Download
        </button>
      </div>
    </div>
  `,
})
export class YamlPreviewComponent {
  private readonly dialogRef = inject(DialogRef<void>);
  private readonly service = inject(DesiredStateRoundTripService);
  private readonly state = inject(NetworkIntentStateService);
  private readonly toast = inject(ToastService);
  private readonly telemetry = inject(TelemetryService);

  protected readonly yaml = signal<string | null>(null);
  protected readonly warnings = signal<string[]>([]);
  protected readonly issues = signal<DesiredStateImportIssueDto[]>([]);
  protected readonly loading = signal(true);

  constructor() {
    this.render();
  }

  protected close(): void {
    this.dialogRef.close();
  }

  protected async onCopy(): Promise<void> {
    const text = this.yaml();
    if (!text) {
      return;
    }
    try {
      await navigator.clipboard.writeText(text);
      this.toast.success('YAML copied to the clipboard.');
    } catch {
      this.toast.error('Could not copy to the clipboard.');
    }
  }

  protected onDownload(): void {
    const text = this.yaml();
    if (!text) {
      return;
    }
    // UTF-8 blob of the server-rendered bytes — no client-side serialization.
    const blob = new Blob([text], { type: 'application/x-yaml;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `${this.state.rackId() ?? 'rack'}-desired-state.yaml`;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  private render(): void {
    const rackId = this.state.rackId();
    if (!rackId) {
      this.loading.set(false);
      return;
    }

    this.service.render(rackId, this.state.renderRequest()).subscribe((result) => {
      this.loading.set(false);
      switch (result.kind) {
        case 'ok':
          this.yaml.set(result.value.yaml);
          this.warnings.set(result.value.warnings);
          this.telemetry.desiredStateExportOutcome(rackId, 'success');
          break;
        case 'validationError':
          this.issues.set(result.issues);
          this.telemetry.desiredStateExportOutcome(rackId, 'validationError');
          break;
        case 'rateLimited':
          this.toast.error('Too many requests. Wait a moment and try again.');
          this.telemetry.desiredStateExportOutcome(rackId, 'rateLimited', result.correlationId);
          break;
        case 'forbidden':
        case 'unauthorized':
        case 'notFound':
          this.toast.error('You are not able to export this rack’s desired state.');
          this.telemetry.desiredStateExportOutcome(rackId, result.kind);
          break;
        case 'unprocessable':
          this.toast.error('This desired state could not be rendered.');
          this.telemetry.desiredStateExportOutcome(
            rackId,
            `unprocessable:${result.reasonCode ?? 'unknown'}`,
          );
          break;
        default:
          this.toast.error('Something went wrong rendering this YAML.', result.correlationId);
          this.telemetry.desiredStateExportOutcome(
            rackId,
            `error:${result.status}`,
            result.correlationId,
          );
      }
    });
  }
}
