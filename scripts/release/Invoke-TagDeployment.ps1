#Requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/TagRelease.Common.ps1"
. "$PSScriptRoot/Deployment.Common.ps1"
. "$PSScriptRoot/AutomaticDeployment.Adapter.ps1"

function Invoke-SsrsAutomaticDeployment($Release,$Evidence) {
    if ($Release.targetId -cnotin @('ssrs-web-dev','ssrs-web-production')) { Stop-TagRelease 'DeploymentTargetRequired' }
    $AuthorizationId=$Release.targetId
    $script:SsrsTargetId=$Release.targetId
    $script:SsrsEnvironmentName=if ($Release.targetId -ceq 'ssrs-web-dev') { 'DEV' } else { 'Production' }
    $Evidence.targetId=$script:SsrsTargetId; $Evidence.environmentName=$script:SsrsEnvironmentName; $Evidence.initializationPerformed=$false
    $bootstrap=$null; $preflight=$null; $context=$null
    try {
        $bootstrap=Open-SsrsBootstrap $AuthorizationId
        . (Join-Path $bootstrap.root 'tools/host/HostEnrollment.Common.ps1')
        $state=Get-SsrsEnrollment $bootstrap $AuthorizationId
        Set-SsrsEnrollmentEvidence $Evidence $state
        # Automatic file deployment never enrolls infrastructure or performs cutover.
        if (-not $state.ReadyForDeployment -or $state.HostState -cne 'Ready') { Stop-Ssrs 'Ssrs.AutomaticDeployment.SeparateEnrollmentRequired' }
        $preflight=Open-SsrsApplicationPreflight $bootstrap $AuthorizationId $true
        $context=Open-SsrsAutomaticContext $bootstrap $AuthorizationId $Release
        $Evidence.artifactVerification='GitHubTransportAndInstalledToolkitVerified'
        Assert-SsrsEnvironmentNames ([Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Machine).Keys)
        $again=Resolve-SsrsTag $Release.tag
        if ($again.objectSha -cne $Release.tagObjectSha -or $again.sourceCommit -cne $Release.sourceCommit) { Stop-TagRelease 'TagMoved' }
        $null=Get-SsrsSourceEligibility (Get-SsrsReleaseTag $Release.tag) $Release.sourceCommit
        $archive=Get-SsrsCanonicalArchive (Get-SsrsReleaseTag $Release.tag) $again
        if (-not $archive) { Stop-TagRelease 'ArchiveRequiredBeforeDeployment' }
        Assert-SsrsCanonicalCompatibility $archive $Release
        if (Test-SsrsTagSuperseded $Release) { $Evidence.outcome='Superseded'; $Evidence.nextAction='None'; return $Evidence }
        $Evidence.deploymentPhase='Unknown'; $Evidence.mutationScope='Unknown'; $Evidence.requiresOperatorIntervention=$true
        $result=Invoke-SsrsToolkitAutomaticDeployment $Release $context
        Assert-SsrsDeploymentResult $result @{releaseId=$Release.provenance.releaseId;sourceCommit=$Release.sourceCommit}
        Set-SsrsCompletedDeploymentEvidence $Evidence $result
        return $Evidence
    } finally {
        try {
            if ($context -and $context.ContainsKey('trustedContext')) { & $context.module { param($Context) Close-ClcTrustedContext $Context } $context.trustedContext }
        } finally {
            try { if ($context) { Close-SsrsSession $context.session } }
            finally { try { Close-SsrsSession $preflight } finally { if ($bootstrap) { foreach ($lease in $bootstrap.leases) { $lease.Dispose() } } } }
        }
    }
}

