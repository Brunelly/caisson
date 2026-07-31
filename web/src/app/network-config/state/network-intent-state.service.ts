// Single injectable page-state service (signals, no NgRx — ADR 0015/mirrors DriftReportStateService)
// owning the Network Config authoring workspace's in-progress draft: the loaded VLAN catalogue and port
// intents, the dirty/saving flags, and the last save's field errors. OnPush is satisfied by signals — no
// manual markForCheck anywhere in this feature.
//
// VLAN Catalogue and Port Intent are two ROUTES over the SAME draft (the backend persists one combined
// payload per rack, story Q3's "single saved state only"), so mutation methods here are immediate local
// edits — nothing is sent to the server until save() is called (typically from the shell's persistent
// Save action).
import { Injectable, computed, inject, signal } from '@angular/core';
import type { Observable } from 'rxjs';
import { tap } from 'rxjs';
import type {
  NetworkIntentFieldError,
  NetworkIntentSaveResult,
} from '../services/network-intent.service';
import { NetworkIntentService } from '../services/network-intent.service';
import type {
  DesiredStateRenderRequest,
  DesiredStateRoundTripEnvelopeDto,
  NetworkIntentDto,
  PortAccessIntentDto,
  PreservedYamlBlockDto,
  VlanCatalogueEntryDto,
} from '../model/network-intent-contracts';
import type {
  PreflightValidationResponse,
  ValidationIssue,
} from '../model/preflight-validation-contracts';

export type NetworkIntentLoadError = 'unauthorized' | 'forbidden' | 'notFound' | 'error';

/** The status of the current pre-flight validation cycle (story #170). */
export type PreflightStatus = 'idle' | 'validating' | 'validated' | 'error';

/** The editor target an issue click resolves to (story #170, AC4): the offending control to focus/scroll. */
export interface FocusTarget {
  uiPath: string | null;
  entityRef: ValidationIssue['entityRef'];
  message: string;
}

@Injectable({ providedIn: 'root' })
export class NetworkIntentStateService {
  private readonly service = inject(NetworkIntentService);

  private readonly _rackId = signal<string | null>(null);
  private readonly _vlanCatalogue = signal<VlanCatalogueEntryDto[]>([]);
  private readonly _portIntents = signal<PortAccessIntentDto[]>([]);
  private readonly _etag = signal<string | null>(null);
  private readonly _updatedAtUtc = signal<string | null>(null);
  private readonly _updatedBy = signal<string | null>(null);
  private readonly _dirty = signal(false);
  private readonly _loading = signal(false);
  private readonly _saving = signal(false);
  private readonly _loadError = signal<NetworkIntentLoadError | null>(null);
  private readonly _fieldErrors = signal<NetworkIntentFieldError[]>([]);
  // Story #169: preserved unknown YAML blocks + warnings + schema version stashed from the last import, so
  // a later Export re-emits the unknown sections byte-for-byte. Client-only — the network-intent save path
  // (#168) persists none of these; they live only for the import→edit→export round-trip.
  private readonly _unknownBlocks = signal<PreservedYamlBlockDto[]>([]);
  private readonly _schemaVersion = signal<number | null>(null);
  private readonly _warnings = signal<string[]>([]);

  // Story #170 pre-flight validation state. The validationRunId binds the issue set to the exact candidate
  // + topology it was validated against; any draft edit clears it (stale-on-edit) so a fresh validation is
  // forced before a PR can be created. Acknowledged safety-warning codes gate PR creation.
  private readonly _issueErrors = signal<ValidationIssue[]>([]);
  private readonly _issueWarnings = signal<ValidationIssue[]>([]);
  private readonly _validationRunId = signal<string | null>(null);
  private readonly _topologySnapshotId = signal<string | null>(null);
  private readonly _acknowledgedWarningCodes = signal<ReadonlySet<string>>(new Set());
  private readonly _lastValidatedAtUtc = signal<string | null>(null);
  private readonly _preflightStatus = signal<PreflightStatus>('idle');

