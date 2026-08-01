using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Options;
using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;
using Caisson.Domain.Enums;
using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
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
    private readonly IBestEffortAuditEventWriter _audit;
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly ICorrelationContext _correlation;
    private readonly IHttpContextAccessor _httpContext;
    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubDesiredStatePrService> _logger;

    public GitHubDesiredStatePrService(
        CaissonDbContext context,
        IGitPullRequestLinkStore links,
        IGitHubPullRequestClient github,
        IBestEffortAuditEventWriter audit,
        IMandatoryAuditOutbox auditOutbox,
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
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
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
            // TOCTOU-only path (the controller pre-checks rack existence; the rack was deleted mid-request).
            // Audit it like the other failure paths (AC6) rather than failing silently.
            await AuditFailureAsync(request.RackId, rackSlug: string.Empty, fingerprint: null, branchName: null,
                GitPrErrorCodes.UnexpectedError, cancellationToken);
            return Failure(GitPrErrorCodes.UnexpectedError, fingerprint: null, branchName: null);
        }

        if (string.IsNullOrWhiteSpace(options.RepoOwner) || string.IsNullOrWhiteSpace(options.RepoName))
        {
            await AuditFailureAsync(request.RackId, rackSlug, fingerprint: null, branchName: null,
                GitPrErrorCodes.GitRepoNotConfigured, cancellationToken);
            return Failure(GitPrErrorCodes.GitRepoNotConfigured, fingerprint: null, branchName: null);
        }

        // Render the candidate to canonical YAML (server-authoritative rackSlug) and fingerprint it via the
        // single canonical helper (one render, reused for the commit body). A render failure is a 422, surfaced
        // by letting DesiredStateRenderException propagate — never a git write.
        var candidateModel = new SupportedDesiredStateModel(rackSlug, request.VlanCatalogue, request.PortIntents);
        var (candidateYaml, fingerprint) = CandidateFingerprint.Render(candidateModel);

        var actorId = ResolveActorId();
        var operatorSlug = PrBranchNaming.Slugify(actorId);
        var correlationId = _correlation.CorrelationId.ToString();

        using var scope = _logger.BeginScope(LogScope(request.RackId, rackSlug, actorId, fingerprint, options));

        // Fast idempotency read: an existing OPEN link is reused with NO Key Vault / GitHub call (≤3s P95, AC2).
        var existing = await _links.FindOpenByFingerprintAsync(request.RackId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            return await CompleteReuseAsync(request.RackId, fingerprint, existing, rackSlug, stopwatch, options, cancellationToken);
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
            return await CompleteReuseAsync(request.RackId, fingerprint, reservation.Link, rackSlug, stopwatch, options, cancellationToken);
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

            var filePath = BuildCommitFilePath(options.CommitPathTemplate, rackSlug);
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

            // Tier 1 (mandatory-durable, story #308 ADR 0064): staged in the SAME transaction as
            // MarkPublished. NOTE the unavoidable boundary this does NOT cover: PostgreSQL atomically
            // commits the link's published state plus this audit row together, but NOT the preceding
            // GitHub API call itself (already durable by the time we reach here) — the reservation's
            // idempotency (a link only reaches here once) is what makes a retry after a crash between the
            // GitHub write and this commit safe, not the outbox transaction.
            StageAuditOutbox(GitPrAuditActions.Created, link, rackSlug, reused: false, errorCode: null, "success");
            await _context.SaveChangesAsync(cancellationToken);

            stopwatch.Stop();
            _logger.LogInformation(
                "Desired-state PR created prNumber={PrNumber} branch={Branch} prUrl={PrUrl} elapsedMs={ElapsedMs}",
                pr.Number, branchName, pr.HtmlUrl, stopwatch.ElapsedMilliseconds);

            return CreatedResult(link, fingerprint, changeSummary);
        }
        catch (PrOnlyGuardrailViolationException)
        {
            await FailReservationAsync(
                link, rackSlug, GitPrAuditActions.RefusedPrOnly, "refused", GitPrErrorCodes.PrOnlyGuardrailViolation,
                cancellationToken);
            throw;
        }
        catch (GitCredentialUnavailableException ex)
        {
            _logger.LogError(ex, "Git credential unavailable while creating a desired-state PR.");
            await FailReservationAsync(
                link, rackSlug, GitPrAuditActions.Failed, "failed", GitPrErrorCodes.GitCredentialsUnavailable,
                cancellationToken);
            return Failure(GitPrErrorCodes.GitCredentialsUnavailable, fingerprint, branchName);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogError("GitHub API call failed with HTTP {Status} while creating a desired-state PR.", ex.StatusCode);
            await FailReservationAsync(
                link, rackSlug, GitPrAuditActions.Failed, "failed", GitPrErrorCodes.GitHubApiFailed, cancellationToken);
            return Failure(GitPrErrorCodes.GitHubApiFailed, fingerprint, branchName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error while creating a desired-state PR.");
            await FailReservationAsync(
                link, rackSlug, GitPrAuditActions.Failed, "failed", GitPrErrorCodes.UnexpectedError, cancellationToken);
            return Failure(GitPrErrorCodes.UnexpectedError, fingerprint, branchName);
        }
    }

    /// <summary>
    /// Completes an idempotent reuse (AC2). A concurrent loser can re-read the winner's reservation while the
    /// winner is still inside its multi-second GitHub publish window, so the row's PR number/url/commit are
    /// still null (NFR3). This waits — DB reads only, no Key Vault / GitHub call, preserving the
    /// zero-GitHub-traffic reuse property — for the winner to publish, then returns the full PR metadata. If
    /// the winner is still publishing when the bounded wait elapses, or its reservation was closed by a
    /// failure, it returns a distinct <c>pr-pending</c> result (never <c>reused=true</c> with a null PR URL),
    /// so the caller retries and self-heals rather than receiving metadata-less reuse.
    /// </summary>
    private async Task<DesiredStatePrCreationResult> CompleteReuseAsync(
        Guid rackId, string fingerprint, GitPullRequestLink link, string rackSlug, Stopwatch stopwatch,
        GitHubOptions options, CancellationToken cancellationToken)
    {
        var published = await AwaitPublishedReuseAsync(rackId, fingerprint, link, options, cancellationToken);
        if (published?.PullRequestNumber is null)
        {
            _logger.LogInformation(
                "Desired-state PR reuse pending: the reservation winner has not published within {WaitMs}ms; "
                + "returning pr-pending for the caller to retry.", options.ReusePublishWaitMs);
            return PendingResult(fingerprint, link.BranchName);
        }

        await AuditLinkAsync(GitPrAuditActions.Reused, published, rackSlug, reused: true, errorCode: null, cancellationToken);
        _logger.LogInformation(
            "Desired-state PR reused prNumber={PrNumber} branch={Branch} elapsedMs={ElapsedMs}",
            published.PullRequestNumber, published.BranchName, stopwatch.ElapsedMilliseconds);
        return ReuseResult(published, fingerprint);
    }

    /// <summary>
    /// Polls the idempotency store (no Key Vault / GitHub call) until the reused link carries published PR
    /// metadata, its Open reservation disappears (winner failed → the caller should retry into a fresh PR), or
    /// the bounded wait elapses. Uses a real monotonic <see cref="Stopwatch"/> for the timeout, NOT the
    /// injected <see cref="TimeProvider"/>, which may be pinned for deterministic branch-name tests.
    /// </summary>
    private async Task<GitPullRequestLink?> AwaitPublishedReuseAsync(
        Guid rackId, string fingerprint, GitPullRequestLink current, GitHubOptions options,
        CancellationToken cancellationToken)
    {
        if (current.PullRequestNumber is not null)
        {
            return current;
        }

        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(1, options.ReusePublishPollMs));
        var waited = Stopwatch.StartNew();
        var link = current;
        while (waited.ElapsedMilliseconds < options.ReusePublishWaitMs)
        {
            await Task.Delay(pollInterval, cancellationToken);
            link = await _links.FindOpenByFingerprintAsync(rackId, fingerprint, cancellationToken);
            if (link is null || link.PullRequestNumber is not null)
            {
                break;
            }
        }

        return link;
    }

    /// <summary>
    /// Substitutes the rack slug into the commit-path template, rejecting any path separator / traversal
    /// sequence outright. Defense-in-depth: a rendered candidate's slug is already DNS-shape-validated before
    /// this point, so this guard normally never fires; it keeps traversal-safety local to the write and robust
    /// against a future refactor that moves or removes the earlier render step (security review).
    /// </summary>
    private static string BuildCommitFilePath(string template, string rackSlug)
    {
        if (rackSlug.Contains('/') || rackSlug.Contains('\\') || rackSlug.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The rack slug is not a valid single path segment for the commit path.");
        }

        return template.Replace("{slug}", rackSlug, StringComparison.Ordinal);
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

    /// <summary>
    /// Closes a reservation after a git failure/refusal and stages its Tier 1 (mandatory-durable) audit
    /// event in the SAME transaction as the status closure (story #308, ADR 0064) — a failed/refused
    /// reservation is as much a durable, security-relevant state transition as a published one. Best-effort
    /// overall (never rethrows): a fault here must never turn a git failure into a 500, at the cost of the
    /// reconciliation note already documented below.
    /// </summary>
    private async Task FailReservationAsync(
        GitPullRequestLink link, string rackSlug, string action, string result, string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            // Close the reservation so a retry can create a fresh PR (the filtered unique index frees the
            // fingerprint). Never force-push; a retry inspects persisted state and opens a new branch/PR.
            link.UpdateStatus(GitPullRequestStatus.Closed, _time.GetUtcNow().UtcDateTime);
            StageAuditOutbox(action, link, rackSlug, reused: false, errorCode, result);
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

    private static DesiredStatePrCreationResult PendingResult(string fingerprint, string branchName)
        => new(
            GatePassed: true,
            Status: "pr-pending",
            Detail: "A pull request for this candidate is being created by a concurrent request; retry shortly "
                + "to obtain its URL and metadata.",
            PullRequestUrl: null,
            BranchName: branchName,
            CandidateFingerprint: fingerprint,
            Reused: false);

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

    /// <summary>Tier 3 (best-effort): a PURE reuse mutates nothing (no reservation status change, no new PR).</summary>
    private Task AuditLinkAsync(
        string action, GitPullRequestLink link, string rackSlug, bool reused, string? errorCode,
        CancellationToken cancellationToken)
    {
        var details = BuildLinkDetails(link, rackSlug, reused, errorCode);
        return WriteAuditAsync(action, link.RackId, errorCode is null ? "success" : "failed", details, cancellationToken);
    }

    /// <summary>
    /// Stages a Tier 1 (mandatory-durable) audit event for a link mutation (publish, or reservation
    /// closure on failure/refusal) directly onto <see cref="_context"/> — the caller commits it in the
    /// SAME <c>SaveChangesAsync</c> as the link change itself (story #308, ADR 0064).
    /// </summary>
    private void StageAuditOutbox(
        string action, GitPullRequestLink link, string rackSlug, bool reused, string? errorCode, string result)
    {
        var details = BuildLinkDetails(link, rackSlug, reused, errorCode);
        var (actorType, actorId) = ResolveActor();
        var envelope = new AuditEventEnvelope(
            actorType, actorId, action, "git-pull-request", link.RackId.ToString(),
            _correlation.CorrelationId, result, RackId: link.RackId,
            DetailsJson: JsonSerializer.Serialize(details));
        _auditOutbox.Add(_context, envelope, _time.GetUtcNow().UtcDateTime);
    }

    private static Dictionary<string, object?> BuildLinkDetails(
        GitPullRequestLink link, string rackSlug, bool reused, string? errorCode)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
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

        return details;
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

    private string ResolveActorId() => ResolveActor().ActorId;

    private (ActorType ActorType, string ActorId) ResolveActor()
        => AuditActorResolver.Resolve(_httpContext.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity()));

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
