using System.Xml.Linq;
using SsrsPortal.Core;
using static SsrsPortal.Soap.SoapTransport;

namespace SsrsPortal.Soap;

public sealed class SsrsClient(SoapTransport transport, PortalOptions options)
{
    public async Task<CatalogItem[]> ListAsync(string root, CancellationToken ct)
    {
        var response = await transport.CallAsync("ListChildren", false,
            [Element(Management, "ItemPath", root), Element(Management, "Recursive", "true")], null, ct);
        var collection = Required(response, Management, "CatalogItems");
        var items = collection.Elements(Management + "CatalogItem").ToArray();
        if (items.Length > options.MaxMetadataItems)
        {
            throw ContractFailure(response, "ListChildren returned more than MaxMetadataItems.", code: "limit");
        }
        return items.Select(x => new CatalogItem(Text(x, Management, "Path"), Text(x, Management, "Name"),
            Text(x, Management, "TypeName"), Hidden(x, response))).ToArray();
    }

    private static bool Hidden(XElement item, XElement response)
    {
        var value = item.Element(Management + "Hidden")?.Value.Trim();

        return value?.ToLowerInvariant() switch
        {
            null or "false" or "0" => false,
            "true" or "1" => true,
            _ => throw ContractFailure(
                response,
                $"ListChildren returned an invalid Hidden value '{value}' " +
                $"for catalog item '{Text(item, Management, "Path")}'.")
        };
    }

    public async Task<bool> VisibleFolderAsync(string path, CancellationToken ct)
    {
        var response = await transport.CallAsync("GetProperties", false,
            [Element(Management, "ItemPath", path), new XElement(Management + "Properties",
                new XElement(Management + "Property", Element(Management, "Name", "Hidden")))], null, ct);
        var values = Required(response, Management, "Values");
        var properties = values.Elements(Management + "Property").ToArray();
        if (response.Elements(Management + "Values").Count() != 1 || values.Elements().Count() != 1 ||
            properties.Length != 1 || Text(properties[0], Management, "Name") != "Hidden" ||
            !bool.TryParse(Text(properties[0], Management, "Value"), out var hidden))
        {
            throw ContractFailure(response, "GetPropertiesResponse/Values must contain one Hidden Property with a Boolean Value.");
        }
        return !hidden;
    }

