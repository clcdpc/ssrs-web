#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workflow=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/deploy-dev.yml'))
$functions=[IO.File]::ReadAllText((Join-Path $repository 'scripts/release/Deployment.Common.ps1'))
$parseErrors=$null; $null=[Management.Automation.Language.Parser]::ParseInput($functions,[ref]$null,[ref]$parseErrors)
if ($parseErrors) { throw ($parseErrors | Out-String) }
. ([ScriptBlock]::Create($functions))
$results=[Collections.Generic.List[object]]::new()
function Test-Case {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)][string] $Name,[Parameter(Mandatory,Position=1)][scriptblock] $Body)
    try { & $Body; $results.Add(@{name=$Name;status='Passed'}) } catch { $results.Add(@{name=$Name;status='Failed'}); throw }
}
function Assert-True([bool] $Value) { if (-not $Value) { $caller=@(Get-PSCallStack)[1]; throw ('Workflow contract assertion failed at line '+$caller.ScriptLineNumber+': '+$caller.Command) } }
function Assert-Fails([scriptblock] $Body,[string] $Code) {
    $failed=$false
    try { $null=& $Body } catch { $failed=$true; if ($Code -and $_.Exception.Message -notlike "*$Code*") { throw } }
    Assert-True $failed
}
function Json-Bytes($Value) { return ,([Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 20 -Compress))) }
function Get-TestApproval {
    return @{approvalId='release-approval';applicationId='ssrs-web';targetId='ssrs-web-dev';enabled=$true;releaseId='ssrs-web-test-1';sourceCommit=('1'*40);releaseZipSha256=('2'*64);manifestSha256=('3'*64);
        provenance=@{workflowPath='.github/workflows/release.yml';runId=42L;runAttempt=2L;buildJob='release';artifactId=88L;transportSha256=('4'*64)}}
}
try {
    Test-Case 'Privileged source/event guards reject an ordinary local or contributor context before host access' {
        Assert-Fails { Assert-SsrsContext } 'Ssrs.TrustedWorkflowRequired'
        Assert-Fails { Assert-SsrsSelector '../escape' } 'Ssrs.InvalidSelector'
        Assert-Fails { Assert-SsrsSelector 'target/elsewhere' } 'Ssrs.InvalidSelector'
    }
    Test-Case 'Strict bootstrap JSON refuses case duplicates, nested duplicates and excessive depth' {
        foreach ($value in @('{"a":1,"A":2}','{"outer":[{"a":1,"A":2}]}')) { Assert-Fails { Read-SsrsJson ([Text.Encoding]::UTF8.GetBytes($value)) } 'Ssrs.DuplicateJson' }
        Assert-Fails { Read-SsrsJson ([byte[]]::new(4194305)) } 'Ssrs.JsonBound'
    }
    Test-Case 'First bootstrap handoff requires an exact integer schema version and bounded expected object shapes' {
        $value=@{contractFamily='clc-host-enrollment-v1';recordKind='host-enrollment-authorization';schemaVersion=1L;authorizationId='fixture-auth';targetId='ssrs-web-dev';
            bootstrap=@{sourceCommit=('1'*40);inventorySha256=('2'*64)};hostPolicy=@{activeToolkit=@{distributionSha256=('3'*64)}}}
        Assert-SsrsBootstrapEnvelope $value 'fixture-auth'
        $json=$value | ConvertTo-Json -Depth 6 -Compress
        $parsed=Read-SsrsJson ([Text.Encoding]::UTF8.GetBytes($json)); Assert-SsrsBootstrapEnvelope $parsed 'fixture-auth'
        foreach ($invalidVersion in @($json.Replace('"schemaVersion":1','"schemaVersion":true'),$json.Replace('"schemaVersion":1','"schemaVersion":1.0'))) {
            $invalid=Read-SsrsJson ([Text.Encoding]::UTF8.GetBytes($invalidVersion))
            Assert-Fails { Assert-SsrsBootstrapEnvelope $invalid 'fixture-auth' } 'Ssrs.IndependentHandoffInvalid'
        }
        $invalid=$value.Clone(); $invalid.bootstrap='private'
        Assert-Fails { Assert-SsrsBootstrapEnvelope $invalid 'fixture-auth' } 'Ssrs.IndependentHandoffInvalid'
    }
    Test-Case 'Runtime environment preflight detects plain and host-prefixed legacy/test sources without values' {
        foreach ($key in @('SsrsPortal__TestConfigurationFile','Portal__ApprovedReports','Entra__TenantId','Entra__GroupId')) {
            foreach ($prefix in @('','ASPNETCORE_','DOTNET_')) { Assert-True (Test-SsrsForbiddenEnvironmentName ($prefix+$key)); Assert-True (Test-SsrsForbiddenEnvironmentName ($prefix+$key+'__child')) }
        }
        foreach ($key in @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','Portal__ReportServer','Entra__AllowedTenantIds')) { Assert-True (-not (Test-SsrsForbiddenEnvironmentName $key)) }
        Assert-Fails { Assert-SsrsEnvironmentNames @('Entra__GroupId') } 'Ssrs.Prerequisite.RemoveUnsupportedWorkerEnvironment'
    }
    Test-Case 'Subscription host prerequisites preserve existing bool, string and optional/null semantics' {
        foreach ($value in @($false,'false',' FALSE ',$null,'')) { Assert-True (-not (Test-SsrsApplicationSubscriptions (Json-Bytes @{Subscriptions=@{Enabled=$value}}))) }
        foreach ($value in @($true,'true',' TRUE ')) { Assert-True (Test-SsrsApplicationSubscriptions (Json-Bytes @{Subscriptions=@{Enabled=$value}})) }
        foreach ($value in @(@{},@{Subscriptions=$null},@{Subscriptions=@{}},@{Subscriptions=@()})) { Assert-True (-not (Test-SsrsApplicationSubscriptions (Json-Bytes $value))) }
    }
    Test-Case 'Application snapshot extraction matches BOM, comment, trailing comma and case-insensitive binder semantics' {
        $text='{"subscriptions":{"eNaBlEd":" true "},"AllowedHosts":"localhost"}'
        $encodings=@([Text.UTF8Encoding]::new($true,$true),[Text.UnicodeEncoding]::new($false,$true,$true),[Text.UnicodeEncoding]::new($true,$true,$true),[Text.UTF32Encoding]::new($false,$true,$true),[Text.UTF32Encoding]::new($true,$true,$true))
        foreach ($encoding in $encodings) {
            $bytes=[byte[]]($encoding.GetPreamble()+$encoding.GetBytes($text)); $before=[Convert]::ToHexString($bytes)
            Assert-True (Test-SsrsApplicationSubscriptions $bytes); Assert-True ([Convert]::ToHexString($bytes) -ceq $before)
        }
        foreach ($json in @('{/* comment */"SuBsCrIpTiOnS":{"ENABLED":true}}','{"subscriptions":{"enabled":true,},}','{"ignoredNumber":1.25,"subscriptions":{"enabled":true}}')) { Assert-True (Test-SsrsApplicationSubscriptions ([Text.Encoding]::UTF8.GetBytes($json))) }
        foreach ($json in @('{}','{"subscriptions":null}','{"subscriptions":[]}','{"subscriptions":{"enabled":null}}','{"subscriptions":{"enabled":false}}','{"subscriptions":{"enabled":" FALSE "}}')) { Assert-True (-not (Test-SsrsApplicationSubscriptions ([Text.Encoding]::UTF8.GetBytes($json)))) }
        foreach ($json in @('{"subscriptions":{"enabled":true,"ENABLED":false}}','{"subscriptions":{},"Subscriptions":{}}','{"Subscriptions:Enabled":true}')) { Assert-Fails { Test-SsrsApplicationSubscriptions ([Text.Encoding]::UTF8.GetBytes($json)) } 'Ssrs.Prerequisite.InvalidApplicationSnapshot' }
        Assert-Fails { Test-SsrsApplicationSubscriptions ([byte[]]@(255,254,0)) } 'Ssrs.Prerequisite.InvalidApplicationSnapshot'
        Assert-Fails { Read-SsrsJson ([Text.Encoding]::UTF8.GetBytes('{/* host JSON remains strict */"a":true,}')) }
    }
    Test-Case 'Candidate-independent ANCM grace requires an explicit exact-site inherited and locked setting' {
        $valid='<configuration><location path="Site" overrideMode="Allow"><system.webServer><aspNetCore shutdownTimeLimit="60" lockAttributes="shutdownTimeLimit"/></system.webServer></location></configuration>'
        Assert-SsrsInheritedGrace (Read-SsrsXml ([Text.Encoding]::UTF8.GetBytes($valid))) 'Site'
        foreach ($invalid in @($valid.Replace('60','10'),$valid.Replace(' lockAttributes="shutdownTimeLimit"',''),$valid.Replace('path="Site"','path="Elsewhere"'),$valid.Replace('<aspNetCore ','<aspNetCore configSource="other.config" '))) {
            Assert-Fails { Assert-SsrsInheritedGrace (Read-SsrsXml ([Text.Encoding]::UTF8.GetBytes($invalid))) 'Site' }
        }
        Assert-Fails { Read-SsrsXml ([Text.Encoding]::UTF8.GetBytes('<!DOCTYPE x [<!ENTITY e SYSTEM "file:///do-not-read">]><configuration>&e;</configuration>')) }
        # A current live web.config value is never accepted as parent proof.
        Assert-Fails { Assert-SsrsInheritedGrace (Read-SsrsXml ([Text.Encoding]::UTF8.GetBytes('<configuration><location path="."><system.webServer><aspNetCore shutdownTimeLimit="60"/></system.webServer></location></configuration>'))) 'Site' }
    }
    Test-Case 'Unsupported IIS redirection fails closed unless disabled is absent or explicit false' {
        foreach ($enabled in @('', ' enabled="false"', ' enabled="False"')) {
            $xml=[Text.Encoding]::UTF8.GetBytes('<configuration><configurationRedirection'+$enabled+'/></configuration>')
            Assert-SsrsLocalIisConfiguration (Read-SsrsXml $xml)
        }
        foreach ($enabled in @(' enabled="true"',' enabled="TRUE"',' enabled=" false "',' enabled="0"',' enabled="1"',' enabled="unknown"')) {
            $xml=[Text.Encoding]::UTF8.GetBytes('<configuration><configurationRedirection'+$enabled+'/></configuration>')
            Assert-Fails { Assert-SsrsLocalIisConfiguration (Read-SsrsXml $xml) } 'Ssrs.Prerequisite.UnsupportedIisConfigurationLayout'
        }
        Assert-Fails { Assert-SsrsLocalIisConfiguration (Read-SsrsXml ([Text.Encoding]::UTF8.GetBytes('<configuration/>'))) } 'Ssrs.Prerequisite.UnsupportedIisConfigurationLayout'
        Assert-Fails { Assert-SsrsLocalIisConfiguration (Read-SsrsXml ([Text.Encoding]::UTF8.GetBytes('<configuration><configurationRedirection enabled="false" configSource="external.config"/></configuration>'))) } 'Ssrs.Prerequisite.UnsupportedIisConfigurationLayout'
    }
    Test-Case 'Runner shell templates disable profiles and preserve trusted PowerShell script and native exit behavior' {
        $admissionShell=[regex]::Match($workflow,'(?m)^        shell: (pwsh .*)$').Groups[1].Value.TrimEnd("`r")
        $deployShell=[regex]::Match($workflow,'(?m)^        shell: (C:\\CLC\\Tools\\PowerShell\\7\.6\.6\\pwsh\.exe .*)$').Groups[1].Value.TrimEnd("`r")
        Assert-True ($admissionShell -ceq 'pwsh -NoLogo -NoProfile -NonInteractive -File "{0}"')
        $parts=$deployShell.Split(' ',2)
        Assert-True ($parts.Count -eq 2 -and $parts[0] -ceq 'C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe' -and $parts[0].IndexOf(' ') -lt 0)
        Assert-True ($parts[1].Contains('-NoLogo -NoProfile -NonInteractive -CommandWithArgs') -and $parts[1].Contains('[System.IO.File]::ReadAllText($args[0])') -and $parts[1].Contains('"{0}"'))
        Assert-True ($workflow.Contains('if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }'))
        # The pinned custom executable is not one of Runner's built-in shell
        # names, so Runner writes an extensionless temp file and adds no fixup.
        # Execute that parsed template against an owned inert file; its path is
        # an argument, so spaces and apostrophes cannot become PowerShell code.
        if ($IsWindows) {
            $pwsh=(Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
            $probeRoot=Join-Path $repository ('artifacts/shell-template-probe/shell probe''s-'+[Guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($probeRoot)
            $probePath=Join-Path $probeRoot 'runner-generated-file'
            $probe=@'
$ErrorActionPreference='Stop'
& $env:ComSpec /d /c exit 37
if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
'@
            [IO.File]::WriteAllText($probePath,$probe,[Text.UTF8Encoding]::new($false))
            $resolvedScriptPath=$probePath.Replace('"','\\"')
            $arguments=[string]::Format([Globalization.CultureInfo]::InvariantCulture,$parts[1],$resolvedScriptPath)
            $start=[Diagnostics.ProcessStartInfo]::new($pwsh); $start.Arguments=$arguments; $start.UseShellExecute=$false
            $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.CreateNoWindow=$true
            $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
            try {
                Assert-True $process.Start()
                Assert-True ($process.WaitForExit(15000))
                $stdout=$process.StandardOutput.ReadToEnd(); $stderr=$process.StandardError.ReadToEnd()
                Assert-True ($process.ExitCode -eq 37 -and [string]::IsNullOrEmpty($stderr) -and $stdout -notmatch 'shell probe')
            } finally {
                if (-not $process.HasExited) { $process.Kill(); $null=$process.WaitForExit(5000) }
                $process.Dispose()
            }
        }
    }
    Test-Case 'Pool AutoStart is a read-only prerequisite for every application configuration' {
        Assert-SsrsPoolAutoStart ([pscustomobject]@{AutoStart=$true})
        Assert-Fails { Assert-SsrsPoolAutoStart ([pscustomobject]@{AutoStart=$false}) } 'Ssrs.Prerequisite.PoolAutoStart'
        Assert-Fails { Assert-SsrsPoolAutoStart ([pscustomobject]@{AutoStart='true'}) } 'Ssrs.Prerequisite.PoolAutoStart'
        Assert-Fails { Assert-SsrsPoolAutoStart $null } 'Ssrs.Prerequisite.PoolAutoStart'
    }
    Test-Case 'Subscription initialization requires a registered native image and an unrestricted effective site module' {
        $root=Join-Path $repository ('artifacts/application-init-module-fixtures/'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($root)
        $imagePath=Join-Path $root 'warmup.dll'; [IO.File]::WriteAllBytes($imagePath,[byte[]]@(0x4d,0x5a))
        $registered=@([pscustomobject]@{name='ApplicationInitializationModule';image=$imagePath;preCondition=''})
        $enabled=@([pscustomobject]@{name='ApplicationInitializationModule';type='';preCondition=''})
        Assert-SsrsApplicationInitializationModule $registered $enabled $imagePath
        Assert-Fails { Assert-SsrsApplicationInitializationModule @() $enabled $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        Assert-Fails { Assert-SsrsApplicationInitializationModule $registered @() $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        # The effective site collection omits the module after an inherited or
        # local <remove name="ApplicationInitializationModule"/> operation.
        $siteRemoved=@([pscustomobject]@{name='StaticFileModule';type='';preCondition=''})
        Assert-Fails { Assert-SsrsApplicationInitializationModule $registered $siteRemoved $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        Assert-Fails { Assert-SsrsApplicationInitializationModule $registered @([pscustomobject]@{name='ApplicationInitializationModule';type='';preCondition='bitness32'}) $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        Assert-Fails { Assert-SsrsApplicationInitializationModule $registered @([pscustomobject]@{name='ApplicationInitializationModule';type='System.Web.TestModule';preCondition=''}) $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        foreach ($unresolved in @('%IIS_BIN%\warmup.dll','%windir%\System32\inetsrv\warmup.dll','%systemroot%\System32\inetsrv\warmup.dll','warmup.dll','C:\other\warmup.dll')) {
            Assert-Fails { Assert-SsrsApplicationInitializationModule @([pscustomobject]@{name='ApplicationInitializationModule';image=$unresolved;preCondition=''}) $enabled $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        }
        Assert-Fails { Assert-SsrsApplicationInitializationModule @([pscustomobject]@{name='ApplicationInitializationModule';image=$imagePath;preCondition='bitness32'}) $enabled $imagePath } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        Assert-Fails { Assert-SsrsApplicationInitializationModule $registered $enabled (Join-Path $root '.\warmup.dll') } 'Ssrs.Prerequisite.ApplicationInitializationModule'
        Assert-Fails { Assert-SsrsApplicationInitializationModule $registered $enabled (Join-Path $root 'missing-warmup.dll') } 'Ssrs.Prerequisite.ApplicationInitializationModule'
    }
    Test-Case 'Producer API binding uses exact historical attempt, artifact, repository, source and successful build job' {
        $approval=Get-TestApproval; $repo=@{id=123L;full_name='clcdpc/ssrs-web'}
        $artifact=@{id=88L;name='ssrs-web-release-42-2';expired=$false;digest=('sha256:'+('4'*64));workflow_run=@{id=42L;repository_id=123L;head_repository_id=123L;head_branch='master';head_sha=('1'*40)}}
        $run=@{id=42L;run_attempt=2L;head_sha=('1'*40);head_branch='master';path='.github/workflows/release.yml';event='workflow_dispatch';status='completed';conclusion='success';repository=@{id=123L};head_repository=@{id=123L}}
        $jobs=@{total_count=1L;jobs=@(@{name='release';run_id=42L;run_attempt=2L;head_sha=('1'*40);status='completed';conclusion='success'})}
        Assert-SsrsProvenance $approval $repo $artifact $run $jobs
        foreach ($field in @('run_attempt','head_sha','path','event','conclusion')) {
            $saved=$run[$field]; $run[$field]=if ($field -ceq 'run_attempt') {3L} else {'wrong'}
            Assert-Fails { Assert-SsrsProvenance $approval $repo $artifact $run $jobs } 'Ssrs.ArtifactProvenanceMismatch'; $run[$field]=$saved
        }
        $artifact.digest='sha256:'+('0'*64); Assert-Fails { Assert-SsrsProvenance $approval $repo $artifact $run $jobs } 'Ssrs.ArtifactProvenanceMismatch'
        Assert-True ($functions.Contains('/attempts/$($p.runAttempt)/jobs?per_page=100'))
    }
    Test-Case 'Success requires actual toolkit terminal identity, generation, pool and worker observations' {
        $approval=Get-TestApproval
        $result=@{status='Deployed';targetId='ssrs-web-dev';releaseId=$approval.releaseId;sourceCommit=$approval.sourceCommit;attemptId=[Guid]::NewGuid().ToString('D');currentGeneration=1L;observedPoolState='Started';workerCount=1L;executingToolkit=@{version='0.1.0';sourceCommit=('2'*40)}}
        Assert-SsrsDeploymentResult $result $approval
        foreach ($field in @('status','targetId','releaseId','sourceCommit','attemptId','currentGeneration','observedPoolState','workerCount')) {
            $saved=$result[$field]; $result[$field]=if ($field -cin @('currentGeneration','workerCount')) {0L} else {'wrong'}
            Assert-Fails { Assert-SsrsDeploymentResult $result $approval }; $result[$field]=$saved
        }
        $toolkit=$result.executingToolkit; $result.Remove('executingToolkit')
        Assert-Fails { Assert-SsrsDeploymentResult $result $approval } 'Ssrs.DeploymentResultInvalid'; $result.executingToolkit=$toolkit
        $result.status='NoOp'; Assert-SsrsDeploymentResult $result $approval
    }
    Test-Case 'Failure output excludes exception secrets, configuration and private authorization fingerprints' {
        $sentinel='PRIVATE_DO_NOT_EMIT'
        $record=[Management.Automation.ErrorRecord]::new([Exception]::new($sentinel),'NativeBadData',[Management.Automation.ErrorCategory]::InvalidData,[pscustomobject]@{password=$sentinel;privateSha256=$sentinel;nextAction=$sentinel;observedPoolState='Stopped';workerCount=0L;attemptId=[Guid]::NewGuid().ToString('D')})
        $evidence=New-SsrsEvidence; Set-SsrsSafeFailure $evidence $record
        $json=$evidence | ConvertTo-Json -Compress
        Assert-True (-not $json.Contains($sentinel)); Assert-True ($evidence.code -ceq 'Ssrs.WorkflowFailed' -and $evidence.observedPoolState -ceq 'Stopped')
        $unknown=New-SsrsEvidence; Set-SsrsSafeFailure $unknown ([Management.Automation.ErrorRecord]::new([Exception]::new('no structured result'),'Clc.ReadinessFailed',[Management.Automation.ErrorCategory]::InvalidData,$null))
        Assert-True ($unknown.code -ceq 'Clc.ReadinessFailed' -and $unknown.nextAction -ceq 'ReviewIndependentPrerequisitesAndRetainEvidence' -and $unknown.requiresOperatorIntervention -eq $true)
    }
    Test-Case 'Partial enrollment Apply preserves typed state, performed flag, operator action and pool observations' {
        $target=[pscustomobject]@{HostState='RecoveryRequired';InitializationPerformed=$true;RequiresOperatorIntervention=$true;NextAction='RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation';ObservedPoolState='Stopped';WorkerCount=0L;AuthorizationId='PRIVATE_AUTH';privateSha256='PRIVATE_HASH'}
        $record=[Management.Automation.ErrorRecord]::new([IO.InvalidDataException]::new('private details must not be emitted'),'Clc.EnrollmentFailed',[Management.Automation.ErrorCategory]::InvalidData,$target)
        $evidence=New-SsrsEvidence; Set-SsrsSafeFailure $evidence $record
        Assert-True ($evidence.enrollmentState -ceq 'RecoveryRequired' -and $evidence.initializationPerformed -eq $true -and $evidence.requiresOperatorIntervention -eq $true)
        Assert-True ($evidence.nextAction -ceq 'RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation' -and $evidence.observedPoolState -ceq 'Stopped' -and $evidence.workerCount -eq 0)
        $unknown=New-SsrsEvidence
        Set-SsrsSafeFailure $unknown ([Management.Automation.ErrorRecord]::new([Exception]::new('safe'),'Clc.EnrollmentFailed',[Management.Automation.ErrorCategory]::InvalidData,[pscustomobject]@{HostState='RecoveryRequired';RequiresOperatorIntervention=$true;NextAction='untrusted-private-value'}))
        Assert-True ($null -eq $unknown.initializationPerformed -and $unknown.nextAction -ceq 'RetainEvidenceAndUseExplicitRecoveryOrMaintenance')
        $json=$evidence | ConvertTo-Json -Compress
        Assert-True (-not $json.Contains('PRIVATE_AUTH') -and -not $json.Contains('PRIVATE_HASH') -and -not $json.Contains('private details'))
    }
    Test-Case 'Post-mutation deployment error preserves safe phase, scope, action, attempt and pool observations' {
        $target=[pscustomobject]@{phase='RecoveryRequired';mutationScope='PossiblyLive';nextAction='RetainEvidenceAndSelectExplicitRecoveryOrMaintenance';observedPoolState='Stopped';workerCount=0L;attemptId=[Guid]::NewGuid().ToString('D');privateSha256='PRIVATE_HASH'}
        $record=[Management.Automation.ErrorRecord]::new([IO.InvalidDataException]::new('{"raw":"PRIVATE_EXCEPTION"}'),'Clc.ReadinessFailed',[Management.Automation.ErrorCategory]::InvalidData,$target)
        $evidence=New-SsrsEvidence; Set-SsrsSafeFailure $evidence $record
        Assert-True ($evidence.deploymentPhase -ceq 'RecoveryRequired' -and $evidence.mutationScope -ceq 'PossiblyLive' -and $evidence.requiresOperatorIntervention -eq $true)
        Assert-True ($evidence.nextAction -ceq 'RetainEvidenceAndSelectExplicitRecoveryOrMaintenance' -and $evidence.observedPoolState -ceq 'Stopped' -and $evidence.workerCount -eq 0)
        Assert-True ($evidence.attemptId -ceq $target.attemptId)
        $json=$evidence | ConvertTo-Json -Compress
        Assert-True (-not $json.Contains('PRIVATE_HASH') -and -not $json.Contains('PRIVATE_EXCEPTION'))
        $unknown=New-SsrsEvidence
        Set-SsrsSafeFailure $unknown ([Management.Automation.ErrorRecord]::new([Exception]::new('private'),'Clc.ReadinessFailed',[Management.Automation.ErrorCategory]::InvalidData,[pscustomobject]@{phase='PrivateValue';mutationScope='PrivateValue';nextAction='PRIVATE_ACTION';observedPoolState='PrivateValue';workerCount='PRIVATE_VALUE'}))
        Assert-True ($unknown.deploymentPhase -ceq 'Unknown' -and $unknown.mutationScope -ceq 'Unknown' -and $unknown.requiresOperatorIntervention -eq $true)
        Assert-True ($unknown.nextAction -ceq 'PreserveEvidenceAndUseInfrastructureMaintenance' -and $unknown.observedPoolState -ceq 'Unknown' -and $null -eq $unknown.workerCount)
        Assert-True (-not (($unknown | ConvertTo-Json -Compress).Contains('PrivateValue')))
    }
    Test-Case 'Workflow routing and trust remain external to labels; privileged job has no checkout or source build' {
        $deploy=$workflow.Substring($workflow.IndexOf('  deploy:'))
        Assert-True ($deploy.Contains('group: ${{ vars.SSRS_DEV_RUNNER_GROUP }}') -and $deploy.Contains('fromJSON(vars.SSRS_DEV_RUNNER_LABELS'))
        Assert-True (-not ($deploy -match 'uses: actions/checkout|pull_request_target|dotnet publish|npm ci|sqlcmd'))
        Assert-True ($workflow.Contains('@refs/heads/master') -and $workflow.Contains('RUNNER_ADMISSION_READY'))
        Assert-True (-not ($functions -match 'Restore-ClcIisDeployment|Stop-WebAppPool|Start-WebAppPool|Remove-Item|sqlcmd'))
    }
    if ($IsWindows) {
        Test-Case 'Initial handoff protection rejects untrusted owners and write ACEs independent of submitted allowlists' {
            $good=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)')
            Assert-SsrsSecurityDescriptor $good
            foreach ($sddl in @('O:BUG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)','O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FW;;;BU)')) {
                Assert-Fails { Assert-SsrsSecurityDescriptor ([Security.AccessControl.RawSecurityDescriptor]::new($sddl)) } 'Ssrs.UnprotectedBootstrap'
            }
        }
        Test-Case 'Whole bootstrap byte authentication precedes any bootstrap execution and retains all leases' {
            Initialize-SsrsNative
            $parent=Join-Path $repository ('artifacts/bootstrap-boundary/'+[Guid]::NewGuid().ToString('N'))
            $script:fixtureProgramData=Join-Path $parent 'program-data'
            function Get-SsrsProgramData { return $script:fixtureProgramData }
            function Assert-SsrsProgramDataEnvironment([string] $Root) {}
            # Only ACL/OS identity observations are synthetic; native physical
            # pins, stream hashing, exact names and first-execution ordering run.
            function Assert-SsrsAcl([string] $Path,[bool] $Ancestor) {}
            $authority=Join-Path $script:fixtureProgramData 'CLC/Deployment/host-authorizations'; $root=Join-Path $script:fixtureProgramData 'CLC/Deployment/Bootstrap/ssrs-web'
            [void][IO.Directory]::CreateDirectory($authority); [void][IO.Directory]::CreateDirectory((Join-Path $root 'CLC.Deployment/Private')); [void][IO.Directory]::CreateDirectory((Join-Path $root 'tools/host'))
            $sentinel=Join-Path $parent 'executed.txt'
            $payload=@{'CLC.Deployment/CLC.Deployment.psm1'='# inert test module';'CLC.Deployment/Private/Deployment.ps1'='# inert private helper; never loaded';'tools/host/HostEnrollment.Common.ps1'="[IO.File]::WriteAllText('$sentinel','executed')"}
            $inventory=@(foreach ($name in $payload.Keys) {
                $path=Join-Path $root $name; [IO.File]::WriteAllText($path,$payload[$name],[Text.UTF8Encoding]::new($false))
                @{path=$name;length=([IO.FileInfo]$path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
            })
            [IO.File]::WriteAllText((Join-Path $root 'source.commit'),('1'*40)); [IO.File]::WriteAllText((Join-Path $root 'distribution.zip'),'inert independently test-pinned distribution')
            $auth=@{contractFamily='clc-host-enrollment-v1';recordKind='host-enrollment-authorization';schemaVersion=1;authorizationId='fixture-auth';targetId='ssrs-web-dev';bootstrap=@{sourceCommit=('1'*40);inventorySha256=(Get-SsrsInventoryHash $inventory)};hostPolicy=@{activeToolkit=@{distributionSha256=(Get-FileHash -LiteralPath (Join-Path $root 'distribution.zip') -Algorithm SHA256).Hash.ToLowerInvariant()}}}
            [IO.File]::WriteAllBytes((Join-Path $authority 'fixture-auth.json'),(Json-Bytes $auth))
            $opened=Open-SsrsBootstrap 'fixture-auth'
            try { Assert-True ($opened.leases.Count -gt 0 -and -not [IO.File]::Exists($sentinel)) } finally { foreach ($lease in $opened.leases) { $lease.Dispose() } }
            Assert-Fails { Open-SsrsBootstrap 'missing-auth' } 'Ssrs.Prerequisite.IndependentHostAuthorizationRequired'
            [IO.File]::AppendAllText((Join-Path $root 'distribution.zip'),'wrong bytes')
            Assert-Fails { Open-SsrsBootstrap 'fixture-auth' } 'Ssrs.ApprovedDistributionMismatch'
            [IO.File]::WriteAllText((Join-Path $root 'distribution.zip'),'inert independently test-pinned distribution')
            $helperPath=Join-Path $root 'CLC.Deployment/Private/Deployment.ps1'
            [IO.File]::AppendAllText($helperPath,'changed')
            Assert-Fails { Open-SsrsBootstrap 'fixture-auth' } 'Ssrs.BootstrapInventoryMismatch'
            [IO.File]::WriteAllText($helperPath,$payload['CLC.Deployment/Private/Deployment.ps1'],[Text.UTF8Encoding]::new($false))
            $foreignPath=Join-Path $root 'CLC.Deployment/Private/Foreign.ps1'
            [IO.File]::WriteAllText($foreignPath,'unexpected generated file')
            Assert-Fails { Open-SsrsBootstrap 'fixture-auth' } 'Ssrs.BootstrapInventoryMismatch'
            [IO.File]::Delete($foreignPath)
            [IO.File]::AppendAllText((Join-Path $root 'tools/host/HostEnrollment.Common.ps1'),'# byte drift')
            Assert-Fails { Open-SsrsBootstrap 'fixture-auth' } 'Ssrs.BootstrapInventoryMismatch'
            Assert-True (-not [IO.File]::Exists($sentinel))
        }
    }
    Test-Case 'Transport authentication, exact extraction and create-new inbox reuse execute against actual ZIP bytes' {
        $root=Join-Path $repository ('artifacts/dev-artifact-boundary/'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($root)
        $transport=Join-Path $root 'fixture-transport.zip'; $inner=[Text.Encoding]::UTF8.GetBytes('inert synthetic release payload; never code')
        $zip=[IO.Compression.ZipFile]::Open($transport,[IO.Compression.ZipArchiveMode]::Create)
        try { $entry=$zip.CreateEntry('release.zip'); $stream=$entry.Open(); try { $stream.Write($inner) } finally { $stream.Dispose() } } finally { $zip.Dispose() }
        $a=Get-TestApproval; $a.releaseZipSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inner)).ToLowerInvariant(); $a.provenance.transportSha256=(Get-FileHash -LiteralPath $transport -Algorithm SHA256).Hash.ToLowerInvariant()
        $global:SsrsTransportTest=@{approval=$a;source=$transport;calls=[Collections.Generic.List[string]]::new();inspectionCount=0}
        $testModule=New-Module -Name ('SsrsArtifactTests'+[Guid]::NewGuid().ToString('N')) -ScriptBlock {
            function Read-ClcHeldFile($Path,$Handles,$Maximum,[switch] $NoCapture) {
                $file=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read); $Handles.Add($file)
                return @{sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)).ToLowerInvariant()}
            }
            function Test-ClcRelease($ArchivePath) {
                $global:SsrsTransportTest.inspectionCount++; $a=$global:SsrsTransportTest.approval
                return @{integrityVerified=$true;authorizedForDeployment=$false;applicationId='ssrs-web';releaseId=$a.releaseId;sourceCommit=$a.sourceCommit;manifestSha256=$a.manifestSha256;archiveSha256=(Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()}
            }
        }
        function Invoke-SsrsGitHub([string] $Path,[string] $OutputPath) {
            $global:SsrsTransportTest.calls.Add($Path); $a=$global:SsrsTransportTest.approval
            if ($OutputPath) { [IO.File]::Copy($global:SsrsTransportTest.source,$OutputPath,$false); return }
            if ($Path -ceq '/repos/clcdpc/ssrs-web') { return @{id=123L;full_name='clcdpc/ssrs-web'} }
            if ($Path.Contains('/artifacts/')) { return @{id=88L;name='ssrs-web-release-42-2';expired=$false;digest=('sha256:'+$a.provenance.transportSha256);workflow_run=@{id=42L;repository_id=123L;head_repository_id=123L;head_branch='master';head_sha=$a.sourceCommit}} }
            if ($Path.Contains('/jobs?')) { return @{total_count=1L;jobs=@(@{name='release';run_id=42L;run_attempt=2L;head_sha=$a.sourceCommit;status='completed';conclusion='success'})} }
            return @{id=42L;run_attempt=2L;head_sha=$a.sourceCommit;head_branch='master';path='.github/workflows/release.yml';event='workflow_dispatch';status='completed';conclusion='success';repository=@{id=123L};head_repository=@{id=123L}}
        }
        $inbox=Join-Path $root 'inbox'; [void][IO.Directory]::CreateDirectory($inbox)
        $selection=@{approval=$a;target=@{artifactInboxRoot=$inbox};module=$testModule;context=@{handles=[Collections.Generic.List[IDisposable]]::new()}}
        try {
            $received=Receive-SsrsArtifact $selection
            Assert-True ($received.transport -ceq 'IndependentlyPinnedTransportVerified' -and [IO.File]::Exists($received.path) -and $global:SsrsTransportTest.inspectionCount -eq 1)
            Assert-True ((Get-FileHash -LiteralPath $received.path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $a.releaseZipSha256)
            $calls=$global:SsrsTransportTest.calls.Count
            $cached=Receive-SsrsArtifact $selection; Test-SsrsSelectedRelease $selection $cached.path
            Assert-True ($cached.transport -ceq 'NotDownloadedApprovedInboxReused' -and $global:SsrsTransportTest.calls.Count -eq $calls)
            foreach ($h in $selection.context.handles) { $h.Dispose() }; $selection.context.handles.Clear()
            [IO.File]::WriteAllText($received.path,'collision retained')
            Assert-Fails { Receive-SsrsArtifact $selection } 'Ssrs.InboxCollision'
            foreach ($h in $selection.context.handles) { $h.Dispose() }; $selection.context.handles.Clear()
            Assert-True ([IO.File]::ReadAllText($received.path) -ceq 'collision retained')
            [IO.File]::Delete($received.path)
            [IO.File]::WriteAllText($transport,'not even a ZIP: wrong transport digest')
            $before=$global:SsrsTransportTest.inspectionCount
            Assert-Fails { Receive-SsrsArtifact $selection } 'Ssrs.ArtifactTransportMismatch'
            Assert-True ($global:SsrsTransportTest.inspectionCount -eq $before -and -not [IO.File]::Exists($received.path))
        } finally { foreach ($h in $selection.context.handles) { $h.Dispose() }; Remove-Variable -Name SsrsTransportTest -Scope Global }
    }
    # Only external I/O boundaries are replaced. The core under test is the exact
    # function extracted above from the future trusted workflow definition.
    $global:SsrsWorkflowTest=@{cleanupFailure=$false}
    $script:stubModule=New-Module -Name ('SsrsWorkflowTests'+[Guid]::NewGuid().ToString('N')) -ScriptBlock {
        function Close-ClcTrustedContext($Context) { if ($global:SsrsWorkflowTest.cleanupFailure) { throw [InvalidOperationException]::new('PRIVATE_CLEANUP_EXCEPTION') } }
    }
    function New-TestEnrollmentResult([string] $State,[bool] $InitializationPerformed) {
        $action=switch ($State) {
            'Ready' {'InvokeSeparatelyApprovedRelease'}
            'ToolkitNotInitialized' {'PlanAuthorizedInitialization'}
            'TargetNotEnrolled' {'PlanAuthorizedInitialization'}
            'Incomplete' {'ReconcileRetainedInfrastructure'}
            'DriftDetected' {'ReconcileConfiguration'}
            'RecoveryRequired' {'UseExistingExplicitRecovery'}
        }
        $code=switch ($State) {
            'Ready' {'VerifiedEnrollment'}
            'ToolkitNotInitialized' {'InstallationAuthorizationRequired'}
            'TargetNotEnrolled' {'EnrollmentAuthorizationRequired'}
            'Incomplete' {'PartialToolkitInstallation'}
            'DriftDetected' {'ProtectedTargetMismatch'}
            'RecoveryRequired' {'UnresolvedDeploymentState'}
        }
        return [pscustomobject]@{TargetId='ssrs-web-dev';HostState=$State;ReadyForDeployment=($State -ceq 'Ready');InitializationPerformed=$InitializationPerformed;
            RequiresOperatorIntervention=($State -cin @('Incomplete','DriftDetected','RecoveryRequired'));ObservedPoolState='Unknown';WorkerCount=$null;Code=$code;NextAction=$action}
    }
    function Get-SsrsEnrollment($Bootstrap,[string] $AuthorizationId) {
        $script:events.Add('Inspect'); $position=[Math]::Min($script:inspectionIndex,$script:states.Count-1); $state=$script:states[$position]; $script:inspectionIndex++
        if ($script:inspectionErrorRecord -and $script:inspectionIndex -eq $script:inspectionErrorOn) { throw $script:inspectionErrorRecord }
        # A new public inspection session reports its own performed flag as false;
        # the workflow must retain a successful Apply=true from the same run.
        return (New-TestEnrollmentResult $state $false)
    }
    function Open-SsrsApplicationPreflight($Bootstrap,[string] $AuthorizationId,[bool] $AfterEnrollment) { $script:events.Add('Preflight'+$AfterEnrollment); if ($script:preflightFailure -and $AfterEnrollment) { Stop-Ssrs 'Ssrs.Prerequisite.SubscriptionIisSetup' }; return @{context=$null;handles=@()} }
    function Close-SsrsSession($Session) {}
    function Initialize-SsrsEnrollment($Bootstrap,[string] $AuthorizationId,[string] $State) { $script:events.Add('Initialize'+$State); if ($script:setupErrorRecord) { throw $script:setupErrorRecord }; if ($script:setupFailure) { Stop-Ssrs $script:setupFailure }; return (New-TestEnrollmentResult 'Ready' $true) }
    function Open-SsrsSelection($Bootstrap,[string] $AuthorizationId,[string] $ApprovalId) { $script:events.Add('SelectApproval'); if ($script:approvalMissing) { Stop-Ssrs 'Clc.ApprovalMissing' }; return @{approval=(Get-TestApproval);module=$script:stubModule;context=$null;session=$null} }
    function Receive-SsrsArtifact($Selection) { $script:events.Add('Acquire'); if ($script:artifactFailure) { Stop-Ssrs 'Ssrs.ArtifactTransportMismatch' }; return @{path='synthetic';transport='IndependentlyPinnedTransportVerified'} }
    function Test-SsrsSelectedRelease($Selection,[string] $ArchivePath) { $script:events.Add('InspectArtifact') }
    function Invoke-SsrsApprovedDeployment($Selection,[string] $ApprovalId) {
        $script:events.Add('Invoke'); if ($script:deploymentErrorRecord) { throw $script:deploymentErrorRecord }; if ($script:deploymentFailure) { Stop-Ssrs $script:deploymentFailure }
        return @{status=$script:outcome;targetId='ssrs-web-dev';releaseId='ssrs-web-test-1';sourceCommit=('1'*40);attemptId=[Guid]::NewGuid().ToString('D');currentGeneration=1L;observedPoolState='Started';workerCount=1L;executingToolkit=@{version='0.1.0-dev';sourceCommit=('2'*40)}}
    }
    function Reset-Core([string[]] $States) {
        $script:states=$States; $script:inspectionIndex=0; $script:inspectionErrorRecord=$null; $script:inspectionErrorOn=0; $script:events=[Collections.Generic.List[string]]::new(); $script:setupFailure=$null; $script:setupErrorRecord=$null; $script:artifactFailure=$false
        $script:approvalMissing=$false; $script:deploymentFailure=$null; $script:deploymentErrorRecord=$null; $script:preflightFailure=$false; $script:outcome='Deployed'; $global:SsrsWorkflowTest.cleanupFailure=$false
    }
    $bootstrap=@{authorization=@{cutover=$null;permittedOperations=@('install-toolkit','enroll-target')}}
    Test-Case 'Ready enrollment skips initialization and reports the actual deployed/no-op result' {
        foreach ($outcome in @('Deployed','NoOp')) {
            Reset-Core @('Ready'); $script:outcome=$outcome; $evidence=New-SsrsEvidence; $result=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence
            Assert-True ($result.outcome -ceq $outcome -and $result.readiness -ceq 'ToolkitVerified' -and $result.initializationPerformed -eq $false -and $result.requiresOperatorIntervention -eq $false)
            Assert-True (-not @($script:events | Where-Object { $_ -clike 'Initialize*' }).Count)
        }
    }
    Test-Case 'Missing toolkit or target performs only authorized setup then requires a new Ready inspection' {
        foreach ($state in @('ToolkitNotInitialized','TargetNotEnrolled')) {
            Reset-Core @($state,'Ready'); $evidence=New-SsrsEvidence; $result=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence
            Assert-True ($result.initializationPerformed -eq $true -and $result.enrollmentState -ceq 'Ready' -and $result.nextAction -ceq 'None' -and $script:events.Contains('Initialize'+$state))
        }
        Reset-Core @('ToolkitNotInitialized','TargetNotEnrolled'); Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false (New-SsrsEvidence) } 'Ssrs.EnrollmentReinspectionRequired'
    }
    Test-Case 'Incomplete, drift and recovery states preserve evidence and never initialize or deploy' {
        foreach ($state in @('Incomplete','DriftDetected','RecoveryRequired')) {
            Reset-Core @($state); $evidence=New-SsrsEvidence; Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } ('Ssrs.Enrollment.'+$state)
            Assert-True ($script:events.Count -eq 1 -and -not $script:events.Contains('Invoke'))
            $expectedAction=switch ($state) {'Incomplete' {'ReconcileRetainedInfrastructure'} 'DriftDetected' {'ReconcileConfiguration'} 'RecoveryRequired' {'UseExistingExplicitRecovery'}}
            Assert-True ($evidence.enrollmentState -ceq $state -and $evidence.requiresOperatorIntervention -eq $true -and $evidence.nextAction -ceq $expectedAction)
        }
    }
    Test-Case 'Expired, disabled or wrong setup authorization failures cannot reach release selection' {
        foreach ($code in @('Clc.EnrollmentAuthorizationExpired','Clc.EnrollmentAuthorizationInvalid','Clc.EnrollmentOperationNotApproved')) {
            Reset-Core @('ToolkitNotInitialized'); $script:setupFailure=$code
            $evidence=New-SsrsEvidence
            Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } $code
            Assert-True ($evidence.enrollmentState -ceq 'Unknown' -and $null -eq $evidence.initializationPerformed -and $evidence.requiresOperatorIntervention -eq $true)
            Assert-True (-not $script:events.Contains('SelectApproval'))
        }
    }
    Test-Case 'Core Apply and deployment exceptions reach safe evidence with only typed native fields' {
        Reset-Core @('ToolkitNotInitialized')
        $partialTarget=[pscustomobject]@{HostState='RecoveryRequired';InitializationPerformed=$true;RequiresOperatorIntervention=$true;
            NextAction='RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation';ObservedPoolState='Stopped';WorkerCount=0L;privateSha256='PRIVATE_APPLY_HASH'}
        $script:setupErrorRecord=[Management.Automation.ErrorRecord]::new([IO.InvalidDataException]::new('PRIVATE_APPLY_EXCEPTION'),'Clc.EnrollmentFailed',[Management.Automation.ErrorCategory]::InvalidData,$partialTarget)
        $partialEvidence=New-SsrsEvidence; $caught=$null
        try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $partialEvidence } catch { $caught=$_ }
        Assert-True ($null -ne $caught -and $partialEvidence.enrollmentState -ceq 'Unknown' -and $null -eq $partialEvidence.initializationPerformed -and $partialEvidence.requiresOperatorIntervention -eq $true)
        Set-SsrsSafeFailure $partialEvidence $caught
        Assert-True ($partialEvidence.enrollmentState -ceq 'RecoveryRequired' -and $partialEvidence.initializationPerformed -eq $true -and
            $partialEvidence.nextAction -ceq 'RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation' -and $partialEvidence.observedPoolState -ceq 'Stopped' -and $partialEvidence.workerCount -eq 0)
        $partialJson=$partialEvidence | ConvertTo-Json -Compress
        Assert-True (-not $partialJson.Contains('PRIVATE_APPLY_HASH') -and -not $partialJson.Contains('PRIVATE_APPLY_EXCEPTION'))

        Reset-Core @('Ready')
        $postMutationTarget=[pscustomobject]@{phase='RecoveryRequired';mutationScope='PossiblyLive';nextAction='RetainEvidenceAndSelectExplicitRecoveryOrMaintenance';
            observedPoolState='Stopped';workerCount=0L;attemptId=[Guid]::NewGuid().ToString('D');privateSha256='PRIVATE_POST_MUTATION_HASH'}
        $script:deploymentErrorRecord=[Management.Automation.ErrorRecord]::new([IO.InvalidDataException]::new('{"raw":"PRIVATE_POST_MUTATION_EXCEPTION"}'),'Clc.ReadinessFailed',[Management.Automation.ErrorCategory]::InvalidData,$postMutationTarget)
        $deploymentEvidence=New-SsrsEvidence; $caught=$null
        try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $deploymentEvidence } catch { $caught=$_ }
        Assert-True ($null -ne $caught -and $script:events.Contains('Invoke'))
        Set-SsrsSafeFailure $deploymentEvidence $caught
        Assert-True ($deploymentEvidence.deploymentPhase -ceq 'RecoveryRequired' -and $deploymentEvidence.mutationScope -ceq 'PossiblyLive' -and
            $deploymentEvidence.requiresOperatorIntervention -eq $true -and $deploymentEvidence.nextAction -ceq 'RetainEvidenceAndSelectExplicitRecoveryOrMaintenance')
        $deploymentJson=$deploymentEvidence | ConvertTo-Json -Compress
        Assert-True (-not $deploymentJson.Contains('PRIVATE_POST_MUTATION_HASH') -and -not $deploymentJson.Contains('PRIVATE_POST_MUTATION_EXCEPTION'))
    }
    Test-Case 'Successful Apply remains sticky when its fresh inspection fails with native initialization false' {
        Reset-Core @('ToolkitNotInitialized','Ready')
        $target=[pscustomobject]@{HostState='RecoveryRequired';InitializationPerformed=$false;RequiresOperatorIntervention=$true;
            NextAction='RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation';ObservedPoolState='Stopped';WorkerCount=0L;AuthorizationId='PRIVATE_REINSPECTION_AUTH';privateSha256='PRIVATE_REINSPECTION_HASH'}
        $script:inspectionErrorRecord=[Management.Automation.ErrorRecord]::new([IO.InvalidDataException]::new('private native failure'),'Clc.EnrollmentFailed',[Management.Automation.ErrorCategory]::InvalidData,$target)
        $script:inspectionErrorOn=2; $evidence=New-SsrsEvidence; $caught=$null
        try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } catch { $caught=$_ }
        Assert-True ($null -ne $caught -and $script:events.Contains('InitializeToolkitNotInitialized') -and $evidence.initializationPerformed -eq $true)
        Set-SsrsSafeFailure $evidence $caught
        Assert-True ($evidence.initializationPerformed -eq $true -and $evidence.enrollmentState -ceq 'RecoveryRequired' -and
            $evidence.nextAction -ceq 'RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation')
        $finalJson=$evidence | ConvertTo-Json -Depth 6 -Compress
        Assert-True ($finalJson.Contains('"initializationPerformed":true') -and $finalJson.Contains('"nextAction":"RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation"') -and
            -not $finalJson.Contains('PRIVATE_REINSPECTION_AUTH') -and -not $finalJson.Contains('PRIVATE_REINSPECTION_HASH'))
    }
    Test-Case 'Failures after Ready and before Invoke replace the stale successful action' {
        Reset-Core @('Ready'); $script:preflightFailure=$true; $evidence=New-SsrsEvidence; $caught=$null
        try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } catch { $caught=$_ }
        Assert-True ($null -ne $caught -and -not $script:events.Contains('Invoke') -and $evidence.nextAction -ceq 'InvokeSeparatelyApprovedRelease')
        Set-SsrsSafeFailure $evidence $caught
        Assert-True ($evidence.nextAction -ceq 'ReviewIndependentPrerequisitesAndRetainEvidence' -and $evidence.requiresOperatorIntervention -eq $true -and $evidence.mutationScope -ceq 'None')

        Reset-Core @('Ready'); $script:artifactFailure=$true; $evidence=New-SsrsEvidence; $caught=$null
        try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } catch { $caught=$_ }
        Assert-True ($null -ne $caught -and -not $script:events.Contains('Invoke'))
        Set-SsrsSafeFailure $evidence $caught
        Assert-True ($evidence.nextAction -ceq 'ReviewIndependentPrerequisitesAndRetainEvidence' -and $evidence.requiresOperatorIntervention -eq $true -and $evidence.mutationScope -ceq 'None')
    }
    Test-Case 'Invoke uncertainty survives unstructured errors and malformed results; cleanup errors cannot report success action' {
        foreach ($kind in @('UnstructuredInvoke','MalformedResult')) {
            Reset-Core @('Ready'); $evidence=New-SsrsEvidence
            if ($kind -ceq 'UnstructuredInvoke') { $script:deploymentFailure='Clc.ReadinessFailed' } else { $script:outcome='Malformed' }
            $caught=$null; try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } catch { $caught=$_ }
            Assert-True ($null -ne $caught -and $script:events.Contains('Invoke') -and $evidence.deploymentPhase -ceq 'Unknown' -and $evidence.mutationScope -ceq 'Unknown')
            Set-SsrsSafeFailure $evidence $caught
            Assert-True ($evidence.outcome -ceq 'Failed' -and $evidence.requiresOperatorIntervention -eq $true -and
                $evidence.nextAction -ceq 'PreserveEvidenceAndUseInfrastructureMaintenance')
        }
        Reset-Core @('Ready'); $evidence=New-SsrsEvidence; $global:SsrsWorkflowTest.cleanupFailure=$true; $caught=$null
        try { $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } catch { $caught=$_ }
        $global:SsrsWorkflowTest.cleanupFailure=$false
        Assert-True ($null -ne $caught -and $evidence.outcome -ceq 'Deployed' -and $evidence.deploymentPhase -ceq 'Completed' -and $evidence.mutationScope -ceq 'None')
        Set-SsrsSafeFailure $evidence $caught
        Assert-True ($evidence.nextAction -ceq 'ReviewIndependentPrerequisitesAndRetainEvidence' -and $evidence.requiresOperatorIntervention -eq $true -and $evidence.nextAction -cne 'None')
    }
    Test-Case 'Supported setup can finish before a missing independent release approval stops invocation' {
        Reset-Core @('ToolkitNotInitialized','Ready'); $script:approvalMissing=$true; $evidence=New-SsrsEvidence
        Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false $evidence } 'Clc.ApprovalMissing'
        Assert-True ($evidence.initializationPerformed -and -not $script:events.Contains('Invoke'))
    }
    Test-Case 'Enabled-subscription prerequisites are checked after new supported enrollment and before invocation' {
        Reset-Core @('ToolkitNotInitialized','Ready'); $script:preflightFailure=$true
        Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false (New-SsrsEvidence) } 'Ssrs.Prerequisite.SubscriptionIisSetup'
        Assert-True ($script:events.Contains('InitializeToolkitNotInitialized') -and -not $script:events.Contains('Invoke'))
    }
    Test-Case 'First cutover is explicit, independently scoped, and never repeated on Ready' {
        $bootstrap.authorization.cutover=@{synthetic=$true}
        Reset-Core @('ToolkitNotInitialized','Ready'); Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false (New-SsrsEvidence) } 'Ssrs.IndependentCutoverAuthorizationRequired'
        Reset-Core @('ToolkitNotInitialized','Ready'); Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $true (New-SsrsEvidence) } 'Ssrs.IndependentCutoverAuthorizationRequired'
        $bootstrap.authorization.permittedOperations+= 'first-cutover'
        Reset-Core @('ToolkitNotInitialized','Ready'); $null=Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $true (New-SsrsEvidence); Assert-True $script:events.Contains('Invoke')
        Reset-Core @('Ready'); Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $true (New-SsrsEvidence) } 'Ssrs.FirstCutoverAlreadyCompleted'
        $bootstrap.authorization.cutover=$null
    }
    Test-Case 'Artifact failure precedes invocation and toolkit failures never trigger automatic restore' {
        Reset-Core @('Ready'); $script:artifactFailure=$true; Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false (New-SsrsEvidence) } 'Ssrs.ArtifactTransportMismatch'; Assert-True (-not $script:events.Contains('Invoke'))
        foreach ($code in @('Clc.ReadinessFailed','Clc.RecoveryRequired','Clc.TargetBusy')) {
            Reset-Core @('Ready'); $script:deploymentFailure=$code; Assert-Fails { Invoke-SsrsDevCore $bootstrap 'auth' 'approval' $false (New-SsrsEvidence) } $code
            Assert-True ($script:events.Contains('Invoke') -and -not @($script:events | Where-Object { $_ -clike 'Restore*' }).Count)
        }
    }
} finally {
    $directory=Join-Path $repository ('artifacts/dev-workflow-tests/'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($directory)
    $report=@{classification='synthetic-workflow-orchestration';liveHostQualified=$false;windowsNativeBridgeCasesExecuted=[bool]$IsWindows;windowsNativeBridgeStatus=$(if ($IsWindows) {'Executed'} else {'NOT RUN - Windows required'});discovered=$results.Count;passed=@($results | Where-Object status -EQ Passed).Count;failed=@($results | Where-Object status -EQ Failed).Count;cases=$results}
    [IO.File]::WriteAllText((Join-Path $directory 'results.json'),($report | ConvertTo-Json -Depth 10))
    Write-Output "DEV workflow: $($report.passed)/$($report.discovered) passed; results $directory/results.json"
}
