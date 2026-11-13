using System.Buffers.Binary;
using System.Net;
using AngleSharp.Html.Parser;

namespace SsrsPortal.Tests;

[CollectionDefinition("Static file lifetime", DisableParallelization = true)]
public sealed class StaticFileLifetimeCollection { }

// Isolate the Linux descriptor measurement from other concurrently starting hosts.
[Collection("Static file lifetime")]
public sealed class StaticFileLifetimeTests
{
    [Fact]
    public async Task DisposedRazorHostsReleaseVersionWatchersAndContinueServingRealAssets()
    {
        var before = InotifyHandles();
        for (var i = 0; i < 8; i++)
        {
            await using var factory = new PortalFactory();
            using var browser = factory.Browser();
            var page = new HtmlParser().ParseDocument(await browser.GetStringAsync("/TestLogin"));
            var favicon = page.QuerySelector("link[rel=icon]")!.GetAttribute("href")!;
            Assert.StartsWith("/favicon.ico?v=", favicon);
            using var iconResponse = await browser.GetAsync(favicon);
            Assert.Equal(HttpStatusCode.OK, iconResponse.StatusCode);
            Assert.Equal("image/x-icon", iconResponse.Content.Headers.ContentType?.MediaType);
            var icon = await iconResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(0, 2)));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2, 2)));
            Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4, 2)));
            Assert.Equal(new byte[] { 16, 24, 32, 48, 64, 128, 0 }, Enumerable.Range(0, 7).Select(index => icon[6 + (index * 16)]).ToArray());
            for (var index = 0; index < 7; index++)
            {
                var entry = 6 + (index * 16);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(entry + 8, 4));
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(entry + 12, 4));
                Assert.True(offset + length <= icon.Length);
                Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, icon.AsSpan((int)offset, 8).ToArray());
            }
            var css = page.QuerySelector("link[rel=stylesheet]")!.GetAttribute("href")!;
            Assert.StartsWith("/css/site.css?v=", css);
            using var asset = await browser.GetAsync(css);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Contains("#copy-report-link", await asset.Content.ReadAsStringAsync());
        }
        if (before.HasValue)
        {
            // Watcher's asynchronous read may finish just after host disposal.
            for (var attempt = 0; attempt < 100 && InotifyHandles() > before; attempt++) { await Task.Delay(10); }
            Assert.True(InotifyHandles() <= before, "Disposed hosts must not accumulate inotify handles for static-file versioning.");
        }
    }

    private static int? InotifyHandles() => OperatingSystem.IsLinux()
        ? Directory.EnumerateFiles("/proc/self/fd").Count(path => new FileInfo(path).LinkTarget == "anon_inode:inotify")
        : null;
}