    public async Task<ReportParameter[]> ParametersAsync(string path, IEnumerable<ParameterValue> values, bool rendering, CancellationToken ct)
    {
        var selected = values.ToArray();
        var args = new List<XElement> { Element(Management, "ItemPath", path), Element(Management, "ForRendering", rendering ? "true" : "false") };
        if (selected.Length > 0)
        {
            args.Add(Values(Management, "Values", selected));
        }
        var response = await transport.CallAsync("GetItemParameters", false, args, null, ct);
        var collection = Required(response, Management, "Parameters");
        var parameters = collection.Elements(Management + "ItemParameter").ToArray();
        if (parameters.Length > options.MaxParameters)
        {
            throw ContractFailure(response, "GetItemParameters returned more than MaxParameters.", code: "limit");
        }
        var result = new List<ReportParameter>();
        foreach (var p in parameters)
        {
            var valid = p.Element(Management + "ValidValues")?.Elements(Management + "ValidValue").Select(v =>
                new OptionValue(v.Element(Management + "Value")?.Value, Text(v, Management, "Label"))).ToArray() ?? [];
            if (valid.Length > options.MaxOptions)
            {
                throw ContractFailure(response, "GetItemParameters returned more than MaxOptions.", code: "limit");
            }
            var type = Text(p, Management, "ParameterTypeName");
            if (type is not "Boolean" and not "Integer" and not "Float" and not "String" and not "DateTime")
            {
                throw ContractFailure(response, "GetItemParameters returned a missing or unsupported ParameterTypeName.");
            }
            result.Add(new ReportParameter(Text(p, Management, "Name"), Text(p, Management, "Prompt"), type,
                Text(p, Management, "ParameterStateName"), Flag(p, Management, "Nullable"), Flag(p, Management, "AllowBlank"),
                Flag(p, Management, "MultiValue"), !Flag(p, Management, "PromptUser"), Flag(p, Management, "ValidValuesQueryBased"), Flag(p, Management, "DefaultValuesQueryBased"),
                p.Element(Management + "Dependencies")?.Elements(Management + "Dependency").Select(x => x.Value).ToArray() ?? [],
                p.Element(Management + "DefaultValues")?.Elements(Management + "Value").Select(x =>
                    (string?)(((string?)x.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance")))?.Trim() is "true" or "1" ? null : x.Value)).ToArray() ?? [], valid));
        }
        if (result.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != result.Count ||
            result.Any(p => string.IsNullOrEmpty(p.Name) || p.Name.Length > 128))
        {
            throw ContractFailure(response, "GetItemParameters returned missing, duplicate or invalid parameter names.");
        }
        return result.ToArray();
    }

    public async Task<ExecutionInfo> LoadAsync(string path, CancellationToken ct)
    {
        var response = await transport.CallAsync("LoadReport", true, [Element(Execution, "Report", path)], null, ct);
        return Info(response);
    }

    public async Task<ExecutionInfo> SetAsync(string id, IEnumerable<ParameterValue> values, CancellationToken ct)
    {
        var response = await transport.CallAsync("SetExecutionParameters", true,
            [Values(Execution, "Parameters", values), Element(Execution, "ParameterLanguage", "en-US")], id, ct);
        return Info(response);
    }

    public async Task<ExecutionInfo> InfoAsync(string id, CancellationToken ct, bool includeFailureContent = true)
    {
        var response = await transport.CallAsync("GetExecutionInfo", true, [], id, ct, includeFailureContent);
        return Info(response);
    }

    public async Task<bool> ToggleAsync(string id, string toggleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(toggleId) || toggleId.Length > 256 || toggleId.Any(char.IsControl))
        {
            throw new PortalException("values", 400);
        }
        var response = await transport.CallAsync("ToggleItem", true, [Element(Execution, "ToggleID", toggleId)], id, ct);
        var children = response.Elements().ToArray();
        if (response.Attributes().Any(a => !a.IsNamespaceDeclaration) ||
            children.Length != 1 || children[0].Name != Execution + "Found" || children[0].HasElements ||
            children[0].Attributes().Any(a => !a.IsNamespaceDeclaration) ||
            response.Nodes().OfType<XText>().Any(t => t.Value.Any(c => c is not (' ' or '\t' or '\r' or '\n'))))
        {
            throw ContractFailure(response, "ToggleItemResponse must contain exactly one qualified Boolean Found.");
        }
        // WSDL xsd:boolean: Found describes recognition, never expanded/collapsed state.
        return children[0].Value.Trim(' ', '\t', '\r', '\n') switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw ContractFailure(response, "ToggleItemResponse contains an invalid Found Boolean.")
        };
    }

    private static ExecutionInfo Info(XElement response)
    {
        var x = Required(response, Execution, "executionInfo");
        if (Text(x, Execution, "ExecutionID").Length is < 1 or > 256 ||
            !int.TryParse(Text(x, Execution, "NumPages"), out var pages) || pages < 0 || pages > 10000)
        {
            throw ContractFailure(response, "executionInfo has missing or invalid ExecutionID or NumPages.");
        }
        bool RequiredFlag(string name) => Text(x, Execution, name).Trim() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw ContractFailure(response, $"executionInfo has a missing or invalid {name} boolean.")
        };
        return new ExecutionInfo(Text(x, Execution, "ExecutionID"), pages, RequiredFlag("CredentialsRequired"), RequiredFlag("ParametersRequired"));
    }

    public async Task<RenderOutput> RenderAsync(string id, string format, int section, CancellationToken ct, string? actionScript = null,
        bool includeFailureContent = true)
    {
        var device = new XElement("DeviceInfo", new XElement("HTMLFragment", "True"), new XElement("Section", section));
        if (actionScript != null)
        {
            // Per-render, unpredictable metadata for the sanitizer. Never executed.
            device.Add(new XElement("ActionScript", actionScript));
        }
        var response = await transport.CallAsync("Render", true, [Element(Execution, "Format", format),
            Element(Execution, "DeviceInfo", format == "HTML5" ? device.ToString(SaveOptions.DisableFormatting) : "<DeviceInfo/>")], id, ct, includeFailureContent);
        var streams = response.Element(Execution + "StreamIds")?.Elements(Execution + "string").Select(x => x.Value).ToArray() ?? [];
        if (streams.Length > options.MaxStreams || streams.Any(s => s.Length is < 1 or > 256 || s.Any(char.IsControl)) || streams.Distinct().Count() != streams.Length)
        {
            throw ContractFailure(response, "Render returned too many, duplicate or invalid StreamIds.", code: "limit");
        }
        return new RenderOutput(Decode(response), Required(response, Execution, "MimeType").Value,
            Text(response, Execution, "Extension"), Text(response, Execution, "Encoding"), streams);
    }

    public async Task<RenderOutput> StreamAsync(string id, string stream, CancellationToken ct, bool includeFailureContent = true)
    {
        var response = await transport.CallAsync("RenderStream", true, [Element(Execution, "Format", "HTML5"),
            Element(Execution, "StreamID", stream), Element(Execution, "DeviceInfo", "<DeviceInfo/>")], id, ct, includeFailureContent);
        return new RenderOutput(Decode(response), Required(response, Execution, "MimeType").Value, "", Text(response, Execution, "Encoding"), []);
    }

    private byte[] Decode(XElement response)
    {
        var base64 = Required(response, Execution, "Result").Value;
        if (base64.Length > ((options.MaxOutputBytes + 2L) / 3) * 4)
        {
            throw ContractFailure(response, "SOAP Result exceeds MaxOutputBytes.", code: "limit");
        }
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0)
            {
                throw ContractFailure(response, "SOAP Result contains no report output.", code: "empty");
            }
            if (bytes.Length > options.MaxOutputBytes)
            {
                throw ContractFailure(response, "Decoded SOAP Result exceeds MaxOutputBytes.", code: "limit");
            }
            return bytes;
        }
        catch (FormatException exception)
        {
            throw ContractFailure(response, "SOAP Result is not valid base64.", exception);
        }
    }
}
