using Caisson.Api.Auditing;
using Caisson.Api.DependencyInjection;
using Caisson.Api.Middleware;
using Caisson.Api.Realtime.Hubs;
using Caisson.Api.Security;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.DependencyInjection;
using Caisson.Orchestration.RackDefinitions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Environment-gated test-auth scheme (ADR 0018): read the flag and fail closed BEFORE any service
// registration, so a misconfigured production/staging deployment refuses to boot rather than silently
// minting the synthetic principal. Default false; never set to true in a committed non-Development
// appsettings file — CI supplies it solely via the Testing__EnableTestAuth environment variable.
var enableTestAuth = builder.Configuration.GetValue("Testing:EnableTestAuth", defaultValue: false);
TestAuthStartupGuard.Validate(builder.Environment, enableTestAuth);

// Fail-closed JWT authority/audience validation (finding #16) — same "refuse to boot" shape.
var jwtAuthority = builder.Configuration["AzureAd:Authority"];
var jwtAudience = builder.Configuration["AzureAd:Audience"];
JwtAuthorityStartupGuard.Validate(builder.Environment, jwtAuthority, jwtAudience);

// Fail-closed RoleMappings validation (finding #17).
var roleMappingsForValidation = builder.Configuration
    .GetSection("Authentication:RoleMappings").Get<Dictionary<string, string>>() ?? new();
RoleClaimsTransformation.ValidateMappings(builder.Environment, roleMappingsForValidation);

// Structured logging (ADR 0012): compact JSON to the console, with the correlation id enriched onto
// every log line via the LogContext property pushed by CorrelationIdMiddleware.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

var connectionString = ResolveConnectionString(builder.Configuration);

// Persistence: the observed-state DbContext and the story-7 persistence services. No secrets are read
// from appsettings — the connection string comes from CAISSON_DB / ConnectionStrings:Caisson (env/Key
// Vault). The API surface is strictly read-only; the ingestion seam is registered but not exposed.
builder.Services.AddDbContext<CaissonDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddCaissonPersistence();
builder.Services.AddSingleton(TimeProvider.System);

// Finding #14: short-TTL cache for the per-entity-type field-map extraction (immutable snapshots, so no
// invalidation is needed — see TopologyEntitiesController).
builder.Services.AddMemoryCache();

// Discovery orchestration (story #8, ADR 0013): the config-bound rack definitions, driver-touching
// pipeline, job/schedule services and the background runner + scheduler. The API names no driver type;
// driver access is transitive through Caisson.Orchestration at runtime (the guard test stays green).
builder.Services.AddCaissonOrchestration(builder.Configuration);

// Fail-closed rack-definition validation (finding #33/#8): an invalid/empty CredentialsRef, two devices
// colliding to the same credential slug, or a TLS_FINGERPRINT paired with a non-TLS switch port refuses
// to boot rather than run with an ambiguous or silently-ignored security setting.
var rackDefinitions = builder.Configuration.GetSection(RackDefinitionOptions.SectionName).Get<RackDefinitionOptions>()
    ?? new RackDefinitionOptions();
RackDefinitionValidation.Validate(rackDefinitions);

// Fail-closed Redis connection validation (finding #2): an unauthenticated, unencrypted Redis connection
// backing live updates refuses to boot outside Development/Testing — see ADR 0021.
RedisEventAuthenticityStartupGuard.Validate(builder.Environment, builder.Configuration);

// Live topology updates (story #9, ADR 0014): the SignalR hub, Redis backplane + per-instance relay,
// heartbeat, metrics and Redis health. Degrades to single-instance SignalR when no Redis is configured.
builder.Services.AddCaissonRealtime(builder.Configuration);

// Correlation-id context (scoped) and the API-access audit writer.
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

