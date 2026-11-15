#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BuildEvidencePath,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long] $ArtifactId,
    [Parameter(Mandatory)][string] $ArtifactTransportSha256,
    [Parameter(Mandatory)][string] $OutputPath
)
. "$PSScriptRoot/Release.Common.ps1"
$evidence=Read-ReleaseJson ([IO.File]::ReadAllBytes((Assert-ReleasePath $BuildEvidencePath)))
$digest=$ArtifactTransportSha256 -creplace '^sha256:',''
if ($digest -cnotmatch '^[a-f0-9]{64}$' -or $evidence.recordKind -cne 'ssrs-web-build-evidence' -or
    $evidence.repository -cne 'clcdpc/ssrs-web' -or $evidence.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or
    $evidence.workflowCommit -cnotmatch '^[a-f0-9]{40}$' -or $evidence.workflowPath -cne '.github/workflows/release.yml' -or
    $evidence.buildJob -cne 'release' -or $evidence.runId -lt 1 -or $evidence.runAttempt -lt 1 -or
    $evidence.innerReleaseSha256 -cnotmatch '^[a-f0-9]{64}$' -or $evidence.manifestSha256 -cnotmatch '^[a-f0-9]{64}$' -or
    $evidence.moduleInventorySha256 -cnotmatch '^[a-f0-9]{64}$' -or $evidence.deploymentApproval -isnot [bool] -or $evidence.deploymentApproval -or
    $evidence.hostInstallationApproval -isnot [bool] -or $evidence.hostInstallationApproval -or -not $evidence.integrityVerified) { Stop-Release 'Release.ProvenanceIncomplete' }
$record=[ordered]@{schemaVersion=1;recordKind='ssrs-web-release-provenance';repository=$evidence.repository;sourceCommit=$evidence.sourceCommit;
    workflowPath=$evidence.workflowPath;workflowCommit=$evidence.workflowCommit;runId=$evidence.runId;runAttempt=$evidence.runAttempt;buildJob=$evidence.buildJob;
    artifactId=$ArtifactId;artifactTransportSha256=$digest;innerReleaseSha256=$evidence.innerReleaseSha256;manifestSha256=$evidence.manifestSha256;
    toolkitBuilder=$evidence.toolkitBuilder;moduleInventorySha256=$evidence.moduleInventorySha256;releaseId=$evidence.releaseId;
    sdkVersion=$evidence.sdkVersion;dependencyLockSha256=$evidence.dependencyLockSha256;integrityVerified=$true;deploymentApproval=$false;hostInstallationApproval=$false}
if ($env:GITHUB_EVENT_NAME -cin @('push','workflow_dispatch') -and $env:GITHUB_REF_TYPE -ceq 'tag') {
    . "$PSScriptRoot/TagRelease.Common.ps1"
    $null=Get-SsrsReleaseTag $env:GITHUB_REF_NAME
    if ($env:RELEASE_TAG_OBJECT -cnotmatch '^[a-f0-9]{40}$') { Stop-Release 'Release.Tag.IdentityRequired' }
    $record.releaseTag=$env:GITHUB_REF_NAME
    $record.tagObjectSha=$env:RELEASE_TAG_OBJECT
}
$stream=[IO.FileStream]::new((Assert-ReleasePath $OutputPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write([Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 16 -Compress))); $stream.Flush($true) } finally { $stream.Dispose() }
[pscustomobject]$record
