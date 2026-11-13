using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class BrandingInitializationTests
{
    [Fact]
    public async Task StartupAndConcurrentFirstRazorPagesShareOneRetrievalAndPinnedResources()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PageProbe(21);
        await using var factory = new PortalFactory(configureServices: services =>
        {
            services.AddSingleton(new SyntheticSsrsHandler { Branding = SyntheticBranding.Dark with { Gate = gate.Task } });
            services.AddSingleton<TimeProvider>(new BrandingClock());
            services.Configure<MvcOptions>(options => options.Filters.Add(probe));
        });
        // Starting the host, without any page request, must initiate both reads and
        // finish startup while they are gated. It cannot require a reachable SSRS.
        var service = factory.Services.GetRequiredService<SsrsBrandingService>();
        await WaitFor(() => factory.Mock.BrandingCalls.Count == 2);
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
        Assert.False(gate.Task.IsCompleted);
        using var user = await factory.Login();
        var reportUrl = await user.Url();
        using var aborted = new CancellationTokenSource();
        var cancelledPage = user.Http.GetAsync(reportUrl, aborted.Token);
        var pages = Enumerable.Range(0, 20).Select(i => user.Http.GetAsync(i % 2 == 0 ? "/" : reportUrl)).ToArray();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(pages, page => Assert.False(page.IsCompleted));
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledPage);
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
        gate.SetResult();
        var responses = await Task.WhenAll(pages).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = await service.GetInitialSnapshotAsync();
        foreach (var response in responses)
        {
            using (response)
            {
                response.EnsureSuccessStatusCode();
                var doc = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
                var logo = doc.QuerySelector(".brand-logo")!.GetAttribute("src")!;
                var theme = doc.QuerySelector("link[href$='theme.css']")!.GetAttribute("href")!;
                Assert.Equal("/branding/" + snapshot.Version + "/logo.png", logo);
                Assert.Equal("/branding/" + snapshot.Version + "/theme.css", theme);
                Assert.Equal(SyntheticBranding.WideLogo, await user.Http.GetByteArrayAsync(logo));
                Assert.Equal(snapshot.Css, await user.Http.GetStringAsync(theme));
            }
        }
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialAbsenceOrTimeoutIsBoundedAndLaterPagesNeverWaitForRetry(bool unavailable)
    {
        var clock = new BrandingClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PageProbe(1);
        await using var factory = new PortalFactory(configureServices: services =>
        {
            services.AddSingleton(new SyntheticSsrsHandler
            { Branding = (unavailable ? SyntheticBranding.Dark : SyntheticBranding.Missing) with { Gate = gate.Task } });
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<MvcOptions>(options => options.Filters.Add(probe));
        });
        using var user = await factory.Login();
        var first = user.Http.GetAsync("/");
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        if (unavailable) { clock.Advance(TimeSpan.FromSeconds(5)); } else { gate.SetResult(); }
        using var response = await first.WaitAsync(TimeSpan.FromSeconds(10));
        response.EnsureSuccessStatusCode();
        var doc = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        Assert.Null(doc.QuerySelector(".brand-logo")); Assert.Null(doc.QuerySelector("link[href$='theme.css']"));
        Assert.NotNull(doc.QuerySelector(".brand-fallback:not([hidden])"));
        var service = factory.Services.GetRequiredService<SsrsBrandingService>();
        var initial = await service.GetInitialSnapshotAsync();
        Assert.Equal(unavailable, initial.Degraded);
        for (var i = 0; i < 5; i++) { (await user.Http.GetAsync("/").WaitAsync(TimeSpan.FromSeconds(10))).EnsureSuccessStatusCode().Dispose(); }
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(1, count));
        var retryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Mock.Branding = SyntheticBranding.Dark with { Gate = retryGate.Task };
        clock.Advance(TimeSpan.FromMinutes(1));
        var cached = await user.Http.GetStringAsync("/").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("brand-logo", cached);
        await WaitFor(() => factory.Mock.BrandingCalls.Values.All(count => count == 2));
        Assert.False(retryGate.Task.IsCompleted);
        retryGate.SetResult();
        await service.RefreshAsync();
        Assert.Contains("brand-logo", await user.Http.GetStringAsync("/"));
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task WarmRazorPagesKeepLastGoodSnapshotDuringCoalescedRefreshAndRecoverWithoutRestart()
    {
        await using var factory = BrandingTests.WithBranding(SyntheticBranding.Dark);
        using var user = await factory.Login();
        var firstHtml = await user.Http.GetStringAsync("/");
        var service = factory.Services.GetRequiredService<SsrsBrandingService>();
        var first = await service.GetInitialSnapshotAsync();
        Assert.Contains(first.Version, firstHtml);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Mock.Branding = SyntheticBranding.Light with { Gate = gate.Task };
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        var pages = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => user.Http.GetStringAsync("/"))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(pages, html => Assert.Contains(first.Version, html));
        await WaitFor(() => factory.Mock.BrandingCalls.Values.All(count => count == 2));
        gate.SetResult();
        var updated = await service.RefreshAsync();
        Assert.NotEqual(first.Version, updated.Version);
        Assert.Contains(updated.Version, await user.Http.GetStringAsync("/"));
        Assert.Equal(first.Logo, await user.Http.GetByteArrayAsync("/branding/" + first.Version + "/logo.png"));
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        factory.Mock.Branding = SyntheticBranding.Dark with { Status = HttpStatusCode.ServiceUnavailable };
        Assert.Contains(updated.Version, await user.Http.GetStringAsync("/"));
        Assert.Equal(updated.Version, (await service.RefreshAsync()).Version);
        Assert.Contains(updated.Version, await user.Http.GetStringAsync("/"));
        Assert.All(factory.Mock.BrandingCalls.Values, count => Assert.Equal(3, count));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FirstRazorPageKeepsSuccessfulResourceWhenOtherResourceIsInvalid(bool logoSucceeds)
    {
        await using var factory = BrandingTests.WithBranding(new(
            logoSucceeds ? SyntheticBranding.WideLogo : SyntheticBranding.Malformed.Logo,
            logoSucceeds ? SyntheticBranding.Malformed.Colors : SyntheticBranding.Dark.Colors));
        using var user = await factory.Login();
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync("/"));
        Assert.Equal(logoSucceeds, doc.QuerySelector(".brand-logo") is not null);
        Assert.Equal(!logoSucceeds, doc.QuerySelector("link[href$='theme.css']") is not null);
    }

    [Fact]
    public async Task ShutdownCancelsStartupReadsAndPreventsPublicationOrNewRefresh()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = BrandingTests.WithBranding(SyntheticBranding.Dark with { Gate = gate.Task });
        var service = factory.Services.GetRequiredService<SsrsBrandingService>();
        await WaitFor(() => factory.Mock.BrandingCalls.Count == 2);
        var mock = factory.Mock;
        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitFor(() => mock.ActiveCalls == 0);
        gate.SetResult();
        factory.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Null((await service.GetInitialSnapshotAsync()).Logo);
        Assert.All(mock.BrandingCalls.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData("white")] [InlineData("black")] [InlineData("partial")] [InlineData("small")]
    public async Task FirstRazorLogoEndpointPreservesEveryOriginalPngByteIncludingAlpha(string kind)
    {
        var png = kind switch
        {
            "white" => SyntheticBranding.WhiteLogo, "black" => SyntheticBranding.BlackLogo,
            "small" => SyntheticBranding.SmallLogo, _ => SyntheticBranding.SsrsWideLogo
        };
        await using var factory = BrandingTests.WithBranding(SyntheticBranding.Dark with { Logo = png });
        using var user = await factory.Login();
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync("/"));
        Assert.Equal(png, await user.Http.GetByteArrayAsync(doc.QuerySelector(".brand-logo")!.GetAttribute("src")!));
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { await Task.Delay(5, deadline.Token); }
    }

    private sealed class PageProbe(int count) : IAsyncResultFilter
    {
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if (context.HttpContext.User.Identity?.IsAuthenticated == true &&
                (context.HttpContext.Request.Path == "/" || context.HttpContext.Request.Path.StartsWithSegments("/report")) &&
                Interlocked.Increment(ref _entered) == count) { Entered.TrySetResult(); }
            await next();
        }
    }
}

// The only timers in these isolated hosts are the branding deadlines. Advancing
// time expires active deadlines without adding five real seconds to every test.
internal sealed class BrandingClock : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
    private readonly List<Deadline> _timers = [];
    public override DateTimeOffset GetUtcNow() { lock (_sync) { return _now; } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            var timer = new Deadline(callback, state, _now + dueTime);
            _timers.Add(timer); return timer;
        }
    }
    public void Advance(TimeSpan duration)
    {
        Deadline[] due;
        lock (_sync) { _now += duration; due = _timers.Where(t => !t.Disposed && t.At <= _now).ToArray(); }
        foreach (var timer in due) { timer.Fire(); }
    }
    private sealed class Deadline(TimerCallback callback, object? state, DateTimeOffset at) : ITimer
    {
        public DateTimeOffset At { get; } = at;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) { Disposed = true; callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
