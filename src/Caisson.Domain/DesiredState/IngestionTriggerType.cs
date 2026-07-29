namespace Caisson.Domain.DesiredState;

/// <summary>How a desired-state ingestion run was initiated (story #62, AC1).</summary>
public enum IngestionTriggerType
{
    /// <summary>The run was started by the poll scheduler on its configured interval.</summary>
    Poll = 0,

    /// <summary>The run was requested by a signed Git provider webhook delivery.</summary>
    Webhook,
}
