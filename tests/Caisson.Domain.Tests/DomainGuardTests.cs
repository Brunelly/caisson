using System.Reflection;
using Caisson.Domain.Topology;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// M0 guardrails enforced by reflection (NFR5 and the read-only scope): the OBSERVED-state model must
/// contain no remediation/desired-state fields and no credential/secret/PII-shaped fields. Finding #27
/// broadens the no-credentials sweep beyond <c>Caisson.Domain</c> to the contract/DTO/options types of
/// <c>Caisson.Api</c>, <c>Caisson.Infrastructure</c> and <c>Caisson.Orchestration</c> — the layers that
/// actually shape the config/wire surface a secret-shaped property name would leak through — and to a
/// broader marker set. It is an allow-list, not a pure deny-list: a contract property whose name merely
/// CONTAINS a marker substring (e.g. <c>IdempotencyKey</c>, <c>EntityStableKey</c> both contain "Key") must
/// be explicitly reviewed and added to <see cref="ReviewedNonSecretProperties"/> rather than silently
/// passing — so a genuinely new secret-shaped property fails loudly instead of blending in.
/// </summary>
/// <remarks>
/// Story #62 deliberately introduces a real, first-class desired-state model under the
/// <c>Caisson.Domain.DesiredState</c> namespace. That namespace is exempt from the remediation-marker
/// sweep below (M0's "no desired-state fields at all" guardrail only ever applied to OBSERVED state —
/// see <c>CLAUDE.md</c>'s Guardrails section) but is NOT exempt from the credential/secret sweep, and
/// gets its own read-only-boundary and secret-marker checks in <see cref="Caisson.Domain.Tests.DesiredStateGuardTests"/>.
/// </remarks>
public sealed class DomainGuardTests
{
    private static readonly Assembly DomainAssembly = typeof(TopologySnapshot).Assembly;

    private const string DesiredStateNamespace = "Caisson.Domain.DesiredState";

    // Story #64: drift items legitimately reference desired-state identity (e.g. DriftReport.
    // DesiredRevisionId) as a READ-side cross-reference, not observed-state gaining a write/remediation
    // field — the same false-positive class the DesiredState exemption above covers. Drift gets its own
    // read-only-boundary checks in Caisson.Domain.Tests.DriftGuardTests.
    private const string DriftNamespace = "Caisson.Domain.Drift";

    // Story #65: Caisson.Domain.Drift.Apply is the first genuinely write/remediation model in the domain
    // — DriftApplyJob legitimately carries "Desired"-named fields (DesiredVlanId) because it IS the
    // apply/remediation job the M0 "no desired-state fields" guardrail was written to keep OUT of
    // observed state. A distinct namespace (not folded into DriftNamespace above) so the exemption's
    // rationale stays explicit and scoped to exactly the new write-path entities.
    private const string DriftApplyNamespace = "Caisson.Domain.Drift.Apply";

