using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.RackDefinitions;

/// <summary>
/// The config-bound <see cref="IRackDefinitionProvider"/> (ADR 0013). It joins the persisted
/// <see cref="Rack"/> (for its stable <see cref="Rack.ExternalKey"/>) to the matching
/// <see cref="RackDefinitionOptions"/> entry and maps each device entry to a secret-free
/// <see cref="DeviceDefinition"/>. Fail-closed when either the rack or its definition is absent.
/// </summary>
public sealed class ConfigurationRackDefinitionProvider : IRackDefinitionProvider
{
    private readonly CaissonDbContext _context;
    private readonly IOptionsMonitor<RackDefinitionOptions> _definitions;
    private readonly IOptionsMonitor<DiscoveryOrchestrationOptions> _options;

    public ConfigurationRackDefinitionProvider(
        CaissonDbContext context,
        IOptionsMonitor<RackDefinitionOptions> definitions,
        IOptionsMonitor<DiscoveryOrchestrationOptions> options)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken)
    {
        var rack = await _context.Racks
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == rackId, cancellationToken);
        if (rack is null)
        {
            throw new RackDefinitionMissingException(rackId);
        }

        var entry = _definitions.CurrentValue.Racks
            .FirstOrDefault(r => string.Equals(r.ExternalKey, rack.ExternalKey, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new RackDefinitionMissingException(rackId);
        }

        var defaultTimeout = TimeSpan.FromSeconds(_options.CurrentValue.DefaultDeviceTimeoutSeconds);
        return new RackDefinition(
            rackId,
            rack.ExternalKey,
            entry.Switches.Select(d => Map(d, defaultTimeout)).ToList(),
            entry.Servers.Select(d => Map(d, defaultTimeout)).ToList());
    }

    private static DeviceDefinition Map(DeviceDefinitionEntry entry, TimeSpan defaultTimeout)
        => new(
            entry.DeviceKey,
            entry.Vendor,
            entry.Model,
            entry.ConnectionKind,
            entry.Host,
            entry.Port,
            entry.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(entry.TimeoutSeconds) : defaultTimeout,
            entry.CredentialsRef);
}
