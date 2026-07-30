// Presentational D3 graph: server -> NIC -> switch/port -> VLAN, columnar layered layout (the graph is
// not a strict tree — a switch's ports and a VLAN can each be reached from many NICs, which breaks
// d3.hierarchy's single-parent assumption — so positions are computed directly rather than via
// d3.hierarchy) plus d3-zoom for pan/zoom. The constructor's `effect()` re-runs the same keyed D3 join
// used for the initial render whenever the `graph` input changes, so a live refresh (the page rebinds
// `[graph]` to the refetched snapshot) patches existing DOM nodes (enter/update/exit) instead of tearing
// down and rebuilding the SVG — pan/zoom and any focused/selected element survive (AC5, no full reload).
import {
  Component,
  ElementRef,
  ViewEncapsulation,
  effect,
  input,
  output,
  viewChild,
} from '@angular/core';
import * as d3 from 'd3';
import type { DriftOverlayEntry } from '../../drift/model/drift-topology-overlay';
import { flattenGraphNodes } from '../model/topology-graph-model';
import type {
  TopologyEdgeKind,
  TopologyGraphEdge,
  TopologyGraphModel,
  TopologyGraphNode,
  TopologyNodeType,
} from '../model/topology-graph-model';

const EMPTY_DRIFT_OVERLAY: ReadonlyMap<string, DriftOverlayEntry> = new Map();

interface Point {
  x: number;
  y: number;
}

const NODE_WIDTH = 168;
const NODE_HEIGHT = 30;
const ROW_HEIGHT = 46;
const COLUMN_X: Record<TopologyNodeType, number> = {
  server: 90,
  nic: 300,
  switch: 510,
  port: 720,
  vlan: 930,
};

const EDGE_BADGE_GLYPH: Partial<Record<TopologyGraphEdge['state'], string>> = {
  ambiguous: '▲', // ▲
  unmapped: '✕', // ✕
};

// Story #67: drift-severity glyphs, deliberately reusing the exact same glyphs the shared status badge
// (shared/badge/status-badge.component.ts) uses for the same severities — one icon always means the
// same thing across the app. NFR5: colour is never the only signal.
const DRIFT_SEVERITY_GLYPH: Record<DriftOverlayEntry['severity'], string> = {
  High: '✕',
  Medium: '▲',
  Low: 'ℹ',
};

@Component({
  selector: 'app-topology-graph',
  standalone: true,
  encapsulation: ViewEncapsulation.None,
  styleUrl: './topology-graph.component.scss',
  template: `
    <!-- role="group", not "img": the graph contains individually-focusable node/edge buttons, and
         role="img" would tell assistive tech to treat this as an atomic, non-interactive image,
         hiding those descendants (axe: nested-interactive, WCAG 4.1.2). -->
    <svg #svg class="topology-graph" role="group" aria-label="Rack topology graph">
      <g class="topology-graph__viewport">
        <g class="topology-graph__structural-edges"></g>
        <g class="topology-graph__edges"></g>
        <g class="topology-graph__edge-badges"></g>
        <g class="topology-graph__nodes"></g>
        <!-- Additive, on top of nodes (paint order): drifted-port glyphs (story #67). Complete no-op
             (empty selection) when driftOverlay is empty/null — the M0 read-only map renders
             byte-for-byte unchanged in that case (do-not-regress #10). -->
        <g class="topology-graph__drift-badges"></g>
      </g>
    </svg>
  `,
})
export class TopologyGraphComponent {
  readonly graph = input<TopologyGraphModel | null>(null);
  readonly driftOverlay = input<ReadonlyMap<string, DriftOverlayEntry> | null>(null);
  readonly nodeSelected = output<TopologyGraphNode>();
  readonly edgeSelected = output<TopologyGraphEdge>();

