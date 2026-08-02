using System.Security.Claims;
using Caisson.Api.Auditing;
using Caisson.Api.Security;
using Caisson.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Api.Tests.Security;

/// <summary>
/// Guards the audit-evasion boundary on the Tier 2 (durable-first-N) denial path (story #308, ADR 0064):
/// the DENIED caller must never be able to influence whether their own denial is recorded. The 403 has
/// already been decided by the time <see cref="ForbidLoggingAuthorizationResultHandler"/> runs, so the
/// caller aborting the connection the instant they see it must not cancel the durable write.
/// </summary>
public sealed class ForbidLoggingAuthorizationResultHandlerTests
{
    [Fact]
    public async Task The_denial_audit_is_not_cancelled_by_the_denied_caller_aborting_the_request()
    {
        var writer = new TokenCapturingDenialAuditWriter();
        var context = BuildContext(writer);

        // The denied caller drops the connection the moment the 403 lands — the single move that, if the
        // request token were used for persistence, would delete the evidence of their own denial.
        using var abortedByCaller = new CancellationTokenSource();
        await abortedByCaller.CancelAsync();
        context.RequestAborted = abortedByCaller.Token;

        await HandleForbiddenAsync(context);

        writer.WasCalled.Should().BeTrue("the Tier 2 writer must still be invoked for an aborted request");
        writer.CapturedToken.IsCancellationRequested.Should().BeFalse(
            "the first-N denial write must use a token the denied caller cannot influence — otherwise " +
            "aborting the connection on the 403 suppresses the durable audit record (audit evasion)");
    }

    [Fact]
    public async Task The_denial_audit_token_is_not_the_request_aborted_token_even_when_that_token_is_live()
    {
        var writer = new TokenCapturingDenialAuditWriter();
        var context = BuildContext(writer);

        using var callerControlled = new CancellationTokenSource();
        context.RequestAborted = callerControlled.Token;

        await HandleForbiddenAsync(context);

        writer.WasCalled.Should().BeTrue();

        // Cancelling AFTER the handler returns proves the captured token is not merely un-cancelled yet:
        // it must not be wired to the caller's request lifetime at all.
        await callerControlled.CancelAsync();
        writer.CapturedToken.IsCancellationRequested.Should().BeFalse(
            "the captured token must not be linked to the denied caller's request lifetime");
    }

    private static async Task HandleForbiddenAsync(HttpContext context)
    {
        var handler = new ForbidLoggingAuthorizationResultHandler(
            NullLogger<ForbidLoggingAuthorizationResultHandler>.Instance);
        var policy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();

        await handler.HandleAsync(
            _ => Task.CompletedTask,
            context,
            policy,
            Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult.Forbid());
    }

    private static DefaultHttpContext BuildContext(TokenCapturingDenialAuditWriter writer)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new StubRequestServices(writer),
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "denied-caller") }, "test")),
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/racks/apply";
        return context;
    }

    /// <summary>Captures the cancellation token the handler hands the Tier 2 writer.</summary>
    private sealed class TokenCapturingDenialAuditWriter : IAuthorizationDenialAuditWriter
    {
        public bool WasCalled { get; private set; }

        public CancellationToken CapturedToken { get; private set; }

        public Task RecordDenialAsync(
            ActorType actorType,
            string actorId,
            string endpoint,
            string outcome,
            Guid? rackId,
            Guid correlationId,
            string? detailsJson,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            CapturedToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The minimal per-request service surface the handler touches: the Tier 2 writer it audits through,
    /// and the <see cref="IAuthenticationService"/> the wrapped framework handler needs to emit the 403.
    /// </summary>
    private sealed class StubRequestServices : IServiceProvider
    {
        private readonly IAuthorizationDenialAuditWriter _writer;
        private readonly StubAuthenticationService _authentication = new();

        public StubRequestServices(IAuthorizationDenialAuditWriter writer) => _writer = writer;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IAuthorizationDenialAuditWriter))
            {
                return _writer;
            }

            return serviceType == typeof(IAuthenticationService) ? _authentication : null;
        }
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }
}
