using System.Reflection;
using Caisson.Domain.Topology;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// M0 guardrails enforced by reflection (NFR5 and the read-only scope): the observed-state model must
/// contain no remediation/desired-state fields and no credential/secret/PII-shaped fields.
/// </summary>
public sealed class DomainGuardTests
{
    private static readonly Assembly DomainAssembly = typeof(TopologySnapshot).Assembly;

    // Substrings that would signal write/remediation/desired-state intent leaking into observed state.
    private static readonly string[] RemediationMarkers =
    {
        "Desired", "Target", "Remediat", "Intent", "DesiredConfig", "ConfigIntent",
    };

    // Substrings that would signal a credential/secret/PII field.
    private static readonly string[] SecretMarkers =
    {
        "Password", "Passwd", "Secret", "Credential", "ApiKey", "PrivateKey", "Passphrase",
        "AccessKey", "AuthToken", "BearerToken",
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
        SecretMarkers.Should().NotContain(
            marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} must not store credentials/secrets/PII (NFR5)", typeName, propertyName);
    }
}
