using System.Reflection;
using Caisson.Domain.DesiredState;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Story #62 legitimately introduces "Intent"/"Desired"-named types, so
/// <see cref="Caisson.Domain.Tests.DomainGuardTests"/> exempts the
/// <c>Caisson.Domain.DesiredState</c> namespace from its remediation-marker sweep. This test asserts
/// the invariants that are STILL supposed to hold for that namespace: no credential/secret-shaped
/// field (M1 only reads Git and stores results — the safety-boundary doc explicitly says "no device
/// credentials"), and no hardware-write/apply/reconcile-shaped method or field, keeping this story's
/// read-only boundary (parse → validate → materialise, no device writes, no drift/apply) compile-time
/// checkable the same way <c>SafetyBoundaryGuardTests</c> checks the driver ReadOnly namespace.
/// </summary>
public sealed class DesiredStateGuardTests
{
    private static readonly Assembly DomainAssembly = typeof(DesiredStateIngestionRun).Assembly;
    private const string DesiredStateNamespace = "Caisson.Domain.DesiredState";

    private static readonly string[] SecretMarkers =
    {
        "Password", "Passwd", "Secret", "Credential", "ApiKey", "PrivateKey", "Passphrase",
        "AccessKey", "AuthToken", "BearerToken", "Creds", "Pw", "Bearer", "Sas", "ConnectionString",
        "Token", "Pin",
    };

    // Hardware-write/apply/reconcile-shaped verbs: this story is strictly read-only (parse + validate +
    // materialise only). Drift computation and safe-apply are later, separate stories.
    private static readonly string[] HardwareWriteMarkers =
    {
        "Applied", "Apply", "Executed", "Execute", "Reconcil", "Rollback", "PushToDevice", "Configure",
    };

    private static readonly HashSet<string> ReviewedNonSecretProperties = new(StringComparer.Ordinal)
    {
        // The Git commit author's display name, not an authentication credential.
        "DesiredStateIngestionRun.CommitAuthor",
    };

    public static IEnumerable<object[]> DesiredStateTypes()
        => DomainAssembly.GetTypes()
            .Where(t => t.Namespace == DesiredStateNamespace && t is { IsClass: true, IsEnum: false })
            .Select(t => new object[] { t });

    public static IEnumerable<object[]> DesiredStateProperties()
    {
        foreach (var type in DomainAssembly.GetTypes()
                     .Where(t => t.Namespace == DesiredStateNamespace && t is { IsClass: true, IsEnum: false }))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { type.Name, property.Name };
            }
        }
    }

    public static IEnumerable<object[]> DesiredStateMethods()
    {
        foreach (var type in DomainAssembly.GetTypes()
                     .Where(t => t.Namespace == DesiredStateNamespace && t is { IsClass: true, IsEnum: false }))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue; // property accessors etc.
                }

                yield return new object[] { type.Name, method.Name };
            }
        }
    }

    [Fact]
    public void DesiredState_namespace_contains_the_expected_entities()
    {
        // Guards against the enumerations above silently covering zero types if the namespace ever
        // changes/moves.
        DesiredStateTypes().Should().NotBeEmpty();
        DesiredStateProperties().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(DesiredStateProperties))]
    public void No_desired_state_property_stores_credentials_or_pii(string typeName, string propertyName)
    {
        if (ReviewedNonSecretProperties.Contains($"{typeName}.{propertyName}"))
        {
            return;
        }

        SecretMarkers.Should().NotContain(
            marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} must not store credentials/secrets/PII — desired-state ingestion only reads Git " +
            "and stores results (safety boundary)", typeName, propertyName);
    }

    [Theory]
    [MemberData(nameof(DesiredStateMethods))]
    public void No_desired_state_method_implies_a_hardware_write(string typeName, string methodName)
    {
        HardwareWriteMarkers.Should().NotContain(
            marker => methodName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} must not imply a device write/apply/reconcile operation — story #62 is strictly " +
            "read-only (parse, validate, materialise only; apply/reconcile are later stories)",
            typeName, methodName);
    }
}
