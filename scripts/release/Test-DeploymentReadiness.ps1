#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string] $ActionlintPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workflowPath=Join-Path $repository '.github/workflows/diagnose-deployment.yml'
$workflow=[IO.File]::ReadAllText($workflowPath).Replace("`r`n","`n")
$revision='4df8d5158cd363c47d4e232af871fb395ea14c2c'
$checkout='actions/checkout@08c6903cd8c0fde910a37f88322edcfb5dd907a8'
$results=[Collections.Generic.List[string]]::new()
function Assert-True([bool] $Value) {
    if (-not $Value) { $caller=@(Get-PSCallStack)[1]; throw ('Diagnostic contract assertion failed at line '+$caller.ScriptLineNumber+': '+$caller.Command) }
}
function Test-Case([string] $Name,[scriptblock] $Body) { & $Body; $results.Add($Name) }
# Fixed indentation slices follow Test-DevDeployment's inline workflow convention.
# Actionlint owns YAML/schema/expression validation; this is not a YAML parser.
$github=[regex]::Match($workflow,'(?ms)^  github:\n.*?(?=^  host:)').Value
$hostJob=[regex]::Match($workflow,'(?ms)^  host:\n.*?(?=^  report:)').Value
$reportJob=[regex]::Match($workflow,'(?ms)^  report:\n.*\z').Value
$scripts=@([regex]::Matches($workflow,'(?m)^        run: \|\n((?:^          .*\n|^\n)+)') | ForEach-Object {
    [regex]::Replace($_.Groups[1].Value,'(?m)^ {10}','')
})

