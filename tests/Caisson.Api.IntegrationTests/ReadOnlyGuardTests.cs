using System.Reflection;
using Caisson.Api.Controllers;
using Caisson.Api.Realtime.Hubs;
using Caisson.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Reflection guard for the read-only safety boundary (NFR1). The boundary is about drivers and
/// HTTP-writes-to-devices, not control-plane HTTP verbs (ADR 0013): the API assembly still references no
/// driver assembly, the read (topology/audit) controllers stay GET-only, and the only non-GET actions
/// are the policy-gated discovery control-plane endpoints. Runs with no database.
/// </summary>
public sealed class ReadOnlyGuardTests
{
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    // Explicit per-controller allow-list of controllers permitted to expose non-GET actions (story #8,
    // story #62).
    private static readonly HashSet<string> NonGetControllerAllowList = new(StringComparer.Ordinal)
    {
        nameof(DiscoveryJobsController),
        nameof(DiscoveryJobDetailController),
        nameof(RackDiscoveryScheduleController),
        nameof(GitWebhookController),
        // Story #65: the drift-apply endpoint — the first destructive, device-mutating write in the API.
        nameof(DriftApplyController),
        // Story #168/#176: network-intent authoring (PUT save, POST validate) — GET stays read-only.
        nameof(NetworkIntentController),
        // Story #169: desired-state YAML round-trip (POST parse, POST render) — both NetworkConfigAuthor-gated.
        nameof(DesiredStateRoundTripController),
        // Story #170: pre-flight validation (POST preflight-validate) and the gated PR-creation endpoint
        // (POST prs) — both NetworkConfigAuthor-gated; side-effect-free except the audit write.
        nameof(DesiredStatePreflightController),
        nameof(DesiredStatePrController),
        // Story #171: the impact-preview POST is a read-shaped-but-side-effecting cache write, gated by
        // TopologyRead so Read Only users can preview (ADR 0055) — allow-listed here and exempted from the
        // WritePolicies check below (like the HMAC webhook), since it carries a read policy, not a write one.
        nameof(DesiredStateImpactPreviewController),
    };

    // Story #171: controllers whose non-GET actions are deliberately gated by a READ policy (TopologyRead)
    // rather than a write policy — a read-shaped preview that happens to persist a cache row (ADR 0055).
    private static readonly HashSet<string> ReadGatedPreviewControllers = new(StringComparer.Ordinal)
    {
        nameof(DesiredStateImpactPreviewController),
    };

    // The policies a non-GET action must be gated by (fail-closed).
    private static readonly HashSet<string> WritePolicies = new(StringComparer.Ordinal)
    {
        AuthorizationPolicies.DiscoveryTrigger,
        AuthorizationPolicies.ScheduleManage,
        AuthorizationPolicies.DriftApply,
        AuthorizationPolicies.NetworkConfigAuthor,
    };

    // Story #62: the Git webhook endpoint is deliberately [AllowAnonymous] — the HMAC signature over
    // the raw body (ADR 0026) IS the authentication, not a bearer-token RBAC policy — so it is exempt
    // from the WritePolicies check below (which only applies to the RBAC-gated discovery write actions).
    private static readonly HashSet<string> HmacAuthenticatedControllers = new(StringComparer.Ordinal)
    {
        nameof(GitWebhookController),
    };

    [Fact]
    public void Api_references_no_driver_assembly()
    {
        var referenced = ApiAssembly.GetReferencedAssemblies().Select(a => a.Name);

        referenced.Should().NotContain(name => name != null && name.StartsWith("Caisson.Drivers", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_controllers_are_get_only_and_writes_are_confined_and_policy_gated()
    {
        var controllers = ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        controllers.Should().NotBeEmpty();

        foreach (var controller in controllers)
        {
            var isReadOnlyController = typeof(ReadOnlyControllerBase).IsAssignableFrom(controller);
            var controllerPolicies = PolicyNames(controller.GetCustomAttributes());

            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName);

            foreach (var action in actions)
            {
                var attributes = action.GetCustomAttributes().ToList();
                var verbs = attributes.OfType<HttpMethodAttribute>().ToList();

                verbs.Should().NotBeEmpty(
                    "action {0}.{1} must declare an explicit HTTP verb", controller.Name, action.Name);

                if (verbs.All(v => v is HttpGetAttribute))
                {
                    continue;
                }

                // A non-GET action: never on a read-only (topology/audit) controller ...
                isReadOnlyController.Should().BeFalse(
                    "action {0}.{1} must be GET-only on read controllers (NFR1)", controller.Name, action.Name);

                // ... only on an allow-listed discovery/webhook controller ...
                NonGetControllerAllowList.Should().Contain(
                    controller.Name,
                    "non-GET action {0}.{1} must live on an allow-listed discovery controller",
                    controller.Name, action.Name);

                if (HmacAuthenticatedControllers.Contains(controller.Name))
                {
                    // Authenticated by HMAC signature verification over the raw body, not an
                    // authorization policy — see ADR 0026.
                    continue;
                }

                if (ReadGatedPreviewControllers.Contains(controller.Name))
                {
                    // A read-shaped preview gated by TopologyRead (ADR 0055): assert it carries the read
                    // policy rather than a write policy, then skip the write-policy requirement.
                    var readPolicies = controllerPolicies.Concat(PolicyNames(action.GetCustomAttributes())).ToList();
                    readPolicies.Should().Contain(
                        AuthorizationPolicies.TopologyRead,
                        "the read-shaped preview action {0}.{1} must be gated by TopologyRead",
                        controller.Name, action.Name);
                    continue;
                }

                // ... and always gated by a discovery write policy (fail-closed).
                var policies = controllerPolicies.Concat(PolicyNames(attributes)).ToList();
                policies.Should().Contain(
                    p => WritePolicies.Contains(p),
                    "non-GET action {0}.{1} must carry a DiscoveryTrigger/ScheduleManage policy",
                    controller.Name, action.Name);
            }
        }
    }

    [Fact]
    public void Topology_hub_exposes_only_subscribe_and_unsubscribe()
    {
        // Story #9 safety boundary: the hub is strictly read-only. The only invokable server methods are
        // the group mechanics; an accidental future mutating method must fail the build. Overrides of the
        // SignalR Hub base (OnConnectedAsync/OnDisconnectedAsync) are not client-invocable, so exclude them.
        var invokable = typeof(TopologyHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetBaseDefinition().DeclaringType == m.DeclaringType)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        invokable.Should().BeEquivalentTo(new[]
        {
            nameof(TopologyHub.SubscribeToRack),
            nameof(TopologyHub.UnsubscribeFromRack),
        });
    }

    private static IEnumerable<string> PolicyNames(IEnumerable<Attribute> attributes)
        => attributes.OfType<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!);
}
