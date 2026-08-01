using Caisson.Domain.Enums;

namespace Caisson.Infrastructure.Persistence.Auditing;

/// <summary>
/// An already-resolved, secret-scrubbed Tier 1 audit event, ready to stage onto the transactional outbox
/// (story #308, ADR 0064). Deliberately a plain record of scalars rather than exposing
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> or <c>ICorrelationContext</c> — those live in
/// <c>Caisson.Api.Middleware</c>, which <c>Caisson.Orchestration</c>/<c>Caisson.Ingestion</c> cannot
/// reference, so callers resolve actor/correlation at their own edge (the API request, or a background
/// job's per-tick correlation id) and hand in the finished envelope.
/// </summary>
public sealed record AuditEventEnvelope(
    ActorType ActorType,
    string ActorId,
    string Action,
    string TargetType,
    string? TargetId,
    Guid CorrelationId,
    string Result,
    Guid? RackId = null,
    Guid? SnapshotId = null,
    string? DetailsJson = null);
