using System.Reflection;
using Caisson.Domain.Topology;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// M0 guardrails enforced by reflection (NFR5 and the read-only scope): the observed-state model must
/// contain no remediation/desired-state fields and no credential/secret/PII-shaped fields. Finding #27
/// broadens the no-credentials sweep beyond <c>Caisson.Domain</c> to the contract/DTO/options types of
/// <c>Caisson.Api</c>, <c>Caisson.Infrastructure</c> and <c>Caisson.Orchestration</c> — the layers that
/// actually shape the config/wire surface a secret-shaped property name would leak through — and to a
/// broader marker set. It is an allow-list, not a pure deny-list: a contract property whose name merely
/// CONTAINS a marker substring (e.g. <c>IdempotencyKey</c>, <c>EntityStableKey</c> both contain "Key") must
/// be explicitly reviewed and added to <see cref="ReviewedNonSecretProperties"/> rather than silently
/// passing — so a genuinely new secret-shaped property fails loudly instead of blending in.
/// </summary>
public sealed class DomainGuardTests
{
    private static readonly Assembly DomainAssembly = typeof(TopologySnapshot).Assembly;

    // Substrings that would signal write/remediation/desired-state intent leaking into observed state.
    private static readonly string[] RemediationMarkers =
    {
        "Desired", "Target", "Remediat", "Intent", "DesiredConfig", "ConfigIntent",
    };

    // Substrings that would signal a credential/secret/PII field. Broadened per finding #27.
    private static readonly string[] SecretMarkers =
    {
        "Password", "Passwd", "Secret", "Credential", "ApiKey", "PrivateKey", "Passphrase",
        "AccessKey", "AuthToken", "BearerToken",
        "Creds", "Pw", "Bearer", "Sas", "ConnectionString", "Auth", "Token", "Key", "Pin",
    };

    // The audit event's Target* fields name the read-only SUBJECT an audited action addressed (standard
    // audit terminology, mandated by the story-7 data model: "each event includes ... target
    // identifiers"). They are not remediation/desired-state config targets, so they are exempt from the
    // "Target" remediation marker — nothing else in the model is.
    private static readonly HashSet<string> AuditSubjectAllowList = new(StringComparer.Ordinal)
    {
        $"{nameof(TopologyAuditEvent)}.{nameof(TopologyAuditEvent.TargetType)}",
        $"{nameof(TopologyAuditEvent)}.{nameof(TopologyAuditEvent.TargetId)}",
    };

    /// <summary>
    /// Contract/DTO/options properties reviewed and confirmed NOT to carry secret material — they merely
    /// match a broadened marker substring by coincidence (an opaque reference, an id, a boolean flag, or a
    /// well-known config-section name). Any future match must be added here only after review; it is the
    /// explicit "approved" list finding #27 asks for.
    /// </summary>
    private static readonly HashSet<string> ReviewedNonSecretProperties = new(StringComparer.Ordinal)
    {
        // Opaque references — the field's own doc/name says "never the secret", only a lookup key.
        "DeviceDefinitionEntry.CredentialsRef",
        "BmcConnectionOptions.CredentialsRef",
        "SwitchConnectionOptions.CredentialsRef",
        // Idempotency/stable/natural keys — application identifiers, not authentication secrets.
        "DiscoveryJob.IdempotencyKey",
        "TriggerDiscoveryRequest.IdempotencyKey",
        "EntityDetailDto.StableKey",
        "EntityDiffDto.EntityStableKey",
        "UnmappedPortDto.SwitchStableKey",
        "PortAttachmentDto.SwitchStableKey",
        "ServerNodeDto.StableKey",
        "NicNodeDto.StableKey",
        "StableKeyCollision.StableKey",
        "Rack.ExternalKey",
        "Server.ExternalDeviceKey",
        "Switch.ExternalDeviceKey",
        "TopologyEntityDiff.EntityStableKey",
        // "CandidateMappings" incidentally contains the substring "pin" (candidate-map-PIN-gs) — a
        // correlation-result collection, not a PIN/secret.
        "TopologySnapshot.CandidateMappings",
        // Config-section-name constants (the string "SectionName", not a secret value).
        "DiscoveryOrchestrationOptions.SectionName",
        "RackDefinitionOptions.SectionName",
        "RealtimeOptions.SectionName",
        // Cursor/pagination — an opaque, already-non-secret continuation token (finding #21 adds an HMAC
        // to it precisely because it is client-visible, not because it carries a credential).
        "CursorCodec.CursorSeparator",
        // Framework/ASP.NET Core auth wiring flags and scheme names — not secret VALUES.
        "JwtBearerOptions.Authority",
        "AuthenticationSchemeOptions.ClaimsIssuer",
    };

    public static IEnumerable<object[]> ObservedProperties()
    {
        foreach (var type in DomainAssembly.GetTypes().Where(t => t is { IsClass: true, IsEnum: false }))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { type.Name, property.Name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ObservedProperties))]
    public void No_property_implies_remediation_or_desired_state(string typeName, string propertyName)
    {
        if (AuditSubjectAllowList.Contains($"{typeName}.{propertyName}"))
        {
            return;
        }

        RemediationMarkers.Should().NotContain(
            marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} must not imply write/remediation/desired-state intent (M0 is read-only)",
            typeName, propertyName);
    }

    [Theory]
    [MemberData(nameof(ObservedProperties))]
    public void No_property_stores_credentials_or_pii(string typeName, string propertyName)
    {
        if (ReviewedNonSecretProperties.Contains($"{typeName}.{propertyName}"))
        {
            return;
        }

        SecretMarkers.Should().NotContain(
            marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} must not store credentials/secrets/PII (NFR5)", typeName, propertyName);
    }

    /// <summary>
    /// The broadened, cross-assembly sweep (finding #27): every public contract/DTO/options property in
    /// <c>Caisson.Api</c>/<c>Caisson.Infrastructure</c>/<c>Caisson.Orchestration</c> either avoids every
    /// broadened marker, or is a reviewed, explicitly-approved false positive.
    /// </summary>
    public static IEnumerable<object[]> ContractProperties()
    {
        var assemblies = new[]
        {
            typeof(Caisson.Api.Controllers.AuditController).Assembly,
            typeof(Caisson.Infrastructure.Persistence.CaissonDbContext).Assembly,
            typeof(Caisson.Orchestration.Discovery.IDiscoveryOrchestrator).Assembly,
        };

        var contractSuffixes = new[] { "Dto", "Options", "Settings", "Request", "Response", "Event", "Contract" };

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().Where(t =>
                t is { IsClass: true, IsEnum: false, IsPublic: true }
                && contractSuffixes.Any(suffix => t.Name.EndsWith(suffix, StringComparison.Ordinal))))
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    yield return new object[] { type.Name, property.Name };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ContractProperties))]
    public void No_api_infrastructure_or_orchestration_contract_property_stores_a_secret(
        string typeName, string propertyName)
    {
        if (ReviewedNonSecretProperties.Contains($"{typeName}.{propertyName}"))
        {
            return;
        }

        SecretMarkers.Should().NotContain(
            marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} matches a broadened secret marker and is not on the reviewed allow-list — either it " +
            "is a new secret-shaped property (fix it) or a genuine false positive that needs adding to " +
            "ReviewedNonSecretProperties after review (finding #27)",
            typeName, propertyName);
    }
}
