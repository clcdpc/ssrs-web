# GitHub Releases are canonical archives; Actions artifacts remain the transport
# for THIS build. No existing release or asset is ever modified by this code.
function Get-SsrsGitHubRelease([string] $Tag) {
    $releases=Get-TagPages '/repos/clcdpc/ssrs-web/releases' ''
    $matches=@($releases | Where-Object tag_name -CEQ $Tag)
    if ($matches.Count -gt 1) { Stop-TagRelease 'ArchiveAmbiguous' }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}
function Get-SsrsCanonicalArchive($Tag,$Reference) {
    $release=Get-SsrsGitHubRelease $Tag.tag
    if (-not $release) { return $null }
    # target_commitish is only a tag-creation hint and may be a branch name.
    # Bind the peeled live tag and archived full commit/object identity below.
    if ($release.draft -ne $false -or $release.tag_name -cne $Tag.tag -or
        $release.prerelease -ne ($Tag.channel -cne 'stable') -or $release.author.id -ne 41898282 -or $release.author.login -cne 'github-actions[bot]') { Stop-TagRelease 'ArchiveIdentityMismatch' }
    $assets=Get-TagPages ('/repos/clcdpc/ssrs-web/releases/'+$release.id+'/assets') ''
    foreach ($name in @('release.zip','release-metadata.json','release-provenance.json','SHA256SUMS','LICENSE','THIRD-PARTY-NOTICES.md')) {
        $selected=@($assets | Where-Object name -CEQ $name)
        if ($selected.Count -ne 1 -or $selected[0].state -cne 'uploaded' -or $selected[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$') { Stop-TagRelease 'ArchiveAssetMismatch' }
    }
    $metadataAsset=@($assets | Where-Object name -CEQ 'release-metadata.json')[0]
    $directory=Join-Path ([IO.Path]::GetTempPath()) ('ssrs-archive-'+[Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($directory)
    $path=Join-Path $directory 'release-metadata.json'
    Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/releases/assets/'+$metadataAsset.id) $path
    if ((Get-Item -LiteralPath $path).Length -gt 4194304 -or ('sha256:'+(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()) -cne $metadataAsset.digest) { Stop-TagRelease 'ArchiveMetadataMismatch' }
    $m=Read-TagJson ([IO.File]::ReadAllBytes($path))
    $package=@($assets | Where-Object name -CEQ 'release.zip')[0]
    $provenance=@($assets | Where-Object name -CEQ 'release-provenance.json')[0]
    if ($m.schemaVersion -ne 1 -or $m.recordKind -cne 'ssrs-web-canonical-release' -or $m.repository -cne 'clcdpc/ssrs-web' -or
        $m.tag -cne $Tag.tag -or $m.channel -cne $Tag.channel -or $m.sourceCommit -cne $Reference.sourceCommit -or $m.tagObjectSha -cne $Reference.objectSha -or
        $m.runId -lt 1 -or $m.runAttempt -lt 1 -or $m.artifactId -lt 1 -or $m.transportSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        ('sha256:'+$m.packageSha256) -cne $package.digest -or ('sha256:'+$m.provenanceSha256) -cne $provenance.digest -or
        $m.releaseId -cne ('ssrs-web-'+$m.runId+'-'+$m.runAttempt)) { Stop-TagRelease 'ArchiveMetadataMismatch' }
    return @{release=$release;metadata=$m;assets=$assets}
}
function Assert-SsrsHistoricalArchive($Archive,$Master) {
    $m=$Archive.metadata
    if ($Archive.release.immutable -ne $true -or $m.sourceEligibility -cnotmatch '^(master|pull-request-[1-9][0-9]*)$' -or
        $m.consumerCommit -cnotmatch '^[a-f0-9]{40}$' -or $m.consumerRunId -lt 1 -or $m.consumerRunAttempt -lt 1) { Stop-TagRelease 'HistoricalImmutableReleaseRequired' }
    $comparison=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/compare/'+$m.consumerCommit+'...'+$Master.commit.sha)
    if ($comparison.base_commit.sha -cne $m.consumerCommit -or $comparison.status -cnotin @('ahead','identical')) { Stop-TagRelease 'HistoricalConsumerUntrusted' }
    $workflow=Invoke-TagGitHub '/repos/clcdpc/ssrs-web/actions/workflows/deploy-release.yml'
    $run=Invoke-TagGitHub ('/repos/clcdpc/ssrs-web/actions/runs/'+$m.consumerRunId+'/attempts/'+$m.consumerRunAttempt)
    $repo=Invoke-TagGitHub '/repos/clcdpc/ssrs-web/'
    if ($run.id -ne $m.consumerRunId -or $run.run_attempt -ne $m.consumerRunAttempt -or $run.head_sha -cne $m.consumerCommit -or $run.head_branch -cne 'master' -or
        $run.repository.id -ne $repo.id -or $run.head_repository.id -ne $repo.id -or $run.event -cne 'workflow_run' -or $run.status -cne 'completed' -or
        $workflow.path -cne '.github/workflows/deploy-release.yml' -or $run.workflow_id -ne $workflow.id -or
        $run.path -cnotin @('.github/workflows/deploy-release.yml','.github/workflows/deploy-release.yml@refs/heads/master')) { Stop-TagRelease 'HistoricalConsumerUntrusted' }
    $jobs=Get-TagPages ('/repos/clcdpc/ssrs-web/actions/runs/'+$run.id+'/attempts/'+$run.run_attempt+'/jobs') 'jobs'
    $job=@($jobs | Where-Object name -CEQ 'archive')
    if ($job.Count -ne 1 -or $job[0].run_id -ne $run.id -or $job[0].run_attempt -ne $run.run_attempt -or $job[0].head_sha -cne $m.consumerCommit -or
        $job[0].status -cne 'completed' -or $job[0].conclusion -cne 'success' -or $job[0].labels -cnotcontains 'windows-2022' -or $job[0].labels -contains 'self-hosted') { Stop-TagRelease 'HistoricalConsumerUntrusted' }
}
function Invoke-SsrsReleaseWrite([string] $Method,[string] $Path,$Body,[string] $FilePath) {
    if (($Method -ceq 'POST' -and $Path -ceq '/repos/clcdpc/ssrs-web/releases') -or
        ($Method -ceq 'PATCH' -and $Path -cmatch '^/repos/clcdpc/ssrs-web/releases/[1-9][0-9]*$')) { $origin='https://api.github.com' }
    elseif ($Method -ceq 'POST' -and $FilePath -and $Path -cmatch '^/repos/clcdpc/ssrs-web/releases/[1-9][0-9]*/assets\?name=[A-Za-z0-9._-]+$') { $origin='https://uploads.github.com' }
    else { Stop-TagRelease 'ArchiveWriteRejected' }
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseCookies=$false
    $client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromMinutes(5)
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),($origin+$Path))
    $response=$null
    try {
        $request.Headers.UserAgent.ParseAdd('ssrs-web-release-archive/1')
        $request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:SSRS_ARTIFACT_READ_TOKEN)
        $request.Headers.Add('X-GitHub-Api-Version','2022-11-28')
        if ($FilePath) {
            $request.Content=[Net.Http.StreamContent]::new([IO.File]::OpenRead($FilePath))
            $request.Content.Headers.ContentType=[Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        } else { $request.Content=[Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 16 -Compress),[Text.Encoding]::UTF8,'application/json') }
        $response=$client.Send($request)
        if (-not $response.IsSuccessStatusCode) { Stop-TagRelease 'ArchiveWriteFailed' }
        return Read-TagJson ($response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
    } finally { if ($response) { $response.Dispose() }; $request.Dispose(); $client.Dispose(); $handler.Dispose() }
}
function Publish-SsrsCanonicalRelease($Release,[string] $Directory) {
    $tag=Get-SsrsReleaseTag $Release.tag
    $reference=Resolve-SsrsTag $Release.tag
    if ($reference.sourceCommit -cne $Release.sourceCommit -or $reference.objectSha -cne $Release.tagObjectSha) { Stop-TagRelease 'TagMoved' }
    $existing=Get-SsrsCanonicalArchive $tag $reference
    if ($existing) { return @{outcome='Preserved';releaseId=$existing.release.id;archivedPackageSha256=$existing.metadata.packageSha256;currentPackageSha256=$Release.provenance.innerReleaseSha256} }
    [void][IO.Directory]::CreateDirectory($Directory)
    [IO.File]::Copy($Release.archivePath,(Join-Path $Directory 'release.zip'),$false)
    $proof=Join-Path $Directory 'release-provenance.json'
    [IO.File]::WriteAllText($proof,($Release.provenance | ConvertTo-Json -Depth 16 -Compress),[Text.UTF8Encoding]::new($false))
    $m=[ordered]@{schemaVersion=1;recordKind='ssrs-web-canonical-release';repository='clcdpc/ssrs-web';tag=$Release.tag;channel=$Release.channel;
        sourceCommit=$Release.sourceCommit;tagObjectSha=$Release.tagObjectSha;sourceEligibility=$Release.sourceEligibility;
        runId=$Release.run.id;runAttempt=$Release.run.run_attempt;releaseId=$Release.provenance.releaseId;artifactId=$Release.provenance.artifactId;
        transportSha256=$Release.provenance.artifactTransportSha256;packageSha256=$Release.provenance.innerReleaseSha256;
        provenanceSha256=(Get-FileHash -LiteralPath $proof).Hash.ToLowerInvariant();consumerCommit=$env:GITHUB_WORKFLOW_SHA;
        consumerRunId=[long]$env:GITHUB_RUN_ID;consumerRunAttempt=[long]$env:GITHUB_RUN_ATTEMPT}
    [IO.File]::WriteAllText((Join-Path $Directory 'release-metadata.json'),($m | ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
    # Notices are also inside the fully inspected runtime package. Extract only
    # these fixed entries, never application scripts or paths from the archive.
    $zip=[IO.Compression.ZipFile]::OpenRead($Release.archivePath)
    try {
        foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md')) {
            $entry=$zip.GetEntry('application/'+$name)
            if ($null -eq $entry -or $entry.Length -lt 1 -or $entry.Length -gt 4194304) { Stop-TagRelease 'ArchiveNoticesRequired' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $Directory $name),$false)
        }
    } finally { $zip.Dispose() }
    $files=@(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name)
    $sums=@($files | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name })
    [IO.File]::WriteAllText((Join-Path $Directory 'SHA256SUMS'),(($sums -join "`n")+"`n"),[Text.UTF8Encoding]::new($false))
    try {
        # The independently checked tag already exists. Omit the unused
        # target_commitish creation hint; it is not source-identity evidence.
        $created=Invoke-SsrsReleaseWrite 'POST' '/repos/clcdpc/ssrs-web/releases' @{
            tag_name=$Release.tag;name=$Release.tag;draft=$true;prerelease=($Release.channel -cne 'stable');make_latest='false';
            body=("Canonical archive for $($Release.tag), source $($Release.sourceCommit).`nProducer: https://github.com/clcdpc/ssrs-web/actions/runs/$($Release.run.id)/attempts/$($Release.run.run_attempt).`nActions artifacts identify each fresh build independently; this archive is never replaced.") }
    } catch {
        # A different publisher may win the create race. Only accept a complete,
        # authenticated archive; never patch or upload into another draft.
        $winner=Get-SsrsCanonicalArchive $tag $reference
        if (-not $winner) { throw }
        return @{outcome='Preserved';releaseId=$winner.release.id;archivedPackageSha256=$winner.metadata.packageSha256;currentPackageSha256=$Release.provenance.innerReleaseSha256}
    }
    if ($created.id -lt 1 -or $created.draft -ne $true -or $created.tag_name -cne $Release.tag) { Stop-TagRelease 'ArchiveIdentityMismatch' }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name) {
        $asset=Invoke-SsrsReleaseWrite 'POST' ('/repos/clcdpc/ssrs-web/releases/'+$created.id+'/assets?name='+$file.Name) $null $file.FullName
        if ($asset.name -cne $file.Name -or $asset.state -cne 'uploaded' -or $asset.digest -cne ('sha256:'+(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant())) { Stop-TagRelease 'ArchiveUploadMismatch' }
    }
    $again=Resolve-SsrsTag $Release.tag
    if ($again.objectSha -cne $reference.objectSha -or $again.sourceCommit -cne $reference.sourceCommit) { Stop-TagRelease 'TagMoved' }
    $null=Invoke-SsrsReleaseWrite 'PATCH' ('/repos/clcdpc/ssrs-web/releases/'+$created.id) @{draft=$false;make_latest='false'}
    $published=Get-SsrsCanonicalArchive $tag $reference
    if (-not $published -or $published.release.id -ne $created.id) { Stop-TagRelease 'ArchivePublicationFailed' }
    return @{outcome='Published';releaseId=$created.id;archivedPackageSha256=$published.metadata.packageSha256;currentPackageSha256=$Release.provenance.innerReleaseSha256}
}
