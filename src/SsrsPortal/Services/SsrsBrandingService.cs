using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SsrsPortal.Core;

namespace SsrsPortal.Services;

// Internal snapshots never become a JSON API. Only their validated image/CSS are served.
internal sealed record BrandingSnapshot(byte[]? Logo, string? Background, string? Foreground,
    string Version, DateTimeOffset RefreshedAt, DateTimeOffset RefreshAfter, bool Degraded)
{
    public string Css => Background is null ? "/* Default portal header. */\n" :
        $".shell-header{{--ssrs-header-background:{Background};--ssrs-header-foreground:{Foreground};" +
        $"--ssrs-header-link:{Foreground};--ssrs-header-hover:{Foreground};--ssrs-header-focus:{Foreground};" +
        $"--ssrs-header-control-background:{Background};--ssrs-header-control-foreground:{Foreground};" +
        $"--ssrs-header-control-border:{Foreground};--ssrs-header-divider:{Foreground};}}\n";
}

public sealed class SsrsBrandingService(HttpClient client, PortalOptions options, TimeProvider clock,
    ILogger<SsrsBrandingService> logger, IHostApplicationLifetime lifetime) : IHostedService
{
    internal const int MaxLogoBytes = 1024 * 1024;
    internal const int MaxColorsBytes = 64 * 1024;
    internal static readonly TimeSpan SuccessLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RetryLifetime = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly Queue<BrandingSnapshot> _history = new();
    private BrandingSnapshot _current = Create(null, null, null, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false);
    private Task? _refresh;
    private bool _initialized;

    // The same singleton/task handles startup, cold pages and later cache refreshes.
    // Starting the host never waits for connectivity; shutdown cancels the shared work.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) { StartRefresh(); }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? refresh;
        lock (_sync) { refresh = _refresh; }
        if (refresh is not null) { await refresh.WaitAsync(cancellationToken); }
    }

    internal async Task<BrandingSnapshot> GetInitialSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task? initial;
        lock (_sync)
        {
            StartRefresh();
            // Absence and failure are completed initializations too: their retry must
            // be nonblocking just like refresh of a previously successful snapshot.
            initial = _initialized ? null : _refresh;
        }
        if (initial is not null) { await initial.WaitAsync(ct); }
        lock (_sync) { return _current; }
    }

    // Explicit immediate cache reads never wait. Resource endpoints only use Find.
    internal BrandingSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            StartRefresh();
            return _current;
        }
    }

    internal BrandingSnapshot? Find(string version)
    {
        if (version.Length != 64 || version.Any(c => !char.IsAsciiHexDigit(c))) { return null; }
        lock (_sync)
        {
            return _current.Version == version ? _current : _history.FirstOrDefault(s => s.Version == version);
        }
    }

    internal async Task<BrandingSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        Task? refresh;
        lock (_sync) { StartRefresh(); refresh = _refresh; }
        if (refresh is not null) { await refresh.WaitAsync(ct); }
        lock (_sync) { return _current; }
    }

    private void StartRefresh()
    {
        if (clock.GetUtcNow() >= _current.RefreshAfter && (_refresh is null || _refresh.IsCompleted) &&
            !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            _refresh = Task.Run(RefreshCoreAsync, CancellationToken.None);
        }
    }

    private async Task RefreshCoreAsync()
    {
        // A shared deadline covers headers, bodies, and both resource reads. It is independent
        // of any user's aborted request and canceled when the application shuts down.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(5, options.RequestSeconds)), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetime.ApplicationStopping);
        var logoTask = ReadAsync("logo", MaxLogoBytes, stop.Token);
        var colorsTask = ReadAsync("colors", MaxColorsBytes, stop.Token);
        try { await Task.WhenAll(logoTask, colorsTask).WaitAsync(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // Optional branding must not fault Razor rendering, even for an unexpected
            // transport failure. Never log exception details from the remote service.
            logger.LogWarning("SSRS branding refresh unavailable; retaining cached resources if available");
        }
        if (lifetime.ApplicationStopping.IsCancellationRequested) { return; }
        // The shared deadline also bounds a transport that fails to observe cancellation.
        // Completed optional resources still publish; late tasks cannot publish a snapshot.
        var logo = logoTask.IsCompletedSuccessfully ? logoTask.Result : Resource.Unavailable;
        var colors = colorsTask.IsCompletedSuccessfully ? colorsTask.Result : Resource.Unavailable;
        lock (_sync)
        {
            var png = logo.Failed ? _current.Logo : logo.Bytes;
            var palette = colors.Failed ? (_current.Background, _current.Foreground) : colors.Colors;
            var degraded = logo.Failed || colors.Failed;
            var now = clock.GetUtcNow();
            var next = Create(png, palette.Item1, palette.Item2, now,
                now + (degraded || (png is null && palette.Item1 is null) ? RetryLifetime : SuccessLifetime), degraded);
            if (next.Version != _current.Version)
            {
                _history.Enqueue(_current);
                // Bounded retention keeps in-flight pages consistent across refreshes.
                while (_history.Count > 3) { _history.Dequeue(); }
            }
            _current = next;
            _initialized = true;
        }
    }

    internal static Uri ResourceUri(string reportServer, string key)
    {
        if (key is not ("logo" or "colors")) { throw new ArgumentOutOfRangeException(nameof(key)); }
        // Exactly the existing configuration validation; no alternate origin/path discovery.
        var origin = PortalOptions.ReportServerUri(reportServer);
        return new Uri(origin, "/Reports/api/v2.0/SafeGetSystemResourceContent(type='UniversalBrand',key='" + key + "')");
    }

    private async Task<Resource> ReadAsync(string key, int limit, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ResourceUri(options.ReportServer, key));
            request.Headers.Accept.ParseAdd(key == "logo" ? "image/png, application/octet-stream, application/json" : "application/json, application/octet-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // SafeGet's documented absence is an empty result. A missing REST route (404)
            // is an availability failure, so it must not discard previously valid branding.
            if (response.StatusCode == HttpStatusCode.NoContent) { return Resource.Missing; }
            if (response.StatusCode != HttpStatusCode.OK) { return Failure(key, "http-" + (int)response.StatusCode); }
            if (response.Content.Headers.ContentEncoding.Count != 0) { return Failure(key, "encoding"); }
            var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (media is not (null or "application/octet-stream" or "application/json") && !(key == "logo" && media == "image/png"))
            { return Failure(key, "media-type"); }
            // Edm.Binary can also be represented by an OData JSON base64 value.
            var wireLimit = media == "application/json" ? (limit * 4 / 3) + 4096 : limit;
            if (response.Content.Headers.ContentLength > wireLimit) { return Failure(key, "size"); }
            using var buffer = new MemoryStream();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var chunk = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + count > wireLimit) { return Failure(key, "size"); }
                buffer.Write(chunk, 0, count);
            }
            var bytes = buffer.ToArray();
            if (bytes.Length == 0) { return Resource.Missing; }
            if (media == "application/json")
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) { return Failure(key, "json-shape"); }
                if (json.RootElement.TryGetProperty("value", out var value))
                {
                    if (json.RootElement.EnumerateObject().Any(p => p.Name is not ("value" or "@odata.context")) ||
                        json.RootElement.EnumerateObject().Count(p => p.Name == "value") != 1) { return Failure(key, "json-shape"); }
                    if (value.ValueKind == JsonValueKind.Null) { return Resource.Missing; }
                    if (value.ValueKind != JsonValueKind.String) { return Failure(key, "binary-value"); }
                    var encoded = value.GetString()!.Replace('-', '+').Replace('_', '/');
                    bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
                }
                else if (key == "logo") { return Failure(key, "binary-value"); }
            }
            if (bytes.Length == 0) { return Resource.Missing; }
            if (bytes.Length > limit) { return Failure(key, "size"); }
            if (key == "logo")
            {
                return PngValidation.Valid(bytes) ? new Resource(bytes, (null, null), false) : Failure(key, "png");
            }
            var palette = BrandingColors.Parse(bytes);
            return new Resource(null, palette, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or FormatException)
        {
            // No URL, exception text, headers, payload, or authentication material in logs.
            return Failure(key, ex is OperationCanceledException ? "timeout-or-stopped" : "invalid-or-unavailable");
        }
    }

    private Resource Failure(string key, string code)
    {
        logger.LogWarning("SSRS branding {Resource} unavailable ({Code}); retaining cached resource if available", key, code);
        return new Resource(null, (null, null), true);
    }

    private static BrandingSnapshot Create(byte[]? logo, string? background, string? foreground,
        DateTimeOffset refreshed, DateTimeOffset after, bool degraded)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(background + ":" + foreground + ":"));
        if (logo is not null) { hash.AppendData(logo); }
        return new BrandingSnapshot(logo, background, foreground, Convert.ToHexStringLower(hash.GetHashAndReset()), refreshed, after, degraded);
    }

    private sealed record Resource(byte[]? Bytes, (string?, string?) Colors, bool Failed)
    {
        public static readonly Resource Missing = new(null, (null, null), false);
        public static readonly Resource Unavailable = new(null, (null, null), true);
    }
}

internal static class BrandingColors
{
    internal static (string?, string?) Parse(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Unique(root)) { throw new JsonException(); }
        if (!root.TryGetProperty("interface", out var palette)) { throw new JsonException(); }
        if (palette.ValueKind != JsonValueKind.Object || !Unique(palette)) { throw new JsonException(); }
        if (!palette.TryGetProperty("secondary", out var value) || Color(value) is not { } background) { throw new JsonException(); }
        var foreground = palette.TryGetProperty("secondaryContrast", out var supplied) ? Color(supplied) : null;
        if (foreground is null || Contrast(background, foreground) < 4.5)
        {
            foreground = Contrast(background, "#000000") >= Contrast(background, "#FFFFFF") ? "#000000" : "#FFFFFF";
        }
        return (background, foreground);
    }

    private static bool Unique(JsonElement value) => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static string? Color(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is { Length: 7 } && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? text.ToUpperInvariant() : null;
    }
    internal static double Contrast(string a, string b)
    {
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static double Luminance(string color)
    {
        double Channel(int offset)
        {
            var v = Convert.ToInt32(color.Substring(offset, 2), 16) / 255.0;
            return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
    }
}
