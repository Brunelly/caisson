# Topology correlation: scoring, reason codes and heuristics

The topology correlation engine (`Caisson.Correlation`) infers **which server NIC is attached to which
switch port, and that port's VLANs**, from read-only discovery output. It is a pure, deterministic,
side-effect-free function — no network, no database, no clock, no randomness — so it is fully unit-testable
with synthetic inputs and reusable by future orchestration/persistence layers. See
[ADR 0010](adr/0010-topology-correlation-engine.md) for the design rationale.

## Inputs and outputs

Inputs are the story-3 driver info records, wrapped for the engine:

- `SwitchTopologySnapshot(SwitchId, Device?, Ports, LldpNeighbours, BridgeHosts, Vlans)`
- `ServerNicSnapshot(ServerId, System?, Nics)`
- `TopologyCorrelationInput(Switches, Servers)`

`ITopologyCorrelationEngine.Correlate(input)` returns `TopologyCorrelationResult`:

| Collection | Item | Meaning |
|------------|------|---------|
| `Mappings` | `NicPortMapping` — `ServerId`, `NicName`, `Mac`, `Port` | Confident 1:1 NIC→port attachment |
| `AmbiguousMappings` | `AmbiguousNicMapping` — `ServerId`, `NicName`, `Mac`, `Candidates` | A NIC with **>1** candidate port |
| `UnmappedNics` | `UnmappedNic` — `ServerId`, `NicName`, `ReasonCodes` | A NIC that could not be placed |
| `UnmappedPorts` | `UnmappedPort` — `SwitchId`, `PortName`, `ReasonCodes` | A port with signal but no NIC |

Each `PortCandidate` carries `SwitchId`, `PortName`, `Confidence` (`ConfidenceScore`), `Vlans`, and
`ReasonCodes`. Every returned record carries **at least one reason code** (NFR4).

## Reason-code catalogue

Reason codes reuse the shared `Caisson.Domain.ReasonCode` enum (append-only, each ≤ 32 chars).

| Reason code | Emitted when |
|-------------|--------------|
| `MacLearnUnique` | The MAC is the sole host learned on an access/edge port (strongest attachment signal). |
| `LldpConsistent` | The port has an LLDP neighbour that does not contradict the mapping. |
| `MissingLldp` | The port has no LLDP neighbour at all (bridge-table-only fallback). |
| `LldpContradicts` | The port's LLDP neighbour identifies a different device (a peer switch/uplink). |
| `ConflictingMacEvidence` | Evidence disagrees — a contradicting LLDP neighbour, or competing non-LAG candidates. |
| `SeenOnTrunkPort` | The MAC was seen only on a trunk/uplink port — not a reliable direct attachment. |
| `MultipleMacPorts` | The MAC was learned on more than one candidate port (ambiguous). |
| `DuplicateMac` | Companion to `MultipleMacPorts`: the same MAC appears on multiple ports. |
| `PortsInSameLag` | Candidates share one switch and identical VLAN config — treated as one LAG. |
| `VlanInferred` | One or more VLANs were inferred for the port from `Pvid`/tagged VLANs. |
| `VlanContextMissing` | No VLAN/bridge context was available for the port. |
| `NotSeenInSwitch` | The NIC MAC was never observed in any switch bridge table (unmapped NIC). |
| `NotSeenInBmc` | A port learned a MAC that no discovered BMC NIC owns (unmapped port). |
| `PortNeighbourUnknown` | A port has an LLDP neighbour that maps to no known NIC (unmapped port). |
| `ParseError` | The BMC reported a NIC but not a parseable MAC (unmapped NIC). |
| `StaleData` | **Reserved** — recency scoring is a deferred follow-up (no timestamp on `BridgeHostEntry`). |

## Scoring weights

Additive, rule-based weights (constants live in `CorrelationScoring`). Scores are `Math.Clamp`ed to
`[0,1]` and rounded to 6 decimal places before a `ConfidenceScore` is built.

| Situation | Base | LLDP adjustment | Typical total |
|-----------|------|-----------------|---------------|
| Unique MAC on an access port, consistent LLDP | `0.70` | `+0.25` | **`0.95`** (High) |
| Unique MAC on an access port, no LLDP | `0.70` | `+0.15` | **`0.85`** (High) |
| MAC seen only on a trunk/uplink port | `0.15` (flat) | — | **`0.15`** (Low) |
| LAG members (same switch, identical VLAN config) | `0.65` (flat, equal) | — | **`0.65`** (Medium) |
| Non-LAG competing candidate | candidate score `× 0.60` | — | below a confident mapping |

