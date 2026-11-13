[CmdletBinding()]
param(
    [string]$NuGetConfigPath,
    [string]$PackagesDirectory
)

$ErrorActionPreference = 'Stop'
$consumerProject = Join-Path $PSScriptRoot '..\src\SsrsPortal\SsrsPortal.csproj'
[xml]$consumer = Get-Content -LiteralPath $consumerProject -Raw
$package = $consumer.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Clc.Configuration"]')
if ($null -eq $package -or [string]::IsNullOrWhiteSpace($package.Version)) {
    throw 'Clc.Configuration package version is not declared in SsrsPortal.csproj.'
}
$packageVersion = $package.Version.Trim('[', ']')

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workRoot = Join-Path $tempRoot ('clc-configuration-publish-' + [guid]::NewGuid().ToString('N'))
$resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRoot)
if (!$resolvedWorkRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish-guard fixture path must remain under the system temporary directory.'
}

function Invoke-DotnetCaptured {
    param([string[]]$Arguments)

    $lines = & dotnet @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($lines -join [System.Environment]::NewLine)
    }
}

try {
    $fixtureDirectory = Join-Path $workRoot 'consumer'
    $sourceDirectory = Join-Path $fixtureDirectory 'fixtures'
    $sourceConfigDirectory = Join-Path $sourceDirectory 'Config'
    $buildDirectory = Join-Path $workRoot 'build'
    $blockedPublish = Join-Path $workRoot 'publish-blocked'
    $allowedPublish = Join-Path $workRoot 'publish-allowed'
    New-Item -ItemType Directory -Path $sourceConfigDirectory -Force | Out-Null

    [System.IO.File]::WriteAllText((Join-Path $fixtureDirectory 'Program.cs'), "System.Console.WriteLine(`"publish guard fixture`" );`n")
    [System.IO.File]::WriteAllText((Join-Path $sourceDirectory 'application.json'), '{}')
    [System.IO.File]::WriteAllText((Join-Path $sourceDirectory 'application.Example.json'), '{}')
    [System.IO.File]::WriteAllText((Join-Path $sourceConfigDirectory 'example.json'), '{}')

    $projectFile = Join-Path $fixtureDirectory 'PublishGuardFixture.csproj'
    $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Clc.Configuration" Version="$packageVersion" />
    <Content Include="fixtures\application.json" CopyToPublishDirectory="Always" />
    <Content Include="fixtures\application.Example.json" CopyToPublishDirectory="Always" />
    <Content Include="fixtures\Config\example.json" CopyToPublishDirectory="Always" />
  </ItemGroup>
</Project>
"@
    [System.IO.File]::WriteAllText($projectFile, $projectText)

    $restoreArguments = @('restore', $projectFile, '--artifacts-path', $buildDirectory)
    if (![string]::IsNullOrWhiteSpace($NuGetConfigPath)) {
        $restoreArguments += @('--configfile', $NuGetConfigPath)
    }
    if (![string]::IsNullOrWhiteSpace($PackagesDirectory)) {
        $restoreArguments += @('--packages', $PackagesDirectory)
    }
    $restore = Invoke-DotnetCaptured -Arguments $restoreArguments
    if ($restore.ExitCode -ne 0) {
        throw "Publish-guard fixture restore failed: $($restore.Output)"
    }

    $blocked = Invoke-DotnetCaptured -Arguments @('publish', $projectFile, '--no-restore', '--artifacts-path', $buildDirectory, '--output', $blockedPublish)
    if ($blocked.ExitCode -eq 0 -or $blocked.Output -notmatch 'CLC_CONFIG001') {
        throw "An operational application.json publish item was not rejected by CLC_CONFIG001: $($blocked.Output)"
    }
    if (Test-Path -LiteralPath (Join-Path $blockedPublish 'fixtures\application.json')) {
        throw 'The rejected operational application.json was copied into publish output.'
    }

    $projectText = $projectText.Replace(
        '<Content Include="fixtures\application.json" CopyToPublishDirectory="Always" />',
        '<Content Include="fixtures\application.json" CopyToPublishDirectory="Never" />')
    [System.IO.File]::WriteAllText($projectFile, $projectText)
    $allowed = Invoke-DotnetCaptured -Arguments @('publish', $projectFile, '--no-restore', '--artifacts-path', $buildDirectory, '--output', $allowedPublish)
    if ($allowed.ExitCode -ne 0) {
        throw "Publish-guard fixture rejected permitted examples: $($allowed.Output)"
    }
    foreach ($example in @('fixtures\application.Example.json', 'fixtures\Config\example.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $allowedPublish $example) -PathType Leaf)) {
            throw "Permitted example configuration was not published: $example"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $allowedPublish 'fixtures\application.json')) {
        throw 'The excluded operational application.json appeared in the permitted publish output.'
    }

    Write-Output "Clc.Configuration $packageVersion publish guard rejected operational application.json and allowed examples."
}
finally {
    if (Test-Path -LiteralPath $resolvedWorkRoot -PathType Container) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}
