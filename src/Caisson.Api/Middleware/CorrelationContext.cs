namespace Caisson.Api.Middleware;

/// <summary>
/// The correlation id resolved for the current request, surfaced so downstream services (e.g. the
/// audit writer) can stamp records created during the request with the same id (AC5). Registered
/// scoped and populated by <see cref="CorrelationIdMiddleware"/>.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>The correlation id for the current request.</summary>
    Guid CorrelationId { get; }
}

/// <summary>Mutable scoped implementation of <see cref="ICorrelationContext"/>.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }
}
