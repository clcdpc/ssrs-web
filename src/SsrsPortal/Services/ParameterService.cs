using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SsrsPortal.Core;
using SsrsPortal.Soap;

namespace SsrsPortal.Services;

public sealed class ParameterService(SsrsClient soap, PortalOptions options)
{
    private const int MaxSelectionValues = 5000;
    // Leave room below the existing 256 KiB HTTP bound for state/provenance hints.
    private const int MaxSelectionRequestBytes = 224 * 1024;
    private int SelectionLimit => Math.Min(options.MaxOptions, MaxSelectionValues);
    public async Task<ReportLinkForm> ResolveLinkAsync(CatalogReport report, string query, CancellationToken ct)
    {
        var link = ReportLinkQuery.Parse(query, options);
        var resolved = await ResolveCoreAsync(report, new(link.Values, Initial: true), false, ct, link);
        return new(resolved.Form, link.HasParameters, link.NoExecute);
    }
    public async Task<(ParameterForm Form, ParameterValue[] ExecutionValues)> ResolveAsync(CatalogReport report,
        ParameterInput input, bool execute, CancellationToken ct)
    {
        var result = await ResolveCoreAsync(report, input, execute, ct);
        return (result.Form, result.ExecutionValues);
    }

    internal Task<(ParameterForm Form, ParameterValue[] ExecutionValues, ReportParameter[] Metadata)> ResolveSubscriptionAsync(
        CatalogReport report, ParameterInput input, CancellationToken ct) => ResolveCoreAsync(report, input, true, ct);

    internal async Task<ParameterForm> PreviewSubscriptionAsync(CatalogReport report, ParameterInput input, CancellationToken ct) =>
        (await ResolveCoreAsync(report, input, false, ct, preserveExplicit: true)).Form;

