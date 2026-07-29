// Single injectable page-state service (signals, deliberately no NgRx — ADR 0015). Owns everything the
// topology page/graph/search/details-panel components read: the current rack, the derived graph,
// snapshot metadata, discovery status, the current selection, and the live-connection/staleness status
// the SignalR service (story #10 step 6) drives.
import { Injectable, computed, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import type { DiscoveryStatusDto, SnapshotMetadataDto } from '../model/topology-contracts';
import {
  type TopologyGraphModel,
  type TopologyGraphNode,
  deriveTopologyGraph,
  findNodeById,
} from '../model/topology-graph-model';
import { DiscoveryStatusService } from '../services/discovery-status.service';
import { TopologySnapshotService } from '../services/topology-snapshot.service';

// 'unprocessable'/'rateLimited' are included for type-completeness with the shared ApiResult<T> union
// (extended for the drift-apply write path) — topology's own GET endpoints never actually produce them.
export type TopologyLoadError =
  'unauthorized' | 'forbidden' | 'notFound' | 'unprocessable' | 'rateLimited' | 'error';

/** Live-connection status shown by the stale/disconnected banner (NFR4: text, not colour-only). */
export type ConnectionStatus = 'connecting' | 'live' | 'stale' | 'disconnected';

@Injectable({ providedIn: 'root' })
export class TopologyStateService {
  private readonly snapshots = inject(TopologySnapshotService);
  private readonly discovery = inject(DiscoveryStatusService);

  private readonly _rackId = signal<string | null>(null);
  private readonly _snapshot = signal<SnapshotMetadataDto | null>(null);
  private readonly _graph = signal<TopologyGraphModel | null>(null);
  private readonly _discoveryStatus = signal<DiscoveryStatusDto | null>(null);
  private readonly _selection = signal<TopologyGraphNode | null>(null);
  private readonly _loading = signal(false);
  private readonly _error = signal<TopologyLoadError | null>(null);
  private readonly _connectionStatus = signal<ConnectionStatus>('connecting');
  /** Set when a live refresh removes the currently selected entity (AC5) — the panel stays open. */
  private readonly _selectionStaleNotice = signal(false);

  readonly rackId = this._rackId.asReadonly();
  readonly snapshot = this._snapshot.asReadonly();
  readonly graph = this._graph.asReadonly();
  readonly discoveryStatus = this._discoveryStatus.asReadonly();
  readonly selection = this._selection.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();
  readonly connectionStatus = this._connectionStatus.asReadonly();
  readonly selectionStaleNotice = this._selectionStaleNotice.asReadonly();

  readonly isLatest = computed(() => this._snapshot() !== null);

  /** Loads the latest snapshot + graph and the discovery status for a rack, in parallel (AC1). */
  loadRackTopology(rackId: string): void {
    const isRackChange = this._rackId() !== null && this._rackId() !== rackId;
    this._rackId.set(rackId);
    this._loading.set(true);
    this._error.set(null);
    if (isRackChange) {
      this.clearSelection();
    }

    forkJoin({
      detail: this.snapshots.getLatest(rackId),
      status: this.discovery.getStatus(rackId),
    }).subscribe(({ detail, status }) => {
      this._loading.set(false);

      if (detail.kind !== 'ok') {
        this._error.set(detail.kind);
        return;
      }

      this._snapshot.set(detail.value.snapshot);
      this._graph.set(deriveTopologyGraph(detail.value.graph));
      this._discoveryStatus.set(status.kind === 'ok' ? status.value : null);
    });
  }

  /** Replaces the graph/snapshot with a freshly refetched one (SignalR snapshot-updated, AC5). Keeps
   * the current selection alive if the entity still exists in the new graph; otherwise raises the
   * inline stale notice rather than closing the details panel. */
  applyRefreshedSnapshot(snapshot: SnapshotMetadataDto, graph: TopologyGraphModel): void {
    this._snapshot.set(snapshot);
    this._graph.set(graph);

    const current = this._selection();
    if (!current) {
      return;
    }

    const stillPresent = findNodeById(graph, current.id);
    if (stillPresent) {
      this._selection.set(stillPresent);
      this._selectionStaleNotice.set(false);
    } else {
      this._selectionStaleNotice.set(true);
    }
  }

  selectEntity(node: TopologyGraphNode): void {
    this._selection.set(node);
    this._selectionStaleNotice.set(false);
  }

  clearSelection(): void {
    this._selection.set(null);
    this._selectionStaleNotice.set(false);
  }

  setConnectionStatus(status: ConnectionStatus): void {
    this._connectionStatus.set(status);
  }

  setDiscoveryStatus(status: DiscoveryStatusDto): void {
    this._discoveryStatus.set(status);
  }
}
