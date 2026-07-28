// Pure, dependency-free derivation of the renderable graph from a TopologyGraphDto — the frontend
// analogue of Caisson.Infrastructure.Persistence.Shaping.TopologyGraphProjector. The wire DTO only
// carries servers[].nics[] (each with a bestAttachment/candidates) plus a flat unmappedPorts[] list;
// switch/port/vlan nodes and every edge are *derived* here by dedup-grouping across every NIC's
// bestAttachment and the unmappedPorts anti-join. No Angular/DOM/D3 dependency — safe to unit test in
// isolation and safe to call from any layer (state service, search index, etc).
import type { NicNodeDto, PortAttachmentDto, TopologyGraphDto } from './topology-contracts';

/** Mirrors Caisson.Correlation.Results.ConfidenceBands (High >= 0.8, Medium >= 0.5, else Low). */
export const CONFIDENCE_HIGH_THRESHOLD = 0.8;
export const CONFIDENCE_MEDIUM_THRESHOLD = 0.5;
export type ConfidenceBand = 'High' | 'Medium' | 'Low';

export function confidenceBandOf(confidence: number): ConfidenceBand {
  if (confidence >= CONFIDENCE_HIGH_THRESHOLD) {
    return 'High';
  }
  if (confidence >= CONFIDENCE_MEDIUM_THRESHOLD) {
    return 'Medium';
  }
  return 'Low';
}

/**
 * A NIC/port/edge's mapping state (AC4, resolved Q3 — only the top candidate is drawn; other
 * candidates surface only in the details panel):
 * - `confirmed`: exactly one candidate attachment.
 * - `ambiguous`: a best attachment exists but more than one candidate was recorded.
 * - `unmapped`: no best attachment (NIC never mapped, or the port has no attaching NIC).
 */
export type MappingState = 'confirmed' | 'ambiguous' | 'unmapped';

export type TopologyNodeType = 'server' | 'nic' | 'switch' | 'port' | 'vlan';

interface BaseGraphNode {
  id: string;
  type: TopologyNodeType;
  label: string;
}

export interface ServerGraphNode extends BaseGraphNode {
  type: 'server';
  stableKey: string;
  hostname: string | null;
  bmcUuid: string | null;
}

export interface NicGraphNode extends BaseGraphNode {
  type: 'nic';
  stableKey: string;
  mac: string;
  serverId: string;
  state: MappingState;
  unmappedReasonCode: string | null;
  bestAttachment: PortAttachmentDto | null;
  candidates: PortAttachmentDto[];
}

export interface SwitchGraphNode extends BaseGraphNode {
  type: 'switch';
  stableKey: string;
  serial: string | null;
}

export interface PortGraphNode extends BaseGraphNode {
  type: 'port';
  stableKey: string;
  switchId: string;
  name: string;
  state: MappingState;
}

export interface VlanGraphNode extends BaseGraphNode {
  type: 'vlan';
  stableKey: string;
  vlanId: number;
}

export type TopologyGraphNode =
  ServerGraphNode | NicGraphNode | SwitchGraphNode | PortGraphNode | VlanGraphNode;

export type TopologyEdgeKind = 'server-nic' | 'nic-port' | 'port-vlan';

export interface TopologyGraphEdge {
  id: string;
  source: string;
  target: string;
  kind: TopologyEdgeKind;
  state: MappingState;
}

export interface TopologyGraphModel {
  snapshotId: string;
  version: number;
  nodes: {
    servers: ServerGraphNode[];
    nics: NicGraphNode[];
    switches: SwitchGraphNode[];
    ports: PortGraphNode[];
    vlans: VlanGraphNode[];
  };
  edges: TopologyGraphEdge[];
}

export function serverNodeId(stableKey: string): string {
  return `server:${stableKey}`;
}

export function nicNodeId(stableKey: string): string {
  return `nic:${stableKey}`;
}

export function switchNodeId(stableKey: string): string {
  return `switch:${stableKey}`;
}

export function portNodeId(switchStableKey: string, portName: string): string {
  return `port:${switchStableKey}/${portName}`;
}

export function vlanNodeId(vlanId: number): string {
  return `vlan:${vlanId}`;
}

/** Mirrors Caisson.Domain.Topology.Diffing.StableKeys.ForSwitchPort — the key the entity-detail API
 * expects, distinct from portNodeId (a render-only D3 join key with a `port:` prefix and `/` separator). */
export function switchPortStableKey(switchStableKey: string, portName: string): string {
  return `${switchStableKey}|${portName}`;
}

/** Mirrors Caisson.Domain.Topology.Diffing.StableKeys.ForVlan. */
export function vlanStableKey(vlanId: number): string {
  return vlanId.toString(10);
}

export function classifyNic(nic: NicNodeDto): MappingState {
  if (!nic.bestAttachment) {
    return 'unmapped';
  }
  return nic.candidates.length > 1 ? 'ambiguous' : 'confirmed';
}