    private async Task<(ParameterForm Form, ParameterValue[] ExecutionValues, ReportParameter[] Metadata)> ResolveCoreAsync(
        CatalogReport report, ParameterInput input, bool execute, CancellationToken ct, ReportLinkQuery? link = null, bool preserveExplicit = false)
    {
        VerifyAccess(report, ct);
        var incoming = input.Values ?? new Dictionary<string, string?[]>(StringComparer.Ordinal);
        var strictExplicit = link != null || preserveExplicit;
        if (incoming.Count > options.MaxParameters || incoming.Any(p => p.Key.Length > 128 || p.Value == null ||
            p.Value.Length > options.MaxOptions || p.Value.Any(v => v?.Length > 2048)))
        {
            throw new PortalException("values", 400);
        }
        var schema = await soap.ParametersAsync(report.Path, [], true, ct);
        VerifyAccess(report, ct);
        ValidateDependencies(schema);
        var linkErrors = new List<string>(link?.Errors ?? []);
        if (link != null)
        {
            incoming = new(incoming, StringComparer.Ordinal);
            foreach (var name in incoming.Keys.Where(k => !schema.Any(p => p.Name == k && !p.Hidden)).ToArray())
            {
                linkErrors.Add($"Unknown or unavailable report input: {name}. Input names are case sensitive.");
                incoming.Remove(name);
            }
        }
        if (input.Defaulted?.Length > options.MaxParameters || input.Changed?.Length > options.MaxParameters ||
            (input.Defaulted ?? []).Any(k => !incoming.ContainsKey(k ?? "") || !schema.Any(p => p.Name == k && !p.Hidden)) ||
            incoming.Keys.Any(k => !schema.Any(p => p.Name == k && !p.Hidden)) ||
            (input.Changed ?? []).Any(k => !incoming.ContainsKey(k ?? "") || !schema.Any(p => p.Name == k && !p.Hidden)))
        {
            throw new PortalException("values", 400);
        }
        var current = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var defaulted = new HashSet<string>(input.Defaulted ?? [], StringComparer.Ordinal);
        var edited = new HashSet<string>(input.Changed ?? [], StringComparer.Ordinal);
        var affected = new HashSet<string>(edited, StringComparer.Ordinal);
        defaulted.ExceptWith(edited);
        foreach (var p in schema)
        {
            if (p.Dependencies.Any(affected.Contains))
            {
                affected.Add(p.Name);
            }
            var supplied = !p.Hidden && incoming.TryGetValue(p.Name, out _);
            current[p.Name] = Normalize(p, supplied ? incoming[p.Name] : [], errors);
            if (!supplied || (!edited.Contains(p.Name) && affected.Contains(p.Name) && (current[p.Name].Length == 0 || defaulted.Contains(p.Name))))
            {
                pending.Add(p.Name);
                current[p.Name] = [];
                defaulted.Remove(p.Name);
            }
            // Root defaults are already evaluated in the schema. Dependent defaults
            // must wait for a response evaluated with the current parent values.
            if (pending.Contains(p.Name) && p.Dependencies.Length == 0)
            {
                current[p.Name] = Normalize(p, p.Defaults, errors);
                pending.Remove(p.Name);
                if (!p.Hidden && current[p.Name].Length > 0) { defaulted.Add(p.Name); }
            }
        }
        ReportParameter[] fresh = schema;
        // Each wave can clear invalid selections, then evaluate their omitted defaults.
        // Compare against the values actually sent; never apply child metadata from
        // an older parent context during the same pass. No state survives this request.
        var maxPasses = 2 * schema.Length + 2;
        for (var pass = 0; schema.Length > 0; pass++)
        {
            if (pass >= maxPasses)
            {
                throw new PortalException("query");
            }
            var sent = current.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            fresh = await soap.ParametersAsync(report.Path, Flatten(sent), true, ct);
            VerifyAccess(report, ct);
            ValidateDependencies(fresh);
            if (fresh.Length != schema.Length || fresh.Where((p, i) => p.Name != schema[i].Name || p.Hidden != schema[i].Hidden ||
                p.Type != schema[i].Type || p.Nullable != schema[i].Nullable || p.AllowBlank != schema[i].AllowBlank ||
                p.MultiValue != schema[i].MultiValue || !p.Dependencies.SequenceEqual(schema[i].Dependencies)).Any())
            {
                throw new PortalException("service");
            }
            fresh = fresh.Select(NormalizeOptions).ToArray();
            var changed = false;
            foreach (var p in fresh)
            {
                var prior = current[p.Name];
                string?[] next = prior;
                if (p.Dependencies.Any(d => !current[d].SequenceEqual(sent[d])))
                {
                    continue;
                }
                if (p.Dependencies.Any(d => current[d].Length == 0) || p.State == "HasOutstandingDependencies")
                {
                    next = [];
                    if (prior.Length > 0)
                    {
                        if (!strictExplicit || !incoming.ContainsKey(p.Name)) { pending.Add(p.Name); }
                        else { errors[p.Name] = "Complete the earlier dependent inputs and choose this value again."; }
                    }
                }
                else
                {
                    var validOptions = p.Options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
                    if (HasOptions(p))
                    {
                        next = prior.Where(v => (v == null && p.Nullable) || validOptions.Contains(v)).ToArray();
                        if (prior.Length != next.Length)
                        {
                            if (!strictExplicit || !incoming.ContainsKey(p.Name)) { pending.Add(p.Name); }
                            // Subscription editing must retain a stale explicit choice
                            // and require correction, never save an unrelated default.
                            if (preserveExplicit && incoming.ContainsKey(p.Name)) { next = prior; }
                            if (execute || strictExplicit)
                            {
                                errors[p.Name] = "A previous choice is no longer valid. Choose again.";
                            }
                        }
                    }
                    // A cleared value is not uninitialized. Invalidated children get
                    // defaults only after a SOAP request omits their stale selections.
                    if (pending.Contains(p.Name) && next.Length == 0 && sent[p.Name].Length == 0)
                    {
                        next = Normalize(p, p.Defaults, errors);
                        if (HasOptions(p))
                        {
                            next = next.Where(v => (v == null && p.Nullable) || validOptions.Contains(v)).ToArray();
                        }
                        pending.Remove(p.Name);
                        if (!p.Hidden && next.Length > 0) { defaulted.Add(p.Name); }
                    }
                }
                if (!prior.SequenceEqual(next))
                {
                    current[p.Name] = next;
                    changed = true;
                }
            }
            if (!changed)
            {
                break;
            }
        }
        foreach (var p in fresh)
        {
            if (p.State == "DynamicValuesUnavailable")
            {
                errors[p.Name] = "Choices are temporarily unavailable. Try again later.";
            }
            else if (p.State == "HasOutstandingDependencies" || p.Dependencies.Any(d => current[d].Length == 0))
            {
                errors[p.Name] = "Complete the earlier dependent inputs first.";
            }
            else if (p.State == "MissingValidValue" || current[p.Name].Length == 0)
            {
                errors.TryAdd(p.Name, "Choose or enter a required value.");
            }
            else if (p.State != "HasValidValue")
            {
                throw new PortalException("service");
            }
        }
        if (execute && errors.Count > 0)
        {
            throw new PortalException(fresh.Any(p => p.State == "DynamicValuesUnavailable") ? "query" : "values", 400);
        }
        var visible = fresh.Where(p => !p.Hidden).Select(p =>
        {
            var chosen = current[p.Name].ToHashSet(StringComparer.Ordinal);
            var available = p.Options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
            // Supply display labels in SSRS collection order, independently of
            // click/submission order, without changing execution value semantics.
            var selectedOptions = p.Options.Where(o => chosen.Contains(o.Value)).DistinctBy(o => o.Value)
                .Concat(current[p.Name].Where(v => !available.Contains(v)).Select(v =>
                    new OptionValue(v, v == null ? "No value" : v.Length == 0 ? "(Blank)" : v))).ToArray();
            return new ParameterField(p.Name, p.Prompt, p.Type, p.State,
                p.Nullable, p.AllowBlank, p.MultiValue, p.QueryBased, p.Dependencies, current[p.Name],
                [], p.Options.Length, errors.GetValueOrDefault(p.Name),
                selectedOptions, HasOptions(p), SelectionLimit,
                p.Options.Length > 0 && p.Options.Select(o => o.Value).ToHashSet().SetEquals(current[p.Name]),
                HasOptions(p) ? ChoicesKey(p, fresh, current) : "", FieldWidth(p));
        }).ToArray();
        VerifyAccess(report, ct);
        return (new ParameterForm(visible, current.Where(v => fresh.Any(p => p.Name == v.Key && !p.Hidden))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal), errors.Count == 0,
            schema.Where(p => !p.Hidden && defaulted.Contains(p.Name) && current[p.Name].Length > 0).Select(p => p.Name).ToArray(),
            linkErrors.Count > 0 ? linkErrors.Distinct().ToArray() : null), Flatten(current), fresh);
    }

    public async Task<ParameterChoices> ChoicesAsync(CatalogReport report, ParameterInput input, string parameter, CancellationToken ct)
    {
        var resolved = await ResolveCoreAsync(report, input, false, ct);
        var p = resolved.Metadata.SingleOrDefault(p => p.Name == parameter && !p.Hidden && HasOptions(p))
            ?? throw new PortalException("notfound", 404);
        if (p.State == "DynamicValuesUnavailable") { throw new PortalException("query"); }
        if (p.State == "HasOutstandingDependencies") { throw new PortalException("values", 400); }
        VerifyAccess(report, ct);
        return new(p.Options, resolved.Form.Fields.Single(f => f.Name == parameter).OptionsKey);
    }

    // A browser-only cache identity, never a validity/authorization token. Include the
    // complete SSRS choices and transitive parent values, even when a parent change
    // happens to return identical choices. Length prefixes distinguish null/blank.
    private static string ChoicesKey(ReportParameter parameter, ReportParameter[] metadata, Dictionary<string, string?[]> current)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string? value)
        {
            var bytes = value == null ? [] : Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, value == null ? -1 : bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        Append(parameter.Name);
        Append(parameter.State == "HasOutstandingDependencies" || parameter.State == "DynamicValuesUnavailable" ? parameter.State : "ready");
        var parents = new HashSet<string>(parameter.Dependencies, StringComparer.Ordinal);
        foreach (var p in metadata.Reverse())
        {
            if (parents.Contains(p.Name)) { parents.UnionWith(p.Dependencies); }
        }
        Append(parents.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var p in metadata.Where(p => parents.Contains(p.Name)))
        {
            Append(p.Name);
            Append(current[p.Name].Length.ToString(CultureInfo.InvariantCulture));
            foreach (var value in current[p.Name]) { Append(value); }
        }
        Append(parameter.Options.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var option in parameter.Options) { Append(option.Value); Append(option.Label); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FieldWidth(ReportParameter p)
    {
        if (!p.MultiValue && p.Type == "Boolean") { return "boolean"; }
        if (HasOptions(p)) { return "choice"; }
        if (p.MultiValue) { return "text"; }
        if (p.Type is "Integer" or "Float") { return "number"; }
        if (p.Type == "DateTime") { return "date"; }
        // SSRS supplies no string length hint. Use stable semantic metadata for the
        // few clearly short categories; leave arbitrary names/search/free text roomy.
        var hint = p.Name + " " + p.Prompt;
        if (new[] { "search", "expression", "description", "name", "note", "text" }
            .Any(word => hint.Contains(word, StringComparison.OrdinalIgnoreCase))) { return "text"; }
        return new[] { "year", "code", "direction", "sort order", "sortorder" }
            .Any(word => hint.Contains(word, StringComparison.OrdinalIgnoreCase)) ? "short" : "text";
    }

    public async Task<OptionSelection> SelectAllAsync(CatalogReport report, ParameterInput input, string parameter, CancellationToken ct)
    {
        var resolved = await ResolveCoreAsync(report, input, false, ct);
        var p = resolved.Metadata.SingleOrDefault(p => p.Name == parameter && !p.Hidden && p.MultiValue && HasOptions(p))
            ?? throw new PortalException("notfound", 404);
        if (p.State == "DynamicValuesUnavailable") { throw new PortalException("query"); }
        if (p.State == "HasOutstandingDependencies") { throw new PortalException("values", 400); }
        var choices = p.Options.DistinctBy(o => o.Value).ToArray();
        var selected = choices.Select(o => o.Value).ToArray();
        var values = new Dictionary<string, string?[]>(resolved.Form.Values, StringComparer.Ordinal) { [parameter] = selected };
        if (choices.Length > SelectionLimit || selected.Any(v => v?.Length > 2048) ||
            JsonSerializer.SerializeToUtf8Bytes(new ParameterInput(values, Changed: [parameter], Defaulted: resolved.Form.Defaulted),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length > MaxSelectionRequestBytes)
        {
            throw new PortalException("selection-limit", 400);
        }
        VerifyAccess(report, ct);
        return new(choices);
    }

    private static bool HasOptions(ReportParameter p) => p.QueryBased || p.Options.Length > 0;

    private static ReportParameter NormalizeOptions(ReportParameter p)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = p.Options.Select(o => new OptionValue(Normalize(p, [o.Value], errors).SingleOrDefault(), o.Label)).ToArray();
        if (errors.Count > 0)
        {
            throw new PortalException("service");
        }
        return p with { Options = normalized };
    }

    private void VerifyAccess(CatalogReport report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (report.Policy != PathPolicy.Identity(options) || !PathPolicy.Permitted(report.Path, options))
        {
            throw new PortalException("notfound", 404);
        }
    }

    private static void ValidateDependencies(ReportParameter[] parameters)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].Dependencies.Any(d => !parameters.Take(i).Any(p => p.Name == d)))
            {
                throw new PortalException("service");
            }
        }
    }

    public static ParameterValue[] Flatten(Dictionary<string, string?[]> values) =>
        values.SelectMany(p => p.Value.Select(v => new ParameterValue(p.Key, v))).ToArray();

    private static string?[] Normalize(ReportParameter p, string?[] values, Dictionary<string, string> errors)
    {
        if ((!p.MultiValue && values.Length > 1) || values.Length > MaxSelectionValues)
        {
            errors[p.Name] = "Choose a valid number of values.";
            return [];
        }
        var result = new List<string?>();
        foreach (var value in values.Distinct())
        {
            if (value == null && p.Nullable)
            {
                result.Add(null);
                continue;
            }
            if (value == null || (value.Length == 0 && (p.Type != "String" || !p.AllowBlank)))
            {
                errors[p.Name] = "Enter a valid value or choose no value if permitted.";
                continue;
            }
            string? normalized = p.Type switch
            {
                "Boolean" => value == "1" ? "true" : value == "0" ? "false" : bool.TryParse(value, out var b) ? b.ToString().ToLowerInvariant() : null,
                "Integer" => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null,
                "Float" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && double.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : null,
                "DateTime" => NormalizeDate(value),
                "String" => value,
                _ => null
            };
            if (normalized == null)
            {
                errors[p.Name] = "Enter a value with the expected type.";
            }
            else
            {
                result.Add(normalized);
            }
        }
        return result.Distinct().ToArray();
    }

    private static string? NormalizeDate(string value)
    {
        // DEV GetItemParameters returns this exact SSRS representation. Defaults,
        // choices and incoming values share the same invariant, wall-clock contract.
        if (DateTime.TryParseExact(value, "M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var serverDate))
        {
            return serverDate.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }
        // Optional fractional format digits otherwise also accept a bare decimal point.
        var fraction = value.IndexOf('.');
        if (fraction >= 0 && (fraction + 1 == value.Length || !char.IsAsciiDigit(value[fraction + 1])))
        {
            return null;
        }
        string[] local = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if (DateTime.TryParseExact(value, local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        }

        string[] zoned = ["yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

        if ((value.EndsWith('Z') || (value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-')) &&
            DateTimeOffset.TryParseExact(value, zoned, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
        {
            return value.EndsWith('Z')
                ? offset.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
                : offset.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
        }
        return null;
    }
}
