import { describe, expect, it } from 'vitest';
import type {
  PortGraphNode,
  SwitchGraphNode,
  TopologyGraphModel,
} from '../../topology/model/topology-graph-model';
import type { DriftItemDto } from './drift-contracts';
import { buildDriftTopologyOverlay } from './drift-topology-overlay';

function switchNode(stableKey: string, id = `switch:${stableKey}`): SwitchGraphNode {
  return { id, type: 'switch', stableKey, serial: null, label: stableKey };
}

function portNode(switchId: string, name: string, id = `port:${switchId}/${name}`): PortGraphNode {
  return {
    id,
    type: 'port',
    stableKey: `${switchId}|${name}`,
    switchId,
    name,
    state: 'unmapped',
    label: name,
  };
}

function graph(switches: SwitchGraphNode[], ports: PortGraphNode[]): TopologyGraphModel {
  return {
    snapshotId: 'snap-1',
    version: 1,
    nodes: { servers: [], nics: [], switches, ports, vlans: [] },
    edges: [],
  };
}

function driftItem(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: 'item-1',
    driftReportId: 'report-1',
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack|sw-01|ether5',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch',
    details: { switchName: 'sw-01', portName: 'ether5' },
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('buildDriftTopologyOverlay', () => {
  it('returns an empty map when there are no drift items (do-not-regress #10)', () => {
    const g = graph([switchNode('sw-01|serial-1')], [portNode('switch:sw-01|serial-1', 'ether5')]);

    expect(buildDriftTopologyOverlay(g, []).size).toBe(0);
  });

  it('matches a port by anchored switchName prefix + exact portName', () => {
    const sw = switchNode('sw-01|serial-1');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [driftItem()]);

    expect(overlay.get(port.id)).toEqual({
      driftItemId: 'item-1',
      driftType: 'AccessVlanMismatch',
      severity: 'High',
    });
  });

  it('anchors the match — a switch stable key sharing only a prefix (not the anchor) never matches', () => {
    // "sw-0" is itself a valid, distinct switch name (escaped stable key "sw-0|serial-9"); its own
    // AccessVlanMismatch item legitimately matches. But a DIFFERENT item naming switchName "sw" must
    // not match this switch just because "sw-0|serial-9" starts with the substring "sw" — anchoring on
    // "sw|" (with the trailing separator) is what a loose `.startsWith(switchName)`/`.includes()` would
    // get wrong.
    const sw = switchNode('sw-0|serial-9');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const legitimate = buildDriftTopologyOverlay(g, [
      driftItem({ details: { switchName: 'sw-0', portName: 'ether5' } }),
    ]);
    expect(legitimate.get(port.id)).toBeDefined();

    const falsePositiveCandidate = buildDriftTopologyOverlay(g, [
      driftItem({ details: { switchName: 'sw', portName: 'ether5' } }),
    ]);
    expect(falsePositiveCandidate.size).toBe(0);
  });

  it('escapes % and | in switchName before anchoring, so a literal separator in a switch name never false-matches', () => {
    // escapeStableKeySegment('a%b|c') === 'a%25b%7Cc'
    const sw = switchNode('a%25b%7Cc|serial-1');
    const port = portNode(sw.id, 'ether1');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [
      driftItem({ details: { switchName: 'a%b|c', portName: 'ether1' } }),
    ]);

    expect(overlay.get(port.id)).toBeDefined();
  });

  it('never loosely matches an un-escaped literal "|" in switchName against an unrelated switch+port', () => {
    // Without escaping, switchName "a|ether9" would itself contain a "|" and could be mistaken for
    // already carrying a switch|port stable key, spuriously matching switch "a" port "ether9".
    const decoy = switchNode('a|serial-1');
    const decoyPort = portNode(decoy.id, 'ether9');
    const g = graph([decoy], [decoyPort]);

    const overlay = buildDriftTopologyOverlay(g, [
      driftItem({ details: { switchName: 'a|ether9', portName: 'ether9' } }),
    ]);

    expect(overlay.size).toBe(0);
  });

  it('ignores drift items of a type other than AccessVlanMismatch', () => {
    const sw = switchNode('sw-01|serial-1');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [
      driftItem({ driftType: 'UnexpectedTrunkConfig' }),
    ]);

    expect(overlay.size).toBe(0);
  });

  it('ignores non-actionable drift items', () => {
    const sw = switchNode('sw-01|serial-1');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [driftItem({ actionable: false })]);

    expect(overlay.size).toBe(0);
  });

  it('ignores items with a missing/malformed details bag', () => {
    const sw = switchNode('sw-01|serial-1');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [
      driftItem({ details: null }),
      driftItem({ details: { switchName: 'sw-01' } }),
      driftItem({ details: 'not-an-object' as never }),
    ]);

    expect(overlay.size).toBe(0);
  });

  it('ignores an item whose port name does not exist on the matched switch', () => {
    const sw = switchNode('sw-01|serial-1');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [
      driftItem({ details: { switchName: 'sw-01', portName: 'ether99' } }),
    ]);

    expect(overlay.size).toBe(0);
  });

  it('picks the highest severity when multiple items hit the same port', () => {
    const sw = switchNode('sw-01|serial-1');
    const port = portNode(sw.id, 'ether5');
    const g = graph([sw], [port]);

    const overlay = buildDriftTopologyOverlay(g, [
      driftItem({ driftItemId: 'low', severity: 'Low' }),
      driftItem({ driftItemId: 'high', severity: 'High' }),
      driftItem({ driftItemId: 'medium', severity: 'Medium' }),
    ]);

    expect(overlay.get(port.id)?.driftItemId).toBe('high');
    expect(overlay.get(port.id)?.severity).toBe('High');
  });

  it('leaves a port with no matching drift item out of the overlay entirely', () => {
    const sw = switchNode('sw-01|serial-1');
    const drifted = portNode(sw.id, 'ether5');
    const clean = portNode(sw.id, 'ether6');
    const g = graph([sw], [drifted, clean]);

    const overlay = buildDriftTopologyOverlay(g, [driftItem()]);

    expect(overlay.has(drifted.id)).toBe(true);
    expect(overlay.has(clean.id)).toBe(false);
  });
});
