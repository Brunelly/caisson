using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Webhook;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Caisson.Api.Controllers;

/// <summary>
/// The Git provider webhook trigger for desired-state ingestion (story #62, AC1/NFR1/NFR2).
/// <see cref="AllowAnonymousAttribute"/> is deliberate and correct here — the HMAC signature over the
/// raw body IS the authentication, not a bearer token, so this endpoint is excluded from the JWT
/// fallback policy rather than gated by it.
/// </summary>
[ApiController]
[Route("api/ingestion/git/webhook")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class GitWebhookController : ControllerBase
{
    private readonly IWebhookSignatureVerifier _verifier;
    private readonly DesiredStateIngestionSignal _signal;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<GitWebhookController> _logger;

    public GitWebhookController(
        IWebhookSignatureVerifier verifier, DesiredStateIngestionSignal signal, ICorrelationContext correlation,
        ILogger<GitWebhookController> logger)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Verifies <c>X-Hub-Signature-256</c> over the raw request body and, on success, enqueues
    /// webhook-triggered ingestion (drained by <c>DesiredStateIngestionRunner</c>) and returns
    /// immediately (AC1: "the response is non-blocking"). An invalid or missing signature is 401 —
    /// never 403, since no authenticated-wrong-role state exists for this endpoint.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.GitWebhook)]
    [ProducesResponseType(typeof(GitWebhookAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();
        using var bodyStream = new MemoryStream();
        await Request.Body.CopyToAsync(bodyStream, cancellationToken);
        var rawBody = bodyStream.ToArray();
        Request.Body.Position = 0;

        var signatureHeader = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!_verifier.Verify(rawBody, signatureHeader))
        {
            _logger.LogWarning(
                "Git webhook signature verification failed correlationId={CorrelationId}", _correlation.CorrelationId);
            return Unauthorized();
        }

        var deliveryId = Request.Headers["X-GitHub-Delivery"].FirstOrDefault();
        _signal.Notify(new WebhookIngestionRequest(deliveryId, _correlation.CorrelationId));

        _logger.LogInformation(
            "Git webhook accepted deliveryId={DeliveryId} correlationId={CorrelationId}",
            deliveryId, _correlation.CorrelationId);

        return Accepted(new GitWebhookAcceptedResponse(_correlation.CorrelationId));
    }
}
