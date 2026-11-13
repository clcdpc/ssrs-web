using System.Text.RegularExpressions;
using Clc.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NLog;
using NLog.Layouts;
using NLog.Targets;
using SsrsPortal.Configuration;
using SsrsPortal.Core;
using SsrsPortal.Logging;
using SsrsPortal.Mock;
using SsrsPortal.Soap;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SsrsPortal.Tests;

public sealed class FileLoggingTests
{
    [Fact]
    public async Task ILoggerWritesCategoryTemplatePropertiesScopesAndFullExceptionChainOnce()
    {
        await using var host = new PortalFactory();
        using var browser = host.Browser();
        var logger = Logger(host);
        using (logger.BeginScope(new Dictionary<string, object?> { ["Trace"] = "trace-sentinel", ["ReportPath"] = "/Polaris/Holds Ratio" }))
        using (logger.BeginScope("named-scope"))
        {
            logger.LogError(ThrownException(), "Execution diagnostic sentinel {Operation}; duration {DurationMs}.", "Render", 1842);
        }
        var text = Read(host);
        foreach (var expected in new[] { "SsrsPortal.LoggingProbe", "[ERROR]", "trace-sentinel", "/Polaris/Holds Ratio", "Render", "\"DurationMs\":1842", "{Operation}", "named-scope", "outer exception sentinel", "inner exception sentinel", nameof(ThrowRoot), "InvalidOperationException", "Inner exception" })
        {
            Assert.Contains(expected, text);
        }
        Assert.Single(Regex.Matches(text, "Message=Execution diagnostic sentinel"));
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[ERROR\]", text);
    }

    [Fact]
    public async Task DefaultLevelsCaptureApplicationDebugAndSuppressFrameworkInformation()
    {
        await using var host = new PortalFactory(); using var browser = host.Browser();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("SsrsPortal.Probe").LogDebug("application debug sentinel");
        factory.CreateLogger("Microsoft.AspNetCore.Probe").LogInformation("framework information sentinel");
        factory.CreateLogger("Microsoft.AspNetCore.Probe").LogWarning("framework warning sentinel");
        factory.CreateLogger("System.Net.Http.HttpClient.Probe").LogInformation("http information sentinel");
        var text = Read(host);
        Assert.Contains("application debug sentinel", text); Assert.Contains("framework warning sentinel", text);
        Assert.DoesNotContain("framework information sentinel", text); Assert.DoesNotContain("http information sentinel", text);
    }

    [Fact]
    public async Task ExternalFileControlsLevelsAndRotationAndCannotBeOverriddenByHostSettings()
    {
        await using var host = new PortalFactory(new()
        {
            ["Logging:LogLevel:SsrsPortal"] = "Warning", ["Logging:File:MaxFileSizeBytes"] = "4096", ["Logging:File:RetentionDays"] = "7"
        }, hostSettings: new() { ["Logging:LogLevel:SsrsPortal"] = "Trace", ["Logging:File:RetentionDays"] = "1" });
        using var browser = host.Browser();
        var logger = Logger(host); logger.LogDebug("filtered debug sentinel"); logger.LogWarning("retained warning sentinel");
        var text = Read(host);
        Assert.DoesNotContain("filtered debug sentinel", text); Assert.Contains("retained warning sentinel", text);
        var target = Logging(host).Factory.Configuration!.FindTargetByName<FileTarget>("application-file");
        Assert.NotNull(target);
        Assert.Equal(4096, target.ArchiveAboveSize); Assert.Equal(7, target.MaxArchiveDays);
    }

    [Fact]
    public void ProductionDirectoryUsesCommonApplicationDataWithConfigAndLogsAsSiblings()
    {
        var paths = ClcApplicationPaths.Resolve(Program.ApplicationDirectoryName);
        var expected = Path.Combine(paths.ApplicationDataDirectory, "Logs");
        Assert.Equal(expected, ApplicationLogging.ConventionalDirectory);
        Assert.Equal(paths.ConfigurationFilePath, ExternalConfigurationLoader.ConventionalPath);
        Assert.Equal(paths.ApplicationDataDirectory, Path.GetDirectoryName(Path.GetDirectoryName(ExternalConfigurationLoader.ConventionalPath)));
    }

