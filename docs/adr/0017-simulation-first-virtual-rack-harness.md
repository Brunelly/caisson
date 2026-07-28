# 0017: A simulation-first virtual-rack harness — one definition, two renderers

## Status

Accepted

## Context

Stories #4/#5 already gave each driver its own private, in-process protocol simulator
(`RouterOsApiSimulator`, `RedfishSimulator`) and story #8 gave discovery orchestration a durable
job pipeline — but there was no single, cohesive fixture that stood up a whole virtual rack, ran it
through the real drivers → real correlation → real persistence → real query API, and asserted the
discovered topology against a known ground truth. Task #57's literal wording asked to "containerize the
Redfish simulator"; the existing simulators are instead fast, deterministic, in-process TCP/TLS servers
already proven in CI (no container runtime, no network namespace, sub-second startup) — containerizing
them would trade away exactly the properties story #11 needs (NFR2's "no random MACs/IDs without
seeding" determinism, and a ≤10-minute CI budget) for no corresponding benefit, since nothing about
their behavior requires isolation from the test host.

A second, harder problem: any fixture asserting "the correlation engine reproduced the expected
topology" is only as trustworthy as the independence between what it *feeds* the engine and what it
*expects back*. A fixture that derives its expectation from the same code path that generates the
input (or vice versa) can't catch a regression in either — it would drift in lockstep. And a fixture
whose LLDP evidence is stubbed directly into a driver-level DTO, rather than genuinely round-tripped
over the wire the switch simulator speaks, would let a regression from "correlate on real LLDP" to
"correlate on MAC learning alone" pass silently — a real, previously-seen failure mode in this class of
system, and explicitly a risk this story calls out (the fidelity guard).

## Decision

**One ground-truth definition, two renderers.** `tests/Caisson.VirtualRack.Fixtures/
VirtualRackDefinition.cs` is a pure, fixed-constant C# model (no `Guid.NewGuid()`/`Random` — NFR2)
describing one switch and one server whose wiring deliberately exercises every correlation band:
a NIC learned on exactly one port with a genuine LLDP neighbour on that port (High confidence,
`MacLearnUnique` + `LldpConsistent`), a NIC learned on two ports (ambiguous), a NIC the switch never
reports (`NotSeenInSwitch`), and a switch port with a foreign MAC (unmapped port).
`RouterOsProfileRenderer`/`RedfishProfileRenderer` render that SAME definition into the two simulators'
wire-profile formats; `ExpectedTopologyBuilder` independently hand-derives the correlation result the
real engine must reproduce, from the engine's own documented scoring rules — not by invoking the engine
— so a regression in either the renderers or the engine shows up as a diff (`TopologyDiff`), not a
tautology. This was verified directly: the real `RouterOsSwitchDriver`/`RedfishBmcDriver` and the real
`TopologyCorrelationEngine`, run against the rendered profiles, reproduce `ExpectedTopologyBuilder`
byte-for-byte, including the exact confidence scores.

**In-process simulators, not containers.** The switch/BMC simulators continue to run in-process
(`tests/Caisson.Drivers.Simulators`, extracted in this story from the two per-driver integration test
projects into a shared non-test support library so both the per-driver suites and this harness use the
same code). This is faster, fully deterministic, and the CI-proven pattern from stories #4/#5; it also
sidesteps CHR's licensing/distribution constraints entirely, mirroring the driver stories' own choice
of a protocol-level simulator over a full CHR VM.

**The consolidated harness drives the REAL orchestration path.** `tests/Caisson.VirtualRack.
IntegrationTests`'s `VirtualRackApiFactory` hosts `Caisson.Api` and overrides *only*
`IRackDefinitionProvider` — `AddCaissonOrchestration` already registers the real
`RouterOsSwitchDriverFactory`/`RedfishBmcDriverFactory` unconditionally, so no fake driver is ever
involved. Credentials flow through the same `CAISSON_SWITCH_*`/`CAISSON_BMC_*` environment variables
production reads. The happy-path test triggers a real discovery job, polls it to `Succeeded`, queries
the real read-only graph API, and — because the graph API's wire DTO collapses each mapping to a single
primary reason code — separately reconstructs the full persisted reason-code evidence from
`TopologyCandidateMapping.EvidenceJson` to assert `LldpConsistent` survived discovery → correlation →
**persistence**, not merely correlation.

**A repo-native seeder over an external harness call.** `tests/Caisson.VirtualRack.Seeder` is a plain
console host that repeats the same "real drivers via the real orchestration path" wiring to seed one
rack for the CI/local Angular e2e smoke, printing `E2E_RACK_ID`/`E2E_SEARCH_TERM`/
`E2E_SEARCH_LABEL_PART`. This was chosen over calling the external `mcp-tooling-caisson` runner because
mcp-tooling must never be referenced from this repo; the live API then serves the already-persisted
snapshot read-only and never needs to reach the simulators again.

## Consequences

- A contributor can reach a rendered topology view from a clean checkout in well under 15 minutes with
  no physical hardware: `infra/sim/docker-compose.yml` (Postgres+Redis only) → the seeder → `Caisson.Api`
  → `npm run serve:e2e` — see `docs/simulation-harness.md`.
- The fidelity guard is a genuine property of this design, not an assertion of convenience: because the
  simulators are in-process (no Linux host bridge in the path), the happy-path test's `LldpConsistent`
  requirement cannot pass by accident — a regression to MAC-only correlation fails it. Anyone who later
  wires a *real* CHR through a Linux/Proxmox-style host bridge must independently set
  `group_fwd_mask 0x4000` (LLDP; `+0x0004` for LACP) on that bridge, or LLDP silently never crosses the
  wire and correlation degrades to MAC-only without any test catching it — documented prominently in
  `docs/simulation-harness.md` precisely because it does not carry over automatically from this harness.
- `UnmappedPort` reason codes are computed by the correlation engine but never persisted (`TopologySnapshotMapper`
  intentionally does not create a candidate row for them) — the harness's deep persisted-topology diff
  therefore only asserts unmapped-port *existence*, not its reason code, which is a real, documented
  limit of what survives to the query layer today, not a gap in the harness.
- The two per-driver integration test projects (`Caisson.Drivers.MikroTik.IntegrationTests`,
  `Caisson.Drivers.Redfish.IntegrationTests`) now depend on the shared `Caisson.Drivers.Simulators`
  library instead of owning private copies of the simulator source — a mechanical, behavior-preserving
  move (both suites pass unchanged).
