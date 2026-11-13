#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ToolkitSourceRoot,
    [Parameter(Mandatory)][string] $ReleaseArchivePath,
    [Parameter(Mandatory)][string] $UpgradeReleaseArchivePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $ToolkitSourceCommit
)
# Synthetic qualification only. The imported upstream module is unchanged. All
# IIS/platform/authorization observations are test-owned; no IIS API is called.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.ToString() -cne '7.6.6') { throw 'This rehearsal requires Windows and PowerShell 7.6.6.' }
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$toolkit=[IO.Path]::GetFullPath($ToolkitSourceRoot)
$archive=[IO.Path]::GetFullPath($ReleaseArchivePath)
$upgradeArchive=[IO.Path]::GetFullPath($UpgradeReleaseArchivePath)
$scriptHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedSource=$ToolkitSourceCommit
# A clean Windows checkout can still have converted source bytes. Authenticate
# every tracked byte using the reviewed producer guard, including isolated Git
# configuration/environment and absolute executable selection, before import.
. (Join-Path $PSScriptRoot 'ExactSourceBytes.Common.ps1')
Assert-ExactSourceBytes -RepositoryRoot $toolkit -Commit $expectedSource
$evidenceRoot=Join-Path $repository ('artifacts/deployment-integration/'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($evidenceRoot)
$results=[Collections.Generic.List[object]]::new()
function Assert-Bridge([bool] $Condition,[string] $Description) { if (-not $Condition) { throw ('Bridge assertion: '+$Description) } }
function Test-BridgeCase {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)][string] $Name,[Parameter(Mandatory,Position=1)][scriptblock] $Body)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    & $Body
    $results.Add(@{name=$Name;status='Passed';seconds=$clock.Elapsed.TotalSeconds})
    Write-Host ('PASS '+$Name)
}
function Get-BridgeInventory([string] $Root) {
    $items=[ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) { $items[$file.FullName.Substring($Root.Length)]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    return ($items | ConvertTo-Json -Compress)
}
function Get-BridgeLiveSnapshot($Fixture) {
    # Release these observation handles before Invoke: the oracle must detect an
    # attempted same-byte rewrite or file replacement rather than block it.
    $records=[Collections.Generic.List[object]]::new()
    $paths=@($Fixture.target.applicationRoot)+@(Get-ChildItem -LiteralPath $Fixture.target.applicationRoot -Recurse -Force | ForEach-Object FullName)+@((Join-Path $Fixture.controlRoot 'current.json'))
    foreach ($path in @($paths | Sort-Object -CaseSensitive)) {
        $handle=[Clc.Foundation.NativePaths]::Open($path)
        try {
            $facts=[Clc.Foundation.NativePaths]::Observe($handle)
            Assert-Bridge (($facts.Attributes -band 0x400) -eq 0) 'NoOp snapshot has no reparse point'
            [Clc.Foundation.NativePaths]::CheckStreams($path)
            $item=Get-Item -LiteralPath $path -Force
            $records.Add([ordered]@{path=$path;volume=$facts.VolumeId;fileId=$facts.FileId;attributes=$facts.Attributes;links=$facts.LinkCount;
                acl=[Clc.Foundation.NativePaths]::SecurityHash($handle);created=$item.CreationTimeUtc.Ticks;written=$item.LastWriteTimeUtc.Ticks;
                content=$(if ($item.PSIsContainer) {$null} else {(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash})})
        } finally { $handle.Dispose() }
    }
    return ($records.ToArray() | ConvertTo-Json -Depth 4 -Compress)
}
function Assert-BridgeBackup($Fixture,$Manifest,[string] $AttemptId,[string] $ExpectedIndexHash) {
    Assert-Bridge ($AttemptId -cmatch '^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$') 'bounded backup attempt selector'
    $root=Join-Path $Fixture.controlRoot ('backups\'+$AttemptId)
    $indexPath=Join-Path $root 'index.json'
    $indexHash=(Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Bridge ($indexHash -ceq $ExpectedIndexHash) 'retained backup index bytes'
    $index=Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json -AsHashtable
    Assert-Bridge ($index.priorEmpty -is [bool] -and -not $index.priorEmpty -and $index.attemptId -ceq $AttemptId -and
        $index.targetKey -ceq $Fixture.targetKey -and $index.inventorySha256 -ceq $Manifest.inventorySha256 -and
        $index.files.Count -eq $Manifest.files.Count) 'nonempty prior backup identity and inventory'
    for ($i=0;$i -lt $Manifest.files.Count;$i++) {
        $expected=$Manifest.files[$i]; $indexed=$index.files[$i]
        Assert-Bridge ($indexed.Count -eq 3 -and $indexed.path -ceq $expected.path -and
            $indexed.length -is [long] -and $indexed.length -eq $expected.length -and
            $indexed.sha256 -ceq $expected.sha256) 'backup index retains exact prior file entries'
    }
    $payload=Join-Path $root 'application'
    $actual=@(Get-ChildItem -LiteralPath $payload -Recurse -Force -File)
    Assert-Bridge ($actual.Count -eq $Manifest.files.Count) 'complete backup file count'
    foreach ($file in $Manifest.files) {
        $path=Join-Path $payload $file.path.Substring('application/'.Length).Replace('/','\')
        Assert-Bridge ((Get-Item -LiteralPath $path).Length -eq $file.length -and
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $file.sha256) 'independent retained backup file length and hash'
    }
}
function Stop-BridgeProgram {
    if ($null -ne $global:SsrsBridge.process) {
        if (-not $global:SsrsBridge.process.HasExited) { $global:SsrsBridge.process.Kill(); Assert-Bridge ($global:SsrsBridge.process.WaitForExit(10000)) 'owned application exited' }
        $global:SsrsBridge.process.Dispose(); $global:SsrsBridge.process=$null
    }
}
function Start-BridgeProgram {
    Assert-Bridge ($null -eq $global:SsrsBridge.process) 'no previous owned process'
    $info=[Diagnostics.ProcessStartInfo]::new($global:SsrsBridge.dotnet)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.WorkingDirectory=$global:ClcTestFixture.target.applicationRoot
    $info.Environment.Clear()
    $info.Environment['SystemRoot']=$env:SystemRoot; $info.Environment['WINDIR']=$env:WINDIR
    foreach ($arg in @((Join-Path $info.WorkingDirectory 'SsrsPortal.dll'),'--environment','Production','--urls',$global:SsrsBridge.address)) { $info.ArgumentList.Add($arg) }
    $p=[Diagnostics.Process]::new(); $p.StartInfo=$info
    Assert-Bridge ($p.Start()) 'owned application started'
    $global:SsrsBridge.process=$p
    # Drain both pipes while the actual application runs; store no log payload in
    # the public evidence record. These are ignored synthetic diagnostics only.
    $global:SsrsBridge.stdout=$p.StandardOutput.ReadToEndAsync(); $global:SsrsBridge.stderr=$p.StandardError.ReadToEndAsync()
    $global:SsrsBridge.readinessCalls=0L
}
function Invoke-BridgeReadiness {
    $global:SsrsBridge.readinessCalls++
    $global:SsrsBridge.httpCalls++
    try {
        if ($null -eq $global:SsrsBridge.process -or $global:SsrsBridge.process.HasExited) { return @{Accepted=$false;Bytes=[byte[]]::new(0)} }
        $response=$global:SsrsBridge.client.GetAsync(($global:SsrsBridge.address+'/health/ready')).GetAwaiter().GetResult()
        try {
            $bytes=$response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $accepted=$response.StatusCode -eq [Net.HttpStatusCode]::OK -and $response.Content.Headers.ContentType.MediaType -ceq 'application/json' -and
                $response.Headers.CacheControl.NoStore -and -not $response.Headers.Contains('Set-Cookie') -and -not $response.Headers.Contains('Location') -and $bytes.Length -le 4096
            if ($accepted) {
                $global:ClcTestFixture.iis.workers=@([long]$global:SsrsBridge.process.Id)
                $global:SsrsBridge.successfulHttp++
            }
            if ($accepted -and $global:SsrsBridge.failReadiness) { $global:SsrsBridge.forcedHttpFailureObserved=$true; $accepted=$false }
            return @{Accepted=$accepted;Bytes=$bytes}
        } finally { $response.Dispose() }
    } catch { return @{Accepted=$false;Bytes=[byte[]]::new(0)} }
}
function New-BridgeFixture {
    $fixture=New-T2Fixture
    $oldTarget=Join-Path $fixture.layout.targetCatalogRoot ($fixture.target.targetId+'.json')
    [IO.File]::Delete($oldTarget)
    $fixture.target.targetId='ssrs-web-dev'; $fixture.target.applicationId='ssrs-web'; $fixture.target.environmentName='DEV'
    $fixture.target.operationalConfigPath=Join-Path $conventionalRoot 'Config/application.json'; $fixture.target.dataProtectionRoot=Join-Path $conventionalRoot 'Keys'
    $fixture.policy.dotnetPath=$global:SsrsBridge.dotnet
    Write-TestJson $fixture.layout.policyPath $fixture.policy
    Write-TestJson (Join-Path $fixture.layout.targetCatalogRoot 'ssrs-web-dev.json') $fixture.target
    Copy-Item -LiteralPath $archive -Destination (Join-Path $fixture.target.artifactInboxRoot 'synthetic-bridge.zip')
    return $fixture
}
function Save-BridgeEvidence($Fixture,[string] $Destination) {
    [void][IO.Directory]::CreateDirectory($Destination)
    if ($Fixture -and [IO.Directory]::Exists($Fixture.controlRoot)) {
        foreach ($file in Get-ChildItem -LiteralPath $Fixture.controlRoot -Filter '*.json' -File -Recurse) {
            $relative=$file.FullName.Substring($Fixture.controlRoot.Length+1)
            $target=Join-Path $Destination $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }
}
function Save-BridgeProcessLogs([string] $Destination) {
    [void][IO.Directory]::CreateDirectory($Destination)
    if ($global:SsrsBridge.ContainsKey('stdout')) { $global:SsrsBridge.stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Destination 'application.stdout.log') }
    if ($global:SsrsBridge.ContainsKey('stderr')) { $global:SsrsBridge.stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Destination 'application.stderr.log') }
}
function Set-BridgeModuleAdapters($Module) {
    $global:SsrsBridge.replace= & $Module { (Get-Command Invoke-ClcManagedReplacement).ScriptBlock }
    $global:SsrsBridge.backup= & $Module { (Get-Command New-ClcVerifiedBackup).ScriptBlock }
    & $Module {
        # Only test-owned observations are replaced. The actual validator native
        # launcher, NTFS/lock/ZIP/approval/state/lifecycle/recovery engine stays.
        function script:Get-ClcInstalledLayout { return $global:ClcTestFixture.layout }
        function script:Get-ClcCallerSid { return 'S-1-5-19' }
        function script:Test-ClcAdministratorToken { return $true }
        function script:Assert-ClcRuntimeIdentity { param($Context,$RuntimeSid) }
        function script:Assert-ClcAccess { param($Path,$Owners,$Writers,$Readers,$DeniedWriters,$ReadMask,$WriteMask) return (Get-ClcSecurityObservation $Path).sha256 }
        function script:Assert-ClcTrustAncestors { param($Context) }
        function script:Get-ClcMachineUuidObservation { return $global:ClcTestFixture.policy.machineIdentity }
        function script:Get-ClcIisPoolNames { return @($global:ClcTestFixture.target.applicationPoolName) }
        function script:Get-ClcIisObservation { return $global:ClcTestFixture.iis }
        function script:Get-ClcPlatformObservation { return @{build='20348';productType=3L;is64Bit=$true;powershellMajor=7L} }
        function script:Get-ClcRuntimeObservation { return @{frameworks=@(@{name='Microsoft.NETCore.App';version='10.0.12'},@{name='Microsoft.AspNetCore.App';version='10.0.12'});ancmMajor=20L;ancmX64=$true;dotnetX64=$true} }
        function script:Assert-ClcHostAccess { }
        function script:Get-ClcPoolObservation { param($Lock) return @{poolState=$global:ClcTestFixture.iis.poolState;workerCount=[long]$global:ClcTestFixture.iis.workers.Count} }
        function script:Assert-ClcHelperProtection { param($Context) Assert-ClcTrustedContextUnchanged $Context }
        # Runtime/helper/access/platform qualification remains synthetic. The
        # original host preflight and descendant walks are production code.
        function script:Assert-ClcHelperDependencies { param($Context,$RuntimeSid) }
        function script:Start-ClcIisHelper {
            param($Lock,$Operation)
            $global:SsrsBridge.operations.Add($Operation)
            if ($Operation -ceq 'Start') { Start-BridgeProgram; $global:ClcTestFixture.iis.poolState='Started'; $global:ClcTestFixture.iis.workers=@([long]$global:SsrsBridge.process.Id) }
            else { Stop-BridgeProgram; $global:ClcTestFixture.iis.poolState='Stopped'; $global:ClcTestFixture.iis.workers=@() }
            return Start-T3HarmlessHelper ''
        }
        function script:Invoke-ClcReadinessRequest { param([string] $Uri) return Invoke-BridgeReadiness }
        function script:Get-ClcReadinessElapsed {
            param([Diagnostics.Stopwatch] $Clock)
            if ($global:SsrsBridge.failReadiness -and $global:SsrsBridge.forcedHttpFailureObserved) { return 120 }
            return $Clock.Elapsed.TotalSeconds
        }
        function script:New-ClcVerifiedBackup {
            param($Session)
            $clock=[Diagnostics.Stopwatch]::StartNew()
            try { & $global:SsrsBridge.backup $Session }
            finally { $global:SsrsBridge.backups.Add(@{seconds=$clock.Elapsed.TotalSeconds}) }
        }
        function script:Invoke-ClcManagedReplacement {
            param($Session,$Source,$SelectedFiles,$ExistingFiles,[switch]$Partial,$OtherFiles=@(),[switch]$AllowChanged)
            $clock=[Diagnostics.Stopwatch]::StartNew()
            try { & $global:SsrsBridge.replace $Session $Source $SelectedFiles $ExistingFiles -Partial:$Partial -OtherFiles $OtherFiles -AllowChanged:$AllowChanged }
            finally { $global:SsrsBridge.replacements.Add(@{seconds=$clock.Elapsed.TotalSeconds;files=$SelectedFiles.Count;recovery=[bool]$Partial}) }
        }
    }
}
# The module's test adapters resolve only global helpers, exactly as upstream's
# fixed harmless-helper fixture does. This script runs in its own pwsh process.
foreach ($name in @('Assert-Bridge','Start-BridgeProgram','Stop-BridgeProgram','Invoke-BridgeReadiness')) {
    Set-Item -Path ('Function:global:'+$name) -Value (Get-Command $name).ScriptBlock
}
$fixture=$null; $ownedConventional=$false; $shortRoot=$null; $markerName=''; $conventionalRoot=''; $programDataLease=$null; $previousTemp=$env:TEMP; $previousTmp=$env:TMP
$global:SsrsBridge=@{process=$null;dotnet=(Get-Command dotnet -CommandType Application).Source;httpCalls=0L;successfulHttp=0L;readinessCalls=0L;failReadiness=$false;forcedHttpFailureObserved=$false;operations=[Collections.Generic.List[string]]::new();replacements=[Collections.Generic.List[object]]::new();backups=[Collections.Generic.List[object]]::new()}
$httpHandler=[Net.Http.HttpClientHandler]::new(); $httpHandler.AllowAutoRedirect=$false; $httpHandler.UseCookies=$false
$global:SsrsBridge.client=[Net.Http.HttpClient]::new($httpHandler); $global:SsrsBridge.client.Timeout=[TimeSpan]::FromSeconds(2)
try {
    # Reuse the actual inline OS resolver, without invoking its privileged entry
    # point or trusting caller-controlled ProgramData environment variables.
    $workflow=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/deploy-dev.yml'))
    $start=$workflow.IndexOf('          # SSRS-DEV-FUNCTIONS-BEGIN'); $end=$workflow.IndexOf('          # SSRS-DEV-FUNCTIONS-END')
    if ($start -lt 0 -or $end -le $start) { throw 'Missing workflow function boundary.' }
    . ([ScriptBlock]::Create([regex]::Replace($workflow.Substring($start,$end-$start),'(?m)^ {10}','')))
    Initialize-SsrsNative
    $programData=[SsrsWorkflow.Native]::ProgramData()
    $programDataLease=[SsrsWorkflow.Native]::Pin($programData,$true)
    $conventionalRoot=Join-Path $programData 'clc-ssrs-web'
    # Directory.Move is same-volume create-new: an existing operational root is
    # never opened, enumerated, changed or cleaned by this rehearsal.
    $sibling=Join-Path $programData ('ssrs-bridge-'+[Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($sibling)
    try { [IO.Directory]::Move($sibling,$conventionalRoot); $ownedConventional=$true }
    catch { if ([IO.Directory]::Exists($sibling) -and [IO.Directory]::GetFileSystemEntries($sibling).Length -eq 0) { [IO.Directory]::Delete($sibling) }; throw 'Synthetic prerequisite: conventional ProgramData namespace must be absent; existing content was left untouched.' }
    $markerName='synthetic-bridge-owner-'+[Guid]::NewGuid().ToString('N')+'.txt'
    [IO.File]::WriteAllText((Join-Path $conventionalRoot $markerName),'synthetic-unapproved-qualification')
    foreach ($name in @('Config','Keys')) { [void][IO.Directory]::CreateDirectory((Join-Path $conventionalRoot $name)) }
    # Short, exclusively owned Temp avoids the unchanged toolkit path budgets.
    $shortRoot=Join-Path ([IO.Path]::GetPathRoot($repository)) ('ssrs-b-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    if ([IO.Directory]::Exists($shortRoot) -or [IO.File]::Exists($shortRoot)) { throw 'Short synthetic root collision.' }
    [void][IO.Directory]::CreateDirectory($shortRoot)
    $acl=Get-Acl -LiteralPath $shortRoot
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $shortRoot -AclObject $acl
    $env:TEMP=$shortRoot; $env:TMP=$shortRoot
    . (Join-Path $toolkit 'tests/Support.ps1')
    . (Join-Path $toolkit 'tests/T2Support.ps1')
    . (Join-Path $toolkit 'tests/T3Support.ps1')
    Set-Item -Path Function:global:Start-T3HarmlessHelper -Value (Get-Command Start-T3HarmlessHelper).ScriptBlock
    $configuration=@{AllowedHosts='localhost;127.0.0.1';Deployment=@{ConfigurationRevision='bridge-001'};
        DataProtection=@{KeyStoreCompatibilityId='ssrs-web-dpapi-user-v1';KeyRingPath=(Join-Path $conventionalRoot 'Keys')};
        Portal=@{UseMock=$false;TestAuth=$false;ReportServer='https://127.0.0.1:1/ReportServer';ReportAccessPolicy='GlobalBlocklist';AllowedRoots=@('/Synthetic');ExcludedPrefixes=@()};
        Entra=@{ClientId=[Guid]::NewGuid().ToString('D');ClientSecret='synthetic-unused-secret';AllowedTenantIds=@('00000000-0000-0000-0000-000000000123')};
        CatalogMonitor=@{Enabled=$false;ConnectionString=$null};Subscriptions=@{Enabled=$false};Logging=@{File=@{Enabled=$true;Rotation='Daily';MaxFileSizeBytes=52428800L;RetentionDays=30L}}}
    $configurationPath=Join-Path $conventionalRoot 'Config/application.json'
    Write-TestJson $configurationPath $configuration
    $fixture=New-BridgeFixture; Set-T2FixtureInModule $fixture
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start()
    $global:SsrsBridge.address='http://127.0.0.1:'+([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $module=Get-Module CLC.Deployment
    Set-BridgeModuleAdapters $module
    $integrity=Test-ClcRelease $archive
    Assert-Bridge ($integrity.integrityVerified -and -not $integrity.authorizedForDeployment) 'actual package integrity only'
    $manifestBytes=(Get-TestZipEntries $archive | Where-Object name -CEQ 'manifest.json').bytes
    $manifest=[Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json -AsHashtable -DateKind String
    $approval=Get-Content -LiteralPath (Join-Path $toolkit 'tests/Fixtures/release-approval.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $approval.approvalId='synthetic-bridge'; $approval.targetId='ssrs-web-dev'; $approval.applicationId='ssrs-web'; $approval.releaseId=$manifest.releaseId
    $approval.sourceRepository=$manifest.source.repository; $approval.sourceCommit=$manifest.source.commit; $approval.releaseZipSha256=$integrity.archiveSha256; $approval.manifestSha256=$integrity.manifestSha256
    $approval.provenance.workflowPath=$manifest.build.workflowPath; $approval.provenance.runId=$manifest.build.runId; $approval.provenance.runAttempt=$manifest.build.runAttempt; $approval.provenance.buildJob=$manifest.build.job
    Write-TestJson (Join-Path $fixture.layout.approvalCatalogRoot 'synthetic-bridge.json') $approval
    $upgradeIntegrity=Test-ClcRelease $upgradeArchive
    $upgradeManifest=[Text.Encoding]::UTF8.GetString((Get-TestZipEntries $upgradeArchive | Where-Object name -CEQ 'manifest.json').bytes) | ConvertFrom-Json -AsHashtable -DateKind String
    Assert-Bridge ($upgradeManifest.applicationId -ceq 'ssrs-web' -and $upgradeManifest.releaseId -cne $manifest.releaseId -and $upgradeIntegrity.archiveSha256 -cne $integrity.archiveSha256) 'distinct compiled approved upgrade artifact'
    $upgradeApproval=$approval | ConvertTo-Json -Depth 12 | ConvertFrom-Json -AsHashtable
    $upgradeApproval.approvalId='synthetic-upgrade'; $upgradeApproval.releaseSequence=2L; $upgradeApproval.releaseId=$upgradeManifest.releaseId
    $upgradeApproval.sourceRepository=$upgradeManifest.source.repository; $upgradeApproval.sourceCommit=$upgradeManifest.source.commit
    $upgradeApproval.releaseZipSha256=$upgradeIntegrity.archiveSha256; $upgradeApproval.manifestSha256=$upgradeIntegrity.manifestSha256
    $upgradeApproval.provenance.workflowPath=$upgradeManifest.build.workflowPath; $upgradeApproval.provenance.runId=$upgradeManifest.build.runId
    $upgradeApproval.provenance.runAttempt=$upgradeManifest.build.runAttempt; $upgradeApproval.provenance.buildJob=$upgradeManifest.build.job
    Write-TestJson (Join-Path $fixture.layout.approvalCatalogRoot 'synthetic-upgrade.json') $upgradeApproval
    Copy-Item -LiteralPath $upgradeArchive -Destination (Join-Path $fixture.target.artifactInboxRoot 'synthetic-upgrade.zip')
    $script:activeApproval='synthetic-bridge'; $script:activeManifest=$manifest
    $script:first=$null; $script:noop=$null; $script:restarted=$null; $script:recovered=$null; $script:keyInventory=''; $script:backups=@{}
    Test-BridgeCase 'Actual public deployment validates the compiled release, publishes NTFS content and accepts actual anonymous HTTP readiness' {
        $script:first=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval
        Assert-Bridge ($first.status -ceq 'Deployed' -and $first.currentGeneration -eq 1 -and $first.workerCount -eq 1) 'first deployed generation'
        Assert-Bridge ($global:SsrsBridge.successfulHttp -gt 0) 'actual ready HTTP accepted'
        Assert-Bridge (@(Get-ChildItem -LiteralPath $fixture.target.dataProtectionRoot -Filter 'key-*.xml').Count -gt 0) 'actual persistent DPAPI key generated by runtime'
        $script:keyInventory=Get-BridgeInventory $fixture.target.dataProtectionRoot
    }
    Test-BridgeCase 'Actual NoOp revalidates the existing ring and fresh readiness without a stop or start' {
        $global:SsrsBridge.operations.Clear(); $calls=$global:SsrsBridge.httpCalls
        $before=Get-BridgeLiveSnapshot $fixture; $replacementCount=$global:SsrsBridge.replacements.Count
        $stagingCount=@(Get-ChildItem -LiteralPath (Join-Path $fixture.controlRoot 'staging') -Directory -Force).Count
        $script:noop=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval
        Assert-Bridge ($noop.status -ceq 'NoOp' -and $noop.currentGeneration -eq 1 -and $global:SsrsBridge.operations.Count -eq 0) 'NoOp kept owned worker'
        Assert-Bridge ($global:SsrsBridge.httpCalls -gt $calls) 'fresh NoOp HTTP request'
        Assert-Bridge ((Get-BridgeInventory $fixture.target.dataProtectionRoot) -ceq $keyInventory) 'NoOp left key bytes unchanged'
        Assert-Bridge ((Get-BridgeLiveSnapshot $fixture) -ceq $before -and $global:SsrsBridge.replacements.Count -eq $replacementCount) 'NoOp preserved live/current bytes, namespace, identities, timestamps and ACLs without replacement'
        Assert-Bridge (@(Get-ChildItem -LiteralPath (Join-Path $fixture.controlRoot 'staging') -Directory -Force).Count -eq $stagingCount) 'NoOp created no new staging directories'
    }
    Test-BridgeCase 'A distinct compiled release upgrades the same fixture and retains the verified initial backup' {
        $script:activeApproval='synthetic-upgrade'; $script:activeManifest=$upgradeManifest
        $script:upgrade=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval
        Assert-Bridge ($upgrade.status -ceq 'Deployed' -and $upgrade.currentGeneration -eq 2 -and $upgrade.releaseId -ceq $upgradeManifest.releaseId) 'distinct release upgrade'
        $j=Get-Content -LiteralPath (Join-Path $fixture.controlRoot 'journal.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert-BridgeBackup $fixture $manifest $j.attemptId $j.backup.indexSha256
        $script:backups[$j.attemptId]=@{hash=$j.backup.indexSha256;manifest=$manifest}
        Assert-Bridge ((Get-BridgeInventory $fixture.target.dataProtectionRoot) -ceq $keyInventory) 'upgrade preserves persistent key bytes'
    }
    # Keep the actual same canonical application/control fixture and every
    # retained attempt, stage, manifest and backup through restart and recovery.
    Test-BridgeCase 'Full-history configuration revision restarts the complete release and preserves the existing key ring' {
        $configuration.Deployment.ConfigurationRevision='bridge-002'
        Write-TestJson $configurationPath $configuration
        $global:SsrsBridge.operations.Clear()
        $script:restarted=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval
        Assert-Bridge ($restarted.status -ceq 'Deployed' -and $restarted.currentGeneration -eq 3 -and $restarted.workerCount -eq 1) 'same-fixture configuration restart generation'
        Assert-Bridge ($global:SsrsBridge.operations.Contains('Stop') -and $global:SsrsBridge.operations.Contains('Start')) 'configuration change performed a controlled restart'
        Assert-Bridge ((Get-BridgeInventory $fixture.target.dataProtectionRoot) -ceq $keyInventory) 'restarted process retained key bytes'
        $j=Get-Content -LiteralPath (Join-Path $fixture.controlRoot 'journal.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert-Bridge ($j.backup.verified -is [bool] -and $j.backup.verified) 'toolkit recorded verified prior backup'
        Assert-BridgeBackup $fixture $activeManifest $j.attemptId $j.backup.indexSha256
        $script:backups[$j.attemptId]=@{hash=$j.backup.indexSha256;manifest=$activeManifest}
        Save-BridgeEvidence $fixture (Join-Path $evidenceRoot 'full-history-config-restart/control')
    }
    Test-BridgeCase 'Repeated full-history NoOp keeps generation three without live mutation' {
        $global:SsrsBridge.operations.Clear()
        $before=Get-BridgeLiveSnapshot $fixture; $replacementCount=$global:SsrsBridge.replacements.Count
        $stagingCount=@(Get-ChildItem -LiteralPath (Join-Path $fixture.controlRoot 'staging') -Directory -Force).Count
        $next=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval
        Assert-Bridge ($next.status -ceq 'NoOp' -and $next.currentGeneration -eq 3 -and $global:SsrsBridge.operations.Count -eq 0) 'same-fixture repeated NoOp'
        Assert-Bridge ((Get-BridgeLiveSnapshot $fixture) -ceq $before -and $global:SsrsBridge.replacements.Count -eq $replacementCount) 'repeated NoOp preserved live/current observations without replacement'
        Assert-Bridge (@(Get-ChildItem -LiteralPath (Join-Path $fixture.controlRoot 'staging') -Directory -Force).Count -eq $stagingCount) 'repeated NoOp created no new staging directories'
    }
    Test-BridgeCase 'Failed readiness retains a stopped recovery candidate and blocks routine retry' {
        $configuration.Deployment.ConfigurationRevision='bridge-003'
        Write-TestJson $configurationPath $configuration
        $global:SsrsBridge.failReadiness=$true
        $failure=$null
        try { $null=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval } catch { $failure=$_ }
        Assert-Bridge ($null -ne $failure -and $failure.FullyQualifiedErrorId -like 'Clc.ReadinessFailed*') 'actual readiness failure'
        Assert-Bridge $global:SsrsBridge.forcedHttpFailureObserved 'failure injection followed actual successful HTTP headers/body'
        $script:journal=Get-Content -LiteralPath (Join-Path $fixture.controlRoot 'journal.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert-Bridge ($journal.phase -ceq 'RecoveryRequired' -and $fixture.iis.poolState -ceq 'Stopped' -and $null -eq $global:SsrsBridge.process) 'stopped retained candidate'
        Assert-Bridge ($journal.backup.verified -is [bool] -and $journal.backup.verified) 'failed deployment retained a verified backup'
        Assert-BridgeBackup $fixture $activeManifest $journal.attemptId $journal.backup.indexSha256
        $script:backups[$journal.attemptId]=@{hash=$journal.backup.indexSha256;manifest=$activeManifest}
        $failure=$null; try { $null=Invoke-ClcIisDeployment ssrs-web-dev $activeApproval } catch { $failure=$_ }
        Assert-Bridge ($null -ne $failure -and $failure.FullyQualifiedErrorId -like 'Clc.RecoveryRequired*') 'routine retry blocked'
        Save-BridgeProcessLogs (Join-Path $evidenceRoot 'fault-fixture/failed-process')
        Save-BridgeEvidence $fixture (Join-Path $evidenceRoot 'fault-fixture/before-recovery')
    }
    Test-BridgeCase 'Explicit test-only AcceptCandidate starts a new actual application process and retains configuration and key bytes' {
        $configurationBytes=[IO.File]::ReadAllBytes($fixture.target.operationalConfigPath)
        $global:SsrsBridge.failReadiness=$false
        $script:recovered=Restore-ClcIisDeployment ssrs-web-dev $journal.attemptId AcceptCandidate synthetic-qualification
        Assert-Bridge ($recovered.status -ceq 'AcceptedCandidate' -and $recovered.currentGeneration -eq 4 -and $recovered.workerCount -eq 1) 'same-fixture explicit recovery generation'
        Assert-Bridge ((Get-BridgeInventory $fixture.target.dataProtectionRoot) -ceq $keyInventory) 'fresh process retained key bytes'
        Assert-Bridge ([Convert]::ToHexString([IO.File]::ReadAllBytes($fixture.target.operationalConfigPath)) -ceq [Convert]::ToHexString($configurationBytes)) 'retained operational configuration'
        $after=Get-Content -LiteralPath $fixture.target.operationalConfigPath -Raw | ConvertFrom-Json -AsHashtable
        Assert-Bridge ($after.Deployment.ConfigurationRevision -ceq 'bridge-003') 'recovery retained the explicitly selected new configuration revision'
        foreach ($attempt in $script:backups.Keys) { Assert-BridgeBackup $fixture $script:backups[$attempt].manifest $attempt $script:backups[$attempt].hash }
    }
    Save-BridgeEvidence $fixture (Join-Path $evidenceRoot 'fault-fixture/after-recovery')
    [ordered]@{classification='synthetic-cross-component-qualification';testScriptSha256=$scriptHash;toolkitSourceCommit=$expectedSource;releaseSourceCommit=$manifest.source.commit;releaseId=$manifest.releaseId;
        passed=$results.Count;cases=$results.ToArray();scenarioFixtures=@('same-canonical-fixture-through-deploy-noop-config-restart-failure-recovery');sequentialUpgradeQualified=$true;configurationRestartQualified=$true;retainedHistoryDeleted=$false;independentlyVerifiedRetainedBackups=$script:backups.Count;replacementTimings=$global:SsrsBridge.replacements.ToArray();backupTimings=$global:SsrsBridge.backups.ToArray();payloadFiles=$manifest.files.Count;payloadBytes=[long](($manifest.files | Measure-Object length -Sum).Sum);upgradePayloadFiles=$upgradeManifest.files.Count;upgradePayloadBytes=[long](($upgradeManifest.files | Measure-Object length -Sum).Sum);actualPublicInvoke=$true;actualPublicRestore=$true;actualMinimalEnvironmentValidator=$true;actualLoopbackHttp=$true;actualNtfsStateAndReplacement=$true;
        httpRequests=$global:SsrsBridge.httpCalls;acceptedActualHttp=$global:SsrsBridge.successfulHttp;firstStatus=$first.status;noOpStatus=$noop.status;recoveryStatus=$recovered.status;
        syntheticObservations=@('IIS/site/pool/worker','TLS/HTTPS transport','platform/runtime/helper-dependency/access/authorization qualification','readiness-failure elapsed clock');realHostQualified=$false;realHostPerformanceQualified=$false;productionDeadlinesUnchanged=$true;unmodifiedToolkitHostPreflight=$true;infrastructureApproved=$false;authorizedForDeployment=$false;realIisTouched=$false;sqlOperations=$false} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'results.json') -Encoding utf8NoBOM
    Write-Host ('Deployment integration: '+$results.Count+'/'+$results.Count+' passed; '+(Join-Path $evidenceRoot 'results.json'))
} catch {
    [ordered]@{classification='failed-synthetic-qualification';passed=$results.Count;error=$_.Exception.Message;stack=$_.ScriptStackTrace;realHostQualified=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'failure.json') -Encoding utf8NoBOM
    if ($fixture -and [IO.Directory]::Exists($fixture.controlRoot)) {
        foreach ($name in @('journal.json','current.json')) { $path=Join-Path $fixture.controlRoot $name; if ([IO.File]::Exists($path)) { Copy-Item -LiteralPath $path -Destination (Join-Path $evidenceRoot ('synthetic-'+$name)) } }
    }
    throw
} finally {
    try {
        Stop-BridgeProgram
        Save-BridgeProcessLogs $evidenceRoot
    } finally {
        $global:SsrsBridge.client.Dispose()
        try {
            if ($fixture -and $null -eq $global:SsrsBridge.process) {
                Save-BridgeEvidence $fixture (Join-Path $evidenceRoot 'final-control')
                Remove-TestWorkspace $fixture.workspace
            }
        } finally {
            $env:TEMP=$previousTemp; $env:TMP=$previousTmp
            try {
                if ($shortRoot -and [IO.Directory]::Exists($shortRoot) -and [IO.Directory]::GetFileSystemEntries($shortRoot).Length -eq 0) { [IO.Directory]::Delete($shortRoot) }
                if ($ownedConventional -and $null -eq $global:SsrsBridge.process) {
                    if ([IO.Path]::GetFullPath($conventionalRoot) -cne (Join-Path $programData 'clc-ssrs-web') -or -not [IO.File]::Exists((Join-Path $conventionalRoot $markerName))) { throw 'Synthetic conventional cleanup ownership lost.' }
                    foreach ($entry in @(Get-Item -LiteralPath $conventionalRoot)+@(Get-ChildItem -LiteralPath $conventionalRoot -Recurse -Force)) {
                        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Synthetic conventional cleanup refuses reparse point.' }
                    }
                    [IO.Directory]::Delete($conventionalRoot,$true)
                }
            } finally { if ($programDataLease) { $programDataLease.Dispose() } }
        }
    }
}
