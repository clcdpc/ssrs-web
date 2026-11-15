using Clc.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;
using SsrsPortal.Configuration;
using SsrsPortal.Soap;

namespace SsrsPortal.Logging;

// Application policy around caller-owned Clc.Logging. Dispose after the host.
internal sealed class ApplicationLogging : IDisposable
{
    private static readonly string[] ProtocolCategories = ["Microsoft.AspNetCore.Authentication", "Microsoft.IdentityModel"];
    private readonly ClcLogging _logging;
    private readonly SafeLogLayout _layout;
    private LogFactory? _factory;
    private string? _clientSecret;
    private bool _archiveLimitConfigured;
    internal string DirectoryPath => _logging.DirectoryPath;
    internal LogFactory Factory => _factory ?? throw new InvalidOperationException("Configured logging is not registered.");
    internal bool FileEnabled { get; private set; } = true;

    internal static string ConventionalDirectory => Path.Combine(ProgramDataPath.Paths.ApplicationDataDirectory, "Logs");

    internal static ApplicationLogging Create(WebApplicationBuilder builder, IHttpContextAccessor? requests = null)
    {
        var testPath = builder.Configuration[ExternalConfigurationLoader.TestFileKey];
        if (ExternalConfigurationLoader.HasTestFileSetting(builder.Configuration))
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("The test-only configuration file override requires Development and is forbidden in Production or Staging.");
            }
            if (string.IsNullOrWhiteSpace(testPath) || !Path.IsPathFullyQualified(testPath))
            {
                throw new InvalidOperationException("The Development test-only configuration file override requires an absolute temporary file path.");
            }
            var applicationDirectory = Path.GetDirectoryName(testPath)!;
            LocalFileSystemPolicy.RequireLocalPhysicalPath(Path.Combine(applicationDirectory, "Logs"), "Application log directory");
            return new ApplicationLogging(applicationDirectory, null, requests);
        }
        var paths = ProgramDataPath.Paths;
        LocalFileSystemPolicy.RequireLocalPhysicalPath(Path.Combine(paths.ApplicationDataDirectory, "Logs"), "Application log directory");
        return new ApplicationLogging(paths.ApplicationDataDirectory, paths, requests);
    }

    private ApplicationLogging(string applicationDirectory, Clc.Configuration.ClcApplicationPaths? paths, IHttpContextAccessor? requests)
    {
        _layout = new SafeLogLayout(() => _clientSecret, requests ?? new HttpContextAccessor());
        void Configure(ClcLoggingOptions options)
        {
            options.Layout = _layout;
            options.ConfigureNLog = ConfigureApplicationNLog;
            options.FlushTimeout = TimeSpan.FromSeconds(5);
            FileEnabled = options.File.Enabled;
        }
        _logging = paths is null ? ClcLogging.CreateForDirectory(applicationDirectory, Configure) : ClcLogging.Create(paths, Configure);
        _logging.Logger.LogInformation("Application bootstrap started.");
    }

    internal void Register(WebApplicationBuilder builder)
    {
        // Register the known secret before validating any other operational settings.
        _clientSecret = builder.Configuration["Entra:ClientSecret"];
        FileLoggingOptions.Validate(builder.Configuration);
        _archiveLimitConfigured = builder.Configuration["Logging:File:MaxArchiveFiles"] is not null;
        builder.Logging.ClearProviders();
        builder.AddClcLogging(_logging);
        // Protocol diagnostics can contain callback credentials. Operators can tune all
        // application categories, but these internals remain at least Warning.
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            // Preserve native provider/category/wildcard precedence and stricter levels.
            // The first rule covers native no-match behavior without outranking configured rules.
            options.Rules.Insert(0, new LoggerFilterRule(null, null, options.MinLevel, null));
            for (var index = 0; index < options.Rules.Count; index++)
            {
                var rule = options.Rules[index];
                options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                    (provider, category, level) =>
                        (provider != typeof(NLogLoggerProvider).FullName ||
                         !ProtocolCategories.Any(prefix => category?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true) ||
                         level >= Microsoft.Extensions.Logging.LogLevel.Warning) &&
                        (rule.Filter?.Invoke(provider, category, level) ?? true));
            }
        });
        builder.Services.AddSingleton(this);
    }

    private void ConfigureApplicationNLog(LoggingConfiguration configuration)
    {
        _factory = configuration.LogFactory;
        var file = configuration.FindTargetByName<FileTarget>("file");
        if (file is not null)
        {
            file.FileName = new CompoundLayout
            {
                Layouts = { Layout.FromLiteral(Path.Combine(DirectoryPath, "ssrs-web-")), new SimpleLayout("${shortdate}.log") }
            };
            if (!_archiveLimitConfigured) { file.MaxArchiveFiles = -1; }
        }
        // Wrap the package's whole route so file failure and recovery keep their ordering.
        var rule = configuration.LoggingRules.Single();
        var asynchronous = new AsyncTargetWrapper(rule.Targets.Single(), 256, AsyncTargetWrapperOverflowAction.Block)
        {
            Name = "async-file", BatchSize = 1, TimeToSleepBetweenBatches = 10
        };
        configuration.AddTarget(asynchronous);
        rule.Targets.Clear();
        rule.Targets.Add(asynchronous);
        // Final safeguard for native factory calls and provider filter misconfiguration.
        // Add only after wrapping the package's single conventional route.
        for (var index = 0; index < ProtocolCategories.Length; index++)
        {
            configuration.LoggingRules.Insert(index, new LoggingRule
            {
                LoggerNamePattern = ProtocolCategories[index] + "*", FinalMinLevel = NLog.LogLevel.Warn
            });
        }
    }

    internal void ObserveLifetime(WebApplication app)
    {
        // Keep targets alive through hosted-service stop and service disposal. The outer
        // Program finally block closes them only after RunAsync has disposed the host.
        app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Application started; environment {Environment}; file logging enabled {FileLoggingEnabled}; log directory {LogDirectory}.", app.Environment.EnvironmentName, FileEnabled, DirectoryPath));
        app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Application stopping; draining requests and background services."));
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            app.Logger.LogInformation("Application stopped.");
            Flush();
        });
    }

    internal void Fatal(Exception exception)
    {
        Flush();
        var unrendered = _layout.HasException(exception) ? null : exception;
        if (_factory is null) { _logging.Logger.LogCritical(unrendered, "Application startup or hosting failed."); }
        else { Write(NLog.LogLevel.Fatal, "Application startup or hosting failed.", unrendered); }
        Flush();
    }

    internal string SafeStartupMessage(Exception exception) => FailureDiagnostics.Bound(FailureDiagnostics.Filter(exception.Message, _clientSecret), 8192);

    internal static void Fallback(Exception exception, TextWriter? output = null) => (output ?? Console.Error).WriteLine(
        "SSRS Web startup/logging failed. Check the conventional Config/application.json and Logs permissions.\n" +
        FailureDiagnostics.Filter(FailureDiagnostics.ExceptionText(exception, 65536)));

    private void Write(NLog.LogLevel level, string message, Exception? exception = null) =>
        Factory.GetLogger("SsrsPortal.Lifecycle").Log(new LogEventInfo(level, "SsrsPortal.Lifecycle", message) { Exception = exception });

    internal void VerifyFileLogging()
    {
        if (!FileEnabled)
        {
            Write(NLog.LogLevel.Warn, "Persistent file logging disabled by Logging:File:Enabled; using process output.");
            Flush();
            return;
        }
        try
        {
            var marker = "logging-ready-" + Guid.NewGuid().ToString("N");
            var probe = new LogEventInfo(NLog.LogLevel.Info, "SsrsPortal.Lifecycle", "Persistent file logging configured/verified; " + marker);
            // The same event supplies the local date, including across midnight/size rollover.
            var filePrefix = "ssrs-web-" + probe.TimeStamp.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Factory.GetLogger(probe.LoggerName).Log(probe);
            Flush();
            // NLog 6 can continue in a numbered file after size rollover/restart.
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, filePrefix + "*.log").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^" + filePrefix + @"(?:\.\d+)?\.log$")) { continue; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // SafeLogLayout bounds a record to 128K characters. Read only its bounded UTF-8 tail.
                var length = stream.Length;
                stream.Seek(Math.Max(0, length - SafeLogLayout.MaximumEventCharacters * 4L), SeekOrigin.Begin);
                var tail = new byte[checked((int)(length - stream.Position))];
                stream.ReadExactly(tail);
                if (System.Text.Encoding.UTF8.GetString(tail).Contains(marker, StringComparison.Ordinal)) { return; }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NLogRuntimeException)
        {
            // Never surface a dependency exception or a submitted path/value to hosting.
        }
        throw new InvalidOperationException("Persistent file logging cannot write to Logs. Check create/write/modify permissions, file locks and available disk space. Startup stopped; see process/hosting output.");
    }

    internal void Flush() => _logging.Flush();
    public void Dispose() => _logging.Dispose();
}
