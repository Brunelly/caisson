using MikroTikSlug = Caisson.Drivers.MikroTik.Credentials.CredentialReferenceSlug;
using RedfishSlug = Caisson.Drivers.Redfish.Credentials.CredentialReferenceSlug;

namespace Caisson.Orchestration.RackDefinitions;

/// <summary>
/// Fail-closed startup validation over the whole config-bound <see cref="RackDefinitionOptions"/> (finding
/// #33), mirroring <c>TestAuthStartupGuard</c>'s "refuse to boot rather than run misconfigured" shape.
/// <c>CredentialReferenceSlug.Validate</c> rejects an individual malformed/empty <c>CredentialsRef</c>
/// (each driver's copy is applied to its own device kind, since a switch's TLS-trust/credential env
/// namespace is <c>CAISSON_SWITCH</c> and a server's is <c>CAISSON_BMC</c> — collisions are meaningful only
/// within one namespace). This additionally catches the case the per-reference regex cannot close by
/// construction: two syntactically distinct references (differing only in case, since the regex already
/// forbids the separator characters that used to cause other collisions) that normalize to the same slug —
/// which would otherwise silently share one secret-store entry and one TLS-trust decision.
/// </summary>
public static class RackDefinitionValidation
{
    private const string SwitchEnvPrefix = "CAISSON_SWITCH";

    /// <exception cref="InvalidOperationException">
    /// Thrown when any device's <c>CredentialsRef</c> is invalid, when two devices of the same kind in the
    /// same configuration normalize to the same slug but differ as strings, or when a switch pairs a
    /// non-TLS transport with a configured certificate fingerprint pin (finding #8 — a pin on a connection
    /// that never negotiates TLS is silently meaningless).
    /// </exception>
    public static void Validate(RackDefinitionOptions options, Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var readEnv = readEnvironment ?? Environment.GetEnvironmentVariable;

        foreach (var rack in options.Racks)
        {
            foreach (var device in rack.Switches)
            {
                MikroTikSlug.Validate(device.CredentialsRef, device.DeviceKey);
            }

            foreach (var device in rack.Servers)
            {
                RedfishSlug.Validate(device.CredentialsRef, device.DeviceKey);
            }

            RejectSlugCollisions(rack.ExternalKey, "switch", rack.Switches, MikroTikSlug.Normalize);
            RejectSlugCollisions(rack.ExternalKey, "server", rack.Servers, RedfishSlug.Normalize);
            RejectFingerprintOnNonTlsSwitches(rack.ExternalKey, rack.Switches, readEnv);
        }
    }

    private static void RejectFingerprintOnNonTlsSwitches(
        string rackKey, List<DeviceDefinitionEntry> switches, Func<string, string?> readEnv)
    {
        foreach (var device in switches)
        {
            if (device.UseTls)
            {
                continue;
            }

            var slug = MikroTikSlug.Normalize(device.CredentialsRef);
            var fingerprint = readEnv($"{SwitchEnvPrefix}_{slug}_TLS_FINGERPRINT")
                ?? readEnv($"{SwitchEnvPrefix}_TLS_FINGERPRINT");
            if (!string.IsNullOrWhiteSpace(fingerprint))
            {
                throw new InvalidOperationException(
                    $"Rack '{rackKey}' switch '{device.DeviceKey}' has a {SwitchEnvPrefix}_{slug}_TLS_FINGERPRINT " +
                    "configured but UseTls is false. A certificate pin on a non-TLS connection is silently " +
                    "meaningless — refusing to start rather than let the operator believe the pin is enforced.");
            }
        }
    }

    private static void RejectSlugCollisions(
        string rackKey, string kind, List<DeviceDefinitionEntry> devices, Func<string, string> normalize)
    {
        var colliding = devices
            .GroupBy(d => normalize(d.CredentialsRef))
            .Where(g => g.Select(d => d.CredentialsRef).Distinct(StringComparer.Ordinal).Count() > 1);

        foreach (var group in colliding)
        {
            var distinctRefs = string.Join(", ", group.Select(d => $"'{d.CredentialsRef}'").Distinct());
            throw new InvalidOperationException(
                $"Rack '{rackKey}' has {kind} devices whose CredentialsRef values ({distinctRefs}) normalize to " +
                $"the same slug '{group.Key}'. Distinct devices must use distinct credential references — " +
                "refusing to start rather than let two devices silently share one secret and TLS-trust decision.");
        }
    }
}