// Off-request-path audit writer (finding #5): a bounded Channel<AuditWriteRequest> decouples the
// synchronous per-request INSERT from the request path; AuditEventBackgroundWriter drains and batches
// it. FullMode=DropWrite (never blocks the request) — a saturated channel drops the newest event with a
// logged warning rather than let the audit trail apply backpressure to reads.
var auditChannel = System.Threading.Channels.Channel.CreateBounded<AuditWriteRequest>(
    new System.Threading.Channels.BoundedChannelOptions(4096)
    {
        FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
builder.Services.AddSingleton(auditChannel.Writer);
builder.Services.AddSingleton(auditChannel.Reader);
builder.Services.AddScoped<IAuditEventWriter, ChannelAuditEventWriter>();
builder.Services.AddHostedService<AuditEventBackgroundWriter>();

// Per-rack access seam (finding #29): allow-all today — see IRackAccessPolicy's own remarks.
builder.Services.AddSingleton<IRackAccessPolicy, AllowAllRackAccessPolicy>();

// Role mapping: Entra group/app-role claims → canonical Caisson roles (config-driven, no custom store).
builder.Services.AddSingleton<Microsoft.AspNetCore.Authentication.IClaimsTransformation>(
    new RoleClaimsTransformation(roleMappingsForValidation));

// AuthN: JWT bearer against Entra ID / OIDC (config-driven; no custom identity system). When the
// environment-gated test-auth scheme is active (Testing:EnableTestAuth, fail-closed outside
// Development/Testing — see TestAuthStartupGuard/ADR 0018), it becomes the DEFAULT scheme instead, so
// the existing fallback RequireAuthenticatedUser + RequireRole policies resolve its synthetic principal
// with zero controller changes. The JWT bearer registration itself is byte-for-byte unchanged either way.
var defaultAuthenticationScheme = enableTestAuth
    ? TestAuthenticationHandler.SchemeName
    : JwtBearerDefaults.AuthenticationScheme;
var authenticationBuilder = builder.Services.AddAuthentication(defaultAuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (!string.IsNullOrWhiteSpace(jwtAuthority))
        {
            options.Authority = jwtAuthority;
            // Finding #16: pin the accepted issuer explicitly rather than trusting whatever the
            // discovery document at Authority happens to advertise — JwtAuthorityStartupGuard already
            // refused to boot on an empty/multi-tenant authority outside Development/Testing, so this is
            // the tenant-specific value in every environment that reaches this line.
            options.TokenValidationParameters.ValidIssuers = new[] { jwtAuthority };
        }

        options.Audience = jwtAudience;
        options.TokenValidationParameters.RoleClaimType = RoleClaimsTransformation.RoleClaimType;
        options.TokenValidationParameters.NameClaimType = "name";
        // Finding #16: pin the accepted signing algorithm so a token signed with a weaker/unexpected
        // algorithm is rejected outright rather than relying on the library's broader default set.
        options.TokenValidationParameters.ValidAlgorithms = new[] { "RS256" };

        // WebSocket auth (story #9): browsers cannot set the Authorization header on the WS upgrade, so
        // read the bearer token from the access_token query-string parameter for the topology hub. Without
        // this, authenticated WS handshakes silently fail and the 401/403 hub ACs cannot be met.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs/topology"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

if (enableTestAuth)
{
    authenticationBuilder.AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
        TestAuthenticationHandler.SchemeName, _ => { });
}

// AuthZ: anonymous → 401 (fallback), authenticated-without-a-recognised-role → 403 (TopologyRead).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(AuthorizationPolicies.TopologyRead, policy => policy.RequireRole(CaissonRoles.All))
    // Story #8: trigger/cancel are Admin+Operator; schedule management is Admin-only. Fail-closed is
    // automatic (fallback → 401 for anonymous; RequireRole → 403 for a recognised-but-insufficient role).
    .AddPolicy(AuthorizationPolicies.DiscoveryTrigger, policy => policy.RequireRole(CaissonRoles.Operators))
    .AddPolicy(AuthorizationPolicies.ScheduleManage, policy => policy.RequireRole(CaissonRoles.Admin));

// CORS (story #10): the Angular SPA is served from a separate origin. No policy existed before this;
// allowed origins are config-driven (Cors:AllowedOrigins) and AllowAnyOrigin is never used. Only
// appsettings.Development.json seeds a default (http://localhost:4200) — a production origin must come
// from environment/Key Vault configuration, never a hard-coded value here.
// Methods are restricted to GET (every topology/audit query endpoint) and POST (required for the
// SignalR hub's negotiate handshake at /hubs/topology/negotiate — TopologySignalRService.buildConnection
// does not set skipNegotiation, so the client always POSTs there before upgrading to a WebSocket) rather
// than AllowAnyMethod(), so the preflight contract mirrors what the SPA actually calls cross-origin.
const string AngularClientCorsPolicy = "AngularClient";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options => options.AddPolicy(AngularClientCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .WithMethods("GET", "POST")
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

// Rate limiting (finding #5): partitioned per authenticated subject (the "oid" claim) so one caller
// cannot exhaust the budget for another. Windows are deliberately generous — the virtual-rack e2e
// seeder's discovery-status polling burst must not trip them — with a materially tighter window
// layered on top for the discovery trigger/cancel endpoints, the only control-plane writes in the API.
// Runs AFTER UseAuthorization() (below) so the oid claim is reliably present by the time it applies.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitionKey(httpContext),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy(RateLimitPolicies.DiscoveryTrigger, httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitionKey(httpContext),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Caisson Control-Plane API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Name = "Authorization",
        Description = "OIDC/Entra JWT bearer token.",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }]
            = Array.Empty<string>(),
    });
});

