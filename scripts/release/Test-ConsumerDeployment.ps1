#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ToolkitRoot,
    [string] $PesterManifest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$ToolkitRoot = (Resolve-Path -LiteralPath $ToolkitRoot).Path

function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$configPath = Join-Path $repository '.clc/release.json'
$pin = (Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json).orchestration.sharedRevision
$head = git -C $ToolkitRoot rev-parse HEAD
Assert-Contract ($LASTEXITCODE -eq 0 -and $head -ceq $pin) 'The toolkit checkout must match the orchestration pin.'
. (Join-Path $ToolkitRoot 'tools/release/Workflow.Common.ps1')
$config = Read-ClcConsumerConfiguration $configPath
Assert-Contract ($config.contractVersion -eq 3 -and $config.requiredToolkitContractVersion -eq 3 -and
    $config.applicationId -ceq 'ssrs-web' -and $config.repository -ceq 'clcdpc/ssrs-web' -and
    $config.integrationBranch -ceq 'master' -and $config.devRuntimeEnvironment -ceq 'Production') 'Wrong consumer application or contract.'
Assert-Contract ($config.producer.workflowPath -ceq '.github/workflows/release.yml' -and $config.producer.job -ceq 'package' -and
    -not $config.producer.Contains('jobName')) 'Wrong producer identity.'

$release = Get-Content -Raw -LiteralPath (Join-Path $repository $config.producer.workflowPath)
$init = Get-Content -Raw -LiteralPath (Join-Path $repository '.github/workflows/initialize-deployment.yml')
$verify = Get-Content -Raw -LiteralPath (Join-Path $repository '.github/workflows/verify.yml')
$sharedWorkflows = @([regex]::Matches($release, '(?m)^    uses: (clcdpc/clc-deployment/[^\s]+)\s*$') | ForEach-Object { $_.Groups[1].Value })
Assert-Contract ($sharedWorkflows.Count -eq 2 -and
    "clcdpc/clc-deployment/.github/workflows/application-release.yml@$pin" -cin $sharedWorkflows -and
    "clcdpc/clc-deployment/.github/workflows/application-deploy.yml@$pin" -cin $sharedWorkflows) 'The shared publication and deployment workflow revisions must match the orchestration pin.'
foreach ($workflow in @($release, $init, $verify)) {
    $checkout = [regex]::Matches($workflow, '(?m)^          repository: clcdpc/clc-deployment\r?\n          ref: ([^\s]+)\s*$')
    Assert-Contract ($checkout.Count -eq 1 -and $checkout[0].Groups[1].Value -ceq $pin) 'An upstream checkout pin differs.'
    Assert-Contract ($workflow -cnotmatch 'workflow_run|HostEnrollment|StandingAuthorization|ReleaseApproval|toolkit\.lock|TOOLKIT_READ_TOKEN') 'Obsolete deployment path in workflow.'
}
foreach ($workflow in @($release, $verify)) {
    Assert-Contract ($workflow.Contains('./artifacts/clc-deployment/tools/HostedScratch.ps1') -and
        $workflow -cmatch '\bNew-ClcHostedScratchDirectory\b') 'Hosted native packaging must use the pinned NTFS scratch selector.'
}
$qualifiedScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Test-QualifiedPackage.ps1')
foreach ($source in @($release, $init, $verify, $qualifiedScript)) {
    Assert-Contract ($source -cnotmatch 'src[/\\]Clc\.Deployment[/\\]Private[/\\]') 'Consumers must use upstream entry points without private imports.'
}
Assert-Contract ($release -cmatch "(?m)^  push:\r?\n    tags: \['v\*'\]" -and $release -cmatch '(?m)^  package:' -and
    $release -cmatch '(?m)^    needs: package\s*$' -and $release -cmatch '(?m)^    needs: \[package, publication\]\s*$' -and
    $release -cmatch 'New-ClcApplicationPackage\.ps1' -and $release -cmatch '-QualifiedPublishRoot' -and
    $release -cmatch 'steps.package.outputs.artifact_name' -and $release -cnotmatch 'self-hosted') 'Release must qualify, publish, then deploy through the shared chain.'
Assert-Contract ($init -cmatch '(?m)^  host:\r?\n    needs: admit\s*$' -and
    $init.Contains('environment: ${{ needs.admit.outputs.target-id }}') -and
    $init.Contains('runs-on: [self-hosted, Windows, X64, "${{ needs.admit.outputs.target-id }}"]') -and
    $init.Contains('CLC_RUNTIME_ACCOUNT: ${{ vars.CLC_RUNTIME_ACCOUNT }}') -and
    $init.Contains('Test-ClcDeploymentHost @target -ExpectedRuntimeAccount $env:CLC_RUNTIME_ACCOUNT -RuntimeEnvironment Production') -and
    $init.Contains('Initialize-ClcDeploymentHost @target -Operation $env:CLC_OPERATION') -and
    $init -cnotmatch 'upload-artifact|Invoke-ClcIisDeployment|Restore-ClcIisDeployment|inputs\.[^ }]+\s*\}\}.*\r?\n.*\$\(') 'Initialization runner or allowed operations changed.'

