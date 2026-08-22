[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = "artifacts/migration-checks/$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = if ($env:POST_DOTNET) { $env:POST_DOTNET } else { 'dotnet' }
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

& $dotnetCommand tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE." }

if (-not $NoBuild) {
    & $dotnetCommand build (Join-Path $repositoryRoot 'Post.sln') `
        --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
}

$contexts = @(
    [pscustomobject]@{
        Name = 'Edge'
        Context = 'EdgeDbContext'
        Project = 'src/Edge/Post.Edge.Infrastructure/Post.Edge.Infrastructure.csproj'
        Idempotent = $false
    },
    [pscustomobject]@{
        Name = 'ParcelLifecycle'
        Context = 'ParcelLifecycleDbContext'
        Project = 'src/Core/Post.Core.Infrastructure/Post.Core.Infrastructure.csproj'
        Idempotent = $true
    },
    [pscustomobject]@{
        Name = 'DeviceConfig'
        Context = 'DeviceConfigDbContext'
        Project = 'src/Core/Post.Core.Infrastructure/Post.Core.Infrastructure.csproj'
        Idempotent = $true
    },
    [pscustomobject]@{
        Name = 'EdgeHealth'
        Context = 'EdgeHealthDbContext'
        Project = 'src/Core/Post.Core.Infrastructure/Post.Core.Infrastructure.csproj'
        Idempotent = $true
    },
    [pscustomobject]@{
        Name = 'OperationalResult'
        Context = 'OperationalResultDbContext'
        Project = 'src/Core/Post.Core.Infrastructure/Post.Core.Infrastructure.csproj'
        Idempotent = $true
    },
    [pscustomobject]@{
        Name = 'Identity'
        Context = 'IdentityDbContext'
        Project = 'src/Core/Post.Core.Infrastructure/Post.Core.Infrastructure.csproj'
        Idempotent = $true
    }
)

$results = [Collections.Generic.List[object]]::new()
foreach ($item in $contexts) {
    $projectPath = Join-Path $repositoryRoot $item.Project
    $pendingArguments = @(
        'ef', 'migrations', 'has-pending-model-changes',
        '--project', $projectPath,
        '--context', $item.Context,
        '--configuration', $Configuration,
        '--no-build'
    )
    & $dotnetCommand @pendingArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Pending-model verification failed for $($item.Context)."
    }

    $scriptPath = Join-Path $outputPath ($item.Name + '.sql')
    $scriptArguments = @(
        'ef', 'migrations', 'script',
        '--project', $projectPath,
        '--context', $item.Context,
        '--configuration', $Configuration,
        '--no-build',
        '--output', $scriptPath
    )
    if ($item.Idempotent) { $scriptArguments += '--idempotent' }
    & $dotnetCommand @scriptArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Migration SQL generation failed for $($item.Context)."
    }
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf) -or
        (Get-Item -LiteralPath $scriptPath).Length -lt 100) {
        throw "Migration SQL for $($item.Context) is missing or unexpectedly empty."
    }

    $sql = Get-Content -LiteralPath $scriptPath -Raw
    if ($sql -notmatch '(?i)CREATE\s+TABLE|ALTER\s+TABLE|CREATE\s+(UNIQUE\s+)?INDEX') {
        throw "Migration SQL for $($item.Context) contains no schema operation."
    }

    $results.Add([pscustomobject]@{
        Context = $item.Context
        ProviderMode = if ($item.Idempotent) { 'SQL Server idempotent' } else { 'SQLite ordered' }
        Bytes = (Get-Item -LiteralPath $scriptPath).Length
        Sha256 = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
        File = [IO.Path]::GetRelativePath($repositoryRoot, $scriptPath).Replace('\', '/')
    })
}

$rows = ($results | ForEach-Object {
    "| $($_.Context) | $($_.ProviderMode) | $($_.Bytes) | $($_.Sha256) |"
}) -join [Environment]::NewLine
$summary = @"
# Migration verification

- Generated UTC: $([DateTimeOffset]::UtcNow.ToString('O'))
- Configuration: $Configuration
- Contexts: $($results.Count)
- Pending model changes: none

| DbContext | Script mode | Bytes | SHA-256 |
| --- | --- | ---: | --- |
$rows
"@
$summary | Set-Content -LiteralPath (Join-Path $outputPath 'summary.md') -Encoding utf8

Write-Output "Verified $($results.Count) DbContexts; no pending model changes. SQL: $outputPath"
