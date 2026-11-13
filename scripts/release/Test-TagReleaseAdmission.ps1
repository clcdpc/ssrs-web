#Requires -Version 7.0
[CmdletBinding()]
param([string] $OutputPath)
. "$PSScriptRoot/TagRelease.Common.ps1"
if ($env:GITHUB_REPOSITORY -cne 'clcdpc/ssrs-web' -or $env:GITHUB_EVENT_NAME -cne 'workflow_run' -or
    $env:GITHUB_REF -cne 'refs/heads/master' -or $env:GITHUB_REF_PROTECTED -cne 'true' -or
    $env:GITHUB_WORKFLOW_REF -cne 'clcdpc/ssrs-web/.github/workflows/deploy-release.yml@refs/heads/master' -or
    $env:GITHUB_WORKFLOW_SHA -cne $env:GITHUB_SHA) { Stop-TagRelease 'TrustedConsumerRequired' }
$event=Read-TagJson ([IO.File]::ReadAllBytes($env:GITHUB_EVENT_PATH))
$directory=Join-Path $env:RUNNER_TEMP ('ssrs-tag-admission-'+[Guid]::NewGuid().ToString('N'))
$release=Get-SsrsAuthenticatedTagRelease $event.workflow_run $env:GITHUB_WORKFLOW_SHA $directory
$superseded=if ($release.targetId) { Test-SsrsTagSuperseded $release } else { $false }
$record=[ordered]@{recordKind='ssrs-web-tag-admission';tag=$release.tag;sourceCommit=$release.sourceCommit;tagObjectSha=$release.tagObjectSha;
    targetId=$release.targetId;sourceEligibility=$release.sourceEligibility;runId=$release.run.id;runAttempt=$release.run.run_attempt;
    artifactId=$release.provenance.artifactId;transportSha256=$release.provenance.artifactTransportSha256;superseded=$superseded;
    deploymentAuthorized=$false;toolkitIntegration='Unavailable'}
if ($OutputPath) { [IO.File]::WriteAllText($OutputPath,($record | ConvertTo-Json -Compress)) }
$record | ConvertTo-Json -Compress | Write-Output
if ($env:GITHUB_OUTPUT) {
    $target=if ($superseded) { '' } else { $release.targetId }
    "target=$target" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