  private readonly svgRef = viewChild<ElementRef<SVGSVGElement>>('svg');
  private readonly positions = new Map<string, Point>();
  private readonly zoomBehavior = d3
    .zoom<SVGSVGElement, unknown>()
    .scaleExtent([0.25, 4])
    // d3-zoom's default extent reads SVGAnimatedLength.baseVal off the <svg> element, which jsdom (the
    // unit-test DOM) doesn't implement — an explicit extent function avoids that codepath entirely and
    // degrades to a 0x0 extent (harmless — pan/zoom just has no viewport to clamp against) instead of
    // throwing, in both jsdom and any real browser that hasn't laid the element out yet.
    .extent(function (this: SVGSVGElement): [[number, number], [number, number]] {
      const rect = this.getBoundingClientRect();
      return [
        [0, 0],
        [rect.width, rect.height],
      ];
    })
    .on('zoom', (event: d3.D3ZoomEvent<SVGSVGElement, unknown>) => {
      const svg = this.svgRef();
      if (!svg) {
        return;
      }
      d3.select(svg.nativeElement)
        .select<SVGGElement>('.topology-graph__viewport')
        .attr('transform', event.transform.toString());
    });
  private zoomAttached = false;

  constructor() {
    effect(() => {
      const svg = this.svgRef();
      const graph = this.graph();
      const driftOverlay = this.driftOverlay();
      if (!svg) {
        return;
      }

      if (!this.zoomAttached) {
        d3.select(svg.nativeElement).call(this.zoomBehavior);
        this.zoomAttached = true;
      }

      this.render(graph, driftOverlay ?? EMPTY_DRIFT_OVERLAY);
    });
  }

  /** Pans/zooms so the given node is centred in the viewport (search selection, AC2). */
  panZoomToNode(nodeId: string): void {
    const svg = this.svgRef();
    const position = this.positions.get(nodeId);
    if (!svg || !position) {
      return;
    }

    const { width, height } = svg.nativeElement.getBoundingClientRect();
    const scale = 1.25;
    const transform = d3.zoomIdentity
      .translate(width / 2 - position.x * scale, height / 2 - position.y * scale)
      .scale(scale);

    d3.select(svg.nativeElement)
      .transition()
      .duration(400)
      .call(this.zoomBehavior.transform, transform);
  }

  private render(
    graph: TopologyGraphModel | null,
    driftOverlay: ReadonlyMap<string, DriftOverlayEntry>,
  ): void {
    const svg = this.svgRef();
    if (!svg) {
      return;
    }

    this.positions.clear();

    const root = d3.select(svg.nativeElement);
    const nodesLayer = root.select<SVGGElement>('.topology-graph__nodes');
    const edgesLayer = root.select<SVGGElement>('.topology-graph__edges');
    const badgesLayer = root.select<SVGGElement>('.topology-graph__edge-badges');
    const structuralLayer = root.select<SVGGElement>('.topology-graph__structural-edges');
    const driftBadgesLayer = root.select<SVGGElement>('.topology-graph__drift-badges');

    if (!graph) {
      nodesLayer.selectAll('*').remove();
      edgesLayer.selectAll('*').remove();
      badgesLayer.selectAll('*').remove();
      structuralLayer.selectAll('*').remove();
      driftBadgesLayer.selectAll('*').remove();
      return;
    }

    const allNodes = flattenGraphNodes(graph);
    computePositions(allNodes).forEach((point, id) => this.positions.set(id, point));

    const structuralLinks = graph.nodes.ports.map((port) => ({
      id: `${port.switchId}->${port.id}`,
      source: port.switchId,
      target: port.id,
    }));

    structuralLayer
      .selectAll<SVGLineElement, (typeof structuralLinks)[number]>('line.structural-edge')
      .data(structuralLinks, (d) => d.id)
      .join('line')
      .attr('class', 'structural-edge')
      .attr('x1', (d) => this.positions.get(d.source)?.x ?? 0)
      .attr('y1', (d) => this.positions.get(d.source)?.y ?? 0)
      .attr('x2', (d) => this.positions.get(d.target)?.x ?? 0)
      .attr('y2', (d) => this.positions.get(d.target)?.y ?? 0);

    const positions = this.positions;
    const onEdgeActivate = (edge: TopologyGraphEdge) => this.edgeSelected.emit(edge);

    edgesLayer
      .selectAll<SVGLineElement, TopologyGraphEdge>('line.edge')
      .data(graph.edges, (d) => d.id)
      .join(
        (enter) =>
          enter
            .append('line')
            .attr('tabindex', 0)
            .attr('role', 'button')
            .on('click', (_event, d) => onEdgeActivate(d))
            .on('keydown', (event: KeyboardEvent, d) => {
              if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                onEdgeActivate(d);
              }
            }),
        (update) => update,
      )
      .attr('class', (d) => `edge edge--${d.kind} edge--${d.state}`)
      .attr('aria-label', (d) => edgeAriaLabel(d))
      .attr('x1', (d) => positions.get(d.source)?.x ?? 0)
      .attr('y1', (d) => positions.get(d.source)?.y ?? 0)
      .attr('x2', (d) => positions.get(d.target)?.x ?? 0)
      .attr('y2', (d) => positions.get(d.target)?.y ?? 0);

