#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ToolkitRoot,
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $SourceCommit,
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $OrdinaryPublishDirectory
)
. "$PSScriptRoot/Release.Common.ps1"
$PSNativeCommandUseErrorActionPreference = $true
$repository = $script:ReleaseRepositoryRoot
. (Join-Path (Resolve-Path -LiteralPath $ToolkitRoot).Path 'tools/release/Workflow.Common.ps1')
$config = Read-ClcConsumerConfiguration (Join-Path $repository '.clc/release.json')
$identity = Get-ClcApplicationTag $Tag
$route = Get-ClcDeploymentRoute $config @{ identity = $identity }
$targetId = [string]$route.targetId
$publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
if ([IO.Path]::Exists($publishRoot)) { throw 'The qualified publish directory must be new.' }
Assert-OrdinaryPublishShutdownTimeLimit (Resolve-Path -LiteralPath $OrdinaryPublishDirectory).Path

$project = Join-Path $repository 'src/SsrsPortal/SsrsPortal.csproj'
Push-Location $repository
try {
    $sdk = (Get-Content -LiteralPath global.json -Raw | ConvertFrom-Json).sdk.version
    if ((dotnet --version).Trim() -cne $sdk) { throw 'Use the SDK version selected in global.json.' }
    $properties = @(
        '-p:ClcIdentityKind=Release'
        "-p:ClcReleaseId=$($identity.releaseId)"
        "-p:ClcSourceCommit=$SourceCommit"
        "-p:ClcReleaseTag=$Tag"
        "-p:ClcTargetId=$targetId"
    )
    dotnet restore $project --locked-mode --runtime win-x64 -p:ClcIdentityKind=Release
    dotnet publish $project -c Release --runtime win-x64 --self-contained false --no-restore `
        -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false @properties --output $publishRoot
} finally {
    Pop-Location
}
$metadata = Get-ClcEmbeddedMetadata (Join-Path $publishRoot 'SsrsPortal.dll')
Assert-ClcCompiledApplicationIdentity $metadata $config.applicationId $identity.releaseId $SourceCommit
if (@($metadata.Keys | Where-Object { $_.StartsWith('Clc.', [StringComparison]::Ordinal) }).Count -ne 6 -or
    $metadata['Clc.ReleaseTag'] -cne $Tag -or $metadata['Clc.TargetId'] -cne $targetId) {
    throw 'The compiled SSRS release tag or target does not match the authorized package.'
}
& "$PSScriptRoot/Export-ThirdPartyNotices.ps1" -PublishDirectory $publishRoot
Assert-PortalPublish $publishRoot
