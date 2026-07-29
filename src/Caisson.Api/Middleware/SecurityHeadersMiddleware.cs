namespace Caisson.Api.Middleware;

/// <summary>
/// Sets a fixed set of defensive response headers on every API response (finding #19). The control
/// plane serves only JSON — never HTML — so the CSP is deliberately the tightest possible
/// (<c>default-src 'none'</c>): there is no script/style/image surface to allow.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        });

        return _next(context);
    }
}