  // Impact-preview freshness (story #171): the last computed preview's candidate id and whether it still
  // reflects the current draft. Any draft edit/import/reload invalidates it (via clearValidation) so a fresh
  // preview is required before PR submission.
  private readonly _previewCandidateId = signal<string | null>(null);
  private readonly _previewFresh = signal(false);
  private readonly _focusTarget = signal<FocusTarget | null>(null);

  readonly rackId = this._rackId.asReadonly();
  readonly vlanCatalogue = this._vlanCatalogue.asReadonly();
  readonly portIntents = this._portIntents.asReadonly();
  readonly updatedAtUtc = this._updatedAtUtc.asReadonly();
  readonly updatedBy = this._updatedBy.asReadonly();
  readonly dirty = this._dirty.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly saving = this._saving.asReadonly();
  readonly loadError = this._loadError.asReadonly();
  readonly fieldErrors = this._fieldErrors.asReadonly();
  readonly unknownBlocks = this._unknownBlocks.asReadonly();
  readonly schemaVersion = this._schemaVersion.asReadonly();
  readonly warnings = this._warnings.asReadonly();

  readonly issueErrors = this._issueErrors.asReadonly();
  readonly issueWarnings = this._issueWarnings.asReadonly();
  readonly validationRunId = this._validationRunId.asReadonly();
  readonly topologySnapshotId = this._topologySnapshotId.asReadonly();
  readonly acknowledgedWarningCodes = this._acknowledgedWarningCodes.asReadonly();
  readonly lastValidatedAtUtc = this._lastValidatedAtUtc.asReadonly();
  readonly preflightStatus = this._preflightStatus.asReadonly();
  readonly previewCandidateId = this._previewCandidateId.asReadonly();
  readonly previewFresh = this._previewFresh.asReadonly();
  readonly validating = computed(() => this._preflightStatus() === 'validating');
  readonly focusTarget = this._focusTarget.asReadonly();

  /** The distinct safety-warning codes in the current run that still require acknowledgement. */
  readonly warningCodesRequiringAck = computed(() => [
    ...new Set(this._issueWarnings().map((w) => w.code)),
  ]);

  /** PR creation is allowed only when the current run is still valid (a validationRunId exists — cleared
   * on any edit), has no errors, and every safety-warning code has been acknowledged (AC3/AC5). */
  readonly canCreatePr = computed(() => {
    if (this._validationRunId() === null || this._issueErrors().length > 0) {
      return false;
    }
    const acknowledged = this._acknowledgedWarningCodes();
    return this.warningCodesRequiringAck().every((code) => acknowledged.has(code));
  });

  /** Loads (or reloads) the rack's saved network intent, discarding any unsaved local edits — used on
   * first navigation into the feature and as the explicit "reload" action after a 409 conflict. */
  load(rackId: string): void {
    this._rackId.set(rackId);
    this._loading.set(true);
    this._loadError.set(null);
    // A fresh load/reload is an explicit "start over" — drop any stashed import blocks/warnings.
    this.clearImportState();
    this.service.getIntent(rackId).subscribe((result) => {
      this._loading.set(false);
      if (result.kind !== 'ok') {
        this._loadError.set(toLoadError(result.kind));
        return;
      }
      this.applyLoaded(result.value.intent, result.value.etag);
    });
  }

  addVlan(entry: VlanCatalogueEntryDto): void {
    this._vlanCatalogue.update((list) => [...list, entry]);
    this.markDirty();
  }

  updateVlan(id: number, entry: VlanCatalogueEntryDto): void {
    this._vlanCatalogue.update((list) => list.map((v) => (v.id === id ? entry : v)));
    this.markDirty();
  }

  retireVlan(id: number): void {
    this._vlanCatalogue.update((list) => list.filter((v) => v.id !== id));
    this.markDirty();
  }

