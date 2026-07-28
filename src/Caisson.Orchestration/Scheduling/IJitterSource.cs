namespace Caisson.Orchestration.Scheduling;

/// <summary>
/// Supplies the randomized jitter the scheduler adds to a rack's fixed interval (AC3). Abstracted so
/// tests can inject a deterministic source instead of <see cref="Random.Shared"/> (NFR4: the scheduled
/// path never touches <c>Random.Shared</c> directly).
/// </summary>
public interface IJitterSource
{
    /// <summary>Returns a jitter value in the inclusive range <c>[0, maxJitterSeconds]</c>.</summary>
    int NextJitterSeconds(int maxJitterSeconds);
}

/// <summary>The production <see cref="IJitterSource"/> backed by <see cref="Random.Shared"/>.</summary>
public sealed class RandomJitterSource : IJitterSource
{
    /// <inheritdoc />
    public int NextJitterSeconds(int maxJitterSeconds)
        => maxJitterSeconds <= 0 ? 0 : Random.Shared.Next(0, maxJitterSeconds + 1);
}
