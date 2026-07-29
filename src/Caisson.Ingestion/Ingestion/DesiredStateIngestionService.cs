using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Git.ReadOnly;
using Caisson.Ingestion.Materializer;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using Caisson.Ingestion.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Caisson.Ingestion.Ingestion;

/// <summary>
/// The single idempotent-insert-under-race implementation shared by <c>GitPollingBackgroundService</c>
/// and the webhook endpoint (story #62, NFR3) — modelled directly on
/// <c>Caisson.Orchestration.Discovery.DiscoveryJobService.EnqueueAsync</c>: insert a running attempt,
/// catch the DB unique-violation race, resolve which index fired, and return the existing run as a
/// no-op. Per-rack processing is partial-accept (Q3): a rack whose file fails validation keeps its
/// previous active version untouched and only gets new <see cref="DesiredStateValidationError"/> rows.
/// </summary>
public sealed class DesiredStateIngestionService : IDesiredStateIngestionService
{
    internal const string CommitShaConstraint = "ux_desired_state_ingestion_run_commit_sha";
    internal const string WebhookDeliveryIdConstraint = "ux_desired_state_ingestion_run_webhook_delivery_id";

    /// <summary>
    /// The fixed service-principal identity stamped on every persisted <see cref="DesiredStateVersion"/>
    /// and its ingestion audit event (story #63, AC1/AC5) — this pipeline has no interactive user
    /// context, so a constant, never a per-request actor, is the correct identity here.
    /// </summary>
    internal const string IngestingServicePrincipal = "desired-state-ingestion";

    private readonly CaissonDbContext _context;
    private readonly IGitRepositoryProvider _git;
    private readonly ITopologyIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly IOptions<GitIngestionOptions> _options;
    private readonly GitIngestionMetrics _metrics;
    private readonly ILogger<DesiredStateIngestionService> _logger;