  /** Sets (or replaces) this port's access-VLAN intent. */
  setPortIntent(switchStableKey: string, portName: string, accessVlanId: number): void {
    this.upsertPortIntent(switchStableKey, portName, accessVlanId);
  }

  /** Reverts a port to Unchanged/Inherit — clears any stored intent for it entirely (AC2: "no row = no
   * intent", never a stored null-VLAN row). */
  clearPortIntent(switchStableKey: string, portName: string): void {
    this._portIntents.update((list) =>
      list.filter((p) => !isSamePort(p, switchStableKey, portName)),
    );
    this.markDirty();
  }

  portIntentFor(switchStableKey: string, portName: string): PortAccessIntentDto | null {
    return this._portIntents().find((p) => isSamePort(p, switchStableKey, portName)) ?? null;
  }

  /** Atomically replaces the draft from a successfully-imported envelope (story #169, AC2): swaps the VLAN
   * catalogue + port intents and stashes the preserved unknown blocks, warnings, and schema version so a
   * later Export re-emits the unknown sections verbatim. Marks the draft dirty. Applied ONLY on a
   * successful parse — a failed import leaves the prior draft untouched. */
  applyImportedEnvelope(envelope: DesiredStateRoundTripEnvelopeDto): void {
    this._vlanCatalogue.set(envelope.supportedModel.vlanCatalogue);
    this._portIntents.set(envelope.supportedModel.portIntents);
    this._unknownBlocks.set(envelope.unknownBlocks);
    this._warnings.set(envelope.warnings);
    this._schemaVersion.set(envelope.schemaVersion);
    this._dirty.set(true);
    this._fieldErrors.set([]);
  }

  /** Builds the render (export) request from the current draft, including the stashed unknown blocks so the
   * server re-emits them byte-for-byte. */
  renderRequest(): DesiredStateRenderRequest {
    return {
      vlanCatalogue: this._vlanCatalogue(),
      portIntents: this._portIntents(),
      unknownBlocks: this._unknownBlocks(),
      warnings: this._warnings(),
      schemaVersion: this._schemaVersion(),
    };
  }

  /** Saves the combined draft. Returns `null` (no-op) when a save is already in flight — the sole
   * client double-submit guard, set synchronously before the HTTP call fires. Returns the in-flight
   * Observable otherwise so the caller (typically the shell's Save action) can show a toast/telemetry
   * per outcome without this state service owning any UI-feedback concern itself. */
  save(): Observable<NetworkIntentSaveResult> | null {
    const rackId = this._rackId();
    if (!rackId || this._saving()) {
      return null;
    }

    this._saving.set(true);
    return this.service
      .saveIntent(
        rackId,
        { vlanCatalogue: this._vlanCatalogue(), portIntents: this._portIntents() },
        this._etag(),
      )
      .pipe(
        tap((result) => {
          this._saving.set(false);
          if (result.kind === 'ok') {
            this.applyLoaded(result.value.intent, result.value.etag);
          } else if (result.kind === 'validationError') {
            this._fieldErrors.set(result.errors);
          }
        }),
      );
  }

  private upsertPortIntent(switchStableKey: string, portName: string, accessVlanId: number): void {
    this._portIntents.update((list) => {
      const next: PortAccessIntentDto = { switchStableKey, portName, accessVlanId };
      const index = list.findIndex((p) => isSamePort(p, switchStableKey, portName));
      if (index === -1) {
        return [...list, next];
      }
      const copy = [...list];
      copy[index] = next;
      return copy;
    });
    this.markDirty();
  }

  /** Marks the start of a validation cycle (drives the panel's shimmer skeletons). */
  beginValidation(): void {
    this._preflightStatus.set('validating');
  }

  /** Records that the validation cycle failed to reach the server (403/network/etc). */
  failValidation(): void {
    this._preflightStatus.set('error');
  }

