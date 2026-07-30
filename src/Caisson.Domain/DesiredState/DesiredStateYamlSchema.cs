using System.Collections.Generic;
using Caisson.Domain.NetworkConfig;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// The single source of truth for the <b>canonical desired-state YAML document shape and ordering rules</b>
/// (story #169, AC1). The round-trip renderer, parser, and their tests all read these constants so they can
/// never drift about "what the canonical document looks like" — no key order or sort key is ever spelled as
/// a literal scattered in the emitter.
/// </summary>
/// <remarks>
/// <para>
/// The document uses the versioned Kubernetes-style envelope the story's AC1 example prescribes
/// (<c>apiVersion</c>/<c>kind</c>/<c>metadata</c>/<c>spec</c>/<c>extensions</c>), deliberately <b>not</b>
/// the legacy flat <c>rackSlug</c>/<c>switches</c> ingestion shape used by
/// <see cref="Ingestion"/>/git-ingestion. Converging the shipped <c>DesiredStateValidator</c>/git-ingestion
/// pipeline onto this envelope is explicitly OUT OF SCOPE for v1 (see ADR 0049); the <c>spec.switches[].ports[]</c>
/// field names are chosen to mirror <see cref="DesiredPortIntent"/> (<c>name</c>/<c>accessVlan</c>/
/// <c>description</c>/<c>neighbor{systemName,portId}</c>) so a later story can converge that pipeline cheaply.
/// </para>
/// <para>
/// Every numeric/length bound comes from <see cref="DesiredStateSchema"/>, the single audited place they are
/// defined — this type re-exposes none of them.
/// </para>
/// </remarks>
public static class DesiredStateYamlSchema
{
    /// <summary>The <c>apiVersion</c> value stamped on every rendered document.</summary>
    public const string ApiVersion = "caisson.dev/v1alpha1";

    /// <summary>The <c>kind</c> value stamped on every rendered document.</summary>
    public const string Kind = "RackDesiredState";

    /// <summary>The reserved top-level key under which unknown/unsupported sections are preserved verbatim (Q1 answer).</summary>
    public const string ExtensionsKey = "extensions";

    /// <summary>Number of spaces per indentation level.</summary>
    public const int IndentSize = 2;

    /// <summary>The canonical newline: LF only, on every platform and locale (AC1/NFR1, Q3 answer).</summary>
    public const string Newline = "\n";

    /// <summary>Top-level mapping key order (AC1 example).</summary>
    public static readonly IReadOnlyList<string> TopLevelKeyOrder =
        new[] { "apiVersion", "kind", "metadata", "spec", ExtensionsKey };

    /// <summary>Key order within <c>metadata</c>.</summary>
    public static readonly IReadOnlyList<string> MetadataKeyOrder = new[] { "rackSlug" };

    /// <summary>Key order within <c>spec</c>.</summary>
    public static readonly IReadOnlyList<string> SpecKeyOrder = new[] { "vlans", "switches" };

    /// <summary>Key order within a <c>spec.vlans[]</c> entry.</summary>
    public static readonly IReadOnlyList<string> VlanKeyOrder = new[] { "vlanId", "name", "description" };

    /// <summary>Key order within a <c>spec.switches[]</c> entry.</summary>
    public static readonly IReadOnlyList<string> SwitchKeyOrder = new[] { "name", "ports" };

    /// <summary>
    /// Key order within a <c>spec.switches[].ports[]</c> entry — mirrors <see cref="DesiredPortIntent"/>'s
    /// field set for forward-compatibility. The v1 supported model (<see cref="PortAccessIntent"/>) only
    /// carries <c>name</c>/<c>accessVlan</c>, so the renderer never emits <c>description</c>/<c>neighbor</c>
    /// and the importer rejects them; the order is reserved here so a future convergence story is cheap.
    /// </summary>
    public static readonly IReadOnlyList<string> PortKeyOrder = new[] { "name", "accessVlan", "description", "neighbor" };

    /// <summary>Key order within a port's <c>neighbor</c> mapping (reserved for a future convergence story).</summary>
    public static readonly IReadOnlyList<string> NeighborKeyOrder = new[] { "systemName", "portId" };

    /// <summary>The one comparer for names (switch names, port names, VLAN names): <see cref="StringComparer.Ordinal"/>.</summary>
    public static readonly StringComparer NameOrdinal = StringComparer.Ordinal;

    /// <summary>
    /// The stable sort order for <c>spec.vlans</c>: by <see cref="VlanCatalogueEntry.Id"/> ascending, then
    /// by <see cref="VlanCatalogueEntry.Name"/> Ordinal — never insertion order (AC1).
    /// </summary>
    public static readonly IComparer<VlanCatalogueEntry> VlanCatalogueOrder =
        Comparer<VlanCatalogueEntry>.Create(static (a, b) =>
        {
            var byId = a.Id.CompareTo(b.Id);
            return byId != 0 ? byId : string.CompareOrdinal(a.Name ?? string.Empty, b.Name ?? string.Empty);
        });
}
