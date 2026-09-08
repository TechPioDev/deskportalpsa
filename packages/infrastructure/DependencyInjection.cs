using Desk.Application.Abstractions;
using Desk.Application.Authorization;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Application.Resilience;
using Desk.Application.Admin;
using Desk.Application.Analytics;
using Desk.Application.Attachments;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Authorization;
using Desk.Application.Sync;
using Desk.Application.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Connectors;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.Infrastructure.Secrets;
using Desk.Infrastructure.Security;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDeskInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Local mode runs with zero external dependencies (in-memory DB + secret store) for a
        // no-Docker demo. Otherwise, connect to Postgres as usual.
        var localMode = config.GetValue("LocalMode:Enabled", false);
        if (localMode)
        {
            // Persist local-demo data to a SQLite file so connections, tickets and mappings survive
            // restarts. Schema is created via EnsureCreated (no migrations) — delete the file if the
            // entity model changes. Path is configurable; defaults to desk-local.db in the run dir.
            var sqlitePath = config.GetValue<string>("LocalMode:SqlitePath") ?? "desk-local.db";
            services.AddDbContext<DeskDbContext>(o => o.UseSqlite($"Data Source={sqlitePath}"));
        }
        else
        {
            var connectionString = config.GetConnectionString("Postgres")
                ?? Environment.GetEnvironmentVariable("DESK_DB_CONNECTION")
                ?? throw new InvalidOperationException("No Postgres connection string configured (ConnectionStrings:Postgres).");
            services.AddDbContext<DeskDbContext>(o =>
                o.UseNpgsql(connectionString, npg => npg.MigrationsAssembly(typeof(DeskDbContext).Assembly.FullName)));
        }

        services.AddSingleton(TimeProvider.System);

        // One TenantContext instance per scope, exposed under both interfaces.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ISettableTenantContext>(sp => sp.GetRequiredService<TenantContext>());

        if (localMode)
        {
            // File-backed so credentials persist with the SQLite DB across restarts (local only).
            var secretsPath = config.GetValue<string>("LocalMode:SecretsPath") ?? "desk-local-secrets.json";
            services.AddSingleton<ISecretStore>(_ => new FileSecretStore(secretsPath));
        }
        else
        {
            AddSecretStore(services, config);
        }

        // Integration framework (Phase 3)
        services.AddSingleton<IMappingEngine, MappingEngine>();
        services.AddSingleton<IResilientExecutor>(_ => new ResilientExecutor());
        services.AddScoped<ISyncEventStore, SyncEventStore>();
        services.AddScoped<IConnectorResolver, ConnectorResolver>();
        services.AddScoped<IConnectionSyncRunner, ConnectionSyncRunner>();

        // Sync engine + real connectors (Phases 4-5)
        services.AddScoped<ITicketSyncService, TicketSyncService>();
        services.AddHttpClient();
        services.AddScoped<IConnectorFactory, AutotaskConnectorFactory>();
        services.AddScoped<IConnectorFactory, ConnectWiseConnectorFactory>();

        // Optional SSRF egress guard on connector HttpClients (blocks private/reserved hosts).
        if (config.GetValue("Connectors:BlockPrivateEgress", false))
        {
            var allowed = new HashSet<string>(
                config.GetSection("Connectors:AllowedHosts").Get<string[]>() ?? [],
                StringComparer.OrdinalIgnoreCase);
            services.AddTransient(_ => new EgressGuard(allowed));
            services.AddHttpClient("autotask").AddHttpMessageHandler(sp => sp.GetRequiredService<EgressGuard>());
            services.AddHttpClient("connectwise").AddHttpMessageHandler(sp => sp.GetRequiredService<EgressGuard>());
        }

        // Assistant. The HttpClient is named and its base address fixed here, so the service can
        // never be pointed at some other host by configuration.
        services.AddHttpClient<Desk.Application.Assistant.IAssistantModel, Desk.Infrastructure.Assistant.GeminiModel>(c =>
        {
            c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            c.Timeout = TimeSpan.FromSeconds(45);
        });
        services.AddScoped<Desk.Application.Assistant.IAssistantService, Desk.Infrastructure.Assistant.AssistantService>();

        // Client portal (Phase 6)
        services.AddScoped<IClientAccessResolver, ClientAccessResolver>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<ITicketReadService, TicketReadService>();
        services.AddScoped<ITicketCommandService, TicketCommandService>();
        services.AddScoped<IEffectivePermissionService, EffectivePermissionService>();
        services.AddScoped<ITicketScopeQuery, TicketScopeQuery>();

        // Client control panel (CP-1 → CP-4 + reports)
        services.AddScoped<Desk.Application.ControlPanel.IControlPanelService, Desk.Infrastructure.ControlPanel.ControlPanelService>();
        services.AddScoped<Desk.Application.ControlPanel.IAccountSettingsService, Desk.Infrastructure.ControlPanel.AccountSettingsService>();
        services.AddSingleton<Desk.Application.ControlPanel.IReportDelivery, Desk.Infrastructure.ControlPanel.LoggingReportDelivery>();
        services.AddScoped<Desk.Application.ControlPanel.IScheduledReportRunner, Desk.Infrastructure.ControlPanel.ScheduledReportRunner>();
        services.AddScoped<Desk.Application.ControlPanel.IClientContentService, Desk.Infrastructure.ControlPanel.ClientContentService>();

        // Analytics (Phase 7)
        services.AddSingleton<IProductivityScorer, ProductivityScorer>();
        services.AddScoped<ITechnicianMetricsService, TechnicianMetricsService>();

        // Attachments (validate -> scan -> quarantine/store -> signed URL)
        services.AddSingleton(new AttachmentStorageOptions
        {
            SigningKey = config["Attachments:SigningKey"] ?? "dev-attachment-signing-key",
            PublicBaseUrl = config["Attachments:PublicBaseUrl"] ?? "http://localhost:5080",
        });
        services.AddSingleton(new AttachmentPolicy());
        // Disk-backed whenever a path is configured (a mounted volume in deployment), so attachment
        // bytes survive restarts. In-memory is the last resort only — a portal that silently loses
        // every file on redeploy is not a testing environment, it is a trap.
        var blobRoot = localMode
            ? config.GetValue<string>("LocalMode:AttachmentsPath") ?? "desk-local-files"
            : config.GetValue<string>("Attachments:StoragePath");
        if (!string.IsNullOrWhiteSpace(blobRoot))
            services.AddSingleton<IObjectStorage>(sp => new FileObjectStorage(
                sp.GetRequiredService<AttachmentStorageOptions>(), sp.GetRequiredService<TimeProvider>(), blobRoot));
        else
            services.AddSingleton<IObjectStorage, InMemoryObjectStorage>();
        services.AddSingleton<IMalwareScanner, HeuristicMalwareScanner>();
        services.AddScoped<IAttachmentService, AttachmentService>();

        // Administration (Phase 8)
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddSingleton<IConnectionFieldCache, ConnectionFieldCache>();
        services.AddScoped<IConnectionAdminService, ConnectionAdminService>();
        services.AddScoped<IMappingAdminService, MappingAdminService>();
        services.AddScoped<IJobMonitorService, JobMonitorService>();
        services.AddScoped<IIntegrationHealthService, IntegrationHealthService>();
        services.AddScoped<ITicketResyncService, Sync.TicketResyncService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<ITechnicianProvisioningService, TechnicianProvisioningService>();
        services.AddScoped<Desk.Application.Analytics.IActivityRecorder, Desk.Infrastructure.Analytics.ActivityRecorder>();
        services.AddScoped<Desk.Application.Analytics.IClientWorkloadService, Desk.Infrastructure.Analytics.ClientWorkloadService>();
        services.AddScoped<Desk.Application.Analytics.IPortalCoverageService, Desk.Infrastructure.Analytics.PortalCoverageService>();
        services.AddScoped<Desk.Application.Analytics.IActivityRollupService, Desk.Infrastructure.Analytics.ActivityRollupService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        // Retention for public enquiries. The default matches what the privacy policy publishes;
        // setting it to 0 keeps them forever, and then the policy has to say so.
        services.AddSingleton(new Application.Marketing.EnquiryRetentionPolicy
        {
            Months = config.GetValue("Enquiries:RetentionMonths", 24),
        });
        services.AddScoped<Application.Marketing.IEnquiryService, Marketing.EnquiryService>();

        return services;
    }

    private static void AddSecretStore(IServiceCollection services, IConfiguration config)
    {
        var encryptionKey = config["Secrets:EncryptionKey"];

        if (!string.IsNullOrWhiteSpace(encryptionKey))
        {
            var options = new SecretEncryptionOptions { Key = encryptionKey };
            // Constructed eagerly so a malformed key fails at startup, not on the first credential
            // write or read.
            services.AddSingleton(new SecretCipher(options));
            // Depends on the scoped DbContext, so this registration must be scoped too.
            services.AddScoped<ISecretStore, EncryptedDbSecretStore>();
        }
        else
        {
            // Dev/test fallback. Production startup asserts a real store is configured.
            services.AddSingleton<ISecretStore, InMemorySecretStore>();
        }
    }
}
