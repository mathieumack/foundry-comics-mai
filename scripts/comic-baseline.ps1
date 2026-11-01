param(
    [Parameter(Mandatory)] [string] $RunDirectory,
    [switch] $Initialize
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pack = Join-Path $repositoryRoot 'evaluations/comic-baseline/v1'
$corpus = Get-Content -LiteralPath (Join-Path $pack 'corpus.json') -Raw | ConvertFrom-Json -AsHashtable
$rubric = Get-Content -LiteralPath (Join-Path $pack 'rubric.json') -Raw | ConvertFrom-Json -AsHashtable
$stageNames = @('scaffold', 'rewrite', 'image', 'composition')
$operations = @{ scaffold = 'project-scaffold'; rewrite = 'prompt-rewrite'; image = 'panel-image'; composition = '' }

function Get-Hash([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ArtifactPath([string] $RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[\\/])\.\.?([\\/]|$)|[:\\]') {
        throw 'Artifact paths must be relative, use forward slashes, and contain no traversal.'
    }
    $current = $RunDirectory
    foreach ($segment in $RelativePath.Split('/')) {
        $current = Join-Path $current $segment
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Symbolic links are not allowed in evidence paths.'
        }
    }
    return $current
}

function Get-Percentile($Values, [double] $Percentile) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    return $sorted[[int][Math]::Ceiling($Percentile * $sorted.Count) - 1]
}

function Get-KnownSum($Values) {
    $known = @($Values | Where-Object { $null -ne $_ })
    if ($known.Count -eq 0) { return $null }
    [long] $sum = 0
    foreach ($value in $known) { $sum += $value }
    return $sum
}

if ($Initialize) {
    if (Test-Path -LiteralPath $RunDirectory) { throw 'Choose a new run directory; existing evidence is never overwritten.' }
    $revision = git -C $repositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine pipeline revision.' }
    $reports = @(foreach ($brief in $corpus.briefs) {
        $stages = @(foreach ($name in $stageNames) {
            $skipped = $name -eq 'scaffold' -and $brief.route -eq 'free-form'
            @{
                name = $name
                state = $(if ($skipped) { 'not-applicable' } else { 'not-run' })
                requestCount = $(if ($skipped -or $name -eq 'composition') { 0 } else { $null })
                retryCount = $(if ($skipped -or $name -eq 'composition') { 0 } else { $null })
                wallDurationMs = $null
                measurementEvidence = $(if ($skipped) { 'Fixed free-form route; no scaffold call.' } elseif ($name -eq 'composition') { 'Local composition; no model calls.' } else { $null })
                traces = @()
            }
        })
        @{
            briefId = $brief.id; technicalOutcome = 'not-run'; errorCategory = $null
            artifacts = @(); stages = $stages; ratings = @()
        }
    })
    $run = [ordered]@{
        schemaVersion = 1; runId = [Guid]::NewGuid().ToString('N'); corpusId = $corpus.corpusId
        corpusSha256 = Get-Hash (Join-Path $pack 'corpus.json')
        rubricSha256 = Get-Hash (Join-Path $pack 'rubric.json')
        harnessRevision = $revision; pipelineRevision = $null; createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        configuration = @{
            promptModel = $null; promptModelVersion = $null; imageModel = $null; imageModelVersion = $null
            sdkVersions = $null; maxParallelImages = $null; imageRetryCount = $null
            imageRetryBaseDelayMilliseconds = $null; rewritePrompts = $true; safetyPolicyVersion = $null
        }
        ceilingApproval = $null; reports = $reports
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $RunDirectory 'references')
    foreach ($reference in $corpus.references) {
        $bytes = [Convert]::FromBase64String($reference.pngBase64)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -ne $reference.sha256) {
            throw 'Fixed reference integrity check failed.'
        }
        [IO.File]::WriteAllBytes((Join-Path $RunDirectory "references/$($reference.fileName)"), $bytes)
    }
    $run | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $RunDirectory 'run.json') -Encoding utf8
    Write-Output 'Initialized 20 pending evidence records. No model calls or human evaluation performed.'
    return
}

