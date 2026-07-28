// Human copy for Caisson.Domain.Enums.ReasonCode (backend enum, string names on the wire — never
// invent a code that isn't defined there). Falls back to the raw code for anything not yet listed here
// (e.g. a future append-only enum value) rather than hiding it.
const REASON_CODE_LABELS: Record<string, string> = {
  Unknown: 'No specific reason recorded',
  NotSeenInSwitch: 'Seen in BMC inventory but not on any switch',
  NotSeenInBmc: 'Seen on a switch but not in any BMC inventory',
  MissingLldp: 'No LLDP evidence was available',
  ConflictingMacEvidence: 'Multiple sources disagree about where this MAC lives',
  DuplicateMac: 'The same MAC was claimed by more than one switch port',
  StaleData: 'The evidence used for correlation was stale',
  DeviceUnreachable: 'The device could not be reached during discovery',
  AuthenticationFailed: 'Authentication to the device failed during discovery',
  ParseError: 'The source data could not be parsed',
  FallbackSource: 'Read via a fallback source rather than the primary one',
  MacLearnUnique: 'Learned on exactly one access/edge port',
  LldpConsistent: 'An LLDP neighbour is consistent with this mapping',
  LldpContradicts: 'An LLDP neighbour identifies a different device',
  MultipleMacPorts: 'This MAC was learned on more than one candidate port',
  PortsInSameLag: 'Candidate ports share a switch and VLAN config, look like one LAG',
  SeenOnTrunkPort: 'Only seen on a trunk/uplink port, not a reliable direct attachment',
  VlanInferred: 'VLAN membership was inferred from the port’s PVID/tagged-VLAN context',
  VlanContextMissing: 'No VLAN/bridge context was available for this port',
  PortNeighbourUnknown: 'This port has an LLDP neighbour that could not be correlated to a NIC',
};

export function reasonCodeLabel(reasonCode: string | null | undefined): string | null {
  if (!reasonCode) {
    return null;
  }
  return REASON_CODE_LABELS[reasonCode] ?? reasonCode;
}
