[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $bundleRoot 'scripts\stop-published-local.ps1') -RuntimeRoot 'artifacts/tester-runtime'