    badgesLayer
      .selectAll<SVGTextElement, TopologyGraphEdge>('text.edge-badge')
      .data(
        graph.edges.filter((e) => EDGE_BADGE_GLYPH[e.state]),
        (d) => d.id,
      )
      .join('text')
      .attr('class', (d) => `edge-badge edge-badge--${d.state}`)
      .attr('text-anchor', 'middle')
      // Task #130 (AC2/AC6): purely decorative — the underlying <line class="edge"> already carries
      // the full state as text via its own aria-label (edgeAriaLabel), so this glyph is never the only
      // place the state is conveyed and must not be announced a second time.
      .attr('aria-hidden', 'true')
      .attr('x', (d) => midpoint(positions.get(d.source), positions.get(d.target)).x)
      .attr('y', (d) => midpoint(positions.get(d.source), positions.get(d.target)).y)
      .text((d) => EDGE_BADGE_GLYPH[d.state] ?? '');

    const onNodeActivate = (node: TopologyGraphNode) => this.nodeSelected.emit(node);

    const nodeSelection = nodesLayer
      .selectAll<SVGGElement, TopologyGraphNode>('g.node')
      .data(allNodes, (d) => d.id)
      .join((enter) => {
        const g = enter
          .append('g')
          .attr('tabindex', 0)
          .attr('role', 'button')
          .on('click', (_event, d) => onNodeActivate(d))
          .on('keydown', (event: KeyboardEvent, d) => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              onNodeActivate(d);
            }
          });
        g.append('rect').attr('width', NODE_WIDTH).attr('height', NODE_HEIGHT).attr('rx', 6);
        g.append('text')
          .attr('x', NODE_WIDTH / 2)
          .attr('y', NODE_HEIGHT / 2 + 4)
          .attr('text-anchor', 'middle');
        // Story #67: an SVG <title> gives a native hover tooltip for free; text is set below (empty for
        // non-drifted nodes, so nothing renders). aria-describedby references it by id for drifted ports.
        g.append('title').attr('id', (d) => driftTooltipId(d.id));
        return g;
      });

    nodeSelection
      .attr('class', (d) =>
        `node node--${d.type} ${nodeStateClass(d)} ${driftNodeClass(d, driftOverlay)}`.trim(),
      )
      .attr('aria-label', (d) => nodeAriaLabel(d, driftOverlay))
      .attr('aria-describedby', (d) => (driftOverlay.has(d.id) ? driftTooltipId(d.id) : null))
      .attr('transform', (d) => {
        const point = positions.get(d.id) ?? { x: 0, y: 0 };
        return `translate(${point.x - NODE_WIDTH / 2}, ${point.y - NODE_HEIGHT / 2})`;
      });

    nodeSelection.select<SVGTextElement>('text').text((d) => d.label);
    nodeSelection
      .select<SVGTitleElement>('title')
      .text((d) => (driftOverlay.get(d.id) ? driftTooltipText(driftOverlay.get(d.id)!) : ''));

    // Additive drift glyph badges, keyed by port id — a complete no-op when driftOverlay is empty
    // (do-not-regress #10). NFR5: colour is never the only signal, so every drifted port also carries
    // this icon (and the aria-label/tooltip text above), never colour (the node--drift-* class) alone.
    const driftedPorts = graph.nodes.ports.filter((port) => driftOverlay.has(port.id));

    driftBadgesLayer
      .selectAll<SVGTextElement, TopologyGraphNode>('text.drift-badge')
      .data(driftedPorts, (d) => d.id)
      .join('text')
      .attr('class', (d) => `drift-badge ${driftNodeClass(d, driftOverlay)}`)
      .attr('text-anchor', 'middle')
      .attr('aria-hidden', 'true')
      .attr('x', (d) => (positions.get(d.id)?.x ?? 0) + NODE_WIDTH / 2 - 8)
      .attr('y', (d) => (positions.get(d.id)?.y ?? 0) - NODE_HEIGHT / 2 - 4)
      .text((d) => DRIFT_SEVERITY_GLYPH[driftOverlay.get(d.id)!.severity]);
  }
}

