using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Core;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class SubscriptionStartupTests
{
    [TestCleanup]
    public void ResetHangfireLogger() => Hangfire.Logging.LogProvider.SetCurrentLogProvider(null);

    // This fixture builds only the subscription composition root; it never reads
    // ProgramData, contacts SSRS/Postmark or substitutes production configuration.
    internal static WebApplication Host(string environment, string connectionString, bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });
        builder.Configuration.Sources.Clear();
        var options = SubscriptionTests.Options;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Subscriptions:Enabled"] = enabled.ToString(),
            ["Subscriptions:ConnectionString"] = options.ConnectionString,
            ["Subscriptions:PortalBaseUrl"] = options.PortalBaseUrl,
            ["Subscriptions:From"] = options.From,
            ["Subscriptions:PostmarkServerToken"] = "synthetic-only"
        });
        builder.Logging.ClearProviders();
        // Unrelated SOAP/catalog services aren't used by these empty test hosts.
        builder.Host.UseDefaultServiceProvider(settings => settings.ValidateOnBuild = false);
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.AddSubscriptions(new PortalOptions());
        builder.Services.AddSingleton<ISubscriptionDelivery, SyntheticSubscriptionDelivery>();
        builder.Services.AddSingleton<ReadinessProbe>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ReadinessProbe>());
        var app = builder.Build();
        // Validation still requires SsrsWeb. Only AFTER validation replace it with
        // a disposable test database, never the developer's operational database.
        app.Services.GetRequiredService<SubscriptionOptions>().ConnectionString = connectionString;
        return app;
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    [DataRow("Staging")]
    public async Task DisabledHostStartsWithoutSqlOrHangfire(string environment)
    {
        await using var app = Host(environment, "invalid connection string;must never be opened", enabled: false);
        await app.StartAsync();
        Assert.IsNull(app.Services.GetService<JobStorage>());
        Assert.IsNull(app.Services.GetService<IBackgroundJobClient>());
        Assert.IsTrue(app.Services.GetRequiredService<ReadinessProbe>().Started);
        await app.StopAsync();
    }

    [TestMethod]
    public async Task ConnectionFailurePreservesSqlExceptionAndStopsWorkers()
    {
        await using var app = Host(Environments.Development,
            "Server=tcp:127.0.0.1,1;Database=SsrsWeb;Integrated Security=true;Encrypt=true;Connect Timeout=1;Connect Retry Count=0;Pooling=false");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.IsInstanceOfType<Microsoft.Data.SqlClient.SqlException>(error.InnerException);
        StringAssert.Contains(error.Message, "Cannot connect");
        StringAssert.Contains(error.Message, "Windows login");
        Assert.IsFalse(error.Message.Contains("Server="));
        Assert.IsFalse(app.Services.GetRequiredService<ReadinessProbe>().Started);
    }

    internal sealed class ReadinessProbe : IHostedService
    {
        internal bool Started { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task VendoredInstallerMatchesPinnedAssembly()
    {
        var actual = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "002-Hangfire.sql"));
        static string Normalize(string script) => string.Join("\n", script.ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');
        Assert.IsTrue(Normalize(Hangfire.SqlServer.SqlServerObjectsInstaller.GetInstallScript("HangFire", enableHeavyMigrations: true)) == Normalize(actual),
            "Vendored SQL must match the pinned assembly, apart from surrounding blank lines, trailing whitespace and line endings.");
        Assert.AreEqual(SubscriptionSchemaCheck.HangfireVersion, Hangfire.SqlServer.SqlServerObjectsInstaller.LatestSchemaVersion);
    }
}
