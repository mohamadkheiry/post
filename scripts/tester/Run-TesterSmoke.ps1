[CmdletBinding()]
param(
    [ValidateRange(10, 300)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$bundleRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtimeDotnet = Join-Path $bundleRoot 'runtime\dotnet.exe'
$secretPath = Join-Path $bundleRoot 'artifacts\tester-local-secrets\device-token.clixml'
$simulatorPath = Join-Path $bundleRoot 'artifacts\publish\Post.DeviceSimulator\Post.DeviceSimulator.dll'
if (-not (Test-Path -LiteralPath $runtimeDotnet -PathType Leaf) -or
    -not (Test-Path -LiteralPath $secretPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $simulatorPath -PathType Leaf)) {
    throw 'The bundle has not been started correctly. Start it first with Start-TesterLocal.ps1.'
}

$deviceToken = [Net.NetworkCredential]::new('', (Import-Clixml -LiteralPath $secretPath)).Password
try {
    Invoke-RestMethod -Uri 'http://127.0.0.1:7088/health/ready' -TimeoutSec 10 | Out-Null
    Invoke-RestMethod -Uri 'http://127.0.0.1:7080/health/ready' -TimeoutSec 10 | Out-Null
    $env:POST_DEVICE_TOKEN = $deviceToken
    $env:POST_DEVICE_URL = 'http://127.0.0.1:7080/ws/device'
    $env:POST_DEVICE_ID = 'tester-device'
    & $runtimeDotnet $simulatorPath --scenario full --timeout-seconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) { throw "Device simulator failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath 'Env:POST_DEVICE_TOKEN' -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'Env:POST_DEVICE_URL' -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'Env:POST_DEVICE_ID' -ErrorAction SilentlyContinue
}

Write-Output 'Synthetic end-to-end smoke test passed: auth, inbound, destination, bag and dispatch.'
