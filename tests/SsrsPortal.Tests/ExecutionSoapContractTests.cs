using System.Net;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ExecutionSoapContractTests
{
    // Independent of both implementations. See Fixtures/README.md for the Microsoft WSDL evidence.
    private const string ExecutionNamespace = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    private const string LegacyNamespace = "http://schemas.microsoft.com/sqlserver/reporting/2005/06/30/reporting/reportingservices";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Execution = ExecutionNamespace;
    private const string Endpoint = "https://synthetic.invalid/ReportServer/ReportExecution2005.asmx";
    private static readonly string[] Operations = ["LoadReport", "SetExecutionParameters", "GetExecutionInfo", "Render", "RenderStream"];

    [Theory]
    [InlineData("LoadReport")]
    [InlineData("SetExecutionParameters")]
    [InlineData("GetExecutionInfo")]
    [InlineData("Render")]
    [InlineData("RenderStream")]
    public async Task ClientExecutionOperationsMatchMicrosoftContract(string operation)
    {
        var response = await FixtureAsync(operation);
        using var handler = new RecordingHandler(response);
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);

        var result = await InvokeAsync(client, operation);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(Endpoint, handler.Endpoint);
        Assert.Equal("\"" + ExecutionNamespace + "/" + operation + "\"", handler.Action);
        Assert.Equal("text/xml", handler.MediaType);
        Assert.Equal("utf-8", handler.Charset);
        var envelope = handler.Request!.Root!;
        Assert.Equal(Soap + "Envelope", envelope.Name);
        Assert.Equal(new[] { Soap + "Header", Soap + "Body" }, envelope.Elements().Select(e => e.Name));
        var header = envelope.Element(Soap + "Header")!;
        if (operation == "LoadReport")
        {
            Assert.Empty(header.Elements());
            var literal = XDocument.Parse(response);
            Assert.Equal(((ExecutionInfo)result).ExecutionId,
                literal.Root!.Element(Soap + "Header")!.Element(Execution + "ExecutionHeader")!.Element(Execution + "ExecutionID")!.Value);
        }
        else
        {
            AssertXmlEqual(XElement.Parse($"<ExecutionHeader xmlns='{ExecutionNamespace}'><ExecutionID>literal-execution</ExecutionID></ExecutionHeader>"),
                Assert.Single(header.Elements()));
        }
        AssertXmlEqual(RequestBody(operation), Assert.Single(envelope.Element(Soap + "Body")!.Elements()));
        if (result is ExecutionInfo info)
        {
            Assert.Equal(new ExecutionInfo("literal-execution", 0, false, false), info);
        }
        else
        {
            var output = Assert.IsType<RenderOutput>(result);
            Assert.Equal(operation == "Render" ? "<h2>Synthetic</h2>" : "body{color:black}", Encoding.UTF8.GetString(output.Bytes));
            Assert.Equal(operation == "Render" ? "text/html" : "text/css", output.MimeType);
            Assert.Equal(operation == "Render" ? "html" : "", output.Extension);
            Assert.Equal("utf-8", output.Encoding);
            Assert.Equal(operation == "Render" ? new[] { "chart", "style" } : [], output.Streams);
        }
    }

    [Fact]
    public async Task ExecutionParametersPreserveRepeatedValuesAndOmitOnlyNullValueElements()
    {
        using var handler = new RecordingHandler(await FixtureAsync("SetExecutionParameters"));
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);
        await client.SetAsync("literal-execution", [new("choices", "one"), new("choices", "two"), new("note", null), new("blank", "")], default);

        var expected = XElement.Parse($"""
            <SetExecutionParameters xmlns="{ExecutionNamespace}">
              <Parameters>
                <ParameterValue><Name>choices</Name><Value>one</Value></ParameterValue>
                <ParameterValue><Name>choices</Name><Value>two</Value></ParameterValue>
                <ParameterValue><Name>note</Name></ParameterValue>
                <ParameterValue><Name>blank</Name><Value /></ParameterValue>
              </Parameters>
              <ParameterLanguage>en-US</ParameterLanguage>
            </SetExecutionParameters>
            """);
        AssertXmlEqual(expected, Assert.Single(handler.Request!.Root!.Element(Soap + "Body")!.Elements()));
    }

    [Theory]
    [InlineData("LoadReport")]
    [InlineData("SetExecutionParameters")]
    [InlineData("GetExecutionInfo")]
    [InlineData("Render")]
    [InlineData("RenderStream")]
    public async Task ClientRejectsLegacyExecutionResponseNamespace(string operation)
    {
        var legacyResponse = (await FixtureAsync(operation)).Replace(ExecutionNamespace, LegacyNamespace, StringComparison.Ordinal);
        using var http = new HttpClient(new RecordingHandler(legacyResponse));
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var client = new SsrsClient(transport, options);
        Assert.Equal("service", (await Assert.ThrowsAsync<PortalException>(() => InvokeAsync(client, operation))).Code);
    }

    [Fact]
    public async Task SyntheticServerAcceptsMicrosoftContractWithoutProductionClient()
    {
        using var http = new HttpClient(new SyntheticSsrsHandler());
        string? id = null;
        foreach (var operation in Operations)
        {
            using var request = RawRequest(operation, id);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var envelope = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
            Assert.Equal(Soap + "Envelope", envelope.Name);
            var body = Assert.Single(envelope.Element(Soap + "Body")!.Elements());
            Assert.Equal(Execution + (operation + "Response"), body.Name);
            Assert.All(body.DescendantsAndSelf(), e => Assert.Equal(Execution, e.Name.Namespace));
            if (operation == "LoadReport")
            {
                id = envelope.Element(Soap + "Header")!.Element(Execution + "ExecutionHeader")!.Element(Execution + "ExecutionID")!.Value;
                Assert.NotEmpty(id);
            }
            else
            {
                Assert.Empty(envelope.Element(Soap + "Header")!.Elements());
            }
            var outputs = operation switch
            {
                "Render" => new[] { "Result", "Extension", "MimeType", "Encoding", "Warnings", "StreamIds" },
                "RenderStream" => ["Result", "Encoding", "MimeType"],
                _ => ["executionInfo"]
            };
            Assert.Equal(outputs.Select(n => Execution + n), body.Elements().Select(e => e.Name));
            if (operation is "LoadReport" or "SetExecutionParameters" or "GetExecutionInfo")
            {
                Assert.Equal(id, body.Element(Execution + "executionInfo")!.Element(Execution + "ExecutionID")!.Value);
            }
            else
            {
                Assert.NotEmpty(Convert.FromBase64String(body.Element(Execution + "Result")!.Value));
            }
        }
    }

    [Theory]
    [InlineData("body")]
    [InlineData("action")]
    [InlineData("header")]
    public async Task SyntheticServerRejectsLegacyNamespaceInEachWireLocation(string location)
    {
        using var handler = new SyntheticSsrsHandler();
        using var http = new HttpClient(handler);
        using var load = RawRequest("LoadReport", null);
        using var loaded = await http.SendAsync(load);
        loaded.EnsureSuccessStatusCode();
        var id = XDocument.Parse(await loaded.Content.ReadAsStringAsync()).Root!.Element(Soap + "Header")!
            .Element(Execution + "ExecutionHeader")!.Element(Execution + "ExecutionID")!.Value;
        using var request = RawRequest("GetExecutionInfo", id);
        if (location == "action")
        {
            request.Headers.Remove("SOAPAction");
            request.Headers.Add("SOAPAction", "\"" + LegacyNamespace + "/GetExecutionInfo\"");
        }
        else
        {
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            var element = xml.Root!.Element(Soap + (location == "body" ? "Body" : "Header"))!.Elements().Single();
            element.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
            foreach (var node in element.DescendantsAndSelf())
            {
                node.Name = XName.Get(node.Name.LocalName, LegacyNamespace);
            }
            request.Content.Dispose();
            request.Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml");
        }
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element(Soap + "Body")!.Element(Soap + "Fault"));
        Assert.Single(handler.Observations);
    }

    private static XElement RequestBody(string operation) => XElement.Parse(operation switch
    {
        "LoadReport" => $"<LoadReport xmlns='{ExecutionNamespace}'><Report>/Training/Reports/Details</Report></LoadReport>",
        "SetExecutionParameters" => $"<SetExecutionParameters xmlns='{ExecutionNamespace}'><Parameters /><ParameterLanguage>en-US</ParameterLanguage></SetExecutionParameters>",
        "GetExecutionInfo" => $"<GetExecutionInfo xmlns='{ExecutionNamespace}' />",
        "Render" => $"<Render xmlns='{ExecutionNamespace}'><Format>HTML5</Format><DeviceInfo>&lt;DeviceInfo&gt;&lt;HTMLFragment&gt;True&lt;/HTMLFragment&gt;&lt;Section&gt;2&lt;/Section&gt;&lt;/DeviceInfo&gt;</DeviceInfo></Render>",
        "RenderStream" => $"<RenderStream xmlns='{ExecutionNamespace}'><Format>HTML5</Format><StreamID>style</StreamID><DeviceInfo>&lt;DeviceInfo/&gt;</DeviceInfo></RenderStream>",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    });

    private static HttpRequestMessage RawRequest(string operation, string? id)
    {
        var header = new XElement(Soap + "Header");
        if (id != null)
        {
            header.Add(XElement.Parse($"<ExecutionHeader xmlns='{ExecutionNamespace}'><ExecutionID>{id}</ExecutionID></ExecutionHeader>"));
        }
        var xml = new XDocument(new XElement(Soap + "Envelope", header, new XElement(Soap + "Body", RequestBody(operation))));
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") };
        request.Headers.Add("SOAPAction", "\"" + ExecutionNamespace + "/" + operation + "\"");
        return request;
    }

    private static async Task<object> InvokeAsync(SsrsClient client, string operation) => operation switch
    {
        "LoadReport" => await client.LoadAsync("/Training/Reports/Details", default),
        "SetExecutionParameters" => await client.SetAsync("literal-execution", [], default),
        "GetExecutionInfo" => await client.InfoAsync("literal-execution", default),
        "Render" => await client.RenderAsync("literal-execution", "HTML5", 2, default),
        "RenderStream" => await client.StreamAsync("literal-execution", "style", default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static Task<string> FixtureAsync(string operation) => File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", operation switch
    {
        "LoadReport" => "execution.xml", "SetExecutionParameters" => "set-execution.xml", "GetExecutionInfo" => "execution-info.xml",
        "Render" => "render.xml", "RenderStream" => "stream.xml", _ => throw new ArgumentOutOfRangeException(nameof(operation))
    }));

    private static void AssertXmlEqual(XElement expected, XElement actual)
    {
        // Prefix declarations and empty-tag syntax do not affect the contract.
        foreach (var element in new[] { expected, actual })
        {
            element.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
            foreach (var empty in element.DescendantsAndSelf().Where(e => !e.HasElements && e.Value.Length == 0))
            {
                empty.RemoveNodes();
            }
        }
        Assert.True(XNode.DeepEquals(expected, actual), $"Expected {expected}; actual {actual}");
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Endpoint { get; private set; }
        public string? Action { get; private set; }
        public string? MediaType { get; private set; }
        public string? Charset { get; private set; }
        public XDocument? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method; Endpoint = request.RequestUri!.AbsoluteUri;
            Action = request.Headers.GetValues("SOAPAction").Single();
            MediaType = request.Content!.Headers.ContentType!.MediaType;
            Charset = request.Content.Headers.ContentType.CharSet;
            Request = XDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/xml") };
        }
    }
}