$json = Get-Content -LiteralPath (Join-Path $RunDirectory 'run.json') -Raw
if (-not (Test-Json -Json $json -SchemaFile (Join-Path $pack 'run.schema.json'))) {
    throw 'Invalid baseline run contract.'
}
$run = $json | ConvertFrom-Json -AsHashtable
if ($run.corpusSha256 -ne (Get-Hash (Join-Path $pack 'corpus.json')) -or
    $run.rubricSha256 -ne (Get-Hash (Join-Path $pack 'rubric.json'))) {
    throw 'Corpus or rubric changed; use the original version or start a new corpus version.'
}
$ids = @($run.reports.briefId | Sort-Object -Unique)
if ($ids.Count -ne 20 -or (Compare-Object $ids @($corpus.briefs.id | Sort-Object))) {
    throw 'Exactly one report per fixed brief is required.'
}
foreach ($reference in $corpus.references) {
    if ((Get-Hash (Get-ArtifactPath "references/$($reference.fileName)")) -ne $reference.sha256) {
        throw 'Run reference integrity check failed.'
    }
}
if ($null -ne $run.ceilingApproval -and @($run.ceilingApproval.stages.name | Sort-Object -Unique).Count -ne 4) {
    throw 'Ceiling approval must cover all four stages exactly once.'
}
$pending = [Collections.Generic.List[string]]::new()
foreach ($report in $run.reports) {
    if (@($report.stages.name | Sort-Object -Unique).Count -ne 4) { throw 'Each report requires four distinct stages.' }
    if (@($report.ratings | ForEach-Object evaluator | Sort-Object -Unique).Count -ne $report.ratings.Count) { throw 'Duplicate human evaluator.' }
    foreach ($artifact in $report.artifacts) {
        if ((Get-Hash (Get-ArtifactPath $artifact.path)) -ne $artifact.sha256) { throw 'Artifact hash mismatch.' }
    }
    foreach ($stage in $report.stages) {
        if ($stage.state -in @('not-run', 'not-applicable') -and
            ($stage.traces.Count -gt 0 -or $stage.requestCount -gt 0 -or $stage.retryCount -gt 0 -or $null -ne $stage.wallDurationMs)) {
            throw 'Unexecuted stage contains execution measurements.'
        }
        if ($stage.name -eq 'composition' -and ($stage.requestCount -ne 0 -or $stage.retryCount -ne 0)) {
            throw 'Local composition must have zero model requests and retries.'
        }
        if ($null -ne $stage.requestCount -and $stage.requestCount -lt $stage.traces.Count) { throw 'Request count is below captured invocation count.' }
        if ($null -ne $stage.retryCount -and $null -ne $stage.requestCount -and $stage.retryCount -gt $stage.requestCount) { throw 'Retry count exceeds request count.' }
        if (($null -ne $stage.requestCount -or $null -ne $stage.retryCount -or $null -ne $stage.wallDurationMs) -and
            [string]::IsNullOrWhiteSpace($stage.measurementEvidence)) { throw 'Measured values require an evidence reference or explanation.' }
        foreach ($trace in $stage.traces) {
            if ($trace.operation -ne $operations[$stage.name]) { throw 'Trace operation does not match stage.' }
            if ($trace.usageSource -eq 'unavailable' -and
                ($null -ne $trace.inputTokens -or $null -ne $trace.outputTokens -or $null -ne $trace.totalTokens)) {
                throw 'Unavailable usage must remain null, not zero.'
            }
        }
    }
    $kinds = @($report.artifacts | ForEach-Object kind)
    $hasEvidence = $report.technicalOutcome -ne 'not-run' -and $report.ratings.Count -ge 2 -and
        'approved-input' -in $kinds -and 'generation-response' -in $kinds -and 'configuration' -in $kinds
    if ($report.technicalOutcome -eq 'succeeded') {
        $brief = $corpus.briefs | Where-Object id -EQ $report.briefId
        $panelCount = switch ($brief.layout) { 'Storyline' { 3 } 'Standard' { 6 } default { 5 } }
        $hasEvidence = $hasEvidence -and 'board' -in $kinds -and @($report.artifacts | Where-Object kind -EQ 'panel').Count -eq $panelCount
    }
    if (-not $hasEvidence) { $pending.Add($report.briefId) }
}

