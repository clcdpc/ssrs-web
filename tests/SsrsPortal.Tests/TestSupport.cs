using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SsrsPortal.Configuration;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class Clock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now += value;
}

public sealed class CaptureLogger<T> : ILogger<T>
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
}

public sealed class Harness : IDisposable
{
    public PortalOptions Options { get; } = new()
    {
        UseMock = true, TestAuth = true, ReportServer = "https://synthetic.invalid/ReportServer", AllowedRoots = [SyntheticSsrsHandler.Root],
        ReportAccessPolicy = "GlobalBlocklist", ExcludedPrefixes = ["/Training/Internal", "/Training/Custom_Restored"]
    };
    public Clock Clock { get; } = new();
    public SyntheticSsrsHandler Mock { get; } = new();
    public CaptureLogger<SoapTransport> Logger { get; } = new();
    public SsrsClient Soap { get; }
    public CatalogService Catalog { get; }
    public ParameterService Parameters { get; }
    public SessionStore Sessions { get; }
    public ExecutionService Execution { get; }
    private readonly HttpClient _http;
    private readonly SoapTransport _transport;
    public Harness(Action<PortalOptions>? configure = null, int? transportRequestSeconds = null, Func<HttpMessageHandler, HttpMessageHandler>? wrapHandler = null)
    {
        configure?.Invoke(Options);
        _http = new HttpClient(wrapHandler?.Invoke(Mock) ?? Mock) { Timeout = Timeout.InfiniteTimeSpan };
        // Tests may separate the batch and SOAP deadlines to exercise each legal outcome.
        // Normal fixtures keep the same live options instance used by production registration.
        var transportOptions = Options;
        if (transportRequestSeconds.HasValue)
        {
            transportOptions = new PortalOptions
            {
                ReportServer = Options.ReportServer, MaxSoapBytes = Options.MaxSoapBytes,
                MaxConcurrentSoap = Options.MaxConcurrentSoap, RequestSeconds = transportRequestSeconds.Value
            };
        }
        _transport = new SoapTransport(_http, transportOptions, Logger);
        Soap = new SsrsClient(_transport, Options);
        Catalog = new CatalogService(Soap, Options, Clock);
        Parameters = new ParameterService(Soap, Options);
        Sessions = new SessionStore(Options, Clock);
        Execution = new ExecutionService(Soap, Catalog, Parameters, Sessions, new HtmlSafety(Options));
    }
    public async Task<CatalogReport> Report(string name = "Holds Ratio") => await Catalog.ResolveAsync((await Catalog.BrowseAsync(default)).Single(r => r.Name == name).Id, default);
    public static ParameterInput Inputs(string library = "1", string flag = "false", string?[]? collections = null) => new(new Dictionary<string, string?[]>
    {
        ["library"] = [library], ["applyCollectionFilter"] = [flag], ["collections"] = collections ?? [library + "-" + (flag == "true" ? "on" : "off") + "-1"],
        ["start"] = ["2026-01-01T00:00:00"], ["ratio"] = ["1.5"], ["note"] = [null]
    });
    public void Dispose()
    {
        Sessions.Dispose(); Catalog.Dispose(); _transport.Dispose(); _http.Dispose();
    }
}

public sealed class PortalFactory(Dictionary<string, string?>? overrides = null, string environment = "Development", Action<IServiceCollection>? configureServices = null,
    Dictionary<string, string?>? hostSettings = null) : WebApplicationFactory<Program>
{
    public TestConfigurationFile ConfigurationFile { get; } = new(overrides);
    public Clock Clock { get; } = new();
    public CaptureProvider Logs { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.UseSetting(ExternalConfigurationLoader.TestFileKey, ConfigurationFile.Path);
        foreach (var item in hostSettings ?? [])
        {
            builder.UseSetting(item.Key, item.Value);
        }
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(Clock);
            s.AddDataProtection().PersistKeysToFileSystem(new(Path.Combine(Path.GetDirectoryName(ConfigurationFile.Path)!, "DataProtectionKeys")));
            // The development static-assets composite does not own/dispose its
            // underlying physical watchers. Each fixture serves the real wwwroot
            // through one DI-owned provider, closing version-cache watches on exit.
            s.AddSingleton<FixtureStaticFiles>();
            s.AddSingleton<IStartupFilter, FixtureWebRoot>();
            configureServices?.Invoke(s);
        });
    }
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) { ConfigurationFile.Dispose(); } }
    }
    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { ConfigurationFile.Dispose(); }
    }
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public SyntheticSsrsHandler Mock => Services.GetRequiredService<SyntheticSsrsHandler>();
    public async Task<LoginClient> Login(string profile = "staff-one")
    {
        var http = Browser();
        var page = await http.GetAsync("/TestLogin");
        if (!page.IsSuccessStatusCode)
        {
            Assert.True(page.IsSuccessStatusCode, "Synthetic sign-in page failed: " + await page.Content.ReadAsStringAsync() +
                "\n" + string.Join('\n', Logs.Messages));
        }
        var doc = new HtmlParser().ParseDocument(await page.Content.ReadAsStringAsync());
        var token = doc.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var signIn = await http.PostAsync("/TestLogin", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Profile"] = profile, ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Found, signIn.StatusCode);
        var csrf = await http.GetAsync("/api/csrf");
        if (signIn.Headers.Location?.OriginalString == "/Denied")
        {
            return new LoginClient(http, "");
        }
        csrf.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        return new LoginClient(http, json.RootElement.GetProperty("token").GetString()!);
    }
}

internal sealed class FixtureStaticFiles(IWebHostEnvironment environment) : PhysicalFileProvider(environment.WebRootPath!) { }

internal sealed class FixtureWebRoot(IWebHostEnvironment environment, FixtureStaticFiles files) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        environment.WebRootFileProvider = files;
        next(app);
    };
}

public sealed class LoginClient(HttpClient http, string csrf) : IDisposable
{
    public HttpClient Http { get; } = http;
    public async Task<HttpResponseMessage> Post(string path, object body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", csrf);
        return await Http.SendAsync(request, ct);
    }
    public async Task<string> Id(string name = "Holds Ratio") => (await Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!.Single(e => e.Name == name).Id;
    public async Task<string> Url(string name = "Holds Ratio") => (await Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!.Single(e => e.Name == name).ReportUrl!;
    public async Task<RunResult> Run(string name = "Holds Ratio", ParameterInput? input = null)
    {
        var id = await Id(name);
        using var response = await Post("/api/reports/" + id + "/run", input ?? Harness.Inputs());
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return System.Text.Json.JsonSerializer.Deserialize<RunResult>(text, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }
    public void Dispose() => Http.Dispose();
}
