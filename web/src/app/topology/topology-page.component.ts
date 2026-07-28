// Routed at /racks/:rackId/topology (AC1). Resolves rackId, loads the latest snapshot + discovery
// status in parallel via TopologyStateService, and shows the snapshot timestamp, a "latest" indicator
// and the discovery-job status. Graph/search/legend/details-panel child components are wired in here by
// later story #10 steps; this page owns only rackId resolution, loading/error state and the header.
import { DatePipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { TopologyDetailsPanelComponent } from './details/topology-details-panel.component';
import { TopologyGraphComponent } from './graph/topology-graph.component';
import { TopologyLegendComponent } from './legend/topology-legend.component';
import { TopologySignalRService } from './live/topology-signalr.service';
import type { TopologyGraphEdge, TopologyGraphNode } from './model/topology-graph-model';
import { TopologySearchComponent } from './search/topology-search.component';
import { TopologyStateService } from './state/topology-state.service';

@Component({
  selector: 'app-topology-page',
  standalone: true,
  template: `
    <section class="topology-page">
      @if (state.loading()) {
        <p role="status">Loading topology…</p>
      } @else if (state.error() === 'notFound') {
        <p role="status">This rack has no topology snapshots yet.</p>
      } @else if (state.error() === 'error') {
        <p role="alert">Something went wrong loading this rack's topology. Try again shortly.</p>
      } @else if (state.snapshot(); as snapshot) {
        <header class="topology-header">
          <h1>Rack topology</h1>
          <span class="badge badge-latest">Latest</span>
          <span class="snapshot-meta">
            Snapshot v{{ snapshot.version }} · {{ snapshot.createdAt | date: 'medium' }}
          </span>
          @if (state.discoveryStatus(); as status) {
            <span class="discovery-status">
              Discovery: {{ status.latestJob?.status ?? 'unknown' }}
              @if (status.lastSuccessAt) {
                (last success {{ status.lastSuccessAt | date: 'short' }})
              }
            </span>
          }
          <app-topology-search (resultSelected)="onSearchResultSelected($event)" />
        </header>

        @if (state.connectionStatus() === 'stale' || state.connectionStatus() === 'disconnected') {
          <p class="connection-banner" role="status">
            {{
              state.connectionStatus() === 'stale'
                ? 'Live updates are stale'
                : 'Live updates disconnected'
            }}
            — showing the last known snapshot.
          </p>
        }

        <div class="topology-shell">
          <app-topology-graph
            #graph
            [graph]="state.graph()"
            (nodeSelected)="onNodeSelected($event)"
            (edgeSelected)="onEdgeSelected($event)"
          />
          <app-topology-legend />
          <app-topology-details-panel />
        </div>
      }
    </section>
  `,
  styles: [
    `
      .topology-page {
        display: flex;
        flex-direction: column;
        height: 100%;
      }

      .topology-header {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        padding: 0.75rem 1rem;
        border-bottom: 1px solid var(--color-border);
      }

      .topology-header h1 {
        font-size: 1.125rem;
        margin: 0;
      }

      .badge-latest {
        background: var(--color-status-confirmed-bg);
        color: var(--color-status-confirmed);
        border-radius: 999px;
        padding: 0.125rem 0.625rem;
        font-size: 0.75rem;
        font-weight: 600;
      }

      .snapshot-meta,
      .discovery-status {
        color: var(--color-text-muted);
        font-size: 0.875rem;
      }

      .connection-banner {
        margin: 0;
        padding: 0.5rem 1rem;
        background: var(--color-status-ambiguous-bg);
        color: var(--color-status-ambiguous);
        font-size: 0.8125rem;
      }

      .topology-shell {
        flex: 1;
        min-height: 0;
        display: flex;
        position: relative;
      }

      .topology-shell app-topology-graph {
        flex: 1;
        min-width: 0;
      }

      .topology-shell app-topology-legend {
        position: absolute;
        bottom: 0;
        left: 0;
        right: 0;
      }
    `,
  ],
  imports: [
    DatePipe,
    TopologyGraphComponent,
    TopologyLegendComponent,
    TopologySearchComponent,
    TopologyDetailsPanelComponent,
  ],
})
export class TopologyPageComponent {
  protected readonly state = inject(TopologyStateService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly signalR = inject(TopologySignalRService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly graphRef = viewChild<TopologyGraphComponent>('graph');

  constructor() {
    const rackId = this.route.snapshot.paramMap.get('rackId');
    if (rackId) {
      this.state.loadRackTopology(rackId);
      this.signalR.connect(rackId);
    }

    this.destroyRef.onDestroy(() => this.signalR.disconnect());

    // AC6: an unauthorized/forbidden response from the initial load (e.g. a session that lost its
    // rack-level access mid-page) routes to the generic access-denied state, same as the guard.
    effect(() => {
      const error = this.state.error();
      if (error === 'unauthorized' || error === 'forbidden') {
        void this.router.navigate(['/access-denied']);
      }
    });
  }

  protected onNodeSelected(node: TopologyGraphNode): void {
    this.state.selectEntity(node);
  }

  protected onEdgeSelected(edge: TopologyGraphEdge): void {
    // Edges have no independent details view yet; selecting one focuses its source node instead.
    this.graphRef()?.panZoomToNode(edge.source);
  }

  /** Selecting a search result opens its drill-down and pans/zooms the graph to it (AC2). */
  protected onSearchResultSelected(node: TopologyGraphNode): void {
    this.state.selectEntity(node);
    this.graphRef()?.panZoomToNode(node.id);
  }
}
