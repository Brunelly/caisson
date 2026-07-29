// Pure, dependency-free correlation of drift items onto the already-derived topology graph (ADR 0033):
// given a TopologyGraphModel and the rack's drift items, produces a Map<portNodeId, DriftOverlayEntry>
// the graph component patches onto its existing port nodes. No Angular/DOM/D3 dependency — safe to unit
// test in isolation, mirroring topology-graph-model.ts's own pure-derivation style.
//
// Scoped to AccessVlanMismatch only (M1's only apply-supported drift type, and the only one carrying a
// machine-resolvable port reference) — every other drift type still appears in the list/detail views,
// just never on the graph.
//
// Correlates via `item.details.switchName`/`item.details.portName` (ADR 0032: DriftEngine additively
// stashes these on AccessVlanMismatch items specifically so consumers don't have to parse the opaque,
// versioned SubjectKey — ADR 0029) rather than SubjectKey. A port's `switchStableKey` is not simply the
// escaped switch name — it may carry additional stable-key segments after it (e.g. a serial) — so the
// match is an ANCHORED prefix (`switchStableKey.startsWith(escape(switchName) + '|')`), never a loose
// `.includes()`, to avoid mis-highlighting a differently-named switch whose stable key happens to
// contain the same substring.
import type { DriftItemDto, DriftSeverity, DriftType } from './drift-contracts';
import { escapeStableKeySegment } from '../../topology/model/topology-graph-model';
import type { PortGraphNode, TopologyGraphModel } from '../../topology/model/topology-graph-model';

export interface DriftOverlayEntry {
  driftItemId: string;
  driftType: DriftType;
  severity: DriftSeverity;
}

const SEVERITY_RANK: Record<DriftSeverity, number> = { Low: 0, Medium: 1, High: 2 };

function isMoreSevere(candidate: DriftSeverity, current: DriftSeverity): boolean {
  return SEVERITY_RANK[candidate] > SEVERITY_RANK[current];
}

function detailsSwitchAndPortName(
  item: DriftItemDto,
): { switchName: string; portName: string } | null {
  const details = item.details;
  if (!details || typeof details !== 'object') {
    return null;
  }
  const switchName = (details as Record<string, unknown>)['switchName'];
  const portName = (details as Record<string, unknown>)['portName'];
  if (typeof switchName !== 'string' || typeof portName !== 'string') {
    return null;
  }
  return { switchName, portName };
}

function findMatchingPort(
  ports: readonly PortGraphNode[],
  switchStableKeyByPortSwitchId: ReadonlyMap<string, string>,
  switchName: string,
  portName: string,
): PortGraphNode | null {
  const anchor = `${escapeStableKeySegment(switchName)}|`;
  for (const port of ports) {
    if (port.name !== portName) {
      continue;
    }
    const switchStableKey = switchStableKeyByPortSwitchId.get(port.switchId);
    if (switchStableKey?.startsWith(anchor)) {
      return port;
    }
  }
  return null;
}

/** Builds the port-node-id -> drift overlay entry map for a rack's AccessVlanMismatch + actionable
 * drift items. Complete no-op (empty map) when there are no such items — the read-only M0 map must
 * render byte-for-byte unchanged in that case (do-not-regress #10). */
export function buildDriftTopologyOverlay(
  graph: TopologyGraphModel,
  driftItems: readonly DriftItemDto[],
): Map<string, DriftOverlayEntry> {
  const overlay = new Map<string, DriftOverlayEntry>();
  if (driftItems.length === 0) {
    return overlay;
  }

  const switchStableKeyByPortSwitchId = new Map<string, string>();
  for (const switchNode of graph.nodes.switches) {
    switchStableKeyByPortSwitchId.set(switchNode.id, switchNode.stableKey);
  }

  for (const item of driftItems) {
    if (item.driftType !== 'AccessVlanMismatch' || !item.actionable) {
      continue;
    }
    const parsed = detailsSwitchAndPortName(item);
    if (!parsed) {
      continue;
    }
    const port = findMatchingPort(
      graph.nodes.ports,
      switchStableKeyByPortSwitchId,
      parsed.switchName,
      parsed.portName,
    );
    if (!port) {
      continue;
    }

    const existing = overlay.get(port.id);
    if (existing && !isMoreSevere(item.severity, existing.severity)) {
      continue;
    }
    overlay.set(port.id, {
      driftItemId: item.driftItemId,
      driftType: item.driftType,
      severity: item.severity,
    });
  }

  return overlay;
}