Test-Case 'Actionlint 1.7.12 accepts the GitHub Actions YAML and expressions' {
    $version=& $ActionlintPath -version 2>&1
    Assert-True ($LASTEXITCODE -eq 0 -and $version[0] -ceq '1.7.12')
    & $ActionlintPath $workflowPath
    Assert-True ($LASTEXITCODE -eq 0)
}
Test-Case 'Only manual dispatch, three independent stages and read-only permissions are defined' {
    Assert-True ($workflow -cmatch '(?m)^name: Validate SSRS Web deployment prerequisites$')
    Assert-True ($workflow -cmatch '(?m)^on:\n  workflow_dispatch:\n\npermissions:\n  contents: read\n  actions: read\n\njobs:')
    Assert-True ((@([regex]::Matches($workflow,'(?m)^  ([a-z]+):$') | ForEach-Object { $_.Groups[1].Value }) -join ',') -ceq 'github,host,report')
    Assert-True ($workflow -cnotmatch '(?m)^\s*(push|pull_request|pull_request_target|release|schedule|workflow_call|inputs|secrets|continue-on-error):|secrets\.|secrets: inherit|: write')
    Assert-True ($hostJob -cnotmatch '(?m)^    needs:')
    Assert-True ($github -cnotmatch '(?m)^    needs:|runs-on:|environment:')
}
Test-Case 'All executable shared code and checkout actions use the reviewed immutable pins' {
    $uses=@([regex]::Matches($workflow,'(?m)^\s+(?:- )?uses: (\S+)') | ForEach-Object { $_.Groups[1].Value })
    Assert-True ($uses.Count -eq 3 -and $uses[0] -ceq "clcdpc/clc-deployment/.github/workflows/validate-deployment.yml@$revision")
    Assert-True ($uses[1] -ceq $checkout -and $uses[2] -ceq $checkout)
    foreach ($job in @($hostJob,$reportJob)) {
        Assert-True ($job.Contains("          repository: clcdpc/clc-deployment`n          ref: '$revision'`n          path: shared-validator`n          persist-credentials: false"))
    }
    Assert-True ($workflow -cnotmatch '(?m)^\s*shared_revision:')
    Assert-True (-not ($uses -cmatch '@(?:master|main|refs/|v\d)'))
}
Test-Case 'Fixed repository, application, target, channel and environment bind both reports' {
    Assert-True ($github.Contains("if: github.repository == 'clcdpc/ssrs-web'"))
    foreach ($pair in @('application_id: ssrs-web','target_id: ssrs-web-dev','environment_name: ssrs-web-dev','integration_branch: master',
        'deployment_channel: DEV','release_workflow: .github/workflows/release.yml','deployment_workflow: .github/workflows/deploy-dev.yml',
        'diagnostic_workflow: .github/workflows/diagnose-deployment.yml','activation_variable: SSRS_DEV_DEPLOYMENT_ENABLED',
        'runner_group: ${{ vars.SSRS_DEV_RUNNER_GROUP }}','runner_labels: self-hosted,Windows,X64,ssrs-web-dev','administrative: false')) {
        Assert-True ($github.Contains("      $pair`n"))
    }
    foreach ($argument in @('-ApplicationId ssrs-web','-TargetId ssrs-web-dev','-EnvironmentName ssrs-web-dev','-DeploymentChannel DEV',
        '-Repository clcdpc/ssrs-web','-IntegrationBranch master','-ReleaseWorkflow .github/workflows/release.yml','-BuildJob release','-ToolkitManifestPath $manifest')) {
        Assert-True ($hostJob.Contains($argument))
    }
    Assert-True ($hostJob.Contains('    environment: ssrs-web-dev'))
}
Test-Case 'Routing requires protected source and separate diagnostic activation before host admission' {
    $condition=[regex]::Match($hostJob,'(?ms)^    if: >-\n(.*?)(?=^    runs-on:)').Groups[1].Value
    foreach ($guard in @("github.repository == 'clcdpc/ssrs-web'","github.event_name == 'workflow_dispatch'","github.ref == 'refs/heads/master'",
        'github.ref_protected',"github.workflow_ref == 'clcdpc/ssrs-web/.github/workflows/diagnose-deployment.yml@refs/heads/master'",
        'github.workflow_sha == github.sha',"vars.SSRS_DEV_DIAGNOSTIC_RUNNER_READY == 'true'","vars.SSRS_DEV_RUNNER_GROUP != ''","vars.SSRS_DEV_RUNNER_LABELS != ''")) {
        Assert-True ($condition.Contains($guard))
    }
    Assert-True ($condition -cnotmatch '\|\|')
    Assert-True ($hostJob.Contains('      group: ${{ vars.SSRS_DEV_RUNNER_GROUP }}'))
    Assert-True ($hostJob.Contains("      labels: `${{ fromJSON(vars.SSRS_DEV_RUNNER_LABELS || '[]') }}"))
    Assert-True ($hostJob -cnotmatch 'SSRS_DEV_DEPLOYMENT_ENABLED|SSRS_DEV_RUNNER_ADMISSION_READY|inputs\.|authorization_id|release_approval|first_cutover|StandingAuthorization')
    Assert-True ($hostJob -cnotmatch 'github\.token|GH_TOKEN|INSPECTION_TOKEN|id-token|environment: deployment-configuration-inspection')
}
Test-Case 'Host code uses the fixed authenticated toolkit selector and only the upstream inspector' {
    Assert-True ($hostJob.Contains("`$manifest='C:\ProgramData\CLC\Deployment\Enrollment\toolkit\versions\0.1.0-beta.3\CLC.Deployment\CLC.Deployment.psd1'"))
    Assert-True ($hostJob.Contains('shell: C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe -NoLogo -NoProfile -NonInteractive -CommandWithArgs'))
    Assert-True ($hostJob.Contains('[System.IO.File]::ReadAllText($args[0])'))
    Assert-True ($hostJob.Contains('& ./shared-validator/tools/deployment-prerequisites/Test-DeploymentHost.ps1'))
    Assert-True ($hostJob -cnotmatch '(?i)dotnet|npm|sqlcmd|Invoke-WebRequest|Invoke-RestMethod|Import-Module|Install-Module|Invoke-ClcIisDeployment|Restore-Clc|Initialize-Clc|Set-Web|Start-Web|Stop-Web|download-artifact|setup-|\$env:PATH')
    Assert-True (@([regex]::Matches($hostJob,'(?m)^      - ')).Count -eq 3)
    Assert-True ($hostJob.IndexOf('Ssrs.TrustedDiagnosticWorkflowRequired') -lt $hostJob.IndexOf('uses: actions/checkout'))
    Assert-True ($hostJob.Contains('ConvertTo-Json -InputObject $report -Depth 12 -Compress'))
    Assert-True ($hostJob.Contains('report_json: ${{ steps.inspect.outputs.report_json }}'))
    Assert-True ($hostJob.Contains('"report_json=$json" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8'))
}
Test-Case 'Every inline PowerShell block parses and the identity guard rejects untrusted contexts' {
    Assert-True ($scripts.Count -eq 3)
    foreach ($body in $scripts) {
        $syntaxErrors=$null
        $null=[Management.Automation.Language.Parser]::ParseInput($body,[ref]$null,[ref]$syntaxErrors)
        Assert-True (-not $syntaxErrors)
    }
    $trusted=@{GITHUB_REPOSITORY='clcdpc/ssrs-web';GITHUB_EVENT_NAME='workflow_dispatch';GITHUB_REF='refs/heads/master';GITHUB_REF_PROTECTED='true';
        GITHUB_WORKFLOW_REF='clcdpc/ssrs-web/.github/workflows/diagnose-deployment.yml@refs/heads/master';GITHUB_SHA=('1'*40);GITHUB_WORKFLOW_SHA=('1'*40);CLC_DIAGNOSTIC_RUNNER_READY='true'}
    $saved=@{}
    try {
        foreach ($key in $trusted.Keys) { $saved[$key]=[Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key,$trusted[$key]) }
        $guard=[ScriptBlock]::Create($scripts[0])
        & $guard
        foreach ($key in $trusted.Keys) {
            [Environment]::SetEnvironmentVariable($key,'untrusted')
            $rejected=$false
            try { & $guard } catch { $rejected=$_.Exception.Message -ceq 'Ssrs.TrustedDiagnosticWorkflowRequired' }
            Assert-True $rejected
            [Environment]::SetEnvironmentVariable($key,$trusted[$key])
        }
        foreach ($value in @('', 'false', 'TRUE')) {
            [Environment]::SetEnvironmentVariable('CLC_DIAGNOSTIC_RUNNER_READY',$value)
            $rejected=$false
            try { & $guard } catch { $rejected=$true }
            Assert-True $rejected
        }
    } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) } }
}
Test-Case 'Always-run hosted aggregation passes both results and reports through the upstream contract' {
    Assert-True ($reportJob.Contains("    if: always()`n    needs: [github, host]`n    runs-on: windows-2022"))
    foreach ($binding in @('CLC_GITHUB_REPORT: ${{ needs.github.outputs.report_json }}','CLC_HOST_REPORT: ${{ needs.host.outputs.report_json }}',
        'CLC_GITHUB_JOB: ${{ needs.github.result }}','CLC_HOST_JOB: ${{ needs.host.result }}')) {
        Assert-True ($reportJob.Contains($binding))
    }
    foreach ($binding in @('[IO.File]::WriteAllText($github,$env:CLC_GITHUB_REPORT)','[IO.File]::WriteAllText($hostReport,$env:CLC_HOST_REPORT)',
        '& ./shared-validator/tools/deployment-prerequisites/Merge-DeploymentReadiness.ps1 -GitHubReportPath $github -HostReportPath $hostReport -GitHubJobResult $env:CLC_GITHUB_JOB -HostJobResult $env:CLC_HOST_JOB',
        'exit $LASTEXITCODE')) {
        Assert-True ($reportJob.Contains($binding))
    }
    foreach ($output in @('github_status','host_status','ready','passed_count','failed_count','unverified_count','skipped_count','not_applicable_count')) {
        Assert-True ($reportJob.Contains("      ${output}: `${{ steps.report.outputs.$output }}"))
    }
    Assert-True ($reportJob -cnotmatch 'self-hosted|continue-on-error|READY_WITH_WARNINGS|exit 0|\|\|.*success')
}
Test-Case 'Aggregation forwards failed/skipped host states and preserves an upstream nonzero exit' {
    $probe=Join-Path $repository ('artifacts/diagnostic-workflow-tests/'+[guid]::NewGuid().ToString('N'))
    $stubDirectory=Join-Path $probe 'shared-validator/tools/deployment-prerequisites'
    [void][IO.Directory]::CreateDirectory($stubDirectory)
    # Stand in for the external command only; its readiness rules belong upstream.
    $stub=@'
param($GitHubReportPath,$HostReportPath,$GitHubJobResult,$HostJobResult)
@{github=[IO.File]::ReadAllText($GitHubReportPath);hostReport=[IO.File]::ReadAllText($HostReportPath);githubResult=$GitHubJobResult;hostResult=$HostJobResult} |
    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $env:RUNNER_TEMP 'received.json')
