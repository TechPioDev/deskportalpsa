using Desk.Application.Abstractions;
using Desk.Application.Jobs;
using Desk.Infrastructure;
using Desk.Infrastructure.Secrets;
using Desk.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(cfg => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

builder.Services.AddDeskInfrastructure(builder.Configuration);

// A background process has no HTTP request to derive a caller from; audit rows it produces are
// attributed to the system. Registered here, not in shared DI, so the API keeps its real resolver.
builder.Services.AddScoped<Desk.Application.Abstractions.ICurrentUser, SystemCurrentUser>();

// Job handlers
builder.Services.AddScoped<IJobHandler, InboundEventJobHandler>();

// Connectors — the real Autotask + ConnectWise factories come from AddDeskInfrastructure.

// Hosted services
builder.Services.AddHostedService<BackgroundJobPollingService>();
builder.Services.AddHostedService<PollingSyncService>();
builder.Services.AddHostedService<ScheduledReportService>();
builder.Services.AddHostedService<ActivityRollupBackgroundService>();
builder.Services.AddHostedService<EnquiryRetentionBackgroundService>();
builder.Services.AddHostedService<AuthorBackfillService>();

var host = builder.Build();

// The API applies this same guard, but the worker builds its own DI container and starts
// independently — a config drift here would run scheduled PSA syncs against a secret store that
// silently loses every credential on restart, with no symptom until the next sync cycle fails.
if (builder.Environment.IsProduction() &&
    host.Services.GetRequiredService<ISecretStore>() is InMemorySecretStore or FileSecretStore)
{
    throw new InvalidOperationException("Refusing to start in Production without Secrets:EncryptionKey configured.");
}

host.Run();
