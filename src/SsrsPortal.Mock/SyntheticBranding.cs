using System.Net;
using System.Text;

namespace SsrsPortal.Mock;

/// <summary>Invented, deterministic REST fixtures; no network fallback exists in mock mode.</summary>
public sealed record SyntheticBranding(byte[]? Logo, byte[]? Colors)
{
    public static byte[] WideLogo => ReadLogo("wide-logo.png");
    public static byte[] CompactLogo => ReadLogo("compact-logo.png");
    public static byte[] SsrsWideLogo => ReadLogo("ssrs-wide-logo.png");
    public static byte[] SmallLogo => ReadLogo("small-logo.png");
    public static byte[] WhiteLogo => ReadLogo("transparent-white.png");
    public static byte[] BlackLogo => ReadLogo("transparent-black.png");
    public static readonly SyntheticBranding Missing = new(null, null);
    public static SyntheticBranding Dark => new(WideLogo, Encoding.UTF8.GetBytes("""
        {"name":"Synthetic training","version":"1.0","interface":{"primary":"#126080","secondary":"#183A53","secondaryContrast":"#FFFFFF"}}
        """));
    public static SyntheticBranding Light => new(CompactLogo, Encoding.UTF8.GetBytes("""
        {"name":"Synthetic development","version":"2.0","interface":{"secondary":"#E9DDAF","secondaryContrast":"#213951"}}
        """));
    public static SyntheticBranding Malformed => new(Encoding.UTF8.GetBytes("<html>Unavailable</html>"), Encoding.UTF8.GetBytes("{\"interface\":[]}"));
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    public string LogoContentType { get; init; } = "application/octet-stream";
    public string ColorsContentType { get; init; } = "application/json";
    public string? ContentEncoding { get; init; }
    public Task? Gate { get; init; }

    private static byte[] ReadLogo(string name)
    {
        using var stream = typeof(SyntheticBranding).Assembly.GetManifestResourceStream("SsrsPortal.Mock.Branding." + name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
