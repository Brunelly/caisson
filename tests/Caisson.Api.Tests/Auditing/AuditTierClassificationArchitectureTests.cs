using System.Reflection;
using System.Runtime.CompilerServices;
using Caisson.Api.Auditing;
using Caisson.Api.Controllers;
using Caisson.Api.Realtime.Hubs;
using Caisson.Api.Services;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.Tests.Auditing;

/// <summary>
/// Source-level architecture guard (story #308, ADR 0064): every audit call site resolves to exactly one
/// explicitly-named tier, and there is no generic seam through which a mutation could accidentally use the
/// droppable Tier 3 channel, or through which the security-critical <c>authorization.forbidden</c> action
/// could be emitted from anywhere but the Tier 2 implementation.
/// </summary>
public sealed class AuditTierClassificationArchitectureTests
{
    /// <summary>
    /// The ONLY types permitted to depend on <see cref="IBestEffortAuditEventWriter"/> — all read
    /// endpoints, or (for the mixed controllers) endpoints whose OWN mutation already emits its Tier 1
    /// event elsewhere and only use the best-effort writer for a genuinely non-mutating observation (e.g.
    /// a read-audit, or an already-terminal/no-op outcome). Adding a new consumer requires deliberately
    /// adding it here — a reviewable, compile-time-visible decision, not something that can happen silently.
    /// </summary>
    private static readonly HashSet<Type> AllowedBestEffortConsumers = new()
    {
        typeof(RackTopologyController),
        typeof(TopologyEntitiesController),
        typeof(RacksController),
        typeof(AuditController),
        typeof(DriftController),
        typeof(RackDiscoveryStatusController),
        typeof(DiscoveryJobsController),
        typeof(DiscoveryJobDetailController),
        typeof(DriftApplyJobController),
        typeof(RackDiscoveryScheduleController),
        typeof(DesiredStateImpactPreviewController),
        typeof(DesiredStatePreflightController),
        typeof(DesiredStateStatusController),
        typeof(DesiredStateRevisionsController),
        typeof(DesiredStateIngestionRunsController),
        typeof(DesiredStateRacksController),
        typeof(DesiredStateRoundTripController),
        typeof(DesiredStatePrController),
        typeof(GitHubDesiredStatePrService),
        typeof(TopologyHub),
    };

    /// <summary>
    /// Known Tier 1 (mandatory-durable) mutation producers whose ENTIRE audit surface is Tier 1 — these
    /// must NEVER take a best-effort dependency for any reason. (Contrast <see cref="GitHubDesiredStatePrService"/>,
    /// a mixed producer that legitimately also uses Tier 3 for its non-mutating "reused" observation, and
    /// so is only in <see cref="AllowedBestEffortConsumers"/>, not here.)
    /// </summary>
    private static readonly Type[] KnownMutationTypes =
    {
        typeof(Caisson.Api.Controllers.NetworkIntentController),
        typeof(Caisson.Api.Controllers.DriftApplyController),
    };

    [Fact]
    public void No_type_outside_the_allow_list_depends_on_the_best_effort_writer()
    {
        var assembly = typeof(Caisson.Api.Auditing.AuditActorResolver).Assembly;
        var offenders = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !AllowedBestEffortConsumers.Contains(t))
            .Where(TakesBestEffortWriter)
            .ToList();

        offenders.Should().BeEmpty(
            "only the allow-listed Tier 3 read/observation surfaces may depend on IBestEffortAuditEventWriter — " +
            "a mutation must use IMandatoryAuditOutbox (Tier 1) or IAuthorizationDenialAuditWriter (Tier 2) instead");
    }

    [Fact]
    public void Known_mutation_producers_do_not_depend_on_the_best_effort_writer()
    {
        foreach (var type in KnownMutationTypes)
        {
            TakesBestEffortWriter(type).Should().BeFalse(
                $"{type.Name} performs a state mutation and must never take a best-effort (droppable) audit dependency for it");
        }
    }

    [Fact]
    public void The_authorization_forbidden_literal_appears_only_in_the_tier_2_implementation()
    {
        var srcRoot = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "AuthorizationDenialAuditWriter.cs")
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue; // documentation/comments, not an emitting code path
                }

                // Word-bounded: "authorization.forbidden" but not the distinct "authorization.forbidden.overflow" aggregate action.
                if (line.Contains("\"authorization.forbidden\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "the literal action string must only be emitted from AuthorizationDenialAuditWriter (the Tier 2 implementation) " +
            "— a hardcoded duplicate elsewhere could bypass the durable-first-N/bounded-overflow contract");
    }

    private static bool TakesBestEffortWriter(Type type)
        => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IBestEffortAuditEventWriter)));

    private static string RepoRoot([CallerFilePath] string thisFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Caisson.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (Caisson.sln) from " + thisFilePath);
    }
}