  /** Applies a successful pre-flight validation result: swaps the issue set, binds the validationRunId to
   * this candidate + topology, resets acknowledgements (a fresh run is a fresh acknowledgement slate), and
   * stamps the last-validated time. Never mutates or saves the draft (AC3/AC4). */
  applyValidation(response: PreflightValidationResponse): void {
    this._issueErrors.set(response.errors);
    this._issueWarnings.set(response.warnings);
    this._validationRunId.set(response.validationRunId);
    this._topologySnapshotId.set(response.topologySnapshotId);
    this._lastValidatedAtUtc.set(response.validatedAtUtc);
    this._acknowledgedWarningCodes.set(new Set());
    this._preflightStatus.set('validated');
  }

  /** Toggles acknowledgement of one safety-warning code (per-warning checkbox in the Create-PR dialog). */
  acknowledgeWarning(code: string, acknowledged: boolean): void {
    this._acknowledgedWarningCodes.update((set) => {
      const next = new Set(set);
      if (acknowledged) {
        next.add(code);
      } else {
        next.delete(code);
      }
      return next;
    });
  }

  /** Acknowledges every current safety-warning code at once (dialog "acknowledge all" convenience). */
  acknowledgeAllWarnings(): void {
    this._acknowledgedWarningCodes.set(new Set(this.warningCodesRequiringAck()));
  }

  /** Replaces the acknowledged-warning-code set wholesale (the Create-PR dialog commits its acks on submit). */
  setAcknowledgedWarningCodes(codes: readonly string[]): void {
    this._acknowledgedWarningCodes.set(new Set(codes));
  }

  /** Requests that the editor focus/scroll to the control an issue points at (cleared once consumed). */
  requestFocus(target: FocusTarget): void {
    this._focusTarget.set(target);
  }

  /** Clears the pending focus target once a routed editor has consumed it. */
  clearFocusTarget(): void {
    this._focusTarget.set(null);
  }

  private applyLoaded(intent: NetworkIntentDto, etag: string | null): void {
    this._vlanCatalogue.set(intent.vlanCatalogue);
    this._portIntents.set(intent.portIntents);
    this._updatedAtUtc.set(intent.updatedAtUtc);
    this._updatedBy.set(intent.updatedBy);
    this._etag.set(etag);
    this._dirty.set(false);
    this._fieldErrors.set([]);
    this.clearValidation();
  }

  private markDirty(): void {
    this._dirty.set(true);
    this._fieldErrors.set([]);
    // Stale-on-edit: any draft change invalidates the last validation run so a fresh one is forced before
    // a PR can be created (TOCTOU safety, AC3/AC4). Keep no stale issues/acks around.
    this.clearValidation();
  }

  /** Records that a fresh impact preview was computed for the current draft (story #171). */
  applyPreview(candidateId: string): void {
    this._previewCandidateId.set(candidateId);
    this._previewFresh.set(true);
  }

  private clearValidation(): void {
    this._issueErrors.set([]);
    this._issueWarnings.set([]);
    this._validationRunId.set(null);
    this._topologySnapshotId.set(null);
    this._acknowledgedWarningCodes.set(new Set());
    this._lastValidatedAtUtc.set(null);
    this._preflightStatus.set('idle');
    this._focusTarget.set(null);
    // Any draft change also invalidates the impact preview (stale-on-edit, story #171 AC3).
    this._previewCandidateId.set(null);
    this._previewFresh.set(false);
  }

  private clearImportState(): void {
    this._unknownBlocks.set([]);
    this._warnings.set([]);
    this._schemaVersion.set(null);
  }
}

function isSamePort(
  intent: PortAccessIntentDto,
  switchStableKey: string,
  portName: string,
): boolean {
  return intent.switchStableKey === switchStableKey && intent.portName === portName;
}

function toLoadError(
  kind: 'unauthorized' | 'forbidden' | 'notFound' | 'unprocessable' | 'rateLimited' | 'error',
): NetworkIntentLoadError {
  switch (kind) {
    case 'unauthorized':
    case 'forbidden':
    case 'notFound':
      return kind;
    default:
      return 'error';
  }
}
