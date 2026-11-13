using Dapper;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using SsrsPortal.Configuration;
using SsrsPortal.Core;
using SsrsPortal.Services;
using SsrsPortal.Soap;

namespace SsrsPortal.Subscriptions;

public static class SubscriptionHosting
{
    public static void AddSubscriptions(this WebApplicationBuilder builder, PortalOptions portal)
    {
        var options = ExternalConfigurationLoader.Bind<SubscriptionOptions>(builder.Configuration, "Subscriptions", required: false);
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(45));
        builder.Services.AddSingleton<SubscriptionSoap>();
        builder.Services.AddSingleton(s => new SubscriptionReportRunner(s.GetRequiredService<CatalogService>(),
            s.GetRequiredService<SubscriptionSoap>().Parameters, s.GetRequiredService<SubscriptionSoap>().Client, options));
        builder.Services.AddSingleton<ISubscriptionReportRunner>(s => s.GetRequiredService<SubscriptionReportRunner>());
        builder.Services.AddSingleton<SubscriptionService>();
        builder.Services.AddSingleton<SubscriptionWorker>();
        if (portal.UseMock && portal.TestAuth)
        {
            // Existing guards forbid both flags outside Development. Synthetic
            // verification never contacts SQL, Hangfire storage or Postmark.
            builder.Services.AddSingleton<SyntheticSubscriptionStore>();
            builder.Services.AddSingleton<ISubscriptionStore>(s => s.GetRequiredService<SyntheticSubscriptionStore>());
            builder.Services.AddSingleton<SyntheticSubscriptionDelivery>();
            builder.Services.AddSingleton<ISubscriptionDelivery>(s => s.GetRequiredService<SyntheticSubscriptionDelivery>());
        }
        else
        {
            builder.Services.AddSingleton<ISubscriptionStore, SqlSubscriptionStore>();
            builder.Services.AddHttpClient<ISubscriptionDelivery, PostmarkDelivery>(http => http.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            if (options.Enabled)
            {
                builder.Services.AddHangfire(config => config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
                    .UseStorage(SubscriptionSchemaCheck.CreateStorage(options, builder.Environment)));
                builder.Services.AddHangfireServer(server =>
                {
                    server.WorkerCount = options.Workers;
                    server.Queues = ["subscriptions"];
                    server.ShutdownTimeout = TimeSpan.FromSeconds(30);
                    server.CancellationCheckInterval = TimeSpan.FromSeconds(1);
                });
                builder.Services.AddSingleton<SubscriptionDispatcher>();
                builder.Services.AddHostedService<SubscriptionRegistration>();
            }
        }
        // No dashboard: Staff is a report-viewing policy, not an administrative role.
    }
}

internal static class SubscriptionSchemaCheck
{
    internal const int ApplicationVersion = 1;
    internal const int HangfireVersion = 9;

