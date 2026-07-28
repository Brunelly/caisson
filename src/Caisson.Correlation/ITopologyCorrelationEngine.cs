using Caisson.Correlation.Input;
using Caisson.Correlation.Results;

namespace Caisson.Correlation;

/// <summary>
/// Correlates read-only switch and BMC discovery output into an explainable NIC↔port↔VLAN topology
/// mapping. Implementations MUST be pure, deterministic and side-effect free (no I/O, no clock, no
/// randomness) so the same input always yields byte-identical output and the engine is reusable from
/// unit tests, CI, and future orchestration/persistence layers (NFR1/NFR2).
/// </summary>
public interface ITopologyCorrelationEngine
{
    /// <summary>
    /// Runs correlation over the given snapshot and returns the confident mappings, ambiguous mappings,
    /// and unmapped NICs/ports. Synchronous by design — the engine performs no I/O.
    /// </summary>
    /// <param name="input">The in-memory discovery snapshot to correlate.</param>
    /// <returns>The deterministic correlation result.</returns>
    TopologyCorrelationResult Correlate(TopologyCorrelationInput input);
}
