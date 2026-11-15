#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Stop-TagRelease([string] $Code) { throw [IO.InvalidDataException]::new('Release.Tag.'+$Code) }
function Get-SsrsReleaseTag([string] $Tag) {
    if ($Tag.Length -gt 128 -or $Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(dev|beta|rc)\.([1-9][0-9]*))?\z') { Stop-TagRelease 'InvalidVersion' }
    $channel=if ($Matches[4]) { $Matches[4] } else { 'stable' }
    foreach ($value in @($Matches[1],$Matches[2],$Matches[3])) { if ([Numerics.BigInteger]::Parse($value) -gt 9999) { Stop-TagRelease 'InvalidVersion' } }
    if ($Matches[5] -and [Numerics.BigInteger]::Parse($Matches[5]) -gt 9998) { Stop-TagRelease 'InvalidVersion' }
    return @{tag=$Tag;channel=$channel;major=$Matches[1];minor=$Matches[2];patch=$Matches[3];sequence=$Matches[5];
        targetId=$(switch ($channel) { dev {'ssrs-web-dev'} stable {'ssrs-web-production'} default {''} })}
}
function Compare-SsrsReleaseTag($Left,$Right) {
    foreach ($part in @('major','minor','patch','sequence')) {
        $a=if ($Left[$part]) { [Numerics.BigInteger]::Parse($Left[$part]) } else { [Numerics.BigInteger]::Zero }
        $b=if ($Right[$part]) { [Numerics.BigInteger]::Parse($Right[$part]) } else { [Numerics.BigInteger]::Zero }
        if ($a -ne $b) { return $a.CompareTo($b) }
    }
    return 0
}
function Invoke-TagGitHub([string] $Path,[string] $OutputPath) {
    if ($Path -cnotmatch '^/repos/clcdpc/ssrs-web/[A-Za-z0-9_./%?=&-]+$') { Stop-TagRelease 'ApiPathRejected' }
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseCookies=$false
    $client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromMinutes(5)
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,('https://api.github.com'+$Path))
    $response=$null; $stream=$null; $destination=$null
    try {
        $request.Headers.UserAgent.ParseAdd('ssrs-web-tag-admission/1')
        $request.Headers.Accept.ParseAdd($(if ($OutputPath -and $Path -cmatch '/releases/assets/[0-9]+$') {'application/octet-stream'} else {'application/vnd.github+json'}))
        $request.Headers.Add('X-GitHub-Api-Version','2022-11-28')
        $request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:SSRS_ARTIFACT_READ_TOKEN)
        $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        if ($OutputPath -and [int]$response.StatusCode -in @(302,303,307,308)) {
            $location=$response.Headers.Location
            if (-not $location.IsAbsoluteUri -or $location.Scheme -cne 'https' -or $location.Port -ne 443 -or $location.UserInfo -or $location.Fragment -or
                ($location.Host -cnotmatch '^[a-z0-9-]+\.blob\.core\.windows\.net$' -and $location.Host -cnotmatch '(^|\.)actions\.githubusercontent\.com$' -and $location.Host -cne 'release-assets.githubusercontent.com')) { Stop-TagRelease 'RedirectRejected' }
            $response.Dispose(); $request.Dispose(); $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$location)
            # A fresh request has no API credentials. Redirects remain disabled.
            $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        }
        $maximum=if ($OutputPath) {1073741824L} else {16777216L}
        if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -gt $maximum) { Stop-TagRelease 'ApiUnavailable' }
        $stream=$response.Content.ReadAsStream()
        $destination=if ($OutputPath) { [IO.FileStream]::new($OutputPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None) } else { [IO.MemoryStream]::new() }
        $buffer=[byte[]]::new(65536); $total=0L
        while (($read=$stream.Read($buffer,0,$buffer.Length)) -gt 0) { $total+=$read; if ($total -gt $maximum) { Stop-TagRelease 'InputBound' }; $destination.Write($buffer,0,$read) }
        if ($OutputPath) { $destination.Flush($true); return }
        return Read-TagJson $destination.ToArray()
    } catch { Stop-TagRelease 'ApiAcquisitionFailed' }
    finally { if ($stream) {$stream.Dispose()}; if ($destination) {$destination.Dispose()}; if ($response) {$response.Dispose()}; $request.Dispose(); $client.Dispose(); $handler.Dispose() }
}
function Read-TagJson([byte[]] $Bytes) {
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    $doc=[Text.Json.JsonDocument]::Parse($text,[Text.Json.JsonDocumentOptions]@{MaxDepth=32})
    function Test-TagElement($Element) {
        if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $Element.EnumerateObject()) { if (-not $names.Add($property.Name)) { Stop-TagRelease 'DuplicateJson' }; Test-TagElement $property.Value }
        } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) { foreach ($item in $Element.EnumerateArray()) { Test-TagElement $item } }
    }
    try { Test-TagElement $doc.RootElement } finally { $doc.Dispose() }
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 32 -NoEnumerate
}
function Get-TagPages([string] $Path,[string] $Property) {
    $items=[Collections.Generic.List[object]]::new()
    for ($page=1; $page -le 20; $page++) {
        $separator=if ($Path.Contains('?')) {'&'} else {'?'}
        $response=Invoke-TagGitHub ($Path+$separator+'per_page=100&page='+$page)
        $batch=@(if ($Property) { $response[$Property] } else { $response })
        foreach ($item in $batch) { $items.Add($item) }
        if ($batch.Count -lt 100) {
            if ($Property -and $response.total_count -ne $items.Count) { Stop-TagRelease 'PaginationInconsistent' }
            return ,$items.ToArray()
        }
    }
    Stop-TagRelease 'PaginationBound'
}
function Resolve-SsrsTag([string] $Tag) {
    $null=Get-SsrsReleaseTag $Tag
    $reference=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/git/ref/tags/'+$Tag)
    if ($reference.ref -cne ('refs/tags/'+$Tag) -or $reference.object.sha -cnotmatch '^[a-f0-9]{40}$') { Stop-TagRelease 'TagMismatch' }
    $identity=$reference.object.sha; $object=$reference.object
    for ($depth=0; $depth -lt 5; $depth++) {
        if ($object.type -ceq 'commit') { return @{objectSha=$identity;sourceCommit=$object.sha} }
        if ($object.type -cne 'tag') { Stop-TagRelease 'TagTargetInvalid' }
        $annotated=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/git/tags/'+$object.sha)
        if ($annotated.sha -cne $object.sha -or $annotated.object.sha -cnotmatch '^[a-f0-9]{40}$') { Stop-TagRelease 'TagMismatch' }
        $object=$annotated.object
    }
    Stop-TagRelease 'TagDepth'
}
function Assert-SsrsTagSource($Tag,[string] $SourceCommit,$Master,$Comparison,$Pulls,[scriptblock] $ReadBranch,[scriptblock] $ReadPermission) {
    if ($SourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $Master.name -cne 'master' -or $Master.protected -isnot [bool] -or -not $Master.protected -or
        $Master.commit.sha -cnotmatch '^[a-f0-9]{40}$' -or $Comparison.base_commit.sha -cne $SourceCommit) { Stop-TagRelease 'SourceRelationshipUnknown' }
    if ($Comparison.status -cin @('ahead','identical')) { return 'master' }
    if ($Comparison.status -cnotin @('behind','diverged')) { Stop-TagRelease 'SourceRelationshipUnknown' }
    if ($Tag.channel -cne 'dev') { Stop-TagRelease 'ProtectedMasterRequired' }
    foreach ($pr in $Pulls) {
        if ($pr.state -cne 'open' -or $pr.base.ref -cne 'master' -or $pr.base.repo.full_name -cne 'clcdpc/ssrs-web' -or
            $null -eq $pr.head.repo -or $pr.head.repo.full_name -cne 'clcdpc/ssrs-web' -or $pr.head.repo.fork -ne $false -or $pr.head.sha -cne $SourceCommit) { continue }
        $branch=& $ReadBranch $pr.head.ref
        $permission=& $ReadPermission $pr.user.login
        if ($branch.ref -ceq ('refs/heads/'+$pr.head.ref) -and $branch.object.type -ceq 'commit' -and $branch.object.sha -ceq $SourceCommit -and
            $permission.permission -cin @('write','maintain','admin')) { return 'pull-request-'+$pr.number }
    }
    Stop-TagRelease 'EligiblePullRequestRequired'
}
function Get-SsrsSourceEligibility($Tag,[string] $SourceCommit,[string] $EventName='push') {
    $master=Invoke-TagGitHub '/repos/clcdpc/ssrs-web/branches/master'
    $comparison=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/compare/'+$SourceCommit+'...'+$master.commit.sha)
    $pulls=if ($Tag.channel -ceq 'dev' -and $comparison.status -cnotin @('ahead','identical')) { Get-TagPages '/repos/clcdpc/ssrs-web/pulls?state=open&base=master' '' } else { @() }
    try { return Assert-SsrsTagSource $Tag $SourceCommit $master $comparison $pulls {
        param($Name) Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/git/ref/heads/'+[Uri]::EscapeDataString($Name))
    } { param($Login) Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/collaborators/'+[Uri]::EscapeDataString($Login)+'/permission') }
    } catch {
        if ($_.Exception.Message -cne 'Release.Tag.EligiblePullRequestRequired' -or $Tag.channel -cne 'dev' -or $EventName -cne 'workflow_dispatch') { throw }
        # Only a new tag dispatch can use historical qualification. Initial push
        # eligibility is unchanged. GitHub immutable releases also lock the tag.
        $reference=Resolve-SsrsTag $Tag.tag
        $archive=Get-SsrsCanonicalArchive $Tag $reference
        if (-not $archive -or $archive.release.immutable -ne $true -or $archive.metadata.sourceCommit -cne $SourceCommit) { Stop-TagRelease 'HistoricalImmutableReleaseRequired' }
        Assert-SsrsHistoricalArchive $archive $master
        return 'historical-release-'+$archive.release.id
    }
}
function Assert-SsrsProducerFiles($SourceTree,$TrustedTree) {
    if ($SourceTree.truncated -ne $false -or $TrustedTree.truncated -ne $false) { Stop-TagRelease 'ProducerTreeIncomplete' }
    $maps=@()
    foreach ($tree in @($SourceTree,$TrustedTree)) {
        $map=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $tree.tree) {
            if ($entry.path -cmatch '^(\.github/|scripts/release/|deployment/toolkit\.lock\.json$|global\.json$)') {
                if ($entry.type -ceq 'tree') { continue }
                if ($entry.type -cne 'blob' -or $entry.mode -cnotin @('100644','100755') -or $map.ContainsKey($entry.path)) { Stop-TagRelease 'ProducerFilesUntrusted' }
                $map.Add($entry.path,($entry.mode+':'+$entry.sha))
            }
        }
        if (-not $map.ContainsKey('.github/workflows/release.yml')) { Stop-TagRelease 'ProducerFilesUntrusted' }
        $maps+=,$map
    }
    if ($maps[0].Count -ne $maps[1].Count) { Stop-TagRelease 'ProducerFilesUntrusted' }
    foreach ($path in $maps[0].Keys) { if (-not $maps[1].ContainsKey($path) -or $maps[0][$path] -cne $maps[1][$path]) { Stop-TagRelease 'ProducerFilesUntrusted' } }
}
function Assert-SsrsTagProducer($EventRun,$Run,$Workflow,$Repository,$Jobs,$Tag,[string] $SourceCommit) {
    if ($Repository.full_name -cne 'clcdpc/ssrs-web' -or $Repository.default_branch -cne 'master' -or $Repository.id -lt 1 -or
        $EventRun.id -ne $Run.id -or $EventRun.run_attempt -ne $Run.run_attempt -or $EventRun.head_sha -cne $SourceCommit -or
        $Run.head_sha -cne $SourceCommit -or $Run.head_branch -cne $Tag.tag -or $EventRun.head_branch -cne $Tag.tag -or
        $Run.id -lt 1 -or $Run.run_attempt -lt 1 -or $Run.workflow_id -ne $Workflow.id -or $Workflow.path -cne '.github/workflows/release.yml' -or
        $Run.path -cnotin @('.github/workflows/release.yml',('.github/workflows/release.yml@refs/tags/'+$Tag.tag)) -or
        $Run.event -cnotin @('push','workflow_dispatch') -or $EventRun.event -cne $Run.event -or $Run.status -cne 'completed' -or $EventRun.status -cne 'completed' -or
        $Run.conclusion -cne 'success' -or $EventRun.conclusion -cne 'success' -or $Run.repository.id -ne $Repository.id -or
        $EventRun.repository.id -ne $Repository.id -or $Run.head_repository.id -ne $Repository.id -or $EventRun.head_repository.id -ne $Repository.id) { Stop-TagRelease 'ProducerMismatch' }
    $job=@($Jobs | Where-Object { $_.name -ceq 'release' })
    if ($job.Count -ne 1 -or $job[0].run_id -ne $Run.id -or $job[0].run_attempt -ne $Run.run_attempt -or $job[0].head_sha -cne $SourceCommit -or
        $job[0].status -cne 'completed' -or $job[0].conclusion -cne 'success' -or $job[0].labels -cnotcontains 'windows-2022' -or $job[0].labels -contains 'self-hosted') { Stop-TagRelease 'BuildJobMismatch' }
}
function Assert-SsrsTagArtifact($Artifact,$Run,$Repository,[string] $Name) {
    if ($Artifact.id -lt 1 -or $Artifact.name -cne $Name -or $Artifact.expired -ne $false -or $Artifact.digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or
        $Artifact.workflow_run.id -ne $Run.id -or $Artifact.workflow_run.repository_id -ne $Repository.id -or $Artifact.workflow_run.head_repository_id -ne $Repository.id -or
        $Artifact.workflow_run.head_sha -cne $Run.head_sha -or $Artifact.workflow_run.head_branch -cne $Run.head_branch) { Stop-TagRelease 'ArtifactMismatch' }
}
function Receive-SsrsTagArtifact($Artifact,[string] $Directory,[string] $EntryName,[long] $Maximum) {
    $path=Join-Path $Directory ([Guid]::NewGuid().ToString('N')+'.transport')
    Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/actions/artifacts/'+$Artifact.id+'/zip') $path
    if (('sha256:'+(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) -cne $Artifact.digest) { Stop-TagRelease 'TransportMismatch' }
    $zip=[IO.Compression.ZipFile]::OpenRead($path)
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne $EntryName -or $zip.Entries[0].Length -lt 1 -or $zip.Entries[0].Length -gt $Maximum) { Stop-TagRelease 'TransportEntryMismatch' }
        $output=Join-Path $Directory $EntryName
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.Entries[0],$output,$false)
        return $output
    } finally { $zip.Dispose() }
}
function Assert-SsrsTagProvenance($Provenance,$Run,$Artifact) {
    $p=$Provenance
    if ($p.schemaVersion -ne 1 -or $p.recordKind -cne 'ssrs-web-release-provenance' -or $p.repository -cne 'clcdpc/ssrs-web' -or
        $p.sourceCommit -cne $Run.head_sha -or $p.workflowCommit -cne $Run.head_sha -or $p.workflowPath -cne '.github/workflows/release.yml' -or
        $p.runId -ne $Run.id -or $p.runAttempt -ne $Run.run_attempt -or $p.buildJob -cne 'release' -or $p.artifactId -ne $Artifact.id -or
        ('sha256:'+$p.artifactTransportSha256) -cne $Artifact.digest -or $p.releaseId -cne ('ssrs-web-'+$Run.id+'-'+$Run.run_attempt) -or
        $p.integrityVerified -ne $true -or $p.deploymentApproval -ne $false -or $p.hostInstallationApproval -ne $false -or
        $p.innerReleaseSha256 -cnotmatch '^[a-f0-9]{64}$' -or $p.manifestSha256 -cnotmatch '^[a-f0-9]{64}$') { Stop-TagRelease 'ProvenanceMismatch' }
}
function Get-SsrsAuthenticatedTagRelease($EventRun,[string] $TrustedCommit,[string] $Directory) {
    if ($TrustedCommit -cnotmatch '^[a-f0-9]{40}$' -or $EventRun.id -lt 1) { Stop-TagRelease 'InvalidContext' }
    $tag=Get-SsrsReleaseTag $EventRun.head_branch
    $reference=Resolve-SsrsTag $tag.tag
    $run=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/actions/runs/'+$EventRun.id)
    $repo=Invoke-TagGitHub '/repos/clcdpc/ssrs-web/'
    $workflow=Invoke-TagGitHub '/repos/clcdpc/ssrs-web/actions/workflows/release.yml'
    $jobs=Get-TagPages ('/repos/clcdpc/ssrs-web/actions/runs/'+$run.id+'/attempts/'+$run.run_attempt+'/jobs') 'jobs'
    Assert-SsrsTagProducer $EventRun $run $workflow $repo $jobs $tag $reference.sourceCommit
    $eligibility=Get-SsrsSourceEligibility $tag $reference.sourceCommit $run.event
    $sourceTree=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/git/trees/'+$reference.sourceCommit+'?recursive=1')
    # Protected-master history establishes trust in that exact historical
    # producer. Unmerged sources must instead match the consumer's reviewed
    # producer files, so their PR cannot redefine its own admission/build code.
    $producerTrustCommit=$TrustedCommit
    if ($eligibility -clike 'historical-release-*') {
        $historical=Get-SsrsCanonicalArchive $tag $reference
        $producerTrustCommit=$historical.metadata.consumerCommit
    }
    $trustedTree=if ($eligibility -ceq 'master') { $sourceTree } else { Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/git/trees/'+$producerTrustCommit+'?recursive=1') }
    Assert-SsrsProducerFiles $sourceTree $trustedTree
    $artifacts=Get-TagPages ('/repos/clcdpc/ssrs-web/actions/runs/'+$run.id+'/artifacts') 'artifacts'
    $suffix=$run.id.ToString()+'-'+$run.run_attempt
    $release=@($artifacts | Where-Object name -CEQ ('ssrs-web-release-'+$suffix))
    $provenance=@($artifacts | Where-Object name -CEQ ('ssrs-web-provenance-'+$suffix))
    if ($release.Count -ne 1 -or $provenance.Count -ne 1) { Stop-TagRelease 'ArtifactSelectionAmbiguous' }
    Assert-SsrsTagArtifact $release[0] $run $repo ('ssrs-web-release-'+$suffix)
    Assert-SsrsTagArtifact $provenance[0] $run $repo ('ssrs-web-provenance-'+$suffix)
    [void][IO.Directory]::CreateDirectory($Directory)
    $provenancePath=Receive-SsrsTagArtifact $provenance[0] $Directory 'release-provenance.json' 4194304
    $p=Read-TagJson ([IO.File]::ReadAllBytes($provenancePath))
    Assert-SsrsTagProvenance $p $run $release[0]
    if ($p.releaseTag -cne $tag.tag -or $p.tagObjectSha -cne $reference.objectSha) { Stop-TagRelease 'TagMismatch' }
    $archive=Receive-SsrsTagArtifact $release[0] $Directory 'release.zip' 1073741824
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $p.innerReleaseSha256) { Stop-TagRelease 'InnerReleaseMismatch' }
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $manifests=@($zip.Entries | Where-Object FullName -CEQ 'manifest.json')
        if ($manifests.Count -ne 1 -or $manifests[0].Length -gt 4194304) { Stop-TagRelease 'ManifestMismatch' }
        $stream=$manifests[0].Open()
        try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($hash -cne $p.manifestSha256) { Stop-TagRelease 'ManifestMismatch' }
    } finally { $zip.Dispose() }
    $again=Resolve-SsrsTag $tag.tag
    if ($again.objectSha -cne $reference.objectSha -or $again.sourceCommit -cne $reference.sourceCommit) { Stop-TagRelease 'TagMoved' }
    return @{tag=$tag.tag;channel=$tag.channel;targetId=$tag.targetId;sourceCommit=$reference.sourceCommit;tagObjectSha=$reference.objectSha;
        sourceEligibility=$eligibility;trustedCommit=$TrustedCommit;provenance=$p;archivePath=$archive;run=$run}
}
function Test-SsrsTagSuperseded($Release) {
    $selected=Get-SsrsReleaseTag $Release.tag
    $refs=Invoke-TagGitHub '/repos/clcdpc/ssrs-web/git/matching-refs/tags/v'
    if (@($refs).Count -ge 1000) { Stop-TagRelease 'TagListingBound' }
    foreach ($ref in $refs) {
        if ($ref.ref -cnotmatch '^refs/tags/(v.+)$') { continue }
        try { $candidate=Get-SsrsReleaseTag $Matches[1] } catch { continue }
        if ($candidate.targetId -cne $selected.targetId -or $candidate.channel -cne $selected.channel -or (Compare-SsrsReleaseTag $candidate $selected) -le 0) { continue }
        $target=Resolve-SsrsTag $candidate.tag
        # Unknown relationships fail closed; a valid newer tag may suppress an older
        # deployment even if its build has not completed. The toolkit owns lineage.
        $null=Get-SsrsSourceEligibility $candidate $target.sourceCommit 'workflow_dispatch'
        return $true
    }
    return $false
}
. "$PSScriptRoot/ReleaseArchive.Common.ps1"