    [Fact]
    public async Task FilesAreCreatedInFixtureDirectoryAndSurviveHostShutdownWithLifecycleEvents()
    {
        using var configuration = new TestConfigurationFile();
        var directory = Path.Combine(Path.GetDirectoryName(configuration.Path)!, "Logs");
        Assert.False(Directory.Exists(directory));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = ["--SsrsPortal:TestConfigurationFile", configuration.Path] });
        using var logging = new ApplicationLogging(ApplicationLogging.ResolveDirectory(builder));
        ExternalConfigurationLoader.AddTo(builder); logging.Configure(builder.Configuration); logging.Register(builder);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        await using (var app = builder.Build())
        {
            logging.ObserveLifetime(app);
            await app.StartAsync();
            app.Logger.LogWarning("survives shutdown sentinel");
            await app.StopAsync();
        }
        logging.Dispose();
        var text = ReadDirectory(directory);
        Assert.Contains("Application bootstrap started", text); Assert.Contains("Application started", text);
        Assert.Contains("Application stopping", text); Assert.Contains("Application stopped", text); Assert.Contains("survives shutdown sentinel", text);
        Assert.All(Directory.GetFiles(directory), path => Assert.Matches(@"ssrs-web-\d{4}-\d{2}-\d{2}(\.\d+)?\.log$", Path.GetFileName(path)));
        // Closed handles permit deletion, as required by fixture cleanup and recycling.
        foreach (var path in Directory.GetFiles(directory)) { using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Theory]
    [InlineData("Logging:File:Rotation", "Hourly")]
    [InlineData("Logging:File:RetentionDays", "-1")]
    [InlineData("Logging:File:RetentionDays", "0")]
    [InlineData("Logging:File:RetentionDays", "3651")]
    [InlineData("Logging:File:MaxFileSizeBytes", "0")]
    [InlineData("Logging:File:MaxFileSizeBytes", "-100")]
    [InlineData("Logging:File:MaxFileSizeBytes", "1073741825")]
    [InlineData("Logging:File:MaxFileSizeBytes", "SECRET_SIZE_SENTINEL")]
    [InlineData("Logging:File:Enabled", "SECRET_BOOLEAN_SENTINEL")]
    [InlineData("Logging:File:Path", "SECRET_PATH_SENTINEL")]
    [InlineData("Logging:LogLevel:SsrsPortal", "SECRET_LEVEL_SENTINEL")]
    [InlineData("Logging:LogLevel:SsrsPortal", "1")]
    public async Task InvalidOptionsFailStartupSafelyAndAreCapturedByBootstrap(string key, string value)
    {
        await using var host = new PortalFactory(new() { [key] = value });
        var exception = Assert.Throws<InvalidOperationException>(() => host.Browser());
        Assert.Null(exception.InnerException);
        var text = ReadDirectory(DirectoryFor(host));
        Assert.Contains("Application startup or hosting failed", text); Assert.Contains("Logging", text);
        Assert.DoesNotContain("SECRET_", text + exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapRecordsMissingOrMalformedConfigurationWithoutEchoingSecrets(bool malformed)
    {
        await using var host = new PortalFactory();
        if (malformed) { File.WriteAllText(host.ConfigurationFile.Path, "{\"ClientSecret\":\"SECRET_JSON_SENTINEL\",SECRET_PARSE_SENTINEL}"); }
        else { File.Delete(host.ConfigurationFile.Path); }
        Assert.Throws<InvalidOperationException>(() => host.Browser());
        var text = ReadDirectory(DirectoryFor(host));
        Assert.Contains("Application bootstrap started", text); Assert.Contains("Application startup or hosting failed", text);
        Assert.Contains(malformed ? "invalid JSON" : "configuration file is missing", text);
        Assert.DoesNotContain("SECRET_", text);
        Assert.Single(Regex.Matches(text, "Message=Application startup or hosting failed"));
    }

    [Fact]
    public void UnwritableLogDestinationFailsExplicitlyAndHasSafeProcessFallback()
    {
        using var fixture = new TestConfigurationFile();
        var exception = Assert.Throws<InvalidOperationException>(() => new ApplicationLogging(fixture.Path));
        using var output = new StringWriter(); ApplicationLogging.Fallback(exception, output);
        Assert.Contains("Logs", exception.Message); Assert.Contains("permissions", output.ToString());
        Assert.DoesNotContain("ClientSecret", output.ToString());
    }

    [Fact]
    public async Task UnreadableConfigurationIsLoggedBeforeNormalInitialization()
    {
        await using var host = new PortalFactory(new() { ["Entra:ClientSecret"] = "UNREADABLE_SECRET_SENTINEL" });
        using (var locked = File.Open(host.ConfigurationFile.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<InvalidOperationException>(() => host.Browser());
            var text = ReadDirectory(DirectoryFor(host));
            Assert.Contains("configuration file cannot be read", text); Assert.DoesNotContain("UNREADABLE_SECRET_SENTINEL", text);
        }
    }

    [Fact]
    public async Task HostingStartupFailureRetainsExceptionOnceAndFiltersCredentials()
    {
        await using var host = new PortalFactory(configureServices: services => services.AddHostedService<FailingStartupService>());
        var exception = Assert.Throws<InvalidOperationException>(() => host.Browser());
        var text = ReadDirectory(DirectoryFor(host));
        Assert.Contains("Application startup or hosting failed", text); Assert.Contains(nameof(FailingStartupService), text);
        Assert.Single(Regex.Matches(text, "InvalidOperationException: dependency initialization sentinel"));
        Assert.DoesNotContain("DEPENDENCY_PASSWORD_SENTINEL", text + exception);
    }

    [Fact]
    public void RuntimeWriteFailureUsesSafeFallbackAndRecoversWithoutRestart()
    {
        using var fixture = new TestConfigurationFile(); using var output = new StringWriter();
        var directory = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "Logs");
        using var logging = new ApplicationLogging(directory, processOutput: output);
        var target = logging.Factory.Configuration!.FindTargetByName<FileTarget>("application-file")!;
        var blocked = Path.Combine(directory, "blocked"); Directory.CreateDirectory(blocked);
        target.FileName = Layout.FromLiteral(blocked);
        logging.Factory.GetLogger("SsrsPortal.Fallback").Error(new Exception("Useful failure Password=RUNTIME_SECRET_SENTINEL"), "Runtime fallback sentinel");
        logging.Flush();
        Assert.Contains("persistent file logging write failed", output.ToString()); Assert.Contains("Useful failure", output.ToString());
        Assert.DoesNotContain("RUNTIME_SECRET_SENTINEL", output.ToString());
        target.FileName = Path.Combine(directory, "ssrs-web-${shortdate}.log");
        logging.Factory.GetLogger("SsrsPortal.Fallback").Warn("FILE_RECOVERED_SENTINEL"); logging.Flush();
        Assert.Contains("FILE_RECOVERED_SENTINEL", ReadDirectory(directory));
    }

    [Fact]
    public void RequestCookiesAuthorizationAntiforgeryAndOidcCodesAreFilteredEvenWithoutLabels()
    {
        using var fixture = new TestConfigurationFile();
        var context = new DefaultHttpContext { TraceIdentifier = "HTTP_TRACE_SENTINEL" };
        context.Request.Path = "/signin-oidc";
        context.Request.Headers.Cookie = "__Host-Portal=COOKIE_SECRET_SENTINEL";
        context.Request.Headers.Authorization = "Bearer HEADER_SECRET_SENTINEL";
        context.Request.Headers["RequestVerificationToken"] = "CSRF_SECRET_SENTINEL";
        context.Request.QueryString = new QueryString("?code=OIDC_CODE_SENTINEL&state=OIDC_STATE_SENTINEL");
        var accessor = new HttpContextAccessor { HttpContext = context };
        var directory = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "Logs");
        try
        {
            using var logging = new ApplicationLogging(directory, accessor);
            logging.Factory.GetLogger("SsrsPortal.RequestProbe").Error(new Exception("Useful context COOKIE_SECRET_SENTINEL HEADER_SECRET_SENTINEL CSRF_SECRET_SENTINEL OIDC_CODE_SENTINEL OIDC_STATE_SENTINEL"), "Request diagnostic");
            logging.Flush();
            var text = ReadDirectory(directory);
            foreach (var secret in new[] { "COOKIE_SECRET_SENTINEL", "HEADER_SECRET_SENTINEL", "CSRF_SECRET_SENTINEL", "OIDC_CODE_SENTINEL", "OIDC_STATE_SENTINEL" }) { Assert.DoesNotContain(secret, text); }
            Assert.Contains("Useful context", text); Assert.Contains("HTTP_TRACE_SENTINEL", text);
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public async Task DisablingFileOutputIsExplicitAndDoesNotDisableBootstrapFailureCapture()
    {
        await using var host = new PortalFactory(new() { ["Logging:File:Enabled"] = "false" }); using var browser = host.Browser();
        Logger(host).LogWarning("disabled file output sentinel");
        var text = Read(host);
        Assert.Contains("Application bootstrap started", text); Assert.DoesNotContain("disabled file output sentinel", text);
    }

    [Theory]
    [InlineData("ClientSecret")]
    [InlineData("Password")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("id_token")]
    [InlineData("Cookie")]
    [InlineData("AuthenticationCookie")]
    [InlineData("ClientAssertion")]
    [InlineData("CodeVerifier")]
    [InlineData("Authorization")]
    [InlineData("RequestVerificationToken")]
    [InlineData("__RequestVerificationToken")]
    [InlineData("AntiforgeryToken")]
    public async Task AuthenticationSecretsAreRemovedFromPropertiesTemplatesScopesAndRawExceptions(string key)
    {
        const string secret = "AUTHENTICATION_SECRET_SENTINEL";
        await using var host = new PortalFactory(); using var browser = host.Browser();
        var logger = Logger(host);
        using (logger.BeginScope(new Dictionary<string, object?> { [key] = secret, ["ReportPath"] = "/Training/Useful Report" }))
        {
            var exception = new InvalidOperationException("Useful SQL and fault context; " + key + "=" + secret, new Exception("Bearer BEARER_SECRET_SENTINEL"));
            logger.LogError(exception, "Useful operation {" + key + "}; report {ReportPath}.", secret, "/Training/Useful Report");
        }
        var text = Read(host);
        Assert.DoesNotContain(secret, text); Assert.DoesNotContain("BEARER_SECRET_SENTINEL", text);
        Assert.Contains("Useful SQL and fault context", text); Assert.Contains("/Training/Useful Report", text); Assert.Contains("[redacted]", text);
    }

    [Fact]
    public async Task ConfiguredSecretAndNestedAuthenticationPropertiesCannotBypassFiltering()
    {
        await using var host = new PortalFactory(new() { ["Entra:ClientSecret"] = "CONFIGURED_SECRET_SENTINEL" }); using var browser = host.Browser();
        Logger(host).LogError(new Exception("Useful root CONFIGURED_SECRET_SENTINEL"), "Useful nested context {Context}",
            new Dictionary<string, object?> { ["Operation"] = "Render", ["Credentials"] = new Dictionary<string, object?> { ["Password"] = "NESTED_SECRET_SENTINEL" } });
        var text = Read(host);
        Assert.DoesNotContain("CONFIGURED_SECRET_SENTINEL", text); Assert.DoesNotContain("NESTED_SECRET_SENTINEL", text); Assert.Contains("Render", text);
    }

    [Fact]
    public async Task ProtocolTraceSettingsCannotEnableRawAuthenticationProtocolDiagnostics()
    {
        await using var host = new PortalFactory(new() { ["Logging:LogLevel:Microsoft.AspNetCore.Authentication"] = "Trace" }); using var browser = host.Browser();
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler").LogDebug("RAW_PROTOCOL_SENTINEL");
        Assert.DoesNotContain("RAW_PROTOCOL_SENTINEL", Read(host));
    }

    [Fact]
    public async Task ProtocolSecurityFloorStillHonorsStricterExternalLevels()
    {
        await using var host = new PortalFactory(new() { ["Logging:LogLevel:Microsoft.AspNetCore.Authentication"] = "Error" }); using var browser = host.Browser();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Authentication.Probe");
        logger.LogWarning("FILTERED_PROTOCOL_WARNING_SENTINEL"); logger.LogError("RETAINED_PROTOCOL_ERROR_SENTINEL");
        var text = Read(host);
        Assert.DoesNotContain("FILTERED_PROTOCOL_WARNING_SENTINEL", text); Assert.Contains("RETAINED_PROTOCOL_ERROR_SENTINEL", text);
    }

    [Fact]
    public async Task ApplicationSignInAndTenantRoleRejectionsAreUsefulWithoutAuthenticationSecrets()
    {
        await using var host = new PortalFactory(); using var accepted = await host.Login(); using var rejected = await host.Login("unknown-tenant");
        var text = Read(host);
        Assert.Contains("Application sign-in completed", text); Assert.Contains("Application sign-in rejected", text);
        Assert.Contains("00000000-0000-0000-0000-000000000099", text); Assert.Contains("TenantApproved", text); Assert.Contains("HasRole", text);
    }

    [Fact]
    public async Task DebugLogsContainSoapFaultsReportParametersAndRequestReferenceWithoutBrowserDetails()
    {
        await using var host = new PortalFactory(); using var user = await host.Login();
        var id = await user.Id();
        var input = Harness.Inputs(); input.Values!["note"] = ["REPORT_PARAMETER_SENTINEL"];
        using (var response = await user.Post("/api/reports/" + id + "/run", input)) { response.EnsureSuccessStatusCode(); }
        host.Mock.Failure = MockFailure.Fault; host.Mock.FailOperation = "GetItemParameters";
        using var failed = await user.Post("/api/reports/" + id + "/parameters", input);
        var json = await failed.Content.ReadAsStringAsync();
        var text = Read(host);
        Assert.Contains("REPORT_PARAMETER_SENTINEL", text); Assert.Contains("ReportPath", text); Assert.Contains("GetItemParameters", text);
        Assert.Contains("faultstring", text); Assert.Contains("private SQL", text); Assert.Contains("ReportService2010.asmx", text);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Contains(document.RootElement.GetProperty("trace").GetString()!, text);
        Assert.DoesNotContain("private SQL", json); Assert.DoesNotContain("Demonstration row", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractionPrivacySuppressesFaultsAndExceptionChainsInPersistentLogs(bool explicitDiagnostics)
    {
        await using var host = new PortalFactory(new() { ["Portal:Diagnostics:Enabled"] = explicitDiagnostics.ToString() });
        using var browser = host.Browser();
        var failure = FailureDiagnostics.Attach(new InvalidOperationException("PRIVATE_EXECUTION_SENTINEL",
            new Exception("PRIVATE_REPORT_VALUE_SENTINEL")),
            new SoapDiagnostic("ToggleItem", "https://synthetic.invalid/ReportServer/ReportExecution2005.asmx", "interaction-soap-reference")
            {
                SuppressContent = true, HttpStatus = 500, Failure = "soap-fault",
                FaultMessage = "PRIVATE_TOGGLE_SENTINEL", FaultXml = "<fault>PRIVATE_REPORT_VALUE_SENTINEL</fault>"
            });
        FailureDiagnostics.Log(Logger(host), host.Services.GetRequiredService<PortalOptions>(), failure, "service", "interaction-request-reference");
        var text = Read(host);
        Assert.Contains("ToggleItem", text); Assert.Contains("interaction-soap-reference", text);
        Assert.Contains("interaction-request-reference", text); Assert.Contains("HTTP Status: 500", text);
        Assert.Contains("Exception content omitted for report interaction privacy.", text);
        foreach (var value in new[] { "PRIVATE_EXECUTION_SENTINEL", "PRIVATE_REPORT_VALUE_SENTINEL", "PRIVATE_TOGGLE_SENTINEL", "<fault>" })
        {
            Assert.DoesNotContain(value, text);
        }
    }

    [Fact]
    public async Task VeryLargeDiagnosticsAndArraysAreBoundedWithExplicitTruncation()
    {
        await using var host = new PortalFactory(); using var browser = host.Browser();
        Logger(host).LogError(new Exception(new string('x', 300000) + "END_EXCEPTION_SENTINEL"), "Large {Payload}; values {Values}", new string('y', 300000), Enumerable.Range(0, 10000).ToArray());
        var text = Read(host);
        Assert.Contains("[truncated]", text); Assert.DoesNotContain("END_EXCEPTION_SENTINEL", text);
        Assert.True(text.Length < SafeLogLayout.MaximumEventCharacters + 16000);
    }

    [Fact]
    public void AggregateExceptionsCannotExpandEveryChildBeforeApplyingDiagnosticLimits()
    {
        using var fixture = new TestConfigurationFile();
        var directory = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "Logs");
        using var logging = new ApplicationLogging(directory);
        var children = Enumerable.Range(0, 1000).Select(_ => new CountingMessageException()).ToArray();
        logging.Factory.GetLogger("SsrsPortal.AggregateProbe").Error(new AggregateException("Bounded aggregate diagnostic", children), "Aggregate failure sentinel");
        logging.Flush();
        var text = ReadDirectory(directory);
        Assert.Contains("Aggregate failure sentinel", text); Assert.Contains("AggregateException", text); Assert.Contains("[truncated]", text);
        Assert.True(children.Sum(child => child.MessageReads) <= 16, "Formatting must not evaluate messages for children beyond the 16-child limit.");
    }

    [Fact]
    public void DeepAggregateMessagesCannotBypassTheExceptionDepthLimit()
    {
        var leaf = new CountingMessageException();
        Exception graph = leaf;
        for (var depth = 0; depth < 1000; depth++) { graph = new AggregateException("Nested aggregate", graph); }
        var text = FailureDiagnostics.ExceptionText(graph, 65536);
        Assert.Contains("AggregateException", text); Assert.Contains("[truncated]", text);
        Assert.Equal(0, leaf.MessageReads); Assert.True(text.Length <= 65536);
        var ordinary = FailureDiagnostics.ExceptionText(new AggregateException("Useful ordinary aggregate context", new InvalidOperationException("Useful ordinary cause")), 65536);
        Assert.Contains("Useful ordinary aggregate context", ordinary); Assert.Contains("Useful ordinary cause", ordinary);
    }

    [Fact]
    public async Task ConcurrentHostsStayIsolatedAndDisposingOneDoesNotStopTheOther()
    {
        var first = new PortalFactory(); var second = new PortalFactory();
        var firstDirectory = DirectoryFor(first); var secondDirectory = DirectoryFor(second);
        try
        {
            await Task.WhenAll(Task.Run(() => { using var browser = first.Browser(); Logger(first).LogWarning("FIRST_HOST_SENTINEL"); }),
                Task.Run(() => { using var browser = second.Browser(); Logger(second).LogWarning("SECOND_HOST_SENTINEL"); }));
            var firstText = Read(first); var secondText = Read(second);
            Assert.Contains("FIRST_HOST_SENTINEL", firstText); Assert.DoesNotContain("SECOND_HOST_SENTINEL", firstText);
            Assert.Contains("SECOND_HOST_SENTINEL", secondText); Assert.DoesNotContain("FIRST_HOST_SENTINEL", secondText);
            await first.DisposeAsync();
            Logger(second).LogWarning("SECOND_HOST_AFTER_DISPOSE_SENTINEL");
            Assert.Contains("SECOND_HOST_AFTER_DISPOSE_SENTINEL", Read(second));
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
        Assert.False(Directory.Exists(firstDirectory)); Assert.False(Directory.Exists(secondDirectory));
    }

    [Fact]
    public void DailyAndSizeRotationAppendAfterRecycleAndRetainMultipleSameDayFiles()
    {
        using var fixture = new TestConfigurationFile(); var directory = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "Logs");
        var options = new FileLoggingOptions { MaxFileSizeBytes = 1024, RetentionDays = 30 };
        var yesterday = DateTime.Now.AddDays(-1);
        using (var factory = FileFactory(directory, options))
        {
            var logger = factory.GetLogger("SsrsPortal.Rotation");
            logger.Log(new LogEventInfo(NLog.LogLevel.Info, logger.Name, "YESTERDAY_SENTINEL") { TimeStamp = yesterday });
            for (var i = 0; i < 12; i++) { logger.Info("ROLLOVER_{0:D2} {1}", i, new string('x', 600)); }
        }
        var initial = ReadDirectory(directory);
        Assert.Contains("YESTERDAY_SENTINEL", initial);
        var files = Directory.GetFiles(directory, "*.log"); Assert.True(files.Length >= 4);
        Assert.Contains(files, file => Path.GetFileName(file).Contains(yesterday.ToString("yyyy-MM-dd"), StringComparison.Ordinal));
        Assert.Contains(files, file => Path.GetFileName(file).Contains(DateTime.Now.ToString("yyyy-MM-dd"), StringComparison.Ordinal));
        using (var factory = FileFactory(directory, options)) { factory.GetLogger("SsrsPortal.Rotation").Info("AFTER_RECYCLE_SENTINEL"); }
        var final = ReadDirectory(directory);
        for (var i = 0; i < 12; i++) { Assert.Single(Regex.Matches(final, "ROLLOVER_" + i.ToString("D2"))); }
        Assert.Contains("AFTER_RECYCLE_SENTINEL", final);
        Assert.All(Directory.GetFiles(directory, "*.log"), path => Assert.True(new FileInfo(path).Length < 2048));
    }

    [Fact]
    public void RetentionRemovesExpiredArchivesAndPreservesEveryRecentSameDayArchive()
    {
        using var fixture = new TestConfigurationFile(); var directory = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "Logs"); Directory.CreateDirectory(directory);
        var old = new[] { "ssrs-web-2000-01-01.000.log", "ssrs-web-2000-01-01.001.log" };
        foreach (var name in old)
        {
            var path = Path.Combine(directory, name); File.WriteAllText(path, "OLD_SENTINEL"); File.SetLastWriteTime(path, DateTime.Now.AddDays(-40)); File.SetCreationTime(path, DateTime.Now.AddDays(-40));
        }
        var recent = Path.Combine(directory, "ssrs-web-" + DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd") + ".009.log"); File.WriteAllText(recent, "RECENT_SENTINEL");
        using (var factory = FileFactory(directory, new FileLoggingOptions())) { factory.GetLogger("SsrsPortal.Retention").Info("CURRENT_SENTINEL"); }
        Assert.All(old, name => Assert.False(File.Exists(Path.Combine(directory, name))));
        Assert.True(File.Exists(recent)); Assert.Contains("CURRENT_SENTINEL", ReadDirectory(directory));
    }

    [Fact]
    public void BootstrapCannotDeleteFilesBeforeReadingAnOperatorsLongerRetention()
    {
        using var fixture = new TestConfigurationFile(); var directory = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "Logs"); Directory.CreateDirectory(directory);
        var older = Path.Combine(directory, "ssrs-web-" + DateTime.Now.AddDays(-45).ToString("yyyy-MM-dd") + ".009.log");
        File.WriteAllText(older, "RETAIN_LONGER_SENTINEL"); File.SetLastWriteTime(older, DateTime.Now.AddDays(-45)); File.SetCreationTime(older, DateTime.Now.AddDays(-45));
        using var logging = new ApplicationLogging(directory);
        Assert.Equal(0, logging.Factory.Configuration!.FindTargetByName<FileTarget>("application-file")!.MaxArchiveDays);
        Assert.True(File.Exists(older));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:RetentionDays"] = "90" }).Build();
        logging.Configure(configuration); logging.Flush();
        Assert.True(File.Exists(older));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task MalformedFileOptionsObjectsFailWithoutFallbackToDefaults(string json)
    {
        await using var host = new PortalFactory();
        host.ConfigurationFile.Document["Logging"] = System.Text.Json.Nodes.JsonNode.Parse("{\"File\":" + json + "}"); host.ConfigurationFile.Save();
        var exception = Assert.Throws<InvalidOperationException>(() => host.Browser());
        Assert.Contains("Logging:File", exception.Message);
    }

    [Fact]
    public async Task CredentialCollectionsAndSecretsAtTruncationBoundariesRemainFiltered()
    {
        const string secret = "TRUNCATION_SECRET_SENTINEL";
        await using var host = new PortalFactory(new() { ["Entra:ClientSecret"] = secret }); using var browser = host.Browser();
        var logger = Logger(host);
        using (logger.BeginScope(new Dictionary<string, object?> { ["Cookie"] = new[] { "COLLECTION_COOKIE_SENTINEL" } }))
        {
            logger.LogError(new Exception(new string('x', 65520) + secret), "Cookie context {Cookie}; useful {Context}",
                new Microsoft.Extensions.Primitives.StringValues("COLLECTION_COOKIE_SENTINEL"), new string('y', 8180) + secret);
        }
        var text = Read(host);
        Assert.DoesNotContain("COLLECTION_COOKIE_SENTINEL", text); Assert.DoesNotContain("TRUNCATION_", text);
        Assert.Contains("[truncated]", text);
    }

    private static LogFactory FileFactory(string directory, FileLoggingOptions options)
    {
        var factory = new LogFactory { AutoShutdown = false, ThrowExceptions = true };
        var configuration = new NLog.Config.LoggingConfiguration(factory);
        configuration.AddRuleForAllLevels(ApplicationLogging.CreateFileTarget(directory, options, Layout.FromString("${message}")));
        factory.Configuration = configuration;
        return factory;
    }

    private static Exception ThrownException()
    {
        try { ThrowRoot(); return new Exception(); }
        catch (Exception inner)
        {
            try { throw new InvalidOperationException("outer exception sentinel", inner); }
            catch (Exception outer) { return outer; }
        }
    }
    private static void ThrowRoot() => throw new ArgumentException("inner exception sentinel");
    private sealed class FailingStartupService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("dependency initialization sentinel Password=DEPENDENCY_PASSWORD_SENTINEL");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class CountingMessageException : Exception
    {
        public int MessageReads { get; private set; }
        public override string Message { get { MessageReads++; return "Bounded child diagnostic"; } }
    }
    private static Microsoft.Extensions.Logging.ILogger Logger(PortalFactory host) => host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SsrsPortal.LoggingProbe");
    private static ApplicationLogging Logging(PortalFactory host) => host.Services.GetRequiredService<ApplicationLogging>();
    private static string DirectoryFor(PortalFactory host) => Path.Combine(Path.GetDirectoryName(host.ConfigurationFile.Path)!, "Logs");
    private static string Read(PortalFactory host) { Logging(host).Flush(); return ReadDirectory(DirectoryFor(host)); }
    private static string ReadDirectory(string directory) => string.Join('\n', Directory.GetFiles(directory, "*.log").Order(StringComparer.Ordinal).Select(path =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }));
}
