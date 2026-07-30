// Non-blocking side panel (region role, NOT a modal — AC5 requires it to keep updating in place under
// live refresh) bound to TopologyStateService.selectedEntity. Renders the entity's friendly-labelled
// latest fields (Caisson.Domain.Topology.Diffing.TopologyEntityFields — never a raw dictionary dump),
// snapshot context, ambiguous candidates with confidence/band/reason, and unmapped reason (AC3).
import { DatePipe } from '@angular/common';
import { Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { DriftItemDto } from '../../drift/model/drift-contracts';
import { DriftSeverityBadgeComponent } from '../../drift/shared/drift-severity-badge.component';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { EntityDiffDto } from '../model/topology-contracts';
import { confidenceBandOf } from '../model/topology-graph-model';
import type { NicGraphNode, PortGraphNode, TopologyGraphNode } from '../model/topology-graph-model';
import { TopologyEntityService } from '../services/topology-entity.service';
import { TopologyStateService } from '../state/topology-state.service';
import { reasonCodeLabel } from './reason-code-labels';

interface FriendlyField {
  label: string;
  value: string;
  /** Task #129: technical identifiers (serials, management/BMC addresses, PVID/tagged-VLAN ids) render
   * in the DS monospace + tabular-numeral treatment; human-authored fields (model, OS, hostname, link
   * state) stay in the proportional base font. */
  mono: boolean;
}

/** Raw TopologyEntityFields.Extract dictionary keys that hold a technical identifier value, across all
 * entity types — never a human-authored name/prose field (see topology-details-panel.component.ts's
 * FIELD_LABELS for the full field set per type). */
const IDENTIFIER_FIELD_KEYS = new Set([
  'serial',
  'managementIp',
  'pvid',
  'taggedVlans',
  'bmcUuid',
  'bmcAddress',
]);

/** Mirrors Caisson.Domain.Topology.Diffing.TopologyEntityFields.Extract's per-type field set, in the
 * same order, with a friendly label per raw dictionary key. Never invents a field not defined there. */
const FIELD_LABELS: Record<string, Record<string, string>> = {
  Switch: {
    serial: 'Serial',
    managementIp: 'Management IP',
    model: 'Model',
    osVersion: 'OS version',
  },
  SwitchPort: { switch: 'Switch', isUp: 'Up', pvid: 'PVID', taggedVlans: 'Tagged VLANs' },
  Server: {
    bmcType: 'BMC type',
    bmcAddress: 'BMC address',
    bmcUuid: 'BMC UUID',
    hostname: 'Hostname',
  },
  Nic: { server: 'Server', name: 'Name', linkState: 'Link state' },
  Vlan: { name: 'Name' },
};

/** Maps a derived graph node's type to the wire entityType TopologyEntitiesController expects. */
const ENTITY_TYPE_BY_NODE_TYPE: Record<TopologyGraphNode['type'], string> = {
  server: 'Server',
  nic: 'Nic',
  switch: 'Switch',
  port: 'SwitchPort',
  vlan: 'Vlan',
};

@Component({
  selector: 'app-topology-details-panel',
  standalone: true,
  imports: [DatePipe, RouterLink, StatusBadgeComponent, DriftSeverityBadgeComponent],
  styleUrl: './topology-details-panel.component.scss',
  template: `
    @if (state.selection(); as node) {
      <aside
        class="details-panel"
        role="region"
        [attr.aria-labelledby]="headingId"
        (keydown.escape)="close()"
      >
        <header class="details-panel__header">
          <h2 #heading [id]="headingId" tabindex="-1">{{ headingFor(node) }}</h2>
          <button
            type="button"
            class="details-panel__close"
            aria-label="Close details panel"
            (click)="close()"
          >
            ✕
          </button>
        </header>

        @if (state.selectionStaleNotice()) {
          <p class="details-panel__notice" role="status">
            This entity is no longer present in the latest snapshot. Showing its last known details.
          </p>
        }

        @if (state.snapshot(); as snapshot) {
          <p class="details-panel__snapshot-meta">
            Snapshot v{{ snapshot.version }} ({{ snapshot.snapshotId }}) ·
            {{ snapshot.createdAt | date: 'medium' }}
          </p>
        }

        <dl class="details-panel__fields">
          @for (field of fields(); track field.label) {
            <dt>{{ field.label }}</dt>
            <dd [class.details-panel__field--identifier]="field.mono">{{ field.value }}</dd>
          }
        </dl>

        @if (isNic(node); as nic) {
          @if (nic.unmappedReasonCode) {
            <section class="details-panel__section">
              <h3>Unmapped</h3>
              <p>{{ unmappedReasonLabel(nic) }}</p>
            </section>
          } @else if (nic.candidates.length > 1) {
            <section class="details-panel__section">
              <h3>Candidate mappings</h3>
              <ul class="details-panel__candidates">
                @for (
                  candidate of nic.candidates;
                  track candidate.switchStableKey + '|' + candidate.portName
                ) {
                  <li>
                    <app-status-badge [kind]="confidenceBandOf(candidate.confidence)" />
                    <span class="details-panel__field--identifier">{{ candidate.portName }}</span>
                    on
                    <span class="details-panel__field--identifier">{{
                      candidate.switchSerial ?? candidate.switchStableKey
                    }}</span>
                    —
                    <span class="details-panel__field--identifier"
                      >{{ (candidate.confidence * 100).toFixed(0) }}%</span
                    >
                    — {{ reasonLabel(candidate.reasonCode) }}
                  </li>
                }
              </ul>
            </section>
          }
        }

        @if (isPort(node); as port) {
          @if (driftItemFor(port); as driftItem) {
            <section class="details-panel__section details-panel__drift">
              <h3>Drift detected</h3>
              <p class="details-panel__drift-badges">
                <span>{{ driftItem.driftType }}</span>
                <app-drift-severity-badge [severity]="driftItem.severity" />
              </p>
              <p>{{ driftItem.why }}</p>
              <p class="details-panel__drift-detected">
                Detected {{ driftItem.createdAt | date: 'medium' }}
              </p>
              <a
                class="details-panel__drift-link"
                [routerLink]="['/racks', state.rackId(), 'drift', 'items', driftItem.driftItemId]"
              >
                View drift report item
              </a>
            </section>
          }
        }

        @if (history().length > 0) {
          <section class="details-panel__section">
            <h3>Change history</h3>
            <ul class="details-panel__history">
              @for (
                entry of history();
                track entry.toSnapshotId ?? entry.fromSnapshotId + entry.changeType
              ) {
                <li>
                  <span class="details-panel__history-change">{{ entry.changeType }}</span>
                  <span class="details-panel__history-date">{{
                    entry.createdAt | date: 'medium'
                  }}</span>
                </li>
              }
            </ul>
          </section>
        }
      </aside>
    }
  `,
})
export class TopologyDetailsPanelComponent {
  protected readonly state = inject(TopologyStateService);
  private readonly entities = inject(TopologyEntityService);

  protected readonly headingId = 'topology-details-heading';
  private readonly headingRef = viewChild<ElementRef<HTMLElement>>('heading');

  protected readonly confidenceBandOf = confidenceBandOf;
  protected readonly reasonLabel = reasonCodeLabel;

  private readonly latestFields = signal<Record<string, string | null> | null>(null);
  protected readonly history = signal<EntityDiffDto[]>([]);

  private triggerElement: HTMLElement | null = null;

  constructor() {
    effect(() => {
      const node = this.state.selection();
      const rackId = this.state.rackId();
      if (node && rackId) {
        this.triggerElement = (document.activeElement as HTMLElement) ?? null;
        // Cleared up front (not left holding the previous entity's data) so a same-type reselection
        // (e.g. NIC -> NIC) never briefly renders this node's labels against the last node's values.
        this.latestFields.set(null);
        this.history.set([]);
        this.loadLatestFields(rackId, node);
        queueMicrotask(() => this.headingRef()?.nativeElement.focus());
      }
    });
  }

  protected isNic(node: TopologyGraphNode): NicGraphNode | null {
    return node.type === 'nic' ? node : null;
  }

  protected isPort(node: TopologyGraphNode): PortGraphNode | null {
    return node.type === 'port' ? node : null;
  }

  /** Story #67: the full DriftItemDto for a drifted port, joined via the overlay's driftItemId — the
   * overlay entry itself only carries {driftItemId, driftType, severity} (see
   * drift/model/drift-topology-overlay.ts), so `why`/`createdAt` come from the state service's raw
   * driftItems() list, loaded alongside the topology graph. Reuses the existing selection flow rather
   * than building a second competing details surface. */
  protected driftItemFor(port: PortGraphNode): DriftItemDto | null {
    const entry = this.state.driftOverlay().get(port.id);
    if (!entry) {
      return null;
    }
    return this.state.driftItems().find((item) => item.driftItemId === entry.driftItemId) ?? null;
  }

  protected headingFor(node: TopologyGraphNode): string {
    switch (node.type) {
      case 'server':
        return `Server: ${node.label}`;
      case 'nic':
        return `NIC: ${node.label}`;
      case 'switch':
        return `Switch: ${node.label}`;
      case 'port':
        return `Port: ${node.label}`;
      case 'vlan':
        return `VLAN: ${node.label}`;
    }
  }

  protected unmappedReasonLabel(nic: NicGraphNode): string {
    return reasonCodeLabel(nic.unmappedReasonCode) ?? 'No reason recorded.';
  }

  protected fields(): FriendlyField[] {
    const node = this.state.selection();
    if (!node) {
      return [];
    }

    const entityType = ENTITY_TYPE_BY_NODE_TYPE[node.type];
    const labels = FIELD_LABELS[entityType] ?? {};
    const latest = this.latestFields();

    return Object.entries(labels).map(([key, label]) => ({
      label,
      value: latest?.[key] ?? '—',
      mono: IDENTIFIER_FIELD_KEYS.has(key),
    }));
  }

  protected close(): void {
    this.state.clearSelection();
    this.triggerElement?.focus();
  }

  private loadLatestFields(rackId: string, node: TopologyGraphNode): void {
    const entityType = ENTITY_TYPE_BY_NODE_TYPE[node.type];
    const stableKey = node.stableKey;
    this.entities.getEntity(rackId, entityType, stableKey).subscribe((result) => {
      this.latestFields.set(result.kind === 'ok' ? result.value.latest : null);
      this.history.set(result.kind === 'ok' ? result.value.history : []);
    });
  }
}