    internal static SqlServerStorage CreateStorage(SubscriptionOptions options, IHostEnvironment environment)
    {
        // The host constructs ALL hosted services before calling StartAsync. Hangfire
        // resolves storage during that construction, so gate its storage factory rather
        // than relying on the registration order of a separate schema hosted service.
        using var db = new SqlConnection(options.ConnectionString);
        try
        {
            db.Open();
        }
        catch (SqlException e)
        {
            throw new InvalidOperationException($"Cannot connect to the subscription database (SQL error {e.Number}). Check server/database availability, Windows login/database access, network connectivity and TLS settings. Inspect InnerException for the SQL failure.", e);
        }

        EnsureSchemas(db, environment.IsDevelopment());
        return new SqlServerStorage(options.ConnectionString, new SqlServerStorageOptions
        {
            // Initialization above is Development-only and skipped for schema 9.
            // Never let storage silently attempt DDL or swallow an installer failure.
            PrepareSchemaIfNecessary = false, SchemaName = "HangFire", TryAutoDetectSchemaDependentOptions = false,
            UseRecommendedIsolationLevel = true, DisableGlobalLocks = true,
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5), QueuePollInterval = TimeSpan.FromSeconds(5)
        });
    }

    internal static void EnsureSchemas(SqlConnection db, bool development)
    {
        RequireVersion(ReadVersions(db, "subscriptions", "SchemaVersion", ApplicationVersion), "subscriptions", ApplicationVersion);
        var versions = ReadVersions(db, "HangFire", "Schema", HangfireVersion, allowMissing: development);
        if (development && versions.Length == 0)
        {
            try
            {
                // Supported Hangfire installer, on the same connection. Unlike storage's
                // best-effort preparation, it propagates installation/permission errors.
                SqlServerObjectsInstaller.Install(db, "HangFire", enableHeavyMigrations: true);
            }
            catch (SqlException e)
            {
                throw new InvalidOperationException($"Development could not initialize [HangFire] schema version {HangfireVersion} (SQL error {e.Number}). The connecting Windows identity needs CREATE SCHEMA, CREATE TABLE and permission to alter the HangFire schema for initial installation. Ask an administrator to provision Database/002-Hangfire.sql instead, then grant runtime permissions with 003-SubscriptionPermissions.sql. Inspect InnerException for the SQL failure.", e);
            }
            versions = ReadVersions(db, "HangFire", "Schema", HangfireVersion);
        }
        RequireVersion(versions, "HangFire", HangfireVersion);
    }

    private static int[] ReadVersions(SqlConnection db, string schema, string table, int expected, bool allowMissing = false)
    {
        try
        {
            // Identifiers are fixed by the calls above, never configuration or user input.
            return db.Query<int>($"SELECT [Version] FROM [{schema}].[{table}]" +
                (schema == "subscriptions" ? " WHERE [Id]=1;" : ";")).ToArray();
        }
        catch (SqlException e) when (allowMissing && e.Number == 208) { return []; }
        catch (SqlException e)
        {
            var reason = e.Number switch
            {
                208 => $"[{schema}].[{table}] is missing or not visible; expected schema version {expected}, observed missing. Run the administrator migration",
                229 => $"cannot read [{schema}].[{table}]; expected schema version {expected}. Check SELECT permission for the connecting Windows identity",
                _ => $"cannot query [{schema}].[{table}]; expected schema version {expected}. Check database connectivity, object definition and access"
            };
            throw new InvalidOperationException($"Subscription database {reason} (SQL error {e.Number}). Inspect InnerException for the SQL failure.", e);
        }
    }

    private static void RequireVersion(int[] versions, string schema, int expected)
    {
        if (versions.Length == 1 && versions[0] == expected) { return; }
        var observed = versions.Length == 0 ? "none (no version row)" : string.Join(", ", versions);
        throw new InvalidOperationException($"Subscription database [{schema}] schema version mismatch: expected {expected}, observed {observed}. Run the matching administrator migration; existing versioned schemas are never automatically upgraded.");
    }
}
internal sealed class SubscriptionRegistration(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        jobs.AddOrUpdate<SubscriptionDispatcher>("ssrs-subscriptions-dispatch", "subscriptions", dispatcher => dispatcher.DispatchAsync(CancellationToken.None),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
internal sealed class SubscriptionSoap : IDisposable
{
    private readonly SoapTransport transport;
    internal SsrsClient Client { get; }
    internal ParameterService Parameters { get; }
    public SubscriptionSoap(HttpClient http, PortalOptions portal, SubscriptionOptions options, ILogger<SoapTransport> logger)
    {
        transport = new SoapTransport(http, portal, logger, operationTimeout: TimeSpan.FromSeconds(options.ExecutionSeconds), suppressFailureContent: true);
        Client = new SsrsClient(transport, portal);
        Parameters = new ParameterService(Client, portal);
    }
    public void Dispose() => transport.Dispose();
}
