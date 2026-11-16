#Requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/Invoke-TagDeployment.ps1"
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work=Join-Path $repository ('artifacts/tag-deployment-tests/'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$results=[Collections.Generic.List[object]]::new()
function Assert-True([bool] $Value) { if (-not $Value) { throw ('Tag contract assertion failed: '+@(Get-PSCallStack)[1].ScriptLineNumber) } }
function Assert-Fails([scriptblock] $Body,[string] $Code) {
    $failed=$false
    try { $null=& $Body } catch { $failed=$true; if ($Code -and $_.Exception.Message -notlike "*$Code*") { throw } }
    Assert-True $failed
}
function Test-Case([string] $Name,[scriptblock] $Body) {
    try { & $Body; $results.Add(@{name=$Name;status='Passed'}) } catch { $results.Add(@{name=$Name;status='Failed'}); throw }
}
function Copy-Record($Value) { return ConvertFrom-Json ($Value | ConvertTo-Json -Depth 30) -AsHashtable -Depth 30 }
function Get-Fixture {
    $sha='1'*40; $repo=@{id=123L;full_name='clcdpc/ssrs-web';default_branch='master'}
    $run=@{id=42L;run_attempt=2L;head_sha=$sha;head_branch='v1.0.0-dev.1';event='push';status='completed';conclusion='success';workflow_id=8L;
        path='.github/workflows/release.yml';repository=(Copy-Record $repo);head_repository=(Copy-Record $repo)}
    $job=@{id=84L;name='release';run_id=42L;run_attempt=2L;head_sha=$sha;status='completed';conclusion='success';labels=@('windows-2022')}
    $artifact=@{id=88L;name='ssrs-web-release-42-2';expired=$false;digest=('sha256:'+('4'*64));workflow_run=@{id=42L;repository_id=123L;head_repository_id=123L;head_sha=$sha;head_branch=$run.head_branch}}
    $p=@{schemaVersion=1L;recordKind='ssrs-web-release-provenance';repository='clcdpc/ssrs-web';sourceCommit=$sha;workflowCommit=$sha;
        workflowPath='.github/workflows/release.yml';runId=42L;runAttempt=2L;buildJob='release';artifactId=88L;artifactTransportSha256=('4'*64);
        releaseId='ssrs-web-42-2';innerReleaseSha256=('2'*64);manifestSha256=('3'*64);integrityVerified=$true;deploymentApproval=$false;hostInstallationApproval=$false;
        releaseTag=$run.head_branch;tagObjectSha=$sha}
    $tree=@{truncated=$false;tree=@(@{path='.github/workflows/release.yml';sha=('8'*40);mode='100644';type='blob'},@{path='scripts/release/Build-PortalRelease.ps1';sha=('9'*40);mode='100644';type='blob'})}
    return @{repo=$repo;run=$run;event=(Copy-Record $run);workflow=@{id=8L;path='.github/workflows/release.yml'};jobs=@($job);artifact=$artifact;p=$p;
        master=@{name='master';protected=$true;commit=@{sha=('7'*40)}};comparison=@{base_commit=@{sha=$sha};status='ahead'};tree=$tree}
}
function Assert-FixtureSource($Fixture,[string] $Tag='v1.0.0-dev.1') {
    Assert-SsrsTagSource (Get-SsrsReleaseTag $Tag) $Fixture.run.head_sha $Fixture.master $Fixture.comparison
}
try {
    Test-Case 'Exact stable, dev, beta and rc grammar routes only dev and stable' {
        foreach ($pair in @(@('v1.0.0','ssrs-web-production'),@('v0.2.10-dev.1','ssrs-web-dev'),@('v1.0.0-beta.1',''),@('v1.0.0-rc.1',''))) {
            Assert-True ((Get-SsrsReleaseTag $pair[0]).targetId -ceq $pair[1])
        }
        foreach ($tag in @('1.0.0','V1.0.0','v01.0.0','v1.00.0','v1.0.01','v1.0.0-dev.0','v1.0.0-dev.01','v1.0.0-DEV.1','v1.0.0-preview.1','v1.0.0-dev','v1.0.0-beta.1+build','v1.0.0.1',"v1.0.0`n",'v1.0.0-rc.-1','v1.0.0-prod.1','v1.0.0-dev.1/escape')) {
            Assert-Fails { Get-SsrsReleaseTag $tag } 'InvalidVersion'
        }
    }
    Test-Case 'DEV accepts master and unmerged history without PR branch permissions or master file equality' {
        $f=Get-Fixture
        foreach ($status in @('ahead','identical','behind','diverged')) {
            $f.comparison.status=$status
            Assert-True ((Assert-FixtureSource $f) -ceq 'tag')
        }
        function Invoke-TagGitHub { throw 'DEV must not query master, PRs, branches, permissions or historical releases' }
        foreach ($tag in @('v1.0.0-dev.1','v1.0.0-beta.1','v1.0.0-rc.1')) {
            Assert-True ((Get-SsrsSourceEligibility (Get-SsrsReleaseTag $tag) ('1'*40)) -ceq 'tag')
        }
    }
    Test-Case 'Stable tags require protected master ancestry and fail closed on unknown comparisons' {
        $f=Get-Fixture
        foreach ($status in @('ahead','identical')) { $f.comparison.status=$status; Assert-True ((Assert-FixtureSource $f 'v1.0.0') -ceq 'master') }
        foreach ($status in @('behind','diverged')) { $f.comparison.status=$status; Assert-Fails { Assert-FixtureSource $f 'v1.0.0' } 'ProtectedMasterRequired' }
        $f.comparison.status='ahead'; $f.master.protected=$false
        Assert-Fails { Assert-FixtureSource $f 'v1.0.0' } 'SourceRelationshipUnknown'; $f.master.protected=$true
        $f.comparison.base_commit.sha='2'*40; Assert-Fails { Assert-FixtureSource $f 'v1.0.0' } 'SourceRelationshipUnknown'
        $f.comparison.base_commit.sha=$f.run.head_sha; $f.comparison.status='unknown'; Assert-Fails { Assert-FixtureSource $f 'v1.0.0' } 'SourceRelationshipUnknown'
        Assert-Fails { Get-SsrsSourceEligibility (Get-SsrsReleaseTag 'v1.0.0-dev.1') 'wrong' } 'SourceRelationshipUnknown'
    }
    Test-Case 'Lightweight and annotated tags bind their exact original object and peeled commit' {
        function Invoke-TagGitHub($Path,$OutputPath) {
            if ($Path -like '*/git/ref/tags/*') { return @{ref='refs/tags/v1.0.0-dev.1';object=@{type='tag';sha=('a'*40)}} }
            if ($Path -like '*/git/tags/*') { return @{sha=('a'*40);object=@{type='commit';sha=('1'*40)}} }
            throw 'Unexpected tag path'
        }
        $r=Resolve-SsrsTag 'v1.0.0-dev.1'
        Assert-True ($r.objectSha -ceq ('a'*40) -and $r.sourceCommit -ceq ('1'*40))
        function Invoke-TagGitHub($Path,$OutputPath) { return @{ref='refs/tags/v1.0.0-dev.2';object=@{type='commit';sha=('1'*40)}} }
        Assert-Fails { Resolve-SsrsTag 'v1.0.0-dev.1' } 'TagMismatch'
        function Invoke-TagGitHub($Path,$OutputPath) { return @{ref='refs/tags/v1.0.0-dev.1';object=@{type='tree';sha=('1'*40)}} }
        Assert-Fails { Resolve-SsrsTag 'v1.0.0-dev.1' } 'TagTargetInvalid'
    }
    Test-Case 'Pagination cannot silently omit PRs jobs or artifacts' {
        function Invoke-TagGitHub($Path,$OutputPath) {
            if ($Path -like '*page=1') { return @{total_count=101;items=@(1..100)} }
            return @{total_count=101;items=@(101)}
        }
        Assert-True ((Get-TagPages '/repos/clcdpc/ssrs-web/items' 'items').Count -eq 101)
        function Invoke-TagGitHub($Path,$OutputPath) { return @{total_count=2;items=@(1)} }
        Assert-Fails { Get-TagPages '/repos/clcdpc/ssrs-web/items' 'items' } 'PaginationInconsistent'
    }
    Test-Case 'Producer identity binds workflow ID path repository source event attempt and successful hosted job' {
        $f=Get-Fixture; $tag=Get-SsrsReleaseTag $f.run.head_branch
        Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag $f.run.head_sha
        foreach ($change in @({param($f) $f.run.workflow_id=99}, {param($f) $f.run.path='.github/workflows/other.yml'},
            {param($f) $f.run.event='pull_request'}, {param($f) $f.run.head_repository.id=99}, {param($f) $f.run.head_sha='2'*40},
            {param($f) $f.run.run_attempt=3}, {param($f) $f.run.head_branch='v1.0.0'}, {param($f) $f.run.conclusion='failure'},
            {param($f) $f.run.status='queued'})) {
            $f=Get-Fixture; & $change $f
            Assert-Fails { Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag ('1'*40) } 'ProducerMismatch'
        }
        foreach ($change in @({param($f) $f.jobs[0].conclusion='failure'}, {param($f) $f.jobs[0].status='in_progress'}, {param($f) $f.jobs[0].head_sha='2'*40}, {param($f) $f.jobs[0].run_id=99}, {param($f) $f.jobs[0].id=0}, {param($f) $f.jobs[0].name='fake'},
            {param($f) $f.jobs[0].labels=@('self-hosted','windows-2022')}, {param($f) $f.jobs[0].run_attempt=1}, {param($f) $f.jobs+=,$f.jobs[0]})) {
            $f=Get-Fixture; & $change $f
            Assert-Fails { Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag ('1'*40) } 'BuildJobMismatch'
        }
    }
    Test-Case 'Completed successful build qualifies during this run; incomplete failed or other jobs never qualify' {
        $f=Get-Fixture; $f.run.status='in_progress'; $f.run.conclusion=$null
        $tag=Get-SsrsReleaseTag $f.run.head_branch
        Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag $f.run.head_sha
        foreach ($status in @('in_progress','queued','waiting')) {
            $f.jobs[0].status=$status
            Assert-Fails { Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag $f.run.head_sha } 'BuildJobMismatch'
        }
        $f.jobs[0].status='completed'; $f.jobs[0].conclusion='failure'
        Assert-Fails { Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag $f.run.head_sha } 'BuildJobMismatch'
        $f.jobs[0].conclusion='success'; $f.jobs[0].name='archive'
        Assert-Fails { Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag $f.run.head_sha } 'BuildJobMismatch'
        Assert-SsrsCompletedHostedJob $f.jobs $f.run 'archive'
        $f.jobs[0].conclusion='failure'; Assert-Fails { Assert-SsrsCompletedHostedJob $f.jobs $f.run 'archive' } 'BuildJobMismatch'
        $f.run.event='workflow_dispatch'; Assert-Fails { Assert-SsrsTagProducer $f.event $f.run $f.workflow $f.repo $f.jobs $tag $f.run.head_sha } 'ProducerMismatch'
    }
    Test-Case 'Tagged workflow and release scripts may change; incomplete or unsupported tree entries fail' {
        $f=Get-Fixture; Assert-SsrsProducerFiles $f.tree
        $f.tree.tree[0].sha='a'*40; $f.tree.tree[1].sha='b'*40
        $f.tree.tree+=@{path='scripts/release/extra.ps1';sha=('3'*40);mode='100644';type='blob'}
        Assert-SsrsProducerFiles $f.tree
        foreach ($change in @({param($t) $t.tree[1].mode='120000'}, {param($t) $t.truncated=$true}, {param($t) $t.tree[0].sha='wrong'})) {
            $other=Copy-Record $f.tree; & $change $other; Assert-Fails { Assert-SsrsProducerFiles $other } 'Producer'
        }
    }
    Test-Case 'Artifact and provenance identity mismatches stop before authorization' {
        $f=Get-Fixture
        Assert-SsrsTagArtifact $f.artifact $f.run $f.repo $f.artifact.name
        Assert-SsrsTagProvenance $f.p $f.run $f.artifact
        foreach ($change in @({param($a) $a.id=0}, {param($a) $a.expired=$true}, {param($a) $a.digest='sha256:wrong'},
            {param($a) $a.name='ssrs-web-release-42-1'}, {param($a) $a.workflow_run.id=99}, {param($a) $a.workflow_run.repository_id=99}, {param($a) $a.workflow_run.head_repository_id=99}, {param($a) $a.workflow_run.head_sha='2'*40}, {param($a) $a.workflow_run.head_branch='master'})) {
            $a=Copy-Record $f.artifact; & $change $a
            Assert-Fails { Assert-SsrsTagArtifact $a $f.run $f.repo $f.artifact.name } 'ArtifactMismatch'
        }
        foreach ($key in @('sourceCommit','workflowCommit','workflowPath','runId','runAttempt','buildJob','artifactId','artifactTransportSha256','releaseId','innerReleaseSha256','manifestSha256')) {
            $p=Copy-Record $f.p; $p[$key]='wrong'; Assert-Fails { Assert-SsrsTagProvenance $p $f.run $f.artifact } 'ProvenanceMismatch'
        }
        $p=Copy-Record $f.p; $p.deploymentApproval=$true; Assert-Fails { Assert-SsrsTagProvenance $p $f.run $f.artifact } 'ProvenanceMismatch'
        Assert-Fails { Read-TagJson ([Text.Encoding]::UTF8.GetBytes('{"id":1,"ID":2}')) } 'DuplicateJson'
    }
    # Exercise the complete authenticated admission pipeline through a fake REST
    # transport, using real ZIP bytes and hashes. No toolkit API is simulated here.
    function New-FixtureZip([string] $Path,[string] $Name,[byte[]] $Bytes) {
        $zip=[IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
        try { $entry=$zip.CreateEntry($Name); $stream=$entry.Open(); try { $stream.Write($Bytes) } finally { $stream.Dispose() } } finally { $zip.Dispose() }
    }
    $f=Get-Fixture; $script:apiFixture=$f; $script:moveTag=$false; $script:resolveCount=0; $script:corruptTransport=$false; $script:tamperProducer=$false
    $manifest=[Text.Encoding]::UTF8.GetBytes('{"synthetic":true}')
    $inner=Join-Path $work 'fixture-release.zip'; New-FixtureZip $inner 'manifest.json' $manifest
    $transport=Join-Path $work 'fixture-transport.zip'; New-FixtureZip $transport 'release.zip' ([IO.File]::ReadAllBytes($inner))
    $f.p.innerReleaseSha256=(Get-FileHash $inner).Hash.ToLowerInvariant(); $f.p.manifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifest)).ToLowerInvariant()
    $f.p.artifactTransportSha256=(Get-FileHash $transport).Hash.ToLowerInvariant(); $f.artifact.digest='sha256:'+$f.p.artifactTransportSha256
    $proof=Join-Path $work 'fixture-proof.zip'; New-FixtureZip $proof 'release-provenance.json' ([Text.Encoding]::UTF8.GetBytes(($f.p | ConvertTo-Json -Depth 10)))
    $script:proofArtifact=Copy-Record $f.artifact; $script:proofArtifact.id=89L; $script:proofArtifact.name='ssrs-web-provenance-42-2'; $script:proofArtifact.digest='sha256:'+(Get-FileHash $proof).Hash.ToLowerInvariant()
    function Invoke-TagGitHub([string] $Path,[string] $OutputPath) {
        $f=$script:apiFixture
        switch -Regex ($Path) {
            '/git/ref/tags/' { $script:resolveCount++; return @{ref='refs/tags/v1.0.0-dev.1';object=@{type='commit';sha=$(if ($script:moveTag -and $script:resolveCount -gt 1) {'2'*40} else {$f.run.head_sha})}} }
            '/actions/runs/42$' { return $f.run }
            '/ssrs-web/$' { return $f.repo }
            '/actions/workflows/release.yml$' { return $f.workflow }
            '/jobs\?' { return @{total_count=$f.jobs.Count;jobs=$f.jobs} }
            '/branches/master$' { return $f.master }
            '/compare/' { return $f.comparison }
            '/git/trees/' {
                if ($script:tamperProducer -and $Path.Contains($f.run.head_sha)) { $t=Copy-Record $f.tree; $t.tree[0].sha='a'*40; return $t }
                return $f.tree
            }
            '/artifacts\?' { return @{total_count=2;artifacts=@($f.artifact,$script:proofArtifact)} }
            '/artifacts/88/zip$' { [IO.File]::WriteAllBytes($OutputPath,([IO.File]::ReadAllBytes($transport))); if ($script:corruptTransport) { [IO.File]::AppendAllText($OutputPath,'corrupt') }; return }
            '/artifacts/89/zip$' { [IO.File]::WriteAllBytes($OutputPath,([IO.File]::ReadAllBytes($proof))); return }
            default { throw ('Unexpected fixture API path: '+$Path) }
        }
    }
    Test-Case 'Complete admission binds real transport ZIP inner ZIP manifest and tag object hashes' {
        $r=Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work 'admitted')
        Assert-True ($r.targetId -ceq 'ssrs-web-dev' -and $r.sourceCommit -ceq $f.run.head_sha -and $r.provenance.artifactId -eq 88)
        $script:authenticated=$r
    }
    Test-Case 'Complete in-progress admission accepts unmerged changed workflow and scripts with no PR lookup' {
        $f.run.status='in_progress'; $f.run.conclusion=$null; $f.comparison.status='diverged'; $script:tamperProducer=$true
        try {
            $r=Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work 'admitted-unmerged')
            Assert-True ($r.sourceEligibility -ceq 'tag' -and $r.provenance.artifactId -eq 88)
            $f.tree.tree[1].sha='b'*40
            $r=Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work 'admitted-advanced-branch')
            Assert-True ($r.sourceEligibility -ceq 'tag')
        } finally { $f.run.status='completed'; $f.run.conclusion='success'; $script:tamperProducer=$false }
    }
    Test-Case 'Workflow revision must equal selected source; failed or incomplete build cannot admit existing artifacts' {
        Assert-Fails { Get-SsrsAuthenticatedTagRelease $f.event ('7'*40) (Join-Path $work 'wrong-workflow') } 'InvalidContext'
        foreach ($state in @('in_progress','failure')) {
            $oldStatus=$f.jobs[0].status; $oldConclusion=$f.jobs[0].conclusion
            if ($state -ceq 'failure') { $f.jobs[0].conclusion=$state } else { $f.jobs[0].status=$state }
            try { Assert-Fails { Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work $state) } 'BuildJobMismatch' }
            finally { $f.jobs[0].status=$oldStatus; $f.jobs[0].conclusion=$oldConclusion }
        }
    }
    Test-Case 'Moved tags and corrupted transport fail complete admission' {
        $script:moveTag=$true; $script:resolveCount=0
        Assert-Fails { Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work 'moved') } 'TagMoved'
        $script:moveTag=$false; $script:corruptTransport=$true
        Assert-Fails { Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work 'corrupt') } 'TransportMismatch'
        $script:corruptTransport=$false
    }
    Test-Case 'A tag pointing at a different source than the authenticated run is refused' {
        $event=Copy-Record $f.event; $event.head_sha='2'*40
        Assert-Fails { Get-SsrsAuthenticatedTagRelease $event ('1'*40) (Join-Path $work 'wrong-source') } 'InvalidContext'
    }
    Test-Case 'Provenance cannot substitute the original tag object inner ZIP or manifest hash' {
        $originalProof=$script:proof; $originalDigest=$script:proofArtifact.digest
        foreach ($field in @('tagObjectSha','innerReleaseSha256','manifestSha256')) {
            $old=$f.p[$field]
            try {
                $f.p[$field]=if ($field -ceq 'tagObjectSha') {'a'*40} else {'a'*64}
                $script:proof=Join-Path $work ([Guid]::NewGuid().ToString('N')+'.proof')
                New-FixtureZip $script:proof 'release-provenance.json' ([Text.Encoding]::UTF8.GetBytes(($f.p | ConvertTo-Json -Depth 10)))
                $script:proofArtifact.digest='sha256:'+(Get-FileHash $script:proof).Hash.ToLowerInvariant()
                $code=switch ($field) { tagObjectSha {'TagMismatch'} innerReleaseSha256 {'InnerReleaseMismatch'} manifestSha256 {'ManifestMismatch'} }
                Assert-Fails { Get-SsrsAuthenticatedTagRelease $f.event ('1'*40) (Join-Path $work $field) } $code
            } finally { $f.p[$field]=$old; $script:proof=$originalProof; $script:proofArtifact.digest=$originalDigest }
        }
    }
    Test-Case 'Transport rejects extra entries and traversal names before extraction' {
        foreach ($name in @('../release.zip','other.zip')) {
            $zip=Join-Path $work ([Guid]::NewGuid().ToString('N')+'.unsafe')
            New-FixtureZip $zip $name ([byte[]]@(1,2,3))
            $a=@{id=88;digest=('sha256:'+(Get-FileHash $zip).Hash.ToLowerInvariant())}
            function Invoke-TagGitHub($Path,$OutputPath) { [IO.File]::WriteAllBytes($OutputPath,[IO.File]::ReadAllBytes($zip)) }
            Assert-Fails { Receive-SsrsTagArtifact $a $work 'release.zip' 100 } 'TransportEntryMismatch'
        }
    }
    Test-Case 'Actual automatic adapter fails closed and never silently reports success' {
        Assert-Fails { Invoke-SsrsToolkitAutomaticDeployment $script:authenticated @{} } 'ToolkitIntegrationUnavailable'
    }
    # Test doubles below replace only protected host observations, the single
    # authorization adapter and existing deployment core. They prove ordering,
    # routing and rejection, not compatibility with an unpublished toolkit.
    $script:events=[Collections.Generic.List[string]]::new(); $script:hostReady=$true; $script:rejectApproval=$false
    function Open-SsrsBootstrap($Id) { $script:events.Add('bootstrap'); return @{root=$work;leases=@()} }
    [void][IO.Directory]::CreateDirectory((Join-Path $work 'tools/host'))
    [IO.File]::WriteAllText((Join-Path $work 'tools/host/HostEnrollment.Common.ps1'),'# synthetic host observations')
    function Get-SsrsEnrollment($Bootstrap,$Id) { $script:events.Add('inspect'); return @{HostState=$(if ($script:hostReady) {'Ready'} else {'TargetNotEnrolled'});ReadyForDeployment=$script:hostReady} }
    function Set-SsrsEnrollmentEvidence($Evidence,$State) { $Evidence.enrollmentState=$State.HostState }
    function Open-SsrsApplicationPreflight($Bootstrap,$Id,$After) { $script:events.Add('preflight'); return $null }
    function Open-SsrsAutomaticContext($Bootstrap,$Id,$Release) { $script:events.Add('verify'); return @{session=$null} }
    function Resolve-SsrsTag($Tag) { return @{sourceCommit=$script:authenticated.sourceCommit;objectSha=$script:authenticated.tagObjectSha} }
    function Get-SsrsSourceEligibility($Tag,$Commit) { return 'master' }
    function Get-SsrsCanonicalArchive($Tag,$Reference) { return @{release=@{id=9};metadata=@{runId=0;runAttempt=0}} }
    function Test-SsrsTagSuperseded($Release) { return $false }
    function Invoke-SsrsToolkitAutomaticDeployment($Release,$Context) {
        $script:events.Add('authorize-'+$Release.targetId)
        if ($script:rejectApproval) { throw 'SyntheticAuthorizationRejected' }
        $script:events.Add('deploy-'+$Release.tag)
        return @{status='NoOp';targetId=$Release.targetId;releaseId=$Release.provenance.releaseId;sourceCommit=$Release.sourceCommit;
            attemptId='11111111-1111-1111-1111-111111111111';currentGeneration=1;observedPoolState='Started';workerCount=1;
            executingToolkit=@{version='mock';sourceCommit=('1'*40)}}
    }
    Test-Case 'Mocked standing authorization accepts successive DEV releases and identical redeployment' {
        $script:events.Clear(); $r=Copy-Record $script:authenticated
        $null=Invoke-SsrsAutomaticDeployment $r (New-SsrsEvidence)
        $r.tag='v1.0.0-dev.2'; $null=Invoke-SsrsAutomaticDeployment $r (New-SsrsEvidence)
        $result=Invoke-SsrsAutomaticDeployment $r (New-SsrsEvidence)
        Assert-True ($result.outcome -ceq 'NoOp' -and @($script:events | Where-Object { $_ -like 'deploy-*' }).Count -eq 3)
        Assert-True (($script:events[0..5] -join ',') -ceq 'bootstrap,inspect,preflight,verify,authorize-ssrs-web-dev,deploy-v1.0.0-dev.1')
    }
    Test-Case 'Mocked authorization rejection and missing enrollment prevent deployment without recovery' {
        $script:events.Clear(); $script:rejectApproval=$true
        Assert-Fails { Invoke-SsrsAutomaticDeployment $script:authenticated (New-SsrsEvidence) } 'SyntheticAuthorizationRejected'
        Assert-True (-not @($script:events | Where-Object { $_ -like 'deploy-*' }).Count)
        $script:rejectApproval=$false; $script:hostReady=$false; $script:events.Clear()
        Assert-Fails { Invoke-SsrsAutomaticDeployment $script:authenticated (New-SsrsEvidence) } 'SeparateEnrollmentRequired'
        Assert-True (($script:events -join ',') -ceq 'bootstrap,inspect'); $script:hostReady=$true
    }
    Test-Case 'Unavailable real adapter blocks the complete surrounding host orchestration' {
        . "$PSScriptRoot/AutomaticDeployment.Adapter.ps1"
        $script:events.Clear()
        Assert-Fails { Invoke-SsrsAutomaticDeployment $script:authenticated (New-SsrsEvidence) } 'ToolkitIntegrationUnavailable'
        Assert-True (($script:events -join ',') -ceq 'bootstrap,inspect,preflight,verify')
    }
    Test-Case 'Missing canonical archive cannot reach the standing deployment adapter' {
        function Get-SsrsCanonicalArchive($Tag,$Reference) { return $null }
        $script:events.Clear()
        Assert-Fails { Invoke-SsrsAutomaticDeployment $script:authenticated (New-SsrsEvidence) } 'ArchiveRequiredBeforeDeployment'
        Assert-True (-not @($script:events | Where-Object { $_ -like 'authorize-*' }).Count)
    }
    Test-Case 'Installed-package rejection and post-queue supersession cannot reach the adapter' {
        function Open-SsrsAutomaticContext($Bootstrap,$Id,$Release) { throw 'Ssrs.ReleaseInspectionMismatch' }
        $script:events.Clear()
        Assert-Fails { Invoke-SsrsAutomaticDeployment $script:authenticated (New-SsrsEvidence) } 'ReleaseInspectionMismatch'
        Assert-True (-not @($script:events | Where-Object { $_ -like 'authorize-*' }).Count)
        function Open-SsrsAutomaticContext($Bootstrap,$Id,$Release) { return @{session=$null} }
        function Test-SsrsTagSuperseded($Release) { return $true }
        $script:events.Clear(); $result=Invoke-SsrsAutomaticDeployment $script:authenticated (New-SsrsEvidence)
        Assert-True ($result.outcome -ceq 'Superseded' -and -not @($script:events | Where-Object { $_ -like 'authorize-*' }).Count)
    }
    Test-Case 'Mocked stable production uses the separate target and artifact-only channels cannot deploy' {
        $r=Copy-Record $script:authenticated; $r.tag='v1.0.0'; $r.channel='stable'; $r.targetId='ssrs-web-production'
        $script:events.Clear(); $result=Invoke-SsrsAutomaticDeployment $r (New-SsrsEvidence)
        Assert-True ($result.outcome -ceq 'NoOp' -and $script:events.Contains('authorize-ssrs-web-production'))
        $r.targetId=''; Assert-Fails { Invoke-SsrsAutomaticDeployment $r (New-SsrsEvidence) } 'DeploymentTargetRequired'
        $script:SsrsTargetId='ssrs-web-dev'; $script:SsrsEnvironmentName='DEV'
    }
    Test-Case 'Toolkit results must bind selected target source release identity and actual successful readiness' {
        $r=$script:authenticated
        $result=Invoke-SsrsToolkitAutomaticDeployment $r @{}
        Assert-SsrsDeploymentResult $result $r.provenance
        foreach ($key in @('targetId','sourceCommit','releaseId','observedPoolState','attemptId')) {
            $bad=Copy-Record $result; $bad[$key]='wrong'; Assert-Fails { Assert-SsrsDeploymentResult $bad $r.provenance } 'DeploymentResultInvalid'
        }
        $bad=Copy-Record $result; $bad.status='Registered'; Assert-Fails { Assert-SsrsDeploymentResult $bad $r.provenance } 'DeploymentResultInvalid'
        $bad=Copy-Record $result; $bad.workerCount=0; Assert-Fails { Assert-SsrsDeploymentResult $bad $r.provenance } 'DeploymentResultInvalid'
    }
    . "$PSScriptRoot/TagRelease.Common.ps1"
    Test-Case 'Superseded versions compare numerically and channels cannot cross deployment targets' {
        Assert-True ((Compare-SsrsReleaseTag (Get-SsrsReleaseTag 'v1.0.0-dev.10') (Get-SsrsReleaseTag 'v1.0.0-dev.2')) -gt 0)
        Assert-True ((Compare-SsrsReleaseTag (Get-SsrsReleaseTag 'v10.0.0') (Get-SsrsReleaseTag 'v2.9.9')) -gt 0)
        function Invoke-TagGitHub($Path,$OutputPath) {
            if ($Path -like '*/matching-refs/*') { return @(@{ref='refs/tags/v1.0.0-dev.2'},@{ref='refs/tags/v2.0.0-beta.1'}) }
            if ($Path -like '*/git/ref/tags/*') { return @{ref='refs/tags/v1.0.0-dev.2';object=@{type='commit';sha=('1'*40)}} }
            throw 'Unexpected supersession path'
        }
        function Get-SsrsSourceEligibility($Tag,$Commit) { return 'master' }
        Assert-True (Test-SsrsTagSuperseded $script:authenticated)
        $r=Copy-Record $script:authenticated; $r.tag='v1.0.0-dev.2'; Assert-True (-not (Test-SsrsTagSuperseded $r))
        $r.tag='v1.0.0'; Assert-True (-not (Test-SsrsTagSuperseded $r))
    }
    Test-Case 'Current tag workflow context binds repository tag revision run and attempt without routing variables' {
        $valid=@{GITHUB_ACTIONS='true';GITHUB_REPOSITORY='clcdpc/ssrs-web';GITHUB_EVENT_NAME='push';GITHUB_REF_TYPE='tag';
            GITHUB_REF='refs/tags/v1.0.0-dev.1';GITHUB_REF_NAME='v1.0.0-dev.1';GITHUB_WORKFLOW_REF='clcdpc/ssrs-web/.github/workflows/release.yml@refs/tags/v1.0.0-dev.1';
            GITHUB_SHA=('1'*40);GITHUB_WORKFLOW_SHA=('1'*40);GITHUB_RUN_ID='42';GITHUB_RUN_ATTEMPT='2'}
        $saved=@{}
        try {
            foreach ($key in $valid.Keys) { $saved[$key]=[Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key,$valid[$key]) }
            $context=Get-SsrsTagWorkflowContext
            Assert-True ($context.id -eq 42 -and $context.run_attempt -eq 2 -and $context.head_sha -ceq ('1'*40))
            foreach ($key in $valid.Keys) {
                [Environment]::SetEnvironmentVariable($key,'wrong')
                Assert-Fails { Get-SsrsTagWorkflowContext } 'ExactTagWorkflowRequired'
                [Environment]::SetEnvironmentVariable($key,$valid[$key])
            }
        } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) } }
    }
    Test-Case 'Inline tagged workflow scripts parse, reject wrong source before I/O and bind downloaded blobs to the selected tree' {
        $workflow=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/release.yml'))
        $bodies=@([regex]::Matches($workflow,'(?m)^        run: \|\r?\n((?:^          .*\r?\n|^\r?\n)+)') | ForEach-Object { [regex]::Replace($_.Groups[1].Value,'(?m)^ {10}','') })
        Assert-True ($bodies.Count -gt 5)
        foreach ($body in $bodies) {
            $errors=$null; $null=[Management.Automation.Language.Parser]::ParseInput($body,[ref]$null,[ref]$errors)
            Assert-True (-not $errors)
        }
        $loader=@($bodies | Where-Object { $_.Contains('Ssrs.ExactTagWorkflowRequired') })
        Assert-True ($loader.Count -eq 1 -and $loader[0].Contains('$hash -cne $expected[0].sha'))
        $guard=$loader[0].Substring(0,$loader[0].IndexOf('$directory='))
        $repo=$env:GITHUB_REPOSITORY
        try {
            $env:GITHUB_REPOSITORY='wrong/repository'
            Assert-Fails { & ([scriptblock]::Create($guard)) } 'ExactTagWorkflowRequired'
        } finally { $env:GITHUB_REPOSITORY=$repo }
    }
    Test-Case 'Tag workflow orders release archive deploy and has no obsolete deployment or dispatch path' {
        $workflow=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/release.yml'))
        # An unmerged tag must not also wake the legacy workflow_run consumer
        # still present on master during the transition.
        Assert-True ($workflow.StartsWith('name: Release and deploy SSRS Web tag'))
        foreach ($name in @('deploy-release.yml','deploy-dev.yml')) { Assert-True (-not [IO.File]::Exists((Join-Path $repository ('.github/workflows/'+$name)))) }
        Assert-True ($workflow.Contains("tags: ['v*']") -and -not $workflow.Contains('workflow_dispatch:') -and -not $workflow.Contains('workflow_run:'))
        $release=$workflow.Substring($workflow.IndexOf('  release:'),$workflow.IndexOf('  archive:')-$workflow.IndexOf('  release:'))
        $archive=$workflow.Substring($workflow.IndexOf('  archive:'),$workflow.IndexOf('  deploy:')-$workflow.IndexOf('  archive:'))
        $deploy=$workflow.Substring($workflow.IndexOf('  deploy:'))
        Assert-True ($archive.Contains('    needs: release') -and $deploy.Contains('    needs: archive'))
        Assert-True (-not $release.Contains('contents: write') -and $archive.Contains('contents: write') -and -not $deploy.Contains('contents: write'))
        Assert-True ($release.Contains('dotnet test') -and $archive.Contains('Publish-TagRelease.ps1'))
        Assert-True ($workflow.Contains('environment: ${{ needs.archive.outputs.target }}') -and $deploy.Contains('group: ${{ needs.archive.outputs.target }}'))
        Assert-True ($deploy.Contains("runs-on: [self-hosted, Windows, X64, '`${{ needs.archive.outputs.target }}']"))
        Assert-True (-not ($deploy -match 'actions/checkout|download-artifact|dotnet |npm |vars\.|refs/heads/master|ref_protected'))
        Assert-True ($deploy.Contains("'?ref='+`$env:GITHUB_WORKFLOW_SHA") -and $deploy.Contains('AutomaticDeployment.Adapter.ps1'))
        Assert-True ($deploy.Contains("needs.archive.outputs.target == 'ssrs-web-dev' || needs.archive.outputs.target == 'ssrs-web-production'"))
        Assert-True (-not ($workflow -match 'vars\.|secrets\.|inputs\.'))
        foreach ($path in Get-ChildItem (Join-Path $repository '.github/workflows') -File) {
            Assert-True (-not ([IO.File]::ReadAllText($path.FullName) -match '(?m)^  workflow_run:'))
        }
    }
} finally {
    $report=@{classification='synthetic-tag-admission-and-mocked-authorization';liveHostQualified=$false;toolkitStandingAuthorizationCompatible=$false;
        discovered=$results.Count;passed=@($results | Where-Object status -EQ Passed).Count;failed=@($results | Where-Object status -EQ Failed).Count;cases=$results}
    [IO.File]::WriteAllText((Join-Path $work 'results.json'),($report | ConvertTo-Json -Depth 8))
    Write-Output "Tag deployment: $($report.passed)/$($report.discovered) passed; results $work/results.json"
}