    public DesiredStateIngestionService(
        CaissonDbContext context,
        IGitRepositoryProvider git,
        ITopologyIdGenerator ids,
        TimeProvider time,
        IOptions<GitIngestionOptions> options,
        GitIngestionMetrics metrics,
        ILogger<DesiredStateIngestionService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IngestionRunResult> RunAsync(
        IngestionTriggerType trigger, string? webhookDeliveryId, Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(webhookDeliveryId))
        {
            var replay = await FindRunIdByWebhookDeliveryIdAsync(webhookDeliveryId, cancellationToken);
            if (replay is { } replayId)
            {
                _metrics.RecordWebhookReplayRejection();
                _logger.LogInformation(
                    "Desired-state ingestion webhook delivery already processed, no-op deliveryId={DeliveryId} runId={RunId} correlationId={CorrelationId}",
                    webhookDeliveryId, replayId, correlationId);
                return new IngestionRunResult(IngestionRunDisposition.IdempotentReplay, replayId);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        _metrics.RecordRunStarted();

        var startedAtUtc = _time.GetUtcNow().UtcDateTime;
        GitCommitInfo commit;
        try
        {
            commit = await _git.GetLatestCommitAsync(_options.Value.Branch, credentialsRef: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await PersistFetchFailureAsync(
                trigger, startedAtUtc, correlationId, webhookDeliveryId, ex, stopwatch, cancellationToken);
        }

        var run = new DesiredStateIngestionRun(
            _ids.NewId(), trigger, startedAtUtc, _options.Value.RepoUrl, _options.Value.Branch,
            correlationId, webhookDeliveryId);
        run.RecordCommit(commit.Sha, commit.Author, commit.CommitTimeUtc, commit.Message);
        _context.DesiredStateIngestionRuns.Add(run);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            Detach(run);

            if (string.Equals(pg.ConstraintName, CommitShaConstraint, StringComparison.Ordinal))
            {
                var existingId = await FindLiveOrProcessedRunIdByCommitShaAsync(commit.Sha, cancellationToken);
                if (existingId is { } id)
                {
                    _logger.LogInformation(
                        "Desired-state ingestion commit already live/processed, no-op commitSha={CommitSha} runId={RunId} correlationId={CorrelationId}",
                        commit.Sha, id, correlationId);
                    return new IngestionRunResult(IngestionRunDisposition.IdempotentReplay, id);
                }
            }

            if (string.Equals(pg.ConstraintName, WebhookDeliveryIdConstraint, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(webhookDeliveryId))
            {
                var existingId = await FindRunIdByWebhookDeliveryIdAsync(webhookDeliveryId, cancellationToken);
                if (existingId is { } id)
                {
                    _metrics.RecordWebhookReplayRejection();
                    return new IngestionRunResult(IngestionRunDisposition.IdempotentReplay, id);
                }
            }

            throw;
        }

        _logger.LogInformation(
            "Desired-state ingestion run started runId={RunId} trigger={Trigger} commitSha={CommitSha} correlationId={CorrelationId}",
            run.Id, trigger, commit.Sha, correlationId);

        await ProcessCommitAsync(run, commit, correlationId, cancellationToken);
        stopwatch.Stop();
        _metrics.RecordRunOutcome(ToOutcome(run.Status), stopwatch.Elapsed);
        _logger.LogInformation(
            "Desired-state ingestion run finished runId={RunId} status={Status} durationMs={DurationMs} correlationId={CorrelationId}",
            run.Id, run.Status, stopwatch.ElapsedMilliseconds, correlationId);

        return new IngestionRunResult(IngestionRunDisposition.Started, run.Id);
    }

    private async Task ProcessCommitAsync(
        DesiredStateIngestionRun run, GitCommitInfo commit, Guid correlationId, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitFileEntry> files;
        try
        {
            files = await _git.EnumerateFilesAsync(commit.Sha, _options.Value.PathGlob, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex, "Failed to enumerate desired-state files runId={RunId} commitSha={CommitSha} correlationId={CorrelationId}",
                run.Id, commit.Sha, correlationId);
            await FailRunAsync(run, IngestionErrorCategory.Parse, $"Failed to enumerate commit files: {ex.Message}", cancellationToken);
            return;
        }

        if (files.Count > _options.Value.MaxFilesPerCommit)
        {
            AddValidationError(
                run, "(commit)", _options.Value.PathGlob, "/",
                $"Commit has {files.Count} matching files, exceeding the {_options.Value.MaxFilesPerCommit}-file bound.");
            run.MarkValidationFailed(_time.GetUtcNow().UtcDateTime);
            await SaveFinalAsync(run, cancellationToken);
            return;
        }

        var succeeded = 0;
        var failed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fallbackRackSlug = Truncate(Path.GetFileNameWithoutExtension(file.Path), DesiredStateSchema.MaxRackSlugLength);

            try
            {
                if (await ProcessFileAsync(run, commit, file, fallbackRackSlug, cancellationToken))
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }
            catch (GitFileTooLargeException ex)
            {
                AddValidationError(run, fallbackRackSlug, file.Path, "/", ex.Message);
                failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Defensive: an unexpected per-file fault must not crash the whole run (NFR8) — this
                // file's outcome becomes a validation failure and the rest of the commit still proceeds.
                _logger.LogError(
                    ex, "Unexpected error processing desired-state file runId={RunId} filePath={FilePath} correlationId={CorrelationId}",
                    run.Id, file.Path, correlationId);
                AddValidationError(run, fallbackRackSlug, file.Path, "/", $"Unexpected error processing this file: {ex.Message}");
                failed++;
            }
        }

        var completedAtUtc = _time.GetUtcNow().UtcDateTime;
        if (succeeded > 0 && failed > 0)
        {
            run.PartiallySucceed(completedAtUtc);
        }
        else if (failed > 0)
        {
            run.MarkValidationFailed(completedAtUtc);
        }
        else
        {
            run.Succeed(completedAtUtc);
        }

        await SaveFinalAsync(run, cancellationToken);
        _logger.LogInformation(
            "Desired-state ingestion run completed runId={RunId} status={Status} succeeded={Succeeded} failed={Failed} correlationId={CorrelationId}",
            run.Id, run.Status, succeeded, failed, correlationId);
    }

    /// <summary>Returns <c>true</c> if the file's rack validated cleanly (and, if changed, was materialised).</summary>
    private async Task<bool> ProcessFileAsync(
        DesiredStateIngestionRun run, GitCommitInfo commit, GitFileEntry file, string fallbackRackSlug,
        CancellationToken cancellationToken)
    {
        if (file.SizeBytes > _options.Value.MaxFileBytes)
        {
            AddValidationError(
                run, fallbackRackSlug, file.Path, "/",
                $"File is {file.SizeBytes} bytes, exceeding the {_options.Value.MaxFileBytes}-byte bound.");
            return false;
        }

        var content = await _git.ReadFileContentAsync(commit.Sha, file.Path, _options.Value.MaxFileBytes, cancellationToken);

        var parsed = DesiredStateYamlParser.Parse(file.Path, content);
        if (!parsed.IsSuccess)
        {
            AddValidationError(run, fallbackRackSlug, parsed.Error!);
            return false;
        }

        var validation = DesiredStateValidator.Validate(file.Path, parsed.Root!);
        if (!validation.IsValid)
        {
            var rackSlug = validation.Document?.RackSlug ?? fallbackRackSlug;
            foreach (var issue in validation.Issues)
            {
                AddValidationError(run, rackSlug, issue);
            }

            return false;
        }

        var document = validation.Document!;
        var contentHash = ComputeContentHash(content);
        var existingActive = await _context.ActiveVersionForRackAsync(document.RackSlug, cancellationToken);
        if (existingActive is not null && string.Equals(existingActive.ContentHash, contentHash, StringComparison.Ordinal))
        {
            // Unchanged since the last successful ingestion — still a success, nothing new to persist, and
            // (AC5) no second ingestion audit event for a replay of the same content.
            return true;
        }

        var desiredStateJson = DesiredStatePayloadSerializer.Serialize(document);
        var createdAtUtc = _time.GetUtcNow().UtcDateTime;
        var version = new DesiredStateVersion(
            _ids.NewId(), document.RackSlug, commit.Sha, run.Id, createdAtUtc, contentHash,
            desiredStateJson, DesiredStateSchema.CurrentSchemaVersion, IngestingServicePrincipal,
            commit.Author, commit.AuthorEmail, commit.CommitTimeUtc);
        var materialized = DesiredStateMaterializer.Materialize(version.Id, document, _ids.NewId);

        var audit = new TopologyAuditEvent(
            _ids.NewId(),
            createdAtUtc,
            ActorType.System,
            IngestingServicePrincipal,
            action: "desired-state.revision.ingested",
            targetType: "desired-state-version",
            correlationId: run.CorrelationId,
            result: "success",
            rackId: null,
            snapshotId: null,
            targetId: version.Id.ToString(),
            detailsJson: BuildIngestionAuditDetails(document.RackSlug, commit.Sha, contentHash));

        _context.DesiredStateVersions.Add(version);
        _context.DesiredRackIntents.Add(materialized.Rack);
        _context.DesiredSwitchIntents.AddRange(materialized.Switches);
        _context.DesiredPortIntents.AddRange(materialized.Ports);
        _context.AuditEvents.Add(audit);
        return true;
    }

    /// <summary>
    /// The rack slug/commit SHA/content hash the ingestion audit event carries (AC5) — well under
    /// <see cref="TopologyAuditEvent.MaxDetailsJsonLength"/>, so no truncation logic is needed here
    /// (contrast <c>TopologySnapshotIngestionService.BuildAuditDetails</c>'s diagnostic-list capping).
    /// </summary>
    private static string BuildIngestionAuditDetails(string rackSlug, string commitSha, string contentHash)
        => JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rackSlug"] = rackSlug,
            ["commitSha"] = commitSha,
            ["contentHash"] = contentHash,
        });

