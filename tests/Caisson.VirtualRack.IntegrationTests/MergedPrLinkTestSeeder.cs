using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Test seam for the story #173 merged-apply gate: simulates "the exact candidate's GitHub PR was created and
/// merged" so a drift-apply through the real controller passes the gate (AC4 positive path). It seeds a
/// <see cref="GitPullRequestStatus.Merged"/> <see cref="GitPullRequestLink"/> whose
/// <see cref="GitPullRequestLink.CandidateFingerprint"/> is the rack's latest ingested
/// <c>DesiredStateVersion.CandidateFingerprint</c> — the SAME canonical fingerprint the production PR-creation
/// path stamps — so this reproduces the real ingestion→PR→merge alignment rather than bypassing the gate with a
/// permissive double. These end-to-end tests drive the real device-write loop, which only runs once the gate
/// allows apply.
/// </summary>
internal static class MergedPrLinkTestSeeder
{
    public static async Task SeedMergedPrForLatestRevisionAsync(IServiceProvider services, Guid rackId)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var rackSlug = await context.Racks
            .Where(r => r.Id == rackId)
            .Select(r => r.ExternalKey)
            .FirstAsync();

        var fingerprint = await context.DesiredStateVersions
            .Where(v => v.RackSlug == rackSlug && v.CandidateFingerprint != null)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ThenByDescending(v => v.Id)
            .Select(v => v.CandidateFingerprint)
            .FirstAsync();

        var linkId = Guid.NewGuid();
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/" + Guid.NewGuid().ToString("N")[..8],
            fingerprint!, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(1234, "https://gh/pr/1234", "commitsha0", DateTime.UtcNow);
        link.UpdateStatus(GitPullRequestStatus.Merged, DateTime.UtcNow);

        var record = new GitPullRequestStatusRecord(
            Guid.NewGuid(), linkId, rackId, "octo", "repo", 1234, "https://gh/pr/1234", DateTime.UtcNow);
        record.ApplyObservation(
            GitPullRequestStatus.Merged, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", DateTime.UtcNow);

        context.GitPullRequestLinks.Add(link);
        context.GitPullRequestStatuses.Add(record);
        await context.SaveChangesAsync();
    }
}
