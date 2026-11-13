using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Clc.Deployment.AspNetCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Configuration;
using ValidationResponse = Clc.Deployment.AspNetCore.DeploymentValidationResponse;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
[DoNotParallelize]
public sealed class ProgramDeploymentIntegrationTests
{
    private const string SyntheticReleaseSourceCommit = "1111111111111111111111111111111111111111";

    [TestMethod]
    public async Task PublishedReleaseEntryPointValidatesAndServesAnonymousConventionalReadiness()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("The actual Program acceptance test requires Windows DPAPI and a Windows Kestrel host."); }

        var repository = RepositoryRoot();
        var artifacts = Path.Combine(repository, "artifacts", "program-release-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var releaseId = "integration-test-" + Guid.NewGuid().ToString("N");
        var syntheticIdentity = PrepareSyntheticReleaseIdentity(releaseId, repository);
        var sourceCommit = syntheticIdentity.SourceCommit;
        var publish = Path.Combine(artifacts, "publish");
        var publishResult = await RunProcess("dotnet", repository,
            ["publish", Path.Combine(repository, "src", "SsrsPortal", "SsrsPortal.csproj"), "--configuration", "Release",
                "--artifacts-path", Path.Combine(artifacts, "build"), "--output", publish,
                "-p:ClcIdentityKind=Release", "-p:ClcReleaseId=" + syntheticIdentity.ReleaseId,
                "-p:ClcSourceCommit=" + syntheticIdentity.SourceCommit],
            timeout: TimeSpan.FromMinutes(2), preserveEnvironment: true);
        Assert.AreEqual(0, publishResult.ExitCode, publishResult.StandardOutput + publishResult.StandardError);
        var publishedExample = Path.Combine(publish, "application.Example.json");
        Assert.IsTrue(File.Exists(publishedExample), "The first-run example must be present in the release publish.");
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(repository, "src", "SsrsPortal", "application.Example.json")),
            File.ReadAllBytes(publishedExample));
        Assert.IsFalse(File.Exists(Path.Combine(publish, "application.json")), "Operational configuration must stay outside publish output.");
        var publishedIdentity = ReadPublishedIdentity(Path.Combine(publish, "SsrsPortal.dll"));
        Assert.AreEqual("ssrs-web", publishedIdentity.ApplicationId);
        Assert.AreEqual(syntheticIdentity.ReleaseId, publishedIdentity.ReleaseId);
        Assert.AreEqual(sourceCommit, publishedIdentity.SourceCommit);
        Assert.AreEqual("Release", publishedIdentity.Kind);
        Assert.IsTrue(publishedIdentity.IsRelease);

        var programData = ProgramDataPath.Root;
        Assert.IsTrue(Path.IsPathFullyQualified(programData), "CommonApplicationData must resolve to a fully qualified Windows path.");
        Assert.AreEqual(programData, InToolkitMinimalEnvironment(() => ProgramDataPath.Root),
            "The OS-known ProgramData path must stay identical when toolkit-only environment variables are absent.");
        LocalFileSystemPolicy.RequireLocalPhysicalPath(programData, "Synthetic ProgramData test parent");
        var conventionalRoot = Path.Combine(programData, "clc-ssrs-web");
        var stagingRoot = "";
        var ownsStaging = false;
        var ownsRoot = false;
        var markerName = "codex-test-owner-" + Guid.NewGuid().ToString("N") + ".txt";
        var markerValue = "synthetic-release-readiness-test:" + Guid.NewGuid().ToString("N");
        var process = (Process?)null;
        string? root = null;
        try
        {
            stagingRoot = CreateOwnedEmptySibling(programData);
            ownsStaging = true;
            Assert.AreEqual(0, Directory.GetFileSystemEntries(stagingRoot).Length, "The unique sibling must be empty before atomic ownership transfer.");
            try
            {
                // Directory.Move is an atomic same-volume create-new operation: it fails if
                // an operational namespace already exists and never inspects its contents.
                Directory.Move(stagingRoot, conventionalRoot);
                ownsStaging = false;
                ownsRoot = true;
                root = conventionalRoot;
            }
            catch (IOException)
            {
                Assert.Inconclusive("Skipped actual Program acceptance: the conventional ProgramData namespace already exists and was left untouched.");
            }

            var marker = Path.Combine(root!, markerName);
            WriteCreateNew(marker, markerValue);
            var configDirectory = Path.Combine(root!, "Config");
            var keyDirectory = Path.Combine(root!, "Keys");
            var logDirectory = Path.Combine(root!, "Logs");
            var configPath = Path.Combine(configDirectory, "application.json");
            var discovery = await RunPublishedArguments(publish, ["--clc-appdata-directory"]);
            Assert.AreEqual(0, discovery.ExitCode);
            Assert.AreEqual(root! + Environment.NewLine, discovery.StandardOutput);
            Assert.AreEqual("", discovery.StandardError);
            Assert.IsFalse(Directory.Exists(configDirectory), "Path discovery must not bootstrap configuration.");
            Assert.IsFalse(Directory.Exists(logDirectory), "Path discovery must not initialize logging.");

            var missingValidation = await RunValidator(publish, configPath, keyDirectory);
            Assert.IsFalse(missingValidation.Valid, "Read-only preflight must reject missing configuration.");
            CollectionAssert.Contains(missingValidation.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
            Assert.IsFalse(Directory.Exists(configDirectory), "Read-only preflight must not bootstrap configuration.");
            Assert.IsFalse(Directory.Exists(keyDirectory), "Read-only preflight must not create keys.");

            var hiddenExample = Path.Combine(artifacts, "hidden-example.json");
            File.Move(publishedExample, hiddenExample);
            try
            {
                var noExample = await RunPublishedArguments(publish, []);
                Assert.AreNotEqual(0, noExample.ExitCode);
                StringAssert.Contains(noExample.StandardError, "Configuration bootstrap example file is missing");
                Assert.IsFalse(Directory.Exists(configDirectory), "A missing example must not leave a configuration file.");
            }
            finally { File.Move(hiddenExample, publishedExample); }

            var firstRun = await RunPublishedArguments(publish, []);
            Assert.AreNotEqual(0, firstRun.ExitCode);
            StringAssert.Contains(firstRun.StandardError, "Created external application configuration from the example");
            CollectionAssert.AreEqual(File.ReadAllBytes(publishedExample), File.ReadAllBytes(configPath), "Bootstrap must copy exact example bytes.");
            File.WriteAllText(configPath, File.ReadAllText(configPath).Replace(
                "\"ClientSecret\": \"REPLACE_ME\"", "\"ClientSecret\": \"SECRET_BOOTSTRAP_SENTINEL\"", StringComparison.Ordinal));
            Assert.IsTrue(File.ReadAllText(configPath).Contains("SECRET_BOOTSTRAP_SENTINEL", StringComparison.Ordinal));
            var starterBytes = File.ReadAllBytes(configPath);
            var placeholders = await RunPublishedArguments(publish, []);
            Assert.AreNotEqual(0, placeholders.ExitCode);
            StringAssert.Contains(placeholders.StandardError, "placeholder REPLACE_ME");
            Assert.IsFalse((placeholders.StandardOutput + placeholders.StandardError).Contains("SECRET_BOOTSTRAP_SENTINEL", StringComparison.Ordinal));
            CollectionAssert.AreEqual(starterBytes, File.ReadAllBytes(configPath), "Rejected placeholders must not rewrite the starter file.");
            foreach (var file in Directory.GetFiles(logDirectory))
            {
                Assert.IsTrue(Regex.IsMatch(Path.GetFileName(file), @"^ssrs-web-\d{4}-\d{2}-\d{2}\.log$"));
                Assert.IsFalse(File.ReadAllText(file).Contains("SECRET_BOOTSTRAP_SENTINEL", StringComparison.Ordinal));
                File.Delete(file);
            }
            Directory.Delete(logDirectory);

            Directory.CreateDirectory(keyDirectory);
            var configuration = AcceptanceConfiguration(keyDirectory);
            var configurationText = configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var configurationBytes = Encoding.UTF8.GetBytes(configurationText);
            File.WriteAllText(configPath, configurationText);
            LocalFileSystemPolicy.RequireLocalPhysicalPath(configPath, "Synthetic conventional configuration");
            LocalFileSystemPolicy.RequireLocalPhysicalPath(keyDirectory, "Synthetic persistent key ring");

            var directValidation = InvokePublishedValidation(Path.Combine(publish, "SsrsPortal.dll"), configPath, keyDirectory);
            Assert.IsTrue(directValidation.Valid, "Published in-process validation errors: " + string.Join(',', directValidation.ErrorCodes));
            Assert.IsTrue(directValidation.RunValid, "Published Run returned: " + directValidation.RunReleaseId);
            var toolkitEnvironmentValidation = InToolkitMinimalEnvironment(() =>
                InvokePublishedValidation(Path.Combine(publish, "SsrsPortal.dll"), configPath, keyDirectory));
            Assert.IsTrue(toolkitEnvironmentValidation.Valid, "Minimal-environment direct validation errors: " + string.Join(',', toolkitEnvironmentValidation.ErrorCodes));
            Assert.IsTrue(toolkitEnvironmentValidation.RunValid, "Minimal-environment Run returned: " + toolkitEnvironmentValidation.RunReleaseId);

            var emptyKeys = FileInventory(keyDirectory);
            var success = await RunValidator(publish, configPath, keyDirectory);
            AssertValidation(success, valid: true, releaseId, sourceCommit, "integration-001");
            AssertInventory(emptyKeys, keyDirectory);
            Assert.IsFalse(Directory.Exists(logDirectory), "Validate-only must not initialize file logging.");

            foreach (var encoding in BomEncodings())
            {
                var encoded = EncodeWithPreamble(configurationText, encoding);
                File.WriteAllBytes(configPath, encoded);
                var bomValidation = await RunValidator(publish, configPath, keyDirectory);
                AssertValidation(bomValidation, valid: true, releaseId, sourceCommit, "integration-001");
                CollectionAssert.AreEqual(encoded, File.ReadAllBytes(configPath), encoding.WebName + " validation must leave source bytes unchanged.");
                AssertInventory(emptyKeys, keyDirectory);
                Assert.IsFalse(Directory.Exists(logDirectory), "Validate-only encoding detection must not initialize logging.");
            }
            var malformedUtf8 = EncodeWithPreamble(configurationText, new UTF8Encoding(true, true)).Append((byte)0xff).ToArray();
            File.WriteAllBytes(configPath, malformedUtf8);
            var malformedEncodingValidation = await RunValidator(publish, configPath, keyDirectory);
            AssertValidation(malformedEncodingValidation, valid: false, releaseId, sourceCommit, "unavailable");
            CollectionAssert.Contains(malformedEncodingValidation.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
            CollectionAssert.AreEqual(malformedUtf8, File.ReadAllBytes(configPath));
            AssertInventory(emptyKeys, keyDirectory);
            Assert.IsFalse(Directory.Exists(logDirectory), "Malformed validate-only encoding must not initialize logging.");
            File.WriteAllBytes(configPath, configurationBytes);

            foreach (var validationIntent in new[] { "--Validate-Deployment", "--validate-deployment=unexpected" })
            {
                var intentResult = await RunPublishedArguments(publish, [validationIntent]);
                Assert.AreEqual(1, intentResult.ExitCode);
                Assert.AreEqual("", intentResult.StandardError);
                Assert.IsLessThan(8192, Encoding.UTF8.GetByteCount(intentResult.StandardOutput));
                using var intentJson = JsonDocument.Parse(intentResult.StandardOutput);
                var intent = JsonSerializer.Deserialize<ValidationResponse>(intentJson.RootElement.GetRawText(), DeploymentContract.Json)!;
                Assert.IsFalse(intent.Valid);
                CollectionAssert.Contains(intent.ErrorCodes, "InvalidArguments");
            }
            AssertInventory(emptyKeys, keyDirectory);
            Assert.IsFalse(Directory.Exists(logDirectory), "Malformed validation-intent variants must not start the web host.");

            var spoofedProgramData = Path.Combine(artifacts, "spoofed-programdata");
            var spoofedPathValidation = await RunValidator(publish, configPath, keyDirectory, spoofedProgramData);
            AssertValidation(spoofedPathValidation, valid: true, releaseId, sourceCommit, "integration-001");
            Assert.IsFalse(Directory.Exists(spoofedProgramData), "ProgramData environment values cannot redirect the conventional path.");
            AssertInventory(emptyKeys, keyDirectory);

            var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory);
            Assert.IsTrue(systemDriveRoot is { Length: >= 2 } && char.IsAsciiLetter(systemDriveRoot[0]) && systemDriveRoot[1] == ':');
            var spoofedSystemDrive = string.Equals(systemDriveRoot[..2], "Z:", StringComparison.OrdinalIgnoreCase) ? "Y:" : "Z:";
            var systemDriveValidation = await RunValidator(publish, configPath, keyDirectory, systemDriveOverride: spoofedSystemDrive);
            Assert.IsFalse(systemDriveValidation.Valid);
            CollectionAssert.Contains(systemDriveValidation.ErrorCodes, "InvalidDeploymentInput");
            AssertInventory(emptyKeys, keyDirectory);
            Assert.IsFalse(Directory.Exists(logDirectory), "A spoofed SystemDrive cannot redirect configuration validation or logging.");
            var rejectedAddress = ReserveLoopbackAddress();
            using (var rejectedHost = StartPublishedProgram(publish, rejectedAddress, spoofedSystemDrive))
            {
                try
                {
                    Assert.IsTrue(rejectedHost.WaitForExit(10000), "A web process with a forged SystemDrive must fail closed promptly.");
                    Assert.AreNotEqual(0, rejectedHost.ExitCode);
                }
                finally { if (!rejectedHost.HasExited) { StopPublishedProgram(rejectedHost); } }
            }
            Assert.IsFalse(Directory.Exists(logDirectory), "A rejected forged SystemDrive must not create logging output.");
            AssertInventory(emptyKeys, keyDirectory);

            var invalidConfiguration = configuration.DeepClone().AsObject();
            invalidConfiguration["AllowedHosts"] = false;
            File.WriteAllText(configPath, invalidConfiguration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var failure = await RunValidator(publish, configPath, keyDirectory);
            AssertValidation(failure, valid: false, releaseId, sourceCommit, "unavailable");
            CollectionAssert.Contains(failure.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
            AssertInventory(emptyKeys, keyDirectory);
            Assert.IsFalse(Directory.Exists(logDirectory), "Rejected validate-only inputs must not create logs.");

            using (var sqlSink = new TcpListener(IPAddress.Loopback, 0))
            {
                sqlSink.Start();
                var sqlPort = ((IPEndPoint)sqlSink.LocalEndpoint).Port;
                var enabledConfiguration = configuration.DeepClone().AsObject();
                enabledConfiguration["CatalogMonitor"]!["Enabled"] = true;
                enabledConfiguration["CatalogMonitor"]!["ConnectionString"] =
                    $"Server=127.0.0.1,{sqlPort};Database=ReportServer;Integrated Security=true;Encrypt=true;TrustServerCertificate=false;Connect Timeout=1";
                enabledConfiguration["Subscriptions"]!["Enabled"] = true;
                enabledConfiguration["Subscriptions"]!["ConnectionString"] =
                    $"Server=127.0.0.1,{sqlPort};Database=SsrsWeb;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=1";
                enabledConfiguration["Subscriptions"]!["PostmarkServerToken"] = "synthetic-validator-token";
                enabledConfiguration["Subscriptions"]!["PortalBaseUrl"] = "https://reports.example.invalid";
                enabledConfiguration["Subscriptions"]!["From"] = "reports@example.invalid";
                enabledConfiguration["Subscriptions"]!["MessageStream"] = "outbound";
                File.WriteAllText(configPath, enabledConfiguration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                var enabledValidation = await RunValidator(publish, configPath, keyDirectory);
                AssertValidation(enabledValidation, valid: true, releaseId, sourceCommit, "integration-001");
                Assert.IsFalse(sqlSink.Pending(), "Validate-only must not establish SQL connections for enabled options.");
                AssertInventory(emptyKeys, keyDirectory);
                Assert.IsFalse(Directory.Exists(logDirectory), "Validate-only with enabled options must not initialize logging.");
            }

            File.WriteAllBytes(configPath, configurationBytes);
            var address = ReserveLoopbackAddress();
            process = StartPublishedProgram(publish, address);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(2) };
            var readiness = await WaitForReadiness(client, process, address, TimeSpan.FromSeconds(30));
            AssertReadiness(readiness, success, "integration-001");
            Assert.IsTrue(Directory.Exists(logDirectory), "The actual host should initialize its conventional Logs directory.");
            Assert.IsTrue(Directory.GetFiles(keyDirectory, "key-*.xml").Length > 0, "The runtime identity should persist a key at startup.");

            string protectedCookie;
            var keysBeforeCookieProbe = FileInventory(keyDirectory);
            using (var keyServices = CreateDataProtectionServices(keyDirectory))
            {
                var protector = keyServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("future-version-probe");
                protectedCookie = protector.Protect("valid-current-ciphertext");
                Assert.AreEqual("valid-current-ciphertext", protector.Unprotect(protectedCookie));
            }
            AssertInventory(keysBeforeCookieProbe, keyDirectory);

            var retainedKeys = FileInventory(keyDirectory);
            var retainedConfiguration = File.ReadAllBytes(configPath);
            StopPublishedProgram(process);
            process = null;
            AssertInventory(retainedKeys, keyDirectory);
            CollectionAssert.AreEqual(retainedConfiguration, File.ReadAllBytes(configPath));

            var retainedValidation = await RunValidator(publish, configPath, keyDirectory);
            AssertValidation(retainedValidation, valid: true, releaseId, sourceCommit, "integration-001");
            AssertInventory(retainedKeys, keyDirectory);

            var originalKeyFiles = FileInventory(keyDirectory);
            foreach (var keyPath in originalKeyFiles.Keys.Where(path => Path.GetFileName(path).StartsWith("key-", StringComparison.OrdinalIgnoreCase)))
            { SetFutureAppDecryptorVersion(keyPath); }
            var futureVersionKeys = FileInventory(keyDirectory);
            var logsBeforeFutureVersion = FileInventory(logDirectory);
            try
            {
                var futureVersionValidation = await RunValidator(publish, configPath, keyDirectory);
                AssertValidation(futureVersionValidation, valid: false, releaseId, sourceCommit, "unavailable");
                CollectionAssert.Contains(futureVersionValidation.ErrorCodes, "ConfigurationOrKeyStoreInvalid");
                AssertInventory(futureVersionKeys, keyDirectory);
                AssertInventory(logsBeforeFutureVersion, logDirectory);

                var rejectedRuntimeAddress = ReserveLoopbackAddress();
                using (var rejectedRuntime = StartPublishedProgram(publish, rejectedRuntimeAddress))
                {
                    Assert.IsTrue(rejectedRuntime.WaitForExit(10000), "The fresh Release process must reject an unbindable retained decryptor before hosting or protecting.");
                    Assert.AreNotEqual(0, rejectedRuntime.ExitCode);
                }
                AssertInventory(futureVersionKeys, keyDirectory);
                CollectionAssert.AreEqual(retainedConfiguration, File.ReadAllBytes(configPath), "Rejected startup must preserve the conventional configuration bytes.");
                AssertStartupLogsDoNotExposeRetainedKeyMetadata(logDirectory);
            }
            finally
            {
                foreach (var original in originalKeyFiles) { File.WriteAllBytes(original.Key, original.Value); }
            }
            AssertInventory(retainedKeys, keyDirectory);

            var secondAddress = ReserveLoopbackAddress();
            process = StartPublishedProgram(publish, secondAddress);
            var restartedReadiness = await WaitForReadiness(client, process, secondAddress, TimeSpan.FromSeconds(30));
            AssertReadiness(restartedReadiness, retainedValidation, "integration-001");
            StopPublishedProgram(process);
            process = null;
            AssertInventory(retainedKeys, keyDirectory);
            CollectionAssert.AreEqual(retainedConfiguration, File.ReadAllBytes(configPath));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(10000); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
            if (ownsStaging && Directory.Exists(stagingRoot))
            {
                if (Directory.GetFileSystemEntries(stagingRoot).Length == 0) { Directory.Delete(stagingRoot); }
            }
            if (ownsRoot && root is not null) { CleanupOwnedProgramData(root, markerName, markerValue); }
        }
    }

    [TestMethod]
    public async Task SyntheticReleaseIdentityDoesNotReadOrMutateDirtyGitState()
    {
        var artifacts = Path.Combine(RepositoryRoot(), "artifacts", "synthetic-identity-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var repository = Path.Combine(artifacts, "controlled-repository");
        Directory.CreateDirectory(repository);
        var poisonedWorkspace = Path.Combine(artifacts, "poisoned-parent-git-state");
        var poisonedGitDirectory = Path.Combine(poisonedWorkspace, ".git");
        var poisonedObjects = Path.Combine(poisonedGitDirectory, "objects");
        Directory.CreateDirectory(Path.Combine(poisonedObjects, "aa"));
        var poisonedIndex = Path.Combine(poisonedGitDirectory, "index");
        var poisonedIndexBytes = Encoding.UTF8.GetBytes("synthetic poisoned parent index");
        var poisonedObjectPath = Path.Combine(poisonedObjects, "aa", "synthetic-object-sentinel");
        var poisonedObjectBytes = Encoding.UTF8.GetBytes("synthetic poisoned parent object directory");
        File.WriteAllBytes(poisonedIndex, poisonedIndexBytes);
        File.WriteAllBytes(poisonedObjectPath, poisonedObjectBytes);
        var oldGitEnvironment = new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY", "GIT_INDEX_FILE", "GIT_ALTERNATE_OBJECT_DIRECTORIES" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", poisonedGitDirectory);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", poisonedWorkspace);
            Environment.SetEnvironmentVariable("GIT_OBJECT_DIRECTORY", poisonedObjects);
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", poisonedIndex);
            Environment.SetEnvironmentVariable("GIT_ALTERNATE_OBJECT_DIRECTORIES", poisonedObjects);
            await RunSyntheticGit(repository, artifacts, "init", "--quiet");
            await RunSyntheticGit(repository, artifacts, "-c", "user.name=Synthetic test", "-c", "user.email=test@example.invalid",
                "commit", "--allow-empty", "--quiet", "-m", "Synthetic controlled baseline");
            var sentinel = Path.Combine(repository, "untracked-sentinel.txt");
            var sentinelContents = Encoding.UTF8.GetBytes("Synthetic untracked content; contains no operational values.");
            File.WriteAllBytes(sentinel, sentinelContents);

            var before = GitStateSnapshot(repository);
            var syntheticIdentity = PrepareSyntheticReleaseIdentity("integration-test-regression", repository);
            Assert.AreEqual(SyntheticReleaseSourceCommit, syntheticIdentity.SourceCommit);
            Assert.AreEqual(40, syntheticIdentity.SourceCommit.Length);
            Assert.IsTrue(syntheticIdentity.SourceCommit.All(character => character == '1'));
            var identityAssembly = CreateIdentityAssembly("ssrs-web", syntheticIdentity.ReleaseId, syntheticIdentity.SourceCommit, "Release");
            Assert.IsTrue(ClcApplicationIdentity.ReadFromAssembly(identityAssembly, "ssrs-web").IsRelease);
            Assert.IsFalse(Encoding.UTF8.GetString(sentinelContents).Contains(syntheticIdentity.SourceCommit, StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(sentinel), "The fixture sentinel remains untracked in its isolated test repository.");
            Assert.IsTrue(File.ReadAllBytes(Path.Combine(repository, ".git", "index")).Length > 0);
            AssertGitState(before, repository);
            CollectionAssert.AreEqual(sentinelContents, File.ReadAllBytes(sentinel));
            CollectionAssert.AreEqual(poisonedIndexBytes, File.ReadAllBytes(poisonedIndex));
            CollectionAssert.AreEqual(poisonedObjectBytes, File.ReadAllBytes(poisonedObjectPath));
        }
        finally
        {
            foreach (var variable in oldGitEnvironment)
            { Environment.SetEnvironmentVariable(variable.Key, variable.Value); }
            foreach (var file in Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories))
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                { throw new InvalidOperationException("Reparse point found in the owned synthetic Git fixture; cleanup stopped."); }
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(artifacts, recursive: true);
        }
    }

    private static JsonObject AcceptanceConfiguration(string keyDirectory) => new()
    {
        ["AllowedHosts"] = "localhost;127.0.0.1",
        ["Deployment"] = new JsonObject { ["ConfigurationRevision"] = "integration-001" },
        ["DataProtection"] = new JsonObject
        { ["KeyStoreCompatibilityId"] = PersistentKeyOptions.CompatibilityId, ["KeyRingPath"] = keyDirectory },
        ["Portal"] = new JsonObject
        {
            ["UseMock"] = false, ["TestAuth"] = false,
            ["ReportServer"] = "https://127.0.0.1:1/ReportServer", ["ReportAccessPolicy"] = "GlobalBlocklist",
            ["AllowedRoots"] = new JsonArray("/Synthetic"), ["ExcludedPrefixes"] = new JsonArray()
        },
        ["Entra"] = new JsonObject
        {
            ["ClientId"] = Guid.NewGuid().ToString("D"), ["ClientSecret"] = "synthetic-unused-secret",
            ["AllowedTenantIds"] = new JsonArray("00000000-0000-0000-0000-000000000123")
        },
        ["CatalogMonitor"] = new JsonObject { ["Enabled"] = false, ["ConnectionString"] = null },
        ["Subscriptions"] = new JsonObject
        {
            ["Enabled"] = false, ["ConnectionString"] = null, ["PostmarkServerToken"] = null,
            ["PortalBaseUrl"] = null, ["From"] = null, ["MessageStream"] = null
        },
        ["Logging"] = new JsonObject
        {
            ["File"] = new JsonObject { ["Enabled"] = true, ["Rotation"] = "Daily", ["MaxFileSizeBytes"] = 52428800, ["RetentionDays"] = 30 },
            ["LogLevel"] = new JsonObject { ["Default"] = "Information", ["Microsoft"] = "Warning", ["Microsoft.AspNetCore"] = "Warning" }
        }
    };

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

    private static ServiceProvider CreateDataProtectionServices(string keyDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        PersistentKeys.Configure(services, keyDirectory);
        return services.BuildServiceProvider();
    }

    private static void SetFutureAppDecryptorVersion(string keyPath)
    {
        var document = XDocument.Load(keyPath);
        var encrypted = document.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single();
        var parts = ((string)encrypted.Attribute("decryptorType")!).Split(',');
        var versionIndex = Array.FindIndex(parts, part => part.TrimStart().StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
        if (versionIndex < 0) { throw new InvalidDataException("Generated app decryptor identity omitted its assembly version."); }
        parts[versionIndex] = " Version=999.0.0.0";
        encrypted.SetAttributeValue("decryptorType", string.Join(',', parts));
        document.Save(keyPath);
    }

    private static async Task<ValidationResponse> RunValidator(string publish, string configPath, string keyDirectory,
        string? programDataOverride = null, string? systemDriveOverride = null)
    {
        var start = NewProcess(DotnetHost(), publish, preserveEnvironment: false);
        foreach (var argument in new[] { Path.Combine(publish, "SsrsPortal.dll"), "--validate-deployment", "--configuration-path", configPath,
            "--data-protection-root", keyDirectory, "--environment", "Production" }) { start.ArgumentList.Add(argument); }
        if (programDataOverride is not null) { start.Environment["ProgramData"] = programDataOverride; }
        if (systemDriveOverride is not null) { start.Environment["SystemDrive"] = systemDriveOverride; }
        var result = await RunProcess(start, TimeSpan.FromSeconds(15));
        Assert.AreEqual("", result.StandardError, "The validation child must keep diagnostics off stderr.");
        Assert.IsLessThan(8192, Encoding.UTF8.GetByteCount(result.StandardOutput), "Validation stdout must be bounded to 8 KiB.");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var response = JsonSerializer.Deserialize<ValidationResponse>(document.RootElement.GetRawText(), DeploymentContract.Json)!;
        Assert.AreEqual(response.Valid ? 0 : 1, result.ExitCode, result.StandardOutput + result.StandardError);
        return response;
    }

    private static Task<ProcessResult> RunPublishedArguments(string publish, string[] arguments)
    {
        var start = NewProcess(DotnetHost(), publish, preserveEnvironment: false);
        start.ArgumentList.Add(Path.Combine(publish, "SsrsPortal.dll"));
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        return RunProcess(start, TimeSpan.FromSeconds(15));
    }

    private static void AssertValidation(ValidationResponse actual, bool valid, string releaseId, string sourceCommit, string revision)
    {
        Assert.AreEqual(valid, actual.Valid,
            $"errors={string.Join(',', actual.ErrorCodes)}; identity={actual.ApplicationId}/{actual.ReleaseId}/{actual.SourceCommit}; runtime={actual.RuntimeEnvironment}; revision={actual.ConfigurationRevision}");
        Assert.AreEqual(1, actual.ValidationContractVersion);
        Assert.AreEqual("ssrs-web", actual.ApplicationId);
        Assert.AreEqual(releaseId, actual.ReleaseId);
        Assert.AreEqual(sourceCommit, actual.SourceCommit);
        Assert.AreEqual("Production", actual.RuntimeEnvironment);
        Assert.AreEqual(revision, actual.ConfigurationRevision);
        Assert.AreEqual(PersistentKeyOptions.CompatibilityId, actual.KeyStoreCompatibilityId);
        if (valid) { Assert.AreEqual(0, actual.ErrorCodes.Length); }
    }

    private static Process StartPublishedProgram(string publish, string address, string? systemDriveOverride = null)
    {
        var start = NewProcess(DotnetHost(), publish, preserveEnvironment: false);
        start.ArgumentList.Add(Path.Combine(publish, "SsrsPortal.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(address);
        if (systemDriveOverride is not null) { start.Environment["SystemDrive"] = systemDriveOverride; }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Published web process did not start.");
        _ = DrainBounded(process.StandardOutput, 32768);
        _ = DrainBounded(process.StandardError, 32768);
        return process;
    }

    private static async Task<JsonDocument> WaitForReadiness(HttpClient client, Process process, string address, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        Exception? last = null;
        while (!deadline.IsCancellationRequested)
        {
            if (process.HasExited) { throw new InvalidOperationException("Published Program exited before readiness was served."); }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/health/ready");
                request.Headers.TryAddWithoutValidation("Cookie", "__Host-Portal=invalid");
                using var response = await client.SendAsync(request, deadline.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Assert.IsLessThan(4096, bytes.Length, "Readiness response must be bounded to 4 KiB.");
                    Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
                    Assert.IsTrue(response.Headers.CacheControl?.NoStore);
                    Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
                    Assert.IsNull(response.Headers.Location);
                    Assert.AreEqual(0, response.Headers.WwwAuthenticate.Count);
                    return JsonDocument.Parse(bytes);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { last = exception; }
            await Task.Delay(200, deadline.Token);
        }
        throw new TimeoutException("Published Program did not become ready within the bounded loopback deadline.", last);
    }

    private static void AssertReadiness(JsonDocument readiness, ValidationResponse validation, string revision)
    {
        using (readiness)
        {
            var root = readiness.RootElement;
            CollectionAssert.AreEquivalent(new[] { "readinessContractVersion", "status", "applicationId", "releaseId", "sourceCommit",
                "runtimeEnvironment", "configurationRevision" }, root.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual(1, root.GetProperty("readinessContractVersion").GetInt32());
            Assert.AreEqual("ready", root.GetProperty("status").GetString());
            Assert.AreEqual(validation.ApplicationId, root.GetProperty("applicationId").GetString());
            Assert.AreEqual(validation.ReleaseId, root.GetProperty("releaseId").GetString());
            Assert.AreEqual(validation.SourceCommit, root.GetProperty("sourceCommit").GetString());
            Assert.AreEqual(validation.RuntimeEnvironment, root.GetProperty("runtimeEnvironment").GetString());
            Assert.AreEqual(revision, root.GetProperty("configurationRevision").GetString());
        }
    }

    private static string ReserveLoopbackAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateOwnedEmptySibling(string parent)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var path = Path.Combine(parent, "clc-ssrs-web-codex-test-" + Guid.NewGuid().ToString("N"));
            if (CreateDirectoryExclusive(path, IntPtr.Zero)) { return path; }
            var error = Marshal.GetLastWin32Error();
            if (error != 183) { throw new Win32Exception(error, "Could not create the private empty ProgramData sibling."); }
        }
        throw new IOException("Could not allocate a unique ProgramData sibling after five attempts.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(string path, IntPtr securityAttributes);

    private static void StopPublishedProgram(Process process)
    {
        if (process.HasExited) { return; }
        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(10000)) { throw new TimeoutException("Published Program did not stop within the bounded cleanup deadline."); }
    }

    private static Dictionary<string, byte[]> FileInventory(string directory) => Directory.GetFiles(directory)
        .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertInventory(Dictionary<string, byte[]> expected, string directory)
    {
        var actual = FileInventory(directory);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var file in expected) { CollectionAssert.AreEqual(file.Value, actual[file.Key]); }
    }

    private static void AssertStartupLogsDoNotExposeRetainedKeyMetadata(string logDirectory)
    {
        foreach (var path in Directory.GetFiles(logDirectory, "ssrs-web-*.log"))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.IsFalse(text.Contains("decryptorType", StringComparison.OrdinalIgnoreCase), "Startup logs must not disclose retained key XML metadata.");
            Assert.IsFalse(text.Contains("999.0.0.0", StringComparison.Ordinal), "Startup logs must not disclose a rejected assembly identity.");
        }
    }

    private static void WriteCreateNew(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value);
    }

    private static void CleanupOwnedProgramData(string root, string markerName, string markerValue)
    {
        var marker = Path.Combine(root, markerName);
        if (!File.Exists(marker) || File.ReadAllText(marker) != markerValue)
        { throw new InvalidOperationException("Synthetic ProgramData ownership marker is missing or changed; cleanup stopped."); }

        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { root, Path.Combine(root, "Config"), Path.Combine(root, "Keys"), Path.Combine(root, "Logs") };
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (!expectedDirectories.Contains(directory) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("Unexpected or reparse-point directory found in synthetic ProgramData; cleanup stopped."); }
        }

        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("Reparse-point file found in synthetic ProgramData; cleanup stopped."); }
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var known = relative.Equals(markerName, StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("Config/application.json", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(relative, @"\AKeys/key-[a-f0-9-]{36}\.xml\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                Regex.IsMatch(relative, @"\ALogs/ssrs-web-[0-9]{4}-[0-9]{2}-[0-9]{2}\.log\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!known) { throw new InvalidOperationException("Unexpected file found in synthetic ProgramData; cleanup stopped."); }
        }

        foreach (var file in files) { File.Delete(file); }
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        { Directory.Delete(directory, recursive: false); }
        Directory.Delete(root, recursive: false);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        { if (File.Exists(Path.Combine(directory.FullName, "SsrsPortal.slnx"))) { return directory.FullName; } }
        throw new InvalidOperationException("Repository root could not be located.");
    }

    private static PublishedIdentity ReadPublishedIdentity(string assemblyPath)
    {
        var loadContext = new PublishedAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var package = loadContext.LoadFromAssemblyName(new AssemblyName("Clc.Deployment.AspNetCore"));
            var type = package.GetType("Clc.Deployment.AspNetCore.ClcApplicationIdentity", throwOnError: true)!;
            var read = type.GetMethod("ReadFromAssembly", BindingFlags.Public | BindingFlags.Static)!;
            var identity = read.Invoke(null, [assembly, "ssrs-web"])!;
            string ReadString(string name) => (string)type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(identity)!;
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
            var isRelease = (bool)type.GetProperty("IsRelease", BindingFlags.Instance | BindingFlags.Public)!.GetValue(identity)!;
            return new(ReadString("ApplicationId"), ReadString("ReleaseId"), ReadString("SourceCommit"), metadata["Clc.IdentityKind"]!, isRelease);
        }
        finally { loadContext.Unload(); }
    }

    private static (bool Valid, string[] ErrorCodes, bool RunValid, string RunReleaseId) InvokePublishedValidation(string assemblyPath, string configPath, string keyDirectory)
    {
        var loadContext = new PublishedAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var contractType = assembly.GetType("SsrsPortal.Configuration.DeploymentContract", throwOnError: true)!;
            var validate = contractType.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
            var arguments = new[] { "--validate-deployment", "--configuration-path", configPath,
                "--data-protection-root", keyDirectory, "--environment", "Production" };
            object result;
            try { result = validate.Invoke(null, [arguments, assembly])!; }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException("Published direct validation threw " + exception.InnerException?.GetType().FullName +
                    " at " + exception.InnerException?.StackTrace);
            }
            var responseType = result.GetType();
            var valid = (bool)responseType.GetProperty("Valid")!.GetValue(result)!;
            var errors = (string[])responseType.GetProperty("ErrorCodes")!.GetValue(result)!;
            var run = contractType.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
            using var output = new StringWriter();
            _ = run.Invoke(null, [arguments, output]);
            using var runDocument = JsonDocument.Parse(output.ToString());
            var runValid = runDocument.RootElement.GetProperty("valid").GetBoolean();
            var runReleaseId = runDocument.RootElement.GetProperty("releaseId").GetString()!;
            return (valid, errors, runValid, runReleaseId);
        }
        finally { loadContext.Unload(); }
    }

    private static T InToolkitMinimalEnvironment<T>(Func<T> action)
    {
        var inherited = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => entry.Key.ToString()!, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in inherited.Keys) { Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process); }
            foreach (var name in new[] { "SystemRoot", "WINDIR" })
            {
                if (inherited.TryGetValue(name, out var value) && value is not null)
                { Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process); }
            }
            return action();
        }
        finally
        {
            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(key => key.ToString()!))
            { Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process); }
            foreach (var variable in inherited.Where(pair => pair.Value is not null))
            { Environment.SetEnvironmentVariable(variable.Key, variable.Value, EnvironmentVariableTarget.Process); }
        }
    }

    private sealed record PublishedIdentity(string ApplicationId, string ReleaseId, string SourceCommit, string Kind, bool IsRelease);

    private sealed class PublishedAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;
        internal PublishedAssemblyLoadContext(string assemblyPath) : base("ssrs-published-identity-test", isCollectible: true)
        { resolver = new AssemblyDependencyResolver(assemblyPath); }
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private static async Task RunSyntheticGit(string repository, string settingsRoot, params string[] arguments)
    {
        var emptyConfiguration = Path.Combine(settingsRoot, "empty-git-config");
        var emptyTemplates = Path.Combine(settingsRoot, "empty-git-templates");
        var emptyHooks = Path.Combine(settingsRoot, "empty-git-hooks");
        if (!File.Exists(emptyConfiguration)) { using (File.Create(emptyConfiguration)) { } }
        Directory.CreateDirectory(emptyTemplates);
        Directory.CreateDirectory(emptyHooks);

        var start = NewProcess("git", repository, preserveEnvironment: false);
        start.Environment["GIT_CONFIG_GLOBAL"] = emptyConfiguration;
        start.Environment["GIT_CONFIG_SYSTEM"] = emptyConfiguration;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TEMPLATE_DIR"] = emptyTemplates;
        var allowedGitEnvironment = new HashSet<string>(["GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_TEMPLATE_DIR"],
            StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(start.Environment.Keys.Any(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) && !allowedGitEnvironment.Contains(name)),
            "Synthetic Git children must not inherit repository, index, or object directory overrides.");
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repository);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=" + emptyHooks);
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        var result = await RunProcess(start, TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
    }

    private static (string ReleaseId, string SourceCommit) PrepareSyntheticReleaseIdentity(string releaseId, string sourceRepository)
    {
        if (string.IsNullOrWhiteSpace(releaseId) || string.IsNullOrWhiteSpace(sourceRepository))
        { throw new ArgumentException("Synthetic acceptance identity inputs are required."); }
        return (releaseId, SyntheticReleaseSourceCommit);
    }

    private static Assembly CreateIdentityAssembly(string applicationId, string releaseId, string sourceCommit, string kind)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Clc.SyntheticIdentity." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        foreach (var pair in new[]
        {
            new KeyValuePair<string, string>("Clc.ApplicationId", applicationId),
            new KeyValuePair<string, string>("Clc.ReleaseId", releaseId),
            new KeyValuePair<string, string>("Clc.SourceCommit", sourceCommit),
            new KeyValuePair<string, string>("Clc.IdentityKind", kind)
        })
        {
            var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [pair.Key, pair.Value]));
        }
        return assembly;
    }

    private static Dictionary<string, byte[]> GitStateSnapshot(string repository)
    {
        var gitDirectory = Path.Combine(repository, ".git");
        var objectDirectory = Path.Combine(gitDirectory, "objects");
        var paths = new[] { Path.Combine(gitDirectory, "index") }
            .Concat(Directory.GetFiles(objectDirectory, "*", SearchOption.AllDirectories));
        return paths.ToDictionary(path => Path.GetRelativePath(gitDirectory, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertGitState(Dictionary<string, byte[]> expected, string repository)
    {
        var actual = GitStateSnapshot(repository);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var file in expected) { CollectionAssert.AreEqual(file.Value, actual[file.Key]); }
    }

    private static Task<ProcessResult> RunProcess(string executable, string workingDirectory, string[] arguments,
        TimeSpan timeout, bool preserveEnvironment)
    {
        var start = NewProcess(executable, workingDirectory, preserveEnvironment);
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        return RunProcess(start, timeout);
    }

    private static async Task<ProcessResult> RunProcess(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child process did not start.");
        var stdout = DrainBounded(process.StandardOutput, 32768);
        var stderr = DrainBounded(process.StandardError, 32768);
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            throw;
        }
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static ProcessStartInfo NewProcess(string executable, string workingDirectory, bool preserveEnvironment)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!preserveEnvironment)
        {
            var inherited = new[] { "SystemRoot", "WINDIR" }.ToDictionary(key => key,
                key => Environment.GetEnvironmentVariable(key), StringComparer.OrdinalIgnoreCase);
            start.Environment.Clear();
            foreach (var variable in inherited.Where(pair => pair.Value is not null)) { start.Environment[variable.Key] = variable.Value!; }
        }
        return start;
    }

    private static string DotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (configured is not null && Path.IsPathFullyQualified(configured) && File.Exists(configured)) { return configured; }
        var roots = new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") };
        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var candidate = Path.Combine(root!, "dotnet.exe");
            if (File.Exists(candidate)) { return candidate; }
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry, "dotnet.exe");
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new FileNotFoundException("The installed dotnet.exe host could not be located for the isolated Windows acceptance child.");
    }

    private static async Task<string> DrainBounded(StreamReader reader, int maximumCharacters)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            var remaining = maximumCharacters - output.Length;
            if (remaining > 0) { output.Append(buffer, 0, Math.Min(remaining, count)); }
        }
        return output.ToString();
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
