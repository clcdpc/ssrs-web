using System.Net;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class SoapEndpointContractTests
{
    // Fixed Microsoft contract literals, independent of the client and synthetic server.
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ManagementNamespace = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";
    private const string ExecutionNamespace = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";

    [Theory]
    [InlineData("GetProperties", "properties", false)]
    [InlineData("ListChildren", "catalog", false)]
    [InlineData("GetItemParameters", "parameters", false)]
    [InlineData("LoadReport", "execution", true)]
    [InlineData("SetExecutionParameters", "set-execution", true)]
    [InlineData("GetExecutionInfo", "execution-info", true)]
    [InlineData("Render", "render", true)]
    [InlineData("RenderStream", "stream", true)]
    public async Task ClientOperationsUseLiteralDocumentedEndpointsNamespacesAndActions(string operation, string fixture, bool execution)
    {
        foreach (var server in new[] { "https://synthetic.invalid/ReportServer", "https://synthetic.invalid/ReportServer/" })
        {
            var response = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".xml"));
            using var handler = new RecordingHandler(response);
            using var http = new HttpClient(handler);
            var options = new PortalOptions { ReportServer = server };
            using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
            var client = new SsrsClient(transport, options);

            await InvokeAsync(client, operation);

            if (operation == "Render") { Assert.NotEmpty(handler.UserAgent!); }
            else { Assert.Null(handler.UserAgent); }
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal(execution
                ? "https://synthetic.invalid/ReportServer/ReportExecution2005.asmx"
                : "https://synthetic.invalid/ReportServer/ReportService2010.asmx", handler.Endpoint);
            XNamespace ns = execution ? ExecutionNamespace : ManagementNamespace;
            Assert.Equal("\"" + ns.NamespaceName + "/" + operation + "\"", handler.Action);
            Assert.Equal(Soap + "Envelope", handler.Request!.Root!.Name);
            var body = Assert.Single(handler.Request.Root.Element(Soap + "Body")!.Elements());
            Assert.Equal(ns + operation, body.Name);
            Assert.All(body.DescendantsAndSelf(), element => Assert.Equal(ns, element.Name.Namespace));
        }
    }

    [Theory]
    [InlineData("https://synthetic.invalid/ReportServer/ReportService2010.asmx", false, true)]
    [InlineData("https://synthetic.invalid/ReportServer/ReportExecution2005.asmx", true, true)]
    [InlineData("https://synthetic.invalid/ReportServer/ReportingService2010.asmx", false, false)]
    [InlineData("https://synthetic.invalid/ReportServer/ReportService2010.asmx", true, false)]
    [InlineData("https://synthetic.invalid/ReportServer/ReportExecution2005.asmx", false, false)]
    public async Task MockAcceptsOnlyTheDocumentedEndpointForEachService(string endpoint, bool execution, bool accepted)
    {
        using var handler = new SyntheticSsrsHandler();
        using var http = new HttpClient(handler);
        var body = XElement.Parse(execution
            ? "<LoadReport xmlns='http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices'><Report>/Training/Reports/Details</Report></LoadReport>"
            : "<ListChildren xmlns='http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer'><ItemPath>/Training</ItemPath><Recursive>true</Recursive></ListChildren>");
        var xml = new XDocument(new XElement(Soap + "Envelope", new XElement(Soap + "Header"), new XElement(Soap + "Body", body)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", execution
            ? "\"http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices/LoadReport\""
            : "\"http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer/ListChildren\"");

        using var response = await http.SendAsync(request);

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.InternalServerError, response.StatusCode);
        var result = Assert.Single(XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element(Soap + "Body")!.Elements());
        if (accepted)
        {
            Assert.Equal(execution
                ? XName.Get("LoadReportResponse", ExecutionNamespace)
                : XName.Get("ListChildrenResponse", ManagementNamespace), result.Name);
            Assert.Single(handler.Observations);
        }
        else
        {
            Assert.Equal(Soap + "Fault", result.Name);
            Assert.Empty(handler.Observations);
        }
    }

    private static async Task<object> InvokeAsync(SsrsClient client, string operation) => operation switch
    {
        "GetProperties" => await client.VisibleFolderAsync("/Training", default),
        "ListChildren" => await client.ListAsync("/Training", default),
        "GetItemParameters" => await client.ParametersAsync("/Training/Reports/Details", [], true, default),
        "LoadReport" => await client.LoadAsync("/Training/Reports/Details", default),
        "SetExecutionParameters" => await client.SetAsync("literal-execution", [], default),
        "GetExecutionInfo" => await client.InfoAsync("literal-execution", default),
        "Render" => await client.RenderAsync("literal-execution", "HTML5", 1, default),
        "RenderStream" => await client.StreamAsync("literal-execution", "style", default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Endpoint { get; private set; }
        public string? Action { get; private set; }
        public string? UserAgent { get; private set; }
        public XDocument? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Endpoint = request.RequestUri!.AbsoluteUri;
            Action = request.Headers.GetValues("SOAPAction").Single();
            UserAgent = request.Headers.Contains("User-Agent") ? request.Headers.UserAgent.ToString() : null;
            Request = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/xml") };
        }
    }
}
