[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$PackageName
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$packageRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'tester-packages'))
$dotnetCommand = if ([string]::IsNullOrWhiteSpace($env:POST_DOTNET)) {
    (Get-Command dotnet -ErrorAction Stop).Source
}
else {
    [IO.Path]::GetFullPath($env:POST_DOTNET)
}
if (-not (Test-Path -LiteralPath $dotnetCommand -PathType Leaf)) {
    throw "dotnet executable was not found: $dotnetCommand"
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Description
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent)
    if (-not $resolvedPath.StartsWith($resolvedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be below $resolvedParent."
    }
    return $resolvedPath
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Expected directory is missing: $Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-PortableRuntimeVersion {
    param([Parameter(Mandatory)][string]$DotnetRoot)

    $coreRoot = Join-Path $DotnetRoot 'shared\Microsoft.NETCore.App'
    $aspRoot = Join-Path $DotnetRoot 'shared\Microsoft.AspNetCore.App'
    $versions = @(Get-ChildItem -LiteralPath $coreRoot -Directory |
        Where-Object { $_.Name -match '^10\.' -and (Test-Path -LiteralPath (Join-Path $aspRoot $_.Name) -PathType Container) } |
        Sort-Object { [Version]$_.Name } -Descending)
    if ($versions.Count -eq 0) {
        throw 'A matching .NET 10 Core and ASP.NET Core runtime was not found.'
    }
    return $versions[0].Name
}

$revision = (& git -C $repositoryRoot rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($revision)) {
    throw 'Could not determine the Git revision for the tester package.'
}
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = "post-platform-tester-$revision-win-x64"
}
if ($PackageName -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'PackageName may only use letters, numbers, dot, underscore and hyphen.'
}

