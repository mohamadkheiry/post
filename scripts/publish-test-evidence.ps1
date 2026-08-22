[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputDirectory,
    [string]$RuntimeEvidencePath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $InputDirectory))
if (-not (Test-Path -LiteralPath $inputPath -PathType Container)) {
    throw "Test-result directory was not found: $inputPath"
}

$summaryPath = Join-Path $inputPath 'summary.md'
$environmentPath = Join-Path $inputPath 'environment.json'
$inventoryPath = Join-Path $inputPath 'test-inventory.csv'
foreach ($requiredPath in @($summaryPath, $environmentPath, $inventoryPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required test evidence was not found: $requiredPath"
    }
}

$summary = Get-Content -LiteralPath $summaryPath -Raw
if ($summary -notmatch '(?m)^- Failed: 0\s*$' -or
    $summary -notmatch '(?m)^- Skipped/not executed: 0\s*$' -or
    $summary -notmatch '(?m)^- Entirely uncovered handwritten methods: 0\s*$') {
    throw 'Only a run with zero failures, zero skips, and zero uncovered handwritten methods can be published.'
}

$environment = Get-Content -LiteralPath $environmentPath -Raw | ConvertFrom-Json -Depth 20
if ([string]::IsNullOrWhiteSpace([string]$environment.commit) -or
    [string]::Equals([string]$environment.commit, 'uncommitted', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test evidence must identify a Git commit.'
}
if ([bool]$environment.workingTreeDirty) {
    throw 'Test evidence must be produced from a clean working tree.'
}

$currentCommit = (& git -C $repositoryRoot rev-parse --short HEAD 2>$null).Trim()
$currentCommitFull = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or
    [string]::IsNullOrWhiteSpace($currentCommit) -or
    [string]::IsNullOrWhiteSpace($currentCommitFull)) {
    throw 'The current Git commit could not be resolved.'
}
if (-not [string]::Equals($currentCommit, [string]$environment.commit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Evidence commit '$($environment.commit)' does not match current HEAD '$currentCommit'."
}
$currentWorkingTreeStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'The current Git working-tree state could not be resolved.'
}
if ($currentWorkingTreeStatus.Count -gt 0) {
    throw 'Published evidence requires the current working tree to remain clean.'
}

$inventoryRows = @(Import-Csv -LiteralPath $inventoryPath)
$curatedInventory = @($inventoryRows | ForEach-Object {
    [pscustomobject]@{
        Project = [string]$_.Project
        Test = [regex]::Replace(
            [string]$_.Test,
            '\s*\(.*\)$',
            '(<arguments-redacted>)',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        Outcome = [string]$_.Outcome
        Duration = [string]$_.Duration
    }
})
$curatedInventoryContent = ($curatedInventory | ConvertTo-Csv -NoTypeInformation) -join "`n"

$generatedAt = [DateTimeOffset]::Parse(
    [string]$environment.generatedAtUtc,
    [Globalization.CultureInfo]::InvariantCulture)
$relativeOutput = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    "docs/test-results/$($generatedAt.UtcDateTime.ToString('yyyyMMdd'))-$currentCommit"
}
else {
    $OutputDirectory
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativeOutput))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'docs/test-results'))
$allowedPrefix = $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Published evidence must remain below docs/test-results.'
}
if (Test-Path -LiteralPath $outputPath) {
    throw "Evidence output already exists and will not be overwritten: $outputPath"
}

$forbiddenPatterns = @(
    '(?i)authorization\s*:',
    '(?i)password\s*=',
    '(?i)"?deviceSecret"?\s*[:=]',
    '(?i)"?access_token"?\s*[:=]',
    '(?i)connectionstrings?\s*[:=]',
    '(?i)[A-Z]:\\Users\\',
    '(?i)/home/[^/]+/'
)
foreach ($sourceFile in @($summaryPath, $environmentPath)) {
    $content = Get-Content -LiteralPath $sourceFile -Raw
    foreach ($pattern in $forbiddenPatterns) {
        if ($content -match $pattern) {
            throw "Test evidence contains forbidden environment/secret-like data in $([IO.Path]::GetFileName($sourceFile))."
        }
    }
}
foreach ($pattern in $forbiddenPatterns) {
    if ($curatedInventoryContent -match $pattern) {
        throw 'Curated test inventory contains forbidden environment/secret-like data.'
    }
}

