using Serilog.Context;

namespace Caisson.Api.Middleware;

/// <summary>
/// Honours an inbound <c>X-Correlation-Id</c> header when it is a valid id, otherwise generates one
/// (AC5). The resolved id is stashed on the scoped <see cref="ICorrelationContext"/>, pushed into the
/// Serilog <see cref="LogContext"/> so every log line for the request carries it, and echoed back in
/// the response header.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    /// <summary>The correlation-id header name (request and response).</summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correlation);

        var correlationId = ResolveCorrelationId(context);
        ((CorrelationContext)correlation).CorrelationId = correlationId;

        var idText = correlationId.ToString();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = idText;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", idText))
        {
            await _next(context);
        }
    }

    private static Guid ResolveCorrelationId(HttpContext context)
        => context.Request.Headers.TryGetValue(HeaderName, out var provided)
            && Guid.TryParse(provided.ToString(), out var parsed)
                ? parsed
                : Guid.NewGuid();
}
