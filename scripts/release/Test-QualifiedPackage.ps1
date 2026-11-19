#Requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ToolkitRoot,
    [Parameter(Mandatory)][string] $OutputDirectory
)
. "$PSScriptRoot/Release.Common.ps1"
$PSNativeCommandUseErrorActionPreference = $true
$ToolkitRoot = (Resolve-Path -LiteralPath $ToolkitRoot).Path
$repository = $script:ReleaseRepositoryRoot
$configPath = Join-Path $repository '.clc/release.json'
$pin = (Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json).orchestration.sharedRevision
if ((git -C $ToolkitRoot rev-parse HEAD) -cne $pin) { throw 'Test toolkit revision differs from the consumer pin.' }
. (Join-Path $ToolkitRoot 'tools/release/Workflow.Common.ps1')
$config = Read-ClcConsumerConfiguration $configPath
Import-Module (Join-Path $ToolkitRoot 'src/Clc.Deployment/Clc.Deployment.psd1') -Force

$work = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $work
$distribution = & (Join-Path $ToolkitRoot 'tools/New-ToolkitDistribution.ps1') -RepositoryRoot $ToolkitRoot -SourceCommit $pin -OutputPath (Join-Path $work 'toolkit.zip')
$releaseLockPath = Join-Path $repository 'src/SsrsPortal/packages.release.lock.json'
$releaseLock = Read-ReleaseJson ([IO.File]::ReadAllBytes($releaseLockPath))
$companionLock = $releaseLock.dependencies['net10.0']['Clc.Deployment.AspNetCore']
$companion = "Clc.Deployment.AspNetCore/$($companionLock.resolved)"
$ordinary = Join-Path $work 'ordinary'
$sourceCommit = '1111111111111111111111111111111111111111'
Push-Location $repository
try {
    dotnet restore src/SsrsPortal/SsrsPortal.csproj --locked-mode
    dotnet publish src/SsrsPortal/SsrsPortal.csproj -c Release --no-restore --output $ordinary
    $sdk = (dotnet --version).Trim()
} finally {
    Pop-Location
}

# HostedReleaseExecution exercises the prequalified entry point from a fresh process.
# Use the public module to package the actual SSRS publish for each channel without GitHub tags.
foreach ($tag in @('v1.2.3-dev.1', 'v1.2.3', 'v1.2.3-beta.1', 'v1.2.3-rc.1')) {
    $identity = Get-ClcApplicationTag $tag
    $route = Get-ClcDeploymentRoute $config @{ identity = $identity }
    $publish = Join-Path $work $identity.releaseId
    & "$PSScriptRoot/Publish-QualifiedPortal.ps1" -ToolkitRoot $ToolkitRoot -Tag $tag -SourceCommit $sourceCommit `
        -PublishDirectory $publish -OrdinaryPublishDirectory $ordinary
    $metadata = @{
        contractFamily = 'clc-iis-release'; schemaVersion = 3; recordKind = 'release-metadata'
        applicationId = $config.applicationId; targetId = $route.targetId; releaseId = $identity.releaseId
        source = @{ repository = $config.repository; commit = $sourceCommit }
        build = @{ workflowPath = $config.producer.workflowPath; runId = 1; runAttempt = 1; job = $config.producer.job; sdkVersion = $sdk; dependencyLockSha256 = $null }
        builder = @{ module = 'Clc.Deployment'; version = $distribution.moduleVersion; sourceCommit = $pin; distributionSha256 = $distribution.distributionSha256 }
        runtime = @{ framework = 'net10.0'; rid = 'win-x64'; architecture = 'x64'; hostingModel = 'inprocess'; workerLimit = 1 }
        changeKind = 'files-only'; validation = @{ contractVersion = 1; entryPoint = 'SsrsPortal.dll' }; readinessContractVersion = 1
    }
    $metadataPath = Join-Path $work "$($identity.releaseId).json"
    $packageDirectory = Join-Path $work "$($identity.releaseId)-package"
    $null = New-Item -ItemType Directory -Path $packageDirectory
    $archive = Join-Path $packageDirectory 'release.zip'
    $metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM
    $null = New-ClcRelease -PublishRoot $publish -MetadataPath $metadataPath -OutputPath $archive
    $source = @{ identity = @{ repository = $config.repository; sourceCommit = $sourceCommit; releaseId = $identity.releaseId; channel = $identity.channel } }
    $producer = @{ workflowPath = $config.producer.workflowPath; runId = 1; runAttempt = 1; buildJob = $config.producer.job }
    $inspection = Get-ClcVerifiedPackage $config $source $producer $archive
    if (-not $inspection.integrityVerified -or $inspection.packageContractVersion -ne 3 -or
        $inspection.applicationId -cne $config.applicationId -or $inspection.sourceCommit -cne $sourceCommit -or
        $inspection.releaseId -cne $identity.releaseId -or $inspection.targetId -cne $route.targetId) {
        throw "Package identity disagrees with the release route for $tag."
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.StartsWith('application/', [StringComparison]::Ordinal)) {
                Assert-PortalContentName $entry.FullName.Substring('application/'.Length)
            }
        }
        $manifest = Read-ReleaseJson (Read-ReleaseEntry ($zip.GetEntry('manifest.json')))
        if ($manifest.build.workflowPath -cne $config.producer.workflowPath -or $manifest.build.job -cne $config.producer.job -or
            $manifest.validation.contractVersion -ne 1 -or $manifest.validation.entryPoint -cne 'SsrsPortal.dll' -or
            $manifest.readinessContractVersion -ne 1 -or $manifest.builder.sourceCommit -cne $pin -or
            $manifest.builder.version -cne $distribution.moduleVersion) {
            throw "Release provenance or application protocol disagrees with the consumer contract for $tag."
        }
        $dependencies = Read-ReleaseJson (Read-ReleaseEntry ($zip.GetEntry('application/SsrsPortal.deps.json')))
        $library = $dependencies.libraries[$companion]
        if ($null -eq $library -or $library.sha512 -cne "sha512-$($companionLock.contentHash)") {
            throw "Package for $tag does not contain the locked companion dependency $companion."
        }
    } finally {
        $zip.Dispose()
    }
}
Write-Output 'Real SSRS publish output, exact toolkit package contract, tag identity and content isolation verified for DEV, Production, Beta and RC.'