/** Derives the renderable graph (nodes + edges) from the wire DTO. Pure — no side effects. */
export function deriveTopologyGraph(graph: TopologyGraphDto): TopologyGraphModel {
  const servers: ServerGraphNode[] = [];
  const nics: NicGraphNode[] = [];
  const switches = new Map<string, SwitchGraphNode>();
  const ports = new Map<string, PortGraphNode>();
  const vlans = new Map<number, VlanGraphNode>();
  const edges: TopologyGraphEdge[] = [];

  const ensureSwitch = (stableKey: string, serial: string | null): SwitchGraphNode => {
    const id = switchNodeId(stableKey);
    let node = switches.get(id);
    if (!node) {
      node = { id, type: 'switch', stableKey, serial, label: serial ?? stableKey };
      switches.set(id, node);
    }
    return node;
  };

  const ensurePort = (
    switchStableKey: string,
    switchSerial: string | null,
    portName: string,
  ): PortGraphNode => {
    const switchNode = ensureSwitch(switchStableKey, switchSerial);
    const id = portNodeId(switchStableKey, portName);
    let node = ports.get(id);
    if (!node) {
      node = {
        id,
        type: 'port',
        stableKey: switchPortStableKey(switchStableKey, portName),
        switchId: switchNode.id,
        name: portName,
        state: 'unmapped',
        label: portName,
      };
      ports.set(id, node);
    }
    return node;
  };

  const ensureVlan = (vlanId: number): VlanGraphNode => {
    const id = vlanNodeId(vlanId);
    let node = vlans.get(vlanId);
    if (!node) {
      node = {
        id,
        type: 'vlan',
        stableKey: vlanStableKey(vlanId),
        vlanId,
        label: `VLAN ${vlanId}`,
      };
      vlans.set(vlanId, node);
    }
    return node;
  };

  // Ports with no attaching NIC (anti-joined server-side) — always unmapped, no VLAN data available
  // (UnmappedPortDto carries no vlans, so no port-vlan edges originate from these).
  for (const unmapped of graph.unmappedPorts) {
    ensurePort(unmapped.switchStableKey, unmapped.switchSerial, unmapped.portName).state =
      'unmapped';
  }

  for (const server of graph.servers) {
    const serverNode: ServerGraphNode = {
      id: serverNodeId(server.stableKey),
      type: 'server',
      stableKey: server.stableKey,
      hostname: server.hostname,
      bmcUuid: server.bmcUuid,
      label: server.hostname ?? server.stableKey,
    };
    servers.push(serverNode);

    for (const nic of server.nics) {
      const state = classifyNic(nic);
      const nicNode: NicGraphNode = {
        id: nicNodeId(nic.stableKey),
        type: 'nic',
        stableKey: nic.stableKey,
        mac: nic.mac,
        serverId: serverNode.id,
        state,
        unmappedReasonCode: nic.unmappedReasonCode,
        bestAttachment: nic.bestAttachment,
        candidates: nic.candidates,
        label: nic.name,
      };
      nics.push(nicNode);

      edges.push({
        id: `${serverNode.id}->${nicNode.id}`,
        source: serverNode.id,
        target: nicNode.id,
        kind: 'server-nic',
        state: 'confirmed',
      });

      const attachment = nic.bestAttachment;
      if (!attachment) {
        continue;
      }

      const portNode = ensurePort(
        attachment.switchStableKey,
        attachment.switchSerial,
        attachment.portName,
      );
      // A port confirmed by any NIC stays confirmed even if another NIC ambiguously also targets it;
      // an ambiguous edge otherwise upgrades an as-yet-untouched (default 'unmapped') port node.
      if (portNode.state !== 'confirmed') {
        portNode.state = state;
      }

      edges.push({
        id: `${nicNode.id}->${portNode.id}`,
        source: nicNode.id,
        target: portNode.id,
        kind: 'nic-port',
        state,
      });

      for (const vlanId of attachment.vlans) {
        const vlanNode = ensureVlan(vlanId);
        const edgeId = `${portNode.id}->${vlanNode.id}`;
        if (!edges.some((e) => e.id === edgeId)) {
          edges.push({
            id: edgeId,
            source: portNode.id,
            target: vlanNode.id,
            kind: 'port-vlan',
            state,
          });
        }
      }
    }
  }

  return {
    snapshotId: graph.snapshotId,
    version: graph.version,
    nodes: {
      servers,
      nics,
      switches: [...switches.values()],
      ports: [...ports.values()],
      vlans: [...vlans.values()],
    },
    edges,
  };
}

/** Every node across all five columns, in a stable (server, nic, switch, port, vlan) order. */
export function flattenGraphNodes(graph: TopologyGraphModel): TopologyGraphNode[] {
  return [
    ...graph.nodes.servers,
    ...graph.nodes.nics,
    ...graph.nodes.switches,
    ...graph.nodes.ports,
    ...graph.nodes.vlans,
  ];
}

export function findNodeById(graph: TopologyGraphModel, id: string): TopologyGraphNode | null {
  return flattenGraphNodes(graph).find((node) => node.id === id) ?? null;
}
