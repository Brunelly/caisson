// A flat, grouped-by-type search index derived once per graph load — client-side only (no
// /topology/search endpoint exists; the resolved medium-rack cap keeps the whole graph resident, ADR
// 0015). Query and indexed text are both normalized identically (lowercased, separators stripped) so a
// MAC matches regardless of separator style (mirrors Caisson.Domain.ValueObjects.MacAddressValue
// normalization) and "vlan 120" matches a "VLAN 120" label the same way "sw1" matches "SW-1".
import type { TopologyGraphModel, TopologyNodeType } from '../model/topology-graph-model';

export interface SearchIndexEntry {
  type: TopologyNodeType;
  id: string;
  label: string;
  matchText: string;
}

export interface SearchResultGroup {
  type: TopologyNodeType;
  entries: SearchIndexEntry[];
}

const GROUP_ORDER: readonly TopologyNodeType[] = ['server', 'nic', 'switch', 'port', 'vlan'];

export const GROUP_LABELS: Record<TopologyNodeType, string> = {
  server: 'Servers',
  nic: 'NICs',
  switch: 'Switches',
  port: 'Ports',
  vlan: 'VLANs',
};

/** Lowercases and strips every non-alphanumeric character, so separator style never matters. */
export function normalizeSearchText(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]/g, '');
}

export function buildSearchIndex(graph: TopologyGraphModel): SearchIndexEntry[] {
  const entries: SearchIndexEntry[] = [];

  for (const server of graph.nodes.servers) {
    entries.push(entry('server', server.id, server.label, server.label));
  }
  for (const nic of graph.nodes.nics) {
    entries.push(entry('nic', nic.id, `${nic.label} · ${nic.mac}`, `${nic.label} ${nic.mac}`));
  }
  for (const sw of graph.nodes.switches) {
    entries.push(entry('switch', sw.id, sw.label, `${sw.label} ${sw.stableKey}`));
  }
  for (const port of graph.nodes.ports) {
    entries.push(entry('port', port.id, port.label, port.label));
  }
  for (const vlan of graph.nodes.vlans) {
    entries.push(entry('vlan', vlan.id, vlan.label, `${vlan.label} ${vlan.vlanId}`));
  }

  return entries;
}

function entry(
  type: TopologyNodeType,
  id: string,
  label: string,
  matchSource: string,
): SearchIndexEntry {
  return { type, id, label, matchText: normalizeSearchText(matchSource) };
}

export function searchEntries(index: SearchIndexEntry[], query: string): SearchIndexEntry[] {
  const normalized = normalizeSearchText(query);
  if (!normalized) {
    return [];
  }
  return index.filter((e) => e.matchText.includes(normalized));
}

/** Groups matches by entity type in a fixed, stable order (AC2). Empty groups are omitted. */
export function groupByType(entries: SearchIndexEntry[]): SearchResultGroup[] {
  return GROUP_ORDER.map((type) => ({
    type,
    entries: entries.filter((e) => e.type === type),
  })).filter((group) => group.entries.length > 0);
}
