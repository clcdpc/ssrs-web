using System.Net;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ManagementSoapContractTests
{
    private const string Management = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";
    private const string Execution = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    [Theory]
    [InlineData("False", true)]
    [InlineData("false", true)]
    [InlineData("True", false)]
    [InlineData("true", false)]
    public async Task GetPropertiesUsesPropertiesInRequestAndValuesInResponse(string value, bool visible)
    {
        using var handler = new LiteralSoapHandler(Envelope("GetProperties", "<Values><Property><Name>Hidden</Name><Value>" + value + "</Value></Property></Values>"));
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        Assert.Equal(visible, await new SsrsClient(transport, options).VisibleFolderAsync("/Training", default));
        XNamespace ns = Management;
        var request = Assert.Single(handler.Request!.Root!.Element(Soap + "Body")!.Elements());
        Assert.Equal(ns + "GetProperties", request.Name);
        Assert.Equal(new[] { ns + "ItemPath", ns + "Properties" }, request.Elements().Select(e => e.Name));
        Assert.Equal("/Training", request.Element(ns + "ItemPath")!.Value);
        var property = Assert.Single(request.Element(ns + "Properties")!.Elements());
        Assert.Equal(ns + "Property", property.Name);
        Assert.Equal(ns + "Name", Assert.Single(property.Elements()).Name);
        Assert.Equal("Hidden", property.Value);
        Assert.DoesNotContain(request.Descendants(), e => e.Name.LocalName == "Values");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Properties><Property><Name>Hidden</Name><Value>False</Value></Property></Properties>")]
    [InlineData("<Values/>")]
    [InlineData("<Values><Property><Name>Hidden</Name></Property></Values>")]
    [InlineData("<Values><Property><Name>Other</Name><Value>False</Value></Property></Values>")]
    [InlineData("<Values><Property><Name>Hidden</Name><Value>invalid</Value></Property></Values>")]
    [InlineData("<Values><Property><Name>Hidden</Name><Value>False</Value></Property><Unexpected/></Values>")]
    [InlineData("<Values xmlns='urn:wrong'><Property><Name>Hidden</Name><Value>False</Value></Property></Values>")]
    [InlineData("<Values/><Values/>")]
    public async Task MissingOrMalformedValuesAndLegacyPropertiesFailWithOperationContext(string values)
    {
        using var handler = new LiteralSoapHandler(Envelope("GetProperties", values));
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var exception = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).VisibleFolderAsync("/Training", default));
        Assert.Equal("service", exception.Code);
        Assert.Contains("Values", exception.Message);
        Assert.Equal("GetProperties", FailureDiagnostics.Find(exception)!.Operation);
        Assert.Equal("response-contract", FailureDiagnostics.Find(exception)!.Failure);
    }

    [Fact]
    public async Task SyntheticGetPropertiesReturnsLiteralDocumentedValuesWrapper()
    {
        using var handler = new SyntheticSsrsHandler();
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://synthetic.invalid/ReportServer/ReportService2010.asmx")
        {
            Content = new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Header/><s:Body><GetProperties xmlns='" + Management + "'><ItemPath>/Training</ItemPath><Properties><Property><Name>Hidden</Name></Property></Properties></GetProperties></s:Body></s:Envelope>", Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", "\"" + Management + "/GetProperties\"");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        XNamespace ns = Management;
        var result = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element(Soap + "Body")!.Element(ns + "GetPropertiesResponse")!;
        Assert.Equal(ns + "Values", Assert.Single(result.Elements()).Name);
        Assert.Equal("False", result.Element(ns + "Values")!.Element(ns + "Property")!.Element(ns + "Value")!.Value);
        Assert.Null(result.Element(ns + "Properties"));
    }

    [Fact]
    public async Task DocumentedXmlBooleanAndNilNumericLexicalFormsAreHonored()
    {
        var parameters = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parameters.xml"));
        parameters = parameters.Replace(">true<", "> 1 <", StringComparison.Ordinal).Replace(">false<", "> 0 <", StringComparison.Ordinal)
            .Replace("nil=\"true\"", "nil=\"1\"", StringComparison.Ordinal);
        using var handler = new LiteralSoapHandler(parameters);
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var result = await new SsrsClient(transport, options).ParametersAsync("/Training/Example", [], true, default);
        Assert.True(result[0].MultiValue);
        Assert.True(result[0].QueryDefault);
        Assert.False(result[0].Nullable);
        Assert.False(result[0].Hidden);
        Assert.Null(result[2].Defaults.Single());
    }

    [Theory]
    [InlineData("1", "0", true, false)]
    [InlineData("0", "1", false, true)]
    [InlineData(" 1 ", " 0 ", true, false)]
    public async Task ExecutionFlagsHonorNumericXmlBooleans(string credentials, string parameters, bool needsCredentials, bool needsParameters)
    {
        using var handler = new LiteralSoapHandler(Envelope("GetExecutionInfo", "<executionInfo><ExecutionID>example</ExecutionID><NumPages>0</NumPages><CredentialsRequired>" + credentials + "</CredentialsRequired><ParametersRequired>" + parameters + "</ParametersRequired></executionInfo>", Execution));
        using var http = new HttpClient(handler);
        var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer" };
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var result = await new SsrsClient(transport, options).InfoAsync("example", default);
        Assert.Equal(needsCredentials, result.CredentialsRequired);
        Assert.Equal(needsParameters, result.ParametersRequired);
    }

    internal static string Envelope(string operation, string members, string ns = Management) =>
        "<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><" + operation + "Response xmlns='" + ns + "'>" + members + "</" + operation + "Response></s:Body></s:Envelope>";
}

// Literal upstream responses, independent of the synthetic SSRS implementation.
internal sealed class LiteralSoapHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string media = "text/xml") : HttpMessageHandler
{
    public XDocument? Request { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, media) };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "UPSTREAM_COOKIE_SENTINEL=private");
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Negotiate AUTH_CHALLENGE_SENTINEL");
        return response;
    }
}
