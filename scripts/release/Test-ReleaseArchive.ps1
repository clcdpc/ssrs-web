#Requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/TagRelease.Common.ps1"
$work=Join-Path $PSScriptRoot ('../../artifacts/release-archive-tests/'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$results=[Collections.Generic.List[object]]::new()
function Assert-True([bool] $Value) { if (-not $Value) { throw ('Archive assertion at '+@(Get-PSCallStack)[1].ScriptLineNumber) } }
function Assert-Fails([scriptblock] $Body,[string] $Code) {
    $failed=$false
    try { $null=& $Body } catch { $failed=$true; if ($_.Exception.Message -notlike "*$Code*") { throw } }
    Assert-True $failed
}
function Test-Case([string] $Name,[scriptblock] $Body) {
    & $Body; $results.Add(@{name=$Name;status='Passed'})
}
function Copy-Record($Value) { return ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 30) -AsHashtable -Depth 30 -NoEnumerate }
function Reset-Service {
    $script:releases=@(); $script:assets=@(); $script:bytes=@{}; $script:writes=@(); $script:race=$false
    $script:currentSource='1'*40; $script:tagObject='1'*40; $script:prOpen=$false; $script:consumerSuccessful=$true
}
function Invoke-TagGitHub([string] $Path,[string] $OutputPath) {
    switch -Regex ($Path) {
        '/releases\?' { return ,$script:releases }
        '/releases/[0-9]+/assets\?' { return ,$script:assets }
        '/releases/assets/([0-9]+)$' { [IO.File]::WriteAllBytes($OutputPath,$script:bytes[[long]$Matches[1]]); return }
        '/git/ref/tags/(.+)$' { return @{ref=('refs/tags/'+$Matches[1]);object=@{type='commit';sha=$script:tagObject}} }
        '/branches/master$' { return @{name='master';protected=$true;commit=@{sha=('7'*40)}} }
        '/compare/' { return @{base_commit=@{sha=($Path -split '/compare/')[1].Split('.')[0]};status=$(if ($Path.Contains('1'*40)) {'diverged'} else {'ahead'})} }
        '/pulls\?' { return ,@() }
        '/ssrs-web/$' { return @{id=123L;full_name='clcdpc/ssrs-web';default_branch='master'} }
        '/workflows/deploy-release.yml$' { return @{id=10L;path='.github/workflows/deploy-release.yml'} }
        '/actions/runs/99/attempts/1$' { return @{id=99L;run_attempt=1L;head_sha=('7'*40);head_branch='master';repository=@{id=123L};head_repository=@{id=123L};
            event='workflow_run';status='completed';path='.github/workflows/deploy-release.yml';workflow_id=10L} }
        '/actions/runs/99/attempts/1/jobs\?' { return @{total_count=1;jobs=@(@{name='archive';run_id=99L;run_attempt=1L;head_sha=('7'*40);status='completed';
            conclusion=$(if ($script:consumerSuccessful) {'success'} else {'failure'});labels=@('windows-2022')})} }
        default { throw ('Unexpected fixture read: '+$Path) }
    }
}
function Invoke-SsrsReleaseWrite([string] $Method,[string] $Path,$Body,[string] $FilePath) {
    $script:writes+=@{method=$Method;path=$Path;body=$Body}
    if ($Path -ceq '/repos/clcdpc/ssrs-web/releases') {
        if ($script:race) { $script:releases=$script:savedRelease; $script:assets=$script:savedAssets; $script:bytes=$script:savedBytes; throw 'SyntheticDuplicateCreate' }
        $r=Copy-Record $Body; $r.id=5L; $r.author=@{id=41898282L;login='github-actions[bot]'}; $r.immutable=$false
        $script:releases=@($r); return $r
    }
    if ($FilePath) {
        $bytes=[IO.File]::ReadAllBytes($FilePath); $id=100L+$script:assets.Count
        $asset=@{id=$id;name=[IO.Path]::GetFileName($FilePath);state='uploaded';digest=('sha256:'+([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant())}
        $script:bytes[$id]=$bytes; $script:assets+=,$asset; return $asset
    }
    $script:releases[0].draft=$false; $script:releases[0].immutable=$true; return $script:releases[0]
}
function New-TestRelease([string] $Tag='v1.0.0-dev.1',[long] $Run=42,[long] $Attempt=1) {
    $path=Join-Path $work ([Guid]::NewGuid().ToString('N')+'.zip')
    $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md')) {
            $entry=$zip.CreateEntry('application/'+$name); $stream=$entry.Open()
            try { $stream.Write([Text.Encoding]::UTF8.GetBytes("synthetic notices, build $Run/$Attempt")) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $t=Get-SsrsReleaseTag $Tag
    return @{tag=$Tag;channel=$t.channel;sourceCommit=('1'*40);tagObjectSha=('1'*40);sourceEligibility='pull-request-7';archivePath=$path;
        run=@{id=$Run;run_attempt=$Attempt;event='workflow_dispatch'};
        provenance=@{releaseId="ssrs-web-$Run-$Attempt";artifactId=($Run*100+$Attempt);innerReleaseSha256=(Get-FileHash $path).Hash.ToLowerInvariant();artifactTransportSha256=('4'*64)}}
}
$previous=@{}
foreach ($name in @('GITHUB_WORKFLOW_SHA','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT')) { $previous[$name]=[Environment]::GetEnvironmentVariable($name) }
try {
    $env:GITHUB_WORKFLOW_SHA='7'*40; $env:GITHUB_RUN_ID='99'; $env:GITHUB_RUN_ATTEMPT='1'
    Test-Case 'DEV beta RC are prereleases and stable is normal; checksums and notices precede publication' {
        foreach ($tag in @('v1.0.0-dev.1','v1.0.0-beta.1','v1.0.0-rc.1','v1.0.0')) {
            Reset-Service; $r=New-TestRelease $tag
            $out=Publish-SsrsCanonicalRelease $r (Join-Path $work $tag)
            Assert-True ($out.outcome -ceq 'Published' -and $script:releases[0].prerelease -eq ($tag -cne 'v1.0.0'))
            Assert-True ($script:writes[0].body.draft -eq $true -and $script:writes[-1].method -ceq 'PATCH' -and $script:assets.Count -eq 6)
            $sums=@($script:assets | Where-Object name -CEQ SHA256SUMS)[0]
            $text=[Text.Encoding]::UTF8.GetString($script:bytes[$sums.id])
            Assert-True ($text.Contains($r.provenance.innerReleaseSha256+'  release.zip') -and $text.Contains('  release-provenance.json') -and $text.Contains('  LICENSE'))
        }
    }
    Test-Case 'Fresh run and attempt preserve every existing asset while retaining distinct current package identities' {
        Reset-Service; $r=New-TestRelease
        $null=Publish-SsrsCanonicalRelease $r (Join-Path $work 'original')
        $original=($script:assets | ConvertTo-Json -Compress); $script:writes=@()
        foreach ($build in @(@(42,2),@(43,1))) {
            $fresh=New-TestRelease $r.tag $build[0] $build[1]
            $out=Publish-SsrsCanonicalRelease $fresh (Join-Path $work ([Guid]::NewGuid().ToString('N')))
            Assert-True ($out.outcome -ceq 'Preserved' -and $out.archivedPackageSha256 -ceq $r.provenance.innerReleaseSha256 -and $out.currentPackageSha256 -cne $out.archivedPackageSha256)
            Assert-True ($fresh.provenance.releaseId -cne $r.provenance.releaseId -and $fresh.provenance.artifactId -ne $r.provenance.artifactId)
        }
        Assert-True ($script:writes.Count -eq 0 -and ($script:assets | ConvertTo-Json -Compress) -ceq $original)
        $script:savedRelease=Copy-Record $script:releases; $script:savedAssets=Copy-Record $script:assets; $script:savedBytes=$script:bytes.Clone()
    }
    Test-Case 'Duplicate create race accepts a complete canonical winner without upload overwrite or patch' {
        $script:releases=@(); $script:race=$true; $script:writes=@()
        $out=Publish-SsrsCanonicalRelease (New-TestRelease) (Join-Path $work 'race')
        Assert-True ($out.outcome -ceq 'Preserved' -and $script:writes.Count -eq 1)
        $script:race=$false
    }
    Test-Case 'Draft incomplete archives fail closed and no existing assets are modified' {
        $script:releases[0].draft=$true; $script:writes=@()
        Assert-Fails { Publish-SsrsCanonicalRelease (New-TestRelease) (Join-Path $work 'draft') } 'ArchiveIdentityMismatch'
        Assert-True ($script:writes.Count -eq 0); $script:releases[0].draft=$false
        $saved=$script:assets; $script:assets=@($script:assets | Where-Object name -CNE LICENSE)
        Assert-Fails { Publish-SsrsCanonicalRelease (New-TestRelease) (Join-Path $work 'missing') } 'ArchiveAssetMismatch'
        $script:assets=$saved
    }
    Test-Case 'Moved tags wrong source wrong prerelease author and altered archive metadata are rejected' {
        $script:tagObject='2'*40
        Assert-Fails { Publish-SsrsCanonicalRelease (New-TestRelease) (Join-Path $work 'moved') } 'TagMoved'; $script:tagObject='1'*40
        $tag=Get-SsrsReleaseTag 'v1.0.0-dev.1'; $ref=@{sourceCommit=('1'*40);objectSha=('1'*40)}
        $script:releases[0].target_commitish='master'
        Assert-True ($null -ne (Get-SsrsCanonicalArchive $tag $ref))
        Assert-Fails { Get-SsrsCanonicalArchive $tag @{sourceCommit=('2'*40);objectSha=('1'*40)} } 'ArchiveMetadataMismatch'
        foreach ($key in @('prerelease','author')) {
            $old=$script:releases[0][$key]; $script:releases[0][$key]=$(if ($key -ceq 'author') {@{id=1;login='wrong'}} else {'wrong'})
            Assert-Fails { Get-SsrsCanonicalArchive $tag $ref } 'ArchiveIdentityMismatch'; $script:releases[0][$key]=$old
        }
        $asset=@($script:assets | Where-Object name -CEQ 'release-metadata.json')[0]; $old=$script:bytes[$asset.id]
        $script:bytes[$asset.id]=[Text.Encoding]::UTF8.GetBytes('{}')
        Assert-Fails { Get-SsrsCanonicalArchive $tag $ref } 'ArchiveMetadataMismatch'; $script:bytes[$asset.id]=$old
    }
    Test-Case 'Closed historical DEV dispatch qualifies using immutable archive and successful master consumer without old Actions artifacts' {
        $tag=Get-SsrsReleaseTag 'v1.0.0-dev.1'
        Assert-True ((Get-SsrsSourceEligibility $tag ('1'*40) 'workflow_dispatch') -ceq 'historical-release-5')
        Assert-Fails { Get-SsrsSourceEligibility $tag ('1'*40) 'push' } 'EligiblePullRequestRequired'
        $script:releases[0].immutable=$false
        Assert-Fails { Get-SsrsSourceEligibility $tag ('1'*40) 'workflow_dispatch' } 'HistoricalImmutableReleaseRequired'; $script:releases[0].immutable=$true
        $script:consumerSuccessful=$false
        Assert-Fails { Get-SsrsSourceEligibility $tag ('1'*40) 'workflow_dispatch' } 'HistoricalConsumerUntrusted'; $script:consumerSuccessful=$true
        $script:releases=@()
        Assert-Fails { Get-SsrsSourceEligibility $tag ('1'*40) 'workflow_dispatch' } 'HistoricalImmutableReleaseRequired'
    }
    Test-Case 'Real dispatch selection guard accepts a tag and rejects an ordinary branch before checkout' {
        $workflow=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../.github/workflows/release.yml'))
        $guard=[regex]::Match($workflow,'(?m)^          if \(\$env:SELECTED_COMMIT .*Release.ExactWorkflowSourceRequired.*$').Value.Trim()
        Assert-True (-not [string]::IsNullOrEmpty($guard))
        $names=@('SELECTED_COMMIT','GITHUB_SHA','GITHUB_WORKFLOW_SHA','GITHUB_REF_TYPE')
        $saved=@{}; foreach ($name in $names) { $saved[$name]=[Environment]::GetEnvironmentVariable($name) }
        try {
            $env:SELECTED_COMMIT='1'*40; $env:GITHUB_SHA='1'*40; $env:GITHUB_WORKFLOW_SHA='1'*40; $env:GITHUB_REF_TYPE='tag'
            & ([scriptblock]::Create($guard))
            $env:GITHUB_REF_TYPE='branch'; Assert-Fails { & ([scriptblock]::Create($guard)) } 'ExactWorkflowSourceRequired'
            $env:GITHUB_REF_TYPE='tag'; $env:GITHUB_WORKFLOW_SHA='2'*40
            Assert-Fails { & ([scriptblock]::Create($guard)) } 'ExactWorkflowSourceRequired'
        } finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) } }
    }
    Test-Case 'Publication and privileged deployment entry points reject ordinary local and branch-dispatch contexts' {
        Assert-Fails { & "$PSScriptRoot/Publish-TagRelease.ps1" } 'TrustedConsumerRequired'
        Assert-Fails { & "$PSScriptRoot/Build-PortalRelease.ps1" -SourceCommit ('1'*40) } 'TrustedProducerContextRequired'
    }
    Test-Case 'Actual publisher entry point loads its own helpers and publishes offline in a clean process' {
        $fixture=Join-Path $work 'publisher'; [void][IO.Directory]::CreateDirectory($fixture)
        # Keep the entry point and shared validation/publication code unchanged.
        # Only acquisition and GitHub I/O are replaced, inside an isolated copy.
        foreach ($name in @('Publish-TagRelease.ps1','Release.Common.ps1','TagRelease.Common.ps1','ReleaseArchive.Common.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixture
        }
        $r=New-TestRelease
        $assembly=Join-Path $fixture 'publisher.dll'
        Add-Type -OutputAssembly $assembly -TypeDefinition @'
[assembly: System.Reflection.AssemblyMetadata("Clc.ApplicationId", "ssrs-web")]
[assembly: System.Reflection.AssemblyMetadata("Clc.IdentityKind", "Release")]
[assembly: System.Reflection.AssemblyMetadata("Clc.ReleaseId", "ssrs-web-42-1")]
[assembly: System.Reflection.AssemblyMetadata("Clc.SourceCommit", "1111111111111111111111111111111111111111")]
public static class PublisherFixture { }
'@
        $manifest=[Text.Encoding]::UTF8.GetBytes('{"build":{"workflowPath":".github/workflows/release.yml","job":"release","runId":42,"runAttempt":1}}')
        $zip=[IO.Compression.ZipFile]::Open($r.archivePath,[IO.Compression.ZipArchiveMode]::Update)
        try {
            foreach ($item in @(@{name='manifest.json';bytes=$manifest},@{name='application/SsrsPortal.dll';bytes=[IO.File]::ReadAllBytes($assembly)})) {
                $stream=$zip.CreateEntry($item.name).Open()
                try { $stream.Write($item.bytes) } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
        $r.provenance.innerReleaseSha256=(Get-FileHash $r.archivePath).Hash.ToLowerInvariant()
        $r.provenance.manifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifest)).ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $fixture 'release.json'),($r | ConvertTo-Json -Depth 8))
        [IO.File]::WriteAllText((Join-Path $fixture 'event.json'),'{"workflow_run":{"id":42,"run_attempt":1}}')
        foreach ($name in @('Copy-Record','Reset-Service','Invoke-TagGitHub','Invoke-SsrsReleaseWrite')) {
            [IO.File]::AppendAllText((Join-Path $fixture 'TagRelease.Common.ps1'),("`nfunction $name {`n"+(Get-Command $name).Definition+"`n}`n"))
        }
        [IO.File]::AppendAllText((Join-Path $fixture 'TagRelease.Common.ps1'),@'
function Get-SsrsAuthenticatedTagRelease($EventRun,[string] $TrustedCommit,[string] $Directory) {
    if ($EventRun.id -ne 42 -or $EventRun.run_attempt -ne 1 -or $TrustedCommit -cne ('7'*40)) { throw 'Unexpected publisher acquisition' }
    [void][IO.Directory]::CreateDirectory($Directory)
    return Read-TagJson ([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'release.json')))
}
Reset-Service
'@)
        # Mirror the real downloader's child script scope: it loads helpers for
        # itself, which must not satisfy the publisher's dependency on them.
        [IO.File]::WriteAllText((Join-Path $fixture 'Get-ReleaseToolkit.ps1'),@'
param($LockPath,$Destination)
. "$PSScriptRoot/Release.Common.ps1"
[pscustomobject]@{moduleManifest=(Join-Path $PSScriptRoot 'OfflineToolkit.psm1')}
'@)
        [IO.File]::WriteAllText((Join-Path $fixture 'OfflineToolkit.psm1'),@'
function Test-ClcRelease($ArchivePath) {
    $r=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($ArchivePath -cne $r.archivePath) { throw 'Unexpected inspector archive' }
    return @{integrityVerified=$true;authorizedForDeployment=$false;applicationId='ssrs-web';releaseId=$r.provenance.releaseId;
        sourceCommit=$r.sourceCommit;archiveSha256=$r.provenance.innerReleaseSha256;manifestSha256=$r.provenance.manifestSha256}
}
'@)
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=Join-Path $PSHOME $(if ($IsWindows) {'pwsh.exe'} else {'pwsh'})
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $fixture 'Publish-TagRelease.ps1'))) { $start.ArgumentList.Add($argument) }
        $context=@{GITHUB_REPOSITORY='clcdpc/ssrs-web';GITHUB_EVENT_NAME='workflow_run';GITHUB_REF='refs/heads/master';GITHUB_REF_PROTECTED='true';
            GITHUB_WORKFLOW_SHA=('7'*40);GITHUB_SHA=('7'*40);GITHUB_WORKFLOW_REF='clcdpc/ssrs-web/.github/workflows/deploy-release.yml@refs/heads/master';
            GITHUB_EVENT_PATH=(Join-Path $fixture 'event.json');GITHUB_RUN_ID='99';GITHUB_RUN_ATTEMPT='1';RUNNER_TEMP=$fixture;SSRS_ARTIFACT_READ_TOKEN='offline-fixture'}
        foreach ($name in $context.Keys) { $start.Environment[$name]=$context[$name] }
        $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
        try {
            Assert-True ($process.Start())
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw ('Publisher process failed: '+$stderr.GetAwaiter().GetResult()) }
            $out=Read-TagJson ([Text.Encoding]::UTF8.GetBytes($stdout.GetAwaiter().GetResult()))
            Assert-True ($out.outcome -ceq 'Published' -and $out.releaseId -eq 5 -and
                $out.archivedPackageSha256 -ceq $r.provenance.innerReleaseSha256 -and $out.currentPackageSha256 -ceq $out.archivedPackageSha256)
            $publication=@(Get-ChildItem -LiteralPath $fixture -Directory -Filter 'ssrs-release-archive-*')[0].FullName
            $files=@(Get-ChildItem -LiteralPath (Join-Path $publication 'publication') -File)
            Assert-True ($files.Count -eq 6)
        } finally { $process.Dispose() }
    }
    Test-Case 'Provenance records tag identity for both producer events and keeps branch CI fixtures independent of release context' {
        $e=@{recordKind='ssrs-web-build-evidence';repository='clcdpc/ssrs-web';sourceCommit=('1'*40);workflowCommit=('1'*40);workflowPath='.github/workflows/release.yml';
            buildJob='release';runId=42;runAttempt=1;releaseId='ssrs-web-42-1';innerReleaseSha256=('2'*64);manifestSha256=('3'*64);moduleInventorySha256=('4'*64);
            deploymentApproval=$false;hostInstallationApproval=$false;integrityVerified=$true;toolkitBuilder=@{};sdkVersion='10.0.303';dependencyLockSha256=('5'*64)}
        $path=Join-Path $work 'build-evidence.json'; [IO.File]::WriteAllText($path,($e | ConvertTo-Json -Depth 6))
        $names=@('GITHUB_EVENT_NAME','GITHUB_REF_TYPE','GITHUB_REF_NAME','RELEASE_TAG_OBJECT')
        $saved=@{}; foreach ($name in $names) { $saved[$name]=[Environment]::GetEnvironmentVariable($name) }
        try {
            foreach ($event in @('push','workflow_dispatch')) {
                $env:GITHUB_EVENT_NAME=$event; $env:GITHUB_REF_TYPE='tag'; $env:GITHUB_REF_NAME='v1.0.0-dev.1'; $env:RELEASE_TAG_OBJECT='1'*40
                $p=& "$PSScriptRoot/Write-ReleaseProvenance.ps1" -BuildEvidencePath $path -ArtifactId 7 -ArtifactTransportSha256 ('6'*64) -OutputPath (Join-Path $work ($event+'.json'))
                Assert-True ($p.releaseTag -ceq 'v1.0.0-dev.1' -and $p.tagObjectSha -ceq ('1'*40))
                $env:GITHUB_REF_TYPE='branch'; $env:GITHUB_REF_NAME='master'; $env:RELEASE_TAG_OBJECT=''
                $p=& "$PSScriptRoot/Write-ReleaseProvenance.ps1" -BuildEvidencePath $path -ArtifactId 7 -ArtifactTransportSha256 ('6'*64) -OutputPath (Join-Path $work ($event+'-branch.json'))
                Assert-True (@($p.PSObject.Properties.Name) -cnotcontains 'releaseTag')
            }
        } finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) } }
    }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name]) }
    $report=@{classification='synthetic-release-archive-and-historical-eligibility';liveReleasePublished=$false;passed=$results.Count;cases=$results}
    [IO.File]::WriteAllText((Join-Path $work 'results.json'),($report | ConvertTo-Json -Depth 8))
    Write-Output "Release archive: $($results.Count) passed; results $work/results.json"
}