function Open-SsrsAutomaticContext($Bootstrap,[string] $AuthorizationId,$Release) {
    $session=$null; $trusted=$null; $module=$null
    try {
        $session=Open-HSession $Bootstrap.configurationPath $AuthorizationId
        Open-HInstalledContext $session
        $module=Import-Module $session.layout.manifestPath -Force -PassThru -Scope Local
        $selection=& $module {
            param($TargetId)
            $context=Open-ClcTrustedContext
            try {
                $target=Read-ClcProtectedTarget $context $TargetId
                $null=Add-ClcProtectedPathPin $context $target.record.artifactInboxRoot -Directory -Mutable -RuntimeSid $target.record.runtimeIdentitySid
                return @{context=$context;target=$target.record}
            } catch { Close-ClcTrustedContext $context; throw }
        } $Release.targetId
        $trusted=$selection.context
        if (-not (Test-ClcValueEqual $selection.target (Get-HTarget $session.configuration))) { Stop-Ssrs 'Ssrs.FixedTargetMismatch' }
        $verified=& $module { param($Path) Test-ClcRelease -ArchivePath $Path } $Release.archivePath
        $p=$Release.provenance
        if ($verified.integrityVerified -ne $true -or $verified.authorizedForDeployment -ne $false -or $verified.applicationId -cne 'ssrs-web' -or
            $verified.releaseId -cne $p.releaseId -or $verified.sourceCommit -cne $Release.sourceCommit -or $verified.archiveSha256 -cne $p.innerReleaseSha256 -or
            $verified.manifestSha256 -cne $p.manifestSha256) { Stop-Ssrs 'Ssrs.ReleaseInspectionMismatch' }
        return @{bootstrap=$Bootstrap;authorizationId=$AuthorizationId;session=$session;module=$module;trustedContext=$trusted;target=$selection.target}
    } catch { if ($trusted) { & $module {param($Context) Close-ClcTrustedContext $Context} $trusted }; Close-SsrsSession $session; throw }
}

function Invoke-SsrsTagDeploymentEntry {
    $evidence=New-SsrsEvidence
    try {
        if (-not $IsWindows -or $PSVersionTable.PSVersion.ToString() -cne '7.6.6' -or $env:GITHUB_JOB -cne 'deploy') { Stop-Ssrs 'Ssrs.TrustedWorkflowRequired' }
        $workflowContext=Get-SsrsTagWorkflowContext
        $release=Get-SsrsAuthenticatedTagRelease $workflowContext $env:GITHUB_WORKFLOW_SHA (Join-Path $env:RUNNER_TEMP ([Guid]::NewGuid().ToString('N')))
        $jobs=Get-TagPages ('/repos/clcdpc/ssrs-web/actions/runs/'+$release.run.id+'/attempts/'+$release.run.run_attempt+'/jobs') 'jobs'
        Assert-SsrsCompletedHostedJob $jobs $release.run 'archive'
        if ($release.targetId -cne $env:SSRS_EXPECTED_TARGET) { Stop-TagRelease 'TargetMismatch' }
        $script:SsrsTargetId=$release.targetId
        $script:SsrsEnvironmentName=if ($release.targetId -ceq 'ssrs-web-dev') { 'DEV' } else { 'Production' }
        $evidence=New-SsrsEvidence
        $evidence.releaseId=$release.provenance.releaseId; $evidence.sourceCommit=$release.sourceCommit
        $evidence.releaseTag=$release.tag; $evidence.tagObjectSha=$release.tagObjectSha
        $evidence.runId=$release.run.id; $evidence.runAttempt=$release.run.run_attempt; $evidence.packageSha256=$release.provenance.innerReleaseSha256
        if (Test-SsrsTagSuperseded $release) { $evidence.outcome='Superseded'; $evidence.nextAction='None' }
        else { $null=Invoke-SsrsAutomaticDeployment $release $evidence }
    } catch {
        Set-SsrsSafeFailure $evidence $_
        throw [InvalidOperationException]::new('Ssrs.AutomaticDeployment.FailedSeeRetainedEvidence')
    }
    finally {
        $json=$evidence | ConvertTo-Json -Depth 6 -Compress
        Write-Output $json
        if ($env:GITHUB_OUTPUT -and $env:RUNNER_TEMP) {
            $path=Join-Path $env:RUNNER_TEMP ('ssrs-tag-deployment-'+[Guid]::NewGuid().ToString('N')+'.json')
            $file=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $file.Write([Text.Encoding]::UTF8.GetBytes($json)); $file.Flush($true) } finally { $file.Dispose() }
            "evidence-path=$path" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
        }
    }
}
# Dot-sourcing supports deterministic tests of the real orchestration.
if ($MyInvocation.InvocationName -cne '.') { Invoke-SsrsTagDeploymentEntry }
