[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = 'artifacts/publish'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repositoryRoot $OutputRoot
$dotnetCommand = if ($env:POST_DOTNET) { $env:POST_DOTNET } else { 'dotnet' }
$hosts = @(
    'src/Core/Post.Core.Gateway/Post.Core.Gateway.csproj',
    'src/Core/Post.Core.Identity.Api/Post.Core.Identity.Api.csproj',
    'src/Core/Post.Core.DataIngestion.Api/Post.Core.DataIngestion.Api.csproj',
    'src/Core/Post.Core.ParcelLifecycle.Api/Post.Core.ParcelLifecycle.Api.csproj',
    'src/Core/Post.Core.DeviceConfig.Api/Post.Core.DeviceConfig.Api.csproj',
    'src/Core/Post.Core.ObjectStorage.Api/Post.Core.ObjectStorage.Api.csproj',
    'src/Core/Post.Core.EdgeHealth.Api/Post.Core.EdgeHealth.Api.csproj',
    'src/Core/Post.Core.OperationalResult.Api/Post.Core.OperationalResult.Api.csproj',
    'src/Edge/Post.Edge.Api/Post.Edge.Api.csproj',
    'tools/Post.DeviceSimulator/Post.DeviceSimulator.csproj'
)

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
& $dotnetCommand restore (Join-Path $repositoryRoot 'Post.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked NuGet restore failed before publish.' }
foreach ($deployable in $hosts) {
    $projectPath = Join-Path $repositoryRoot $deployable
    $projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $projectOutput = Join-Path $outputPath $projectName
    & $dotnetCommand publish $projectPath --configuration $Configuration --output $projectOutput --nologo --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $projectName." }
}

Write-Output "Published $($hosts.Count) deployables to $outputPath."