$bundlePath = Assert-ChildPath -Path (Join-Path $packageRoot $PackageName) -Parent $packageRoot -Description 'Tester package directory'
$zipPath = Assert-ChildPath -Path (Join-Path $packageRoot ($PackageName + '.zip')) -Parent $packageRoot -Description 'Tester package archive'
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
if (Test-Path -LiteralPath $bundlePath) { Remove-Item -LiteralPath $bundlePath -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Force -Path $bundlePath | Out-Null

try {
    $publishRootRelative = Join-Path 'artifacts\tester-packages' (Join-Path $PackageName 'artifacts\publish')
    & (Join-Path $repositoryRoot 'scripts\publish-local.ps1') `
        -Configuration $Configuration `
        -OutputRoot $publishRootRelative

    $publishPath = Join-Path $bundlePath 'artifacts\publish'
    if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
        throw 'Publish output was not created in the tester package.'
    }

    $dotnetRoot = Split-Path -Parent $dotnetCommand
    $runtimeVersion = Get-PortableRuntimeVersion -DotnetRoot $dotnetRoot
    $runtimePath = Join-Path $bundlePath 'runtime'
    New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null
    foreach ($entry in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $source = Join-Path $dotnetRoot $entry
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $runtimePath -Force
        }
    }
    Copy-DirectoryContents -Source (Join-Path $dotnetRoot 'host') -Destination (Join-Path $runtimePath 'host')
    Copy-DirectoryContents -Source (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$runtimeVersion") `
        -Destination (Join-Path $runtimePath "shared\Microsoft.NETCore.App\$runtimeVersion")
    Copy-DirectoryContents -Source (Join-Path $dotnetRoot "shared\Microsoft.AspNetCore.App\$runtimeVersion") `
        -Destination (Join-Path $runtimePath "shared\Microsoft.AspNetCore.App\$runtimeVersion")

    # The deliverable is Windows x64. Remove only copied non-Windows native
    # runtime assets; managed assemblies and win*/any assets remain intact.
    Get-ChildItem -LiteralPath $publishPath -Directory -Recurse |
        Where-Object { $_.Name -eq 'runtimes' } |
        ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Directory | Where-Object {
                $_.Name -notmatch '^(win|any)'
            } | ForEach-Object {
                $nativePath = Assert-ChildPath -Path $_.FullName -Parent $bundlePath -Description 'Native runtime asset'
                Remove-Item -LiteralPath $nativePath -Recurse -Force
            }
        }

    $docsPath = Join-Path $bundlePath 'docs'
    New-Item -ItemType Directory -Force -Path $docsPath | Out-Null
    foreach ($document in @('README.md', 'deploy.md', 'SECURITY.md', '.env.example')) {
        $source = Join-Path $repositoryRoot $document
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $bundlePath -Force
        }
    }
    foreach ($document in @('TESTING.md', 'TESTER-HANDOFF.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs\$document") -Destination $docsPath -Force
    }
    Copy-DirectoryContents -Source (Join-Path $repositoryRoot 'docs\api') -Destination (Join-Path $docsPath 'api')
    Copy-DirectoryContents -Source (Join-Path $repositoryRoot 'docs\diagrams') -Destination (Join-Path $docsPath 'diagrams')
    Copy-DirectoryContents -Source (Join-Path $repositoryRoot 'docs\test-results') -Destination (Join-Path $docsPath 'test-results')

    $scriptsPath = Join-Path $bundlePath 'scripts'
    New-Item -ItemType Directory -Force -Path $scriptsPath | Out-Null
    foreach ($scriptName in @('start-published-local.ps1', 'stop-published-local.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts\$scriptName") -Destination $scriptsPath -Force
    }
    Copy-DirectoryContents -Source (Join-Path $repositoryRoot 'scripts\tester') -Destination (Join-Path $scriptsPath 'tester')

    $environmentPath = Join-Path $docsPath 'api\post-platform.postman_environment.tester.json'
    $environment = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\api\post-platform.postman_environment.example.json') -Raw |
        ConvertFrom-Json -Depth 32
    $environment.id = '9b76dc5a-1a8a-43aa-a66a-3edb6f635624'
    $environment.name = 'Post Platform - Tester local Mock'
    foreach ($item in $environment.values) {
        if ($item.key -eq 'coreBase') { $item.value = 'http://127.0.0.1:7088' }
        if ($item.key -eq 'edgeBase') { $item.value = 'http://127.0.0.1:7080' }
        if ($item.key -eq 'edgeId') { $item.value = '33333333-3333-4333-8333-333333333333' }
        if ($item.key -eq 'centerCode') { $item.value = '12345' }
    }
    Write-Utf8File -Path $environmentPath -Content ($environment | ConvertTo-Json -Depth 32)

    $manifest = [ordered]@{
        packageName = $PackageName
        sourceRevision = $revision
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        operatingSystem = 'Windows x64'
        runtime = ".NET $runtimeVersion portable runtime"
        profile = 'Mock by default; LocalConnected prompts for destination OAuth credentials and does not save them.'
        syntheticFixture = [ordered]@{
            centerCode = '12345'
            deviceId = 'tester-device'
            logicalDeviceId = 'TESTER-01'
            smokeScenario = 'authenticate, inbound, destination assignment, bag close, dispatch close'
        }
        deployables = @(
            'Post.Core.Gateway', 'Post.Core.Identity.Api', 'Post.Core.DataIngestion.Api',
            'Post.Core.ParcelLifecycle.Api', 'Post.Core.DeviceConfig.Api', 'Post.Core.ObjectStorage.Api',
            'Post.Core.EdgeHealth.Api', 'Post.Core.OperationalResult.Api', 'Post.Edge.Api', 'Post.DeviceSimulator')
    }
    Write-Utf8File -Path (Join-Path $bundlePath 'VERSION.json') -Content ($manifest | ConvertTo-Json -Depth 16)

    $checksumPath = Join-Path $bundlePath 'checksums.sha256'
    $checksums = Get-ChildItem -LiteralPath $bundlePath -File -Recurse |
        Where-Object { $_.FullName -ne $checksumPath } |
        Sort-Object FullName |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $relative = [IO.Path]::GetRelativePath($bundlePath, $_.FullName).Replace('\', '/')
            "$hash  $relative"
        }
    Write-Utf8File -Path $checksumPath -Content (($checksums -join [Environment]::NewLine) + [Environment]::NewLine)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $bundlePath,
        $zipPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8File -Path ($zipPath + '.sha256') -Content "$zipHash  $([IO.Path]::GetFileName($zipPath))`n"

    Write-Output "Tester bundle: $bundlePath"
    Write-Output "Tester archive: $zipPath"
    Write-Output "Archive SHA-256: $zipHash"
}
catch {
    if (Test-Path -LiteralPath $bundlePath) {
        Remove-Item -LiteralPath $bundlePath -Recurse -Force
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    throw
}
