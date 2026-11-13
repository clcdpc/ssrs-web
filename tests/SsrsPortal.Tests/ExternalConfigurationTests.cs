using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Clc.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SsrsPortal.Configuration;
using SsrsPortal.Core;
using SsrsPortal.Logging;
using SsrsPortal.Security;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.Tests;

[CollectionDefinition(EnvironmentSensitiveCollection.Name, DisableParallelization = true)]
public sealed class EnvironmentSensitiveCollection
{
    public const string Name = "Environment-sensitive configuration tests";
}

[Collection(EnvironmentSensitiveCollection.Name)]
public sealed class ExternalConfigurationTests
{
    [Fact]
    public void NormalPathUsesCommonApplicationDataAndHasNoEnvironmentSpecificSuffix()
    {
        var packagePaths = ClcApplicationPaths.Resolve(Program.ApplicationDirectoryName);
        var verifiedPaths = ProgramDataPath.Paths;
        Assert.Equal(packagePaths.ConfigurationFilePath, verifiedPaths.ConfigurationFilePath);
        Assert.Equal(packagePaths.ConfigurationFilePath, ExternalConfigurationLoader.ConventionalPath);
        Assert.Equal(Path.GetFullPath(packagePaths.ConfigurationFilePath), Path.GetFullPath(ExternalConfigurationLoader.ConventionalPath));
        Assert.Equal(Path.Combine(packagePaths.ApplicationDataDirectory, "Logs"), ApplicationLogging.ConventionalDirectory);
        var packageRoot = Path.GetDirectoryName(packagePaths.ApplicationDataDirectory)!;
        var nativeRoot = ProgramDataPath.ResolveLegacyRoot();
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.True(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(nativeRoot)), pathComparison),
            "ClcApplicationPaths CommonApplicationData must resolve to the same root as the legacy native ProgramData check.");
        Assert.True(Path.IsPathFullyQualified(ExternalConfigurationLoader.ConventionalPath));
    }

    [Fact]
    public async Task ApplicationDataCommandRunsBeforeConfigurationAndHostStartup()
    {
        var paths = ClcApplicationPaths.Resolve(Program.ApplicationDirectoryName);
        var missingDirectory = Path.Combine(Path.GetTempPath(), "ssrs-config-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(missingDirectory);
        var missingConfiguration = Path.Combine(missingDirectory, "application.json");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["SsrsPortal__TestConfigurationFile"] = missingConfiguration;
        start.Environment.Remove("ProgramData");

        try
        {
            var valid = await RunCommand(start, "--clc-appdata-directory");
            Assert.Equal(0, valid.ExitCode);
            Assert.Equal(paths.ApplicationDataDirectory + Environment.NewLine, valid.StandardOutput);
            Assert.Empty(valid.StandardError);
            Assert.False(File.Exists(missingConfiguration));

            var spoofedProgramData = Path.Combine(missingDirectory, "spoofed-programdata");
            start.Environment["ProgramData"] = spoofedProgramData;
            var spoofed = await RunCommand(start, "--clc-appdata-directory");
            Assert.Equal(0, spoofed.ExitCode);
            Assert.Equal(paths.ApplicationDataDirectory + Environment.NewLine, spoofed.StandardOutput);
            Assert.Empty(spoofed.StandardError);
            Assert.False(Directory.Exists(spoofedProgramData));

            if (OperatingSystem.IsWindows())
            {
                start.Environment.TryGetValue("SystemDrive", out var originalSystemDrive);
                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var forgedSystemDrive = string.Equals(systemDrive, "Z:\\", StringComparison.OrdinalIgnoreCase) ? "Y:" : "Z:";
                start.Environment["SystemDrive"] = forgedSystemDrive;
                var rejected = await RunCommand(start, "--clc-appdata-directory");
                Assert.Equal(1, rejected.ExitCode);
                Assert.Empty(rejected.StandardOutput);
                Assert.Contains("Unable to resolve the application's CommonApplicationData directory.", rejected.StandardError);
                Assert.False(File.Exists(missingConfiguration));
                if (originalSystemDrive is null) { start.Environment.Remove("SystemDrive"); }
                else { start.Environment["SystemDrive"] = originalSystemDrive; }
            }

            var malformed = await RunCommand(start, "--clc-appdata-directory=SECRET_COMMAND_SENTINEL");
            Assert.Equal(2, malformed.ExitCode);
            Assert.Empty(malformed.StandardOutput);
            Assert.Contains("Use --clc-appdata-directory alone", malformed.StandardError);
            Assert.DoesNotContain("SECRET_COMMAND_SENTINEL", malformed.StandardError);
            Assert.False(File.Exists(missingConfiguration));
        }
        finally
        {
            Directory.Delete(missingDirectory, recursive: true);
        }
    }

    [Fact]
    public void UnrelatedArgumentsRemainUnhandledWhenNativePathResolutionWouldFail()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var originalSystemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var forgedSystemDrive = string.Equals(systemDrive, "Z:\\", StringComparison.OrdinalIgnoreCase) ? "Y:" : "Z:";
        try
        {
            Environment.SetEnvironmentVariable("SystemDrive", forgedSystemDrive);
            Assert.False(ApplicationPathCommandLine.TryHandle(["--urls", "http://127.0.0.1:7443"], out var exitCode));
            Assert.Equal(0, exitCode);
        }
        finally { Environment.SetEnvironmentVariable("SystemDrive", originalSystemDrive); }
    }

    private static async Task<ProcessResult> RunCommand(ProcessStartInfo template, string argument)
    {
        var start = new ProcessStartInfo(template.FileName)
        {
            WorkingDirectory = template.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (var value in template.ArgumentList) { start.ArgumentList.Add(value); }
        start.ArgumentList.Add(argument);
        foreach (var variable in template.Environment) { start.Environment[variable.Key] = variable.Value; }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The configuration command process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            throw;
        }
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    [Fact]
    public async Task ValidExternalFileBindsExistingOptionsAndDefaults()
    {
        await using var factory = new PortalFactory(new() { ["Portal:CatalogSeconds"] = "321" });
        // Omit optional settings to exercise code defaults rather than fixture values.
        factory.ConfigurationFile.Document["Portal"]!.AsObject().Remove("RequestSeconds");
        factory.ConfigurationFile.Document.Remove("CatalogMonitor");
        factory.ConfigurationFile.Save();
        using var browser = factory.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/TestLogin")).StatusCode);
        var portal = factory.Services.GetRequiredService<PortalOptions>();
        Assert.True(portal.UseMock); Assert.True(portal.TestAuth);
        Assert.Equal("https://synthetic.invalid/ReportServer", portal.ReportServer);
        Assert.Equal(["/Training"], portal.AllowedRoots);
        Assert.Equal(321, portal.CatalogSeconds); Assert.Equal(30, portal.RequestSeconds);
        Assert.Equal(AuthenticationTests.Client, factory.Services.GetRequiredService<EntraOptions>().ClientId);
        var monitor = factory.Services.GetRequiredService<CatalogMonitorOptions>();
        Assert.False(monitor.Enabled); Assert.Equal(30, monitor.IntervalSeconds); Assert.Equal(5, monitor.CommandTimeoutSeconds);
    }

    [Fact]
    public async Task DisabledMonitorAndSubscriptionsKeepNullOptionalStringSemantics()
    {
        await using var factory = new PortalFactory();
        factory.ConfigurationFile.Document["CatalogMonitor"] = new JsonObject
        { ["Enabled"] = false, ["ConnectionString"] = null, ["IntervalSeconds"] = 30, ["CommandTimeoutSeconds"] = 5 };
        factory.ConfigurationFile.Document["Subscriptions"] = new JsonObject { ["Enabled"] = false };
        factory.ConfigurationFile.Document["Subscriptions"]!["ConnectionString"] = null;
        factory.ConfigurationFile.Document["Subscriptions"]!["PostmarkServerToken"] = null;
        factory.ConfigurationFile.Document["Subscriptions"]!["PortalBaseUrl"] = null;
        factory.ConfigurationFile.Document["Subscriptions"]!["From"] = null;
        factory.ConfigurationFile.Document["Subscriptions"]!["MessageStream"] = null;
        factory.ConfigurationFile.Document["Entra"]!["ClientSecret"] = null;
        factory.ConfigurationFile.Save();

        using var browser = factory.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/TestLogin")).StatusCode);
        Assert.False(factory.Services.GetRequiredService<CatalogMonitorOptions>().Enabled);
        Assert.Null(factory.Services.GetRequiredService<CatalogMonitorOptions>().ConnectionString);
        var subscriptions = factory.Services.GetRequiredService<SubscriptionOptions>();
        Assert.False(subscriptions.Enabled);
        Assert.Null(subscriptions.ConnectionString);
        Assert.Null(subscriptions.PostmarkServerToken);
        Assert.Null(subscriptions.PortalBaseUrl);
        Assert.Null(subscriptions.From);
        Assert.Null(subscriptions.MessageStream);
        subscriptions.Validate();
    }

    [Theory]
    [InlineData("CatalogMonitor", "{\"Enabled\":null}")]
    [InlineData("CatalogMonitor", "[]")]
    [InlineData("CatalogMonitor", "null")]
    [InlineData("Subscriptions", "{\"Enabled\":null,\"Workers\":null,\"PostmarkServerToken\":42}")]
    [InlineData("Subscriptions", "[]")]
    [InlineData("Subscriptions", "{}")]
    [InlineData("Subscriptions", "null")]
    public async Task OptionalSectionsKeepPriorJsonBinderAndOptionsValidationSemantics(string section, string json)
    {
        await using var factory = new PortalFactory();
        factory.ConfigurationFile.Document[section] = JsonNode.Parse(json);
        factory.ConfigurationFile.Save();

        using var priorConfiguration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(factory.ConfigurationFile.Path, optional: false, reloadOnChange: false).Build();
        var prior = ApplicationConfiguration.Read(priorConfiguration, development: true);

        using var browser = factory.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/TestLogin")).StatusCode);
        var monitor = factory.Services.GetRequiredService<CatalogMonitorOptions>();
        var subscriptions = factory.Services.GetRequiredService<SubscriptionOptions>();
        Assert.Equal(prior.Monitor.Enabled, monitor.Enabled);
        Assert.Equal(prior.Monitor.IntervalSeconds, monitor.IntervalSeconds);
        Assert.Equal(prior.Monitor.CommandTimeoutSeconds, monitor.CommandTimeoutSeconds);
        Assert.Equal(prior.Monitor.ConnectionString, monitor.ConnectionString);
        Assert.Equal(prior.Subscriptions.Enabled, subscriptions.Enabled);
        Assert.Equal(prior.Subscriptions.Workers, subscriptions.Workers);
        Assert.Equal(prior.Subscriptions.PostmarkServerToken, subscriptions.PostmarkServerToken);
    }

    [Theory]
    [InlineData("CatalogMonitor", "{\"Enabled\":true,\"ConnectionString\":\"invalid\"}")]
    [InlineData("Subscriptions", "{\"Enabled\":true,\"ConnectionString\":\"invalid\"}")]
    public async Task OptionalSubsystemsStillRejectValuesThroughTheirExistingValidators(string section, string json)
    {
        await using var factory = new PortalFactory();
        factory.ConfigurationFile.Document[section] = JsonNode.Parse(json);
        factory.ConfigurationFile.Save();
        Assert.Throws<InvalidOperationException>(() => factory.Browser());
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task SupportedBomEncodingsMatchPriorJsonBinderAndDoNotRewriteTheFile(string encodingName)
    {
        await using var factory = new PortalFactory();
        var text = File.ReadAllText(factory.ConfigurationFile.Path);
        var encoded = EncodeWithBom(text, encodingName);
        File.WriteAllBytes(factory.ConfigurationFile.Path, encoded);

        using var priorConfiguration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(factory.ConfigurationFile.Path, optional: false, reloadOnChange: false).Build();
        var prior = ApplicationConfiguration.Read(priorConfiguration, development: true);

        using var browser = factory.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/TestLogin")).StatusCode);
        Assert.Equal(prior.Portal.ReportServer, factory.Services.GetRequiredService<PortalOptions>().ReportServer);
        Assert.Equal(prior.Entra.ClientId, factory.Services.GetRequiredService<EntraOptions>().ClientId);
        Assert.Equal(prior.Monitor.IntervalSeconds, factory.Services.GetRequiredService<CatalogMonitorOptions>().IntervalSeconds);
        Assert.Equal(prior.Subscriptions.Workers, factory.Services.GetRequiredService<SubscriptionOptions>().Workers);
        Assert.Equal(encoded, File.ReadAllBytes(factory.ConfigurationFile.Path));
    }

    [Theory]
    [InlineData("utf8-truncated")]
    [InlineData("utf8-invalid")]
    [InlineData("utf16le-surrogate")]
    [InlineData("utf16be-surrogate")]
    [InlineData("utf32le-truncated")]
    [InlineData("utf32be-invalid-scalar")]
    public async Task MalformedBomEncodingsFailRedactedAndLeaveSourceBytesUnchanged(string failureKind)
    {
        await using var factory = new PortalFactory();
        var bytes = MalformedEncoding(failureKind);
        File.WriteAllBytes(factory.ConfigurationFile.Path, bytes);
        var before = File.ReadAllBytes(factory.ConfigurationFile.Path);

        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("invalid JSON", error.Message);
        Assert.DoesNotContain("SECRET_ENCODING_SENTINEL", error.ToString() + string.Join('\n', factory.Logs.Messages));
        Assert.Equal(before, File.ReadAllBytes(factory.ConfigurationFile.Path));
    }

    private static byte[] EncodeWithBom(string text, string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            "utf32be" => new UTF32Encoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    }

    private static byte[] MalformedEncoding(string failureKind)
    {
        const string json = "{\"AllowedHosts\":\"SECRET_ENCODING_SENTINEL\"}";
        return failureKind switch
        {
            "utf8-truncated" => [0xef, 0xbb],
            "utf8-invalid" => Encoding.UTF8.GetBytes(json).Append((byte)0xff).ToArray(),
            "utf16le-surrogate" => new UnicodeEncoding(false, true, true).GetPreamble()
                .Concat(new UnicodeEncoding(false, false, true).GetBytes(json)).Concat(new byte[] { 0x00, 0xd8 }).ToArray(),
            "utf16be-surrogate" => new UnicodeEncoding(true, true, true).GetPreamble()
                .Concat(new UnicodeEncoding(true, false, true).GetBytes(json)).Concat(new byte[] { 0xd8, 0x00 }).ToArray(),
            "utf32le-truncated" => new UTF32Encoding(false, true, true).GetPreamble()
                .Concat(new UTF32Encoding(false, false, true).GetBytes(json)).Append((byte)0x01).ToArray(),
            "utf32be-invalid-scalar" => new UTF32Encoding(true, true, true).GetPreamble()
                .Concat(new UTF32Encoding(true, false, true).GetBytes(json)).Concat(new byte[] { 0x00, 0x11, 0x00, 0x00 }).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
    }

    [Fact]
    public async Task MissingFileStopsStartupAndDoesNotCreateAReplacement()
    {
        await using var factory = new PortalFactory();
        File.Delete(factory.ConfigurationFile.Path);
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("Required external configuration file is missing", error.Message);
        Assert.Contains("application.Example.json", error.Message);
        Assert.Null(error.InnerException);
        Assert.False(File.Exists(factory.ConfigurationFile.Path));
    }

    [Fact]
    public async Task MalformedJsonFailsWithActionableRedactedDiagnostics()
    {
        await using var factory = new PortalFactory();
        File.WriteAllText(factory.ConfigurationFile.Path, "{\"Entra\":{\"ClientSecret\":\"SECRET_JSON_SENTINEL\"}, SECRET_PARSE_SENTINEL }");
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("invalid JSON", error.Message); Assert.Contains("restart", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString() + string.Join('\n', factory.Logs.Messages));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"host\":\"portal.example.invalid\"}")]
    [InlineData("[\"portal.example.invalid\"]")]
    [InlineData("\"  \"")]
    public async Task AllowedHostsMustBeANonemptyJsonStringForNormalStartup(string value)
    {
        await using var factory = new PortalFactory();
        File.WriteAllText(factory.ConfigurationFile.Path, "{\"AllowedHosts\":" + value + "}");
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.DoesNotContain("portal.example.invalid", error.ToString());
    }

    [Theory]
    [InlineData("AllowedHosts")]
    [InlineData("Portal")]
    [InlineData("Portal:ReportServer")]
    [InlineData("Portal:ReportAccessPolicy")]
    [InlineData("Portal:AllowedRoots")]
    [InlineData("Entra")]
    [InlineData("Entra:ClientId")]
    [InlineData("Entra:AllowedTenantIds")]
    public async Task MissingRequiredSectionOrValueStopsStartup(string key)
    {
        await using var factory = new PortalFactory();
        var parts = key.Split(':');
        var parent = parts.Length == 1 ? factory.ConfigurationFile.Document : factory.ConfigurationFile.Document[parts[0]]!.AsObject();
        parent.Remove(parts[^1]); factory.ConfigurationFile.Save();
        Assert.Throws<InvalidOperationException>(() => factory.Browser());
    }

    [Theory]
    [InlineData("Portal", "AllowedRoots")]
    [InlineData("Entra", "AllowedTenantIds")]
    public async Task NullRequiredArraysFailClearly(string section, string key)
    {
        await using var factory = new PortalFactory();
        factory.ConfigurationFile.Document[section]![key] = null; factory.ConfigurationFile.Save();
        Assert.Throws<InvalidOperationException>(() => factory.Browser());
    }

    [Theory]
    [InlineData("Portal:ReportServer", "http://ssrs.example.invalid/ReportServer")]
    [InlineData("Portal:ReportServer", "https://user:SECRET_URI_SENTINEL@ssrs.example.invalid/ReportServer")]
    [InlineData("Portal:ReportServer", "https://ssrs.example.invalid/Reports")]
    [InlineData("Portal:ReportServer", "https://ssrs.example.invalid/ReportServer?secret=SECRET_QUERY_SENTINEL")]
    [InlineData("Portal:ReportServer", "https://ssrs.example.invalid/ReportServer#secret")]
    [InlineData("Portal:ReportAccessPolicy", "Other")]
    [InlineData("Portal:AllowedRoots:0", "/Training/../Other")]
    [InlineData("Portal:ExcludedPrefixes:0", "/Training/")]
    [InlineData("Portal:RequestSeconds", "121")]
    [InlineData("Portal:MaxSoapBytes", "67108865")]
    [InlineData("Portal:MaxOutputBytes", "33554433")]
    [InlineData("Portal:MaxMetadataItems", "10001")]
    [InlineData("Portal:MaxOptions", "10001")]
    [InlineData("Portal:MaxParameters", "201")]
    [InlineData("Portal:MaxStreams", "201")]
    [InlineData("Portal:MaxConcurrentSoap", "33")]
    [InlineData("Portal:MaxSessions", "501")]
    [InlineData("Portal:MaxSessionsPerUser", "11")]
    [InlineData("Portal:SessionMinutes", "61")]
    [InlineData("Portal:CatalogSeconds", "3601")]
    [InlineData("Portal:MaxSessionBytes", "4095")]
    [InlineData("Portal:MaxAggregateSessionBytes", "536870913")]
    [InlineData("Portal:RequestSeconds", "SECRET_BINDER_SENTINEL")]
    [InlineData("Portal:UseMock", "SECRET_BOOLEAN_SENTINEL")]
    [InlineData("Logging:LogLevel:Default", "SECRET_LOG_LEVEL_SENTINEL")]
    [InlineData("Entra:ClientId", "SECRET_CLIENT_SENTINEL")]
    [InlineData("Entra:AllowedTenantIds:0", "SECRET_TENANT_SENTINEL")]
    [InlineData("Entra:AllowedTenantIds:0", "00000000-0000-0000-0000-000000000000")]
    [InlineData("Entra:AllowedTenantIds:0", "9188040d-6c67-4c5b-b112-36a304b66dad")]
    [InlineData("Entra:AllowedTenantIds:1", AuthenticationTests.Tenant)]
    [InlineData("CatalogMonitor:IntervalSeconds", "4")]
    [InlineData("CatalogMonitor:CommandTimeoutSeconds", "121")]
    public async Task InvalidOperationalValuesFailBeforeAnyNetworkAndNeverEchoValues(string key, string value)
    {
        await using var factory = new PortalFactory(new() { [key] = value, ["Entra:ClientSecret"] = "SECRET_CLIENT_CREDENTIAL_SENTINEL" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString() + string.Join('\n', factory.Logs.Messages));
    }

    [Theory]
    [InlineData("")]
    [InlineData("SECRET_SQL_SYNTAX_SENTINEL")]
    [InlineData("Server=sql.invalid;Database=ReportServer;User ID=SECRET_USER_SENTINEL;Password=SECRET_SQL_SENTINEL;Encrypt=true")]
    [InlineData("Server=sql.invalid;Database=ReportServer;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=sql.invalid;Database=ReportServer;Integrated Security=true;Encrypt=true;TrustServerCertificate=true")]
    public async Task InvalidEnabledSqlMonitorStopsStartupWithoutDisclosingCredentials(string connection)
    {
        await using var factory = new PortalFactory(new()
        {
            ["CatalogMonitor:Enabled"] = "true", ["CatalogMonitor:ConnectionString"] = connection
        });
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("CatalogMonitor", error.Message); Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString() + string.Join('\n', factory.Logs.Messages));
    }

    [Theory]
    [InlineData("Portal", "ApprovedReports")]
    [InlineData("Entra", "TenantId")]
    [InlineData("Entra", "GroupId")]
    public async Task ObsoleteKeysAreRejectedEvenWhenEmpty(string section, string key)
    {
        await using var factory = new PortalFactory();
        factory.ConfigurationFile.Document[section]![key] = new JsonArray(); factory.ConfigurationFile.Save();
        Assert.Contains(section + ":" + key, Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
    }

    [Fact]
    public async Task ExternalFileIsAuthoritativeOverEnvironmentCommandLineAndAppsettingsWithoutArrayMerging()
    {
        using var file = new TestConfigurationFile();
        var directory = Path.GetDirectoryName(file.Path)!;
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), """
            { "AllowedHosts":"attacker.invalid", "Portal":{"UseMock":false,"AllowedRoots":["/Appsettings","/Stale"]},
              "Entra":{"AllowedTenantIds":["00000000-0000-0000-0000-000000000099"]} }
            """);
        // A unique prefix lets this test exercise the environment provider without changing
        // process-global operational variables used by concurrently running factories.
        var prefix = "SSRS_CONFIGURATION_REGRESSION_" + Guid.NewGuid().ToString("N") + "_";
        var variables = new Dictionary<string, string>
        {
            ["Portal__TestAuth"] = "false", ["Portal__ExcludedPrefixes__2"] = "/Stale",
            ["Entra__ClientSecret"] = "SECRET_ENV_SENTINEL", ["Logging__LogLevel__Default"] = "Trace",
            ["Logging__File__Enabled"] = "false", ["Logging__File__RetentionDays"] = "1"
        };
        try
        {
            foreach (var pair in variables) { Environment.SetEnvironmentVariable(prefix + pair.Key, pair.Value); }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development", ContentRootPath = directory,
                Args = ["--urls", "http://127.0.0.1:41234", "--Portal:UseMock", "false", "--Portal:AllowedRoots:1", "/CommandLine",
                    "--Entra:ClientId", "SECRET_ARG_SENTINEL", "--SsrsPortal:TestConfigurationFile", file.Path]
            });
            builder.Configuration.AddEnvironmentVariables(prefix);
            ExternalConfigurationLoader.AddTo(builder);
            await using var app = builder.Build();
            var portal = ExternalConfigurationLoader.Bind<PortalOptions>(app.Configuration, "Portal");
            var entra = ExternalConfigurationLoader.Bind<EntraOptions>(app.Configuration, "Entra");
            portal.Validate(true); entra.Validate(true);
            Assert.True(portal.UseMock); Assert.True(portal.TestAuth);
            Assert.Equal(["/Training"], portal.AllowedRoots);
            Assert.Equal(2, portal.ExcludedPrefixes.Length);
            Assert.Equal(AuthenticationTests.Client, entra.ClientId); Assert.Empty(entra.ClientSecret);
            Assert.Equal(2, entra.AllowedTenantIds.Length);
            Assert.Equal("localhost", app.Configuration["AllowedHosts"]);
            Assert.Equal("Information", app.Configuration["Logging:LogLevel:Default"]);
            var logging = Logging.FileLoggingOptions.Read(app.Configuration);
            Assert.True(logging.Enabled); Assert.Equal(30, logging.RetentionDays);
            Assert.Equal("http://127.0.0.1:41234", app.Configuration[WebHostDefaults.ServerUrlsKey]);
            Assert.True(app.Environment.IsDevelopment());
        }
        finally
        {
            foreach (var key in variables.Keys) { Environment.SetEnvironmentVariable(prefix + key, null); }
        }
    }

    [Fact]
    public async Task ObsoleteKeysInDiscardedProvidersStillFailClearly()
    {
        await using var factory = new PortalFactory(hostSettings: new() { ["Entra:GroupId"] = "SECRET_LEGACY_SENTINEL" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("Entra:GroupId", error.Message); Assert.DoesNotContain("SECRET_", error.ToString());
    }

    [Fact]
    public async Task OperationalFileCannotDeclareItsOwnTestPathOverride()
    {
        await using var factory = new PortalFactory(new() { [ExternalConfigurationLoader.TestFileKey] = "SECRET_PATH_SENTINEL" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("must not appear in application.json", error.Message);
        Assert.DoesNotContain("SECRET_", error.ToString());
    }

    [Fact]
    public async Task DevelopmentCanUseRealSsrsAndEntraAndRequiresAClientSecret()
    {
        await using var factory = new PortalFactory(AuthenticationTests.RealAuthSettings());
        using var browser = factory.Browser();
        Assert.IsType<HttpClientHandler>(factory.Services.GetRequiredService<HttpMessageHandler>());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/TestLogin")).StatusCode);
        factory.Services.GetRequiredService<PortalOptions>().Validate(false);
        factory.Services.GetRequiredService<EntraOptions>().Validate(false);
        await using var missingSecret = new PortalFactory(new(AuthenticationTests.RealAuthSettings()) { ["Entra:ClientSecret"] = "" });
        Assert.Contains("ClientSecret", Assert.Throws<InvalidOperationException>(() => missingSecret.Browser()).Message);
    }

    [Fact]
    public async Task HostFilteringUsesOnlyExternalAllowedHosts()
    {
        await using var factory = new PortalFactory(new() { ["AllowedHosts"] = "portal.example.invalid" },
            hostSettings: new() { ["AllowedHosts"] = "*" });
        using var browser = factory.Browser();
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("/TestLogin")).StatusCode);
        using var approved = new HttpRequestMessage(HttpMethod.Get, "/TestLogin");
        approved.Headers.Host = "portal.example.invalid";
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(approved)).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Staging")]
    public async Task NonDevelopmentRejectsTheTestPathBeforeReadingAnyFile(string environment)
    {
        await using var factory = new PortalFactory(AuthenticationTests.RealAuthSettings(), environment);
        File.Delete(factory.ConfigurationFile.Path);
        Assert.Contains("test-only", Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/application.json")]
    public async Task TestOverrideRejectsBlankAndRelativePaths(string path)
    {
        await using var factory = new PortalFactory(hostSettings: new() { [ExternalConfigurationLoader.TestFileKey] = path });
        Assert.Contains("absolute temporary file path", Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ProductionValidationRejectsSyntheticSettingsReadFromTheExternalFile(bool mock, bool testAuth)
    {
        using var file = new TestConfigurationFile(new() { ["Portal:UseMock"] = mock.ToString(), ["Portal:TestAuth"] = testAuth.ToString() });
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(file.Path, optional: false, reloadOnChange: false).Build();
        var portal = ExternalConfigurationLoader.Bind<PortalOptions>(configuration, "Portal");
        Assert.Contains("require Development", Assert.Throws<InvalidOperationException>(() => portal.Validate(false)).Message);
    }

    [Fact]
    public async Task SyntheticAuthenticationRequiresSyntheticSsrsEvenInDevelopment()
    {
        await using var factory = new PortalFactory(new() { ["Portal:UseMock"] = "false" });
        Assert.Contains("requires synthetic SOAP", Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
    }

    [Fact]
    public async Task ConcurrentHostsHaveIndependentFilesAndNoReloadAndCleanUpOnDisposal()
    {
        var first = new PortalFactory(new() { ["Portal:CatalogSeconds"] = "111" });
        var second = new PortalFactory(new() { ["Portal:CatalogSeconds"] = "222" });
        var firstPath = first.ConfigurationFile.Path; var secondPath = second.ConfigurationFile.Path;
        try
        {
            Assert.NotEqual(firstPath, secondPath);
            await Task.WhenAll(Task.Run(() => { using var client = first.Browser(); }), Task.Run(() => { using var client = second.Browser(); }));
            Assert.Equal(111, first.Services.GetRequiredService<PortalOptions>().CatalogSeconds);
            Assert.Equal(222, second.Services.GetRequiredService<PortalOptions>().CatalogSeconds);
            first.ConfigurationFile.Document["Portal"]!["CatalogSeconds"] = 333; first.ConfigurationFile.Save();
            Assert.Equal(111, first.Services.GetRequiredService<PortalOptions>().CatalogSeconds);
            Assert.Equal("111", first.Services.GetRequiredService<IConfiguration>()["Portal:CatalogSeconds"]);
            Assert.NotSame(first.Mock, second.Mock);
        }
        finally
        {
            await first.DisposeAsync(); await second.DisposeAsync();
        }
        Assert.False(File.Exists(firstPath)); Assert.False(File.Exists(secondPath));
    }
}
