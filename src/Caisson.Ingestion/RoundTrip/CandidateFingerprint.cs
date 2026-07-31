using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;

namespace Caisson.Ingestion.RoundTrip;

/// <summary>
/// Computes the stable idempotency fingerprint of a rack desired-state candidate (story #172, Q1 answer):
/// the lowercase 64-hex SHA-256 of the candidate's <em>canonical YAML</em>. The candidate model is rendered
/// through the same <see cref="DesiredStateYamlRenderer"/> the ingestion read path uses and hashed via the
/// shared <see cref="DesiredStateContentHash"/>, so two candidates that differ only in collection ordering
/// (e.g. reordered VLAN or port entries) canonicalize identically and yield the same fingerprint, while any
/// semantic content change yields a different one. This is what makes the PR idempotency key
/// content-addressable.
/// <para>
/// Lives in <c>Caisson.Ingestion</c> (not <c>Caisson.Domain</c>) because it must invoke the renderer, which
/// is an ingestion concern; <c>Caisson.Domain</c> carries no ingestion dependency. See ADR 0056.
/// </para>
/// </summary>
public static class CandidateFingerprint
{
    /// <summary>
    /// Renders <paramref name="model"/> to canonical YAML and returns its lowercase 64-hex SHA-256 digest.
    /// Throws <see cref="DesiredStateRenderException"/> if the model is semantically invalid (the caller
    /// surfaces that as a 422 rather than attempting any git write).
    /// </summary>
    public static string Compute(SupportedDesiredStateModel model) => Render(model).Fingerprint;

    /// <summary>
    /// Renders <paramref name="model"/> to canonical YAML <em>once</em> and returns both the rendered document
    /// and its lowercase 64-hex SHA-256 fingerprint, so a caller that also needs the YAML (e.g. to commit it)
    /// reuses the single render rather than re-rendering. Keeps the canonical "render → hash" algorithm in one
    /// place; <see cref="Compute"/> is the fingerprint-only shorthand. Throws
    /// <see cref="DesiredStateRenderException"/> on a semantically invalid model.
    /// </summary>
    public static (string Yaml, string Fingerprint) Render(SupportedDesiredStateModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var canonicalYaml = DesiredStateYamlRenderer.Render(model).Yaml;
        return (canonicalYaml, DesiredStateContentHash.Compute(canonicalYaml));
    }
}
