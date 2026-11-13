using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Security;

namespace SsrsPortal.Soap;

public sealed class SoapTransport(HttpClient client, PortalOptions options, ILogger<SoapTransport> logger,
    IHttpContextAccessor? requestContext = null, EntraOptions? entra = null, TimeSpan? operationTimeout = null, bool suppressFailureContent = false) : IDisposable
{
    public static readonly XNamespace Envelope = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Management = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";
    public static readonly XNamespace Execution = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    // The installed SSRS HTML5 renderer omits interactive controls without a
    // browser-compatible HTTP User-Agent, even with ActionScript in DeviceInfo.
    // Keep the verified value deterministic across hosts and portal browsers.
    private const string Html5UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private readonly SemaphoreSlim _slots = new(options.MaxConcurrentSoap);

    public async Task<XElement> CallAsync(string operation, bool execution, IEnumerable<XElement> args,
        string? executionId, CancellationToken cancellation, bool includeFailureContent = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(operationTimeout ?? TimeSpan.FromSeconds(options.RequestSeconds));
        includeFailureContent &= !suppressFailureContent;
        var entered = false;
        var timer = Stopwatch.StartNew();
        var endpoint = new Uri(options.ReportServer.TrimEnd('/') + (execution ? "/ReportExecution2005.asmx" : "/ReportService2010.asmx"));
        var diagnostic = new SoapDiagnostic(operation, endpoint.AbsoluteUri,
            requestContext?.HttpContext?.TraceIdentifier ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"))
        { SuppressContent = !includeFailureContent || operation == "ToggleItem" };
        // Toggle faults can echo runtime identifiers. Keep only operation/status/
        // timing diagnostics for this mutation; never capture its envelope or fault text.
        var capture = options.Diagnostics.CaptureFailures && includeFailureContent && operation != "ToggleItem";
        using var buffer = new MemoryStream();
        try
        {
            await _slots.WaitAsync(timeout.Token);
            entered = true;
            XNamespace ns = execution ? Execution : Management;
            var header = new XElement(Envelope + "Header");
            if (executionId != null)
            {
                header.Add(new XElement(Execution + "ExecutionHeader", new XElement(Execution + "ExecutionID", executionId)));
            }
            var requestBody = new XElement(ns + operation, args);
            var doc = new XDocument(new XElement(Envelope + "Envelope", header,
                new XElement(Envelope + "Body", requestBody)));
            var text = doc.ToString(SaveOptions.DisableFormatting);
            if (Encoding.UTF8.GetByteCount(text) > options.MaxSoapBytes)
            {
                throw Fail("request-limit", "The outgoing SOAP document exceeds MaxSoapBytes.", "limit");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("SOAPAction", "\"" + ns.NamespaceName + "/" + operation + "\"");
            if (execution && operation == "Render" && requestBody.Element(Execution + "Format")?.Value == "HTML5")
            {
                request.Headers.UserAgent.ParseAdd(Html5UserAgent);
            }
            request.Content = new StringContent(text, Encoding.UTF8, "text/xml");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            diagnostic = diagnostic with
            {
                HttpStatus = (int)response.StatusCode,
                Reason = capture ? Capture(response.ReasonPhrase, 1024) : null,
                ContentType = capture ? Capture(response.Content.Headers.ContentType?.ToString(), 1024) : null
            };
            var credentials = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            var redirect = (int)response.StatusCode is >= 300 and < 400;
            var media = response.Content.Headers.ContentType?.MediaType;
            var invalidType = media is not "text/xml" and not "application/soap+xml";
            if (!capture && !credentials && !redirect && response.Content.Headers.ContentLength > options.MaxSoapBytes)
            {
                throw Fail("response-limit", "SSRS response Content-Length exceeds MaxSoapBytes.", "limit");
            }
            if (!capture && (credentials || redirect || invalidType)) { throw HttpFailure(); }

            // Error pages need only a bounded prefix. SOAP (including HTTP 500 faults)
            // retains the existing document limit and DTD protections.
            var prefixOnly = credentials || redirect || invalidType;
            var maximum = prefixOnly ? Math.Min(options.MaxSoapBytes, FailureDiagnostics.CaptureCharacters * 4) : options.MaxSoapBytes;
            if (!prefixOnly && response.Content.Headers.ContentLength > maximum)
            {
                throw Fail("response-limit", "SSRS response Content-Length exceeds MaxSoapBytes.", "limit");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var chunk = new byte[8192];
            var truncated = false;
            while (true)
            {
                var count = await stream.ReadAsync(chunk, timeout.Token);
                if (count == 0) { break; }
                var remaining = maximum - (int)buffer.Length;
                await buffer.WriteAsync(chunk.AsMemory(0, Math.Min(count, remaining)), timeout.Token);
                if (count > remaining)
                {
                    truncated = true;
                    if (prefixOnly) { break; }
                    throw Fail("response-limit", "SSRS response body exceeds MaxSoapBytes.", "limit");
                }
            }
            if (capture && prefixOnly) { diagnostic = diagnostic with { ResponseExcerpt = Excerpt(buffer, truncated, entra?.ClientSecret, requestContext?.HttpContext) }; }
            if (redirect || invalidType || (credentials && truncated)) { throw HttpFailure(); }
            buffer.Position = 0;
            using var reader = XmlReader.Create(buffer, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = options.MaxSoapBytes,
                IgnoreComments = true
            });
            XDocument xml;
            try { xml = XDocument.Load(reader); }
            catch (XmlException exception) when (credentials) { throw HttpFailure(exception); }
            timeout.Token.ThrowIfCancellationRequested();
            var body = xml.Root?.Name == Envelope + "Envelope" ? xml.Root.Element(Envelope + "Body") : null;
            if (body?.Element(Envelope + "Fault") is { } fault)
            {
                diagnostic = diagnostic with
                {
                    FaultCode = capture ? Capture(fault.Element("faultcode")?.Value, 1024) : null,
                    FaultMessage = capture ? Capture(fault.Element("faultstring")?.Value, 2048) : null,
                    FaultDetail = capture ? Capture(fault.Element("detail")?.ToString(SaveOptions.DisableFormatting)) : null,
                    FaultXml = capture ? Capture(fault.ToString(SaveOptions.DisableFormatting)) : null,
                    ResponseExcerpt = null
                };
                throw Fail("soap-fault", "SSRS returned a SOAP Fault.", credentials ? "credentials" : "service");
            }
            if (!response.IsSuccessStatusCode)
            {
                if (capture) { diagnostic = diagnostic with { ResponseExcerpt = Excerpt(buffer, false, entra?.ClientSecret, requestContext?.HttpContext) }; }
                throw HttpFailure();
            }
            var results = body?.Elements().ToArray();
            if (results?.Length != 1 || results[0].Name != ns + operation + "Response")
            {
                if (capture) { diagnostic = diagnostic with { ResponseExcerpt = Excerpt(buffer, false, entra?.ClientSecret, requestContext?.HttpContext) }; }
                throw Fail("response-contract", $"Expected one SOAP Body/{ns + operation + "Response"}; received {FailureDiagnostics.Bound(body?.Elements().FirstOrDefault()?.Name.ToString() ?? "no SOAP body", 512)}.");
            }
            var result = results[0];
            result.AddAnnotation(diagnostic with { Milliseconds = timer.ElapsedMilliseconds });
            logger.LogInformation("SOAP operation {Operation} completed in {Milliseconds}ms, {Bytes} bytes; trace {Trace}",
                operation, timer.ElapsedMilliseconds, buffer.Length, diagnostic.Trace);
            return result;

            PortalException HttpFailure(Exception? innerException = null) => Fail(credentials ? "http-credentials" : redirect ? "redirect" : invalidType ? "content-type" : "http-status",
                credentials ? "SSRS returned HTTP 401 or 403." : redirect ? "SSRS returned an unexpected HTTP redirect." :
                invalidType ? "SSRS returned an unexpected or missing Content-Type." : "SSRS returned an unsuccessful HTTP status.",
                credentials ? "credentials" : "service", innerException);
        }
        catch (OperationCanceledException exception)
        {
            var code = cancellation.IsCancellationRequested ? "cancelled" : "timeout";
            diagnostic = diagnostic with { Failure = code, Milliseconds = timer.ElapsedMilliseconds };
            logger.LogInformation("SOAP operation {Operation} stopped with {Code} in {Milliseconds}ms; trace {Trace}",
                operation, code, timer.ElapsedMilliseconds, diagnostic.Trace);
            FailureDiagnostics.Attach(exception, diagnostic);
            if (cancellation.IsCancellationRequested)
            {
                LogFailure(exception, code);
                throw;
            }
            var failure = FailureDiagnostics.Attach(new PortalException("timeout", 504, "The SSRS SOAP operation timed out.", exception), diagnostic);
            LogFailure(failure, code);
            throw failure;
        }
        catch (PortalException exception)
        {
            FailureDiagnostics.Attach(exception, diagnostic with { Milliseconds = timer.ElapsedMilliseconds });
            LogFailure(exception, exception.Code);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or XmlException or InvalidOperationException)
        {
            diagnostic = diagnostic with
            {
                Failure = exception is XmlException ? "xml" : exception is HttpRequestException http ? http.HttpRequestError.ToString() : exception.GetType().Name,
                Milliseconds = timer.ElapsedMilliseconds,
                ResponseExcerpt = capture && exception is XmlException ? Excerpt(buffer, false, entra?.ClientSecret, requestContext?.HttpContext) : diagnostic.ResponseExcerpt
            };
            var failure = FailureDiagnostics.Attach(new PortalException("service", message: "The SSRS SOAP operation failed.", innerException: exception), diagnostic);
            LogFailure(failure, "service");
            throw failure;
        }
        finally
        {
            if (entered) { _slots.Release(); }
        }

        PortalException Fail(string failure, string message, string code = "service", Exception? innerException = null)
        {
            diagnostic = diagnostic with { Failure = failure };
            return new PortalException(code, message: message, innerException: innerException);
        }

        string Capture(string? value, int maximum = FailureDiagnostics.CaptureCharacters) =>
            FailureDiagnostics.Bound(FailureDiagnostics.Filter(value, entra?.ClientSecret, requestContext?.HttpContext), maximum);

        void LogFailure(Exception exception, string code)
        {
            // Log at the producer so shared catalog work still reports a failure after
            // its last HTTP waiter disconnects. Boundaries log only a short correlation.
            if (options.Diagnostics.Enabled || (options.Diagnostics.DetailedFailuresAtDebug && logger.IsEnabled(LogLevel.Debug)))
            {
                FailureDiagnostics.Log(logger, options, exception, code, diagnostic.Trace, entra?.ClientSecret, requestContext?.HttpContext);
            }
        }
    }

    private static string Excerpt(MemoryStream buffer, bool truncated, string? clientSecret, HttpContext? request)
    {
        var bytes = buffer.GetBuffer().AsSpan(0, Math.Min((int)buffer.Length, FailureDiagnostics.CaptureCharacters * 4));
        var encoding = bytes.StartsWith(new byte[] { 255, 254 }) ? Encoding.Unicode :
            bytes.StartsWith(new byte[] { 254, 255 }) ? Encoding.BigEndianUnicode : Encoding.UTF8;
        var text = encoding.GetString(bytes);
        // A failed envelope can still contain a rendered Result. Never capture report output.
        text = Regex.Replace(text, @"(<(?:[\w.-]+:)?Result\b[^>]*>)[\s\S]*?(</(?:[\w.-]+:)?Result\s*>|$)",
            "$1[report output omitted]$2", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        return FailureDiagnostics.Bound(FailureDiagnostics.Filter(text, clientSecret, request) + (truncated ? "\n[truncated]" : ""));
    }

    internal static PortalException ContractFailure(XElement response, string message, Exception? innerException = null, string code = "service")
    {
        var exception = new PortalException(code, message: message, innerException: innerException);
        return response.Annotation<SoapDiagnostic>() is { } diagnostic
            ? FailureDiagnostics.Attach(exception, diagnostic with { Failure = "response-contract" }) : exception;
    }

    internal static XElement Required(XElement root, XNamespace ns, string name) =>
        root.Element(ns + name) ?? throw ContractFailure(root, $"SSRS {root.Name.LocalName} is missing required {name}.");

    public static XElement Element(XNamespace ns, string name, object value) => new(ns + name, value);
    public static XElement Values(XNamespace ns, string container, IEnumerable<ParameterValue> values) =>
        new(ns + container, values.Select(p => new XElement(ns + "ParameterValue", new XElement(ns + "Name", p.Name),
            p.Value == null ? null : new XElement(ns + "Value", p.Value))));
    public static string Text(XElement root, XNamespace ns, string name) => root.Element(ns + name)?.Value ?? "";
    public static bool Flag(XElement root, XNamespace ns, string name) => Text(root, ns, name).Trim() is "true" or "1";
    public void Dispose() => _slots.Dispose();
}
