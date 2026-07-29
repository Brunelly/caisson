using Caisson.Infrastructure.LiveUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Caisson.Api.Security;

/// <summary>
/// The fail-closed gate for the Redis connection backing live updates (finding #2), mirroring
/// <see cref="JwtAuthorityStartupGuard"/>'s "refuse to boot rather than run misconfigured" shape. An
/// unauthenticated, unencrypted Redis connection is exactly the kind of surface the new HMAC event
/// authenticity check (<see cref="TopologyEventAuthenticity"/>) is defending against downstream of — so
/// letting Redis itself sit open in a real deployment would undercut it. Only runs when the Redis-backed
/// live-updates path is actually enabled (<see cref="RealtimeOptions.IsRedisEnabled(IConfiguration)"/>);
/// the no-Redis dev/CI fallback (ADR 0014) is untouched.
/// </summary>
public static class RedisEventAuthenticityStartupGuard
{
    /// <summary>
    /// Validates the resolved Redis connection string against the host environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown outside Development/Testing when Redis is enabled but the resolved connection has neither a
    /// password nor TLS configured.
    /// </exception>
    public static void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        if (!RealtimeOptions.IsRedisEnabled(configuration, out var connectionString))
        {
            return;
        }

        var parsed = ConfigurationOptions.Parse(connectionString!);
        if (parsed.Ssl || !string.IsNullOrEmpty(parsed.Password))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The configured Redis connection ({RealtimeOptions.RedisConnectionStringEnvVar}) has neither " +
            $"a password nor TLS under ASPNETCORE_ENVIRONMENT='{environment.EnvironmentName}'. Live " +
            "topology events are relayed to every connected client through this connection — refusing to " +
            "start rather than run an unauthenticated, unencrypted pub/sub channel outside Development/Testing.");
    }
}
