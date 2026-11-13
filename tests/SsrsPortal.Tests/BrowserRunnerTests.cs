using System.Diagnostics;
using Xunit.Abstractions;

namespace SsrsPortal.Tests;

public sealed class BrowserRunnerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MultipleDotnetCandidatesLaunchTheFirstExecutableInsteadOfJoiningPaths()
    {
        var repository = FindRepository();
        var artifactRoot = Path.GetFullPath(Path.Combine(repository, "artifacts", "runner-regression"));
        var workspace = Path.GetFullPath(Path.Combine(artifactRoot, Guid.NewGuid().ToString("N")));
        Assert.StartsWith(artifactRoot + Path.DirectorySeparatorChar, workspace, StringComparison.OrdinalIgnoreCase);
        var scripts = Directory.CreateDirectory(Path.Combine(workspace, "scripts")).FullName;
        Directory.CreateDirectory(Path.Combine(workspace, "src", "SsrsPortal"));
        File.Copy(Path.Combine(repository, "scripts", "Test-Portal.ps1"), Path.Combine(scripts, "Test-Portal.ps1"));
        var secondSdk = Directory.CreateDirectory(Path.Combine(workspace, "second-sdk")).FullName;
        var invalidDotnet = Path.Combine(secondSdk, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        await File.WriteAllTextAsync(invalidDotnet, "This second SDK candidate must never be executed.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(invalidDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var wrapper = Path.Combine(workspace, "Verify-DotnetSelection.ps1");
        await File.WriteAllTextAsync(wrapper, """
            $ErrorActionPreference = 'Stop'
            $dotnetDirectory = Split-Path -Parent (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
            $env:PATH = $dotnetDirectory + [IO.Path]::PathSeparator + (Join-Path $PSScriptRoot 'second-sdk')
            if (@(Get-Command dotnet -CommandType Application).Count -lt 2) { throw 'Fixture requires multiple SDK candidates.' }
            # Isolate executable selection from browser/certificate prerequisites.
            function node { $global:LASTEXITCODE = 0 }
            function dotnet { $global:LASTEXITCODE = 0 }
            $caught = $false
            try { & (Join-Path $PSScriptRoot 'scripts/Test-Portal.ps1') -BrowserExecutable '' }
            catch {
                # The real SDK must start, then reject this fixture's intentionally
                # absent application DLL. A joined executable path cannot start.
                if ($_.Exception.Message -notmatch 'Synthetic application startup failed') { throw }
                $caught = $true
            }
            if (!$caught) { throw 'Expected missing fixture application failure.' }
            Write-Output 'SDK-RESOLUTION-PASS'
            """);
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
        Assert.True(pwsh is not null, "The cross-platform browser harness requires installed PowerShell 7.");
        var start = new ProcessStartInfo(pwsh!)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", wrapper })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            var diagnostics = await stdout + await stderr;
            output.WriteLine(diagnostics);
            Assert.True(process.ExitCode == 0, diagnostics);
            Assert.Contains("Synthetic HTTPS portal:", diagnostics);
            Assert.Contains("SDK-RESOLUTION-PASS", diagnostics);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrerequisiteFailureRestoresEnvironmentIncludingAbsentAndEmptyValues(bool initiallyEmpty)
    {
        var repository = FindRepository();
        var workspace = Path.Combine(repository, "artifacts", "runner-regression", Guid.NewGuid().ToString("N"));
        var scriptDirectory = Directory.CreateDirectory(Path.Combine(workspace, "scripts")).FullName;
        File.Copy(Path.Combine(repository, "scripts", "Test-Portal.ps1"), Path.Combine(scriptDirectory, "Test-Portal.ps1"));
        var wrapper = Path.Combine(workspace, "Verify-Restoration.ps1");
        await File.WriteAllTextAsync(wrapper, """
            param([switch]$InitiallyEmpty)
            $ErrorActionPreference = 'Stop'
            $env:ASPNETCORE_ENVIRONMENT = 'previous-aspnet'
            $env:DOTNET_ENVIRONMENT = 'previous-dotnet'
            $env:PORTAL_BASE_URL = 'https://previous.invalid/'
            $browser = if ($InitiallyEmpty) { '' } else { [NullString]::Value }
            [Environment]::SetEnvironmentVariable('PORTAL_BROWSER_EXECUTABLE', $browser, 'Process')
            $before = @{}
            foreach ($name in @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','PORTAL_BROWSER_EXECUTABLE','PORTAL_BASE_URL')) {
                $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            }
            $location = (Get-Location).Path
            # Fail after the harness changes its environment, without invoking
            # Node, installing browsers, generating certificates or starting SSRS.
            $env:PATH = ''
            $caught = $false
            try { & (Join-Path $PSScriptRoot 'scripts/Test-Portal.ps1') -BrowserExecutable '' }
            catch {
                if ($_.Exception.Message -notmatch 'node') { throw }
                $caught = $true
            }
            if (!$caught) { throw 'Expected browser prerequisite failure.' }
            foreach ($name in $before.Keys) {
                if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $before[$name]) {
                    throw "Environment not restored: $name"
                }
            }
            if ((Get-Location).Path -cne $location) { throw 'Working directory not restored.' }
            if (Get-ChildItem $PSScriptRoot -Recurse -Filter application.json -File) { throw 'Temporary configuration leaked.' }
            Write-Output 'FAILURE-RESTORATION-PASS'
            """);
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
        Assert.True(pwsh is not null, "The cross-platform browser harness requires installed PowerShell 7.");
        var start = new ProcessStartInfo(pwsh!)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", wrapper })
        {
            start.ArgumentList.Add(argument);
        }
        if (initiallyEmpty) start.ArgumentList.Add("-InitiallyEmpty");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            var diagnostics = await stdout + await stderr;
            output.WriteLine(diagnostics);
            Assert.True(process.ExitCode == 0, diagnostics);
            Assert.Contains("FAILURE-RESTORATION-PASS", diagnostics);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(workspace, recursive: true);
        }
    }

    [WindowsPowerShellFact]
    public async Task WindowsPowerShellFailsBeforeBrowserRunnerSetupWithPwshInstruction()
    {
        var repository = FindRepository();
        var artifactRoot = Path.GetFullPath(Path.Combine(repository, "artifacts", "runner-regression"));
        var workspace = Path.GetFullPath(Path.Combine(artifactRoot, Guid.NewGuid().ToString("N")));
        Assert.StartsWith(artifactRoot + Path.DirectorySeparatorChar, workspace, StringComparison.OrdinalIgnoreCase);
        var scriptDirectory = Directory.CreateDirectory(Path.Combine(workspace, "scripts")).FullName;
        var script = Path.Combine(scriptDirectory, "Test-Portal.ps1");
        File.Copy(Path.Combine(repository, "scripts", "Test-Portal.ps1"), script);
        var browser = Path.Combine(workspace, "existing-browser.exe");
        await File.WriteAllTextAsync(browser, "synthetic placeholder");
        var commandDirectory = Directory.CreateDirectory(Path.Combine(workspace, "commands")).FullName;
        // If prerequisite handling regresses, prevent certificate setup or portal startup.
        await File.WriteAllTextAsync(Path.Combine(commandDirectory, "dotnet.cmd"), "@echo off\r\nexit /b 97\r\n");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell), "This Windows regression requires installed Windows PowerShell 5.1.");
        var start = new ProcessStartInfo(powershell)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-BrowserExecutable", browser })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PATH"] = commandDirectory;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timer = Stopwatch.StartNew();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            timer.Stop();
            var diagnostics = await stdout + await stderr;
            output.WriteLine($"Windows PowerShell exited {process.ExitCode} after {timer.Elapsed.TotalSeconds:F2}s: {diagnostics}");
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("PowerShell 7 or later", diagnostics);
            Assert.Contains("pwsh -NoProfile -File ./scripts/Test-Portal.ps1", diagnostics);
            Assert.DoesNotContain("Synthetic portal did not become ready", diagnostics);
            Assert.False(Directory.Exists(Path.Combine(workspace, "artifacts")), "Unsupported PowerShell must stop before runner setup.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "Test-Portal.ps1")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Cannot locate scripts/Test-Portal.ps1 for the runner regression.");
    }

    private sealed class WindowsPowerShellFactAttribute : FactAttribute
    {
        public WindowsPowerShellFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows PowerShell 5.1 prerequisite behavior requires Windows.";
            }
        }
    }
}
