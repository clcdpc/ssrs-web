using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Mock;
using SsrsPortal.Tests;

namespace SsrsPortal.BrowserChecks;

// Test-only host: inject fixtures before startup without adding production
// configuration, mutable HTTP endpoints or a second branding initialization path.
internal static class BrowserCheckRunner
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--dpapi-probe")
        {
            await DeploymentProcessProbe.RunAsync(args[1..]);
            return;
        }
        var root = Path.GetFullPath(args.Single());
        foreach (var mode in new[] { "valid", "unavailable" })
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new FirstPageProbe();
            // Qualify the additional navigation against the real inherited dark
            // branding and wide PNG in the successful cold-start scenario.
            var configuration = mode == "valid" ? new Dictionary<string, string?>
            {
                ["Subscriptions:Enabled"] = "true", ["Subscriptions:ConnectionString"] = "Server=localhost;Database=SsrsWeb;Integrated Security=true;Encrypt=true",
                ["Subscriptions:PortalBaseUrl"] = "https://localhost", ["Subscriptions:From"] = "reports@example.invalid", ["Subscriptions:PostmarkServerToken"] = "synthetic-only"
            } : null;
            await using var factory = new PortalFactory(configuration, configureServices: services =>
            {
                services.AddSingleton(new SyntheticSsrsHandler { Branding = SyntheticBranding.Dark with { Logo = SyntheticBranding.SsrsWideLogo, Gate = gate.Task } });
                services.Configure<MvcOptions>(options => options.Filters.Add(probe));
            });
            await using var host = factory.WithWebHostBuilder(builder => builder.UseContentRoot(Path.Combine(root, "src", "SsrsPortal")));
            host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(root, "tests", "browser", "browser-branding-startup.cjs"));
            start.ArgumentList.Add(mode);
            var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PORTAL_BROWSER_EXECUTABLE")) && File.Exists(edge))
            { start.Environment["PORTAL_BROWSER_EXECUTABLE"] = edge; }
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            SyntheticSsrsHandler? mock = null;
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(deadline.Token)) is not null)
                {
                    if (line == "BROWSER_READY")
                    {
                        // Launch Chromium first so its startup time cannot consume
                        // the real, five-second SSRS deadline of this fresh host.
                        var server = host.Services.GetRequiredService<IServer>();
                        mock = host.Services.GetRequiredService<SyntheticSsrsHandler>();
                        while (mock.BrandingCalls.Count != 2) { await Task.Delay(5, deadline.Token); }
                        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
                        await process.StandardInput.WriteLineAsync(address);
                        await process.StandardInput.FlushAsync();
                    }
                    else if (line == "INITIAL_PAGE_REQUESTED")
                    {
                        await probe.Entered.Task.WaitAsync(deadline.Token);
                        if (mode == "valid") { gate.TrySetResult(); }
                        // Unavailable mode deliberately waits for the real deadline.
                    }
                    else if (line == "EXPIRE_FOR_OUTAGE")
                    {
                        mock!.Branding = SyntheticBranding.Dark with { Status = HttpStatusCode.ServiceUnavailable };
                        factory.Clock.Advance(TimeSpan.FromMinutes(10));
                        await process.StandardInput.WriteLineAsync("OUTAGE_READY");
                        await process.StandardInput.FlushAsync();
                    }
                    else { Console.WriteLine(line); }
                }
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode != 0) { throw new InvalidOperationException(await stderr); }
                if (mock is null || mock.BrandingCalls.Count != 2 || mock.BrandingCalls.Values.Any(count => count != (mode == "valid" ? 2 : 1)))
                { throw new InvalidOperationException("Cold/warm page requests duplicated the shared branding retrieval."); }
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        await BreadcrumbChecks.RunAsync(root);
    }

    private sealed class FirstPageProbe : IAsyncResultFilter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if (context.HttpContext.User.Identity?.IsAuthenticated == true && context.HttpContext.Request.Path == "/") { Entered.TrySetResult(); }
            await next();
        }
    }
}
