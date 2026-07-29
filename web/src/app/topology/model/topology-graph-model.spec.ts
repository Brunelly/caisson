import { describe, expect, it } from 'vitest';
import {
  CONFIDENCE_HIGH_THRESHOLD,
  CONFIDENCE_MEDIUM_THRESHOLD,
  classifyNic,
  confidenceBandOf,
  deriveTopologyGraph,
  escapeStableKeySegment,
} from './topology-graph-model';
import type { NicNodeDto, PortAttachmentDto, TopologyGraphDto } from './topology-contracts';

function attachment(partial: Partial<PortAttachmentDto>): PortAttachmentDto {
  return {
    switchStableKey: 'SW-1',
    switchSerial: 'sw1-serial',
    portName: 'ether1',
    confidence: 0.9,
    band: 'High',
    reasonCode: 'MacLearnUnique',
    vlans: [],
    ...partial,
  };
}

function fixture(): TopologyGraphDto {
  const confirmedAttachment = attachment({ portName: 'ether1', confidence: 0.92, vlans: [10] });
  const ambiguousBest = attachment({
    portName: 'ether2',
    confidence: 0.6,
    band: 'Medium',
    vlans: [20],
  });
  const ambiguousOther = attachment({
    portName: 'ether3',
    confidence: 0.55,
    band: 'Medium',
    vlans: [20],
  });

  const confirmedNic: NicNodeDto = {
    stableKey: 'nic:srv-1:eth0',
    name: 'eth0',
    mac: 'aabbccddee01',
    bestAttachment: confirmedAttachment,
    candidates: [confirmedAttachment],
    unmappedReasonCode: null,
  };

  const ambiguousNic: NicNodeDto = {
    stableKey: 'nic:srv-2:eth0',
    name: 'eth0',
    mac: 'aabbccddee02',
    bestAttachment: ambiguousBest,
    candidates: [ambiguousBest, ambiguousOther],
    unmappedReasonCode: null,
  };

  const unmappedNic: NicNodeDto = {
    stableKey: 'nic:srv-2:eth1',
    name: 'eth1',
    mac: 'aabbccddee03',
    bestAttachment: null,
    candidates: [],
    unmappedReasonCode: 'NotSeenInSwitch',
  };

  return {
    snapshotId: 'snap-1',
    version: 3,
    correlationId: 'corr-1',
    servers: [
      { stableKey: 'srv-1', hostname: 'srv-01', bmcUuid: 'uuid-1', nics: [confirmedNic] },
      {
        stableKey: 'srv-2',
        hostname: 'srv-02',
        bmcUuid: 'uuid-2',
        nics: [ambiguousNic, unmappedNic],
      },
    ],
    unmappedPorts: [{ switchStableKey: 'SW-1', switchSerial: 'sw1-serial', portName: 'ether4' }],
  };
}

describe('classifyNic', () => {
  it('classifies a NIC with one candidate as confirmed', () => {
    const nic = fixture().servers[0].nics[0];
    expect(classifyNic(nic)).toBe('confirmed');
  });

  it('classifies a NIC with a best attachment and >1 candidates as ambiguous', () => {
    const nic = fixture().servers[1].nics[0];
    expect(classifyNic(nic)).toBe('ambiguous');
  });

  it('classifies a NIC with no best attachment as unmapped', () => {
    const nic = fixture().servers[1].nics[1];
    expect(classifyNic(nic)).toBe('unmapped');
  });
});

describe('confidenceBandOf', () => {
  it.each([
    [1.0, 'High'],
    [CONFIDENCE_HIGH_THRESHOLD, 'High'],
    [0.79, 'Medium'],
    [CONFIDENCE_MEDIUM_THRESHOLD, 'Medium'],
    [0.49, 'Low'],
    [0, 'Low'],
  ])('bands %f as %s', (confidence, expected) => {
    expect(confidenceBandOf(confidence)).toBe(expected);
  });
});

