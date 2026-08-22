[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$bundleRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'artifacts'))
$target = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'tester-runtime'))
if (-not $target.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to reset a path outside this tester bundle artifacts directory.'
}

$state = Join-Path $target 'processes.json'
if (Test-Path -LiteralPath $state -PathType Leaf) {
    throw 'Tester processes are still registered. Run .\scripts\tester\Stop-Tester.ps1 first.'
}

if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $target | Out-Null
Write-Output "Reset synthetic runtime data at $target. Protected local keys were retained."
