using Caisson.Domain.DesiredState;

namespace Caisson.Drift;

/// <summary>
/// The typed desired-state tree <see cref="DriftEngine"/> computes against: the same shape as
/// <c>Caisson.Infrastructure.Persistence.Queries.DesiredStateVersionTree</c>, but declared here (over
/// only <c>Caisson.Domain</c> types) so the pure engine never needs a project reference to
/// <c>Caisson.Infrastructure</c> (which would pull in EF Core/Npgsql and break the purity guard).
/// <c>DriftComputationService</c> maps the Infrastructure-side tree onto this one field-for-field before
/// calling <see cref="DriftEngine.Compute"/>.
/// </summary>
public sealed record DesiredStateTree(
    DesiredStateVersion Version,
    DesiredRackIntent Rack,
    IReadOnlyList<DesiredSwitchIntent> Switches,
    IReadOnlyList<DesiredPortIntent> Ports);
