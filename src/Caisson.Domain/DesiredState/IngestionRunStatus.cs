namespace Caisson.Domain.DesiredState;

/// <summary>
/// The lifecycle state of a desired-state ingestion run (story #62, AC1/AC3/Q3). A run progresses
/// <see cref="Running"/> → one terminal state. <see cref="Succeeded"/>/<see cref="PartiallySucceeded"/>/
/// <see cref="ValidationFailed"/> all mean the run itself completed (the commit was fully processed);
/// they differ only in how many of the commit's rack files validated cleanly (Q3's partial-accept
/// policy). <see cref="Failed"/> means the run could not complete at all for an infrastructure reason
/// (auth/network/persistence) and is the only status that is safely retriable on the next poll tick —
/// see the partial-unique index filter on <c>commit_sha</c> in
/// <c>DesiredStateIngestionRunConfiguration</c>.
/// </summary>
public enum IngestionRunStatus
{
    /// <summary>The run has fetched its commit and is validating/materialising rack files.</summary>
    Running = 0,

    /// <summary>Every rack file in the commit validated and was materialised.</summary>
    Succeeded,

    /// <summary>Some rack files validated (and were materialised); others failed validation (Q3).</summary>
    PartiallySucceeded,

    /// <summary>The run completed, but no rack file in the commit validated.</summary>
    ValidationFailed,

    /// <summary>The run could not complete for an infrastructure reason; see <see cref="IngestionErrorCategory"/>.</summary>
    Failed,
}
