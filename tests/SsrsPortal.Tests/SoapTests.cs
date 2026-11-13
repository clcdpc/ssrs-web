using System.Net;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;
namespace SsrsPortal.Tests;

public sealed class SoapTests
{
    [Fact]
    public async Task LiteralMicrosoftWireFixturesParseNamedOutputsChoicesDependenciesAndNilDefaults()
    {
        var handler = new LiteralHandler(); using var http = new HttpClient(handler);
        using var h = new Harness();
        using var transport = new SoapTransport(http, h.Options, h.Logger);
        var soap = new SsrsClient(transport, h.Options);
        Assert.True(await soap.VisibleFolderAsync("/Training", default));
        Assert.Equal("Example", (await soap.ListAsync("/Training", default)).Single().Name);
        var parameters = await soap.ParametersAsync("/Training/Example", [new("applyFilter", "false"), new("hiddenNote", null)], true, default);
        Assert.Equal(new[] { "library", "applyFilter", "hiddenNote" }, parameters.Select(p => p.Name));
        Assert.Equal("Integer", parameters[0].Type); Assert.True(parameters[0].MultiValue); Assert.True(parameters[0].QueryDefault);
        Assert.Equal("Synthetic library", parameters[0].Options.Single().Label); Assert.Equal("2", parameters[0].Defaults.Single());
        Assert.Equal("HasOutstandingDependencies", parameters[1].State); Assert.Equal("library", parameters[1].Dependencies.Single());
        Assert.Equal("false", parameters[1].Defaults.Single()); Assert.True(parameters[2].Hidden); Assert.Null(parameters[2].Defaults.Single());
        var info = await soap.LoadAsync("/Training/Example", default); Assert.Equal("literal-execution", info.ExecutionId); Assert.Equal(0, info.NumPages);
        Assert.Equal(info.ExecutionId, (await soap.SetAsync(info.ExecutionId, [new("applyFilter", "false")], default)).ExecutionId);
        Assert.Equal(info.ExecutionId, (await soap.InfoAsync(info.ExecutionId, default)).ExecutionId);
        var output = await soap.RenderAsync(info.ExecutionId, "HTML5", 2, default); Assert.Equal("<h2>Synthetic</h2>", Encoding.UTF8.GetString(output.Bytes));
        Assert.Equal(new[] { "chart", "style" }, output.Streams);
        var stream = await soap.StreamAsync(info.ExecutionId, "style", default); Assert.Equal("text/css", stream.MimeType); Assert.NotEmpty(stream.Bytes);
        var request = handler.Requests.Single(x => x.Root!.Element(SoapTransport.Envelope + "Body")!.Elements().Single().Name.LocalName == "GetItemParameters");
        Assert.DoesNotContain(request.Descendants(), x => x.Name.LocalName is "HistoryID" or "Credentials");
        Assert.Equal("false", request.Descendants(SoapTransport.Management + "Value").Single().Value);
        Assert.All(handler.Requests.Where(x => x.Descendants(SoapTransport.Execution + "Render").Any() || x.Descendants(SoapTransport.Execution + "RenderStream").Any()),
            x => Assert.Equal(info.ExecutionId, x.Descendants(SoapTransport.Execution + "ExecutionID").Single().Value));
        var properties = handler.Requests.Single(x => x.Descendants(SoapTransport.Management + "GetProperties").Any());
        Assert.Equal("Hidden", properties.Descendants(SoapTransport.Management + "Property").Single().Element(SoapTransport.Management + "Name")!.Value);
    }

    [Theory]
    [InlineData(MockFailure.Unauthorized, "credentials")]
    [InlineData(MockFailure.Forbidden, "credentials")]
    [InlineData(MockFailure.Redirect, "service")]
    [InlineData(MockFailure.Fault, "service")]
    [InlineData(MockFailure.Malformed, "service")]
    [InlineData(MockFailure.Dtd, "service")]
    [InlineData(MockFailure.Oversized, "limit")]
    [InlineData(MockFailure.Unavailable, "service")]
    public async Task TransportBoundsAndFailureCategoriesAreSafe(MockFailure failure, string code)
    {
        using var h = new Harness(); h.Mock.Failure = failure;
        var ex = await Assert.ThrowsAsync<PortalException>(() => h.Soap.ListAsync("/Training", default));
        Assert.Equal(code, ex.Code); Assert.Single(h.Mock.Observations);
        Assert.DoesNotContain("private", ex.Message);
        Assert.Contains(h.Logger.Messages, message => message.Contains("Portal failure", StringComparison.Ordinal));
        if (failure == MockFailure.Fault) { Assert.Contains(h.Logger.Messages, message => message.Contains("private SQL", StringComparison.Ordinal)); }
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationReleaseSoapConcurrencySlots()
    {
        using var h = new Harness(); h.Options.RequestSeconds = 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
        try
        {
            Assert.Equal("timeout", (await Assert.ThrowsAsync<PortalException>(() => h.Soap.ListAsync("/Training", default))).Code);
            Assert.Contains(h.Logger.Messages, m => m.Contains("stopped with timeout", StringComparison.Ordinal));
            h.Options.RequestSeconds = 30;
            using var cts = new CancellationTokenSource();
            var cancelled = h.Soap.ListAsync("/Training", cts.Token);
            await CatalogCacheTests.WaitUntil(() => h.Mock.ActiveCalls == 1);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Contains(h.Logger.Messages, m => m.Contains("stopped with cancelled", StringComparison.Ordinal));
            Assert.Equal(0, h.Mock.ActiveCalls);
            h.Mock.TargetResponseGates.Clear(); Assert.NotEmpty(await h.Soap.ListAsync("/Training", default));
        }
        finally
        {
            h.Mock.TargetResponseGates.Clear(); gate.TrySetResult();
        }
    }

    [Fact]
    public async Task ChunkedResponseCannotBypassByteBounds()
    {
        using var h = new Harness(); h.Options.MaxSoapBytes = 1024;
        using var http = new HttpClient(new ChunkedHandler()); using var transport = new SoapTransport(http, h.Options, h.Logger);
        Assert.Equal("limit", (await Assert.ThrowsAsync<PortalException>(() => transport.CallAsync("ListChildren", false, [], null, default))).Code);
    }

    private sealed class LiteralHandler : HttpMessageHandler
    {
        public List<XDocument> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Requests.Add(xml);
            var op = xml.Root!.Element(SoapTransport.Envelope + "Body")!.Elements().Single().Name.LocalName;
            var file = op switch { "GetProperties" => "properties", "ListChildren" => "catalog", "GetItemParameters" => "parameters", "LoadReport" => "execution", "SetExecutionParameters" => "set-execution", "GetExecutionInfo" => "execution-info", "Render" => "render", "RenderStream" => "stream", _ => throw new InvalidOperationException() };
            var response = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", file + ".xml"), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/xml") };
        }
    }
    private sealed class ChunkedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new MemoryStream(new byte[2048]));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
