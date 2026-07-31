using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Caisson.Infrastructure.Persistence.Queries;

/// <summary>
/// The outcome of an idempotent reservation attempt. <see cref="Inserted"/> is true when THIS caller won the
/// reservation (its <see cref="Link"/> is tracked and should be published), false when a concurrent request
/// already holds the open reservation (its <see cref="Link"/> is the existing winner to reuse).
/// </summary>
public sealed record GitPullRequestLinkReservation(bool Inserted, GitPullRequestLink Link);

/// <summary>
/// The rack-scoped idempotency store for desired-state PR links (story #172, Task #206). Behind an interface
/// so the publisher can depend on it without a direct <c>DbContext</c> coupling; the concrete implementation
/// shares the request-scoped <see cref="CaissonDbContext"/>, so a reservation it tracks can be published by
/// the same unit of work.
/// </summary>
public interface IGitPullRequestLinkStore
{
    /// <summary>
    /// Finds the single <see cref="GitPullRequestStatus.Open"/> link for a rack + candidate fingerprint, or
    /// <c>null</c> on a miss. Untracked: this is the fast idempotency read (no Key Vault / GitHub call on a
    /// hit).
    /// </summary>
    Task<GitPullRequestLink?> FindOpenByFingerprintAsync(
        Guid rackId, string candidateFingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves the open link for a candidate by inserting <paramref name="link"/>. If a concurrent request
    /// already inserted an Open link for the same (rack, fingerprint) — detected as the named partial-unique
    /// index violation — the losing row is detached and the existing winner is re-read and returned with
    /// <see cref="GitPullRequestLinkReservation.Inserted"/> false (NFR3: N identical requests → 1 PR).
    /// </summary>
    Task<GitPullRequestLinkReservation> InsertOrGetExistingAsync(
        GitPullRequestLink link, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IGitPullRequestLinkStore"/>. The insert-then-catch-unique-violation flow is copied from
/// <c>DriftApplyJobService.RequestApplyAsync</c>: it catches only the <see cref="PostgresErrorCodes.UniqueViolation"/>
/// on the named constraint <see cref="GitPullRequestLinkConfiguration.OpenLinkUniqueConstraint"/> (never a
/// blanket <see cref="DbUpdateException"/> swallow), detaches the loser, and re-reads the winner.
/// </summary>
public sealed class GitPullRequestLinkStore : IGitPullRequestLinkStore
{
    private readonly CaissonDbContext _context;

    public GitPullRequestLinkStore(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<GitPullRequestLink?> FindOpenByFingerprintAsync(
        Guid rackId, string candidateFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidateFingerprint);

        return _context.GitPullRequestLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.RackId == rackId
                    && x.CandidateFingerprint == candidateFingerprint
                    && x.Status == GitPullRequestStatus.Open,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GitPullRequestLinkReservation> InsertOrGetExistingAsync(
        GitPullRequestLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        _context.GitPullRequestLinks.Add(link);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new GitPullRequestLinkReservation(Inserted: true, link);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(pg.ConstraintName, GitPullRequestLinkConfiguration.OpenLinkUniqueConstraint,
                StringComparison.Ordinal))
        {
            _context.Entry(link).State = EntityState.Detached;

            var winner = await FindOpenByFingerprintAsync(link.RackId, link.CandidateFingerprint, cancellationToken);
            if (winner is not null)
            {
                return new GitPullRequestLinkReservation(Inserted: false, winner);
            }

            // The winning Open row vanished between the conflict and the re-read (e.g. it was just closed);
            // surface the original failure rather than pretend a reuse.
            throw;
        }
    }
}
