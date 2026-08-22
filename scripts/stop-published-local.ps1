[CmdletBinding()]
param(
    [string]$RuntimeRoot = 'artifacts/local-runtime'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runtimePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RuntimeRoot))
$allowedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $runtimePath.StartsWith($allowedRuntimeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RuntimeRoot must resolve below the repository artifacts directory.'
}

$statePath = Join-Path $runtimePath 'processes.json'
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    Write-Output 'No published local-process state file exists.'
    return
}

$entries = @(Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 10)
$stopped = 0
foreach ($entry in $entries) {
    $process = Get-Process -Id ([int]$entry.Pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) { continue }

    $expectedStart = [DateTimeOffset]::Parse(
        [string]$entry.StartedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture)
    $actualStart = $process.StartTime.ToUniversalTime()
    if ([math]::Abs(($actualStart - $expectedStart.UtcDateTime).TotalSeconds) -gt 1) {
        throw "PID $($entry.Pid) was reused; refusing to stop an unrelated process."
    }

    $command = Get-CimInstance Win32_Process -Filter "ProcessId = $($entry.Pid)"
    $expectedDll = [IO.Path]::GetFileName([string]$entry.DllPath)
    if ($null -eq $command -or $command.CommandLine -notlike "*$expectedDll*") {
        throw "PID $($entry.Pid) does not match $expectedDll; refusing to stop it."
    }

    Stop-Process -Id ([int]$entry.Pid)
    $stopped++
}

Remove-Item -LiteralPath $statePath
Write-Output "Stopped $stopped published local hosts. Runtime databases and logs were retained at $runtimePath."
