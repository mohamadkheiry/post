[CmdletBinding()]
param(
    [ValidateSet('Mock', 'LocalConnected')]
    [string]$EdgeProfile = 'Mock',
    [string]$PublishRoot = 'artifacts/publish',
    [string]$RuntimeRoot = 'artifacts/local-runtime',
    [ValidateRange(5, 120)]
    [int]$ReadinessTimeoutSeconds = 45,
    [switch]$EncryptEdgeStorage
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PublishRoot))
$runtimePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RuntimeRoot))
$allowedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $runtimePath.StartsWith($allowedRuntimeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RuntimeRoot must resolve below the repository artifacts directory.'
}

$statePath = Join-Path $runtimePath 'processes.json'
if (Test-Path -LiteralPath $statePath) {
    throw "A local-process state file already exists. Stop that instance first: $statePath"
}

$dotnetCommand = if ($env:POST_DOTNET) {
    [IO.Path]::GetFullPath($env:POST_DOTNET)
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

function Get-ValidatedStorageKey {
    param([Parameter(Mandatory)][string]$VariableName)

    $value = [Environment]::GetEnvironmentVariable($VariableName)
    if ([string]::IsNullOrEmpty($value)) { return $null }
    if ($value.Length -lt 32 -or $value.Length -gt 256 -or $value.ToCharArray().Where({ [char]::IsControl($_) }).Count -gt 0) {
        throw "The external Edge storage key in '$VariableName' is invalid."
    }
    return $value
}

$edgeStorageKey = Get-ValidatedStorageKey -VariableName 'POST_EDGE_SQLITE_KEY'
$edgePreviousStorageKey = Get-ValidatedStorageKey -VariableName 'POST_EDGE_SQLITE_PREVIOUS_KEY'
if ($EncryptEdgeStorage -and [string]::IsNullOrEmpty($edgeStorageKey)) {
    throw 'EncryptEdgeStorage requires POST_EDGE_SQLITE_KEY in the caller environment.'
}
if (-not $EncryptEdgeStorage -and -not [string]::IsNullOrEmpty($edgePreviousStorageKey)) {
    throw 'POST_EDGE_SQLITE_PREVIOUS_KEY is only valid together with EncryptEdgeStorage.'
}

$services = @(
    [pscustomobject]@{ Name = 'identity'; Project = 'Post.Core.Identity.Api'; Port = 7101; Kind = 'core' },
    [pscustomobject]@{ Name = 'data-ingestion'; Project = 'Post.Core.DataIngestion.Api'; Port = 7102; Kind = 'core' },
    [pscustomobject]@{ Name = 'parcel-lifecycle'; Project = 'Post.Core.ParcelLifecycle.Api'; Port = 7103; Kind = 'core' },
    [pscustomobject]@{ Name = 'device-config'; Project = 'Post.Core.DeviceConfig.Api'; Port = 7104; Kind = 'core' },
    [pscustomobject]@{ Name = 'object-storage'; Project = 'Post.Core.ObjectStorage.Api'; Port = 7105; Kind = 'core' },
    [pscustomobject]@{ Name = 'edge-health'; Project = 'Post.Core.EdgeHealth.Api'; Port = 7106; Kind = 'core' },
    [pscustomobject]@{ Name = 'operational-result'; Project = 'Post.Core.OperationalResult.Api'; Port = 7107; Kind = 'core' },
    [pscustomobject]@{ Name = 'gateway'; Project = 'Post.Core.Gateway'; Port = 7088; Kind = 'gateway' },
    [pscustomobject]@{ Name = 'edge'; Project = 'Post.Edge.Api'; Port = 7080; Kind = 'edge' }
)

New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null
$logPath = Join-Path $runtimePath 'logs'
$coreDataPath = Join-Path $runtimePath 'core-data'
New-Item -ItemType Directory -Force -Path $logPath, $coreDataPath | Out-Null

$started = [Collections.Generic.List[object]]::new()
try {
    foreach ($service in $services) {
        $workingDirectory = Join-Path $publishPath $service.Project
        $dllPath = Join-Path $workingDirectory ($service.Project + '.dll')
        if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
            throw "Published host is missing: $dllPath. Run scripts/publish-local.ps1 first."
        }

        $processEnvironment = @{
            ASPNETCORE_ENVIRONMENT = 'Development'
            ASPNETCORE_URLS = "http://127.0.0.1:$($service.Port)"
        }
        if ($service.Kind -eq 'core') {
            $processEnvironment.Api__AuthenticationEnabled = 'false'
            $processEnvironment.Api__DevelopmentIdentity__Enabled = 'true'
            $processEnvironment.Api__DevelopmentIdentity__EdgeId = '33333333-3333-4333-8333-333333333333'
            $processEnvironment.Api__DevelopmentIdentity__ExchangeCenterCode = '12345'
            $processEnvironment.Core__Database__Provider = 'Sqlite'
            $processEnvironment.Core__Database__Directory = $coreDataPath
            $processEnvironment.Core__Database__ApplyMigrations = 'false'
            $processEnvironment.Core__Outbox__Enabled = 'false'
        }
        elseif ($service.Kind -eq 'gateway') {
            $processEnvironment.Auth__AuthenticationEnabled = 'false'
        }
        else {
            $processEnvironment.Api__AuthenticationEnabled = 'false'
            $processEnvironment.Edge__Integrations__Profile = $EdgeProfile
            $processEnvironment.Edge__Storage__ConnectionString =
                'Data Source=' + (Join-Path $runtimePath 'edge.db')
            $processEnvironment.Edge__Storage__ApplyMigrationsOnStartup = 'true'
            $processEnvironment.Edge__Storage__MinimumAvailableDiskBytes = '0'
            $processEnvironment.Edge__Storage__EncryptionEnabled = $EncryptEdgeStorage.ToString().ToLowerInvariant()
            $processEnvironment.Edge__Storage__BackupDirectory = (Join-Path $runtimePath 'edge-backups')
            $processEnvironment.Edge__Security__AllowUnboundDevices = 'true'
            $processEnvironment.Edge__LocalAdmin__AuthenticationEnabled = 'false'
            # Tester launchers may supply a synthetic Mock fixture or an explicitly
            # entered LocalConnected OAuth credential.  Forward only this small
            # allow-list; never copy the caller's entire environment into a host.
            foreach ($variableName in @(
                'Edge__Mock__CenterCode',
                'Edge__Mock__ConfigurationVersion',
                'Edge__Mock__Devices__0__DeviceId',
                'Edge__Mock__Devices__0__LogicalId',
                'Edge__Mock__Devices__0__Token',
                'Edge__Mock__Devices__0__IsActive',
                'Edge__DestinationApi__Username',
                'Edge__DestinationApi__Password')) {
                $value = [Environment]::GetEnvironmentVariable($variableName)
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    $processEnvironment[$variableName] = $value
                }
            }
            if ($EncryptEdgeStorage) {
                $processEnvironment.POST_EDGE_SQLITE_KEY = $edgeStorageKey
                if (-not [string]::IsNullOrEmpty($edgePreviousStorageKey)) {
                    $processEnvironment.POST_EDGE_SQLITE_PREVIOUS_KEY = $edgePreviousStorageKey
                }
            }
        }

        $stdout = Join-Path $logPath ($service.Name + '.stdout.log')
        $stderr = Join-Path $logPath ($service.Name + '.stderr.log')
        $quotedDllArgument = '"' + $dllPath + '"'
        $process = Start-Process `
            -FilePath $dotnetCommand `
            -ArgumentList @($quotedDllArgument) `
            -WorkingDirectory $workingDirectory `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -Environment $processEnvironment `
            -PassThru
        $started.Add([pscustomobject]@{
            Name = $service.Name
            Pid = $process.Id
            DllPath = $dllPath
            StartedAtUtc = $process.StartTime.ToUniversalTime().ToString('O')
            HealthUrl = "http://127.0.0.1:$($service.Port)/health/ready"
        })
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ReadinessTimeoutSeconds)
    $pending = [Collections.Generic.HashSet[string]]::new(
        [string[]]$started.Name,
        [StringComparer]::Ordinal)
    while ($pending.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $deadline) {
        foreach ($entry in $started) {
            if (-not $pending.Contains($entry.Name)) { continue }
            $process = Get-Process -Id $entry.Pid -ErrorAction SilentlyContinue
            if ($null -eq $process) {
                throw "Published host '$($entry.Name)' exited before readiness. Inspect $logPath."
            }

            try {
                $response = Invoke-WebRequest -Uri $entry.HealthUrl -TimeoutSec 2 -SkipHttpErrorCheck
                if ($response.StatusCode -eq 200) { $null = $pending.Remove($entry.Name) }
            }
            catch [System.Net.Http.HttpRequestException] {
                # The host is still starting.
            }
            catch [System.Threading.Tasks.TaskCanceledException] {
                # The readiness probe timed out while the host was still starting.
            }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 250 }
    }

    if ($pending.Count -gt 0) {
        throw "Readiness timed out for: $([string]::Join(', ', $pending)). Inspect $logPath."
    }

    $started | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
    Write-Output "Started $($started.Count) published hosts. Gateway: http://127.0.0.1:7088; Edge: http://127.0.0.1:7080."
    Write-Output "Process state: $statePath"
}
catch {
    foreach ($entry in $started) {
        Stop-Process -Id $entry.Pid -ErrorAction SilentlyContinue
    }
    throw
}