$resolvedRuntimeEvidencePath = $null
if (-not [string]::IsNullOrWhiteSpace($RuntimeEvidencePath)) {
    $resolvedRuntimeEvidencePath = if ([IO.Path]::IsPathRooted($RuntimeEvidencePath)) {
        [IO.Path]::GetFullPath($RuntimeEvidencePath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RuntimeEvidencePath))
    }
    if (-not (Test-Path -LiteralPath $resolvedRuntimeEvidencePath -PathType Leaf)) {
        throw "Runtime E2E evidence was not found: $resolvedRuntimeEvidencePath"
    }

    $runtimeContent = Get-Content -LiteralPath $resolvedRuntimeEvidencePath -Raw
    $runtime = $runtimeContent | ConvertFrom-Json -Depth 30
    if ([int]$runtime.schemaVersion -ne 1 -or
        -not [string]::Equals(
            [string]$runtime.composeProject,
            'post-platform-final',
            [StringComparison]::Ordinal)) {
        throw 'Runtime E2E evidence schema or scoped Compose project is not approved.'
    }
    if (-not [string]::Equals([string]$runtime.result, 'PASS', [StringComparison]::Ordinal)) {
        throw 'Only PASS runtime E2E evidence can be published.'
    }
    $runtimeRevision = [string]$runtime.sourceRevision
    if ($runtimeRevision -notmatch '\A[0-9a-fA-F]{40}\z' -or
        -not [string]::Equals($runtimeRevision, $currentCommitFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime evidence revision '$runtimeRevision' does not match current HEAD '$currentCommit'."
    }
    if ($runtime.workingTreeClean -isnot [bool] -or -not $runtime.workingTreeClean) {
        throw 'Runtime E2E evidence must be produced from a clean working tree.'
    }
    if ($runtime.executedProfiles.connectedEdge -isnot [bool] -or
        $runtime.executedProfiles.observability -isnot [bool] -or
        $runtime.executedProfiles.highAvailability -isnot [bool] -or
        -not $runtime.executedProfiles.connectedEdge -or
        -not $runtime.executedProfiles.observability -or
        -not $runtime.executedProfiles.highAvailability) {
        throw 'Runtime E2E evidence must execute connected-edge, observability, and HA profiles.'
    }
    if ([int]$runtime.performance.count -lt 200 -or
        [int]$runtime.performance.errors -ne 0 -or
        [decimal]$runtime.performance.throughputPerSecond -lt 2) {
        throw 'Runtime E2E evidence did not pass the documented capacity baseline.'
    }
    if ([int]$runtime.sql.authoritativeDatabaseCount -ne 5 -or
        [int]$runtime.sql.tdeEncryptedDatabaseCount -ne 5) {
        throw 'Runtime E2E evidence did not verify all five authoritative/TDE databases.'
    }
    $requiredRuntimeAssertions = @(
        'auth-on-admin-edge-bootstrap',
        'unprovisioned-edge-not-ready',
        'edge-sqlite-encrypted-db-wal-shm-restart-persistence',
        'edge-gateway-core-sync-async',
        'capacity-baseline-two-parcels-per-second',
        'redis-sql-authoritative-stale-outage-two-replicas',
        'operational-result-sql-authoritative-redis-conflict-outage-rebuild',
        's3-sse-signed-header-negative-positive-metadata-sql',
        'kafka-publish-consume-restart-idempotency-redelivery-dlq',
        'sql-tde-five-databases',
        'generated-secrets-absent-from-images',
        'prometheus-loki-grafana-exporter-outage-nonfatal',
        'sql-always-on-sync-failover-query-failback')
    $runtimeAssertions = [Collections.Generic.HashSet[string]]::new(
        [string[]]@($runtime.assertions),
        [StringComparer]::Ordinal)
    foreach ($requiredAssertion in $requiredRuntimeAssertions) {
        if (-not $runtimeAssertions.Contains($requiredAssertion)) {
            throw "Runtime E2E evidence is missing assertion '$requiredAssertion'."
        }
    }
    foreach ($pattern in $forbiddenPatterns) {
        if ($runtimeContent -match $pattern) {
            throw 'Runtime E2E evidence contains forbidden environment/secret-like data.'
        }
    }
}

[IO.Directory]::CreateDirectory($outputPath) | Out-Null
Copy-Item -LiteralPath $summaryPath -Destination (Join-Path $outputPath 'summary.md')
Copy-Item -LiteralPath $environmentPath -Destination (Join-Path $outputPath 'environment.json')
$curatedInventory |
    Export-Csv -LiteralPath (Join-Path $outputPath 'test-inventory.csv') -NoTypeInformation -Encoding utf8
if ($null -ne $resolvedRuntimeEvidencePath) {
    Copy-Item -LiteralPath $resolvedRuntimeEvidencePath -Destination (Join-Path $outputPath 'runtime-e2e.json')
}

$hashLines = foreach ($publishedFile in Get-ChildItem -LiteralPath $outputPath -File) {
    $hash = (Get-FileHash -LiteralPath $publishedFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($publishedFile.Name)"
}
$hashLines | Sort-Object | Set-Content -LiteralPath (Join-Path $outputPath 'checksums.sha256') -Encoding ascii

Write-Output "Published curated test evidence for commit $currentCommit to $relativeOutput."
