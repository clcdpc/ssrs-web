#Requires -Version 7.0
[CmdletBinding()]
param([string] $PublishDirectory,[string] $OrdinaryPublishDirectory,[string] $SyntheticToolkitArchive,[string] $SyntheticToolkitLock,[string] $HistoricalToolkitArchive,[switch] $PublishedToolkit)
. "$PSScriptRoot/Release.Common.ps1"
. "$PSScriptRoot/ExactSourceBytes.Common.ps1"
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work=Join-Path $repositoryRoot ('artifacts/release-packaging-tests/'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$results=[Collections.Generic.List[object]]::new()
if ($PublishedToolkit -and (-not $IsWindows -or -not $PublishDirectory)) { throw 'Published toolkit packaging requires Windows and an actual CLC Release publish directory.' }
function Test-Case {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory,Position=0)][string] $Name,
        [Parameter(Mandatory,Position=1)][scriptblock] $Body
    )
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try { & $Body; $results.Add(@{name=$Name;status='Passed';milliseconds=$clock.ElapsedMilliseconds}) }
    catch { $results.Add(@{name=$Name;status='Failed';milliseconds=$clock.ElapsedMilliseconds}); throw }
}
function Assert-True([bool] $Condition) { if (-not $Condition) { throw 'Packaging assertion failed.' } }
function Assert-Fails([scriptblock] $Body,[string] $Code) {
    $failed=$false
    try { $null=& $Body } catch { $failed=$true; if ($Code -and $_.Exception.Message -notlike "*$Code*" -and $_.FullyQualifiedErrorId -notlike "$Code*") { throw } }
    Assert-True $failed
}
function Invoke-FixtureGit([string] $Root,[string[]] $Arguments) {
    return ,(Invoke-ExactSourceGit -RepositoryRoot $Root -GitArguments $Arguments)
}
function Invoke-FixtureGitText([string] $Root,[string[]] $Arguments) {
    return ConvertFrom-ExactSourceGitText (Invoke-FixtureGit $Root $Arguments)
}
function Invoke-InlineSourceGuard([string] $ScriptPath,[string] $Root,[string] $Commit,[string] $RunnerTemp) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Command pwsh -ErrorAction Stop).Source
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$ScriptPath)) { $start.ArgumentList.Add($argument) }
    $start.Environment['GITHUB_WORKSPACE']=$Root
    $start.Environment['GITHUB_SHA']=$Commit
    $start.Environment['RUNNER_TEMP']=$RunnerTemp
    $start.Environment['GIT_DIR']='poisoned-git-dir'
    $start.Environment['GIT_WORK_TREE']='poisoned-work-tree'
    $start.Environment['GIT_INDEX_FILE']='poisoned-index'
    $start.Environment['GIT_OBJECT_DIRECTORY']='poisoned-objects'
    $start.Environment['GIT_ALTERNATE_OBJECT_DIRECTORIES']='poisoned-alternates'
    $start.Environment['GIT_CONFIG_GLOBAL']='poisoned-global-config'
    $start.Environment['GIT_CONFIG_COUNT']='1'
    $start.Environment['GIT_CONFIG_KEY_0']='core.autocrlf'
    $start.Environment['GIT_CONFIG_VALUE_0']='true'
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    try {
        if (-not $process.Start()) { throw 'SourceGuardFixture.PwshUnavailable' }
        $stdoutTask=$process.StandardOutput.ReadToEndAsync()
        $stderrTask=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{exitCode=$process.ExitCode;stdout=$stdoutTask.GetAwaiter().GetResult();stderr=$stderrTask.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}
function Invoke-SharedSourceGuard([string] $RunnerPath,[string] $Root,[string] $Commit) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Command pwsh -ErrorAction Stop).Source
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$RunnerPath,'-RepositoryRoot',$Root,'-Commit',$Commit)) { $start.ArgumentList.Add($argument) }
    $start.Environment['GIT_DIR']='poisoned-git-dir'
    $start.Environment['GIT_WORK_TREE']='poisoned-work-tree'
    $start.Environment['GIT_INDEX_FILE']='poisoned-index'
    $start.Environment['GIT_OBJECT_DIRECTORY']='poisoned-objects'
    $start.Environment['GIT_ALTERNATE_OBJECT_DIRECTORIES']='poisoned-alternates'
    $start.Environment['GIT_CONFIG_GLOBAL']='poisoned-global-config'
    $start.Environment['GIT_CONFIG_COUNT']='1'
    $start.Environment['GIT_CONFIG_KEY_0']='core.autocrlf'
    $start.Environment['GIT_CONFIG_VALUE_0']='true'
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    try {
        if (-not $process.Start()) { throw 'SourceGuardFixture.PwshUnavailable' }
        $stdoutTask=$process.StandardOutput.ReadToEndAsync()
        $stderrTask=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{exitCode=$process.ExitCode;stdout=$stdoutTask.GetAwaiter().GetResult();stderr=$stderrTask.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}
function Assert-InlineGuardFailure([string] $ScriptPath,[string] $Root,[string] $Commit,[string] $RunnerTemp,[string] $Code) {
    $result=Invoke-InlineSourceGuard $ScriptPath $Root $Commit $RunnerTemp
    Assert-True ($result.exitCode -ne 0 -and ($result.stderr -like "*$Code*" -or $result.stdout -like "*$Code*"))
}
function New-TestZip([string] $Path,[hashtable] $Payload) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($name in $Payload.Keys) {
            $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::NoCompression); $entry.ExternalAttributes=0
            $output=$entry.Open(); try { $output.Write([Text.Encoding]::UTF8.GetBytes($Payload[$name])) } finally { $output.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}
function New-TestPortalArchive([string] $PublishDirectory,[string] $Path,[string] $ReleaseId,[string] $SourceCommit) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        $manifest=[ordered]@{applicationId='ssrs-web';releaseId=$ReleaseId;source=[ordered]@{repository='clcdpc/ssrs-web';commit=$SourceCommit};
            runtime=[ordered]@{framework='net10.0';rid='win-x64';architecture='x64';hostingModel='inprocess';workerLimit=1};
            validation=[ordered]@{entryPoint='SsrsPortal.dll';contractVersion=1};readinessContractVersion=1}
        $entry=$zip.CreateEntry('manifest.json',[IO.Compression.CompressionLevel]::NoCompression)
        $output=$entry.Open(); try { $bytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress)); $output.Write($bytes) } finally { $output.Dispose() }
        foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Sort-Object FullName) {
            $relative=[IO.Path]::GetRelativePath($PublishDirectory,$file.FullName).Replace('\','/')
            $entry=$zip.CreateEntry("application/$relative",[IO.Compression.CompressionLevel]::NoCompression); $entry.ExternalAttributes=0
            $input=[IO.File]::OpenRead($file.FullName); $output=$entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}
try {
    Test-Case 'Browser favicon contains seven valid PNG frames at the declared tab sizes' {
        $icon=[IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'src/SsrsPortal/wwwroot/favicon.ico'))
        Assert-True ($icon.Length -ge 6 -and [BitConverter]::ToUInt16($icon,0) -eq 0 -and [BitConverter]::ToUInt16($icon,2) -eq 1 -and [BitConverter]::ToUInt16($icon,4) -eq 7)
        Assert-True ((@(0..6 | ForEach-Object { $icon[6+($_*16)] }) -join ',') -ceq '16,24,32,48,64,128,0')
        foreach ($index in 0..6) {
            $offset=[BitConverter]::ToUInt32($icon,6+($index*16)+12); $length=[BitConverter]::ToUInt32($icon,6+($index*16)+8)
            Assert-True ($offset -ge 118 -and $offset+$length -le $icon.Length)
            $signature=@(for ($byteIndex=0; $byteIndex -lt 8; $byteIndex++) { $icon[[int]$offset+$byteIndex] })
            Assert-True (($signature -join ',') -ceq '137,80,78,71,13,10,26,10')
        }
        $layout=[IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/SsrsPortal/Pages/Shared/_Layout.cshtml'))
        Assert-True ($layout -match 'rel="icon"[^>]+~/favicon\.ico' -and $layout -match 'DeploymentIdentity\.SourceCommit')
    }
    Test-Case 'Exact-source inline and shared guards prove raw blobs, reject normalized CRLF, index changes, redirected worktrees and locally ignored source' {
        $fixture=Join-Path $work 'exact-source-fixture'
        [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'src/SsrsPortal/wwwroot'))
        [IO.File]::WriteAllText((Join-Path $fixture '.gitattributes'), '* text=auto' + [char]10, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $fixture 'README.md'), 'fixture LF bytes' + [char]10, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $fixture 'git.exe'), 'inert fixture executable; it must never be invoked.')
        [IO.File]::WriteAllText((Join-Path $fixture 'src/SsrsPortal/SsrsPortal.csproj'), '<Project />' + [char]10, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $fixture 'src/SsrsPortal/wwwroot/index.html'), '<html />' + [char]10, [Text.UTF8Encoding]::new($false))
        $null=Invoke-FixtureGit $fixture @('init','-q','-b','master')
        $null=Invoke-FixtureGit $fixture @('config','core.autocrlf','false')
        $null=Invoke-FixtureGit $fixture @('config','core.eol','lf')
        $null=Invoke-FixtureGit $fixture @('config','user.name','Exact Source Fixture')
        $null=Invoke-FixtureGit $fixture @('config','user.email','exact-source@example.invalid')
        $null=Invoke-FixtureGit $fixture @('add','--','.')
        $null=Invoke-FixtureGit $fixture @('commit','-q','-m','baseline')
        $commit=Invoke-FixtureGitText $fixture @('rev-parse','HEAD')
        $yaml=[IO.File]::ReadAllText((Join-Path $repositoryRoot '.github/workflows/release.yml'))
        $pattern='(?ms)^      - name: Verify exact source bytes before any repository script\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<body>.*?)(?=^      - name:)'
        $match=[regex]::Match($yaml,$pattern)
        Assert-True $match.Success
        $inline=[regex]::Replace($match.Groups['body'].Value,'(?m)^ {10}','')
        $inlinePath=Join-Path $work 'extracted-inline-source-guard.ps1'
        [IO.File]::WriteAllText($inlinePath,$inline,[Text.UTF8Encoding]::new($false))
        $runnerTemp=Join-Path $work 'inline-runner-temp'; [void][IO.Directory]::CreateDirectory($runnerTemp)
        $runnerPath=Join-Path $PSScriptRoot 'Test-ExactSourceBytes.ps1'
        $shared=Invoke-SharedSourceGuard $runnerPath $fixture $commit
        Assert-True ($shared.exitCode -eq 0 -and $shared.stdout -match 'Exact source bytes verified for \d+ tracked files')
        $inlineResult=Invoke-InlineSourceGuard $inlinePath $fixture $commit $runnerTemp
        Assert-True ($inlineResult.exitCode -eq 0 -and $inlineResult.stdout -match 'Exact source bytes verified for \d+ tracked files')

        $redirectedWorktree=Join-Path $work 'redirected-worktree'; [void][IO.Directory]::CreateDirectory($redirectedWorktree)
        $null=Invoke-FixtureGit $fixture @('config','core.worktree',$redirectedWorktree)
        [IO.File]::WriteAllText((Join-Path $fixture 'Unreviewed.cs'),'unreviewed source')
        Assert-Fails { & "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $fixture -Commit $commit } 'ExactSource.UntrackedOrIgnoredSourceAmbiguity'
        Assert-InlineGuardFailure $inlinePath $fixture $commit $runnerTemp 'Release.ExactSource.UntrackedOrIgnoredSourceAmbiguity'
        [IO.File]::Delete((Join-Path $fixture 'Unreviewed.cs'))
        $null=Invoke-FixtureGit $fixture @('config','--unset','core.worktree')

        $null=Invoke-FixtureGit $fixture @('config','core.autocrlf','true')
        [IO.File]::Delete((Join-Path $fixture 'README.md'))
        $null=Invoke-FixtureGit $fixture @('checkout','--','README.md')
        Assert-True (-not (Invoke-FixtureGitText $fixture @('status','--porcelain=v1')))
        $readmeBytes=[IO.File]::ReadAllBytes((Join-Path $fixture 'README.md'))
        Assert-True (@($readmeBytes | Where-Object { $_ -eq 13 }).Count -gt 0)
        Assert-Fails { & "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $fixture -Commit $commit } 'ExactSource.WorkingTreeBlobMismatch'
        Assert-InlineGuardFailure $inlinePath $fixture $commit $runnerTemp 'Release.ExactSource.WorkingTreeBlobMismatch'

        $null=Invoke-FixtureGit $fixture @('config','core.autocrlf','false')
        $null=Invoke-FixtureGit $fixture @('config','core.eol','lf')
        [IO.File]::Delete((Join-Path $fixture 'README.md'))
        $null=Invoke-FixtureGit $fixture @('checkout','--','README.md')
        [IO.File]::WriteAllText((Join-Path $fixture 'src/SsrsPortal/SsrsPortal.csproj'), '<Project><Changed /></Project>' + [char]10, [Text.UTF8Encoding]::new($false))
        $null=Invoke-FixtureGit $fixture @('add','--','src/SsrsPortal/SsrsPortal.csproj')
        Assert-Fails { & "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $fixture -Commit $commit } 'ExactSource.IndexMismatch'
        Assert-InlineGuardFailure $inlinePath $fixture $commit $runnerTemp 'Release.ExactSource.IndexMismatch'

        $null=Invoke-FixtureGit $fixture @('reset','--hard','HEAD')
        [IO.File]::WriteAllText((Join-Path $fixture 'README.md'), 'changed working source' + [char]10, [Text.UTF8Encoding]::new($false))
        Assert-Fails { & "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $fixture -Commit $commit } 'ExactSource.WorkingTreeBlobMismatch'
        Assert-InlineGuardFailure $inlinePath $fixture $commit $runnerTemp 'Release.ExactSource.WorkingTreeBlobMismatch'

        $null=Invoke-FixtureGit $fixture @('reset','--hard','HEAD')
        $exclude=Join-Path $fixture '.git/info/exclude'
        [IO.File]::AppendAllText($exclude,'src/SsrsPortal/wwwroot/artifacts/' + [char]10,[Text.UTF8Encoding]::new($false))
        [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'src/SsrsPortal/wwwroot/artifacts'))
        [IO.File]::WriteAllText((Join-Path $fixture 'src/SsrsPortal/wwwroot/artifacts/ignored.js'),'ignored source')
        Assert-Fails { & "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $fixture -Commit $commit } 'ExactSource.UntrackedOrIgnoredSourceAmbiguity'
        Assert-InlineGuardFailure $inlinePath $fixture $commit $runnerTemp 'Release.ExactSource.UntrackedOrIgnoredSourceAmbiguity'
    }
    Test-Case 'Canonical archive names reject traversal, ADS, reserved names and ambiguous casing inputs' {
        foreach ($name in @('../escape','a/../b','C:/x','a\b','a:b','a//b','a/CON.txt','.clc-state','a.','a/./b')) { Assert-Fails { Assert-ReleaseName $name } }
        Assert-ReleaseName 'wwwroot/images/company-logo.png'
    }
    Test-Case 'Strict provenance JSON rejects duplicates including nested arrays' {
        foreach ($json in @('{"source":1,"Source":2}','{"x":[{"a":1,"A":2}]}')) { Assert-Fails { Read-ReleaseJson ([Text.Encoding]::UTF8.GetBytes($json)) } 'Release.DuplicateJson' }
    }
    Test-Case 'Unprovisioned builder pin fails before extraction or executable evaluation' {
        $unprovisioned=Read-ReleaseJson ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'deployment/toolkit.lock.json')))
        $unprovisioned.distributionSha256=$null; $unprovisioned.moduleInventorySha256=$null; $unprovisioned.distributionUri=$null
        $unprovisionedPath=Join-Path $work 'unprovisioned-toolkit-lock.json'
        [IO.File]::WriteAllText($unprovisionedPath,($unprovisioned | ConvertTo-Json -Compress))
        Assert-Fails { & "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath $unprovisionedPath -Destination (Join-Path $work 'absent-builder') } 'Release.Prerequisite.IndependentToolkitDistributionPinRequired'
        Assert-True (-not [IO.Path]::Exists((Join-Path $work 'absent-builder')))
    }
    Test-Case 'CI raw checkout preparation preserves exact bytes and clean Git state after CRLF checkout' {
        $fixture=Join-Path $work 'crlf-checkout-fixture'; [void][IO.Directory]::CreateDirectory($fixture)
        $readme=Join-Path $fixture 'README.md'
        [IO.File]::WriteAllText($readme,"reviewed LF bytes`n",[Text.UTF8Encoding]::new($false))
        $null=Invoke-FixtureGit $fixture @('init','-q','-b','master')
        foreach ($setting in @(@('core.autocrlf','false'),@('core.eol','lf'),@('user.name','Checkout Fixture'),@('user.email','checkout@example.invalid'))) {
            $null=Invoke-FixtureGit $fixture @('config','--local',$setting[0],$setting[1])
        }
        $null=Invoke-FixtureGit $fixture @('add','--','README.md')
        $null=Invoke-FixtureGit $fixture @('commit','-q','-m','reviewed source')
        $commit=Invoke-FixtureGitText $fixture @('rev-parse','HEAD')
        $null=Invoke-FixtureGit $fixture @('config','--local','core.autocrlf','true')
        [IO.File]::Delete($readme)
        $null=Invoke-FixtureGit $fixture @('checkout','--force','--detach',$commit)
        Assert-True ([IO.File]::ReadAllText($readme).Contains("`r`n"))
        Assert-True (-not (Invoke-FixtureGitText $fixture @('status','--porcelain=v1','--untracked-files=normal')))
        # Reproduce the previous CI preparation: raw bytes pass, but subsequent
        # ordinary Git status still sees the checkout under its persistent policy.
        $null=Invoke-FixtureGit $fixture @('-c','core.autocrlf=false','checkout-index','--all','--force')
        $null=Assert-ExactSourceBytes -RepositoryRoot $fixture -Commit $commit
        Assert-True ([bool](Invoke-FixtureGitText $fixture @('status','--porcelain=v1','--untracked-files=normal')))
        $workflow=[IO.File]::ReadAllText((Join-Path $repositoryRoot '.github/workflows/verify.yml'))
        $match=[regex]::Match($workflow,'(?ms)^      - name: Prepare raw pinned toolkit checkout\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<body>.*?)(?=^      - name:)')
        Assert-True $match.Success
        $body=[regex]::Replace($match.Groups['body'].Value,'(?m)^ {10}','')
        $toolkitLock=Read-ReleaseJson ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'deployment/toolkit.lock.json')))
        Assert-True $body.Contains($toolkitLock.sourceCommit)
        $body=$body.Replace('artifacts/toolkit-source','$fixture').Replace($toolkitLock.sourceCommit,'$commit')
        $null=& ([scriptblock]::Create($body))
        Assert-True ((Invoke-FixtureGitText $fixture @('config','--local','--get','core.autocrlf')) -ceq 'false')
        Assert-True ((Invoke-FixtureGitText $fixture @('rev-parse','HEAD')) -ceq $commit)
        Assert-True (-not (Invoke-FixtureGitText $fixture @('status','--porcelain=v1','--untracked-files=normal')))
        $null=Assert-ExactSourceBytes -RepositoryRoot $fixture -Commit $commit
        [IO.File]::AppendAllText($readme,"unreviewed change`n",[Text.UTF8Encoding]::new($false))
        Assert-Fails { Assert-ExactSourceBytes -RepositoryRoot $fixture -Commit $commit } 'ExactSource.WorkingTreeBlobMismatch'
    }
    if ($IsWindows) {
        Test-Case 'Canonical fixture temporary state ignores a runner TEMP on another volume' {
            $conventionalRoot=Join-Path $work 'owned-canonical-fixture'; [void][IO.Directory]::CreateDirectory($conventionalRoot)
            $otherDrive=if ([IO.Path]::GetPathRoot($conventionalRoot) -ieq 'C:\') {'D:\'} else {'C:\'}
            $shortRoot=$otherDrive+'synthetic-runner-temp-not-accessed'
            $source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-DeploymentIntegration.ps1'))
            $selection=[regex]::Match($source,'(?s)# SSRS-BRIDGE-TEMP-BEGIN\r?\n(?<body>.*?)# SSRS-BRIDGE-TEMP-END')
            Assert-True $selection.Success
            $savedTemp=$env:TEMP; $savedTmp=$env:TMP
            try {
                $env:TEMP=$shortRoot; $env:TMP=$shortRoot
                $null=& ([scriptblock]::Create($selection.Groups['body'].Value))
                $temporary=[IO.Path]::GetTempPath().TrimEnd('\')
                Assert-True ($temporary -ceq $conventionalRoot -and [IO.Path]::GetPathRoot($temporary) -ine [IO.Path]::GetPathRoot($shortRoot))
                $ownedSource=Join-Path $temporary 'clc-fixture\Deployment'; [void][IO.Directory]::CreateDirectory($ownedSource)
                $payload=Join-Path $ownedSource 'source.commit'; [IO.File]::WriteAllText($payload,('1'*40))
                $expectedHash=Get-ReleaseHash $payload
                $destination=Join-Path $conventionalRoot 'Deployment'
                [IO.Directory]::Move($ownedSource,$destination)
                Assert-True (-not [IO.Directory]::Exists($ownedSource) -and (Get-ReleaseHash (Join-Path $destination 'source.commit')) -ceq $expectedHash)
                [void][IO.Directory]::CreateDirectory($ownedSource)
                Assert-Fails { [IO.Directory]::Move($ownedSource,$destination) }
                Assert-True ([IO.Directory]::Exists($ownedSource) -and (Get-ReleaseHash (Join-Path $destination 'source.commit')) -ceq $expectedHash)
            } finally { $env:TEMP=$savedTemp; $env:TMP=$savedTmp }
        }
    }
    Test-Case 'Wrong whole-archive pin cannot execute its bundled self-verifier' {
        $sentinel=Join-Path $work 'must-not-exist.txt'; $evil=Join-Path $work 'untrusted.zip'
        New-TestZip $evil @{'tools/Test-ToolkitDistribution.ps1'="[IO.File]::WriteAllText('$sentinel','executed')"}
        $lock=Read-ReleaseJson ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'deployment/toolkit.lock.json')))
        $lock.distributionSha256='0'*64; $lock.moduleInventorySha256='0'*64
        $lockPath=Join-Path $work 'wrong-pin.json'; [IO.File]::WriteAllText($lockPath,($lock | ConvertTo-Json -Compress))
        Assert-Fails { & "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath $lockPath -Destination (Join-Path $work 'untrusted-builder') -ArchivePath $evil } 'Release.ToolkitArchivePinMismatch'
        Assert-True (-not [IO.Path]::Exists($sentinel)); Assert-True (-not [IO.Path]::Exists((Join-Path $work 'untrusted-builder')))
    }
    Test-Case 'Authenticated old formats and unexpected, duplicate, case-colliding or legacy module entries fail before extraction' {
        foreach ($kind in @('old-format','unexpected','duplicate','case-collision','legacy-module')) {
            $archive=Join-Path $work ($kind+'.zip'); $destination=Join-Path $work ($kind+'-builder')
            $stream=[IO.File]::Create($archive); $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
            try {
                $names=@('distribution.json')
                $names+=switch ($kind) {
                    'unexpected' { 'unexpected.txt' }
                    'duplicate' { 'source.commit'; 'source.commit' }
                    'case-collision' { 'source.commit'; 'Source.commit' }
                    'legacy-module' { 'CLC.Deployment/CLC.Deployment.psd1' }
                }
                foreach ($name in $names) {
                    $entry=$zip.CreateEntry($name); $entry.ExternalAttributes=0; $output=$entry.Open()
                    try { $output.Write([Text.Encoding]::UTF8.GetBytes($(if ($kind -eq 'old-format') {'{"formatVersion":2}'} else {'{"formatVersion":3}'}))) } finally { $output.Dispose() }
                }
            } finally { $zip.Dispose(); $stream.Dispose() }
            $lock=Read-ReleaseJson ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'deployment/toolkit.lock.json')))
            $lock.distributionSha256=Get-ReleaseHash $archive
            $lockPath=Join-Path $work ($kind+'.lock.json'); [IO.File]::WriteAllText($lockPath,($lock | ConvertTo-Json -Compress))
            $code=if ($kind -eq 'old-format') {'Release.Prerequisite.RenamedToolkitPublicationAndRepinRequired'} else {'Release.ToolkitArchiveShape'}
            Assert-Fails { & "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath $lockPath -Destination $destination -ArchivePath $archive } $code
            Assert-True (-not [IO.Path]::Exists($destination))
        }
    }
    if ($HistoricalToolkitArchive -or $PublishedToolkit) {
        Test-Case 'Real beta.3 archive authenticates with its historical pin then fails format validation before extraction' {
            # Keep the historical identity independent of the current build pin:
            # otherwise archive authentication would mask the format rejection.
            $historicalLock=@{schemaVersion=1;repository='clcdpc/clc-deployment';sourceCommit='a3a94cd26daa583d392f73e99e56872876a88467';version='0.1.0-beta.3';
                distributionSha256='7afcf9a22442d205840831b803edba4a46c1684cb00063c78592e16e21aac4b3';
                moduleInventorySha256='e285ea0ae2300d91f30f647dc32ed4e08f4b586e3c3275dd03a6267f91786913';
                distributionUri='https://api.github.com/repos/clcdpc/clc-deployment/releases/assets/744767900'}
            $historicalPath=Join-Path $work 'historical.lock.json'; [IO.File]::WriteAllText($historicalPath,($historicalLock | ConvertTo-Json -Compress))
            $destination=Join-Path $work 'historical-toolkit'
            $acquire=@{LockPath=$historicalPath;Destination=$destination}
            if ($HistoricalToolkitArchive) { $acquire.ArchivePath=[IO.Path]::GetFullPath($HistoricalToolkitArchive) }
            Assert-Fails { & "$PSScriptRoot/Get-ReleaseToolkit.ps1" @acquire } 'Release.Prerequisite.RenamedToolkitPublicationAndRepinRequired'
            Assert-True (-not [IO.Path]::Exists($destination))
        }
    }
    if ($PublishedToolkit) {
        $publishedLockPath=Join-Path $repositoryRoot 'deployment/toolkit.lock.json'
        $publishedLock=Read-ReleaseJson ([IO.File]::ReadAllBytes($publishedLockPath))
        $publishedRoot=Join-Path $work 'published-toolkit'
        Test-Case 'Checked-in lock downloads and verifies the published format-3 distribution before canonical import' {
            $builder=& "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath $publishedLockPath -Destination $publishedRoot
            Assert-True ($builder.executionPinVerified -and -not $builder.hostInstallationAuthorized -and -not $builder.applicationDeploymentAuthorized)
            Assert-True ($builder.version -ceq $publishedLock.version -and $builder.sourceCommit -ceq $publishedLock.sourceCommit -and
                $builder.distributionSha256 -ceq $publishedLock.distributionSha256 -and $builder.moduleInventorySha256 -ceq $publishedLock.moduleInventorySha256)
            $checked=& (Join-Path $publishedRoot 'tools/Test-ToolkitDistribution.ps1') -ArchivePath (Join-Path $publishedRoot 'distribution.zip') -ExpectedSha256 $publishedLock.distributionSha256 -ExpectedSourceCommit $publishedLock.sourceCommit -ExpectedVersion $publishedLock.version -InstalledModuleRoot (Join-Path $publishedRoot 'Clc.Deployment') -RequireInfrastructureTools
            Assert-True ($checked.integrityVerified -and $checked.formatVersion -eq 3 -and $checked.moduleName -ceq 'Clc.Deployment' -and $checked.inventorySha256 -ceq $publishedLock.moduleInventorySha256)
            Assert-True ((Get-ReleaseHash (Join-Path $publishedRoot 'distribution.zip')) -ceq $publishedLock.distributionSha256)
            Import-Module $builder.moduleManifest -Force
            $module=Get-Module Clc.Deployment
            $moduleVersion=$module.Version.ToString()
            $prerelease=$module.PrivateData.PSData['Prerelease']
            if ($prerelease) { $moduleVersion+='-'+$prerelease }
            Assert-True ($module.Name -ceq 'Clc.Deployment' -and $moduleVersion -ceq $publishedLock.version -and
                $module.ModuleBase -ceq (Join-Path $publishedRoot 'Clc.Deployment'))
        }
        Test-Case 'The published toolkit derives canonical deployment paths from the application ID for both targets' {
            $module=Get-Module Clc.Deployment
            & $module {
                param($FixtureRoot,$Version)
                function Get-ClcProgramDataRoot { return $FixtureRoot }
                foreach ($targetId in @('ssrs-web-dev','ssrs-web-production')) {
                    $layout=Get-ClcTargetLayout 'ssrs-web' $targetId $Version
                    if ($layout.targetRoot -cne (Join-Path $FixtureRoot 'clc-ssrs-web') -or $layout.targetId -cne $targetId) {
                        throw 'Published toolkit must keep application directory and target identity distinct.'
                    }
                }
            } $work $publishedLock.version
        }
        Test-Case 'Published archive rejects incorrect source, version and module inventory pins before module import' {
            foreach ($field in @('sourceCommit','version','moduleInventorySha256')) {
                $wrong=Read-ReleaseJson ([IO.File]::ReadAllBytes($publishedLockPath))
                $wrong[$field]=switch ($field) { 'sourceCommit' {'0'*40} 'version' {'0.1.0-beta.999'} 'moduleInventorySha256' {'0'*64} }
                $path=Join-Path $work ('wrong-published-'+$field+'.json'); [IO.File]::WriteAllText($path,($wrong | ConvertTo-Json -Compress))
                $code=if ($field -eq 'moduleInventorySha256') {'Release.ToolkitInventoryPinMismatch'} else {'Distribution identity differs.'}
                Assert-Fails { & "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath $path -Destination (Join-Path $work ('wrong-published-'+$field)) -ArchivePath (Join-Path $publishedRoot 'distribution.zip') } $code
            }
        }
    }
    $empty=Join-Path $work 'empty-publish'; [void][IO.Directory]::CreateDirectory($empty)
    function New-NoticePackageFixture([string] $Name,[string] $PackageId,[string] $PackageVersion,[string] $LicenseFileName,[string] $LicenseText) {
        $root=Join-Path $work ('notice-package-'+$Name+'-'+[Guid]::NewGuid().ToString('N'))
        $publish=Join-Path $root 'publish'; $packages=Join-Path $root 'packages'
        $packageDirectory=Join-Path (Join-Path $packages $PackageId.ToLowerInvariant()) $PackageVersion.ToLowerInvariant()
        [void][IO.Directory]::CreateDirectory($publish); [void][IO.Directory]::CreateDirectory($packageDirectory)
        foreach ($name in @('LICENSE','COPYRIGHT.md','BINARY-DISTRIBUTION-NOTICE.md')) { [IO.File]::Copy((Join-Path $repositoryRoot $name),(Join-Path $publish $name)) }
        $packageLibraries=@{}; $packageLibraries["$PackageId/$PackageVersion"]=@{type='package'}
        $dependencies=@{
            runtimeTarget=@{name='.NETCoreApp,Version=v10.0'}
            libraries=$packageLibraries
        }
        [IO.File]::WriteAllText((Join-Path $publish 'SsrsPortal.deps.json'),($dependencies | ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
        $nuspec="<package><metadata><id>$PackageId</id><version>$PackageVersion</version><authors>Microsoft</authors><copyright>Microsoft</copyright><license type=`"file`">$LicenseFileName</license></metadata></package>"
        [IO.File]::WriteAllText((Join-Path $packageDirectory ($PackageId.ToLowerInvariant()+'.nuspec')),$nuspec,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllBytes((Join-Path $packageDirectory $LicenseFileName),[Text.UTF8Encoding]::new($false).GetBytes($LicenseText))
        return [pscustomobject]@{PublishDirectory=$publish;NuGetPackagesDirectory=$packages;PackageDirectory=$packageDirectory}
    }
    function Invoke-NoticePackageFixture([pscustomobject] $Fixture) {
        $null=& "$PSScriptRoot/Export-ThirdPartyNotices.ps1" -PublishDirectory $Fixture.PublishDirectory -NuGetPackagesDirectory $Fixture.NuGetPackagesDirectory
    }
    function Get-NoticeFixtureLicensePath($Fixture) {
        $notice=[IO.File]::ReadAllText((Join-Path $Fixture.PublishDirectory 'THIRD-PARTY-NOTICES.md'))
        $reference=[regex]::Match($notice,'\]\(THIRD-PARTY-LICENSES/([A-Za-z0-9_.-]+)\)')
        Assert-True $reference.Success
        return Join-Path $Fixture.PublishDirectory ('THIRD-PARTY-LICENSES/'+$reference.Groups[1].Value)
    }
    $trackedSniText=[IO.File]::ReadAllText((Join-Path $repositoryRoot 'THIRD-PARTY-LICENSES/Microsoft.Data.SqlClient.SNI.runtime-7.1.0-LICENSE.txt'))
    $oppositeSniText=if ($trackedSniText.Contains("`r`n")) {
        [regex]::Replace($trackedSniText,"\r\n?|\n","`n")
    } else {
        [regex]::Replace($trackedSniText,"\r\n?|\n","`r`n")
    }
    Test-Case 'SNI exporter honors the package-declared uppercase LICENSE.txt path' {
        $fixture=New-NoticePackageFixture 'filename-case' 'Microsoft.Data.SqlClient.SNI.runtime' '7.1.0' 'LICENSE.txt' $trackedSniText
        $packageFiles=@(Get-ChildItem -LiteralPath $fixture.PackageDirectory -File | ForEach-Object Name)
        Assert-True ($packageFiles -ccontains 'LICENSE.txt' -and $packageFiles -cnotcontains 'license.txt')
        Invoke-NoticePackageFixture $fixture
        $exported=Get-NoticeFixtureLicensePath $fixture
        Assert-True ((Get-ReleaseBytesHash ([IO.File]::ReadAllBytes($exported))) -ceq (Get-ReleaseBytesHash ([Text.UTF8Encoding]::new($false).GetBytes($trackedSniText))))
        $repeat=New-NoticePackageFixture 'filename-repeat' 'Microsoft.Data.SqlClient.SNI.runtime' '7.1.0' 'LICENSE.txt' $trackedSniText
        Invoke-NoticePackageFixture $repeat
        Assert-True ([IO.Path]::GetFileName((Get-NoticeFixtureLicensePath $repeat)) -ceq [IO.Path]::GetFileName($exported))
    }
    Test-Case 'SNI exporter normalizes line endings but preserves the exact upstream license bytes' {
        $fixture=New-NoticePackageFixture 'line-endings' 'Microsoft.Data.SqlClient.SNI.runtime' '7.1.0' 'LICENSE.txt' $oppositeSniText
        Invoke-NoticePackageFixture $fixture
        $exported=Get-NoticeFixtureLicensePath $fixture
        $packageLicense=Join-Path $fixture.PackageDirectory 'LICENSE.txt'
        Assert-True ((Get-ReleaseBytesHash ([IO.File]::ReadAllBytes($exported))) -ceq (Get-ReleaseHash $packageLicense))
    }
    Test-Case 'Hangfire license verification normalizes line endings while preserving exact package bytes' {
        $trackedHangfireText=[IO.File]::ReadAllText((Join-Path $repositoryRoot 'THIRD-PARTY-LICENSES/Hangfire-1.8.25-LICENSE.md'))
        if ($trackedHangfireText.Contains("`r`n")) { $packageHangfireText=[regex]::Replace($trackedHangfireText,"\r\n?|\n","`n") }
        else { $packageHangfireText=[regex]::Replace($trackedHangfireText,"\r\n?|\n","`r`n") }
        $fixture=New-NoticePackageFixture 'hangfire-line-endings' 'Hangfire.Core' '1.8.25' 'LICENSE.md' $packageHangfireText
        Invoke-NoticePackageFixture $fixture
        $exported=Join-Path $fixture.PublishDirectory 'THIRD-PARTY-LICENSES/Hangfire.Core-1.8.25-LICENSE.md'
        Assert-True ((Get-ReleaseBytesHash ([IO.File]::ReadAllBytes($exported))) -ceq (Get-ReleaseHash (Join-Path $fixture.PackageDirectory 'LICENSE.md')))
    }
    Test-Case 'SNI exporter rejects a changed license after line-ending normalization' {
        $replacement=if ([string]$trackedSniText[0] -ceq 'X') {'Y'} else {'X'}
        $changedSniText=$replacement+$trackedSniText.Substring(1)
        if ($trackedSniText.Contains("`r`n")) { $changedSniText=[regex]::Replace($changedSniText,"\r\n?|\n","`n") }
        else { $changedSniText=[regex]::Replace($changedSniText,"\r\n?|\n","`r`n") }
        $fixture=New-NoticePackageFixture 'changed-license' 'Microsoft.Data.SqlClient.SNI.runtime' '7.1.0' 'LICENSE.txt' $changedSniText
        Assert-Fails { Invoke-NoticePackageFixture $fixture } 'ThirdParty.SniLicenseTextMismatch'
    }
    $admin=Join-Path $work 'administrative-setup.zip'
    Test-Case 'Separate administrative artifact retains exact SQL, project notices and Hangfire license bytes' {
        $null=& "$PSScriptRoot/New-SubscriptionSetupArtifact.ps1" -OutputPath $admin -SourceCommit ('1'*40)
        $null=& "$PSScriptRoot/Test-SubscriptionPublish.ps1" -PublishDirectory $empty -AdministrationArchive $admin
        $zip=[IO.Compression.ZipFile]::OpenRead($admin)
        try {
            $expectedEntries=@(
                'Database/001-Subscriptions.sql','Database/002-Hangfire.sql','Database/003-SubscriptionPermissions.sql',
                'Database/Initialize-Subscriptions.ps1','Database/Licenses/Hangfire/LICENSE.md',
                'Database/Licenses/Hangfire/COPYING','Database/Licenses/Hangfire/COPYING.LESSER','Database/Licenses/Hangfire/README.md',
                'Database/Licenses/Project/LICENSE','Database/Licenses/Project/COPYRIGHT.md',
                'Operations/Configure-SubscriptionIis.ps1','setup-manifest.json'
            )
            Assert-True ($zip.Entries.Count -eq $expectedEntries.Count)
            foreach ($path in $expectedEntries) { Assert-True ($null -ne $zip.GetEntry($path)) }
            foreach ($path in @('Database/Licenses/Project/LICENSE','Database/Licenses/Project/COPYRIGHT.md')) {
                $entry=$zip.GetEntry($path)
                $source=Join-Path $repositoryRoot ($path.Substring('Database/Licenses/Project/'.Length))
                Assert-True ($null -ne $entry)
                Assert-True ((Get-ReleaseBytesHash (Read-ReleaseEntry $entry)) -ceq (Get-ReleaseHash $source))
            }
        } finally { $zip.Dispose() }
        $again=Join-Path $work 'administrative-setup-repeat.zip'
        $null=& "$PSScriptRoot/New-SubscriptionSetupArtifact.ps1" -OutputPath $again -SourceCommit ('1'*40)
        Assert-True ((Get-ReleaseHash $admin) -ceq (Get-ReleaseHash $again))
    }
    Test-Case 'Administrative tooling leaked into application publish is rejected' {
        [IO.File]::WriteAllText((Join-Path $empty 'migration.sql'),'forbidden')
        Assert-Fails { & "$PSScriptRoot/Test-SubscriptionPublish.ps1" -PublishDirectory $empty -AdministrationArchive $admin } 'Release.AdministrationInApplication'
    }
    Test-Case 'Provenance requires producer and distinct GitHub transport/inner artifact identity' {
        $evidence=@{schemaVersion=1;recordKind='ssrs-web-build-evidence';repository='clcdpc/ssrs-web';sourceCommit=('1'*40);workflowCommit=('1'*40);workflowPath='.github/workflows/release.yml';
            runId=1;runAttempt=1;buildJob='release';innerReleaseSha256=('2'*64);manifestSha256=('3'*64);moduleInventorySha256=('4'*64);deploymentApproval=$false;hostInstallationApproval=$false;integrityVerified=$true;
            releaseId='ssrs-web-1-1';sdkVersion='10.0.303';dependencyLockSha256=('5'*64);toolkitBuilder=@{module='Clc.Deployment';version='0.1.0-dev';sourceCommit=('6'*40);distributionSha256=('7'*64)}}
        $path=Join-Path $work 'synthetic-build-evidence.json'; [IO.File]::WriteAllText($path,($evidence | ConvertTo-Json -Depth 10 -Compress))
        $provenance=& "$PSScriptRoot/Write-ReleaseProvenance.ps1" -BuildEvidencePath $path -ArtifactId 42 -ArtifactTransportSha256 ('sha256:'+('8'*64)) -OutputPath (Join-Path $work 'provenance.json')
        Assert-True ($provenance.artifactId -eq 42 -and $provenance.artifactTransportSha256 -ceq ('8'*64) -and $provenance.innerReleaseSha256 -ceq ('2'*64) -and -not $provenance.deploymentApproval)
        # A same-tag rebuild retains source but must carry this attempt's
        # package/artifact identity rather than reusing the canonical build.
        $evidence.runAttempt=2; $evidence.releaseId='ssrs-web-1-2'; $evidence.innerReleaseSha256='a'*64; $evidence.manifestSha256='b'*64
        [IO.File]::WriteAllText($path,($evidence | ConvertTo-Json -Depth 10 -Compress))
        $rebuilt=& "$PSScriptRoot/Write-ReleaseProvenance.ps1" -BuildEvidencePath $path -ArtifactId 43 -ArtifactTransportSha256 ('c'*64) -OutputPath (Join-Path $work 'rebuilt-provenance.json')
        Assert-True ($rebuilt.sourceCommit -ceq $provenance.sourceCommit -and $rebuilt.runId -eq $provenance.runId -and $rebuilt.runAttempt -eq 2 -and
            $rebuilt.releaseId -cne $provenance.releaseId -and $rebuilt.artifactId -ne $provenance.artifactId -and
            $rebuilt.innerReleaseSha256 -cne $provenance.innerReleaseSha256 -and $rebuilt.artifactTransportSha256 -cne $provenance.artifactTransportSha256)
        $evidence.Remove('workflowCommit'); [IO.File]::WriteAllText($path,($evidence | ConvertTo-Json -Depth 10 -Compress))
        Assert-Fails { & "$PSScriptRoot/Write-ReleaseProvenance.ps1" -BuildEvidencePath $path -ArtifactId 42 -ArtifactTransportSha256 ('8'*64) -OutputPath (Join-Path $work 'incomplete.json') }
    }
    Test-Case 'Production producer refuses caller-supplied local provenance' {
        Assert-Fails { & "$PSScriptRoot/Build-PortalRelease.ps1" -SourceCommit ('1'*40) } 'Release.TrustedProducerContextRequired'
    }
    if ($OrdinaryPublishDirectory) {
        $OrdinaryPublishDirectory=[IO.Path]::GetFullPath($OrdinaryPublishDirectory)
        Test-Case 'Actual ordinary SDK publish retains the 60 second ANCM shutdown grace' {
            Assert-OrdinaryPublishShutdownTimeLimit $OrdinaryPublishDirectory
        }
    }
    if ($PublishDirectory) {
        $PublishDirectory=[IO.Path]::GetFullPath($PublishDirectory)
        $identity=Read-CompiledReleaseIdentity ([IO.File]::ReadAllBytes((Join-Path $PublishDirectory 'SsrsPortal.dll')))
        $releaseId=$identity['Clc.ReleaseId']; $sourceCommit=$identity['Clc.SourceCommit']
        Test-Case 'Actual CLC Release publish removes shutdownTimeLimit and excludes its transform source' {
            Assert-ReleasePublishShutdownTimeLimit $PublishDirectory
            $transformFiles=@(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter 'web.clc-release.config' -Force)
            Assert-True ($transformFiles.Count -eq 0)
        }
        Test-Case 'Published release PE identity and complete file policy pass without Mock/fixture dependency' { Assert-PortalPublish $PublishDirectory $releaseId $sourceCommit }
        Test-Case 'Published bootstrap example is required, byte-exact and the only permitted application JSON' {
            foreach ($change in @('missing','modified')) {
                $copy=Join-Path $work ('publish-example-'+$change); [void][IO.Directory]::CreateDirectory($copy)
                Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $copy -Recurse -Force
                $example=Join-Path $copy 'application.Example.json'
                if ($change -eq 'missing') { Remove-Item -LiteralPath $example }
                else { [IO.File]::WriteAllText($example,'{"AllowedHosts":"changed"}') }
                Assert-Fails { Assert-PortalPublish $copy $releaseId $sourceCommit } 'Release.ConfigurationExampleMismatch'
            }
            $nested=Join-Path $work 'publish-nested-example'; [void][IO.Directory]::CreateDirectory($nested)
            Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $nested -Recurse -Force
            [void][IO.Directory]::CreateDirectory((Join-Path $nested 'Config'))
            [IO.File]::WriteAllText((Join-Path $nested 'Config/application.Example.json'),'{}')
            Assert-Fails { Assert-PortalPublish $nested $releaseId $sourceCommit } 'Release.ForbiddenPublishContent'
        }
        Test-Case 'Representative application archive carries the exact third-party inventory and license texts' {
            $archive=Join-Path $work 'representative-application.zip'
            New-TestPortalArchive $PublishDirectory $archive $releaseId $sourceCommit
            $null=Assert-PortalReleaseArchive $archive $releaseId $sourceCommit
            $zip=[IO.Compression.ZipFile]::OpenRead($archive)
            try {
                if ($IsWindows) {
                    . "$PSScriptRoot/Deployment.Common.ps1"
                    Initialize-SsrsNative
                    $nativeProgramData=[SsrsWorkflow.Native]::ProgramData()
                    $applicationData=Join-Path $nativeProgramData 'clc-ssrs-web'
                    $control=Join-Path $applicationData ('Deployment\control\'+('a'*64))
                    foreach ($entry in $zip.Entries | Where-Object FullName -CLike 'application/*') {
                        $relative=$entry.FullName.Replace('/','\')
                        foreach ($prefix in @($applicationData,(Join-Path $control ('staging\'+('a'*36))),(Join-Path $control ('backups\'+('a'*36))))) {
                            Assert-True ((Join-Path $prefix $relative).Length -le 240)
                        }
                    }
                }
                foreach ($name in @('application/LICENSE','application/COPYRIGHT.md','application/BINARY-DISTRIBUTION-NOTICE.md','application/THIRD-PARTY-NOTICES.md',
                    'application/THIRD-PARTY-LICENSES/Hangfire-1.8.25-COPYING.LESSER')) {
                    Assert-True ($null -ne $zip.GetEntry($name))
                }
                $notice=[Text.Encoding]::UTF8.GetString((Read-ReleaseEntry ($zip.GetEntry('application/THIRD-PARTY-NOTICES.md'))))
                $sniRow=@($notice.Split([char]10) | Where-Object { $_.StartsWith('| [Microsoft.Data.SqlClient.SNI.runtime/7.1.0](') })
                $sniName=[regex]::Match($sniRow[0],'\]\(THIRD-PARTY-LICENSES/([A-Za-z0-9_.-]+)\)').Groups[1].Value
                $packageCache=if ($env:NUGET_PACKAGES) {$env:NUGET_PACKAGES} elseif ($env:USERPROFILE) {Join-Path $env:USERPROFILE '.nuget/packages'} else {Join-Path $env:HOME '.nuget/packages'}
                Assert-True ((Get-ReleaseBytesHash (Read-ReleaseEntry ($zip.GetEntry('application/THIRD-PARTY-LICENSES/'+$sniName)))) -ceq
                    (Get-ReleaseHash (Join-Path $packageCache 'microsoft.data.sqlclient.sni.runtime/7.1.0/LICENSE.txt')))
                foreach ($entry in $zip.Entries | Where-Object FullName -CLike 'application/THIRD-PARTY-LICENSES/*') {
                    Assert-True ([IO.Path]::GetFileName($entry.FullName).Length -le 39)
                    $stagePath='C:\ProgramData\clc-ssrs-web\Deployment\control\'+('a'*64)+'\staging\'+('a'*36)+'\'+$entry.FullName.Replace('/','\')
                    Assert-True ($stagePath.Length -le 240)
                }
                foreach ($row in $notice.Split([char]10) | Where-Object { $_.StartsWith('| [') }) {
                    $licenseName=[regex]::Match($row,'\]\(THIRD-PARTY-LICENSES/([A-Za-z0-9_.-]+)\)').Groups[1].Value
                    $digest=[regex]::Match($row,'\| ([a-f0-9]{64}) \|$').Groups[1].Value
                    Assert-True ($digest -ceq (Get-ReleaseBytesHash (Read-ReleaseEntry ($zip.GetEntry('application/THIRD-PARTY-LICENSES/'+$licenseName)))))
                }
            } finally { $zip.Dispose() }
        }
        Test-Case 'Publish without its third-party notice file or license directory is rejected for the notice finding' {
            foreach ($name in @('THIRD-PARTY-NOTICES.md','THIRD-PARTY-LICENSES')) {
                $copy=Join-Path $work ('publish-missing-notices-'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($copy)
                Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $copy -Recurse -Force
                $missing=Join-Path $copy $name
                if (-not [IO.Path]::Exists($missing)) { throw "Packaging fixture missing notice output: $name" }
                Remove-Item -LiteralPath $missing -Force -Recurse
                Assert-Fails { Assert-PortalPublish $copy $releaseId $sourceCommit } 'Release.ThirdPartyNoticeMissing'
            }
        }
        Test-Case 'Compiled identity cannot be substituted by source or release metadata' {
            Assert-Fails { Assert-CompiledReleaseIdentity $identity 'incorrect-release' $sourceCommit } 'Release.CompiledIdentityMismatch'
            Assert-Fails { Assert-CompiledReleaseIdentity $identity $releaseId ('0'*40) } 'Release.CompiledIdentityMismatch'
            $otherTag=if ($identity['Clc.TargetId'] -ceq 'ssrs-web-dev') {'v1.0.0'} else {'v1.0.0-dev.1'}
            Assert-Fails { Assert-CompiledReleaseIdentity $identity $releaseId $sourceCommit $otherTag } 'Release.CompiledTargetMismatch'
            foreach ($change in @(@('Clc.TargetId','ssrs-web-production'),@('Clc.ReleaseTag','v1.0.0'))) {
                $wrong=[Collections.Generic.Dictionary[string,string]]::new($identity,[StringComparer]::Ordinal)
                $wrong[$change[0]]=$change[1]
                if ($wrong[$change[0]] -cne $identity[$change[0]]) { Assert-Fails { Assert-CompiledReleaseIdentity $wrong $releaseId $sourceCommit } 'Release.CompiledTargetMismatch' }
            }
        }
        Test-Case 'Configuration, key material, scripts, SQL and fixture DLL leakage fail before toolkit evaluation' {
            foreach ($name in @('application.json','appsettings.Production.json','deployment.json','packages.release.lock.json','secret.key','key-11111111-1111-1111-1111-111111111111111.xml','.env','migration.sql','deploy.ps1','SsrsPortal.Mock.dll')) {
                $copy=Join-Path $work ('invalid-'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($copy)
                [IO.File]::Copy((Join-Path $PublishDirectory 'SsrsPortal.dll'),(Join-Path $copy 'SsrsPortal.dll'))
                [IO.File]::WriteAllText((Join-Path $copy $name),'forbidden')
                Assert-Fails { Assert-PortalPublish $copy $releaseId $sourceCommit } 'Release.ForbiddenPublishContent'
            }
        }
        if ($PublishedToolkit) {
            Test-Case 'Published builder packages actual Release output with exact source, target and producer bindings' {
                $output=Join-Path $work 'published-package'; [void][IO.Directory]::CreateDirectory($output)
                $dependencyHash=Get-ReleaseDependencyLockHash $repositoryRoot
                $evidence=& "$PSScriptRoot/New-PortalRelease.ps1" -PublishRoot $PublishDirectory -OutputDirectory $output -ToolkitLockPath $publishedLockPath -ToolkitDirectory (Join-Path $work 'published-package-toolkit') -ToolkitArchivePath (Join-Path $publishedRoot 'distribution.zip') -ReleaseId $releaseId -SourceCommit $sourceCommit -ReleaseTag $identity['Clc.ReleaseTag'] -RunId 42 -RunAttempt 2 -DependencyLockSha256 $dependencyHash -WorkflowCommit ('2'*40)
                Assert-True ($evidence.integrityVerified -and -not $evidence.deploymentApproval -and -not $evidence.hostInstallationApproval -and
                    $evidence.toolkitBuilder.module -ceq 'Clc.Deployment' -and $evidence.toolkitBuilder.version -ceq $publishedLock.version -and
                    $evidence.toolkitBuilder.sourceCommit -ceq $publishedLock.sourceCommit -and $evidence.toolkitBuilder.distributionSha256 -ceq $publishedLock.distributionSha256 -and
                    $evidence.moduleInventorySha256 -ceq $publishedLock.moduleInventorySha256 -and $evidence.sourceCommit -ceq $sourceCommit -and
                    $evidence.releaseId -ceq $releaseId -and $evidence.runId -eq 42 -and $evidence.runAttempt -eq 2 -and $evidence.workflowCommit -ceq ('2'*40))
                $archive=Join-Path $output 'release.zip'; $verified=Test-ClcRelease -ArchivePath $archive
                Assert-True ($verified.integrityVerified -and -not $verified.authorizedForDeployment -and $verified.archiveSha256 -ceq $evidence.innerReleaseSha256 -and
                    $verified.manifestSha256 -ceq $evidence.manifestSha256 -and $verified.inventorySha256 -ceq $evidence.inventorySha256)
                $null=Assert-PortalReleaseArchive $archive $releaseId $sourceCommit $identity['Clc.ReleaseTag']
                $zip=[IO.Compression.ZipFile]::OpenRead($archive)
                try {
                    $manifest=Read-ReleaseJson (Read-ReleaseEntry ($zip.GetEntry('manifest.json')))
                    Assert-True ($manifest.builder.module -ceq 'Clc.Deployment' -and $manifest.builder.version -ceq $publishedLock.version -and
                        $manifest.builder.sourceCommit -ceq $publishedLock.sourceCommit -and $manifest.builder.distributionSha256 -ceq $publishedLock.distributionSha256 -and
                        $manifest.source.commit -ceq $sourceCommit -and $manifest.releaseId -ceq $releaseId -and $manifest.build.runId -eq 42 -and
                        $manifest.build.runAttempt -eq 2 -and $manifest.build.job -ceq 'release' -and $manifest.build.workflowPath -ceq '.github/workflows/release.yml' -and
                        $manifest.build.dependencyLockSha256 -ceq $dependencyHash)
                } finally { $zip.Dispose() }
            }
        }
        if ($SyntheticToolkitArchive -and $SyntheticToolkitLock) {
            if (-not $IsWindows) { throw 'Native toolkit packaging qualification requires Windows.' }
            $package=Join-Path $work 'package'; [void][IO.Directory]::CreateDirectory($package)
            Test-Case 'Actual independently test-pinned merged toolkit builds and verifies the complete win-x64 release' {
                $evidence=& "$PSScriptRoot/New-PortalRelease.ps1" -PublishRoot $PublishDirectory -OutputDirectory $package -ToolkitLockPath ([IO.Path]::GetFullPath($SyntheticToolkitLock)) -ToolkitDirectory (Join-Path $work 'toolkit') -ToolkitArchivePath ([IO.Path]::GetFullPath($SyntheticToolkitArchive)) -ReleaseId $releaseId -SourceCommit $sourceCommit -ReleaseTag $identity['Clc.ReleaseTag'] -RunId 1 -RunAttempt 1 -DependencyLockSha256 (Get-ReleaseDependencyLockHash $repositoryRoot) -WorkflowCommit ('1'*40)
                Assert-True ($evidence.integrityVerified -and -not $evidence.deploymentApproval)
            }
            Import-Module (Join-Path $work 'toolkit/Clc.Deployment/Clc.Deployment.psd1') -Force
            $archive=Join-Path $package 'release.zip'; $metadata=(Get-ChildItem -LiteralPath $work -Filter 'metadata-*.json')[0].FullName
            Test-Case 'Actual toolkit packaging repeats exact bytes from identical published files and metadata' {
                $again=New-ClcRelease -PublishRoot $PublishDirectory -MetadataPath $metadata -OutputPath (Join-Path $package 'repeat.zip')
                Assert-True ($again.archiveSha256 -ceq (Get-ReleaseHash $archive))
            }
            Test-Case 'Actual toolkit rejects wrong RID and hosting model' {
                $original=[IO.File]::ReadAllText($metadata)
                foreach ($change in @(@('win-x64','linux-x64'),@('inprocess','outofprocess'))) {
                    $bad=Join-Path $work ('metadata-invalid-'+[Guid]::NewGuid().ToString('N')+'.json')
                    [IO.File]::WriteAllText($bad,$original.Replace($change[0],$change[1]))
                    Assert-Fails { New-ClcRelease -PublishRoot $PublishDirectory -MetadataPath $bad -OutputPath (Join-Path $package ('bad-'+[Guid]::NewGuid().ToString('N')+'.zip')) }
                }
            }
            Test-Case 'Application independently detects compiled-source disagreement in an otherwise toolkit-valid package' {
                $bad=Join-Path $work 'metadata-wrong-source.json'; $text=[IO.File]::ReadAllText($metadata)
                [IO.File]::WriteAllText($bad,$text.Replace($sourceCommit,('9'*40)))
                $wrong=Join-Path $package 'wrong-source.zip'
                $null=New-ClcRelease -PublishRoot $PublishDirectory -MetadataPath $bad -OutputPath $wrong
                Assert-True ((Test-ClcRelease -ArchivePath $wrong).integrityVerified)
                Assert-Fails { Assert-PortalReleaseArchive $wrong $releaseId ('9'*40) } 'Release.CompiledIdentityMismatch'
            }
            Test-Case 'Independently pinned module inventory mismatch fails before module import' {
                $wrong=Read-ReleaseJson ([IO.File]::ReadAllBytes(([IO.Path]::GetFullPath($SyntheticToolkitLock))))
                $wrong.moduleInventorySha256='0'*64
                $path=Join-Path $work 'wrong-module-pin.json'; [IO.File]::WriteAllText($path,($wrong | ConvertTo-Json -Compress))
                Assert-Fails { & "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath $path -Destination (Join-Path $work 'wrong-module') -ArchivePath ([IO.Path]::GetFullPath($SyntheticToolkitArchive)) } 'Release.ToolkitInventoryPinMismatch'
            }
            Test-Case 'Actual toolkit rejects unsafe ZIP names and corrupted artifact bytes' {
                $badZip=Join-Path $work 'unsafe.zip'; New-TestZip $badZip @{'../escape'='unsafe'}
                Assert-Fails { Test-ClcRelease -ArchivePath $badZip }
                $corrupt=Join-Path $work 'corrupt.zip'; $bytes=[IO.File]::ReadAllBytes($archive); $bytes[[int]($bytes.Length/2)]=$bytes[[int]($bytes.Length/2)] -bxor 1; [IO.File]::WriteAllBytes($corrupt,$bytes)
                Assert-Fails { Test-ClcRelease -ArchivePath $corrupt }
            }
            Test-Case 'Application rejects invalid IIS configuration and toolkit rejects malformed XML' {
                $badPublish=Join-Path $work 'bad-web'; [void][IO.Directory]::CreateDirectory($badPublish)
                foreach ($name in @('SsrsPortal.dll','SsrsPortal.runtimeconfig.json')) { [IO.File]::Copy((Join-Path $PublishDirectory $name),(Join-Path $badPublish $name)) }
                [IO.File]::WriteAllText((Join-Path $badPublish 'web.config'),'<configuration><evil /></configuration>')
                Assert-Fails { Assert-ReleasePublishShutdownTimeLimit $badPublish } 'Release.WebConfigInvalid'
                [IO.File]::WriteAllText((Join-Path $badPublish 'web.config'),'<configuration>')
                Assert-Fails { New-ClcRelease -PublishRoot $badPublish -MetadataPath $metadata -OutputPath (Join-Path $package 'bad-web.zip') }
            }
        }
    }
} finally {
    $report=[ordered]@{classification='synthetic-qualification';deploymentAuthorized=$false;discovered=$results.Count;passed=@($results | Where-Object status -EQ Passed).Count;failed=@($results | Where-Object status -EQ Failed).Count;nativeToolkitRequested=[bool]($SyntheticToolkitArchive -or $PublishedToolkit);publishedToolkitRequested=[bool]$PublishedToolkit;cases=$results}
    [IO.File]::WriteAllText((Join-Path $work 'results.json'),($report | ConvertTo-Json -Depth 12))
    Write-Output "Release packaging: $($report.passed)/$($report.discovered) passed; results $work/results.json"
}