// Health checks: /health/live is self-only; /health/ready probes the database.
var health = builder.Services.AddHealthChecks();
if (!string.IsNullOrWhiteSpace(connectionString))
{
    health.AddNpgSql(connectionString, name: "db", tags: new[] { "ready" });
}

// Finding #19: HSTS (non-Development) with a one-year max-age.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

// Finding #19: honour X-Forwarded-For/-Proto from a trusted reverse proxy so IsHttps and the client IP
// are correct behind one — otherwise UseHttpsRedirection/HSTS and any IP-based logic would see the
// proxy's own scheme/address instead of the original request's.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // No KnownProxies/KnownNetworks are configured out of the box (deployment-specific); operators behind
    // a reverse proxy must add their proxy's address via configuration for the headers to be honoured —
    // ForwardedHeadersMiddleware ignores XFF/XFP from an unrecognised source by default (fail-closed).
});

var app = builder.Build();

// Finding #19: AllowedHosts "*" (the shipped default, since real deployment hostnames are
// environment-specific and cannot be hard-coded here) disables ASP.NET Core's host-header filtering
// entirely. That is fine in Development; outside it, an operator MUST override AllowedHosts with the
// deployment's real hostname(s) via configuration — surfaced loudly here rather than silently trusting
// any Host header.
if (!app.Environment.IsDevelopment()
    && string.Equals(app.Configuration["AllowedHosts"], "*", StringComparison.Ordinal))
{
    app.Logger.LogError(
        "AllowedHosts is \"*\" under ASPNETCORE_ENVIRONMENT={Environment}. Set AllowedHosts to the " +
        "deployment's real hostname(s) via configuration — host-header filtering is currently disabled.",
        app.Environment.EnvironmentName);
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

app.UseMiddleware<Caisson.Api.Middleware.SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options => options.IncludeQueryInRequestPath = false);

app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger/OpenAPI discloses the full API surface, so it is gated to an explicit allow-list of
// non-production environments (finding #18) rather than a negative "!IsProduction()" check — which
// would also expose it, unauthenticated, on Staging/QA or any custom environment name. A hosted
// deployment that needs the schema can serve it behind auth via configuration.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(AngularClientCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<TopologyHub>("/hubs/topology");
// Health checks are exempt from rate limiting (finding #5) — a load balancer/orchestrator probes these
// frequently and unauthenticated, so the global per-subject limiter would otherwise apply to every
// caller under the same "anonymous" partition key.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
    .AllowAnonymous().DisableRateLimiting();

// Test-auth startup visibility (ADR 0018): make it unmistakable in the logs whenever every request is
// being authenticated as the fixed, non-privileged caisson-ci-e2e principal instead of a real token.
if (enableTestAuth)
{
    app.Logger.LogWarning(
        "Caisson.Api is running with the environment-gated TEST-AUTH SCHEME ACTIVE (Testing:EnableTestAuth=true, " +
        "ASPNETCORE_ENVIRONMENT={Environment}). Every request authenticates as the fixed principal '{Subject}' " +
        "holding only the {Role} role. This must never be enabled outside Development/Testing (enforced at " +
        "startup by TestAuthStartupGuard).",
        app.Environment.EnvironmentName, TestAuthenticationHandler.Subject, CaissonRoles.ReadOnly);
}

// Live-updates startup visibility (story #9, NFR4): make it obvious to operators when cross-instance
// relay is off because Redis is unconfigured.
var realtimeOptions = app.Configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
if (RealtimeOptions.IsRedisEnabled(app.Configuration, out var realtimeRedis))
{
    app.Logger.LogInformation("Live topology updates enabled with Redis backplane (channel {Channel}).", realtimeOptions.EventsChannel);
}
else
{
    app.Logger.LogWarning(
        "Live topology updates running WITHOUT Redis (single-instance, no cross-instance relay) — Realtime:Enabled={Enabled}, Redis configured={RedisConfigured}.",
        realtimeOptions.Enabled, !string.IsNullOrWhiteSpace(realtimeRedis));
}

app.Run();

static string ResolveConnectionString(IConfiguration configuration)
    => Environment.GetEnvironmentVariable("CAISSON_DB")
        ?? configuration.GetConnectionString("Caisson")
        ?? string.Empty;

static string RateLimitPartitionKey(HttpContext httpContext)
    => httpContext.User.FindFirst("oid")?.Value ?? "anonymous";

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in integration tests.</summary>
public partial class Program
{
    private Program()
    {
    }
}
