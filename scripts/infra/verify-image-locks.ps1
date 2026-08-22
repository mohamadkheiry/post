[CmdletBinding()]
param(
    [string]$ComposeFile = (Join-Path $PSScriptRoot '..\..\compose.full.yaml'),
    [string]$Dockerfile = (Join-Path $PSScriptRoot '..\..\Dockerfile')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedCompose = [System.IO.Path]::GetFullPath($ComposeFile)
if (-not (Test-Path -LiteralPath $resolvedCompose -PathType Leaf)) {
    throw "Compose file was not found: $resolvedCompose"
}
$resolvedDockerfile = [System.IO.Path]::GetFullPath($Dockerfile)
if (-not (Test-Path -LiteralPath $resolvedDockerfile -PathType Leaf)) {
    throw "Dockerfile was not found: $resolvedDockerfile"
}

$matches = [System.Collections.Generic.List[object]]::new()
foreach ($line in [System.IO.File]::ReadLines($resolvedCompose)) {
    $match = [regex]::Match(
        $line,
        '^\s*image:\s*(?<reference>[^\s@]+)@(?<digest>sha256:[a-f0-9]{64})\s*$')
    if ($match.Success) {
        $matches.Add([pscustomobject]@{
            Reference = $match.Groups['reference'].Value
            Digest = $match.Groups['digest'].Value
        })
    }
}

foreach ($line in [System.IO.File]::ReadLines($resolvedDockerfile)) {
    $match = [regex]::Match(
        $line,
        '^ARG\s+DOTNET_(?:SDK|RUNTIME)_VERSION=(?<reference>[^\s@]+)@(?<digest>sha256:[a-f0-9]{64})\s*$')
    if ($match.Success) {
        $reference = if ($line.Contains('SDK_VERSION', [StringComparison]::Ordinal)) {
            "mcr.microsoft.com/dotnet/sdk:$($match.Groups['reference'].Value)"
        } else {
            "mcr.microsoft.com/dotnet/aspnet:$($match.Groups['reference'].Value)"
        }
        $matches.Add([pscustomobject]@{
            Reference = $reference
            Digest = $match.Groups['digest'].Value
        })
    }
}

$unique = @($matches | Sort-Object Reference, Digest -Unique)
if ($unique.Count -lt 11) {
    throw "Expected at least eleven digest-pinned upstream/runtime images; found $($unique.Count)."
}

if ($unique.Reference -match ':latest$') {
    throw 'Mutable latest image tags are forbidden in the full-development topology.'
}

foreach ($image in $unique) {
    $manifestJson = (& docker buildx imagetools inspect --format '{{json .Manifest}}' $image.Reference 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($manifestJson)) {
        throw "Could not resolve upstream manifest for $($image.Reference)."
    }
    try {
        $actual = [string](($manifestJson | ConvertFrom-Json -Depth 20).digest)
    } catch {
        throw "Could not parse the upstream manifest for $($image.Reference)."
    }
    if ([string]::IsNullOrWhiteSpace($actual)) {
        throw "The upstream manifest for $($image.Reference) did not include a digest."
    }

    if (-not [string]::Equals($actual, $image.Digest, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Image lock mismatch for $($image.Reference): the tag no longer resolves to the committed digest."
    }
}

Write-Output "Verified $($unique.Count) immutable upstream image locks."
