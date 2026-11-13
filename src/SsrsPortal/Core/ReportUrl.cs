using System.Text;

namespace SsrsPortal.Core;

// Decode once, before any normalization. Catalog paths are names, not filesystem paths.
public static class ReportUrl
{
    public static string For(CatalogReport report) => ForCatalogPath(report.Path);
    internal static string ForCatalogPath(string path) => "/report/" +
        string.Join('/', path[1..].Split('/').Select(Uri.EscapeDataString));

    public static string ParsePath(string rawPath)
    {
        if (!rawPath.StartsWith("/report/", StringComparison.OrdinalIgnoreCase) || rawPath.Length > 8192)
        {
            throw new PortalException("notfound", 404);
        }
        try
        {
            var segments = rawPath[8..].Split('/').Select(p => Decode(p, false)).ToArray();
            if (segments.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('/') || p.Contains('\\') || p.Any(char.IsControl)))
            {
                throw new FormatException();
            }
            var path = "/" + string.Join('/', segments);
            if (!PathPolicy.CatalogPath(path)) { throw new FormatException(); }
            return path;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or EncoderFallbackException)
        {
            throw new PortalException("notfound", 404);
        }
    }

    internal static string Decode(string encoded, bool query)
    {
        var utf8 = new UTF8Encoding(false, true);
        using var bytes = new MemoryStream();
        for (var i = 0; i < encoded.Length;)
        {
            if (encoded[i] == '%')
            {
                if (i + 2 >= encoded.Length || !Uri.IsHexDigit(encoded[i + 1]) || !Uri.IsHexDigit(encoded[i + 2]))
                {
                    throw new FormatException();
                }
                bytes.WriteByte(Convert.ToByte(encoded.Substring(i + 1, 2), 16));
                i += 3;
            }
            else
            {
                var end = encoded.IndexOf('%', i);
                if (end < 0) { end = encoded.Length; }
                var literal = encoded[i..end];
                bytes.Write(utf8.GetBytes(query ? literal.Replace('+', ' ') : literal));
                i = end;
            }
        }
        return utf8.GetString(bytes.ToArray());
    }
}