describe('deriveTopologyGraph', () => {
  it('carries through snapshot identity', () => {
    const model = deriveTopologyGraph(fixture());
    expect(model.snapshotId).toBe('snap-1');
    expect(model.version).toBe(3);
  });

  it('emits one server node per server, labelled by hostname', () => {
    const model = deriveTopologyGraph(fixture());
    expect(model.nodes.servers).toHaveLength(2);
    expect(model.nodes.servers.map((s) => s.label)).toEqual(['srv-01', 'srv-02']);
  });

  it('emits one NIC node per NIC, classified confirmed/ambiguous/unmapped', () => {
    const model = deriveTopologyGraph(fixture());
    expect(model.nodes.nics).toHaveLength(3);

    const states = Object.fromEntries(model.nodes.nics.map((n) => [n.stableKey, n.state]));
    expect(states['nic:srv-1:eth0']).toBe('confirmed');
    expect(states['nic:srv-2:eth0']).toBe('ambiguous');
    expect(states['nic:srv-2:eth1']).toBe('unmapped');

    const unmapped = model.nodes.nics.find((n) => n.stableKey === 'nic:srv-2:eth1');
    expect(unmapped?.unmappedReasonCode).toBe('NotSeenInSwitch');
  });

  it('dedups the switch across every attaching port and the unmapped port', () => {
    const model = deriveTopologyGraph(fixture());
    expect(model.nodes.switches).toHaveLength(1);
    expect(model.nodes.switches[0].stableKey).toBe('SW-1');
  });

  it('gives ports and VLANs a backend-format stableKey (StableKeys.ForSwitchPort/ForVlan), not the D3 join id', () => {
    const model = deriveTopologyGraph(fixture());
    const ether1 = model.nodes.ports.find((p) => p.name === 'ether1')!;
    // StableKeys.ForSwitchPort is "{switchKey}|{portName}" — the entity-detail API expects this,
    // not the render-only "port:SW-1/ether1" join id (a real bug caught while wiring the details panel).
    expect(ether1.stableKey).toBe('SW-1|ether1');
    expect(ether1.stableKey).not.toBe(ether1.id);

    const vlan10 = model.nodes.vlans.find((v) => v.vlanId === 10)!;
    expect(vlan10.stableKey).toBe('10');
  });

  it('derives a port node only for best-attachment ports and unmapped ports, not other candidates', () => {
    const model = deriveTopologyGraph(fixture());
    const portNames = model.nodes.ports.map((p) => p.name).sort();
    // ether3 is a candidate on the ambiguous NIC but never the best attachment, so it is not drawn
    // as a graph node (Q3: only the top candidate edge is drawn; others live in the details panel).
    expect(portNames).toEqual(['ether1', 'ether2', 'ether4']);
  });

  it('classifies port state from the attaching NIC, and unmapped ports as unmapped', () => {
    const model = deriveTopologyGraph(fixture());
    const byName = Object.fromEntries(model.nodes.ports.map((p) => [p.name, p.state]));
    expect(byName['ether1']).toBe('confirmed');
    expect(byName['ether2']).toBe('ambiguous');
    expect(byName['ether4']).toBe('unmapped');
  });

  it('dedups VLAN nodes across ports', () => {
    const model = deriveTopologyGraph(fixture());
    expect(model.nodes.vlans.map((v) => v.vlanId).sort()).toEqual([10, 20]);
  });

  it('emits a server-nic edge for every NIC, including unmapped ones', () => {
    const model = deriveTopologyGraph(fixture());
    const serverNicEdges = model.edges.filter((e) => e.kind === 'server-nic');
    expect(serverNicEdges).toHaveLength(3);
  });

  it('emits a nic-port edge only when a best attachment exists', () => {
    const model = deriveTopologyGraph(fixture());
    const nicPortEdges = model.edges.filter((e) => e.kind === 'nic-port');
    expect(nicPortEdges).toHaveLength(2);
    expect(nicPortEdges.every((e) => e.state !== 'unmapped')).toBe(true);
  });

  it('emits one port-vlan edge per vlan entry on the best attachment', () => {
    const model = deriveTopologyGraph(fixture());
    const portVlanEdges = model.edges.filter((e) => e.kind === 'port-vlan');
    expect(portVlanEdges).toHaveLength(2);
  });

  it('emits no port-vlan edges for unmapped ports (no vlan data on UnmappedPortDto)', () => {
    const model = deriveTopologyGraph(fixture());
    const ether4 = model.nodes.ports.find((p) => p.name === 'ether4')!;
    expect(model.edges.some((e) => e.source === ether4.id)).toBe(false);
  });

  it('finding #31: dedups a port-vlan edge shared by two NICs on the same port/VLAN into exactly one edge', () => {
    const sharedAttachment = attachment({ portName: 'ether1', vlans: [10] });
    const graph: TopologyGraphDto = {
      snapshotId: 'snap-1',
      version: 1,
      correlationId: 'corr-1',
      servers: [
        {
          stableKey: 'srv-1',
          hostname: 'srv-01',
          bmcUuid: 'uuid-1',
          nics: [
            {
              stableKey: 'nic:srv-1:eth0',
              name: 'eth0',
              mac: 'aabbccddee01',
              bestAttachment: sharedAttachment,
              candidates: [sharedAttachment],
              unmappedReasonCode: null,
            },
          ],
        },
        {
          stableKey: 'srv-2',
          hostname: 'srv-02',
          bmcUuid: 'uuid-2',
          nics: [
            {
              stableKey: 'nic:srv-2:eth0',
              name: 'eth0',
              mac: 'aabbccddee02',
              bestAttachment: sharedAttachment,
              candidates: [sharedAttachment],
              unmappedReasonCode: null,
            },
          ],
        },
      ],
      unmappedPorts: [],
    };

    const model = deriveTopologyGraph(graph);
    const portVlanEdges = model.edges.filter((e) => e.kind === 'port-vlan');
    expect(portVlanEdges).toHaveLength(1);
  });
});

describe('escapeStableKeySegment', () => {
  it('leaves an ordinary segment unchanged', () => {
    expect(escapeStableKeySegment('sw-01')).toBe('sw-01');
  });

  it('escapes a literal "%" to "%25"', () => {
    expect(escapeStableKeySegment('sw%01')).toBe('sw%2501');
  });

  it('escapes a literal "|" to "%7C"', () => {
    expect(escapeStableKeySegment('sw|01')).toBe('sw%7C01');
  });

  it('escapes "%" before "|" so a literal "%" is never mistaken for a partial escape', () => {
    // A naive %-then-| order applied the other way round would turn "a%7Cb" (a literal percent
    // followed by "7Cb") into something indistinguishable from an already-escaped "|" — escaping '%'
    // first guarantees the only '%' sequences in the output are ones this function itself produced.
    expect(escapeStableKeySegment('a%7Cb')).toBe('a%257Cb');
  });
});
