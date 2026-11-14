# Contract: clc-deployment PR #2 docs/verified-workflow-integration.md.
# Only verified data crosses this boundary. The independently installed toolkit
# owns standing policy, exact approvals, ordering, locking and IIS lifecycle.
function Invoke-SsrsToolkitAutomaticDeployment($Release,$Context) {
    $module=Get-SsrsRecordProperty $Context 'module'
    if (-not $module) { throw [NotSupportedException]::new('Ssrs.AutomaticDeployment.ToolkitIntegrationUnavailable') }
    $compatible=& $module {
        $command=Get-Command Invoke-ClcIisDeployment -CommandType Function
        $sets=@($command.ParameterSets | Where-Object Name -CEQ 'Automated')
        return $sets.Count -eq 1 -and @($sets[0].Parameters | Where-Object { $_.IsMandatory -and $_.Name -cin @('TargetId','StandingAuthorizationId','ReleaseTag','ArchivePath','VerifiedReleasePath') }).Count -eq 5
    }
    if (-not $compatible) { throw [NotSupportedException]::new('Ssrs.AutomaticDeployment.ToolkitIntegrationUnavailable') }
    Assert-SsrsSelector $Context.standingAuthorizationId
    $target=$Context.target
    if ($target.targetId -cne $Release.targetId -or $target.applicationId -cne 'ssrs-web') { Stop-Ssrs 'Ssrs.FixedTargetMismatch' }
    $classification=& $module { param($Tag) Test-ClcRelease -ReleaseTag $Tag } $Release.tag
    $expectedChannel=if ($Release.targetId -ceq 'ssrs-web-dev') {'DEV'} else {'Production'}
    if ($classification.channel -cne $expectedChannel) { Stop-Ssrs 'Ssrs.AutomaticDeployment.ChannelMismatch' }
    $p=$Release.provenance
    $identity=[ordered]@{contractFamily='clc-file-iis-v1';schemaVersion=1;recordKind='verified-release';targetId=$Release.targetId;applicationId='ssrs-web';
        releaseId=$p.releaseId;tag=$Release.tag;sourceRepository='clcdpc/ssrs-web';sourceCommit=$Release.sourceCommit;
        releaseZipSha256=$p.innerReleaseSha256;manifestSha256=$p.manifestSha256;
        provenance=[ordered]@{workflowPath='.github/workflows/release.yml';runId=[long]$Release.run.id;runAttempt=[long]$Release.run.run_attempt;
            buildJob='release';artifactId=[long]$p.artifactId;transportSha256=$p.artifactTransportSha256}}
    $name='verified-'+$Release.run.id+'-'+$Release.run.run_attempt+'-'+[Guid]::NewGuid().ToString('N')
    $archive=Join-Path $target.artifactInboxRoot ($name+'.zip')
    $record=Join-Path $target.artifactInboxRoot ($name+'.json')
    # The protected inbox and target have already been authenticated and pinned
    # by Open-SsrsAutomaticContext. Create-new inputs survive failure for review.
    [IO.File]::Copy($Release.archivePath,$archive,$false)
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $p.innerReleaseSha256) { Stop-Ssrs 'Ssrs.InnerReleaseMismatch' }
    $file=[IO.FileStream]::new($record,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $file.Write([Text.Encoding]::UTF8.GetBytes(($identity | ConvertTo-Json -Depth 8 -Compress))); $file.Flush($true) } finally { $file.Dispose() }
    return & $module {
        param($Target,$Standing,$Tag,$Archive,$Identity)
        Invoke-ClcIisDeployment -TargetId $Target -StandingAuthorizationId $Standing -ReleaseTag $Tag -ArchivePath $Archive -VerifiedReleasePath $Identity
    } $Release.targetId $Context.standingAuthorizationId $Release.tag $archive $record
}
