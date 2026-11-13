using System.Net;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class Html5RenderingSoapTests
{
    // Independent, literal value from the operator's successful DEV experiment.
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string Callback = "SsrsPortalAction_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly XNamespace E = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    private static readonly XNamespace S = "http://schemas.xmlsoap.org/soap/envelope/";

    [Theory]
    [InlineData(null)]
    [InlineData(Callback)]
    public async Task Html5UsesVerifiedHttpHeaderAndPreservesSoapAndDeviceSettings(string? callback)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        await new SsrsClient(transport, options).RenderAsync("literal-execution", "HTML5", 3, default, callback);

        Assert.Equal(BrowserUserAgent, handler.UserAgent);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://synthetic.invalid/ReportServer/ReportExecution2005.asmx", handler.Endpoint);
        Assert.Equal("\"" + E + "/Render\"", handler.Action);
        Assert.Equal("text/xml; charset=utf-8", handler.ContentType);
        var root = handler.Request!.Root!;
        Assert.Equal("literal-execution", root.Element(S + "Header")!.Element(E + "ExecutionHeader")!.Element(E + "ExecutionID")!.Value);
        var body = Assert.Single(root.Element(S + "Body")!.Elements());
        Assert.Equal(E + "Render", body.Name);
        Assert.Equal(new[] { E + "Format", E + "DeviceInfo" }, body.Elements().Select(e => e.Name));
        Assert.Equal("HTML5", body.Element(E + "Format")!.Value);
        var device = XElement.Parse(body.Element(E + "DeviceInfo")!.Value);
        Assert.Equal(callback is null ? new[] { "HTMLFragment", "Section" } : ["HTMLFragment", "Section", "ActionScript"],
            device.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("True", device.Element("HTMLFragment")!.Value);
        Assert.Equal("3", device.Element("Section")!.Value);
        Assert.Equal(callback, device.Element("ActionScript")?.Value);
        Assert.Empty(http.DefaultRequestHeaders.UserAgent);
    }

    [Theory]
    [InlineData("PDF")]
    [InlineData("EXCELOPENXML")]
    [InlineData("WORDOPENXML")]
    [InlineData("CSV")]
    public async Task ExportsAfterInteractiveRenderDoNotInheritHeaderOrDeviceSettings(string format)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);
        await client.RenderAsync("literal-execution", "HTML5", 2, default, Callback);
        Assert.Equal(BrowserUserAgent, handler.UserAgent);
        await client.RenderAsync("literal-execution", format, 2, default, Callback);
        Assert.Null(handler.UserAgent);
        Assert.Equal(format, handler.Request!.Descendants(E + "Format").Single().Value);
        Assert.Equal("<DeviceInfo/>", handler.Request.Descendants(E + "DeviceInfo").Single().Value);
        Assert.Empty(http.DefaultRequestHeaders.UserAgent);
    }

    [Fact]
    public async Task Html5HeaderIsDeterministicWithoutChangingSharedHttpClientDefaults()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SyntheticCaller/1.0");
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);
        await client.RenderAsync("literal-execution", "HTML5", 1, default, Callback);
        Assert.Equal(BrowserUserAgent, handler.UserAgent);
        await client.RenderAsync("literal-execution", "PDF", 1, default);
        Assert.Equal("SyntheticCaller/1.0", handler.UserAgent);
        Assert.Equal("SyntheticCaller/1.0", http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Theory]
    [InlineData("normal", false)]
    [InlineData("normal", true)]
    [InlineData("reportviewer", false)]
    [InlineData("reportviewer", true)]
    public async Task SyntheticRendererOmitsInteractiveControlsWhenRequiredHeaderIsRemoved(string behavior, bool stripHeader)
    {
        using var mock = new SyntheticSsrsHandler();
        using var filter = new HeaderFilter(mock, stripHeader);
        using var http = new HttpClient(filter);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);
        var execution = await client.LoadAsync(SyntheticSsrsHandler.Expandable, default);
        await client.SetAsync(execution.ExecutionId, [new("initiallyExpanded", "false"), new("behavior", behavior)], default);
        var result = await client.RenderAsync(execution.ExecutionId, "HTML5", 1, default, Callback);
        var html = Encoding.UTF8.GetString(result.Bytes);
        var document = new HtmlParser().ParseDocument(html);
        Assert.Equal(!stripHeader, document.QuerySelectorAll("a[onclick] > img").Length > 0);
        Assert.Equal(!stripHeader, html.Contains(Callback, StringComparison.Ordinal));
        Assert.Equal(!stripHeader, html.Contains("TogglePlus", StringComparison.Ordinal));
        if (behavior == "reportviewer") { Assert.Contains("15", document.QuerySelector("table.system-items")!.TextContent); }
        Assert.Equal(stripHeader ? "" : BrowserUserAgent, Assert.Single(mock.Observations, o => o.Operation == "Render").UserAgent);
    }

    private sealed class HeaderFilter(HttpMessageHandler inner, bool stripHeader) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (stripHeader) { request.Headers.Remove("User-Agent"); }
            return base.SendAsync(request, ct);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Endpoint { get; private set; }
        public string? Action { get; private set; }
        public string? ContentType { get; private set; }
        public XDocument? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgent = request.Headers.Contains("User-Agent") ? request.Headers.UserAgent.ToString() : null;
            Method = request.Method;
            Endpoint = request.RequestUri!.AbsoluteUri;
            Action = request.Headers.GetValues("SOAPAction").Single();
            ContentType = request.Content!.Headers.ContentType!.ToString();
            Request = XDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render.xml"), ct), Encoding.UTF8, "text/xml")
            };
        }
    }
}
