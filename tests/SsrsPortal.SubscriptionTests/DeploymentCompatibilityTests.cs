using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Clc.Deployment.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Configuration;
using SsrsPortal.Tests;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class DeploymentCompatibilityTests
{
    private static Assembly Identity => typeof(DeploymentCompatibilityTests).Assembly;

    private static TestConfigurationFile Config(string keys)
    {
        var file = new TestConfigurationFile();
        file.Document["Portal"]!["UseMock"] = false;
        file.Document["Portal"]!["TestAuth"] = false;
        file.Document["Entra"]!["ClientSecret"] = "SECRET_SENTINEL";
        file.Document["Deployment"] = new JsonObject { ["ConfigurationRevision"] = "test-001" };
        file.Document["DataProtection"] = new JsonObject { ["KeyStoreCompatibilityId"] = PersistentKeyOptions.CompatibilityId, ["KeyRingPath"] = keys };
        file.Save();
        return file;
    }

    [TestMethod]
    public void ValidatorRejectsArgumentsAndSyntheticIdentityWithExactBoundedSecretFreeOutput()
    {
        using var output = new StringWriter();
        Assert.AreEqual(1, DeploymentContract.Run(["--validate-deployment", "SECRET_SENTINEL"], output));
        using var document = JsonDocument.Parse(output.ToString());
        CollectionAssert.AreEquivalent(new[] { "validationContractVersion", "valid", "applicationId", "releaseId", "sourceCommit",
            "runtimeEnvironment", "configurationRevision", "keyStoreCompatibilityId", "errorCodes" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.IsLessThan(8192, System.Text.Encoding.UTF8.GetByteCount(output.ToString()));
        Assert.IsFalse(output.ToString().Contains("SECRET", StringComparison.Ordinal));
        Assert.IsFalse(document.RootElement.GetProperty("valid").GetBoolean());
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("production")]
    [DataRow("SECRET_SENTINEL")]
    public void UnsupportedEnvironmentNeverLoadsConfiguration(string environment)
    {
        var result = DeploymentContract.Validate(["--validate-deployment", "--configuration-path", "missing", "--data-protection-root", "missing", "--environment", environment], Identity);
        CollectionAssert.Contains(result.ErrorCodes, "UnsupportedEnvironment");
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ConventionalPathCannotBeOverriddenAndDuplicatesAreRefused()
    {
        var result = DeploymentContract.Validate(["--validate-deployment", "--configuration-path", Path.Combine(Path.GetTempPath(), "application.json"),
            "--data-protection-root", Path.GetTempPath(), "--environment", "Production"], Identity);
        CollectionAssert.Contains(result.ErrorCodes, "ConfigurationPathMismatch");
        result = DeploymentContract.Validate(["--validate-deployment", "--environment", "Production", "--environment", "Staging", "--data-protection-root", "x"], Identity);
        CollectionAssert.Contains(result.ErrorCodes, "InvalidArguments");
    }

    [TestMethod]
    public void ValidationOfMissingConfigurationDoesNotBootstrapDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "ssrs-readonly-preflight-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "Config", "application.json");
        var keys = Path.Combine(root, "Keys");
        var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", path,
            "--data-protection-root", keys, "--environment", "Production"], Identity, path);
        Assert.IsFalse(result.Valid);
        CollectionAssert.Contains(result.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
        Assert.IsFalse(Directory.Exists(root), "Deployment validation must leave a missing ProgramData fixture absent.");
    }

    [TestMethod]
    public async Task CommandLineValidationExitsPromptlyWithoutHostOrSecretDiagnostics()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        foreach (var argument in new[] { Path.Combine(repository, $"src/SsrsPortal/bin/{buildConfiguration}/net10.0/SsrsPortal.dll"),
            "--validate-deployment", "--configuration-path", "SECRET_SENTINEL", "--data-protection-root", "SECRET_SENTINEL", "--environment", "Production" })
        { start.ArgumentList.Add(argument); }
        start.Environment["SsrsPortal__TestConfigurationFile"] = "SECRET_SENTINEL";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:1";
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(true); throw; }
        Assert.AreEqual(1, process.ExitCode);
        Assert.AreEqual("", await errors);
        using var document = JsonDocument.Parse(await output);
        Assert.IsFalse(document.RootElement.GetProperty("valid").GetBoolean());
        Assert.IsFalse((await output).Contains("SECRET", StringComparison.Ordinal));
        Assert.IsLessThan(8192, (await output).Length);
    }

    [TestMethod]
    [DataRow("{\"Portal\":false}")]
    [DataRow("{\"Portal\":{},\"portal\":{}}")]
    [DataRow("{\"Portal\":{\"AllowedRoots\":{\"0\":\"/A\"}}}")]
    [DataRow("{\"Portal\":{\"ReportServer\":42}}")]
    [DataRow("{\"Portal\":{\"Diagnostics\":null}}")]
    [DataRow("{\"Unused\":[[{\"Key\":\"one\",\"key\":\"two\"}]]}")]
    [DataRow("{\"Deployment:ConfigurationRevision\":\"SECRET_SENTINEL\"}")]
    public void SharedLoaderRejectsInvalidStructureWithoutEchoingInput(string json)
    {
        using var file = new TestConfigurationFile();
        File.WriteAllText(file.Path, json);
        var error = Assert.Throws<InvalidOperationException>(() => ExternalConfigurationLoader.Load(file.Path));
        Assert.IsFalse(error.ToString().Contains("SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("false")]
    [DataRow("42")]
    [DataRow("null")]
    [DataRow("{\"host\":\"portal.example.invalid\"}")]
    [DataRow("[\"portal.example.invalid\"]")]
    public void ValidatorRejectsNonStringAllowedHostsWithStableErrors(string value)
    {
        using var file = new TestConfigurationFile();
        File.WriteAllText(file.Path, "{\"AllowedHosts\":" + value + "}");
        var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", file.Path,
            "--data-protection-root", Path.Combine(Path.GetTempPath(), "unused-key-root"), "--environment", "Production"], Identity, file.Path);
        Assert.IsFalse(result.Valid);
        CollectionAssert.Contains(result.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
        Assert.IsLessThan(8192, JsonSerializer.Serialize(result, DeploymentContract.Json).Length);
    }

    [TestMethod]
    [DoNotParallelize]
    public void ValidatorAndNormalStartupRejectAmbientBootstrapConflictsBeforeReadingTheSuccessFixture()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("A usable Windows DPAPI fixture is required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-ambient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var config = Config(root);
        try
        {
            using (var provider = KeyProvider(root))
            { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie"); }
            var arguments = new[] { "--validate-deployment", "--configuration-path", config.Path,
                "--data-protection-root", root, "--environment", "Production" };
            Assert.IsTrue(DeploymentContract.ValidateCore(arguments, Identity, config.Path).Valid,
                "The conventional success fixture must pass before ambient conflicts are introduced.");

            var conflicts = new Dictionary<string, string>
            {
                ["SsrsPortal__TestConfigurationFile"] = config.Path,
                ["SsrsPortal__TestConfigurationFile__Nested"] = "sentinel",
                ["Entra__GroupId"] = "sentinel",
                ["Entra__GroupId__0"] = "sentinel",
                ["Entra__TenantId"] = "sentinel",
                ["Entra:TenantId"] = "sentinel",
                ["Portal__ApprovedReports__0"] = "sentinel",
                ["ASPNETCORE_SsrsPortal__TestConfigurationFile"] = config.Path,
                ["DOTNET_SsrsPortal__TestConfigurationFile__Nested"] = "sentinel",
                ["ASPNETCORE_Entra__GroupId"] = "sentinel",
                ["DOTNET_Entra__TenantId__0"] = "sentinel"
            };
            foreach (var conflict in conflicts)
            {
                Environment.SetEnvironmentVariable(conflict.Key, conflict.Value);
                try
                {
                    var result = DeploymentContract.ValidateCore(arguments, Identity, config.Path);
                    Assert.IsFalse(result.Valid, conflict.Key);
                    CollectionAssert.Contains(result.ErrorCodes, "AmbiguousConfigurationSource", conflict.Key);

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
                    try { Assert.Throws<InvalidOperationException>(() => ExternalConfigurationLoader.AddTo(builder)); }
                    finally { ((IDisposable)builder.Configuration).Dispose(); }
                }
                finally { Environment.SetEnvironmentVariable(conflict.Key, null); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SharedOptionsValidateRevisionCompatibilitySubscriptionsAndDevelopmentIsolationWithoutServices()
    {
        using var file = Config(Path.GetTempPath());
        using (var configuration = ExternalConfigurationLoader.Load(file.Path))
        { Assert.AreEqual("test-001", ApplicationConfiguration.Read(configuration, false).Deployment.ConfigurationRevision); }
        file.Document["Deployment"]!["ConfigurationRevision"] = "test-002"; file.Save();
        using (var configuration = ExternalConfigurationLoader.Load(file.Path))
        { Assert.AreEqual("test-002", ApplicationConfiguration.Read(configuration, false).Deployment.ConfigurationRevision); }
        foreach (var edit in new Action[]
        {
            () => file.Document["Deployment"]!["ConfigurationRevision"] = "SECRET bad revision",
            () => file.Document["DataProtection"]!["KeyStoreCompatibilityId"] = "wrong",
            () => file.Document["Portal"]!["UseMock"] = true,
            () => { file.Document["Subscriptions"] = new JsonObject { ["Enabled"] = true, ["ConnectionString"] = "SECRET_SENTINEL" }; }
        })
        {
            using var valid = Config(Path.GetTempPath());
            var before = file.Document.DeepClone();
            edit(); file.Save();
            using var configuration = ExternalConfigurationLoader.Load(file.Path);
            var error = Assert.Throws<InvalidOperationException>(() => ApplicationConfiguration.Read(configuration, false));
            Assert.IsFalse(error.ToString().Contains("SECRET", StringComparison.Ordinal));
            file.Document.Clear(); foreach (var entry in before.AsObject()) { file.Document[entry.Key] = entry.Value?.DeepClone(); }
        }
    }

    [TestMethod]
    public void DisabledOptionalMonitorAndSubscriptionNullStringsRetainPureValidationSemantics()
    {
        using var file = Config(Path.GetTempPath());
        file.Document["Portal"]!["UseMock"] = true;
        file.Document["Portal"]!["TestAuth"] = true;
        file.Document["Entra"]!["ClientSecret"] = null;
        file.Document["CatalogMonitor"] = new JsonObject { ["Enabled"] = false, ["ConnectionString"] = null };
        file.Document["Subscriptions"] = new JsonObject
        {
            ["Enabled"] = false, ["ConnectionString"] = null, ["PostmarkServerToken"] = null,
            ["PortalBaseUrl"] = null, ["From"] = null, ["MessageStream"] = null
        };
        file.Save();

        using var configuration = ExternalConfigurationLoader.Load(file.Path);
        var settings = ApplicationConfiguration.Read(configuration, development: true);
        Assert.IsFalse(settings.Monitor.Enabled);
        Assert.IsNull(settings.Monitor.ConnectionString);
        Assert.IsFalse(settings.Subscriptions.Enabled);
        Assert.IsNull(settings.Subscriptions.ConnectionString);
        Assert.IsNull(settings.Subscriptions.PostmarkServerToken);
        Assert.IsNull(settings.Subscriptions.PortalBaseUrl);
        Assert.IsNull(settings.Subscriptions.From);
        Assert.IsNull(settings.Subscriptions.MessageStream);
        Assert.IsNull(settings.Entra.ClientSecret);
    }

    [TestMethod]
    public void ValidatorPreservesPriorBinderSemanticsForOptionalSubsystemSections()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Production validation requires the Windows persistent-key contract."); }
        var fixtures = new (string Section, string Json)[]
        {
            ("CatalogMonitor", "{\"Enabled\":null}"),
            ("CatalogMonitor", "[]"),
            ("Subscriptions", "{\"Enabled\":null,\"Workers\":null,\"PostmarkServerToken\":42}"),
            ("Subscriptions", "[]"),
            ("Subscriptions", "{}")
        };
        foreach (var fixture in fixtures)
        {
            var root = Path.Combine(Path.GetTempPath(), "ssrs-optional-binder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var file = Config(root);
                file.Document[fixture.Section] = JsonNode.Parse(fixture.Json);
                file.Save();
                using (var provider = KeyProvider(root))
                { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie"); }

                using var priorConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(file.Path, optional: false, reloadOnChange: false).Build();
                var prior = ApplicationConfiguration.Read(priorConfiguration, development: false);
                using var currentConfiguration = ExternalConfigurationLoader.Load(file.Path);
                var current = ApplicationConfiguration.Read(currentConfiguration, development: false);
                Assert.AreEqual(prior.Monitor.Enabled, current.Monitor.Enabled, fixture.Json);
                Assert.AreEqual(prior.Monitor.IntervalSeconds, current.Monitor.IntervalSeconds, fixture.Json);
                Assert.AreEqual(prior.Monitor.CommandTimeoutSeconds, current.Monitor.CommandTimeoutSeconds, fixture.Json);
                Assert.AreEqual(prior.Monitor.ConnectionString, current.Monitor.ConnectionString, fixture.Json);
                Assert.AreEqual(prior.Subscriptions.Enabled, current.Subscriptions.Enabled, fixture.Json);
                Assert.AreEqual(prior.Subscriptions.Workers, current.Subscriptions.Workers, fixture.Json);
                Assert.AreEqual(prior.Subscriptions.PostmarkServerToken, current.Subscriptions.PostmarkServerToken, fixture.Json);

                var arguments = new[] { "--validate-deployment", "--configuration-path", file.Path,
                    "--data-protection-root", root, "--environment", "Production" };
                Assert.IsTrue(DeploymentContract.ValidateCore(arguments, Identity, file.Path).Valid, fixture.Json);
            }
            finally { Directory.Delete(root, true); }
        }

        foreach (var fixture in new (string Section, string Json)[]
        {
            ("CatalogMonitor", "{\"Enabled\":true,\"ConnectionString\":\"invalid\"}"),
            ("Subscriptions", "{\"Enabled\":true,\"ConnectionString\":\"invalid\"}")
        })
        {
            var root = Path.Combine(Path.GetTempPath(), "ssrs-invalid-optional-binder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var file = Config(root);
                file.Document[fixture.Section] = JsonNode.Parse(fixture.Json);
                file.Save();
                using (var provider = KeyProvider(root))
                { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie"); }
                using var priorConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(file.Path, optional: false, reloadOnChange: false).Build();
                Assert.Throws<InvalidOperationException>(() => ApplicationConfiguration.Read(priorConfiguration, development: false));
                using var currentConfiguration = ExternalConfigurationLoader.Load(file.Path);
                Assert.Throws<InvalidOperationException>(() => ApplicationConfiguration.Read(currentConfiguration, development: false));
                var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", file.Path,
                    "--data-protection-root", root, "--environment", "Production"], Identity, file.Path);
                Assert.IsFalse(result.Valid);
                CollectionAssert.Contains(result.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [TestMethod]
    public void SupportedBomEncodingsPreservePriorBinderAndDeploymentValidationSemanticsWithoutRewrites()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Production validation requires the Windows persistent-key contract."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-bom-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var file = Config(root);
            using (var provider = KeyProvider(root))
            { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie"); }
            var json = File.ReadAllText(file.Path);
            foreach (var encoding in BomEncodings())
            {
                var encoded = EncodeWithPreamble(json, encoding);
                File.WriteAllBytes(file.Path, encoded);
                using var priorConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(file.Path, optional: false, reloadOnChange: false).Build();
                var prior = ApplicationConfiguration.Read(priorConfiguration, development: false);
                using var currentConfiguration = ExternalConfigurationLoader.Load(file.Path);
                var current = ApplicationConfiguration.Read(currentConfiguration, development: false);
                Assert.AreEqual(prior.Portal.ReportServer, current.Portal.ReportServer, encoding.WebName);
                Assert.AreEqual(prior.Entra.ClientId, current.Entra.ClientId, encoding.WebName);
                Assert.AreEqual(prior.Monitor.IntervalSeconds, current.Monitor.IntervalSeconds, encoding.WebName);
                Assert.AreEqual(prior.Subscriptions.Workers, current.Subscriptions.Workers, encoding.WebName);
                var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", file.Path,
                    "--data-protection-root", root, "--environment", "Production"], Identity, file.Path);
                Assert.IsTrue(result.Valid, string.Join(',', result.ErrorCodes));
                CollectionAssert.AreEqual(encoded, File.ReadAllBytes(file.Path));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedBomEncodingsFailLoaderAndValidatorRedactedWithoutRewritingTheInput()
    {
        foreach (var failureKind in new[] { "utf8-truncated", "utf8-invalid", "utf16le-surrogate", "utf16be-surrogate", "utf32le-truncated", "utf32be-invalid-scalar" })
        {
            using var file = Config(Path.Combine(Path.GetTempPath(), "unused-encoding-key-root"));
            var bytes = MalformedEncoding(failureKind);
            File.WriteAllBytes(file.Path, bytes);
            var before = File.ReadAllBytes(file.Path);
            var error = Assert.Throws<InvalidOperationException>(() => ExternalConfigurationLoader.Load(file.Path));
            Assert.IsFalse(error.ToString().Contains("SECRET_ENCODING_SENTINEL", StringComparison.Ordinal));

            var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", file.Path,
                "--data-protection-root", Path.Combine(Path.GetTempPath(), "unused-encoding-key-root"), "--environment", "Production"], Identity, file.Path);
            Assert.IsFalse(result.Valid, failureKind);
            CollectionAssert.Contains(result.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
            Assert.IsFalse(JsonSerializer.Serialize(result, DeploymentContract.Json).Contains("SECRET_ENCODING_SENTINEL", StringComparison.Ordinal));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(file.Path));
        }
    }

    [TestMethod]
    public async Task AnonymousHealthNeverChallengesOrSetsCookiesEvenForInvalidStaffCookie()
    {
        await using var factory = new PortalFactory();
        using var client = factory.Browser();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-Portal=invalid");
        var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode); // Synthetic Development cannot claim release readiness.
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
        Assert.IsNull(response.Headers.Location);
        Assert.AreEqual(0, response.Headers.WwwAuthenticate.Count);
    }

    [TestMethod]
    public async Task StartedReadinessWritesExactSuccessfulHttpContract()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.WebHost.UseTestServer();
        var state = new DeploymentReadinessGate();
        var identity = ClcApplicationIdentity.ReadFromAssembly(Identity, "ssrs-web");
        await using var app = builder.Build();
        app.UseClcDeploymentReadiness(identity, "Production", () => "test-001", state);
        state.MarkReady();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "readinessContractVersion", "status", "applicationId", "releaseId", "sourceCommit", "runtimeEnvironment", "configurationRevision" },
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("ssrs-web-test-001", document.RootElement.GetProperty("releaseId").GetString());
        await app.StopAsync();
    }

    [TestMethod]
    public async Task ReadinessRequiresApplicationChecksAndHostStartAndRejectsStopping()
    {
        var gate = new DeploymentReadinessGate();
        var lifetime = new TestHostApplicationLifetime();
        using var services = new ServiceCollection().AddSingleton<IHostApplicationLifetime>(lifetime).BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseClcDeploymentReadiness(ClcApplicationIdentity.ReadFromAssembly(Identity, "ssrs-web"), "Production", () => "test-001", gate);
        var pipeline = builder.Build();

        var beforeChecks = await ReadinessResponse(pipeline, services);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, beforeChecks.StatusCode);
        Assert.IsTrue(beforeChecks.NoStore);

        gate.MarkReady(); // The application marks ready only after its local configuration and key checks.
        var beforeHostStart = await ReadinessResponse(pipeline, services);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, beforeHostStart.StatusCode);
        Assert.IsTrue(beforeHostStart.NoStore);

        lifetime.Start();
        var started = await ReadinessResponse(pipeline, services);
        Assert.AreEqual(StatusCodes.Status200OK, started.StatusCode);
        Assert.IsTrue(started.NoStore);

        lifetime.StopApplication();
        var stopping = await ReadinessResponse(pipeline, services);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, stopping.StatusCode);
        Assert.IsTrue(stopping.NoStore);
    }

    private static async Task<(int StatusCode, bool NoStore)> ReadinessResponse(RequestDelegate pipeline, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/health/ready";
        context.Response.Body = new MemoryStream();
        await pipeline(context);
        return (context.Response.StatusCode, context.Response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void Start() => started.Cancel();
        public void StopApplication() => stopping.Cancel();
    }

    [TestMethod]
    public void WindowsPlaintextAndUnknownProtectionAreRejectedReadOnly()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows key-ring checks required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-bad-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var xml in new[] { "<key><masterKey><value>SECRET_SENTINEL</value></masterKey></key>",
                "<key><encryptedSecret xmlns='http://schemas.asp.net/2015/03/dataProtection' decryptorType='unknown'><unknown/></encryptedSecret></key>" })
            {
                var path = Path.Combine(root, "key.xml"); File.WriteAllText(path, xml);
                var error = Assert.Throws<InvalidOperationException>(() => PersistentKeys.ValidateDirectory(root, AppContext.BaseDirectory));
                Assert.IsFalse(error.ToString().Contains("SECRET", StringComparison.Ordinal));
                Assert.AreEqual(xml, File.ReadAllText(path));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WindowsFrameworkGeneratedKeyAndRevocationMetadataRemainReadableWithoutWrites()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows key-ring checks required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-revoked-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            using (var provider = KeyProvider(root))
            {
                _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie");
                var manager = provider.GetRequiredService<IKeyManager>();
                _ = manager.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(30));
                id = manager.GetAllKeys().First(key => !key.IsRevoked).KeyId;
                manager.RevokeKey(id, "synthetic test revocation");
                Assert.IsTrue(manager.GetAllKeys().Single(key => key.KeyId == id).IsRevoked);
            }

            var before = Snapshot(root);
            PersistentKeys.ValidateDirectory(root, AppContext.BaseDirectory);
            AssertSnapshot(before, root);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WindowsMalformedRetainedKeyMetadataIsRejectedBeforeAReplacementCanBeWritten()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows key-ring checks required."); }
        foreach (var mutation in new Action<XElement>[]
        {
            root => root.SetAttributeValue("id", "not-a-guid"),
            root => root.SetAttributeValue("version", "2"),
            root => root.Element("creationDate")!.Value = "not-a-time",
            root => root.Element("activationDate")!.Value = "2099-01-01T00:00:00Z",
            root => root.Element("expirationDate")!.Value = "not-a-time",
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType", "unknown"),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "type")),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "assembly")),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "token")),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "duplicate")),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "extra")),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "version")),
            root => root.Element("descriptor")!.SetAttributeValue("deserializerType",
                MutateAssemblyQualifiedType((string)root.Element("descriptor")!.Attribute("deserializerType")!, "overflow")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection"))
                .Single().SetAttributeValue("decryptorType", "unknown"),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "assembly")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "token")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "duplicate")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "extra")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "version")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "overflow")),
            root => root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
                .SetAttributeValue("decryptorType", MutateAssemblyQualifiedType(
                    (string)root.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single().Attribute("decryptorType")!, "future")),
            root => root.Descendants("ssrs-user-dpapi").Single().SetAttributeValue("version", "2"),
            root => root.Descendants("ssrs-user-dpapi").Single().Element("encryptedKey")!.Element("value")!.Value = "AQ*D",
            root => root.Descendants("ssrs-user-dpapi").Single().Element("encryptedKey")!.Element("value")!.Value = "AQ=",
            root => root.Descendants("ssrs-user-dpapi").Single().Element("encryptedKey")!.Element("value")!.Value = ""
        })
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ssrs-malformed-key-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            try
            {
                using (var provider = KeyProvider(rootPath))
                { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie"); }
                var keyPath = Directory.GetFiles(rootPath, "key-*.xml").Single();
                var document = XDocument.Load(keyPath);
                mutation(document.Root!);
                document.Save(keyPath);
                var before = Snapshot(rootPath);
                Assert.Throws<InvalidOperationException>(() => PersistentKeys.ValidateDirectory(rootPath, AppContext.BaseDirectory));
                AssertSnapshot(before, rootPath);
            }
            finally { Directory.Delete(rootPath, true); }
        }
    }

    [TestMethod]
    public void TrustedAssemblyQualifiedTypeParserAllowsOnlyTrustedIdentityWithAnyValidVersion()
    {
        var descriptor = typeof(IKeyManager).Assembly.GetType(
            "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer")!;
        var appDecryptorIdentity = typeof(UserScopeDpapiDecryptor).AssemblyQualifiedName!;
        Assert.IsTrue(PersistentKeys.MatchesBindableAppDecryptorIdentity(appDecryptorIdentity));
        Assert.IsTrue(PersistentKeys.MatchesBindableAppDecryptorIdentity(MutateAssemblyQualifiedType(appDecryptorIdentity, "older")));
        Assert.IsTrue(PersistentKeys.MatchesBindableAppDecryptorIdentity(MutateAssemblyQualifiedType(appDecryptorIdentity, "casing")));
        Assert.IsFalse(PersistentKeys.MatchesBindableAppDecryptorIdentity(MutateAssemblyQualifiedType(appDecryptorIdentity, "future")));
        foreach (var trustedType in new[] { descriptor, typeof(UserScopeDpapiDecryptor) })
        {
            var identity = trustedType.AssemblyQualifiedName!;
            Assert.IsTrue(PersistentKeys.MatchesTrustedTypeIdentity(identity, trustedType));
            Assert.IsFalse(PersistentKeys.MatchesTrustedTypeIdentity(new string('A', 1025), trustedType));
            Assert.IsTrue(PersistentKeys.MatchesTrustedTypeIdentity(MutateAssemblyQualifiedType(identity, "older"), trustedType));
            Assert.IsTrue(PersistentKeys.MatchesTrustedTypeIdentity(MutateAssemblyQualifiedType(identity, "future"), trustedType));
            Assert.IsTrue(PersistentKeys.MatchesTrustedTypeIdentity(MutateAssemblyQualifiedType(identity, "maximum"), trustedType));
            Assert.IsTrue(PersistentKeys.MatchesTrustedTypeIdentity(MutateAssemblyQualifiedType(identity, "casing"), trustedType));
            foreach (var mutation in new[] { "type", "assembly", "token", "culture", "duplicate", "extra", "version", "overflow" })
            { Assert.IsFalse(PersistentKeys.MatchesTrustedTypeIdentity(MutateAssemblyQualifiedType(identity, mutation), trustedType), mutation); }
        }
    }

    [TestMethod]
    public void WindowsOlderAssemblyVersionsAndWrappedBase64PreserveExistingCookieWithoutWrites()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows DPAPI retained key compatibility required."); }
        foreach (var metadataKind in new[] { "app-decryptor", "framework-descriptor", "app-decryptor-casing", "framework-descriptor-future" })
        {
            var root = Path.Combine(Path.GetTempPath(), "ssrs-cross-version-key-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string protectedCookie;
                using (var initial = KeyProvider(root))
                {
                    protectedCookie = initial.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture")
                        .Protect("existing-cookie");
                }

                var keyPath = Directory.GetFiles(root, "key-*.xml").Single();
                var document = XDocument.Load(keyPath);
                if (metadataKind is "app-decryptor" or "app-decryptor-casing")
                {
                    var encrypted = document.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single();
                    var mutation = metadataKind == "app-decryptor" ? "older" : "casing";
                    encrypted.SetAttributeValue("decryptorType", MutateAssemblyQualifiedType((string)encrypted.Attribute("decryptorType")!, mutation));
                    if (metadataKind == "app-decryptor")
                    {
                        var value = encrypted.Element("ssrs-user-dpapi")!.Element("encryptedKey")!.Element("value")!;
                        var encoded = value.Value;
                        value.Value = encoded.Insert(Math.Min(12, encoded.Length), "\r\n  ");
                    }
                }
                else
                {
                    var descriptor = document.Root!.Element("descriptor")!;
                    var mutation = metadataKind == "framework-descriptor-future" ? "future" : "older";
                    descriptor.SetAttributeValue("deserializerType", MutateAssemblyQualifiedType((string)descriptor.Attribute("deserializerType")!, mutation));
                }
                document.Save(keyPath);
                var before = Snapshot(root);

                PersistentKeys.ValidateDirectory(root, AppContext.BaseDirectory);
                AssertSnapshot(before, root);
                using var retained = KeyProvider(root);
                var plainCookie = retained.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Unprotect(protectedCookie);
                Assert.AreEqual("existing-cookie", plainCookie);
                AssertSnapshot(before, root);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [TestMethod]
    public void WindowsFutureAppDecryptorVersionIsRejectedBeforeRuntimeProtectOrAnyWrite()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows DPAPI binding qualification required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-future-app-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string protectedCookie;
            using (var current = KeyProvider(root))
            {
                var protector = current.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture");
                protectedCookie = protector.Protect("valid-existing-cookie");
                Assert.AreEqual("valid-existing-cookie", protector.Unprotect(protectedCookie));
            }
            using var config = Config(root);
            var keyPath = Directory.GetFiles(root, "key-*.xml").Single();
            var document = XDocument.Load(keyPath);
            var encrypted = document.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single();
            encrypted.SetAttributeValue("decryptorType", MutateAssemblyQualifiedType((string)encrypted.Attribute("decryptorType")!, "future"));
            document.Save(keyPath);
            var afterMutation = Snapshot(root);

            string[] arguments = ["--validate-deployment", "--configuration-path", config.Path,
                "--data-protection-root", root, "--environment", "Production"];
            var validation = DeploymentContract.ValidateCore(arguments, Identity, config.Path);
            Assert.IsFalse(validation.Valid);
            CollectionAssert.Contains(validation.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
            AssertSnapshot(afterMutation, root);

            using var runtime = KeyProvider(root);
            Assert.Throws<InvalidOperationException>(() => PersistentKeys.VerifyUsable(runtime, root, AppContext.BaseDirectory));
            AssertSnapshot(afterMutation, root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string MutateAssemblyQualifiedType(string identity, string mutation)
    {
        var parts = identity.Split(',');
        var versionIndex = Array.FindIndex(parts, part => part.TrimStart().StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
        var tokenIndex = Array.FindIndex(parts, part => part.TrimStart().StartsWith("PublicKeyToken=", StringComparison.OrdinalIgnoreCase));
        var cultureIndex = Array.FindIndex(parts, part => part.TrimStart().StartsWith("Culture=", StringComparison.OrdinalIgnoreCase));
        return mutation switch
        {
            "older" or "future" => ReplacePart(parts, versionIndex, " Version=" + (mutation == "older" ? "0.9.0.0" : "999.0.0.0")),
            "maximum" => ReplacePart(parts, versionIndex, " Version=65535.65535.65535.65535"),
            "version" => ReplacePart(parts, versionIndex, " Version=invalid"),
            "overflow" => ReplacePart(parts, versionIndex, " Version=65536.0.0.0"),
            "casing" => ChangeAssemblyQualifiedTypeCasing(parts),
            "type" => ReplacePart(parts, 0, " Malicious.Type"),
            "assembly" => ReplacePart(parts, 1, " Untrusted.Assembly"),
            "culture" => ReplacePart(parts, cultureIndex, " Culture=evil"),
            "token" => ReplacePart(parts, tokenIndex, " PublicKeyToken=0011223344556677"),
            "duplicate" => identity + ", Version=1.0.0.0",
            "extra" => identity + ", ProcessorArchitecture=MSIL",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
    }

    private static string ChangeAssemblyQualifiedTypeCasing(string[] parts)
    {
        parts[1] = parts[1].ToLowerInvariant();
        for (var index = 2; index < parts.Length; index++)
        {
            var separator = parts[index].IndexOf('=');
            if (separator < 0) { continue; }
            var name = parts[index][..separator].Trim();
            var value = parts[index][(separator + 1)..].Trim();
            var changedName = name.Equals("Version", StringComparison.OrdinalIgnoreCase) ? "vErSiOn" :
                name.Equals("Culture", StringComparison.OrdinalIgnoreCase) ? "cUlTuRe" : "pUbLiCkEyToKeN";
            parts[index] = $" {changedName}={value.ToUpperInvariant()}";
        }
        return string.Join(',', parts);
    }

    private static string ReplacePart(string[] parts, int index, string value)
    {
        if (index < 0) { throw new InvalidOperationException("Generated assembly-qualified type omitted an expected field."); }
        parts[index] = value;
        return string.Join(',', parts);
    }

    [TestMethod]
    public void WindowsSkippedMalformedKeyIsDetectedEvenBesideAnotherValidKeyAndDuplicateIdsFailClosed()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows key-ring checks required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-skipped-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var provider = KeyProvider(root))
            {
                var manager = provider.GetRequiredService<IKeyManager>();
                _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie");
                _ = manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
            }
            var keys = Directory.GetFiles(root, "key-*.xml");
            Assert.AreEqual(2, keys.Length);
            var invalidPath = keys[0];
            var document = XDocument.Load(invalidPath);
            document.Root!.SetAttributeValue("id", "not-a-guid");
            document.Save(invalidPath);
            var before = Snapshot(root);
            Assert.Throws<InvalidOperationException>(() => PersistentKeys.ValidateDirectory(root, AppContext.BaseDirectory));
            AssertSnapshot(before, root);

            File.WriteAllBytes(invalidPath, before[invalidPath]);
            File.Copy(keys[1], Path.Combine(root, "duplicate-id.xml"));
            var duplicateBefore = Snapshot(root);
            Assert.Throws<InvalidOperationException>(() => PersistentKeys.ValidateDirectory(root, AppContext.BaseDirectory));
            AssertSnapshot(duplicateBefore, root);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WindowsValidationReadsUnusableCiphertextAsMetadataOnlyButRuntimeRejectsItBeforeProtect()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows DPAPI runtime check required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-unusable-ciphertext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var provider = KeyProvider(root))
            { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-cookie"); }
            using var config = Config(root);
            var keyPath = Directory.GetFiles(root, "key-*.xml").Single();
            var keyDocument = XDocument.Load(keyPath);
            keyDocument.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection"))
                .Single().Element("ssrs-user-dpapi")!.Element("encryptedKey")!.Element("value")!.Value = "AQID";
            keyDocument.Save(keyPath);
            var before = Snapshot(root);

            var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", config.Path,
                "--data-protection-root", root, "--environment", "Production"], Identity, config.Path);
            Assert.IsTrue(result.Valid, "Validate-only is metadata-only and must not decrypt under the deployment identity.");
            AssertSnapshot(before, root);

            using var runtime = KeyProvider(root);
            var runtimeError = Assert.Throws<InvalidOperationException>(() =>
                PersistentKeys.VerifyUsable(runtime, root, AppContext.BaseDirectory));
            Assert.IsFalse(runtimeError.ToString().Contains("AQID", StringComparison.Ordinal));
            AssertSnapshot(before, root);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WindowsFixedDriveClassifierRejectsNetworkAndUnknownVolumesBeforePathAccess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows path classification checks required."); }
        foreach (var driveType in new[] { DriveType.Network, DriveType.Unknown })
        {
            var classified = "";
            var error = Assert.Throws<InvalidOperationException>(() => LocalFileSystemPolicy.RequireLocalPhysicalPath(
                @"Z:\\nonexistent\application.json", "External application configuration", root =>
                { classified = root; return driveType; }));
            Assert.AreEqual("Z:\\", classified);
            Assert.IsFalse(error.ToString().Contains("nonexistent", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void WindowsProgramDataKnownFolderOnlyExpandsSystemDriveFromSystemWindowsDirectory()
    {
        Assert.AreEqual(@"E:\ProgramData", ProgramDataPath.NormalizeWindowsPath(@"%SystemDrive%\ProgramData", @"E:\Windows"));
        Assert.AreEqual(@"d:\Shared\ProgramData", ProgramDataPath.NormalizeWindowsPath(@"d:\Shared\ProgramData", null));
        Assert.Throws<InvalidOperationException>(() => ProgramDataPath.NormalizeWindowsPath(@"%SystemDrive%\ProgramData", null));
        Assert.Throws<InvalidOperationException>(() => ProgramDataPath.NormalizeWindowsPath(@"%USERPROFILE%\ProgramData", @"C:\Windows"));
        Assert.Throws<InvalidOperationException>(() => ProgramDataPath.NormalizeWindowsPath(@"%SystemDrive%\ProgramData", @"\\server\share\Windows"));
        ProgramDataPath.RequireSystemDriveAgreement(@"E:\Windows", "E:");
        Assert.Throws<InvalidOperationException>(() => ProgramDataPath.RequireSystemDriveAgreement(@"E:\Windows", "Z:"));
    }

    private static Encoding[] BomEncodings() =>
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
        new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
        new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true)
    ];

    private static byte[] EncodeWithPreamble(string text, Encoding encoding) =>
        encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

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

    private static ServiceProvider KeyProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        PersistentKeys.Configure(services, root);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.GetFiles(root)
        .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertSnapshot(Dictionary<string, byte[]> expected, string root)
    {
        var actual = Snapshot(root);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var file in expected) { CollectionAssert.AreEqual(file.Value, actual[file.Key]); }
    }

    [TestMethod]
    public void WindowsDpapiPersistsAndUnprotectsAcrossProvidersAndValidationDoesNotWrite()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows user-scope DPAPI qualification requires Windows; Linux settings are never weakened."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string cookie;
            ServiceProvider Provider()
            {
                var services = new ServiceCollection();
                services.AddLogging();
                if (OperatingSystem.IsWindows())
                { PersistentKeys.Configure(services, root); }
                return services.BuildServiceProvider();
            }
            using (var first = Provider())
            { PersistentKeys.VerifyUsable(first, root, AppContext.BaseDirectory); cookie = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("cookie-test").Protect("secret-cookie"); }
            var before = Directory.GetFiles(root).ToDictionary(p => p, File.ReadAllBytes);
            using var config = Config(root);
            Assert.AreEqual("test-001", DeploymentContract.ValidateConfiguration(config.Path, root, AppContext.BaseDirectory));
            var result = DeploymentContract.ValidateCore(["--validate-deployment", "--configuration-path", config.Path,
                "--data-protection-root", root, "--environment", "Production"], Identity, config.Path);
            Assert.IsTrue(result.Valid);
            Assert.AreEqual("test-001", result.ConfigurationRevision);
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), Directory.GetFiles(root));
            foreach (var file in before) { CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key)); }
            using (var second = Provider())
            { Assert.AreEqual("secret-cookie", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("cookie-test").Unprotect(cookie)); }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WindowsApplicationCompositionUnprotectsAfterFreshProcessRestart()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows DPAPI fresh-process test requires Windows."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var config = Config(root);
            var token = Path.Combine(Path.GetDirectoryName(config.Path)!, "token.txt");
            var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var probe = Path.Combine(repository, $"tests/SsrsPortal.BrowserChecks/bin/{buildConfiguration}/net10.0/SsrsPortal.BrowserChecks.dll");
            foreach (var action in new[] { "protect", "unprotect" })
            {
                var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var argument in new[] { probe, "--dpapi-probe", action, config.Path, token }) { start.ArgumentList.Add(argument); }
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch { process.Kill(true); throw; }
                Assert.AreEqual(0, process.ExitCode, "Isolated DPAPI child failed: " + await error + await output);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnmarkedDpapiRingsAreRefusedWithoutChangingKeys(bool machineScope)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows DPAPI required."); }
        var root = Path.Combine(Path.GetTempPath(), "ssrs-incompatible-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            if (OperatingSystem.IsWindows())
            { services.AddDataProtection().PersistKeysToFileSystem(new(root)).ProtectKeysWithDpapi(machineScope); }
            using (var provider = services.BuildServiceProvider())
            { _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("synthetic"); }
            var before = Directory.GetFiles(root).ToDictionary(p => p, File.ReadAllBytes);
            Assert.Throws<InvalidOperationException>(() => PersistentKeys.ValidateDirectory(root, AppContext.BaseDirectory));
            foreach (var file in before) { CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key)); }
        }
        finally { Directory.Delete(root, true); }
    }
}
