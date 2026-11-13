using System.Net;
using System.Text;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class BrandingFailureTests
{
    [Theory]
    [InlineData("connection")] [InlineData("dns")] [InlineData("cancelled")]
    public async Task TransportFailuresNeverPropagateToPagesAndAreNegativeCached(string kind)
    {
        using var handler = new Handler((_, _) => throw kind switch
        {
            "connection" => new HttpRequestException("sensitive server failure"),
            "dns" => new IOException("internal DNS details"),
            _ => new OperationCanceledException()
        });
        using var http = new HttpClient(handler);
        using var lifetime = new BrandingTests.TestLifetime();
        var clock = new Clock(); var log = new CaptureLogger<SsrsBrandingService>();
        var service = Create(http, clock, log, lifetime);
        var snapshot = await service.RefreshAsync();
        Assert.True(snapshot.Degraded); Assert.Null(snapshot.Logo); Assert.Null(snapshot.Background);
        await service.RefreshAsync(); Assert.Equal(2, handler.Calls);
        Assert.All(log.Messages, message => { Assert.DoesNotContain("sensitive", message); Assert.DoesNotContain("internal", message); });
    }

    [Fact]
    public async Task ChunkedBodiesCannotBypassSizeLimitsAndAreReadWithCancellation()
    {
        var bodies = new List<UnboundedContent>();
        using var handler = new Handler((request, _) =>
        {
            var content = new UnboundedContent(); bodies.Add(content);
            content.Headers.ContentType = new(request.RequestUri!.AbsolutePath.Contains("'logo'", StringComparison.Ordinal) ? "image/png" : "application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler); using var lifetime = new BrandingTests.TestLifetime();
        var service = Create(http, new Clock(), new(), lifetime);
        var snapshot = await service.RefreshAsync();
        Assert.Null(snapshot.Logo); Assert.Null(snapshot.Background);
        Assert.All(bodies, body => Assert.True(body.BytesRead <= SsrsBrandingService.MaxLogoBytes + 8192));
        Assert.All(bodies, body => Assert.True(body.ReadWithCancellation));
    }

    [Fact]
    public async Task OneDeadlineCoversSlowHeadersAndBodiesWithoutRealDelays()
    {
        foreach (var slowBody in new[] { false, true })
        {
            var clock = new DeadlineClock();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(async (_, ct) =>
            {
                if (slowBody)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent(entered) };
                }
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable");
            });
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var lifetime = new BrandingTests.TestLifetime();
            var service = Create(http, clock, new(), lifetime);
            var refresh = service.RefreshAsync();
            await entered.Task;
            clock.ExpireDeadline();
            var snapshot = await refresh;
            Assert.True(snapshot.Degraded); Assert.Null(snapshot.Logo);
        }
    }

    [Fact]
    public async Task RedirectLocationIsNeverFetchedAndUserTokensAreNeverForwarded()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("synthetic.invalid", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization); Assert.Null(request.Content); Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(request.Headers.Contains("Cookie"));
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://unrelated.invalid/credentials");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); using var lifetime = new BrandingTests.TestLifetime();
        var snapshot = await Create(http, new Clock(), new(), lifetime).RefreshAsync();
        Assert.Null(snapshot.Logo); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":\"\"}")]
    public async Task EmptyRestResponsesMeanNoInstalledResource(string json)
    {
        using var handler = new Handler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler); using var lifetime = new BrandingTests.TestLifetime();
        var snapshot = await Create(http, new Clock(), new(), lifetime).RefreshAsync();
        Assert.False(snapshot.Degraded); Assert.Null(snapshot.Logo); Assert.Null(snapshot.Background);
    }

    [Fact]
    public async Task SharedDeadlineRetainsSuccessfulResourceAndLateResponseCannotOverwriteRecovery()
    {
        var clock = new BrandingClock();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var recovery = false;
        using var handler = new Handler(async (request, _) =>
        {
            var fixture = recovery ? SyntheticBranding.Light : SyntheticBranding.Dark;
            if (request.RequestUri!.AbsolutePath.Contains("'colors'", StringComparison.Ordinal) && Interlocked.Increment(ref attempt) == 1)
            {
                entered.TrySetResult(); await late.Task; // Deliberately ignores cancellation.
                lateReturned.TrySetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri.AbsolutePath.Contains("'logo'", StringComparison.Ordinal)
                    ? fixture.Logo! : fixture.Colors!)
            };
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var lifetime = new BrandingTests.TestLifetime();
        var service = Create(http, clock, new(), lifetime);
        var initial = service.GetInitialSnapshotAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        var partial = await initial.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SyntheticBranding.WideLogo, partial.Logo);
        Assert.Null(partial.Background); Assert.True(partial.Degraded);
        recovery = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await service.RefreshAsync();
        Assert.Equal("#E9DDAF", recovered.Background);
        late.SetResult();
        await lateReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(recovered.Version, (await service.GetInitialSnapshotAsync()).Version);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task UnexpectedTransportFailureIsContainedAndNegativeCached()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("private transport details"));
        using var http = new HttpClient(handler);
        using var lifetime = new BrandingTests.TestLifetime();
        var service = Create(http, new Clock(), new(), lifetime);
        Assert.True((await service.GetInitialSnapshotAsync()).Degraded);
        Assert.Null((await service.GetInitialSnapshotAsync()).Logo);
        Assert.Equal(2, handler.Calls);
    }

    private static SsrsBrandingService Create(HttpClient http, TimeProvider clock, CaptureLogger<SsrsBrandingService> logger, BrandingTests.TestLifetime lifetime) =>
        new(http, new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" }, clock, logger, lifetime);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref _calls); return send(request, cancellationToken); }
    }

    private sealed class UnboundedContent : HttpContent
    {
        private readonly InfiniteStream _body = new();
        public int BytesRead => _body.BytesRead;
        public bool ReadWithCancellation => _body.ReadWithCancellation;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => Task.FromResult<Stream>(_body);
    }
    private class InfiniteStream : Stream
    {
        public int BytesRead { get; private set; }
        public bool ReadWithCancellation { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadWithCancellation |= cancellationToken.CanBeCanceled;
            cancellationToken.ThrowIfCancellationRequested(); buffer.Span.Fill(0x41); BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class SlowContent(TaskCompletionSource entered) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => Task.FromResult<Stream>(new SlowStream(entered));
        private sealed class SlowStream(TaskCompletionSource entered) : InfiniteStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0;
            }
        }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback; private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(5), dueTime); _state = state; _callback = callback; return new Timer();
        }
        public void ExpireDeadline() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