exit 37
'@
    [IO.File]::WriteAllText((Join-Path $stubDirectory 'Merge-DeploymentReadiness.ps1'),$stub)
    $scriptPath=Join-Path $probe 'aggregate.ps1'
    [IO.File]::WriteAllText($scriptPath,$scripts[2])
    foreach ($result in @('failure','skipped','cancelled')) {
        $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=$probe
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$scriptPath)) { $start.ArgumentList.Add($argument) }
        $start.Environment['RUNNER_TEMP']=$probe
        $start.Environment['CLC_GITHUB_REPORT']='{"fixture":"github"}'
        $start.Environment['CLC_HOST_REPORT']='{"fixture":"host"}'
        $start.Environment['CLC_GITHUB_JOB']='success'; $start.Environment['CLC_HOST_JOB']=$result
        $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
        try {
            Assert-True $process.Start()
            Assert-True ($process.WaitForExit(15000))
            Assert-True ($process.ExitCode -eq 37 -and [string]::IsNullOrEmpty($process.StandardError.ReadToEnd()))
            $received=Get-Content -LiteralPath (Join-Path $probe 'received.json') -Raw | ConvertFrom-Json
            Assert-True ($received.github -ceq '{"fixture":"github"}' -and $received.hostReport -ceq '{"fixture":"host"}')
            Assert-True ($received.githubResult -ceq 'success' -and $received.hostResult -ceq $result)
        } finally {
            if (-not $process.HasExited) { $process.Kill(); $null=$process.WaitForExit(5000) }
            $process.Dispose()
        }
    }
}
Test-Case 'No normal release/deployment/verification workflow invokes or depends on the diagnostic' {
    foreach ($file in @('release.yml','deploy-dev.yml','verify.yml')) {
        $text=[IO.File]::ReadAllText((Join-Path $repository ".github/workflows/$file"))
        Assert-True ($text -cnotmatch 'diagnose-deployment\.yml|SSRS_DEV_DIAGNOSTIC_RUNNER_READY|Merge-DeploymentReadiness|validate-deployment\.yml')
    }
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $repository '.clc/release.json')))
}
Write-Output "Deployment diagnostic integration: $($results.Count)/$($results.Count) passed (static/synthetic only; no host inspection)."
