using System.Text;
using SsrsPortal.Core;

namespace SsrsPortal.Services;

// Request.Query combines names without case sensitivity. SSRS does not.
// Parse the raw query into an ordinal map, retaining each repeated selection.
internal sealed record ReportLinkQuery(Dictionary<string, string?[]> Values, bool HasParameters, bool NoExecute, string[] Errors)
{
    internal static string WithoutLegacyControls(string query)
    {
        var pairs = (query.StartsWith('?') ? query[1..] : query).Split('&', StringSplitOptions.RemoveEmptyEntries).Where(pair =>
        {
            var split = pair.IndexOf('=');
            try
            {
                var key = ReportUrl.Decode(split < 0 ? pair : pair[..split], true);
                return !key.Equals("id", StringComparison.OrdinalIgnoreCase) && !key.Equals("token", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException or EncoderFallbackException)
            {
                return true; // Keep malformed input for the normal link validation feedback.
            }
        }).ToArray();
        return pairs.Length == 0 ? "" : "?" + string.Join('&', pairs);
    }

    internal static ReportLinkQuery Parse(string? query, PortalOptions options)
    {
        var values = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var errors = new List<string>();
        var nulls = new HashSet<string>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        var noExecute = false;
        var controlSeen = false;
        var hasParameters = false;
        if (query?.Length > 224 * 1024) { return new(new(StringComparer.Ordinal), true, false, ["The report link exceeds the supported request size."]); }
        try
        {
            foreach (var pair in (query?.StartsWith('?') == true ? query[1..] : query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.IndexOf('=');
                string key, value;
                try
                {
                    key = ReportUrl.Decode(split < 0 ? pair : pair[..split], true);
                    value = ReportUrl.Decode(split < 0 ? "" : pair[(split + 1)..], true);
                }
                catch (Exception error) when (error is FormatException or DecoderFallbackException or EncoderFallbackException)
                {
                    errors.Add("The report link contains malformed input encoding.");
                    hasParameters = true;
                    continue; // Preserve other valid selections regardless of query order.
                }
                if (key == "portal:noExecute")
                {
                    if (controlSeen || split < 0 || value is not ("true" or "false" or "1" or "0"))
                    {
                        errors.Add("Use portal:noExecute once, with true, false, 1 or 0.");
                    }
                    controlSeen = true;
                    noExecute |= value is "true" or "1";
                    continue;
                }
                hasParameters = true;
                var isNull = key.EndsWith(":isnull", StringComparison.Ordinal);
                var name = isNull ? key[..^7] : key;
                if (name.Length is 0 or > 128 || name.Any(char.IsControl) || name.Contains(':') || split < 0)
                {
                    errors.Add("The report link contains an unsupported input name or syntax.");
                    continue;
                }
                if (!values.TryGetValue(name, out var selections))
                {
                    if (values.Count >= options.MaxParameters) { throw new FormatException(); }
                    values.Add(name, selections = []);
                }
                if (value.Length > 2048 || value.Contains('\0') || selections.Count >= Math.Min(options.MaxOptions, 5000))
                {
                    invalid.Add(name);
                    errors.Add($"Input {name} exceeds the supported value or selection limit.");
                    continue;
                }
                if ((isNull && (value != "true" || selections.Count > 0)) || (!isNull && nulls.Contains(name)))
                {
                    invalid.Add(name);
                    errors.Add($"Input {name} has conflicting or unsupported null selections.");
                }
                if (isNull) { nulls.Add(name); }
                selections.Add(isNull ? null : value);
            }
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or EncoderFallbackException)
        {
            // No partial, truncated query may become eligible for execution.
            errors.Add("The report link contains malformed encoding or too many inputs.");
        }
        return new(values.ToDictionary(p => p.Key, p => invalid.Contains(p.Key) ? [] : p.Value.ToArray(), StringComparer.Ordinal),
            hasParameters, noExecute, errors.Distinct().ToArray());
    }
}
