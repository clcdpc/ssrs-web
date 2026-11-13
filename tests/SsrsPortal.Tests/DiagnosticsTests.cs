using System.Net;
using System.Security.Authentication;
using System.Security.Claims;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using SsrsPortal.Configuration;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Security;
using SsrsPortal.Services;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class DiagnosticsTests
{
    private const string Fault = "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body><soap:Fault><faultcode>soap:Server</faultcode><faultstring>rsAccessDenied: The service account cannot read this folder. &lt;script&gt;alert('fault')&lt;/script&gt;</faultstring><detail><ErrorCode>rsAccessDenied</ErrorCode><Message>Permission denied for folder /Training</Message><More>&lt;script&gt;alert('upstream')&lt;/script&gt;</More></detail></soap:Fault></soap:Body></soap:Envelope>";

    [Fact]
    public async Task DebugServerDiagnosticsAreAvailableByDefaultAndBrowserDetailsRemainDisabled()
    {
        await using var factory = new PortalFactory();
        factory.ConfigurationFile.Document["Portal"]!.AsObject().Remove("Diagnostics");
        factory.ConfigurationFile.Save();
        using var user = await factory.Login();
        var options = factory.Services.GetRequiredService<PortalOptions>();
        Assert.False(options.Diagnostics.Enabled);
        Assert.True(options.Diagnostics.DetailedFailuresAtDebug);
        Assert.False(options.Diagnostics.ShowDetailsInBrowser);
        factory.Mock.Failure = MockFailure.Fault;
        var response = await user.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("private SQL", await response.Content.ReadAsStringAsync());
        Assert.Contains("private SQL", string.Join('\n', factory.Logs.Messages));
    }

    [Fact]
    public async Task ExplicitDiagnosticsLogBoundedParameterContextWithoutSuccessfulReportOutput()
    {
        await using var factory = Factory(true, false, "127.0.0.1");
        using var user = await factory.Login();
        var input = Harness.Inputs(); input.Values!["note"] = ["RAW_SELECTION_SENTINEL"];
        using var response = await user.Post("/api/reports/" + await user.Id() + "/run", input);
        response.EnsureSuccessStatusCode();
        var logs = string.Join('\n', factory.Logs.Messages);
        foreach (var value in new[] { "Synthetic library", "synthetic-execution", "Demonstration row", "<Render", "<ParameterValue" })
        {
            Assert.DoesNotContain(value, logs);
        }
        Assert.Contains("SOAP operation Render completed", logs);
    }

    [Theory]
    [InlineData(401, "text/html", "Denied by Windows", "credentials", "http-credentials")]
    [InlineData(403, "text/xml", "<denied/>", "credentials", "http-credentials")]
    [InlineData(302, "text/html", "Unexpected redirect to login", "service", "redirect")]
    [InlineData(200, "text/html", "<html>Wrong endpoint sentinel</html>", "service", "content-type")]
    [InlineData(200, "text/xml", "<broken>XML failure sentinel", "service", "xml")]
    [InlineData(200, "text/xml", "<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><WrongResponse xmlns='urn:wrong'/></s:Body></s:Envelope>", "service", "response-contract")]
    public async Task TransportFailuresRetainHttpContextAndBoundedActualResponse(int status, string media, string body, string code, string failure)
    {
        using var handler = new LiteralSoapHandler(body, (HttpStatusCode)status, media);
        using var http = new HttpClient(handler);
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>(), Accessor());
        var exception = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).ListAsync("/Training", default));
        Assert.Equal(code, exception.Code);
        var diagnostic = FailureDiagnostics.Find(exception)!;
        Assert.Equal(status, diagnostic.HttpStatus);
        Assert.Equal(failure, diagnostic.Failure);
        Assert.StartsWith(media, diagnostic.ContentType);
        Assert.Equal("ListChildren", diagnostic.Operation);
        Assert.Equal("request-reference", diagnostic.Trace);
        Assert.Equal("https://synthetic.invalid/ReportServer/ReportService2010.asmx", diagnostic.Endpoint);
        Assert.True(diagnostic.Milliseconds >= 0);
        Assert.NotEmpty(diagnostic.ResponseExcerpt!);
        if (failure == "xml") { Assert.IsType<XmlException>(exception.InnerException); }
        var details = FailureDiagnostics.Format(exception, "request-reference");
        Assert.Contains("request-reference", details);
        Assert.DoesNotContain("UPSTREAM_COOKIE_SENTINEL", details);
        Assert.DoesNotContain("AUTH_CHALLENGE_SENTINEL", details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http500SoapFaultRetainsActualCodeMessageDetailAndOriginalXmlOnlyWhenOptedIn(bool enabled)
    {
        using var handler = new LiteralSoapHandler(Fault, HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        var options = Options(enabled);
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>(), Accessor());
        var exception = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).VisibleFolderAsync("/Training", default));
        var diagnostic = FailureDiagnostics.Find(exception)!;
        Assert.Equal("service", exception.Code);
        Assert.Equal(500, diagnostic.HttpStatus);
        Assert.Equal("soap-fault", diagnostic.Failure);
        if (enabled)
        {
            Assert.Equal("soap:Server", diagnostic.FaultCode);
            Assert.Contains("rsAccessDenied", diagnostic.FaultMessage);
            Assert.Contains("<ErrorCode>rsAccessDenied</ErrorCode>", diagnostic.FaultDetail);
            Assert.Contains("faultstring", diagnostic.FaultXml);
            Assert.Contains("Permission denied for folder /Training", FailureDiagnostics.Format(exception, "request-reference"));
        }
        else
        {
            Assert.Null(diagnostic.FaultCode); Assert.Null(diagnostic.FaultMessage);
            Assert.Null(diagnostic.FaultDetail); Assert.Null(diagnostic.FaultXml);
            Assert.DoesNotContain("rsAccessDenied", FailureDiagnostics.Format(exception, "request-reference"));
        }
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task ConnectionAndTlsFailuresPreserveOriginalExceptionObjectsMessagesAndStackTraces(HttpRequestError error)
    {
        var cause = new AuthenticationException("certificate chain validation sentinel", new InvalidOperationException("root cause sentinel"));
        var original = new HttpRequestException(error, "connection failure sentinel", cause);
        using var http = new HttpClient(new ThrowingHandler(original));
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>(), Accessor());
        var exception = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).ListAsync("/Training", default));
        Assert.Same(original, exception.InnerException);
        Assert.Same(cause, exception.InnerException!.InnerException);
        Assert.Contains(nameof(ThrowingHandler), original.StackTrace);
        var text = FailureDiagnostics.Format(exception, "request-reference");
        foreach (var expected in new[] { "HttpRequestException", "AuthenticationException", "InvalidOperationException", "connection failure sentinel", "certificate chain validation sentinel", "root cause sentinel", nameof(ThrowingHandler) })
        {
            Assert.Contains(expected, text);
        }
        Assert.Equal(error.ToString(), FailureDiagnostics.Find(exception)!.Failure);
    }

    [Fact]
    public async Task TimeoutRetainsCancellationCauseAndOperationContext()
    {
        using var http = new HttpClient(new WaitingHandler());
        var options = Options(); options.RequestSeconds = 1;
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>(), Accessor());
        var exception = await Assert.ThrowsAsync<PortalException>(() => transport.CallAsync("GetProperties", false, [], null, default));
        Assert.Equal("timeout", exception.Code); Assert.Equal(504, exception.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal("GetProperties", FailureDiagnostics.Find(exception)!.Operation);
        Assert.True(FailureDiagnostics.Find(exception)!.Milliseconds >= 500);
    }

    [Theory]
    [InlineData("ListChildren", false, "CatalogItems")]
    [InlineData("GetItemParameters", false, "Parameters")]
    [InlineData("LoadReport", true, "executionInfo")]
    [InlineData("SetExecutionParameters", true, "executionInfo")]
    [InlineData("GetExecutionInfo", true, "executionInfo")]
    [InlineData("Render", true, "Result")]
    [InlineData("RenderStream", true, "Result")]
    public async Task MissingConsumedResponseMembersRetainUsefulContractDiagnostics(string operation, bool execution, string member)
    {
        var ns = execution ? "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices" : "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";
        using var handler = new LiteralSoapHandler(ManagementSoapContractTests.Envelope(operation, "", ns));
        using var http = new HttpClient(handler);
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>(), Accessor());
        var client = new SsrsClient(transport, options);
        var exception = await Assert.ThrowsAsync<PortalException>(async () =>
        {
            switch (operation)
            {
                case "ListChildren": await client.ListAsync("/Training", default); break;
                case "GetItemParameters": await client.ParametersAsync("/Training/R", [], true, default); break;
                case "LoadReport": await client.LoadAsync("/Training/R", default); break;
                case "SetExecutionParameters": await client.SetAsync("execution", [], default); break;
                case "GetExecutionInfo": await client.InfoAsync("execution", default); break;
                case "Render": await client.RenderAsync("execution", "HTML5", 1, default); break;
                case "RenderStream": await client.StreamAsync("execution", "style", default); break;
            }
        });
        Assert.Contains(member, exception.Message);
        Assert.Equal(operation, FailureDiagnostics.Find(exception)!.Operation);
        Assert.Equal("request-reference", FailureDiagnostics.Find(exception)!.Trace);
    }

    [Theory]
    [InlineData("CredentialsRequired")]
    [InlineData("ParametersRequired")]
    public async Task MissingRequiredExecutionFlagsFailClosedWithContext(string member)
    {
        var body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "execution-info.xml"));
        body = body.Replace("<" + member + ">false</" + member + ">", "", StringComparison.Ordinal);
        using var handler = new LiteralSoapHandler(body);
        using var http = new HttpClient(handler);
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var exception = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).InfoAsync("literal-execution", default));
        Assert.Contains(member, exception.Message);
        Assert.Equal("GetExecutionInfo", FailureDiagnostics.Find(exception)!.Operation);
    }

    [Fact]
    public async Task CapturesAndPresentationsAreBoundedAndClearlyTruncated()
    {
        var body = Fault.Replace("Permission denied for folder /Training", "start sentinel " + new string('x', 100000) + " end sentinel", StringComparison.Ordinal);
        using var handler = new LiteralSoapHandler(body, HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var exception = await Assert.ThrowsAsync<PortalException>(() => transport.CallAsync("GetProperties", false, [], null, default));
        var diagnostic = FailureDiagnostics.Find(exception)!;
        Assert.True(diagnostic.FaultDetail!.Length <= FailureDiagnostics.CaptureCharacters);
        Assert.True(diagnostic.FaultXml!.Length <= FailureDiagnostics.CaptureCharacters);
        var text = FailureDiagnostics.Format(exception, "reference");
        Assert.True(text.Length <= FailureDiagnostics.MaximumCharacters);
        Assert.Contains("[truncated]", text); Assert.Contains("Exception:", text);
        Assert.DoesNotContain("end sentinel", text);
    }

    [Fact]
    public async Task MalformedResponseExcerptIsBoundedAndDoesNotContainRenderedResults()
    {
        const string result = "SUCCESSFUL_REPORT_OUTPUT_SENTINEL";
        using var handler = new LiteralSoapHandler("<Result>" + result + "</Result><broken>start sentinel " + new string('x', 50000));
        using var http = new HttpClient(handler);
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var exception = await Assert.ThrowsAsync<PortalException>(() => transport.CallAsync("Render", true, [], "private-execution-id", default));
        var excerpt = FailureDiagnostics.Find(exception)!.ResponseExcerpt!;
        Assert.True(excerpt.Length <= FailureDiagnostics.CaptureCharacters);
        Assert.Contains("[truncated]", excerpt); Assert.DoesNotContain(result, excerpt);
        Assert.Contains("start sentinel", excerpt);
    }

    [Theory]
    [InlineData("Development", "127.0.0.1", "127.0.0.1", true, true, true)]
    [InlineData("Development", "::1", "::1", true, true, true)]
    [InlineData("Development", "::ffff:127.0.0.1", "::ffff:127.0.0.1", true, true, true)]
    [InlineData("Development", "192.0.2.1", "192.0.2.1", true, true, false)]
    [InlineData("Development", "127.0.0.1", "192.0.2.1", true, true, false)]
    [InlineData("Development", "192.0.2.1", "127.0.0.1", true, true, false)]
    [InlineData("Development", null, null, true, true, false)]
    [InlineData("Production", "127.0.0.1", "127.0.0.1", true, true, false)]
    [InlineData("Staging", "127.0.0.1", "127.0.0.1", true, true, false)]
    [InlineData("Development", "127.0.0.1", "127.0.0.1", false, true, false)]
    [InlineData("Development", "127.0.0.1", "127.0.0.1", true, false, false)]
    public void BrowserGateRequiresEverySecurityCondition(string environment, string? peer, string? remote, bool enabled, bool authorized, bool allowed)
    {
        var context = new DefaultHttpContext { User = authorized ? AuthenticationTests.Principal() : new ClaimsPrincipal(new ClaimsIdentity()) };
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        context.Items[FailureDiagnostics.PeerAddressKey] = peer is null ? null : IPAddress.Parse(peer);
        var options = Options(); options.Diagnostics.ShowDetailsInBrowser = enabled;
        Assert.Equal(allowed, FailureDiagnostics.BrowserAllowed(context, new TestEnvironment(environment), options, Entra()));
        if (authorized)
        {
            ((ClaimsIdentity)context.User.Identity!).RemoveClaim(context.User.FindFirst("roles")!);
            Assert.False(FailureDiagnostics.BrowserAllowed(context, new TestEnvironment(environment), options, Entra()));
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ExternalConfigurationRejectsBrowserDetailsOutsideDevelopmentButPermitsExplicitServerLogging(string environment)
    {
        using var file = new TestConfigurationFile(new() { ["Portal:UseMock"] = "false", ["Portal:TestAuth"] = "false", ["Portal:Diagnostics:Enabled"] = "true", ["Portal:Diagnostics:ShowDetailsInBrowser"] = "true" });
        using var configuration = (Microsoft.Extensions.Configuration.ConfigurationRoot)new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonFile(file.Path).Build();
        var options = ExternalConfigurationLoader.Bind<PortalOptions>(configuration, "Portal");
        Assert.Contains("ShowDetailsInBrowser", Assert.Throws<InvalidOperationException>(() => options.Validate(environment == "Development")).Message);
        options.Diagnostics.ShowDetailsInBrowser = false;
        Assert.NotNull(options.Validate(false));
        Assert.True(options.Diagnostics.Enabled);
    }

    [Theory]
    [InlineData(false, false, "127.0.0.1", false)]
    [InlineData(true, false, "127.0.0.1", false)]
    [InlineData(false, true, "127.0.0.1", true)]
    [InlineData(true, true, "127.0.0.1", true)]
    [InlineData(true, true, "192.0.2.1", false)]
    public async Task BrowserAndServerOptInsAreIndependentAndRawErrorsAreEncoded(bool logging, bool browserDetails, string peer, bool visible)
    {
        await using var factory = Factory(logging, browserDetails, peer);
        using var user = await factory.Login();
        factory.Mock.Failure = MockFailure.Fault;
        using var response = await user.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(visible, html.Contains("private SQL", StringComparison.Ordinal));
        Assert.Equal(visible, html.Contains("Diagnostic details", StringComparison.Ordinal));
        Assert.Contains("Reference:", html);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        var logs = string.Join('\n', factory.Logs.Messages);
        Assert.Equal(logging, logs.Contains("private SQL", StringComparison.Ordinal));
        Assert.Contains("GetProperties", logs);
        using var api = await user.Http.GetAsync("/api/catalog");
        using var json = JsonDocument.Parse(await api.Content.ReadAsStringAsync());
        Assert.Equal(visible, json.RootElement.TryGetProperty("diagnostics", out _));
        Assert.Contains(json.RootElement.GetProperty("trace").GetString()!, string.Join('\n', factory.Logs.Messages));
    }

    [Fact]
    public async Task HtmlDiagnosticsEncodeUpstreamMarkupAndNeverLeakResponseOrRequestAuthenticationHeaders()
    {
        await using var factory = new PortalFactory(new() { ["Portal:Diagnostics:Enabled"] = "true", ["Portal:Diagnostics:ShowDetailsInBrowser"] = "true" }, configureServices: services =>
        {
            services.AddSingleton<IStartupFilter>(new PeerFilter("127.0.0.1"));
            services.AddSingleton<HttpMessageHandler>(new LiteralSoapHandler(Fault, HttpStatusCode.InternalServerError));
        });
        using var user = await factory.Login();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Authorization = new("Bearer", "REQUEST_TOKEN_SENTINEL");
        using var response = await user.Http.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("rsAccessDenied", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        var logs = string.Join('\n', factory.Logs.Messages);
        foreach (var secret in new[] { "REQUEST_TOKEN_SENTINEL", "UPSTREAM_COOKIE_SENTINEL", "AUTH_CHALLENGE_SENTINEL" })
        {
            Assert.DoesNotContain(secret, html); Assert.DoesNotContain(secret, logs);
        }
        Assert.Single(factory.Logs.Messages, message => message.Contains("details Reference:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("outsider")]
    [InlineData("old-group")]
    [InlineData("unknown-tenant")]
    public async Task UnauthorizedRequestsNeverReceiveRawDiagnostics(string profile)
    {
        await using var factory = Factory(true, true, "127.0.0.1");
        factory.Mock.Failure = MockFailure.Fault;
        using var user = await factory.Login(profile);
        using var response = await user.Http.GetAsync("/api/catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("diagnostics", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Mock.Observations);
    }

    [Fact]
    public void RawPresentationRetainsUsefulFaultTextWhileFilteringAuthenticationMaterialAndKnownSecret()
    {
        var raw = "Useful rsAccessDenied detail. <Password>XML_PASSWORD_SENTINEL</Password>\n" +
            "&lt;Password&gt;ESCAPED_PASSWORD_SENTINEL&lt;/Password&gt;\n" +
            "Authorization: Bearer AUTHORIZATION_SENTINEL\nCookie: SESSION=COOKIE_SENTINEL\n" +
            "Set-Cookie: SESSION=SET_COOKIE_SENTINEL\nclient_secret=CONFIG_CREDENTIAL_SENTINEL\n" +
            "{\"access_token\":\"JSON_TOKEN_SENTINEL\"}\nBearer BEARER_SENTINEL\n" +
            "https://user:URI_PASSWORD_SENTINEL@host/ReportServer\nraw configured-secret-sentinel";
        var exception = FailureDiagnostics.Attach(new PortalException("service", innerException: new HttpRequestException(raw)),
            new SoapDiagnostic("GetProperties", "https://synthetic.invalid/ReportServer/ReportService2010.asmx", "reference") { FaultDetail = raw });
        var text = FailureDiagnostics.Format(exception, "reference", "configured-secret-sentinel");
        Assert.Contains("Useful rsAccessDenied detail.", text);
        Assert.Contains("HttpRequestException", text);
        foreach (var secret in new[] { "XML_PASSWORD_SENTINEL", "ESCAPED_PASSWORD_SENTINEL", "AUTHORIZATION_SENTINEL", "COOKIE_SENTINEL", "SET_COOKIE_SENTINEL", "CONFIG_CREDENTIAL_SENTINEL", "JSON_TOKEN_SENTINEL", "BEARER_SENTINEL", "URI_PASSWORD_SENTINEL", "configured-secret-sentinel" })
        {
            Assert.DoesNotContain(secret, text);
        }
        Assert.Contains(raw, exception.InnerException!.Message);
    }

    [Fact]
    public async Task ARemoteRequestForwardedByALocalProxyCannotObtainBrowserDiagnostics()
    {
        await using var factory = Factory(true, true, "127.0.0.1");
        using var user = await factory.Login();
        factory.Mock.Failure = MockFailure.Fault;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog");
        request.Headers.Add("X-Forwarded-For", "192.0.2.45");
        using var response = await user.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("diagnostics", text); Assert.DoesNotContain("private SQL", text);
        Assert.Contains("private SQL", string.Join('\n', factory.Logs.Messages));
    }

    [Fact]
    public void KnownRequestAuthenticationValuesAreRemovedEvenIfEchoedWithoutLabels()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer TOKEN_SENTINEL";
        context.Request.Headers.Cookie = "__Host-Portal=COOKIE_SENTINEL";
        context.Request.Headers["RequestVerificationToken"] = "CSRF_SENTINEL";
        var exception = new InvalidOperationException("Useful failure: TOKEN_SENTINEL COOKIE_SENTINEL CSRF_SENTINEL");
        var text = FailureDiagnostics.Format(exception, "reference", request: context);
        Assert.Contains("Useful failure:", text);
        Assert.DoesNotContain("TOKEN_SENTINEL", text);
        Assert.DoesNotContain("COOKIE_SENTINEL", text);
        Assert.DoesNotContain("CSRF_SENTINEL", text);
        Assert.Contains("TOKEN_SENTINEL", exception.Message);
    }

    [Fact]
    public async Task BackgroundSoapRefreshLogsFaultDetailsAtItsBoundaryWhenExplicitlyEnabled()
    {
        using var harness = new MonitorHarness();
        harness.Portal.Options.Diagnostics.Enabled = true;
        using var monitor = new CatalogMonitor(harness.Provider, harness.Portal.Catalog, harness.Options,
            harness.Portal.Clock, harness.Logger, harness.Portal.Options);
        Assert.Equal(CatalogPollOutcome.Baseline, await monitor.PollAsync(default));
        harness.ModifyCatalog(MonitorHarness.A, item => item with { ModifiedDate = "2026-10-30T00:00:00Z" });
        harness.UpdateSnapshot();
        harness.Portal.Mock.Failure = MockFailure.Fault;
        Assert.Equal(CatalogPollOutcome.Failed, await monitor.PollAsync(default));
        var details = Assert.Single(harness.Logger.Messages.Concat(harness.Portal.Logger.Messages), message => message.Contains("details Reference:", StringComparison.Ordinal));
        Assert.Contains("GetProperties", details);
        Assert.Contains("private SQL", details);
        Assert.Contains("Fault Code: soap:Server", details);
    }

    [Fact]
    public async Task InvalidBase64KeepsItsOriginalFormatExceptionAndNeverCapturesReportOutput()
    {
        var ns = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
        using var handler = new LiteralSoapHandler(ManagementSoapContractTests.Envelope("Render", "<Result>NOT_BASE64_SELECTION_SENTINEL!</Result><MimeType>text/html</MimeType>", ns));
        using var http = new HttpClient(handler);
        var options = Options();
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var exception = await Assert.ThrowsAsync<PortalException>(() => new SsrsClient(transport, options).RenderAsync("example", "HTML5", 1, default));
        Assert.IsType<FormatException>(exception.InnerException);
        Assert.Equal("Render", FailureDiagnostics.Find(exception)!.Operation);
        Assert.DoesNotContain("NOT_BASE64_SELECTION_SENTINEL", FailureDiagnostics.Format(exception, "reference"));
    }

    [Fact]
    public async Task DisabledDiagnosticsPreserveOversizedContentClassificationBeforeContentTypeRejection()
    {
        using var handler = new LiteralSoapHandler(new string('x', 2048), media: "text/html");
        using var http = new HttpClient(handler);
        var options = Options(false); options.MaxSoapBytes = 1024;
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var exception = await Assert.ThrowsAsync<PortalException>(() => transport.CallAsync("GetProperties", false, [], null, default));
        Assert.Equal("limit", exception.Code);
        Assert.Equal(502, exception.Status);
        Assert.Null(FailureDiagnostics.Find(exception)!.ResponseExcerpt);
    }

    [Fact]
    public async Task SharedCatalogContractFailuresAreLoggedEvenAfterTheLastWaiterCancels()
    {
        using var handler = new GatedHandler();
        using var http = new HttpClient(handler);
        var options = Options(); options.AllowedRoots = ["/Training"]; options.ReportAccessPolicy = "GlobalBlocklist";
        using var transport = new SoapTransport(http, options, new CaptureLogger<SoapTransport>());
        var logger = new CaptureLogger<CatalogService>();
        using var catalog = new CatalogService(new SsrsClient(transport, options), options, TimeProvider.System, logger);
        using var cancellation = new CancellationTokenSource();
        var caller = catalog.BrowseAsync(cancellation.Token);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
        }
        finally { handler.Release.TrySetResult(); }
        await CatalogCacheTests.WaitUntil(() => logger.Messages.Any(message => message.Contains("details Reference:", StringComparison.Ordinal)));
        await catalog.StopAsync(default);
        var details = Assert.Single(logger.Messages, message => message.Contains("details Reference:", StringComparison.Ordinal));
        Assert.Contains("GetProperties", details);
        Assert.Contains("missing required Values", details);
        Assert.Contains("ReportService2010.asmx", details);
    }

    private static PortalOptions Options(bool enabled = true) => new()
    {
        ReportServer = "https://synthetic.invalid/ReportServer", Diagnostics = new DiagnosticsOptions { Enabled = enabled, DetailedFailuresAtDebug = enabled }
    };
    private static HttpContextAccessor Accessor() => new() { HttpContext = new DefaultHttpContext { TraceIdentifier = "request-reference" } };
    private static EntraOptions Entra() => new() { ClientId = AuthenticationTests.Client, AllowedTenantIds = [AuthenticationTests.Tenant] };
    private static PortalFactory Factory(bool logging, bool browser, string peer) => new(new()
    {
        ["Portal:Diagnostics:Enabled"] = logging.ToString(), ["Portal:Diagnostics:DetailedFailuresAtDebug"] = "false", ["Portal:Diagnostics:ShowDetailsInBrowser"] = browser.ToString()
    }, configureServices: services => services.AddSingleton<IStartupFilter>(new PeerFilter(peer)));

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw exception;
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        }
    }
    private sealed class GatedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ManagementSoapContractTests.Envelope("GetProperties", ""), System.Text.Encoding.UTF8, "text/xml")
            };
        }
    }
    private sealed class PeerFilter(string peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Parse(peer); return continuation(context); });
            next(app);
        };
    }
    private sealed class TestEnvironment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
