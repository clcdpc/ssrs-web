using System.Text;
using System.Text.Json;
using Clc.Configuration;
using Microsoft.AspNetCore.Hosting;
using SsrsPortal.Core;
using SsrsPortal.Security;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.Configuration;

internal static class ExternalConfigurationLoader
{
    // Only the isolated Development test runners may select a temporary file.
    internal const string TestFileKey = "SsrsPortal:TestConfigurationFile";
    private const int MaximumJsonDepth = 32;

    private static readonly IReadOnlyDictionary<string, string?> LoggingDefaults = new Dictionary<string, string?>
    {
        ["DetailedErrors"] = "false",
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:SsrsPortal"] = "Debug",
        ["Logging:LogLevel:Microsoft"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore.Authentication"] = "Warning",
        ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning"
    };

    internal static string ConventionalPath => ProgramDataPath.Paths.ConfigurationFilePath;

    internal static void AddTo(WebApplicationBuilder builder)
    {
        RejectPreLoadSources(builder.Configuration, builder.Environment.IsDevelopment());
        var hasTestFile = HasTestFileSetting(builder.Configuration);
        string path;
        if (hasTestFile)
        {
            var testFile = builder.Configuration[TestFileKey];
            if (string.IsNullOrWhiteSpace(testFile) || !Path.IsPathFullyQualified(testFile))
            {
                throw new InvalidOperationException("The Development test-only configuration file override requires an absolute temporary file path.");
            }
            path = testFile;
            using var loaded = Load(path);
            ReplaceWithExternalSnapshot(builder.Configuration, loaded);
        }
        else
        {
            var paths = ProgramDataPath.Paths;
            path = paths.ConfigurationFilePath;
            RequireLocalPath(path);
            // Only normal startup may create the starter file. Keep the package's
            // path resolution pinned to the native, SystemDrive-checked ProgramData root.
            ProgramDataPath.WithVerifiedCommonApplicationData(() =>
                builder.AddClcConfigurationWithBootstrap(Program.ApplicationDirectoryName, "application.Example.json"));
            using var loaded = Load(path);
            ReplaceWithExternalSnapshot(builder.Configuration, loaded);
        }

        ValidateLoadedConfiguration(builder.Configuration);
    }

    internal static ConfigurationManager Load(string path)
    {
        RequireLocalPath(path);
        using var shapeSource = OpenShapeSource(path);
        using var snapshot = LoadSharedSnapshot(path);
        ValidateApplicationShape(shapeSource, path);

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(LoggingDefaults);
        configuration.AddInMemoryCollection(snapshot.AsEnumerable());
        ValidateLoadedConfiguration(configuration);
        return configuration;
    }

    private static ConfigurationRoot LoadSharedSnapshot(string path)
    {
        try
        {
            return (ConfigurationRoot)ClcConfigurationReader.LoadFromFile(Program.ApplicationDirectoryName, path);
        }
        catch (ClcConfigurationException exception)
        {
            throw Translate(exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw InvalidConfiguration(path);
        }
    }

    private static void ReplaceWithExternalSnapshot(ConfigurationManager configuration, IConfiguration loaded)
    {
        var hosting = new[]
        {
            WebHostDefaults.ServerUrlsKey,
            "http_ports",
            "https_ports",
            WebHostDefaults.PreferHostingUrlsKey
        }.ToDictionary(key => key, key => configuration[key]);

        configuration.Sources.Clear();
        configuration.AddInMemoryCollection(hosting);
        configuration.AddInMemoryCollection(loaded.AsEnumerable());
    }

    private static void ValidateLoadedConfiguration(IConfigurationRoot configuration)
    {
        ConfigurationContract.RejectLegacy(configuration);
        if (HasTestFileSetting(configuration))
        {
            throw new InvalidOperationException("The test-only configuration file override must not appear in application.json. Only isolated Development runners may supply it before configuration loads.");
        }
    }

    private static FileStream OpenShapeSource(string path)
    {
        try
        {
            // The shared reader uses the same read sharing mode. Holding this handle while it
            // loads prevents operational replacement during the application-specific shape pass.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException($"Required external configuration file is missing: '{path}'. Provision it from application.Example.json and grant the process identity read access.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"External configuration file cannot be read: '{path}'. Check file access for the process identity and restart the application.");
        }
    }

    private static void RequireLocalPath(string path)
    {
        try
        {
            LocalFileSystemPolicy.RequireLocalPhysicalPath(path, "External application configuration");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw InvalidConfiguration(path);
        }
    }

    private static void ValidateApplicationShape(Stream source, string path)
    {
        try
        {
            if (source.Length > 4 * 1024 * 1024) { throw new InvalidDataException(); }
            source.Position = 0;
            using var text = new StreamReader(source, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true,
                bufferSize: 8192, leaveOpen: true);
            using var document = JsonDocument.Parse(text.ReadToEnd(), new()
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = MaximumJsonDepth
            });
            ValidateApplicationShape(document.RootElement);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or JsonException or DecoderFallbackException or IOException or ArgumentException)
        {
            throw InvalidConfiguration(path);
        }
    }

    private static void ValidateApplicationShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
        var allowedHosts = root.EnumerateObject()
            .SingleOrDefault(property => property.Name.Equals("AllowedHosts", StringComparison.OrdinalIgnoreCase));
        if (allowedHosts.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(allowedHosts.Value.GetString()))
        {
            throw new InvalidDataException();
        }

        foreach (var section in new (string Name, Type Type)[]
        {
            ("Portal", typeof(PortalOptions)),
            ("Entra", typeof(EntraOptions)),
            ("CatalogMonitor", typeof(CatalogMonitorOptions)),
            ("Subscriptions", typeof(SubscriptionOptions)),
            ("Deployment", typeof(DeploymentOptions)),
            ("DataProtection", typeof(PersistentKeyOptions))
        })
        {
            var found = root.EnumerateObject()
                .Where(property => property.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (found.Length != 0)
            {
                // These two optional operational sections historically used framework binder
                // semantics plus their own validators; retain that behavior as-is.
                if (section.Name is "CatalogMonitor" or "Subscriptions") { continue; }
                if (found[0].Value.ValueKind == JsonValueKind.Null) { throw new InvalidDataException(); }
                CheckShape(found[0].Value, section.Type, section.Name);
            }
        }
    }

    private static void CheckShape(JsonElement element, Type type, string path)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (OptionalNullString(path)) { return; }
            throw new InvalidDataException();
        }
        if (type == typeof(string))
        {
            if (element.ValueKind != JsonValueKind.String) { throw new InvalidDataException(); }
        }
        else if (type.IsArray)
        {
            if (element.ValueKind != JsonValueKind.Array) { throw new InvalidDataException(); }
            foreach (var item in element.EnumerateArray()) { CheckShape(item, type.GetElementType()!, path + "[]"); }
        }
        else if (type.IsPrimitive)
        {
            if (element.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException();
            }
        }
        else
        {
            if (element.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
            foreach (var property in element.EnumerateObject())
            {
                var member = type.GetProperties().SingleOrDefault(candidate =>
                    candidate.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
                if (member is not null)
                {
                    CheckShape(property.Value, member.PropertyType, path + "." + member.Name);
                }
                else if (type == typeof(DeploymentOptions) || type == typeof(PersistentKeyOptions))
                {
                    throw new InvalidDataException();
                }
            }
        }
    }

    private static bool OptionalNullString(string path) => path == "Entra.ClientSecret";

    private static InvalidOperationException Translate(ClcConfigurationException exception) => exception.Reason switch
    {
        ClcConfigurationErrorReason.MissingFile => new($"Required external configuration file is missing: '{exception.FilePath}'. Provision it from application.Example.json and grant the process identity read access."),
        ClcConfigurationErrorReason.UnreadableFile => new($"External configuration file cannot be read: '{exception.FilePath}'. Check file access for the process identity and restart the application."),
        ClcConfigurationErrorReason.InvalidFrameworkLogLevel => new("External configuration Logging:LogLevel settings must use valid log levels (Trace through None). Correct the logging settings and restart the application."),
        _ => InvalidConfiguration(exception.FilePath)
    };

    private static InvalidOperationException InvalidConfiguration(string path) =>
        new($"External configuration file contains invalid JSON or application shape: '{path}'. Correct the JSON and restart the application.");

    internal static void RejectPreLoadSources(IConfigurationRoot? configuration, bool development, bool validation = false)
    {
        if (configuration is not null) { ConfigurationContract.RejectLegacy(configuration); }
        using var ambient = (ConfigurationRoot)new ConfigurationBuilder()
            .AddEnvironmentVariables("ASPNETCORE_")
            .AddEnvironmentVariables("DOTNET_")
            .AddEnvironmentVariables()
            .Build();
        ConfigurationContract.RejectLegacy(ambient);

        var hasTestFile = configuration is not null && HasTestFileSetting(configuration) || HasTestFileSetting(ambient);
        if (hasTestFile && (validation || !development))
        {
            throw new InvalidOperationException("The test-only configuration file override requires Development startup and is forbidden in Production, Staging and deployment validation.");
        }
    }

    internal static bool HasTestFileSetting(IConfigurationRoot configuration) => configuration.Providers.Any(provider =>
        provider.TryGet(TestFileKey, out _) || provider.GetChildKeys([], TestFileKey).Any());

    internal static T Bind<T>(IConfiguration configuration, string section, bool required = true) where T : new()
    {
        try
        {
            return configuration.GetSection(section).Get<T>() ?? (required
                ? throw new InvalidOperationException()
                : new T());
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        {
            // Binder errors include submitted values; expose only the known section name.
            throw new InvalidOperationException($"External configuration section '{section}' is missing or has invalid value types. Check application.Example.json and restart the application.");
        }
    }
}
