using System.Text.Json;
using System.Text.Json.Nodes;
using SsrsPortal.Core;
using SsrsPortal.Security;

namespace SsrsPortal.Tests;

// Each fixture owns its file; no process-global hook or operational ProgramData access.
public sealed class TestConfigurationFile : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SsrsPortal.Tests", Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public JsonObject Document { get; } = new()
    {
        ["AllowedHosts"] = "localhost",
        ["Portal"] = JsonSerializer.SerializeToNode(new PortalOptions
        {
            UseMock = true, TestAuth = true, ReportServer = "https://synthetic.invalid/ReportServer",
            ReportAccessPolicy = "GlobalBlocklist", AllowedRoots = ["/Training"],
            ExcludedPrefixes = ["/Training/Custom_Restored", "/Training/Internal"]
        }),
        ["Entra"] = JsonSerializer.SerializeToNode(new EntraOptions
        {
            ClientId = AuthenticationTests.Client,
            AllowedTenantIds = [AuthenticationTests.Tenant, AuthenticationTests.SecondTenant]
        }),
        ["CatalogMonitor"] = JsonSerializer.SerializeToNode(new CatalogMonitorOptions())
    };

    public TestConfigurationFile(Dictionary<string, string?>? overrides = null)
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "application.json");
        foreach (var pair in overrides ?? [])
        {
            var parts = pair.Key.Split(':');
            JsonNode current = Document;
            foreach (var part in parts[..^1])
            {
                current = current[part] ??= new JsonObject();
            }
            if (current is JsonArray array)
            {
                var index = int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture);
                while (array.Count <= index) { array.Add(null); }
                array[index] = JsonValue.Create(pair.Value);
            }
            else
            {
                current[parts[^1]] = JsonValue.Create(pair.Value);
            }
        }
        Save();
    }

    public void Save() => File.WriteAllText(Path, Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }
}
