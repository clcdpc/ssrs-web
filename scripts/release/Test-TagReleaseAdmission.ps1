#Requires -Version 7.0
[CmdletBinding()]
param([string] $OutputPath)
. "$PSScriptRoot/TagRelease.Common.ps1"
$context=Get-SsrsTagWorkflowContext
$directory=Join-Path $env:RUNNER_TEMP ('ssrs-tag-admission-'+[Guid]::NewGuid().ToString('N'))
$release=Get-SsrsAuthenticatedTagRelease $context $env:GITHUB_WORKFLOW_SHA $directory
$superseded=if ($release.targetId) { Test-SsrsTagSuperseded $release } else { $false }
$record=[ordered]@{recordKind='ssrs-web-tag-admission';tag=$release.tag;sourceCommit=$release.sourceCommit;tagObjectSha=$release.tagObjectSha;
    targetId=$release.targetId;sourceEligibility=$release.sourceEligibility;runId=$release.run.id;runAttempt=$release.run.run_attempt;
    artifactId=$release.provenance.artifactId;transportSha256=$release.provenance.artifactTransportSha256;superseded=$superseded;
    deploymentAuthorized=$false;toolkitIntegration='RequiresIndependentlyInstalledVerifiedWorkflowContract'}
if ($OutputPath) { [IO.File]::WriteAllText($OutputPath,($record | ConvertTo-Json -Compress)) }
$record | ConvertTo-Json -Compress | Write-Output
if ($env:GITHUB_OUTPUT) {
    $target=if ($superseded) { '' } else { $release.targetId }
    "target=$target" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