function computePositions(nodes: TopologyGraphNode[]): Map<string, Point> {
  const positions = new Map<string, Point>();
  const perColumnIndex: Partial<Record<TopologyNodeType, number>> = {};

  for (const node of nodes) {
    const index = perColumnIndex[node.type] ?? 0;
    perColumnIndex[node.type] = index + 1;
    positions.set(node.id, { x: COLUMN_X[node.type], y: (index + 1) * ROW_HEIGHT });
  }

  return positions;
}

function midpoint(a?: Point, b?: Point): Point {
  if (!a || !b) {
    return { x: 0, y: 0 };
  }
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

function nodeStateClass(node: TopologyGraphNode): string {
  return 'state' in node ? `node--${node.state}` : '';
}

// Story #67: a token-driven class per drift severity, deliberately namespaced (`node--drift-*`, not
// `node--{state}`) so it never collides with the existing confirmed/ambiguous/unmapped mapping-state
// classes above — a port can be simultaneously e.g. `node--confirmed` (its NIC mapping is fine) AND
// `node--drift-high` (its VLAN is still wrong).
function driftNodeClass(
  node: TopologyGraphNode,
  overlay: ReadonlyMap<string, DriftOverlayEntry>,
): string {
  const entry = overlay.get(node.id);
  return entry ? `node--drift-${entry.severity.toLowerCase()}` : '';
}

function driftTooltipId(nodeId: string): string {
  return `topology-drift-tooltip-${nodeId.replace(/[^a-zA-Z0-9_-]/g, '_')}`;
}

function driftTooltipText(entry: DriftOverlayEntry): string {
  return `Drift detected: ${entry.driftType}, ${entry.severity} severity`;
}

function nodeAriaLabel(
  node: TopologyGraphNode,
  overlay: ReadonlyMap<string, DriftOverlayEntry>,
): string {
  const base = (() => {
    switch (node.type) {
      case 'server':
        return `Server ${node.label}`;
      case 'nic':
        return `NIC ${node.label}, MAC ${node.mac}, ${node.state}`;
      case 'switch':
        return `Switch ${node.label}`;
      case 'port':
        return `Port ${node.label}, ${node.state}`;
      case 'vlan':
        return `VLAN ${node.vlanId}`;
    }
  })();

  const entry = overlay.get(node.id);
  // NFR5: severity is always spoken as text here, alongside the icon glyph and the colour-driven
  // class — never colour-only.
  return entry ? `${base}, drift detected: ${entry.driftType}, ${entry.severity} severity` : base;
}

function edgeAriaLabel(edge: TopologyGraphEdge): string {
  const kindLabel: Record<TopologyEdgeKind, string> = {
    'server-nic': 'Server to NIC link',
    'nic-port': 'NIC to port link',
    'port-vlan': 'Port to VLAN link',
  };
  return `${kindLabel[edge.kind]}, ${edge.state}`;
}
