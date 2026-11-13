using System.Net;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Services;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ToggleDiagnosticsTests
{
    [Fact]
    public void ContradictoryDuplicateStatesAreCountedAndTheirEmbeddedRuntimeIdsAreScrubbed()
    {
        using var h = new Harness(o => o.Diagnostics.Enabled = true); var logger = new CaptureLogger<HtmlSafety>();
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dev-reportviewer-toggle.html"));
        var expanded = fixture.Replace("aria-expanded=\"false\"", "aria-expanded=\"true\"", StringComparison.Ordinal)
            .Replace("TogglePlus.gif", "ToggleMinus.gif", StringComparison.Ordinal).Replace("Expand 15", "Collapse 15", StringComparison.Ordinal);
        var context = new ToggleRenderContext("/Training/Example", "SsrsPortalAction_" + new string('a', 64), new byte[32], 1, []);
        var safe = new HtmlSafety(h.Options, logger).Sanitize(Encoding.UTF8.GetBytes(fixture + expanded), "portal", "private-execution", [], context);
        Assert.Empty(context.Actions); Assert.DoesNotContain("230iT0R0x11", safe);
        Assert.Contains("0 registered controls", Assert.Single(logger.Messages)); Assert.Contains("2 state", Assert.Single(logger.Messages));
    }

    [Fact]
    public void RecognitionDiagnosticsSeparateSyntaxIconAndRendererCountsWithoutSourceValues()
    {
        using var h = new Harness(o => o.Diagnostics.Enabled = true);
        var logger = new CaptureLogger<HtmlSafety>();
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dev-reportviewer-toggle.html"));
        var context = new ToggleRenderContext("/Training/Example", "SsrsPortalAction_" + new string('a', 64), new byte[32], 1, []);
        var legacy = "<a onclick=\"" + context.Callback + "('Toggle','legacy-id',event);return false;\"><img src='/ReportServer?rs:Command=Get&amp;rc:GetImage=8.0.0TogglePlus.gif'></a>";
        var url = "<a href='/ReportServer?%2FTraining%2FExample&amp;rs:SessionID=private-execution&amp;rs:ShowHideToggle=url-id'><img src='/ReportServer?rs:Command=Get&amp;rc:GetImage=8.0.0TogglePlus.gif'></a>";
        var html = fixture + legacy + url + fixture.Replace("InvokeReportAction", "OtherMethod", StringComparison.Ordinal) +
            fixture.Replace("RendererResources.TogglePlus.gif", "RendererResources.Sort.gif", StringComparison.Ordinal);
        new HtmlSafety(h.Options, logger).Sanitize(Encoding.UTF8.GetBytes(html), "portal", "private-execution", [], context);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("3 registered controls, 1 ActionScript, 1 ShowHideToggle URL, 1 ReportViewer InvokeReportAction", message);
        Assert.Contains("1 syntax, 1 icon/resource", message);
        foreach (var secret in new[] { "230iT0R0x11", "private-execution", "ReportViewerControl_ctl09", "legacy-id", "url-id", "Expand 15", "ReportServer", context.Callback })
        { Assert.DoesNotContain(secret, message); }
    }

    [Theory]
    [InlineData("ToggleItem")]
    [InlineData("GetExecutionInfo")]
    [InlineData("Render")]
    [InlineData("RenderStream")]
    [InlineData("Export")]
    public async Task InteractionFaultsCannotEchoReportDataOrIdentifiersEvenWhenDiagnosticsEnabled(string operation)
    {
        EchoHandler? echo = null;
        using var h = new Harness(o => o.Diagnostics.Enabled = true, wrapHandler: inner => echo = new EchoHandler(inner));
        var report = await h.Report("Expandable Groups");
        var run = await h.Execution.RunAsync("owner", report.Id, new(null, Initial: true), default);
        using var lease = await h.Sessions.AcquireAsync(run.Token, "owner", default);
        Assert.True(lease.Session.HasToggleControls);
        echo!.Operation = operation;
        PortalException error;
        if (operation == "RenderStream")
        {
            error = await Assert.ThrowsAsync<PortalException>(() => h.Execution.StreamAsync(lease, lease.Session.Streams.Keys.First()));
        }
        else if (operation == "Export")
        {
            error = await Assert.ThrowsAsync<PortalException>(() => h.Execution.ExportAsync(lease, OutputFormat.All["csv"]));
        }
        else
        {
            error = await Assert.ThrowsAsync<PortalException>(() => h.Execution.ToggleAsync(lease, lease.Session.ToggleActions.Keys.First()));
        }
        var details = FailureDiagnostics.Format(error, "test-reference");
        var logs = string.Join('\n', h.Logger.Messages);
        foreach (var text in new[] { details, logs })
        {
            Assert.DoesNotContain(lease.Session.ExecutionId, text);
            Assert.DoesNotContain("1iT0", text); Assert.DoesNotContain("REPORT_VALUE_SENTINEL", text);
            Assert.DoesNotContain("faultstring", text);
            Assert.DoesNotContain("<s:Envelope", text);
        }
        Assert.Contains("test-reference", details);
        Assert.DoesNotContain(h.Options.ReportServer, details);
        Assert.Contains("HTTP Status: 500", details);
        Assert.Contains("privacy", details);
    }

    [Fact]
    public async Task MalformedXmlExceptionMessagesCannotLeakIdentifiersInToggleDiagnostics()
    {
        const string response = "<Found>&PRIVATE_TOGGLE_SENTINEL;</Found>";
        using var http = new HttpClient(new LiteralHandler(response));
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer", Diagnostics = new() { Enabled = true } };
        var logger = new CaptureLogger<SoapTransport>(); using var transport = new SoapTransport(http, options, logger);
        var error = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).ToggleAsync("private-execution", "runtime-toggle", default));
        Assert.DoesNotContain("PRIVATE_TOGGLE_SENTINEL", string.Join('\n', logger.Messages));
        Assert.DoesNotContain("PRIVATE_TOGGLE_SENTINEL", FailureDiagnostics.Format(error, "test-reference"));
    }

    private sealed class EchoHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string? Operation { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = xml.Root!.Elements().Single(e => e.Name.LocalName == "Body").Elements().Single();
            var operation = body.Name.LocalName;
            if (operation == "Render" && body.Elements().Single(e => e.Name.LocalName == "Format").Value != "HTML5") { operation = "Export"; }
            if (operation != Operation) { return await base.SendAsync(request, ct); }
            var id = xml.Descendants().Single(e => e.Name.LocalName == "ExecutionID").Value;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><s:Fault><faultcode>s:Server</faultcode><faultstring>REPORT_VALUE_SENTINEL {id} 1iT0</faultstring><detail>REPORT_VALUE_SENTINEL</detail></s:Fault></s:Body></s:Envelope>", Encoding.UTF8, "text/xml")
            };
        }
    }
    private sealed class LiteralHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(response, Encoding.UTF8, "text/xml") });
    }
}