    private async Task SaveFinalAsync(DesiredStateIngestionRun run, CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to persist desired-state ingestion results runId={RunId}", run.Id);
            await FailRunAsync(run, IngestionErrorCategory.Persistence, $"Failed to persist ingestion results: {ex.Message}", cancellationToken);
        }
    }

    private async Task FailRunAsync(
        DesiredStateIngestionRun run, IngestionErrorCategory category, string message, CancellationToken cancellationToken)
    {
        // Detach every other staged change (the versions/intents/errors this run tried to persist) so a
        // persistence fault never leaves the run stuck at Running — only its terminal Failed status is
        // saved (NFR8).
        foreach (var entry in _context.ChangeTracker.Entries()
                     .Where(e => e.State != EntityState.Unchanged && !ReferenceEquals(e.Entity, run))
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        run.Fail(_time.GetUtcNow().UtcDateTime, category, message);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<IngestionRunResult> PersistFetchFailureAsync(
        IngestionTriggerType trigger, DateTime startedAtUtc, Guid correlationId, string? webhookDeliveryId,
        Exception ex, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var category = ClassifyFetchException(ex);
        var run = new DesiredStateIngestionRun(
            _ids.NewId(), trigger, startedAtUtc, _options.Value.RepoUrl, _options.Value.Branch,
            correlationId, webhookDeliveryId);
        run.Fail(startedAtUtc, category, ex.Message);
        _context.DesiredStateIngestionRuns.Add(run);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(pg.ConstraintName, WebhookDeliveryIdConstraint, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(webhookDeliveryId))
        {
            Detach(run);
            var existingId = await FindRunIdByWebhookDeliveryIdAsync(webhookDeliveryId, cancellationToken);
            if (existingId is { } id)
            {
                _metrics.RecordWebhookReplayRejection();
                return new IngestionRunResult(IngestionRunDisposition.IdempotentReplay, id);
            }

            throw;
        }

        stopwatch.Stop();
        _metrics.RecordRunOutcome(IngestionRunOutcome.InfraFailed, stopwatch.Elapsed);
        _logger.LogError(
            ex, "Failed to fetch latest commit for desired-state ingestion category={Category} durationMs={DurationMs} correlationId={CorrelationId}",
            category, stopwatch.ElapsedMilliseconds, correlationId);
        return new IngestionRunResult(IngestionRunDisposition.Started, run.Id);
    }

    private static IngestionRunOutcome ToOutcome(IngestionRunStatus status) => status switch
    {
        IngestionRunStatus.Succeeded => IngestionRunOutcome.Succeeded,
        IngestionRunStatus.PartiallySucceeded => IngestionRunOutcome.PartiallySucceeded,
        IngestionRunStatus.ValidationFailed => IngestionRunOutcome.ValidationFailed,
        _ => IngestionRunOutcome.InfraFailed,
    };

    private void AddValidationError(DesiredStateIngestionRun run, string rackSlug, DesiredStateValidationIssue issue)
        => AddValidationError(run, rackSlug, issue.FilePath, issue.Location, issue.Message, issue.Severity, issue.Line, issue.Column);

    private void AddValidationError(
        DesiredStateIngestionRun run, string rackSlug, string filePath, string location, string message,
        ValidationSeverity severity = ValidationSeverity.Error, int? line = null, int? column = null)
    {
        _context.DesiredStateValidationErrors.Add(new DesiredStateValidationError(
            _ids.NewId(), run.Id, _time.GetUtcNow().UtcDateTime, rackSlug, filePath, location, message, severity, line, column));
        _logger.LogWarning(
            "Desired-state validation error runId={RunId} rackSlug={RackSlug} filePath={FilePath} location={Location} message={Message}",
            run.Id, rackSlug, filePath, location, message);
    }

    private async Task<Guid?> FindRunIdByWebhookDeliveryIdAsync(string webhookDeliveryId, CancellationToken cancellationToken)
        => await _context.DesiredStateIngestionRuns
            .Where(r => r.WebhookDeliveryId == webhookDeliveryId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Guid?> FindLiveOrProcessedRunIdByCommitShaAsync(string commitSha, CancellationToken cancellationToken)
        => await _context.DesiredStateIngestionRuns
            .Where(r => r.CommitSha == commitSha && r.Status != IngestionRunStatus.Failed)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private void Detach(DesiredStateIngestionRun run) => _context.Entry(run).State = EntityState.Detached;

    private static string ComputeContentHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>
    /// Classifies a commit-fetch fault as <see cref="IngestionErrorCategory.Auth"/> when the failure
    /// looks credential-related, else <see cref="IngestionErrorCategory.Network"/>. Internal so tests can
    /// exercise the classification directly.
    /// </summary>
    internal static IngestionErrorCategory ClassifyFetchException(Exception ex)
    {
        var message = ex.Message;
        return !string.IsNullOrEmpty(message)
            && (message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || message.Contains("credential", StringComparison.OrdinalIgnoreCase))
            ? IngestionErrorCategory.Auth
            : IngestionErrorCategory.Network;
    }
}