foreach ($case in @(
    @{ tag = 'v1.2.3-dev.4'; id = 'v1-2-3-dev-4'; channel = 'DEV'; target = 'ssrs-web-dev' }
    @{ tag = 'v1.2.3'; id = 'v1-2-3'; channel = 'Production'; target = 'ssrs-web-production' }
    @{ tag = 'v1.2.3-beta.1'; id = 'v1-2-3-beta-1'; channel = 'ArtifactOnly'; target = $null }
    @{ tag = 'v1.2.3-rc.1'; id = 'v1-2-3-rc-1'; channel = 'ArtifactOnly'; target = $null }
)) {
    $identity = Get-ClcApplicationTag $case.tag
    $route = Get-ClcDeploymentRoute $config @{ identity = $identity }
    Assert-Contract ($route.targetId -ceq $case.target -and $identity.releaseId -ceq $case.id -and
        $identity.channel -ceq $case.channel) "Wrong identity for $($case.tag)."
    if ($case.target) {
        Assert-Contract ($route.environment -ceq $case.target -and $route.runtimeEnvironment -ceq 'Production' -and
            ($route.runnerLabels -join '/') -ceq "self-hosted/Windows/X64/$($case.target)") "Wrong route for $($case.tag)."
    } else {
        Assert-Contract ($route.Count -eq 1) "Artifact-only tag $($case.tag) must not select an environment or runner."
    }
}

# Execute the actual hosted job's admission script without scheduling a host.
$block = [regex]::Match($init.Replace("`r`n", "`n"), '(?ms)^        run: \|\n(?<script>(?:^ {10}[^\n]*\n)+)')
Assert-Contract $block.Success 'The hosted admission script could not be found.'
$bash = if ($IsWindows) {
    Get-Command git -All -CommandType Application | ForEach-Object {
        Join-Path (Split-Path (Split-Path $_.Source)) 'bin/bash.exe'
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
} else {
    (Get-Command bash -CommandType Application).Source
}
Assert-Contract (-not [string]::IsNullOrWhiteSpace($bash)) 'Git Bash is required to test Windows workflow admission.'
$work = Join-Path $repository ('artifacts/host-admission/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$scriptPath = Join-Path $work 'admit.sh'
$outputPath = Join-Path $work 'output'
[IO.File]::WriteAllText($scriptPath, [regex]::Replace($block.Groups['script'].Value, '(?m)^ {10}', ''))
$defaults = @{
    GITHUB_REPOSITORY = 'clcdpc/ssrs-web'; GITHUB_EVENT_NAME = 'workflow_dispatch'; GITHUB_REF = 'refs/heads/master'
    GITHUB_WORKFLOW_REF = 'clcdpc/ssrs-web/.github/workflows/initialize-deployment.yml@refs/heads/master'
    GITHUB_WORKFLOW_SHA = ('1' * 40); GITHUB_SHA = ('1' * 40); REF_PROTECTED = 'true'
    TARGET = 'DEV'; OPERATION = 'Preview'; GITHUB_OUTPUT = $outputPath
    SITE_NAME = 'Test only'; POOL_NAME = 'Test only'; READINESS_URI = 'https://ssrs.invalid/health/ready'
}
foreach ($case in @(
    @{ env = @{}; expected = 'ssrs-web-dev' }
    @{ env = @{ TARGET = 'Production'; OPERATION = 'Initialize' }; expected = 'ssrs-web-production' }
    @{ env = @{ OPERATION = 'Validate'; SITE_NAME = ''; POOL_NAME = ''; READINESS_URI = '' }; expected = 'ssrs-web-dev' }
    @{ env = @{ GITHUB_REF = 'refs/heads/untrusted' }; expected = '' }
    @{ env = @{ GITHUB_EVENT_NAME = 'pull_request' }; expected = '' }
    @{ env = @{ GITHUB_REPOSITORY = 'example/ssrs-web' }; expected = '' }
    @{ env = @{ GITHUB_WORKFLOW_REF = 'clcdpc/ssrs-web/.github/workflows/other.yml@refs/heads/master' }; expected = '' }
    @{ env = @{ GITHUB_WORKFLOW_SHA = ('2' * 40) }; expected = '' }
    @{ env = @{ REF_PROTECTED = 'false' }; expected = '' }
    @{ env = @{ TARGET = 'ssrs-web-production' }; expected = '' }
    @{ env = @{ SITE_NAME = ' '; OPERATION = 'Initialize' }; expected = '' }
    @{ env = @{ OPERATION = 'Restore' }; expected = '' }
)) {
    [IO.File]::WriteAllText($outputPath, '')
    $start = [Diagnostics.ProcessStartInfo]::new($bash)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.ArgumentList.Add($scriptPath.Replace('\', '/'))
    foreach ($key in $defaults.Keys) { $start.Environment[$key] = $defaults[$key] }
    foreach ($key in $case.env.Keys) { $start.Environment[$key] = $case.env[$key] }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $detail = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        $output = [IO.File]::ReadAllText($outputPath).TrimEnd()
        if ($case.expected) {
            Assert-Contract ($process.ExitCode -eq 0 -and $output -ceq "target-id=$($case.expected)") "Expected host admission: $detail"
        } else {
            Assert-Contract ($process.ExitCode -ne 0 -and -not $output) 'A rejected dispatch scheduled a host.'
        }
    } finally {
        $process.Dispose()
    }
}
Write-Output 'Consumer configuration, routing and hosted initialization admission checks passed.'

if ($PesterManifest) {
    Import-Module (Resolve-Path -LiteralPath $PesterManifest).Path -Force
    $pester = New-PesterConfiguration
    $pester.Run.Path = @('SharedApplicationRelease', 'ReleaseApi', 'HostedReleaseExecution', 'IisConfiguration', 'Lifecycle') | ForEach-Object {
        Join-Path $ToolkitRoot "tests/$_.Tests.ps1"
    }
    $pester.Run.PassThru = $true
    $pester.Output.Verbosity = 'Normal'
    $result = Invoke-Pester -Configuration $pester
    Assert-Contract ($result.Result -ceq 'Passed' -and $result.PassedCount -gt 0 -and
        $result.SkippedCount -eq 0 -and $result.NotRunCount -eq 0 -and $result.InconclusiveCount -eq 0) 'Upstream synthetic deployment checks did not complete.'
}
