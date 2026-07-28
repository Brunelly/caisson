using Caisson.Api.Auditing;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.Persistence;
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
    });

// AuthZ: anonymous → 401 (fallback), authenticated-without-a-recognised-role → 403 (TopologyRead).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(AuthorizationPolicies.TopologyRead, policy => policy.RequireRole(CaissonRoles.All));

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

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
    .AllowAnonymous();

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
