// Single injectable page-state service (signals, no NgRx — ADR 0015/mirrors DriftReportStateService)
// owning the Network Config authoring workspace's in-progress draft: the loaded VLAN catalogue and port
// intents, the dirty/saving flags, and the last save's field errors. OnPush is satisfied by signals — no
// manual markForCheck anywhere in this feature.
//
// VLAN Catalogue and Port Intent are two ROUTES over the SAME draft (the backend persists one combined
// payload per rack, story Q3's "single saved state only"), so mutation methods here are immediate local
// edits — nothing is sent to the server until save() is called (typically from the shell's persistent
// Save action).
import { Injectable, inject, signal } from '@angular/core';
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

export type NetworkIntentLoadError = 'unauthorized' | 'forbidden' | 'notFound' | 'error';

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

  private applyLoaded(intent: NetworkIntentDto, etag: string | null): void {
    this._vlanCatalogue.set(intent.vlanCatalogue);
    this._portIntents.set(intent.portIntents);
    this._updatedAtUtc.set(intent.updatedAtUtc);
    this._updatedBy.set(intent.updatedBy);
    this._etag.set(etag);
    this._dirty.set(false);
    this._fieldErrors.set([]);
  }

  private markDirty(): void {
    this._dirty.set(true);
    this._fieldErrors.set([]);
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
