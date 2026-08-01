using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Infrastructure.Persistence.Auditing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caisson.Api.Startup;

/// <summary>
/// DI registration for the Tier 1 (mandatory-durable) audit outbox dispatcher (story #308, ADR 0064).
/// Binds the validated <see cref="AuditDurabilityOptions"/> and registers <see cref="IMandatoryAuditOutbox"/>
/// (stateless — safe as a singleton) and the <see cref="AuditOutboxDispatcher"/> hosted service.
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

        return services;
    }
}
