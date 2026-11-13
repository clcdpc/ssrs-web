using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using NLog.Targets.Wrappers;
using SsrsPortal.Configuration;
using SsrsPortal.Soap;

namespace SsrsPortal.Logging;

// Each host owns this factory. Never configure LogManager or call its global Shutdown.
internal sealed class ApplicationLogging : IDisposable
{
    private readonly LogFactory _factory = new() { AutoShutdown = false };
    private readonly SafeLogLayout _layout;
    private readonly TextWriter _processOutput;
    private string? _clientSecret;
    private int _fileFailures;
    private int _disposed;
    internal string DirectoryPath { get; }
    internal LogFactory Factory => _factory;
    internal bool FileEnabled { get; private set; } = true;

    internal static string ConventionalDirectory => Path.Combine(ProgramDataPath.Paths.ApplicationDataDirectory, "Logs");

    internal static string ResolveDirectory(WebApplicationBuilder builder)
    {
        var testPath = builder.Configuration[ExternalConfigurationLoader.TestFileKey];
        string directory;
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
            directory = Path.Combine(Path.GetDirectoryName(testPath)!, "Logs");
        }
        else { directory = ConventionalDirectory; }
        LocalFileSystemPolicy.RequireLocalPhysicalPath(directory, "Application log directory");
        return directory;
    }

    internal ApplicationLogging(string directory, IHttpContextAccessor? requests = null, TextWriter? processOutput = null)
    {
        DirectoryPath = directory;
        _processOutput = processOutput ?? Console.Error;
        _layout = new SafeLogLayout(() => _clientSecret, requests ?? new HttpContextAccessor());
        try
        {
            Configure(new FileLoggingOptions(), bootstrap: true);
            Write(NLog.LogLevel.Info, "Application bootstrap started.");
            VerifyFileLogging();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Configure(IConfiguration configuration)
    {
        // Register the known secret before validating any other operational settings.
        _clientSecret = configuration["Entra:ClientSecret"];
        var options = FileLoggingOptions.Read(configuration);
        Configure(options);
        if (options.Enabled)
        {
            Write(NLog.LogLevel.Info, "Persistent file logging configured.");
            VerifyFileLogging();
        }
        else
        {
            Write(NLog.LogLevel.Warn, "Persistent file logging disabled by Logging:File:Enabled; using process output.");
        }
    }

    private void Configure(FileLoggingOptions options, bool bootstrap = false)
    {
        Flush();
        FileEnabled = options.Enabled;
        var configuration = new LoggingConfiguration(_factory);
        Target target;
        if (options.Enabled)
        {
            try { Directory.CreateDirectory(DirectoryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Persistent file logging cannot create the conventional Logs directory. Provision Logs with create/write/modify permissions for the process identity.");
            }
            var fallback = new FallbackGroupTarget("file-with-fallback") { ReturnToFirstOnSuccess = true };
            var file = CreateFileTarget(DirectoryPath, options, _layout);
            // Do not delete archives before the authoritative retention has been read.
            if (bootstrap) { file.MaxArchiveDays = 0; }
            configuration.AddTarget(file);
            fallback.Targets.Add(file);
            fallback.Targets.Add(new ProcessOutputTarget(_layout, _processOutput, () => Interlocked.Increment(ref _fileFailures)));
            target = fallback;
        }
        else
        {
            target = new ProcessOutputTarget(_layout, _processOutput);
        }
        var asynchronous = new AsyncTargetWrapper(target, 256, AsyncTargetWrapperOverflowAction.Block)
        {
            Name = "async-file", BatchSize = 1, TimeToSleepBetweenBatches = 10
        };
        configuration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, asynchronous);
        _factory.Configuration = configuration;
    }

    internal static FileTarget CreateFileTarget(string directory, FileLoggingOptions options, NLog.Layouts.Layout layout) => new("application-file")
    {
        FileName = Path.Combine(directory, "ssrs-web-${shortdate}.log"),
        Layout = layout,
        // ${shortdate} rolls at local midnight; ArchiveSuffixFormat handles same-day sizes.
        ArchiveAboveSize = options.MaxFileSizeBytes,
        ArchiveSuffixFormat = ".{0:000}",
        MaxArchiveDays = options.RetentionDays,
        MaxArchiveFiles = -1,
        ArchiveOldFileOnStartup = false,
        DeleteOldFileOnStartup = false,
        ReplaceFileContentsOnEachWrite = false,
        KeepFileOpen = true,
        AutoFlush = true,
        CreateDirs = true,
        OpenFileCacheSize = 1
    };

    internal void Register(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        // Only the application.json providers retained by ExternalConfigurationLoader are used.
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddProvider(new NLogLoggerProvider(new NLogProviderOptions
        {
            IncludeScopes = true, CaptureMessageTemplates = true, CaptureMessageProperties = true,
            RemoveLoggerFactoryFilter = false, ShutdownOnDispose = false, AutoShutdown = false,
            RegisterServiceProvider = false, LoggingConfigurationSectionName = ""
        }, _factory));
        // Protocol diagnostics can contain callback credentials. Operators can tune all
        // application categories, but these internals remain at least Warning.
        var protocolCategories = new[] { "Microsoft.AspNetCore.Authentication", "Microsoft.IdentityModel" };
        var configuredCategories = builder.Configuration.GetSection("Logging:LogLevel").GetChildren().Select(child => child.Key)
            .Where(name => protocolCategories.Any(prefix => name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)));
        foreach (var category in protocolCategories.Concat(configuredCategories).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.Logging.AddFilter<NLogLoggerProvider>(category, level =>
                level >= Microsoft.Extensions.Logging.LogLevel.Warning && level >= ConfiguredLevel(builder.Configuration, category));
        }
        builder.Services.AddSingleton(this);
    }

    private static Microsoft.Extensions.Logging.LogLevel ConfiguredLevel(IConfiguration configuration, string category)
    {
        var levels = configuration.GetSection("Logging:LogLevel").GetChildren();
        var setting = levels.Where(level => category.StartsWith(level.Key + ".", StringComparison.OrdinalIgnoreCase) ||
                category.Equals(level.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(level => level.Key.Length).FirstOrDefault()?.Value
            ?? configuration["Logging:LogLevel:Default"];
        return Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(setting, true, out var minimum) ? minimum : Microsoft.Extensions.Logging.LogLevel.Information;
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
        Write(NLog.LogLevel.Fatal, "Application startup or hosting failed.", _layout.HasException(exception) ? null : exception);
        Flush();
    }

    internal string SafeStartupMessage(Exception exception) => FailureDiagnostics.Bound(FailureDiagnostics.Filter(exception.Message, _clientSecret), 8192);

    internal static void Fallback(Exception exception, TextWriter? output = null) => (output ?? Console.Error).WriteLine(
        "SSRS Web startup/logging failed. Check the conventional Config/application.json and Logs permissions.\n" +
        FailureDiagnostics.Filter(FailureDiagnostics.ExceptionText(exception, 65536)));

    private void Write(NLog.LogLevel level, string message, Exception? exception = null) =>
        _factory.GetLogger("SsrsPortal.Lifecycle").Log(new LogEventInfo(level, "SsrsPortal.Lifecycle", message) { Exception = exception });

    private void VerifyFileLogging()
    {
        Flush();
        if (Volatile.Read(ref _fileFailures) > 0 || !Directory.EnumerateFiles(DirectoryPath, "ssrs-web-*.log").Any())
        {
            throw new InvalidOperationException("Persistent file logging cannot write to Logs. Check create/write/modify permissions, file locks and available disk space. Startup stopped; see process/hosting output.");
        }
    }

    internal void Flush()
    {
        if (Volatile.Read(ref _disposed) == 0) { _factory.Flush(TimeSpan.FromSeconds(5)); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _factory.Flush(TimeSpan.FromSeconds(5));
        _factory.Dispose();
    }

    private sealed class ProcessOutputTarget(NLog.Layouts.Layout layout, TextWriter output, Action? fileFailure = null) : TargetWithLayout
    {
        protected override void InitializeTarget() { Layout = layout; base.InitializeTarget(); }
        protected override void Write(LogEventInfo logEvent)
        {
            fileFailure?.Invoke();
            if (fileFailure is not null) { output.WriteLine("SSRS Web persistent file logging write failed; check Logs permissions, file locks and disk space. Event follows on process output."); }
            output.WriteLine(RenderLogEvent(Layout, logEvent));
        }
    }
}
