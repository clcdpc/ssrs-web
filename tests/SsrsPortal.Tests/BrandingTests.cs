using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class BrandingTests
{
    [Theory]
    [InlineData("https://dev.example/ReportServer", "https://dev.example/Reports/api/v2.0/")]
    [InlineData("https://training.example:8443/ReportServer/", "https://training.example:8443/Reports/api/v2.0/")]
    [InlineData("https://prod.example/ReportServer", "https://prod.example/Reports/api/v2.0/")]
    [InlineData("https://[::1]:9443/ReportServer", "https://[::1]:9443/Reports/api/v2.0/")]
    public void OnlyConfiguredHttpsOriginAndPortAreUsed(string reportServer, string prefix)
    {
        foreach (var key in new[] { "logo", "colors" })
        {
            Assert.Equal(prefix + "SafeGetSystemResourceContent(type='UniversalBrand',key='" + key + "')",
                SsrsBrandingService.ResourceUri(reportServer, key).AbsoluteUri);
        }
    }

    [Theory]
    [InlineData("http://host/ReportServer")]
    [InlineData("https://host/Reports")]
    [InlineData("https://user:secret@host/ReportServer")]
    [InlineData("https://host/ReportServer?url=https://evil.invalid")]
    [InlineData("https://host/ReportServer#fragment")]
    [InlineData("https://host/ReportServer/../../Reports")]
    public void EndpointValidationRemainsStrict(string value) =>
        Assert.Throws<InvalidOperationException>(() => SsrsBrandingService.ResourceUri(value, "logo"));

    [Fact]
    public void ArbitraryResourceKeysAreRejected() => Assert.Throws<ArgumentOutOfRangeException>(() =>
        SsrsBrandingService.ResourceUri("https://host/ReportServer", "https://evil.invalid"));

    [Fact]
    public async Task ProductionTransportRetainsServiceIdentityAndRejectsRedirectsAndCookies()
    {
        await using var factory = new PortalFactory(new()
        {
            ["Portal:UseMock"] = "false", ["Portal:TestAuth"] = "false", ["Entra:ClientSecret"] = "synthetic-only"
        });
        var handler = Assert.IsType<HttpClientHandler>(factory.Services.GetRequiredService<HttpMessageHandler>());
        Assert.True(handler.UseDefaultCredentials);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task RawBinaryPngAndJsonColorsPopulateCoherentSnapshotAndCache()
    {
        using var h = new BrandingHarness(SyntheticBranding.Dark);
        var first = await h.Service.RefreshAsync();
        Assert.Equal(SyntheticBranding.WideLogo, first.Logo);
        Assert.Equal("#183A53", first.Background); Assert.Equal("#FFFFFF", first.Foreground);
        Assert.False(first.Degraded);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromMinutes(10), first.RefreshAfter);
        for (var i = 0; i < 30; i++) { Assert.Same(first, h.Service.GetSnapshot()); }
        await h.Service.RefreshAsync();
        Assert.Equal(1, h.Mock.BrandingCalls["logo"]); Assert.Equal(1, h.Mock.BrandingCalls["colors"]);
        Assert.DoesNotContain("synthetic.invalid", first.Css);
    }

    [Fact]
    public async Task ODataBinaryEnvelopeSupportsStandardAndUrlSafeBase64()
    {
        var fixture = SyntheticBranding.Dark;
        using var h = new BrandingHarness(new SyntheticBranding(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value = Convert.ToBase64String(fixture.Logo!).Replace('+', '-').Replace('/', '_').TrimEnd('=') })),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
            { ["@odata.context"] = "https://internal.invalid/$metadata#Edm.Binary", ["value"] = Convert.ToBase64String(fixture.Colors!) })))
            { LogoContentType = "application/json" });
        var snapshot = await h.Service.RefreshAsync();
        Assert.Equal(fixture.Logo, snapshot.Logo); Assert.Equal("#183A53", snapshot.Background);
        Assert.DoesNotContain("internal.invalid", snapshot.Css);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    public async Task MimeDoesNotOverrideValidatedPngBytes(string mime)
    {
        using var h = new BrandingHarness(SyntheticBranding.Light with { LogoContentType = mime });
        Assert.NotNull((await h.Service.RefreshAsync()).Logo);
    }

    [Fact]
    public async Task ColdWaitersShareInitializationAndExpiredSnapshotsRemainImmediate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new BrandingHarness(SyntheticBranding.Dark with { Gate = gate.Task });
        var fallback = h.Service.GetSnapshot();
        Assert.Null(fallback.Logo);
        await h.Service.StartAsync(default);
        var pending = Enumerable.Range(0, 60).Select(_ => h.Service.GetInitialSnapshotAsync()).ToArray();
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        Assert.Same(fallback, h.Service.GetSnapshot());
        gate.SetResult();
        var snapshots = await Task.WhenAll(pending);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.All(h.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Mock.Branding = SyntheticBranding.Light;
        Assert.Same(snapshots[0], await h.Service.GetInitialSnapshotAsync());
        var next = await h.Service.RefreshAsync();
        Assert.Equal("#E9DDAF", next.Background); Assert.NotEqual(snapshots[0].Version, next.Version);
        Assert.Equal(SyntheticBranding.CompactLogo, next.Logo);
        Assert.Same(snapshots[0], h.Service.Find(snapshots[0].Version));
        Assert.All(h.Mock.BrandingCalls.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelEnvironmentRefresh()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new BrandingHarness(SyntheticBranding.Dark with { Gate = gate.Task });
        using var cancelled = new CancellationTokenSource();
        var pending = h.Service.GetInitialSnapshotAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        gate.SetResult();
        Assert.NotNull((await h.Service.RefreshAsync()).Logo);
        Assert.All(h.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(500)] [InlineData(302)] [InlineData(204)]
    public async Task HttpFailuresOrAbsenceAreNegativeCached(int status)
    {
        using var h = new BrandingHarness(SyntheticBranding.Dark with { Status = (HttpStatusCode)status });
        var snapshot = await h.Service.RefreshAsync();
        Assert.Null(snapshot.Logo); Assert.Null(snapshot.Background);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromMinutes(1), snapshot.RefreshAfter);
        await h.Service.RefreshAsync();
        Assert.All(h.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Mock.Branding = SyntheticBranding.Light;
        Assert.NotNull((await h.Service.RefreshAsync()).Logo);
    }

    [Fact]
    public async Task TemporaryFailuresRetainLastGoodResourcesButConfirmedRemovalRestoresDefault()
    {
        using var h = new BrandingHarness(SyntheticBranding.Dark);
        var first = await h.Service.RefreshAsync();
        foreach (var fixture in new[] { SyntheticBranding.Malformed,
            SyntheticBranding.Dark with { Status = HttpStatusCode.ServiceUnavailable },
            SyntheticBranding.Dark with { Status = HttpStatusCode.NotFound },
            SyntheticBranding.Dark with { Status = HttpStatusCode.Unauthorized } })
        {
            h.Clock.Advance(TimeSpan.FromMinutes(10)); h.Mock.Branding = fixture;
            var retained = await h.Service.RefreshAsync();
            Assert.True(retained.Degraded); Assert.Equal(first.Version, retained.Version);
            Assert.Equal(first.Logo, retained.Logo); Assert.Equal(first.Background, retained.Background);
        }
        h.Clock.Advance(TimeSpan.FromMinutes(1)); h.Mock.Branding = SyntheticBranding.Missing;
        var removed = await h.Service.RefreshAsync();
        Assert.Null(removed.Logo); Assert.Null(removed.Background); Assert.False(removed.Degraded);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LogoAndPaletteAreIndependent(bool logo)
    {
        using var h = new BrandingHarness(new(logo ? SyntheticBranding.WideLogo : null, logo ? null : SyntheticBranding.Dark.Colors));
        var snapshot = await h.Service.RefreshAsync();
        Assert.Equal(logo, snapshot.Logo is not null); Assert.Equal(!logo, snapshot.Background is not null);
    }

    [Theory]
    [InlineData("<html>Error</html>", "image/png")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>", "application/octet-stream")]
    [InlineData("not a PNG", "image/png")]
    [InlineData("{\"value\":\"https://evil.invalid/logo.png\"}", "application/json")]
    [InlineData("{\"value\":\"aGVsbG8=\",\"url\":\"https://evil.invalid\"}", "application/json")]
    public async Task UntrustedLogoDataNeverReachesSnapshot(string value, string mime)
    {
        using var h = new BrandingHarness(new(Encoding.UTF8.GetBytes(value), null) { LogoContentType = mime });
        Assert.Null((await h.Service.RefreshAsync()).Logo);
    }

    [Theory]
    [InlineData("text/html")] [InlineData("image/svg+xml")] [InlineData("text/css")]
    public async Task UnexpectedMimeIsRejectedEvenForOtherwiseValidPayload(string mime)
    {
        using var h = new BrandingHarness(SyntheticBranding.Dark with { LogoContentType = mime, ColorsContentType = mime });
        var snapshot = await h.Service.RefreshAsync(); Assert.Null(snapshot.Logo); Assert.Null(snapshot.Background);
    }

    [Fact]
    public async Task EncodedResponsesAndOversizedPngOrJsonAreRejected()
    {
        foreach (var fixture in new[]
        {
            SyntheticBranding.Dark with { ContentEncoding = "gzip" },
            new SyntheticBranding(new byte[SsrsBrandingService.MaxLogoBytes + 1], new byte[SsrsBrandingService.MaxColorsBytes * 2]),
        })
        {
            using var h = new BrandingHarness(fixture);
            var snapshot = await h.Service.RefreshAsync(); Assert.Null(snapshot.Logo); Assert.Null(snapshot.Background);
        }
    }

    [Fact]
    public void PngValidationChecksCrcDimensionsContainerAndDecodedPixels()
    {
        var valid = SyntheticBranding.WideLogo; Assert.True(PngValidation.Valid(valid));
        Assert.True(PngValidation.Valid(SyntheticBranding.CompactLogo));
        Assert.False(PngValidation.Valid(valid[..20])); Assert.False(PngValidation.Valid(valid.Concat(new byte[] { 0 }).ToArray()));
        var badCrc = valid.ToArray(); badCrc[40] ^= 1; Assert.False(PngValidation.Valid(badCrc));
        foreach (var width in new uint[] { 0, 4097, uint.MaxValue })
        {
            var huge = valid.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16, 4), width);
            BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(29, 4), PngValidation.Crc(huge.AsSpan(12, 17)));
            Assert.False(PngValidation.Valid(huge));
        }
        // A valid CRC cannot disguise a broken compressed IDAT stream.
        var damaged = valid.ToArray(); var offset = 8;
        while (!damaged.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) { offset += (int)BinaryPrimitives.ReadUInt32BigEndian(damaged.AsSpan(offset, 4)) + 12; }
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(damaged.AsSpan(offset, 4));
        damaged[offset + 8] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(offset + 8 + size, 4), PngValidation.Crc(damaged.AsSpan(offset + 4, size + 4)));
        Assert.False(PngValidation.Valid(damaged));
    }

    [Theory]
    [InlineData("#183a53", "#ffffff", "#FFFFFF")]
    [InlineData("#e9ddaf", "#213951", "#213951")]
    [InlineData("#808080", null, "#000000")]
    [InlineData("#FFFFFF", "#FFFFFF", "#000000")]
    [InlineData("#000000", "#111111", "#FFFFFF")]
    [InlineData("#183A53", "var(--evil)", "#FFFFFF")]
    public void PaletteUsesSecondaryAndAccessibleForeground(string background, string? supplied, string expected)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { @interface = new { secondary = background, secondaryContrast = supplied } });
        var palette = BrandingColors.Parse(bytes);
        Assert.Equal(background.ToUpperInvariant(), palette.Item1); Assert.Equal(expected, palette.Item2);
        Assert.True(BrandingColors.Contrast(palette.Item1!, palette.Item2!) >= 4.5);
    }

    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("{\"interface\":[]}")]
    [InlineData("{\"interface\":{\"secondary\":\"red\"}}")]
    [InlineData("{\"interface\":{\"secondary\":\"#fff\"}}")]
    [InlineData("{\"interface\":{\"secondary\":\"#GGGGGG\"}}")]
    [InlineData("{\"interface\":{\"secondary\":\"#112233;background:url(https://evil.invalid)\"}}")]
    [InlineData("{\"interface\":{\"secondary\":\"rgb(0,0,0)\"}}")]
    [InlineData("{\"interface\":{\"secondary\":\"#112233\",\"secondary\":\"#445566\"}}")]
    public void MalformedOrInjectedPaletteIsRejected(string json) =>
        Assert.Throws<JsonException>(() => BrandingColors.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task AuthenticatedRoutesServeOnlyVersionPinnedValidatedResourcesWithExistingSecurityHeaders()
    {
        await using var factory = WithBranding(SyntheticBranding.Dark); using var user = await factory.Login();
        var service = factory.Services.GetRequiredService<SsrsBrandingService>();
        var snapshot = await service.RefreshAsync();
        var html = await user.Http.GetStringAsync(await user.Url());
        var doc = new HtmlParser().ParseDocument(html);
        var brand = doc.QuerySelector(".brand")!;
        Assert.Equal("/", brand.GetAttribute("href")); Assert.Equal("Staff reports", brand.GetAttribute("aria-label"));
        Assert.NotNull(brand.QuerySelector(".brand-fallback[hidden]"));
        Assert.Equal("", brand.QuerySelector("img")!.GetAttribute("alt"));
        Assert.NotNull(doc.QuerySelector(".breadcrumbs")); Assert.NotNull(doc.QuerySelector(".account form[action='/signout']"));
        var logoPath = brand.QuerySelector("img")!.GetAttribute("src")!;
        using var response = await user.Http.GetAsync(logoPath);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(snapshot.Logo, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("style-src 'self';", csp); Assert.DoesNotContain("unsafe-inline", csp);
        var cssPath = doc.QuerySelector("link[href$='theme.css']")!.GetAttribute("href")!;
        using var css = await user.Http.GetAsync(cssPath);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType); Assert.Equal(snapshot.Css, await css.Content.ReadAsStringAsync());
        Assert.Contains("--ssrs-header-background:#183A53;", snapshot.Css);
        Assert.Contains("--ssrs-header-foreground:#FFFFFF;", snapshot.Css);
        Assert.Matches(@"^\.shell-header\{(?:--ssrs-header-[a-z-]+:#[0-9A-F]{6};)+\}\n$", snapshot.Css);
        Assert.DoesNotContain("synthetic.invalid", html);
        foreach (var path in new[] { "/branding/logo", "/branding/colors", "/branding/https://evil.invalid", "/branding/notaversion/theme.css", "/branding/" + snapshot.Version + "/anything" })
        { Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(path)).StatusCode); }
        using var anonymous = factory.Browser();
        Assert.Equal(HttpStatusCode.Found, (await anonymous.GetAsync(logoPath)).StatusCode);
        using var denied = await factory.Login("outsider");
        Assert.Equal(HttpStatusCode.Found, (await denied.Http.GetAsync(cssPath)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(10)); factory.Mock.Branding = SyntheticBranding.Light;
        var next = await service.RefreshAsync(); Assert.NotEqual(snapshot.Version, next.Version);
        Assert.Equal(snapshot.Logo, await user.Http.GetByteArrayAsync(logoPath));
        Assert.Equal(snapshot.Css, await user.Http.GetStringAsync(cssPath));
        Assert.Equal(next.Logo, await user.Http.GetByteArrayAsync("/branding/" + next.Version + "/logo.png"));
    }

    [Fact]
    public async Task MissingResourcesPreserveDefaultMarkupAndNeverExposeBrokenImage()
    {
        await using var factory = new PortalFactory(); using var user = await factory.Login();
        var service = factory.Services.GetRequiredService<SsrsBrandingService>(); await service.RefreshAsync();
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync("/"));
        Assert.Null(doc.QuerySelector(".brand-logo")); Assert.Null(doc.QuerySelector(".brand-fallback[hidden]"));
        Assert.Contains("Staff reports", doc.QuerySelector(".brand")!.TextContent);
        Assert.Null(doc.QuerySelector("link[href$='theme.css']"));
        Assert.DoesNotContain(factory.Mock.Observations, o => o.Operation.Contains("Brand", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PartialResourcesRenderIndependentlyThroughActualRazorLayout(bool logo, bool colors)
    {
        await using var factory = WithBranding(new(logo ? SyntheticBranding.WideLogo : null, colors ? SyntheticBranding.Dark.Colors : null));
        using var user = await factory.Login();
        var snapshot = await factory.Services.GetRequiredService<SsrsBrandingService>().RefreshAsync();
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync("/"));
        Assert.Equal(logo, doc.QuerySelector(".brand-logo") is not null);
        Assert.Equal(logo, doc.QuerySelector(".brand-fallback[hidden]") is not null);
        Assert.Equal(colors, doc.QuerySelector("link[href$='theme.css']") is not null);
        using var image = await user.Http.GetAsync("/branding/" + snapshot.Version + "/logo.png");
        Assert.Equal(logo ? HttpStatusCode.OK : HttpStatusCode.NotFound, image.StatusCode);
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task VersionHistoryIsBoundedAndNoEndpointCanStartRemoteRequests()
    {
        using var h = new BrandingHarness(SyntheticBranding.Dark);
        var old = await h.Service.RefreshAsync();
        for (var i = 0; i < 5; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(10));
            h.Mock.Branding = new(null, Encoding.UTF8.GetBytes("{\"interface\":{\"secondary\":\"#12340" + i + "\"}}"));
            await h.Service.RefreshAsync();
        }
        Assert.Null(h.Service.Find(old.Version)); Assert.Null(h.Service.Find("https://evil.invalid"));
        var calls = h.Mock.BrandingCalls["logo"];
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(h.Service.Find((await h.Service.RefreshAsync()).Version));
        Assert.Equal(calls + 1, h.Mock.BrandingCalls["logo"]);
        Assert.All(h.Logger.Messages, message => Assert.DoesNotContain("synthetic.invalid", message));
    }

    internal static PortalFactory WithBranding(SyntheticBranding fixture, TimeProvider? clock = null) =>
        new(configureServices: services =>
        {
            services.AddSingleton(new SyntheticSsrsHandler { Branding = fixture });
            if (clock is not null) { services.AddSingleton(clock); }
        });

    private sealed class BrandingHarness : IDisposable
    {
        public SyntheticSsrsHandler Mock { get; } = new();
        public Clock Clock { get; } = new();
        public CaptureLogger<SsrsBrandingService> Logger { get; } = new();
        public SsrsBrandingService Service { get; }
        private readonly HttpClient _client;
        private readonly TestLifetime _lifetime = new();
        public BrandingHarness(SyntheticBranding fixture)
        {
            Mock.Branding = fixture; _client = new HttpClient(Mock) { Timeout = Timeout.InfiniteTimeSpan };
            Service = new(_client, new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" }, Clock, Logger, _lifetime);
        }
        public void Dispose() { _lifetime.StopApplication(); _client.Dispose(); _lifetime.Dispose(); }
    }

    internal sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() => _stop.Dispose();
    }
}
