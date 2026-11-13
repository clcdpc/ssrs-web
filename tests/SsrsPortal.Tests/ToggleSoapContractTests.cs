using System.Net;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ToggleSoapContractTests
{
    // Literal contract from MS-RSWSRE2005 revision 7, Appendix A. Do not replace
    // with either implementation's namespace, shape or response constants.
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace E = Ns;
    private static readonly XNamespace S = SoapNs;
    private static string Envelope(string body) => $"<s:Envelope xmlns:s='{SoapNs}'><s:Header/><s:Body>{body}</s:Body></s:Envelope>";
    private static string Response(string value) => Envelope($"<ToggleItemResponse xmlns='{Ns}'>{value}</ToggleItemResponse>");

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData(" false ", false)]
    public async Task LiteralFoundAndIndependentlyAssertedWireContract(string value, bool expected)
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "toggle.xml"));
        using var handler = new Handler(fixture.Replace(">true<", ">" + value + "<", StringComparison.Ordinal));
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);
        Assert.Equal(expected, await client.ToggleAsync("literal-execution", "runtime<&\"id", default));
        Assert.Equal("https://synthetic.invalid/ReportServer/ReportExecution2005.asmx", handler.Uri);
        Assert.Equal("\"" + Ns + "/ToggleItem\"", handler.Action);
        Assert.Equal("text/xml", handler.MediaType);
        Assert.Null(handler.UserAgent);
        var root = handler.Request!.Root!;
        Assert.Equal(S + "Envelope", root.Name);
        var header = Assert.Single(root.Element(S + "Header")!.Elements());
        Assert.Equal(E + "ExecutionHeader", header.Name);
        var executionId = Assert.Single(header.Elements());
        Assert.Equal(E + "ExecutionID", executionId.Name); Assert.Equal("literal-execution", executionId.Value);
        var operation = Assert.Single(root.Element(S + "Body")!.Elements());
        Assert.Equal(E + "ToggleItem", operation.Name);
        var argument = Assert.Single(operation.Elements());
        Assert.Equal(E + "ToggleID", argument.Name); Assert.Equal("runtime<&\"id", argument.Value);
        Assert.False(argument.HasElements); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Found/>")]
    [InlineData("<Found>True</Found>")]
    [InlineData("<Found>yes</Found>")]
    [InlineData("<Found>2</Found>")]
    [InlineData("<Found>&#160;true&#160;</Found>")]
    [InlineData("<Found>true</Found><Found>false</Found>")]
    [InlineData("<Found>true</Found><Unexpected/>")]
    [InlineData("<Found xmlns=''>true</Found>")]
    [InlineData("<Found xmlns='urn:wrong'>true</Found>")]
    [InlineData("<Found><Value>true</Value></Found>")]
    [InlineData("<Found xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:nil='true'/>")]
    [InlineData("unexpected<Found>true</Found>")]
    public async Task MalformedAmbiguousAndUnexpectedFoundFailClosed(string body)
    {
        using var handler = new Handler(Response(body));
        Assert.Equal("service", (await Assert.ThrowsAsync<PortalException>(() => Invoke(handler))).Code);
    }

    [Fact]
    public async Task UnexpectedResponseAttributesAreRejected()
    {
        using var handler = new Handler(Response("<Found>true</Found>").Replace("ToggleItemResponse xmlns", "ToggleItemResponse extra='value' xmlns", StringComparison.Ordinal));
        Assert.Equal("service", (await Assert.ThrowsAsync<PortalException>(() => Invoke(handler))).Code);
    }

    [Theory]
    [InlineData("wrapper-namespace")]
    [InlineData("wrapper-name")]
    [InlineData("multiple-wrappers")]
    [InlineData("fault")]
    [InlineData("transport")]
    [InlineData("oversized")]
    [InlineData("malformed-xml")]
    public async Task TransportAndEnvelopeFailuresAreNotFoundFalse(string kind)
    {
        var response = kind switch
        {
            "wrapper-namespace" => Response("<Found>true</Found>").Replace(Ns, "urn:wrong", StringComparison.Ordinal),
            "wrapper-name" => Response("<Found>true</Found>").Replace("ToggleItemResponse", "ToggleItemResult", StringComparison.Ordinal),
            "multiple-wrappers" => Envelope($"<ToggleItemResponse xmlns='{Ns}'><Found>true</Found></ToggleItemResponse><Extra/>"),
            "fault" => Envelope("<s:Fault><faultcode>s:Server</faultcode><faultstring>private toggle identifier</faultstring></s:Fault>"),
            "oversized" => new string('x', 2049),
            "malformed-xml" => "<broken>",
            _ => Response("<Found>false</Found>")
        };
        using var handler = new Handler(response) { FailTransport = kind == "transport", Status = kind == "fault" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK };
        Assert.Equal(kind == "oversized" ? "limit" : "service",
            (await Assert.ThrowsAsync<PortalException>(() => Invoke(handler, maxBytes: 1024))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeoutDoNotRetry(bool timeout)
    {
        using var handler = new Handler(Response("<Found>true</Found>")) { Delay = 5000 };
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(handler, cancellation.Token, seconds: 1);
        // Cancel only after the handler has entered, rather than racing a 30ms
        // timer against transport startup on a busy test runner.
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout) { Assert.Equal("timeout", (await Assert.ThrowsAsync<PortalException>(() => pending)).Code); }
        else { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nidentifier")]
    public async Task InvalidIdsNeverReachTransport(string id)
    {
        using var handler = new Handler(Response("<Found>true</Found>"));
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => Invoke(handler, toggleId: id))).Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => Invoke(handler, toggleId: new string('x', 257)))).Code);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-action")]
    [InlineData("wrong-namespace")]
    [InlineData("wrong-header")]
    [InlineData("missing-id")]
    [InlineData("duplicate-id")]
    public async Task MockChecksRawMicrosoftRequestWithoutProductionBuilder(string change)
    {
        using var http = new HttpClient(new SyntheticSsrsHandler());
        async Task<HttpResponseMessage> Send(string op, string body, string header, string actionNs = Ns)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://synthetic.invalid/ReportServer/ReportExecution2005.asmx");
            request.Headers.Add("SOAPAction", $"\"{actionNs}/{op}\"");
            request.Content = new StringContent($"<s:Envelope xmlns:s='{SoapNs}'><s:Header>{header}</s:Header><s:Body>{body}</s:Body></s:Envelope>", Encoding.UTF8, "text/xml");
            return await http.SendAsync(request);
        }
        using var load = await Send("LoadReport", $"<LoadReport xmlns='{Ns}'><Report>/Training/Reports/Expandable Groups</Report></LoadReport>", "");
        var id = XDocument.Parse(await load.Content.ReadAsStringAsync()).Root!.Element(S + "Header")!.Element(E + "ExecutionHeader")!.Element(E + "ExecutionID")!.Value;
        var header = $"<ExecutionHeader xmlns='{(change == "wrong-header" ? "urn:wrong" : Ns)}'><ExecutionID>{id}</ExecutionID></ExecutionHeader>";
        var arg = change == "missing-id" ? "" : "<ToggleID>not-found</ToggleID>" + (change == "duplicate-id" ? "<ToggleID>second</ToggleID>" : "");
        using var result = await Send("ToggleItem", $"<ToggleItem xmlns='{(change == "wrong-namespace" ? "urn:wrong" : Ns)}'>{arg}</ToggleItem>", header, change == "wrong-action" ? "urn:wrong" : Ns);
        Assert.Equal(change == "valid" ? HttpStatusCode.OK : HttpStatusCode.InternalServerError, result.StatusCode);
        if (change == "valid")
        {
            var found = XDocument.Parse(await result.Content.ReadAsStringAsync()).Root!.Element(S + "Body")!.Element(E + "ToggleItemResponse")!.Element(E + "Found");
            Assert.Equal("false", found!.Value);
        }
    }

    private static async Task<bool> Invoke(Handler handler, CancellationToken ct = default, int seconds = 30, int maxBytes = 24576, string toggleId = "runtime-id")
    {
        using var http = new HttpClient(handler, false);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer", RequestSeconds = seconds, MaxSoapBytes = maxBytes, Diagnostics = new() { Enabled = true } };
        var logger = new CaptureLogger<SoapTransport>();
        using var transport = new SoapTransport(http, options, logger);
        try { return await new SsrsClient(transport, options).ToggleAsync("literal-execution", toggleId, ct); }
        finally { Assert.DoesNotContain(logger.Messages, m => m.Contains("private toggle identifier", StringComparison.Ordinal) || m.Contains("literal-execution", StringComparison.Ordinal)); }
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Uri { get; private set; }
        public string? Action { get; private set; }
        public string? MediaType { get; private set; }
        public string? UserAgent { get; private set; }
        public XDocument? Request { get; private set; }
        public int Delay { get; init; }
        public bool FailTransport { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Uri = request.RequestUri!.AbsoluteUri; Action = request.Headers.GetValues("SOAPAction").Single();
            MediaType = request.Content!.Headers.ContentType!.MediaType;
            UserAgent = request.Headers.Contains("User-Agent") ? request.Headers.UserAgent.ToString() : null;
            Request = XDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Started.TrySetResult();
            if (Delay > 0) { await Task.Delay(Delay, ct); }
            if (FailTransport) { throw new HttpRequestException("Synthetic transport failure"); }
            return new HttpResponseMessage(Status) { Content = new StringContent(response, Encoding.UTF8, "text/xml") };
        }
    }
}
