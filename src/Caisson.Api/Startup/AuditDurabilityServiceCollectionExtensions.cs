using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Infrastructure.Persistence.Auditing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Api.Startup;

/// <summary>
/// DI registration for the Tier 1 (mandatory-durable) audit outbox dispatcher and the Tier 2
/// (durable-first-N + bounded counter) denial audit pipeline (story #308, ADR 0064). Binds the validated
/// <see cref="AuditDurabilityOptions"/> and registers <see cref="IMandatoryAuditOutbox"/> (stateless —
/// safe as a singleton), the <see cref="AuditOutboxDispatcher"/> hosted service,
/// <see cref="IAuthorizationDenialAuditWriter"/>, the shared <see cref="DenialOverflowAccumulator"/>
/// singleton, and the <see cref="AuditDenialFlushService"/> hosted service.
/// </summary>
public static class AuditDurabilityServiceCollectionExtensions
{
    public static IServiceCollection AddCaissonAuditDurability(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AuditDurabilityOptions>()
            .Bind(configuration.GetSection(AuditDurabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IMandatoryAuditOutbox, MandatoryAuditOutbox>();
        services.TryAddSingleton<AuditOutboxMetrics>();
        services.AddHostedService<AuditOutboxDispatcher>();

        // Tier 2 (ADR 0064): the accumulator is a singleton (per-instance overflow state, by design — see
        // its own remarks), the writer is scoped (needs the per-request CaissonDbContext), and the flush
        // service periodically persists the accumulator's tallies.
        services.TryAddSingleton<DenialOverflowAccumulator>();
        services.TryAddScoped<IAuthorizationDenialAuditWriter, AuthorizationDenialAuditWriter>();
        services.AddHostedService<AuditDenialFlushService>();

        return services;
    }
}
