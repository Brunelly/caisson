using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Options;
using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;
using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.RoundTrip;
using Caisson.Ingestion.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Caisson.Api.Services;

/// <summary>
/// The real desired-state PR publisher (story #172, Task #207). Orchestrates, behind the existing #170 seam:
/// render the candidate to canonical YAML and fingerprint it; a fast idempotency read (reuse with no Key
/// Vault / GitHub call); the PR-only guardrail (refuse before any write); reserve the fingerprint (concurrent
/// losers reuse the winner); then the winner reads the default-branch head, creates a feature ref, commits
/// the YAML, opens a PR against the default branch, records the real PR metadata, and audits the outcome.
/// Every create/reuse/refuse/fail path emits a durable audit and a structured log scope; no secret or
/// candidate YAML is ever logged or persisted in the audit.
/// </summary>
public sealed class GitHubDesiredStatePrService : IDesiredStatePrService
{
    private readonly CaissonDbContext _context;
    private readonly IGitPullRequestLinkStore _links;
    private readonly IGitHubPullRequestClient _github;
    private readonly IAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IHttpContextAccessor _httpContext;
    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubDesiredStatePrService> _logger;

    public GitHubDesiredStatePrService(
        CaissonDbContext context,
        IGitPullRequestLinkStore links,
        IGitHubPullRequestClient github,
        IAuditEventWriter audit,
        ICorrelationContext correlation,
        IHttpContextAccessor httpContext,
        IOptions<GitHubOptions> options,
        TimeProvider time,
        ILogger<GitHubDesiredStatePrService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DesiredStatePrCreationResult> CreatePullRequestAsync(
        DesiredStatePrCreationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.Value;
        var stopwatch = Stopwatch.StartNew();

        var rackSlug = await _context.Racks
            .Where(r => r.Id == request.RackId)
            .Select(r => r.ExternalKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (rackSlug is null)
        {
            return Failure(GitPrErrorCodes.UnexpectedError, fingerprint: null, branchName: null);
        }

        if (string.IsNullOrWhiteSpace(options.RepoOwner) || string.IsNullOrWhiteSpace(options.RepoName))
        {
            await AuditFailureAsync(request.RackId, rackSlug, fingerprint: null, branchName: null,
                GitPrErrorCodes.GitRepoNotConfigured, cancellationToken);
            return Failure(GitPrErrorCodes.GitRepoNotConfigured, fingerprint: null, branchName: null);
        }

        // Render the candidate to canonical YAML (server-authoritative rackSlug) and fingerprint it. A render
        // failure is a 422, surfaced by letting DesiredStateRenderException propagate — never a git write.
        var candidateModel = new SupportedDesiredStateModel(rackSlug, request.VlanCatalogue, request.PortIntents);
        var candidateYaml = DesiredStateYamlRenderer.Render(candidateModel).Yaml;
        var fingerprint = DesiredStateContentHash.Compute(candidateYaml);

        var actorId = ResolveActorId();
        var operatorSlug = PrBranchNaming.Slugify(actorId);
        var correlationId = _correlation.CorrelationId.ToString();

        using var scope = _logger.BeginScope(LogScope(request.RackId, rackSlug, actorId, fingerprint, options));

        // Fast idempotency read: an existing OPEN link is reused with NO Key Vault / GitHub call (≤3s P95, AC2).
        var existing = await _links.FindOpenByFingerprintAsync(request.RackId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            await AuditLinkAsync(GitPrAuditActions.Reused, existing, rackSlug, reused: true, errorCode: null, cancellationToken);
            _logger.LogInformation(
                "Desired-state PR reused prNumber={PrNumber} branch={Branch} elapsedMs={ElapsedMs}",
                existing.PullRequestNumber, existing.BranchName, stopwatch.ElapsedMilliseconds);
            return ReuseResult(existing, fingerprint);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var branchName = PrBranchNaming.Build(rackSlug, actorId, fingerprint, now, options.BranchPrefix);

        // Early PR-only guardrail against the configured default branch: refuse with zero credential/GitHub
        // calls when the (generated) branch would target the default branch (AC3, defense-in-depth).
        try
        {
            PrOnlyGuardrail.EnsureNotDefaultBranch(branchName, options.DefaultBranch);
        }
        catch (PrOnlyGuardrailViolationException)
        {
            await AuditRefusalAsync(request.RackId, rackSlug, fingerprint, branchName, cancellationToken);
            throw;
        }

        // Reserve the fingerprint. A concurrent loser re-reads and reuses the winner (NFR3: N=5 → 1 PR).
        var link = new GitPullRequestLink(
            Guid.NewGuid(), request.RackId, options.RepoOwner, options.RepoName, branchName, fingerprint,
            actorId, now, correlationId);
        var reservation = await _links.InsertOrGetExistingAsync(link, cancellationToken);
        if (!reservation.Inserted)
        {
            await AuditLinkAsync(GitPrAuditActions.Reused, reservation.Link, rackSlug, reused: true, errorCode: null, cancellationToken);
            return ReuseResult(reservation.Link, fingerprint);
        }

        // Winner: compute the change summary (DB reads only, no GitHub) then perform the git write.
        var changeSummary = await ComputeChangeSummaryAsync(rackSlug, candidateModel, request.RackId, cancellationToken);

        try
        {
            var repo = await _github.GetRepositoryAsync(cancellationToken);
            var defaultBranch = string.IsNullOrEmpty(repo.DefaultBranch) ? options.DefaultBranch : repo.DefaultBranch;

            // Authoritative re-check against the branch GitHub actually reports (over the configured value).
            PrOnlyGuardrail.EnsureNotDefaultBranch(branchName, defaultBranch);

            var head = await _github.GetBranchHeadAsync(defaultBranch, cancellationToken);
            await _github.CreateBranchAsync(branchName, head.CommitSha, cancellationToken);

            var filePath = options.CommitPathTemplate.Replace("{slug}", rackSlug, StringComparison.Ordinal);
            var existingFile = await _github.GetFileMetadataAsync(branchName, filePath, cancellationToken);
            var commitMessage = PrMetadataComposer.ComposeTitle(rackSlug, operatorSlug);
            var commit = await _github.CommitFileAsync(
                branchName, filePath, candidateYaml, commitMessage, existingFile?.Sha, cancellationToken);

            var title = PrMetadataComposer.ComposeTitle(rackSlug, operatorSlug);
            var body = PrMetadataComposer.ComposeBody(new PrBodyModel(
                rackSlug, operatorSlug, now, fingerprint, request.ValidationRunId,
                request.AcknowledgedWarningCodes, changeSummary, correlationId));
            var pr = await _github.OpenPullRequestAsync(title, body, branchName, defaultBranch, cancellationToken);

            link.MarkPublished(pr.Number, pr.HtmlUrl, commit.Sha, _time.GetUtcNow().UtcDateTime);
            await _context.SaveChangesAsync(cancellationToken);

            await AuditLinkAsync(GitPrAuditActions.Created, link, rackSlug, reused: false, errorCode: null, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "Desired-state PR created prNumber={PrNumber} branch={Branch} prUrl={PrUrl} elapsedMs={ElapsedMs}",
                pr.Number, branchName, pr.HtmlUrl, stopwatch.ElapsedMilliseconds);

            return CreatedResult(link, fingerprint, changeSummary);
        }
        catch (PrOnlyGuardrailViolationException)
        {
            await FailReservationAsync(link, cancellationToken);
            await AuditRefusalAsync(request.RackId, rackSlug, fingerprint, branchName, cancellationToken);
            throw;
        }
        catch (GitCredentialUnavailableException ex)
        {
            _logger.LogError(ex, "Git credential unavailable while creating a desired-state PR.");
            await FailReservationAsync(link, cancellationToken);
            await AuditLinkAsync(GitPrAuditActions.Failed, link, rackSlug, reused: false,
                GitPrErrorCodes.GitCredentialsUnavailable, cancellationToken);
            return Failure(GitPrErrorCodes.GitCredentialsUnavailable, fingerprint, branchName);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogError("GitHub API call failed with HTTP {Status} while creating a desired-state PR.", ex.StatusCode);
            await FailReservationAsync(link, cancellationToken);
            await AuditLinkAsync(GitPrAuditActions.Failed, link, rackSlug, reused: false,
                GitPrErrorCodes.GitHubApiFailed, cancellationToken);
            return Failure(GitPrErrorCodes.GitHubApiFailed, fingerprint, branchName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error while creating a desired-state PR.");
            await FailReservationAsync(link, cancellationToken);
            await AuditLinkAsync(GitPrAuditActions.Failed, link, rackSlug, reused: false,
                GitPrErrorCodes.UnexpectedError, cancellationToken);
            return Failure(GitPrErrorCodes.UnexpectedError, fingerprint, branchName);
        }
    }

    private async Task<PrChangeSummary> ComputeChangeSummaryAsync(
        string rackSlug, SupportedDesiredStateModel candidateModel, Guid rackId, CancellationToken cancellationToken)
    {
        var baseline = await _context.ActiveVersionForRackAsync(rackSlug, cancellationToken);
        var baselineModel = baseline is null
            ? new SupportedDesiredStateModel(rackSlug, Array.Empty<Domain.NetworkConfig.VlanCatalogueEntry>(),
                Array.Empty<Domain.NetworkConfig.PortAccessIntent>())
            : BaselineIntentProjection.Project(rackSlug, baseline.DesiredStateJson, candidateModel.VlanCatalogue);

        var diff = SemanticDiffEngine.Diff(baselineModel, candidateModel, rackId);
        return PrMetadataComposer.ToChangeSummary(diff);
    }

    private async Task FailReservationAsync(GitPullRequestLink link, CancellationToken cancellationToken)
    {
        try
        {
            // Close the reservation so a retry can create a fresh PR (the filtered unique index frees the
            // fingerprint). Never force-push; a retry inspects persisted state and opens a new branch/PR.
            link.UpdateStatus(GitPullRequestStatus.Closed, _time.GetUtcNow().UtcDateTime);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close a desired-state PR reservation after a git failure; a retry may be blocked until reconciliation.");
        }
    }

    private static DesiredStatePrCreationResult ReuseResult(GitPullRequestLink link, string fingerprint)
        => new(
            GatePassed: true,
            Status: "pr-reused",
            Detail: "An open pull request already exists for this candidate; reusing it.",
            PullRequestUrl: link.PullRequestUrl,
            PullRequestNumber: link.PullRequestNumber,
            BranchName: link.BranchName,
            CommitSha: link.CommitSha,
            CandidateFingerprint: fingerprint,
            Reused: true,
            RepoOwner: link.RepoOwner,
            RepoName: link.RepoName);

    private static DesiredStatePrCreationResult CreatedResult(
        GitPullRequestLink link, string fingerprint, PrChangeSummary summary)
        => new(
            GatePassed: true,
            Status: "pr-created",
            Detail: "A pull request was created for the desired-state candidate.",
            PullRequestUrl: link.PullRequestUrl,
            PullRequestNumber: link.PullRequestNumber,
            BranchName: link.BranchName,
            CommitSha: link.CommitSha,
            CandidateFingerprint: fingerprint,
            Reused: false,
            RepoOwner: link.RepoOwner,
            RepoName: link.RepoName,
            ChangeSummary: summary);

    private static DesiredStatePrCreationResult Failure(string errorCode, string? fingerprint, string? branchName)
        => new(
            GatePassed: true,
            Status: "pr-failed",
            Detail: GitPrErrorCodes.MessageFor(errorCode),
            PullRequestUrl: null,
            BranchName: branchName,
            CandidateFingerprint: fingerprint,
            ErrorCode: errorCode);

    private Task AuditLinkAsync(
        string action, GitPullRequestLink link, string rackSlug, bool reused, string? errorCode,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["correlationId"] = _correlation.CorrelationId,
            ["rackId"] = link.RackId,
            ["rackSlug"] = rackSlug,
            ["fingerprint"] = link.CandidateFingerprint,
            ["repoOwner"] = link.RepoOwner,
            ["repoName"] = link.RepoName,
            ["branch"] = link.BranchName,
            ["prNumber"] = link.PullRequestNumber,
            ["prUrl"] = link.PullRequestUrl,
            ["reused"] = reused,
        };
        if (errorCode is not null)
        {
            details["errorCode"] = errorCode;
        }

        return WriteAuditAsync(action, link.RackId, errorCode is null ? "success" : "failed", details, cancellationToken);
    }

    private Task AuditRefusalAsync(
        Guid rackId, string rackSlug, string fingerprint, string branchName, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["correlationId"] = _correlation.CorrelationId,
            ["rackId"] = rackId,
            ["rackSlug"] = rackSlug,
            ["fingerprint"] = fingerprint,
            ["branch"] = branchName,
            ["errorCode"] = GitPrErrorCodes.PrOnlyGuardrailViolation,
            ["reused"] = false,
        };
        return WriteAuditAsync(GitPrAuditActions.RefusedPrOnly, rackId, "refused", details, cancellationToken);
    }

    private Task AuditFailureAsync(
        Guid rackId, string rackSlug, string? fingerprint, string? branchName, string errorCode,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["correlationId"] = _correlation.CorrelationId,
            ["rackId"] = rackId,
            ["rackSlug"] = rackSlug,
            ["fingerprint"] = fingerprint,
            ["branch"] = branchName,
            ["errorCode"] = errorCode,
            ["reused"] = false,
        };
        return WriteAuditAsync(GitPrAuditActions.Failed, rackId, "failed", details, cancellationToken);
    }

    private Task WriteAuditAsync(
        string action, Guid rackId, string result, Dictionary<string, object?> details, CancellationToken cancellationToken)
    {
        var user = _httpContext.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        // Details carry counts/ids/urls only — never the PAT or candidate YAML (relies on the SecretScrubber
        // backstop + the audit-details 8 KB bound).
        return _audit.WriteActionAsync(
            user, rackId, action, "git-pull-request", rackId.ToString(),
            result, cancellationToken, JsonSerializer.Serialize(details));
    }

    private string ResolveActorId()
    {
        var user = _httpContext.HttpContext?.User;
        return user?.FindFirst("oid")?.Value
            ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.FindFirst("sub")?.Value
            ?? user?.Identity?.Name
            ?? "unknown";
    }

    private Dictionary<string, object?> LogScope(
        Guid rackId, string rackSlug, string actorId, string fingerprint, GitHubOptions options)
        => new(StringComparer.Ordinal)
        {
            ["correlationId"] = _correlation.CorrelationId,
            ["rackId"] = rackId,
            ["rackSlug"] = rackSlug,
            ["actorId"] = actorId,
            ["fingerprint"] = fingerprint,
            ["repo"] = $"{options.RepoOwner}/{options.RepoName}",
        };
}