$stageSummaries = @(foreach ($name in $stageNames) {
    $stages = @($run.reports.stages | Where-Object name -EQ $name)
    $executed = @($stages | Where-Object { $_.state -notin @('not-run', 'not-applicable') })
    $durations = @($executed | Where-Object { $null -ne $_.wallDurationMs } | ForEach-Object wallDurationMs)
    $traces = @($stages | ForEach-Object { $_.traces })
    $knownRequests = @($stages | Where-Object { $null -ne $_.requestCount })
    $knownRetries = @($stages | Where-Object { $null -ne $_.retryCount })
    $usage = [ordered]@{}
    foreach ($field in @('inputTokens', 'outputTokens', 'totalTokens')) {
        $reported = @($traces | Where-Object { $_.usageSource -eq 'provider-reported' -and $null -ne $_[$field] })
        $complete = $knownRequests.Count -eq 20 -and (Get-KnownSum @($knownRequests | ForEach-Object requestCount)) -eq $traces.Count -and $reported.Count -eq $traces.Count -and $traces.Count -gt 0
        $usage[$field] = @{
            reportedSubtotal = Get-KnownSum @($reported | ForEach-Object { $_[$field] })
            reportedInvocationCount = $reported.Count
            completeTotal = $(if ($complete) { Get-KnownSum @($reported | ForEach-Object { $_[$field] }) } else { $null })
        }
    }
    @{
        name = $name; capturedInvocationCount = $traces.Count
        requestCount = $(if ($knownRequests.Count -eq 20) { Get-KnownSum @($knownRequests | ForEach-Object requestCount) } else { $null })
        knownRequestSubtotal = Get-KnownSum @($knownRequests | ForEach-Object requestCount); reportsWithKnownRequests = $knownRequests.Count
        retryCount = $(if ($knownRetries.Count -eq 20) { Get-KnownSum @($knownRetries | ForEach-Object retryCount) } else { $null })
        knownRetrySubtotal = Get-KnownSum @($knownRetries | ForEach-Object retryCount); reportsWithKnownRetries = $knownRetries.Count
        executedReports = $executed.Count; durationSampleCount = $durations.Count
        p50WallDurationMs = Get-Percentile $durations 0.50; p95WallDurationMs = Get-Percentile $durations 0.95
        reportedUsage = $usage; cost = $null; costSource = 'unavailable'
    }
})
$editorial = [ordered]@{}
foreach ($dimension in $rubric.dimensions.Keys) {
    $scores = @($run.reports | ForEach-Object { $_.ratings } | ForEach-Object { $_.scores[$dimension] } | Where-Object { $null -ne $_ } | Sort-Object)
    $median = if ($scores.Count -eq 0) { $null } elseif ($scores.Count % 2 -eq 1) {
        $scores[[int][Math]::Floor($scores.Count / 2)]
    } else { ($scores[$scores.Count / 2 - 1] + $scores[$scores.Count / 2]) / 2.0 }
    $editorial[$dimension] = @{ median = $median; scoreCount = $scores.Count }
}
[ordered]@{
    schemaVersion = 1; runId = $run.runId; corpusId = $run.corpusId
    pendingEvidenceBriefIds = @($pending)
    configurationComplete = $null -ne $run.pipelineRevision -and @($run.configuration.Values | Where-Object { $null -eq $_ }).Count -eq 0
    ceilingApprovalRecorded = $null -ne $run.ceilingApproval
    technicalOutcomes = @($run.reports | Group-Object technicalOutcome | ForEach-Object { @{ outcome = $_.Name; count = $_.Count } })
    editorial = $editorial; stages = $stageSummaries
    note = 'Descriptive evidence only, not a release gate or proof of quality. Null means unknown; subtotals are not complete usage or billing.'
} | ConvertTo-Json -Depth 30
