[CmdletBinding()]
param(
    [ValidateSet('Mock', 'LocalConnected')]
    [string]$Profile = 'Mock',
    [switch]$ResetData,
    [ValidateRange(5, 120)]
    [int]$ReadinessTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$bundleRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtimeDotnet = Join-Path $bundleRoot 'runtime\dotnet.exe'
if (-not (Test-Path -LiteralPath $runtimeDotnet -PathType Leaf)) {
    throw "Portable .NET runtime is missing: $runtimeDotnet"
}

$runtimeRoot = 'artifacts/tester-runtime'
$secretRoot = Join-Path $bundleRoot 'artifacts\tester-local-secrets'
New-Item -ItemType Directory -Force -Path $secretRoot | Out-Null

if ($ResetData) {
    & (Join-Path $PSScriptRoot 'Reset-TesterData.ps1')
}

function Get-OrCreateProtectedRandomValue {
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidateRange(32, 256)][int]$ByteCount = 48
    )

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $secure = Import-Clixml -LiteralPath $Path
        if ($secure -isnot [Security.SecureString]) {
            throw "Protected local value has an invalid format: $Path"
        }
        return $secure
    }

    $bytes = [byte[]]::new($ByteCount)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $secure = ConvertTo-SecureString -String ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
    $secure | Export-Clixml -LiteralPath $Path
    return $secure
}

function ConvertTo-TransientPlainText {
    param([Parameter(Mandatory)][Security.SecureString]$Value)
    return [Net.NetworkCredential]::new('', $Value).Password
}

$storageKey = ConvertTo-TransientPlainText (Get-OrCreateProtectedRandomValue `
    -Path (Join-Path $secretRoot 'edge-storage-key.clixml'))
$deviceToken = ConvertTo-TransientPlainText (Get-OrCreateProtectedRandomValue `
    -Path (Join-Path $secretRoot 'device-token.clixml') -ByteCount 32)

$transientVariables = @(
    'POST_DOTNET',
    'POST_EDGE_SQLITE_KEY',
    'Edge__Mock__CenterCode',
    'Edge__Mock__ConfigurationVersion',
    'Edge__Mock__Devices__0__DeviceId',
    'Edge__Mock__Devices__0__LogicalId',
    'Edge__Mock__Devices__0__Token',
    'Edge__Mock__Devices__0__IsActive',
    'Edge__DestinationApi__Username',
    'Edge__DestinationApi__Password')

try {
    $env:POST_DOTNET = $runtimeDotnet
    $env:POST_EDGE_SQLITE_KEY = $storageKey
    $env:Edge__Mock__CenterCode = '12345'
    $env:Edge__Mock__ConfigurationVersion = '1'
    $env:Edge__Mock__Devices__0__DeviceId = 'tester-device'
    $env:Edge__Mock__Devices__0__LogicalId = 'TESTER-01'
    $env:Edge__Mock__Devices__0__Token = $deviceToken
    $env:Edge__Mock__Devices__0__IsActive = 'true'

    if ($Profile -eq 'LocalConnected') {
        $destinationUsername = Read-Host 'Destination OAuth username'
        $destinationPassword = Read-Host 'Destination OAuth password' -AsSecureString
        if ([string]::IsNullOrWhiteSpace($destinationUsername) -or $destinationPassword.Length -eq 0) {
            throw 'LocalConnected requires a non-empty destination OAuth username and password.'
        }
        $env:Edge__DestinationApi__Username = $destinationUsername
        $env:Edge__DestinationApi__Password = ConvertTo-TransientPlainText $destinationPassword
    }

    & (Join-Path $bundleRoot 'scripts\start-published-local.ps1') `
        -EdgeProfile $Profile `
        -PublishRoot 'artifacts/publish' `
        -RuntimeRoot $runtimeRoot `
        -ReadinessTimeoutSeconds $ReadinessTimeoutSeconds `
        -EncryptEdgeStorage
}
finally {
    foreach ($variableName in $transientVariables) {
        Remove-Item -LiteralPath "Env:$variableName" -ErrorAction SilentlyContinue
    }
}

Write-Output 'Tester services are ready. Gateway: http://127.0.0.1:7088; Edge: http://127.0.0.1:7080.'
Write-Output 'Run .\scripts\tester\Run-TesterSmoke.ps1 for the end-to-end synthetic scenario.'
