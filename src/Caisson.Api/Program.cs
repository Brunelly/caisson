using Caisson.Api.Auditing;
using Caisson.Api.DependencyInjection;
using Caisson.Api.Middleware;
using Caisson.Api.Realtime.Hubs;
using Caisson.Api.Security;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

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

// Discovery orchestration (story #8, ADR 0013): the config-bound rack definitions, driver-touching
// pipeline, job/schedule services and the background runner + scheduler. The API names no driver type;
// driver access is transitive through Caisson.Orchestration at runtime (the guard test stays green).
builder.Services.AddCaissonOrchestration(builder.Configuration);

// Live topology updates (story #9, ADR 0014): the SignalR hub, Redis backplane + per-instance relay,
// heartbeat, metrics and Redis health. Degrades to single-instance SignalR when no Redis is configured.
builder.Services.AddCaissonRealtime(builder.Configuration);

// Correlation-id context (scoped) and the API-access audit writer.
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());
builder.Services.AddScoped<IAuditEventWriter, AuditEventWriter>();

// Role mapping: Entra group/app-role claims → canonical Caisson roles (config-driven, no custom store).
var roleMappings = builder.Configuration
    .GetSection("Authentication:RoleMappings").Get<Dictionary<string, string>>() ?? new();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authentication.IClaimsTransformation>(
    new RoleClaimsTransformation(roleMappings));

// AuthN: JWT bearer against Entra ID / OIDC (config-driven; no custom identity system).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["AzureAd:Authority"];
        if (!string.IsNullOrWhiteSpace(authority))
        {
            options.Authority = authority;
        }

        options.Audience = builder.Configuration["AzureAd:Audience"];
        options.TokenValidationParameters.RoleClaimType = RoleClaimsTransformation.RoleClaimType;
        options.TokenValidationParameters.NameClaimType = "name";

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
const string AngularClientCorsPolicy = "AngularClient";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options => options.AddPolicy(AngularClientCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

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

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger/OpenAPI discloses the full API surface, so it is gated to non-production environments rather
// than exposed unauthenticated on a hosted control plane (ADR 0012). A hosted deployment that needs the
// schema can serve it behind auth via configuration.
if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(AngularClientCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<TopologyHub>("/hubs/topology");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
    .AllowAnonymous();

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

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in integration tests.</summary>
public partial class Program
{
    private Program()
    {
    }
}
