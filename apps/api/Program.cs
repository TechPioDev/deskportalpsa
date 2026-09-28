using System.Threading.RateLimiting;
using Desk.Api.Auth;
using Desk.Api.Middleware;
using Desk.Application.Abstractions;
using Desk.Infrastructure;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Secrets;
using Desk.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logging (JSON to console; OTLP exporter is added below for traces/metrics).
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

var config = builder.Configuration;

// ---- Infrastructure (DbContext, tenant context, secret store) ----
builder.Services.AddDeskInfrastructure(config);

// ---- Connectors ----
// Real Autotask and ConnectWise factories are registered by AddDeskInfrastructure. Both Wave-1
// providers now have production connectors; the mock is retained only for tests.

// ---- Identity plumbing ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IClaimsTransformation, DeskClaimsTransformation>();

// Local mode: run without external dependencies (in-memory DB/secrets + dev auto-login). Only
// honoured in Development — a guard below refuses it in Production.
var localMode = builder.Environment.IsDevelopment() && config.GetValue("LocalMode:Enabled", false);

// ---- AuthN ----
if (localMode)
{
    // Dev auto-login as the seeded admin — never registered outside Development local mode.
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = config["Keycloak:Authority"];
            options.Audience = config["Keycloak:Audience"];
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(config["Keycloak:Audience"]);
            options.TokenValidationParameters.ValidateLifetime = true;
        });
}

// ---- AuthZ: permission-claim policies ----
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddAuthorization();

// ---- API surface ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ---- Health ----
builder.Services.AddHealthChecks()
    .AddDbContextCheck<DeskDbContext>("database");

// ---- Rate limiting (global fixed window) ----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Two limits, chained: the person's own allowance, and a much larger ceiling for their whole
    // organization. Per organization alone made colleagues compete for one budget; per person alone
    // would let one tenant's headcount set the load the host has to carry. See RateLimitPartitions.
    o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: RateLimitPartitions.UserKey(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimitPartitions.PerUserPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                })),
        PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartitions.OrganizationKey(ctx) is { } org
                ? RateLimitPartition.GetFixedWindowLimiter(org, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimitPartitions.PerOrganizationPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                })
                // Anonymous traffic is bounded by the per-address limit above and by the narrower
                // policies on the routes that allow it, so it needs no organization ceiling.
                : RateLimitPartition.GetNoLimiter<string>("anonymous")));

    // The public forms are the one unauthenticated write in the product, so they get their own
    // much tighter budget, partitioned by IP: the global allowance is sized for a signed-in app
    // session and would let a single host post thousands of enquiries an hour.
    // Alerts from monitoring tools get their own budget, PER SOURCE KEY. Every delivery arrives
    // through the web app, so to the API they share one remote address: without this, one noisy
    // tool would spend the whole anonymous allowance and silence every other tool — and the desk's
    // own traffic with it. Sized for a tool reporting a few conditions a second during a storm.
    o.AddPolicy("alert-intake", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Request.Headers.TryGetValue("X-Desk-Alert-Key", out var alertKey) && alertKey.Count > 0
            ? "alert:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(alertKey.ToString())))[..16]
            : "alert:anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));

    o.AddPolicy("public-forms", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
});

// ---- CORS (allowlist from config) ----
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("web", p => p
    .WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// ---- Request size cap ----
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 25 * 1024 * 1024);

// NOTE: OpenTelemetry OTLP export is deferred to the observability phase. The current
// OTLP exporter package carries an unpatched moderate advisory (GHSA-4625-4j76-fww9) and
// the strict NU1902 gate (TreatWarningsAsErrors) rightly blocks it. Structured Serilog logs
// with correlation ids cover Phase-2 observability.

var app = builder.Build();

// Local mode must never be active in Production.
if (app.Environment.IsProduction() && config.GetValue("LocalMode:Enabled", false))
{
    throw new InvalidOperationException("LocalMode:Enabled is not permitted in Production.");
}

// Production must not fall back to a local (in-memory / file) secret store.
if (app.Environment.IsProduction() &&
    app.Services.GetRequiredService<ISecretStore>() is InMemorySecretStore or FileSecretStore)
{
    throw new InvalidOperationException("Refusing to start in Production without Secrets:EncryptionKey configured.");
}

// Startup DB init. Local mode creates the in-memory schema and seeds a demo org + admin; otherwise
// apply migrations for local/dev bring-up (in production run migrations as an explicit deploy step).
if (localMode)
{
    using var startupScope = app.Services.CreateScope();
    startupScope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
    var startupDb = startupScope.ServiceProvider.GetRequiredService<DeskDbContext>();
    await startupDb.Database.EnsureCreatedAsync();
    await DatabaseSeeder.SeedBuiltInRolesAsync(startupDb);
    await DatabaseSeeder.SeedBuiltInPermissionTemplatesAsync(startupDb);
    await DatabaseSeeder.SeedLocalDemoAsync(startupDb);
    await DatabaseSeeder.SeedDefaultDepartmentsAsync(startupDb);
    app.Logger.LogWarning("LOCAL MODE: in-memory DB + dev auto-login active. Not for production.");
}
else if (app.Environment.IsDevelopment() || config.GetValue("RunMigrationsOnStartup", false))
{
    using var startupScope = app.Services.CreateScope();
    startupScope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
    var startupDb = startupScope.ServiceProvider.GetRequiredService<DeskDbContext>();
    await startupDb.Database.MigrateAsync();
    await DatabaseSeeder.SeedBuiltInRolesAsync(startupDb);
    await DatabaseSeeder.SeedBuiltInPermissionTemplatesAsync(startupDb);

    // First-admin bootstrap. A fresh non-local deployment has zero users, and sign-in binds an IdP
    // subject to an existing row by email — so without this, no one can ever log in.
    var bootstrapEmail = config["Bootstrap:AdminEmail"];
    if (!string.IsNullOrWhiteSpace(bootstrapEmail))
        await DatabaseSeeder.SeedBootstrapAdminAsync(
            startupDb,
            config["Bootstrap:OrganizationName"] ?? "MSP",
            config["Bootstrap:OrganizationSlug"] ?? "msp",
            bootstrapEmail,
            config["Bootstrap:AdminName"] ?? bootstrapEmail);

    // Depends on SeedBootstrapAdminAsync above having created the organization row.
    await DatabaseSeeder.SeedDefaultDepartmentsAsync(startupDb);

    app.Logger.LogInformation("Database migrated and built-in roles/templates/departments seeded.");
}

// ---- Pipeline order matters ----
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();
app.UseCors("web");
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>(); // after auth, before controllers
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready");

app.Run();

/// <summary>Exposed so the integration/unit test host can reference the API entrypoint.</summary>
public partial class Program;