    // Story #168: Caisson.Domain.NetworkConfig IS the network-intent authoring model itself (a VLAN
    // catalogue plus per-port access-VLAN intent) — like DesiredState/DriftApply above, it legitimately
    // carries "Intent"-named fields (e.g. RackNetworkIntent.IntentJson) because authoring intent is
    // exactly what it exists to do. The M0 "no desired-state/intent fields" guardrail only ever applied to
    // OBSERVED state (see CLAUDE.md's Guardrails section).
    private const string NetworkConfigNamespace = "Caisson.Domain.NetworkConfig";

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
        // Story #64: same *.StableKey/EntityStableKey rationale — a natural-key identifier, not a secret.
        "DriftItemDto.SubjectKey",
        "UnmappedPortDto.SwitchStableKey",
        "PortAttachmentDto.SwitchStableKey",
        "ServerNodeDto.StableKey",
        "NicNodeDto.StableKey",
        "StableKeyCollision.StableKey",
        "Rack.ExternalKey",
        // bug-231: the rack-catalogue list DTO's copy of Rack.ExternalKey — same natural-key rationale.
        "RackSummaryDto.ExternalKey",
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
        // Story #62 desired-state model: "CommitAuthor" incidentally contains "Auth" (commit-AUTH-or) —
        // it is the Git commit's author name, not an authentication credential.
        "DesiredStateIngestionRun.CommitAuthor",
        // Story #62 desired-state tree stable identifiers — same rationale as the other *.StableKey
        // entries above, not authentication secrets.
        "DesiredRackIntent.StableKey",
        "DesiredSwitchIntent.StableKey",
        "DesiredPortIntent.StableKey",
        // Story #62 API contracts: same two false positives, now on the DTO shapes returned to clients.
        "DesiredStateIngestionRunSummaryDto.CommitAuthor",
        "DesiredRackIntentDto.StableKey",
        "DesiredSwitchIntentDto.StableKey",
        "DesiredPortIntentDto.StableKey",
        // Story #63: the git commit author's name/email/timestamp on the persisted revision and its read
        // DTOs — same "Author contains Auth" false positive as DesiredStateIngestionRun.CommitAuthor
        // above, not an authentication credential.
        "DesiredStateVersion.AuthorName",
        "DesiredStateVersion.AuthorEmail",
        "DesiredStateVersion.AuthorWhenUtc",
        "DesiredStateActiveDto.AuthorName",
        "DesiredStateActiveDto.AuthorEmail",
        "DesiredStateActiveDto.AuthorWhenUtc",
        "DesiredStateRevisionMetadataDto.AuthorName",
        "DesiredStateRevisionMetadataDto.AuthorEmail",
        "DesiredStateRevisionMetadataDto.AuthorWhenUtc",
        "DesiredStateRevisionDetailDto.AuthorName",
        "DesiredStateRevisionDetailDto.AuthorEmail",
        "DesiredStateRevisionDetailDto.AuthorWhenUtc",
        // Story #64: same *.StableKey/EntityStableKey rationale — a natural-key identifier, not a secret.
        "DriftItem.SubjectKey",
        // Story #65: DeviceDefinition.DeviceKey's stable identifier, resolved during revalidation — same
        // *.ExternalDeviceKey/StableKey rationale as the entries above, not an authentication secret.
        "DriftApplyJob.SwitchDeviceKey",
        // Story #65: same false positive on the API contract DTO that surfaces the job's resolved target.
        "DriftApplyJobDetailDto.SwitchDeviceKey",
        // Story #65: same *.SubjectKey rationale as DriftItem.SubjectKey above, not a secret.
        "DriftApplyJob.SubjectKey",
        // Story #168: same *.StableKey/*.SwitchStableKey rationale as the topology DTOs above — a
        // discovered switch's stable identifier, not an authentication secret.
        "PortAccessIntent.SwitchStableKey",
        "PortAccessIntentDto.SwitchStableKey",
        "SwitchInventoryDto.StableKey",
        "SwitchPortInventoryDto.StableKey",
        // Story #170: pre-flight rack-inventory + issue entity references — the same discovered switch/port
        // stable-identifier rationale as the topology *.StableKey entries above, not authentication secrets.
        "InventorySwitch.StableKey",
        "InventoryPort.StableKey",
        "EntityRef.SwitchStableKey",
        "EntityRefDto.SwitchStableKey",
        // Story #172: non-secret GitHub PR options/settings — the PAT NAME (not value), the vault URI, and
        // the auth MODE enum. The secret value itself is never bound here; it resolves at runtime through
        // IGitCredentialProvider (Key Vault via managed identity, ADR 0059), never through these POCOs.
        "GitHubOptions.PatSecretName",
        "GitHubOptions.KeyVaultUri",
        "GitHubOptions.AuthMode",
        "KeyVaultCredentialSettings.KeyVaultUri",
        "KeyVaultCredentialSettings.SecretName",
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

    /// <summary>
    /// Same as <see cref="ObservedProperties"/> but excludes <c>Caisson.Domain.DesiredState</c> — the
    /// remediation-marker sweep exists to keep OBSERVED state read-only-shaped; it was never meant to
    /// (and now cannot, since story #62 legitimately adds "Intent"/"Desired"-named types) apply to the
    /// first-class desired-state model itself.
    /// </summary>
    public static IEnumerable<object[]> ObservedPropertiesExcludingDesiredState()
    {
        foreach (var type in DomainAssembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsEnum: false }
                         && t.Namespace != DesiredStateNamespace && t.Namespace != DriftNamespace
                         && t.Namespace != DriftApplyNamespace && t.Namespace != NetworkConfigNamespace))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { type.Name, property.Name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ObservedPropertiesExcludingDesiredState))]
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
    /// <c>Caisson.Api</c>/<c>Caisson.Infrastructure</c>/<c>Caisson.Orchestration</c>/<c>Caisson.Ingestion</c>
    /// either avoids every broadened marker, or is a reviewed, explicitly-approved false positive.
    /// </summary>
    public static IEnumerable<object[]> ContractProperties()
    {
        var assemblies = new[]
        {
            typeof(Caisson.Api.Controllers.AuditController).Assembly,
            typeof(Caisson.Infrastructure.Persistence.CaissonDbContext).Assembly,
            typeof(Caisson.Orchestration.Discovery.IDiscoveryOrchestrator).Assembly,
            typeof(Caisson.Ingestion.Options.GitIngestionOptions).Assembly,
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
