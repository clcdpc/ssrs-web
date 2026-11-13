using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace SsrsPortal.Mock;

public enum MockFailure { None, Unauthorized, Forbidden, Redirect, Fault, Malformed, Dtd, Oversized, Slow, Empty, LargeOutput, WrongMime, Unavailable, ToggleNotFound, ToggleLostResponse, HtmlPdf }
public sealed record SoapObservation(string Operation, string? ExecutionId, XDocument Request, string? UserAgent = null);
public sealed record SyntheticCatalogItem(string Path, string Type = "Report", bool Hidden = false, string? ModifiedDate = "2026-10-10T00:00:00Z", string? Identity = null);

/// <summary>Development-only, in-process SOAP server. Validates wire requests before producing realistic SSRS wrapped responses.</summary>
public sealed class SyntheticSsrsHandler : HttpMessageHandler
{
    public const string Root = "/Training";
    public const string Holds = "/Training/Reports/Holds Ratio";
    public const string Workstations = "/Training/Reports/Circulation By Workstation";
    public const string Link = "/Training/Shortcuts/Holds Link";
    public const string Details = "/Training/Reports/Details";
    public const string Defaults = "/Training/Reports/Default Showcase";
    public const string BranchDates = "/Training/Reports/Check Ins by Day of Week by Hour";
    public const string SingleChoices = "/Training/Reports/Single Choice Showcase";
    public const string CompactInputs = "/Training/Reports/Compact Inputs";
    public const string OneInput = "/Training/Reports/One Input";
    public const string Expandable = "/Training/Reports/Expandable Groups";
    public const string DeepReport = "/Training/Reports/Custom/Regional activity/Circulation/Hourly transactions/Check Ins by Day of Week by Hour with a very long report name for regional circulation review and historical comparisons";
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Management = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";
    public static readonly XNamespace Execution = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private readonly ConcurrentDictionary<string, MockExecution> _executions = new();
    private readonly object _executionSync = new();
    private readonly ConcurrentQueue<SoapObservation> _observations = new();
    public SoapObservation[] Observations => _observations.ToArray();
    public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
    public SyntheticCatalogItem[]? CatalogItems { get; set; }
    public ConcurrentDictionary<string, bool> RequiredCredentials { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, string> LinkedTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, bool> FaultTargets { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, int> TargetDelayMilliseconds { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Task> TargetResponseGates { get; } = new(StringComparer.Ordinal);
    public MockFailure Failure { get; set; }
    public SyntheticBranding Branding { get; set; } = SyntheticBranding.Missing;
    public ConcurrentDictionary<string, int> BrandingCalls { get; } = new(StringComparer.Ordinal);
    public string? FailOperation { get; set; }
    public bool UnknownPageCount { get; set; }
    public bool PageCountChangesAfterRender { get; set; }
    public bool ParameterQueryUnavailable { get; set; }
    public int DelayMilliseconds { get; set; } = 2000;
    public Task? ToggleResponseGate { get; set; }
    public int ActiveCalls => _active;
    public int MaxActiveCalls => _maxActive;
    public void ResetConcurrencyPeak() => Interlocked.Exchange(ref _maxActive, Volatile.Read(ref _active));
    private int _active;
    private int _maxActive;
    private int _serial;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        InterlockedExtensions.Max(ref _maxActive, active);
        try
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.Scheme == "https")
            {
                var key = request.RequestUri.AbsolutePath switch
                {
                    "/Reports/api/v2.0/SafeGetSystemResourceContent(type='UniversalBrand',key='logo')" => "logo",
                    "/Reports/api/v2.0/SafeGetSystemResourceContent(type='UniversalBrand',key='colors')" => "colors",
                    _ => null
                };
                if (key is null || request.RequestUri.Query.Length != 0) { return new HttpResponseMessage(HttpStatusCode.NotFound); }
                BrandingCalls.AddOrUpdate(key, 1, (_, count) => count + 1);
                var fixture = Branding;
                if (fixture.Gate is not null) { await fixture.Gate.WaitAsync(cancellationToken); }
                var bytes = key == "logo" ? fixture.Logo : fixture.Colors;
                var response = new HttpResponseMessage(fixture.Status == HttpStatusCode.OK && bytes is null ? HttpStatusCode.NoContent : fixture.Status)
                { Content = new ByteArrayContent(bytes ?? []) };
                response.Content.Headers.ContentType = new(key == "logo" ? fixture.LogoContentType : fixture.ColorsContentType);
                if (fixture.ContentEncoding is not null) { response.Content.Headers.ContentEncoding.Add(fixture.ContentEncoding); }
                return response;
            }
            if (request.Method != HttpMethod.Post || request.RequestUri?.Scheme != "https")
            {
                return Fault();
            }
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = xml.Root?.Element(Soap + "Body")?.Elements().Single();
            if (xml.Root?.Name != Soap + "Envelope" || body == null)
            {
                return Fault();
            }
            var op = body.Name.LocalName;
            var execution = body.Name.Namespace == Execution;
            var ns = execution ? Execution : Management;
            var id = xml.Root.Element(Soap + "Header")?.Element(Execution + "ExecutionHeader")?.Element(Execution + "ExecutionID")?.Value;
            var expectedPath = execution ? "/ReportServer/ReportExecution2005.asmx" : "/ReportServer/ReportService2010.asmx";
            if (body.Name.Namespace != ns || request.RequestUri.AbsolutePath != expectedPath ||
                request.Headers.GetValues("SOAPAction").Single() != "\"" + ns.NamespaceName + "/" + op + "\"" ||
                body.Descendants().Any(e => e.Name.Namespace != ns) || body.Descendants().Any(e => e.Attribute(Xsi + "nil") != null))
            {
                return Fault();
            }
            if (execution && op != "LoadReport" && (id == null || !_executions.ContainsKey(id)))
            {
                return Fault();
            }
            if ((!execution || op == "LoadReport") && id != null)
            {
                return Fault();
            }
            if (op == "ToggleItem" && (xml.Root.Elements(Soap + "Header").Count() != 1 ||
                xml.Root.Element(Soap + "Header")!.Elements().Count() != 1 ||
                xml.Root.Element(Soap + "Header")!.Elements().Single().Name != Execution + "ExecutionHeader" ||
                xml.Root.Element(Soap + "Header")!.Elements().Single().Elements().Count() != 1 ||
                body.Elements().Count() != 1 || body.Elements().Single().Name != Execution + "ToggleID" ||
                body.Elements().Single().HasElements || body.Elements().Single().HasAttributes ||
                string.IsNullOrWhiteSpace(Read(body, "ToggleID")) || Read(body, "ToggleID").Length > 256))
            {
                return Fault();
            }
            _observations.Enqueue(new SoapObservation(op, id, xml, request.Headers.UserAgent.ToString()));
            var target = body.Elements().FirstOrDefault(x => x.Name.LocalName is "ItemPath" or "Report" or "DataSource")?.Value ?? "(execution)";
            Calls.AddOrUpdate(op + "|" + target, 1, (_, count) => count + 1);
            while (_observations.Count > 200)
            {
                _observations.TryDequeue(out _);
            }
            var failure = FailOperation == null || op == FailOperation ? Failure : MockFailure.None;
            if (failure == MockFailure.Slow)
            {
                await Task.Delay(DelayMilliseconds, cancellationToken);
            }
            if (op == "ToggleItem" && _executions[id!].Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "slow")
            {
                await Task.Delay(600, cancellationToken);
            }
            if (TargetDelayMilliseconds.TryGetValue(op + "|" + target, out var targetDelay))
            {
                await Task.Delay(targetDelay, cancellationToken);
            }
            if (TargetResponseGates.TryGetValue(op + "|" + target, out var gate))
            {
                await gate.WaitAsync(cancellationToken);
            }
            if (FaultTargets.ContainsKey(op + "|" + target))
            {
                return Fault();
            }
            if (failure == MockFailure.Unavailable)
            {
                throw new HttpRequestException("Synthetic transport failure");
            }
            if (failure is MockFailure.Unauthorized or MockFailure.Forbidden)
            {
                return new HttpResponseMessage(failure == MockFailure.Unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
            }
            if (failure == MockFailure.Redirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://untrusted.invalid/report");
                return redirect;
            }
            if (failure == MockFailure.Fault)
            {
                return Fault();
            }
            if (failure == MockFailure.Malformed)
            {
                return Response("<broken>");
            }
            if (failure == MockFailure.Dtd)
            {
                return Response("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///private'>]><x>&e;</x>");
            }
            if (failure == MockFailure.Oversized)
            {
                return Response(new string('x', 25 * 1024 * 1024));
            }
            try
            {
                var result = op switch
                {
                    "ListChildren" => List(body),
                    "GetProperties" => Properties(body),
                    "GetItemParameters" => Parameters(body),
                    "LoadReport" => Load(body),
                    "SetExecutionParameters" => Set(body, id!),
                    "GetExecutionInfo" => GetInfo(body, id!),
                    "Render" => Render(body, id!, failure, request.Headers.UserAgent.ToString()),
                    "RenderStream" => Stream(body, id!, failure),
                    "ToggleItem" => Toggle(body, id!, failure),
                    _ => throw new InvalidOperationException("Unknown mock operation")
                };
                if (op == "ToggleItem")
                {
                    if (ToggleResponseGate != null) { await ToggleResponseGate.WaitAsync(cancellationToken); }
                    if (failure == MockFailure.ToggleLostResponse || _executions[id!].Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "lost")
                    {
                        throw new HttpRequestException("Synthetic lost toggle reply after mutation");
                    }
                }
                var header = new XElement(Soap + "Header");
                if (op == "LoadReport")
                {
                    header.Add(new XElement(Execution + "ExecutionHeader", new XElement(Execution + "ExecutionID", result.Element(Execution + "ExecutionID")?.Value)));
                }
                var response = new XElement(ns + op + "Response");
                if (op == "ToggleItem")
                {
                    response.Add(result);
                }
                else if (op is "Render" or "RenderStream")
                {
                    response.Add(result.Elements());
                }
                else
                {
                    result.Name = ns + (op switch
                    {
                        "ListChildren" => "CatalogItems", "GetItemParameters" => "Parameters",
                        "GetProperties" => "Values",
                        _ => "executionInfo"
                    });
                    response.Add(result);
                }
                return Response(new XDocument(new XElement(Soap + "Envelope", header, new XElement(Soap + "Body", response))).ToString());
            }
            catch (InvalidOperationException)
            {
                return Fault();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private static void Shape(XElement x, params string[] names)
    {
        if (!x.Elements().Select(e => e.Name.LocalName).SequenceEqual(names))
        {
            throw new InvalidOperationException("Bad SOAP shape");
        }
    }
    private static string Read(XElement x, string name) => x.Element(x.Name.Namespace + name)?.Value ?? "";
    private static XElement E(XNamespace ns, string name, object? value) => new(ns + name, value);
    private static SyntheticCatalogItem[] DefaultCatalog =>
        [
            new("/Training/Reports", "Folder"), new("/Training/Shortcuts", "Folder"),
            new(Holds), new(Workstations), new(Details), new(Defaults), new(BranchDates), new(SingleChoices), new(CompactInputs), new(OneInput), new(Expandable), new(Link, "LinkedReport"),
            new("/Training/Reports/User Identity"), new("/Training/Reports/Prompted"), new("/Training/Reports/Broken"),
            new("/Training/Reports/Hidden", Hidden: true), new("/Training/Reports/Additional Report"),
            new("/Training/Reports/Blocked Subreport"), new("/Training/Shortcuts/Unsafe Link", "LinkedReport"),
            new("/Training/Custom_Restored", "Folder"), new("/Training/Custom_Restored/Old"),
            new("/Training/Internal", "Folder"), new("/Training/Internal/System"),
            new("/Training/Reports/Sorting fixture", "Folder"),
            new("/Training/Reports/Sorting fixture/~Tech Services", "Folder"), new("/Training/Reports/Sorting fixture/~Tech Services/Tech sample"),
            new("/Training/Reports/Sorting fixture/~Archives", "Folder"), new("/Training/Reports/Sorting fixture/~Archives/Archive sample"),
            new("/Training/Reports/Sorting fixture/Administration", "Folder"), new("/Training/Reports/Sorting fixture/Administration/Admin sample"),
            new("/Training/Reports/Sorting fixture/Circulation", "Folder"), new("/Training/Reports/Sorting fixture/Circulation/Circ sample"),
            new("/Training/Reports/Sorting fixture/Alpha sample"), new("/Training/Reports/Sorting fixture/Linked sample", "LinkedReport"),
            new("/Training/Reports/Sorting fixture/~Report sample"),
            new("/Training/Reports/Sorting fixture/~Hidden", "Folder", Hidden: true), new("/Training/Reports/Sorting fixture/~Hidden/Hidden sample"),
            new("/Training/Reports/Custom", "Folder"), new("/Training/Reports/Custom/Regional activity", "Folder"),
            new("/Training/Reports/Custom/Regional activity/Circulation", "Folder"), new("/Training/Reports/Custom/Regional activity/Circulation/Hourly transactions", "Folder"), new(DeepReport)
        ];

    private XElement List(XElement request)
    {
        Shape(request, "ItemPath", "Recursive");
        if (Read(request, "Recursive") != "true")
        {
            throw new InvalidOperationException();
        }
        var root = Read(request, "ItemPath");
        var configured = (CatalogItems ?? DefaultCatalog).Select((item, i) => item with { Identity = item.Identity ?? i.ToString() })
            .Where(i => i.Path != root && i.Path.StartsWith(root == "/" ? "/" : root + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        return new XElement("array", configured.Select((c, i) => new XElement(Management + "CatalogItem", E(Management, "ID", c.Identity ?? i.ToString()),
            E(Management, "Name", c.Path.Split('/').Last()), E(Management, "Path", c.Path), E(Management, "TypeName", c.Type), E(Management, "Hidden", c.Hidden ? "true" : "false"),
            c.ModifiedDate == null ? null : E(Management, "ModifiedDate", c.ModifiedDate))));
    }

    private XElement Properties(XElement request)
    {
        Shape(request, "ItemPath", "Properties");
        var requested = request.Element(Management + "Properties")!.Elements(Management + "Property").ToArray();
        if (requested.Length != 1 || Read(requested[0], "Name") != "Hidden")
        {
            throw new InvalidOperationException();
        }
        var path = Read(request, "ItemPath");
        var folder = (CatalogItems ?? DefaultCatalog).FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (folder == null && path != "/" && !path.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException();
        }
        return new XElement("result", new XElement(Management + "Property", E(Management, "Name", "Hidden"), E(Management, "Value", folder?.Hidden == true ? "True" : "False")));
    }
    public static XElement BuildParameters(string path, Dictionary<string, string?[]> values, bool rendering)
    {
        var list = new List<XElement>();
        string? Pick(string n) => values.GetValueOrDefault(n)?.FirstOrDefault();
        void Add(string name, string type, string prompt, bool multi = false, bool nullable = false, bool blank = false, bool hidden = false,
            bool query = false, bool queryDefault = false, string[]? dependencies = null, string?[]? defaults = null, (string Value, string Label)[]? choices = null, string? state = null)
        {
            var v = values.GetValueOrDefault(name) ?? defaults ?? [];
            var actual = state ?? (v.Length == 0 ? "MissingValidValue" : choices != null && v.Any(s => !(nullable && s == null) && !choices.Any(o => o.Value == s)) ? "MissingValidValue" : "HasValidValue");
            if (!rendering && query)
            {
                actual = "DynamicValuesUnavailable";
            }
            list.Add(new XElement(Management + "ItemParameter", E(Management, "Name", name), E(Management, "ParameterTypeName", type),
                E(Management, "Nullable", nullable ? "true" : "false"), E(Management, "AllowBlank", blank ? "true" : "false"),
                E(Management, "MultiValue", multi ? "true" : "false"), E(Management, "QueryParameter", "true"), E(Management, "Prompt", prompt),
                E(Management, "PromptUser", hidden ? "false" : "true"), E(Management, "Dependencies", (dependencies ?? []).Select(d => E(Management, "Dependency", d))),
                E(Management, "ValidValuesQueryBased", query ? "true" : "false"), E(Management, "ValidValues", (choices ?? []).Select(c =>
                    new XElement(Management + "ValidValue", E(Management, "Label", c.Label), E(Management, "Value", c.Value)))),
                E(Management, "DefaultValuesQueryBased", queryDefault ? "true" : "false"), E(Management, "DefaultValues", (defaults ?? []).Select(d =>
                    d == null ? new XElement(Management + "Value", new XAttribute(Xsi + "nil", "true")) : E(Management, "Value", d))), E(Management, "ParameterStateName", actual)));
        }
        if (path == Expandable)
        {
            Add("initiallyExpanded", "Boolean", "Initially expanded", defaults: ["false"]);
            Add("behavior", "String", "Behavior", defaults: ["normal"], choices:
                [("normal", "Normal"), ("slow", "Slow toggle"), ("render-failure", "Render fails once after toggle"),
                 ("lost", "Lost toggle reply"), ("missing", "Toggle not found"), ("unknown", "Unknown page count"), ("reportviewer", "ReportViewer capture"),
                 ("native-layout", "Native report layout")]);
        }
        else if (path == OneInput)
        {
            Add("OrganizationID", "Integer", "OrganizationID", defaults: ["21"]);
        }
        else if (path == CompactInputs)
        {
            Add("OrganizationID", "Integer", "OrganizationID", defaults: ["21"]);
            Add("minimumCopies", "Integer", "Minimum copies", defaults: ["3"]);
            Add("percentage", "Float", "Percentage", defaults: ["75.5"]);
            Add("inactive", "Boolean", "Include inactive", defaults: ["false"]);
            Add("reportYear", "String", "Report year", defaults: ["2026"]);
            Add("materialCode", "String", "Material code", defaults: ["BK"]);
            Add("sortDirection", "String", "Sort direction", defaults: ["ASC"]);
            Add("expression", "String", "Search expression", defaults: ["A practical long text editing field"]);
            Add("format", "String", "Format", defaults: ["5"], choices: Enumerable.Range(1, 5)
                .Select(i => (i.ToString(), i == 5 ? "A longer format label that stays accessible without stretching every parameter across the report page" : "Format " + i)).ToArray());
        }
        else if (path == BranchDates)
        {
            // Dates copied from the reported DEV metadata; all choices/data are synthetic.
            Add("beginDate", "DateTime", "beginDate", defaults: ["9/1/2026 12:00:00 AM"]);
            Add("endDate", "DateTime", "endDate", defaults: ["9/30/2026 11:59:59 PM"]);
            Add("branch", "Integer", "Branches", multi: true, query: true, defaults: ["1", "80", "152"],
                choices: Enumerable.Range(1, 152).Select(i => (i.ToString(), "Synthetic branch " + i.ToString("D3"))).ToArray());
            var ready = values.GetValueOrDefault("branch")?.Length > 0;
            var branch = Pick("branch");
            Add("department", "String", "Department", query: true, queryDefault: true, dependencies: ["branch"],
                defaults: ready ? [branch + "-team"] : [], choices: ready ? [(branch + "-team", "Team for branch " + branch), ("shared", "Shared team")] : [],
                state: ready ? null : "HasOutstandingDependencies");
            Add("blankChoices", "String", "Nullable choices", multi: true, nullable: true, blank: true, defaults: [""],
                choices: [("", "(Blank choice)"), ("named", "Named choice"), ("null", "Literal null")]);
            // Display-only material names mirror the requested UI; values and
            // report data remain synthetic, with no dependency on DEV SSRS.
            string[] materials = ["4K Blu-ray", "Abstract", "Archival/Mixed Materials", "Atlas", "Audio Book on Cassette",
                "Audio Book on CD", "Audio Books", "Blu-ray + DVD", "Book", "Book + CD", "Book Club Kit", "Braille",
                "CD-ROM", "Chart", "Compact Disc", "DVD", "DVD + Book", "Digital Audio", "Digital Video", "Document",
                "eAudio", "eBook", "Electronic Resource", "Equipment", "Film", "Flash Card", "Game", "Globe", "Graphic Novel",
                "Journal", "Kit", "Large Print", "Learning Kit", "Magazine", "Manuscript", "Map", "Microfiche", "Microfilm",
                "Music", "Newspaper", "Object", "Pamphlet", "Photograph", "Picture", "Playaway", "Reference Book", "Score",
                "Serial", "Slide", "Software", "Streaming Video", "Video Game"];
            var materialChoices = materials.Select((name, index) => ("material-" + index, name)).ToArray();
            Add("materials", "String", "Types of Material", multi: true, defaults: materialChoices.Select(c => c.Item1).ToArray(), choices: materialChoices);
        }
        else if (path == SingleChoices)
        {
            // Synthetic single-value counterpart to the live DEV branch scenario.
            Add("branch", "Integer", "branch", query: true, defaults: ["152"],
                choices: Enumerable.Range(1, 152).Select(i => (i.ToString(), i == 21 ? "CLC Test branch" : "Synthetic branch " + i.ToString("D3"))).ToArray());
            var ready = Pick("branch") != null;
            Add("department", "String", "Department", query: true, queryDefault: true, dependencies: ["branch"],
                defaults: ready ? [Pick("branch") + "-team"] : [],
                choices: ready ? [(Pick("branch") + "-team", "Team for branch " + Pick("branch")), ("shared", "Shared team")] : [],
                state: ready ? null : "HasOutstandingDependencies");
            Add("workstation", "Integer", "Workstation", query: true, queryDefault: true, defaults: ["3500"],
                choices: Enumerable.Range(1, 4192).Select(i => (i.ToString(), "Synthetic workstation " + i.ToString("D4"))).ToArray());
            Add("required", "String", "Required choice", choices: [("a", "Choice A"), ("b", "Choice B")]);
            Add("nullable", "String", "Nullable choice", nullable: true, blank: true, defaults: [""],
                choices: [("", "(Blank choice)"), ("named", "Named choice"), ("null", "Literal null"), ("None", "Literal None value"), ("none-labelled", "None")]);
            Add("note", "String", "Unrelated note", defaults: ["Keep this note"]);
        }
        else if (path == Workstations)
        {
            Add("library", "Integer", "Library", query: true, defaults: ["153"], choices: Enumerable.Range(1, 153).Select(i => (i.ToString(), "Synthetic library " + i)).ToArray());
            Add("workstations", "Integer", "Workstations", multi: true, query: true, choices: Enumerable.Range(1, 4192).Select(i => (i.ToString(), "Synthetic workstation " + i.ToString("D4"))).ToArray());
        }
        else if (path == Defaults)
        {
            Add("branch", "Integer", "Branch", defaults: ["1"], choices: [("1", "Main library"), ("2", "Other library")]);
            Add("flag", "Boolean", "Include inactive", defaults: ["False"]);
            var ready = Pick("branch") != null && Pick("flag") != null;
            var child = Pick("branch") + "-" + Pick("flag");
            Add("child", "String", "Department", query: true, queryDefault: true, dependencies: ["branch", "flag"],
                defaults: ready ? [child] : [], choices: ready ? [(child, "Department for branch " + Pick("branch"))] : [],
                state: ready ? null : "HasOutstandingDependencies");
            Add("branchNote", "String", "Branch note", queryDefault: true, dependencies: ["branch"],
                defaults: Pick("branch") == null ? [] : ["Branch " + Pick("branch")], state: Pick("branch") == null ? "HasOutstandingDependencies" : null);
            Add("start", "DateTime", "Precise date", defaults: ["2026-10-30T09:15:23.1234567"]);
            Add("zoned", "DateTime", "Zoned date", defaults: ["2026-10-30T09:15:23.123-04:00"]);
            Add("note", "String", "Nullable note", nullable: true, defaults: [null]);
            Add("blank", "String", "Blank note", blank: true, defaults: [""]);
            Add("lines", "String", "Multiple entries", multi: true, defaults: ["First entry", "Second entry"]);
            Add("workstations", "Integer", "Default workstations", multi: true, query: true, queryDefault: true, defaults: ["3500", "4192"],
                choices: Enumerable.Range(1, 4192).Select(i => (i.ToString(), "Synthetic workstation " + i.ToString("D4"))).ToArray());
            Add("internal", "String", "Hidden default", hidden: true, queryDefault: true, dependencies: ["branch"],
                defaults: Pick("branch") == null ? [] : ["internal-" + Pick("branch")], state: Pick("branch") == null ? "HasOutstandingDependencies" : null);
        }
        else if (path is Holds or Link)
        {
            Add("library", "Integer", "Library", query: true, defaults: ["1"], choices: Enumerable.Range(1, 19).Select(i => (i.ToString(), "Synthetic library " + i)).ToArray());
            Add("applyCollectionFilter", "Boolean", "Apply collection filter");
            var upstream = Pick("library") != null && Pick("applyCollectionFilter") != null;
            var count = Pick("library") switch { "2" => 73, "3" => 72, _ => 71 };
            var prefix = Pick("library") + "-" + (Pick("applyCollectionFilter") == "true" ? "on-" : "off-");
            var choices = upstream ? Enumerable.Range(1, count).Select(i => (prefix + i, "Synthetic collection " + i)).ToArray() : [];
            Add("collections", "String", "Collections", multi: true, query: true, queryDefault: true,
                dependencies: ["library", "applyCollectionFilter"], defaults: upstream ? [prefix + "1"] : [], choices: choices,
                state: upstream ? null : "HasOutstandingDependencies");
            Add("start", "DateTime", "Start date", defaults: ["2026-01-01T00:00:00"]);
            Add("ratio", "Float", "Minimum ratio", defaults: ["1.5"]);
            Add("note", "String", "Optional note", nullable: true, blank: true, defaults: [null]);
            Add("material", "String", "Optional material", nullable: true, queryDefault: true, defaults: [null],
                choices: Enumerable.Range(1, 50).Select(i => (i.ToString(), "Synthetic material " + i)).ToArray());
            Add("internalMode", "String", "Internal", hidden: true, defaults: ["safe-default"]);
        }
        return new XElement("result", list);
    }
    private static Dictionary<string, string?[]> ParseValues(XElement? values, XNamespace ns)
    {
        return values?.Elements(ns + "ParameterValue").GroupBy(v => v.Element(ns + "Name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Element(ns + "Value")?.Value).ToArray(), StringComparer.Ordinal) ?? [];
    }
    private XElement Parameters(XElement request)
    {
        Shape(request, request.Element(Management + "Values") == null ? ["ItemPath", "ForRendering"] : ["ItemPath", "ForRendering", "Values"]);
        var result = BuildParameters(Read(request, "ItemPath"), ParseValues(request.Element(Management + "Values"), Management), Read(request, "ForRendering") == "true");
        if (ParameterQueryUnavailable)
        {
            foreach (var item in result.Elements().Where(p => Read(p, "ValidValuesQueryBased") == "true"))
            {
                item.Element(Management + "ParameterStateName")!.Value = "DynamicValuesUnavailable";
            }
        }
        return result;
    }
    private XElement Load(XElement request)
    {
        Shape(request, "Report");
        var path = Read(request, "Report");
        var catalog = CatalogItems ?? DefaultCatalog;
        var item = catalog.SingleOrDefault(i => i.Path == path && i.Type is "Report" or "LinkedReport");
        if (item == null)
        {
            throw new InvalidOperationException();
        }
        // The SSRS double resolves linked definitions internally, without a portal lookup.
        var target = item.Type == "LinkedReport" ? LinkedTargets.GetValueOrDefault(path) ??
            (path == Link ? Holds : "/Training/Internal/System") : path;
        if (!catalog.Any(i => i.Path == target && i.Type == "Report"))
        {
            throw new InvalidOperationException("Synthetic missing linked target");
        }
        if (path.EndsWith("Broken", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Synthetic invalid SSRS data source");
        }
        lock (_executionSync)
        {
            foreach (var expired in _executions.Where(p => DateTimeOffset.UtcNow - p.Value.Created > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray())
            {
                _executions.TryRemove(expired, out _);
            }
            if (_executions.Count >= 512)
            {
                throw new InvalidOperationException("Synthetic backend capacity reached");
            }
            var id = "synthetic-execution-" + Interlocked.Increment(ref _serial);
            var e = new MockExecution(path);
            _executions[id] = e;
            return Info(id, e);
        }
    }
    private XElement Set(XElement request, string id)
    {
        Shape(request, "Parameters", "ParameterLanguage");
        var e = _executions[id];
        e.Values = ParseValues(request.Element(Execution + "Parameters"), Execution);
        var metadata = BuildParameters(e.Path, e.Values, true);
        if (metadata.Elements().Any(p => Read(p, "ParameterStateName") != "HasValidValue") ||
            e.Values.Keys.Any(k => !metadata.Elements().Any(p => Read(p, "Name") == k)))
        {
            throw new InvalidOperationException();
        }
        return Info(id, e);
    }
    private XElement GetInfo(XElement request, string id)
    {
        Shape(request);
        return Info(id, _executions[id]);
    }
    private XElement Info(string id, MockExecution e) => new XElement("result",
        E(Execution, "HasSnapshot", e.Rendered ? "true" : "false"), E(Execution, "NeedsProcessing", e.Rendered ? "false" : "true"), E(Execution, "AllowQueryExecution", "true"),
        E(Execution, "CredentialsRequired", RequiredCredentials.GetValueOrDefault(e.Path) || e.Path.EndsWith("Prompted", StringComparison.Ordinal) ? "true" : "false"),
        E(Execution, "ParametersRequired", BuildParameters(e.Path, e.Values, true).Elements().Any(p => Read(p, "ParameterStateName") != "HasValidValue") ? "true" : "false"),
        E(Execution, "ExpirationDateTime", "2026-10-10T12:00:00"), E(Execution, "ExecutionDateTime", "2026-10-10T11:00:00"),
        E(Execution, "NumPages", e.Rendered && !UnknownPageCount && e.Values.GetValueOrDefault("behavior")?.FirstOrDefault() != "unknown" ?
            (PageCountChangesAfterRender ? e.ReportedPages : PageCount(e)) : 0), E(Execution, "HasDocumentMap", "false"),
        E(Execution, "ExecutionID", id), E(Execution, "ReportPath", e.Path), E(Execution, "AutoRefreshInterval", 0));
    private XElement Render(XElement request, string id, MockFailure failure, string userAgent)
    {
        Shape(request, "Format", "DeviceInfo");
        var format = Read(request, "Format");
        var e = _executions[id];
        var device = XElement.Parse(Read(request, "DeviceInfo"));
        var section = format == "HTML5" ? int.Parse(device.Element("Section")!.Value) : 1;
        var callback = device.Element("ActionScript")?.Value;
        if (format == "HTML5" && (device.Element("HTMLFragment")?.Value != "True" || section is < 1 or > 10000 ||
            !(device.Elements().Select(x => x.Name.LocalName).SequenceEqual(["HTMLFragment", "Section"]) ||
              (device.Elements().Select(x => x.Name.LocalName).SequenceEqual(["HTMLFragment", "Section", "ActionScript"]) &&
               System.Text.RegularExpressions.Regex.IsMatch(callback ?? "", @"\ASsrsPortalAction_[0-9a-f]{64}\z")))))
        {
            throw new InvalidOperationException();
        }
        if (e.FailNextRender)
        {
            e.FailNextRender = false;
            throw new InvalidOperationException("Synthetic post-toggle render failure");
        }
        if (e.Path == Expandable && !e.VisibilityInitialized)
        {
            e.VisibilityInitialized = true;
            if (e.Values.GetValueOrDefault("initiallyExpanded")?.FirstOrDefault() == "true") { e.Expanded.UnionWith(["1iT0", "2iT0", "4iT0"]); }
        }
        section = Math.Min(section, PageCount(e));
        e.ReportedPages = PageCount(e);
        e.Rendered = true;
        if (format == "HTML5") { e.RenderVersion++; }
        var ownerMark = "Synthetic run " + id.Split('-').Last();
        var html = $"<style>.report{{width:1200px;border-collapse:collapse}}td{{padding:12px;border:1px solid gray}}</style><link rel='stylesheet' href='https://synthetic.invalid/ReportServer?%2FTraining%2FReports%2FHolds%20Ratio&amp;rs:SessionID={id}&amp;rs:Format=HTML5&amp;rs:ImageID=style' />" +
            $"<h2>Page {section}</h2><p>{ownerMark}</p><table class='report'><tr><th>Synthetic subreport details</th><th>Count</th></tr><tr><td>Demonstration row {section}</td><td>{section * 11}</td></tr></table>" +
            $"<img src='https://synthetic.invalid/ReportServer?%2FTraining%2FReports%2FHolds%20Ratio&amp;rs:SessionID={id}&amp;rs:Format=HTML5&amp;rs:ImageID=chart' alt='Synthetic chart'/>" +
            "<script>parent.document.body.dataset.pwned='yes'</script><a href='javascript:alert(1)' onclick='alert(1)'>Expand row</a><iframe src='https://untrusted.invalid'></iframe>";
        var streamNames = new[] { "chart", "style" };
        if (e.Path == Expandable)
        {
            // Synthetic capability gate models the operator's DEV observation;
            // it is not the installed renderer's browser-detection algorithm.
            var interactive = userAgent.StartsWith("Mozilla/5.0 ", StringComparison.Ordinal) &&
                userAgent.Contains("AppleWebKit/537.36", StringComparison.Ordinal) &&
                userAgent.Contains("Chrome/120.0.0.0", StringComparison.Ordinal) &&
                userAgent.Contains("Safari/537.36", StringComparison.Ordinal);
            html = ExpansionHtml(e, id, section, callback, interactive);
            if (e.Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "native-layout")
            {
                using var fixture = typeof(SyntheticSsrsHandler).Assembly.GetManifestResourceStream("SsrsPortal.Mock.HoldsRatioLayout.html")!;
                using var reader = new StreamReader(fixture);
                html = reader.ReadToEnd();
            }
            streamNames = ["chart-" + e.RenderVersion, "style-" + e.RenderVersion];
            ownerMark += " Visibility: " + string.Join(';', e.Expanded.Order(StringComparer.Ordinal));
        }
        (byte[] Bytes, string Mime, string Extension) payload = format switch
        {
            "HTML5" => (Encoding.UTF8.GetBytes(html), "text/html", "html"),
            "PDF" => (failure == MockFailure.HtmlPdf ? Encoding.UTF8.GetBytes("<html>Not a PDF</html>") : Pdf(ownerMark, Math.Max(1, PageCount(e))), "application/pdf", "pdf"),
            "EXCELOPENXML" => (OpenXml("xl/workbook.xml", ownerMark), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
            "WORDOPENXML" => (OpenXml("word/document.xml", ownerMark), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx"),
            "CSV" => (Encoding.UTF8.GetBytes("Synthetic,Count\r\n" + ownerMark + ",22\r\n"), "text/csv", "csv"),
            _ => throw new InvalidOperationException()
        };
        var bytes = failure == MockFailure.Empty ? [] : failure == MockFailure.LargeOutput ? new byte[17 * 1024 * 1024] : payload.Bytes;
        return new XElement("result", E(Execution, "Result", Convert.ToBase64String(bytes)), E(Execution, "Extension", payload.Extension),
            E(Execution, "MimeType", failure == MockFailure.WrongMime ? (format == "HTML5" ? "application/octet-stream" : "text/html") : payload.Mime), E(Execution, "Encoding", "utf-8"), E(Execution, "Warnings", null),
            E(Execution, "StreamIds", format == "HTML5" ? streamNames.Select(s => E(Execution, "string", s)) : null));
    }

    private static int PageCount(MockExecution e) => e.Path == Expandable ? 2 +
        (e.Expanded.Contains("1iT0") ? 1 + (e.Expanded.Contains("2iT0") ? 1 : 0) : 0) : 2;

    private XElement Toggle(XElement request, string id, MockFailure failure)
    {
        Shape(request, "ToggleID");
        var e = _executions[id];
        var toggle = Read(request, "ToggleID");
        var visible = new[] { "1iT0", "3iT0", "4iT0" }.Contains(toggle) ||
            (e.Expanded.Contains("1iT0") && new[] { "2iT0", "5iT0" }.Contains(toggle)) ||
            (e.Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "reportviewer" &&
                (toggle is "230iT0R0x11" or "231iT0R0x11" || toggle == "232iT0R0x11" && e.Expanded.Contains("230iT0R0x11")));
        if (e.Path != Expandable || !visible || failure == MockFailure.ToggleNotFound || e.Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "missing")
        {
            return E(Execution, "Found", "false");
        }
        if (!e.Expanded.Add(toggle)) { e.Expanded.Remove(toggle); }
        if (e.Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "render-failure") { e.FailNextRender = true; }
        return E(Execution, "Found", "true");
    }

    private static string ExpansionHtml(MockExecution e, string id, int page, string? callback, bool interactive)
    {
        // Invented report data. Markup follows the public Html5ServerRenderer /
        // RenderOnClickActionScript implementation, NOT captured DEV SSRS output.
        var report = Uri.EscapeDataString(Expandable);
        string Resource(string name) => $"https://synthetic.invalid/ReportServer?{report}&amp;rs:SessionID={id}&amp;rs:Format=HTML5&amp;rs:ImageID={name}-{e.RenderVersion}";
        string Control(string toggle, bool url = false)
        {
            if (!interactive) { return ""; }
            var expanded = e.Expanded.Contains(toggle);
            var action = callback != null && !url ? $"onclick=\"{callback}('Toggle','{toggle}',event);return false;\"" :
                $"href=\"https://synthetic.invalid/ReportServer?{report}&amp;rs:SessionID={id}&amp;rs:Format=HTML5&amp;rs:Command=Render&amp;rs%3aShowHideToggle={toggle}&amp;rc:Section={page}\"";
            return $"<a tabindex=\"1\" {action}><img src=\"https://synthetic.invalid/ReportServer?rs:Command=Get&amp;rc:GetImage=8.0.0Toggle{(expanded ? "Minus" : "Plus")}.gif\" alt=\"{(expanded ? "Collapse" : "Expand")}\" /></a>";
        }
        var html = new StringBuilder($"<style>table{{border-collapse:collapse;width:100%}}td,th{{padding:8px;border:1px solid gray}}.spacer{{height:900px}}</style><link rel='stylesheet' href='{Resource("style")}'/><h2>Page {page}</h2><p>Synthetic expandable report</p><table><tr><th>Group</th><th>Contents</th></tr>");
        html.Append($"<tr><td>{Control("4iT0", true)} Single level</td><td>Independent row</td></tr>");
        if (e.Expanded.Contains("4iT0")) { html.Append("<tr><td>Single detail</td><td>Single-level expanded content</td></tr>"); }
        html.Append($"<tr><td>{Control("1iT0")} Library</td><td>Parent group</td></tr>");
        if (e.Expanded.Contains("1iT0"))
        {
            html.Append($"<tr><td>{Control("2iT0")} Branch East</td><td>Nested group</td></tr>");
            if (e.Expanded.Contains("2iT0")) { html.Append("<tr><td>Detail East</td><td>Nested expanded content</td></tr>"); }
            html.Append($"<tr><td>{Control("5iT0")} Branch West</td><td>Independent child</td></tr>");
            if (e.Expanded.Contains("5iT0")) { html.Append("<tr><td>Detail West</td><td>Sibling expanded content</td></tr>"); }
        }
        html.Append($"<tr><td>{Control("3iT0", true)} Library South</td><td>Independent parent</td></tr>");
        if (e.Expanded.Contains("3iT0")) { html.Append("<tr><td>Detail South</td><td>Independent expanded content</td></tr>"); }
        html.Append("</table>");
        if (e.Values.GetValueOrDefault("behavior")?.FirstOrDefault() == "reportviewer")
        {
            // Structural fixture based on a real DEV SSRS standard-viewer HTML
            // capture. Invented rows/data; NOT a captured SOAP HTML5 fragment.
            string Cell(string toggle, string value)
            {
                var expanded = e.Expanded.Contains(toggle);
                var action = $"var rp=$get('ctl00_Viewer_ctl17_ReportControl');if(rp&amp;&amp;rp.control)rp.control.InvokeReportAction('Toggle','{toggle}',event);return false;";
                var control = interactive ?
                    $"<a role='button' aria-expanded='{(expanded ? "true" : "false")}' tabindex='12' onclick=\"{action}\" onkeypress=\"if(event.keyCode == 13 || event.which == 13){{{action}}}\">" +
                    $"<img border='0' src='/ReportServer/Reserved.ReportViewerWebControl.axd?OpType=Resource&amp;Version=16.0.1115.92&amp;Name=Microsoft.ReportingServices.Rendering.HtmlRenderer.RendererResources.Toggle{(expanded ? "Minus" : "Plus")}.gif' alt='{(expanded ? "Collapse" : "Expand")} {value}'></a>" : "";
                return $"<td><div id='Redacted_{toggle}' style='display:flex;flex-flow:row-reverse'><div id='Redacted_{toggle}_aria' style='display:flex;width:100%;justify-content:flex-start;max-width:15.70mm'>" +
                    control +
                    $"<div style='width:12.45mm;min-width:12.45mm'>{value}</div></div></div></td>";
            }
            html.Append("<table class='system-items' style='width:1200px'><caption>System items compatibility fixture</caption><tr><th>System Items</th><th>System Holds</th><th>Assigned Branch</th></tr>");
            html.Append($"<tr>{Cell("230iT0R0x11", "15")}<td>3</td><td>Central</td></tr>");
            if (e.Expanded.Contains("230iT0R0x11"))
            {
                html.Append($"<tr>{Cell("232iT0R0x11", "14")}<td>13</td><td>ReportViewer nested group</td></tr>");
                if (e.Expanded.Contains("232iT0R0x11")) { html.Append("<tr><td>Nested numeric detail</td><td>13</td><td>Expanded child</td></tr>"); }
            }
            html.Append($"<tr>{Cell("231iT0R0x11", "23")}<td>46</td><td>West</td></tr>");
            if (e.Expanded.Contains("231iT0R0x11")) { html.Append("<tr><td>Independent numeric detail</td><td>46</td><td>Expanded sibling</td></tr>"); }
            html.Append("<tr><td>1</td><td>3</td><td>Ordinary numeric cell</td></tr></table>");
        }
        html.Append($"<div class='spacer'>Scroll preservation fixture</div><img src='{Resource("chart")}' alt='Synthetic chart'/><script>parent.document.body.dataset.pwned='yes'</script><a href='javascript:alert(1)' onclick='alert(1)'>Unsafe action</a>");
        return html.ToString();
    }
    private static byte[] OpenXml(string entry, string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Write(string name, string value)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(value);
            }
            var word = entry.StartsWith("word/", StringComparison.Ordinal);
            Write("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/" + entry + "' ContentType='application/vnd.openxmlformats-officedocument." + (word ? "wordprocessingml.document" : "spreadsheetml.sheet") + ".main+xml'/>" + (word ? "" : "<Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/>") + "</Types>");
            Write("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='" + entry + "'/></Relationships>");
            if (word)
            {
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Write(entry, new XElement(w + "document", new XElement(w + "body", new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", text))))).ToString());
            }
            else
            {
                Write(entry, "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Synthetic' sheetId='1' r:id='rId1'/></sheets></workbook>");
                Write("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
                XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Write("xl/worksheets/sheet1.xml", new XElement(x + "worksheet", new XElement(x + "sheetData", new XElement(x + "row", new XAttribute("r", "1"),
                    new XElement(x + "c", new XAttribute("r", "A1"), new XAttribute("t", "inlineStr"), new XElement(x + "is", new XElement(x + "t", text)))))).ToString());
            }
        }
        return ms.ToArray();
    }
    private static byte[] Pdf(string text, int pages)
    {
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        List<string> objects = ["<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[" + string.Join(' ', Enumerable.Range(0, pages).Select(i => (4 + i * 2) + " 0 R")) + "]/Count " + pages + ">>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>"];
        for (var page = 0; page < pages; page++)
        {
            var content = "BT /F1 18 Tf 50 740 Td (Page " + (page + 1) + " - " + text + ") Tj ET";
            objects.Add("<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents " + (5 + page * 2) + " 0 R>>");
            objects.Add("<</Length " + content.Length + ">>\nstream\n" + content + "\nendstream");
        }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position); Write((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n");
        }
        var xref = ms.Position; Write("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        }
        Write("trailer<</Size " + (objects.Count + 1) + "/Root 1 0 R>>\nstartxref\n" + xref + "\n%%EOF");
        return ms.ToArray();
    }
    private XElement Stream(XElement request, string id, MockFailure failure)
    {
        Shape(request, "Format", "StreamID", "DeviceInfo");
        if (Read(request, "Format") != "HTML5" || !_executions[id].Rendered || Read(request, "DeviceInfo") != "<DeviceInfo/>")
        {
            throw new InvalidOperationException();
        }
        var stream = Read(request, "StreamID");
        var e = _executions[id];
        if (e.Path == Expandable)
        {
            if (stream != "style-" + e.RenderVersion && stream != "chart-" + e.RenderVersion) { throw new InvalidOperationException(); }
            stream = stream.Split('-')[0];
        }
        var bytes = stream == "style" ? Encoding.UTF8.GetBytes(".report{background-color:#f5f8ff}body{font-family:Arial} .bad{background:url(https://untrusted.invalid);behavior:evil}") :
            stream == "chart" ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aY1sAAAAASUVORK5CYII=") : throw new InvalidOperationException();
        return new XElement("result", E(Execution, "Result", failure == MockFailure.Empty ? "" : Convert.ToBase64String(bytes)), E(Execution, "Encoding", "utf-8"),
            E(Execution, "MimeType", failure == MockFailure.WrongMime ? "image/svg+xml" : stream == "style" ? "text/css" : "image/png"));
    }
    private static HttpResponseMessage Fault() => Response(new XDocument(new XElement(Soap + "Envelope", new XElement(Soap + "Body",
        new XElement(Soap + "Fault", new XElement("faultcode", "soap:Server"), new XElement("faultstring", "Synthetic private SQL detail must never reach users"), new XElement("detail", "private"))))).ToString(), HttpStatusCode.InternalServerError);
    private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
    private sealed class MockExecution(string path)
    {
        public string Path { get; } = path;
        public Dictionary<string, string?[]> Values { get; set; } = [];
        public bool Rendered { get; set; }
        public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);
        public bool VisibilityInitialized { get; set; }
        public bool FailNextRender { get; set; }
        public int RenderVersion { get; set; }
        public int ReportedPages { get; set; }
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        var prior = Volatile.Read(ref target);
        while (prior < value)
        {
            var original = Interlocked.CompareExchange(ref target, value, prior);
            if (original == prior)
            {
                return;
            }
            prior = original;
        }
    }
}