## Confidence bands

The story's answered question fixes the bands (helper `ConfidenceBands`, mirrored by the e2e harness):

- **High** ≥ `0.8`
- **Medium** `0.5`–`0.79`
- **Low** < `0.5` (default)

The domain `ConfidenceScore` stays band-agnostic; banding is a presentation-layer concern.

## Access-vs-trunk classification

Distinguishing edge/access ports from trunk/uplink ports is the crown-jewel disambiguation — a MAC seen on
a trunk is transiting, not directly attached. A port is classified **trunk** when **any** of these hold
(LLDP peer-switch first, the others as fallback):

1. **LLDP peer-switch** — an LLDP neighbour's `ChassisId`, `SystemName`, or `MgmtAddress` identifies another
   switch in the same snapshot (matched against each switch's `SwitchId` / `ManagementIp` / `Serial`).
2. **Multi-VLAN tagging** — the port tags more than one VLAN (`TaggedVlans.Count > 1`); an access port
   normally carries only its `Pvid`.
3. **High learned-MAC count** — the port has learned more than `TrunkMacCountThreshold` (**4**) distinct
   MACs.

Otherwise the port is **access**. When a MAC is seen on both an access and a trunk port, the access port
wins and the trunk sighting is demoted out of the mapping.

## LAG detection (and its limitation)

Per the answered question, when all of a NIC's candidate ports are on **one switch** and share an
**identical `(Pvid, sorted TaggedVlans)` signature**, they are treated as members of a single LAG:
`PortsInSameLag` is added and all members receive an **equal boosted** Medium-band score. This is a
VLAN-config-shape heuristic, **not** real LACP membership — genuine bond state would require richer driver
evidence (a follow-up; see ADR 0010).

## Determinism and tie-breaks

For identical inputs the output is byte-identical across runs (NFR2):

- `Mappings` / `AmbiguousMappings` / `UnmappedNics` sorted by `(ServerId, NicName)` ordinal.
- `UnmappedPorts` sorted by `(SwitchId, PortName)` ordinal.
- Candidates within an ambiguous mapping sorted by `(Confidence desc, SwitchId, PortName)` ordinal.
- Scores are rounded (6 dp) **before** ordering so equal-evidence ties break identically every run.
- No reliance on hash/dictionary enumeration order anywhere.

## Worked examples

- **Clean 1:1** — NIC MAC learned on one access port `ether1` (`Pvid 10`) with a consistent LLDP neighbour
  → one `NicPortMapping`, VLAN `[10]`, `0.95` High, reasons `MacLearnUnique, LldpConsistent, VlanInferred`.
- **Missing-LLDP fallback** — same but no LLDP → still maps, `0.85` High, `MacLearnUnique, MissingLldp`.
- **Duplicate MAC** — MAC on `sw1/ether1` and `sw2/ether5`, LLDP does not disambiguate → one
  `AmbiguousNicMapping` with both candidates, each `MultipleMacPorts, DuplicateMac, ConflictingMacEvidence`,
  ordered by confidence then port key.
- **LAG** — MAC on `sw1/ether1` and `sw1/ether2`, identical VLAN config → ambiguous with `PortsInSameLag`
  and equal `0.65` Medium scores.
- **Trunk vs access** — MAC on access `ether1` and multi-VLAN trunk `ether24` → maps to `ether1` (High);
  the trunk sighting is demoted and `ether24` is excluded from `UnmappedPorts`.
- **Trunk-only** — MAC seen only on trunk `ether48` → a `NicPortMapping` at `0.15` Low with
  `SeenOnTrunkPort`.
- **VLAN present vs absent** — `Pvid 42` → `Vlans [42]`, `VlanInferred`; no VLAN context → empty `Vlans`,
  `VlanContextMissing`.
- **Unmapped NIC / port** — NIC MAC never in any bridge table → `UnmappedNic` `NotSeenInSwitch`; MAC-less
  NIC → `ParseError`. Access port with an unowned learned MAC and an unknown LLDP neighbour → `UnmappedPort`
  `NotSeenInBmc, PortNeighbourUnknown`.
