using Caisson.Domain.ValueObjects;

namespace Caisson.Correlation.Results;

/// <summary>
/// The confidence bands used to communicate correlation certainty to UI/persistence/tests: High ≥ 0.8,
/// Medium 0.5–0.79, Low &lt; 0.5 (the story's answered question). This is a presentation-layer grouping
/// only — the domain <see cref="ConfidenceScore"/> deliberately stays band-agnostic; the engine emits a
/// numeric score and this helper classifies it identically to the e2e harness.
/// </summary>
public static class ConfidenceBands
{
    /// <summary>Inclusive lower bound of the High band.</summary>
    public const double HighThreshold = 0.8;

    /// <summary>Inclusive lower bound of the Medium band.</summary>
    public const double MediumThreshold = 0.5;

    /// <summary>The confidence band a score falls into.</summary>
    public enum Band
    {
        /// <summary>Confidence &lt; 0.5: not a reliable direct attachment.</summary>
        Low,

        /// <summary>Confidence 0.5–0.79: a plausible but non-unique attachment.</summary>
        Medium,

        /// <summary>Confidence ≥ 0.8: a confident direct attachment.</summary>
        High,
    }

    /// <summary>Classifies a bounded confidence score into its band.</summary>
    public static Band Of(ConfidenceScore score) => Of(score.Value);

    /// <summary>Classifies a raw confidence value into its band.</summary>
    public static Band Of(double value)
        => value >= HighThreshold ? Band.High
            : value >= MediumThreshold ? Band.Medium
            : Band.Low;
}
