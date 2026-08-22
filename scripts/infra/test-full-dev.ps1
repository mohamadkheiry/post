[CmdletBinding()]
param(
    [ValidateSet('post-platform-final')]
    [string]$ProjectName = 'post-platform-final',
    [switch]$ResetData,
    [switch]$IncludeObservability,
    [switch]$IncludeHa,
    [switch]$KeepRunning,
    [switch]$SkipBuild,
    [switch]$RequireCleanRevision
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$composeBase = @(
    '-p', $ProjectName,
    '-f', (Join-Path $repositoryRoot 'compose.yaml'),
    '-f', (Join-Path $repositoryRoot 'compose.full.yaml'))
$secretDirectory = Join-Path $repositoryRoot '.secrets\full-dev'
$gatewayBase = 'http://127.0.0.1:7088'
$edgeBase = 'http://127.0.0.1:7080'
$edgeId = '33333333-3333-4333-8333-333333333333'
$centerCode = '12345'
$edgeLogicalCode = 'FULLDEV-EDGE-01'
$runId = [Guid]::NewGuid().ToString('N')
$started = [System.Diagnostics.Stopwatch]::StartNew()
$succeeded = $false
$performanceEvidence = $null
$sourceRevision = $null
$workingTreeClean = $false

function Write-Phase {
    param([Parameter(Mandatory)][string]$Message)
    Write-Output ("[full-dev] {0}" -f $Message)
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$Capture
    )

    $output = & docker compose @composeBase @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The scoped Docker Compose command failed (exit $LASTEXITCODE)."
    }

    if ($Capture) {
        return ($output -join "`n")
    }

    if ($output) {
        $output | Write-Output
    }
}

function Read-PrivateEnvFile {
    param([Parameter(Mandatory)][string]$Path)

    $values = [ordered]@{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) {
            continue
        }

        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            throw "A malformed line exists in the private environment file '$Path'."
        }

        $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
    }

    return $values
}

function Set-PrivateEnvValue {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    if ($Value.Contains("`r", [StringComparison]::Ordinal) -or
        $Value.Contains("`n", [StringComparison]::Ordinal)) {
        throw 'A private environment value cannot contain a line break.'
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $replaced = $false
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line.StartsWith("$Name=", [StringComparison]::Ordinal)) {
            $lines.Add("$Name=$Value")
            $replaced = $true
        } else {
            $lines.Add($line)
        }
    }
    if (-not $replaced) {
        $lines.Add("$Name=$Value")
    }

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllLines(
            $temporary,
            $lines,
            [System.Text.UTF8Encoding]::new($false))
        if (-not $IsWindows) {
            [System.IO.File]::SetUnixFileMode(
                $temporary,
                [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
        }
        [System.IO.File]::Move($temporary, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Invoke-JsonRequest {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [object]$Body,
        [hashtable]$Headers = @{},
        [Parameter(Mandatory)][int[]]$ExpectedStatus
    )

    $request = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        SkipHttpErrorCheck = $true
        TimeoutSec = 45
    }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }

    $response = Invoke-WebRequest @request
    if ($ExpectedStatus -notcontains [int]$response.StatusCode) {
        throw "HTTP $Method $([Uri]$Uri).AbsolutePath returned $([int]$response.StatusCode)."
    }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
        $parsed = $response.Content | ConvertFrom-Json -Depth 30
    }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $parsed }
}

function Wait-HttpStatus {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [int]$ExpectedStatus = 200,
        [int]$TimeoutSeconds = 180
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -SkipHttpErrorCheck -TimeoutSec 5
            if ([int]$response.StatusCode -eq $ExpectedStatus) {
                return
            }
        } catch {
            # Startup transport failures are expected until the bounded deadline.
        }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "HTTP readiness did not reach $ExpectedStatus before timeout."
}

function Invoke-SqlScalar {
    param(
        [Parameter(Mandatory)][string]$Query,
        [ValidateSet('sql-primary', 'sql-secondary')]
        [string]$Service = 'sql-primary'
    )

    $output = Invoke-Compose -Capture -Arguments @(
        'exec', '-T', '-e', "POST_SQL_QUERY=$Query", $Service,
        '/bin/bash', '-ec',
        'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -b -C -S localhost -U sa -h -1 -W -Q "$POST_SQL_QUERY"')
    return $output.Trim()
}

function Wait-SqlScalar {
    param(
        [Parameter(Mandatory)][string]$Query,
        [Parameter(Mandatory)][string]$Expected,
        [ValidateSet('sql-primary', 'sql-secondary')]
        [string]$Service = 'sql-primary',
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $actual = Invoke-SqlScalar -Query $Query -Service $Service
        if ([string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
            return
        }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "SQL assertion did not reach the expected scalar value before timeout."
}

function Invoke-ToolShell {
    param(
        [Parameter(Mandatory)][string]$Script,
        [hashtable]$Environment = @{}
    )

    $arguments = @(
        'run', '--rm', '--network', "$ProjectName`_post-backend",
        '--read-only', '--tmpfs', '/tmp:size=16m,mode=1777',
        '--security-opt', 'no-new-privileges:true', '--cap-drop', 'ALL',
        '--entrypoint', '/bin/sh')
    foreach ($entry in $Environment.GetEnumerator()) {
        $arguments += @('-e', "$($entry.Key)=$($entry.Value)")
    }
    $arguments += @(
        'curlimages/curl:8.16.0@sha256:463eaf6072688fe96ac64fa623fe73e1dbe25d8ad6c34404a669ad3ce1f104b6',
        '-ec', $Script)
    $output = & docker @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'The isolated E2E HTTP client command failed.'
    }
    return ($output -join "`n")
}

function Assert-EncryptedEdgeStorage {
    param([Parameter(Mandatory)][string]$PlaintextSentinel)

    $edgeContainer = "$ProjectName-edge-1"
    & docker run --rm --network none --read-only --tmpfs '/tmp:size=8m,mode=1777' `
        --security-opt 'no-new-privileges:true' --cap-drop ALL --user '0:0' `
        --volumes-from "$edgeContainer`:ro" -e "POST_EDGE_SENTINEL=$PlaintextSentinel" `
        --entrypoint /bin/sh `
        'curlimages/curl:8.16.0@sha256:463eaf6072688fe96ac64fa623fe73e1dbe25d8ad6c34404a669ad3ce1f104b6' `
        -ec @'
test -s /data/edge/edge.db
test "$(head -c 15 /data/edge/edge.db 2>/dev/null)" != "SQLite format 3"
for file in /data/edge/edge.db /data/edge/edge.db-wal /data/edge/edge.db-shm; do
  if test -f "$file" && grep -a -F -q "$POST_EDGE_SENTINEL" "$file"; then exit 42; fi
done
'@ *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'The Edge database/header/WAL/SHM encryption-at-rest assertion failed.'
    }
}

function Get-ScopedResourceSnapshot {
    $containerIds = @(& docker ps -q --filter "label=com.docker.compose.project=$ProjectName")
    if ($LASTEXITCODE -ne 0) { throw 'The scoped container inventory could not be read.' }

    $snapshot = foreach ($containerId in $containerIds) {
        $json = & docker stats --no-stream --format '{{json .}}' $containerId
        if ($LASTEXITCODE -ne 0) { throw 'A scoped Docker resource sample failed.' }
        $stat = $json | ConvertFrom-Json -Depth 10
        [ordered]@{
            service = [string]$stat.Name
            cpu = [string]$stat.CPUPerc
            memory = [string]$stat.MemUsage
            networkIo = [string]$stat.NetIO
            blockIo = [string]$stat.BlockIO
            pids = [string]$stat.PIDs
        }
    }
    return @($snapshot | Sort-Object { $_.service })
}

function Get-DotNetExecutable {
    if (-not [string]::IsNullOrWhiteSpace($env:POST_DOTNET) -and
        (Test-Path -LiteralPath $env:POST_DOTNET -PathType Leaf)) {
        return $env:POST_DOTNET
    }

    if (-not [string]::IsNullOrWhiteSpace($env:POST_DOTNET_EXE) -and
        (Test-Path -LiteralPath $env:POST_DOTNET_EXE -PathType Leaf)) {
        return $env:POST_DOTNET_EXE
    }

    $local = if ($IsWindows) {
        Join-Path $env:USERPROFILE '.cache\dotnet-sdk-10.0.400\dotnet.exe'
    } else {
        Join-Path $env:HOME '.dotnet\dotnet'
    }
    if (Test-Path -LiteralPath $local -PathType Leaf) {
        return $local
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw 'A .NET 10 SDK executable is required for the real device simulator gate.'
    }
    return $command.Source
}

function Invoke-RpkProduce {
    param(
        [Parameter(Mandatory)][string]$Topic,
        [Parameter(Mandatory)][string]$Payload
    )

    $Payload | & docker compose @composeBase exec -T redpanda rpk topic produce $Topic --brokers=localhost:9092 *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Kafka publish to '$Topic' failed."
    }
}

function Wait-KafkaPayload {
    param(
        [Parameter(Mandatory)][string]$Topic,
        [Parameter(Mandatory)][string]$Marker,
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $payload = Invoke-Compose -Capture -Arguments @(
            'exec', '-T', '-e', "POST_KAFKA_TOPIC=$Topic", 'redpanda', '/bin/bash', '-ec',
            'timeout 3 rpk topic consume "$POST_KAFKA_TOPIC" --brokers=localhost:9092 --offset=start --num=100 --format=%v 2>/dev/null || true')
        if ($payload.Contains($Marker, [StringComparison]::Ordinal)) {
            return $payload
        }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Kafka topic '$Topic' did not contain the expected marker before timeout."
}

function Assert-FinalImagesContainNoSecrets {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary[]]$SecretSets)

    $secretKeyPattern = '(?i)(password|secret|signingkey|connectionstrings?|accesskey|kms|sqlite_key|root_user|token)'
    $secretValues = @($SecretSets | ForEach-Object {
            foreach ($entry in $_.GetEnumerator()) {
                if ([string]$entry.Key -match $secretKeyPattern) { [string]$entry.Value }
            }
        } |
        Where-Object { $_.Length -ge 8 } |
        Sort-Object -Unique)
    $applicationServices = @(
        'gateway', 'identity', 'data-ingestion', 'parcel-lifecycle', 'device-config',
        'object-storage', 'edge-health', 'operational-result', 'edge')
    $imageIds = foreach ($service in $applicationServices) {
        $containerId = (& docker ps -a -q `
            --filter "label=com.docker.compose.project=$ProjectName" `
            --filter "label=com.docker.compose.service=$service" | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace([string]$containerId)) {
            throw "The final '$service' application container is missing before image scanning."
        }
        & docker inspect --format '{{.Image}}' $containerId
        if ($LASTEXITCODE -ne 0) { throw "The final '$service' image could not be resolved." }
    }
    $imageIds = @($imageIds | Sort-Object -Unique)
    if ($imageIds.Count -ne 9) {
        throw 'Expected nine distinct locally-built application images before secret scanning.'
    }

    foreach ($imageId in $imageIds) {
        $metadata = ((& docker image inspect $imageId) -join "`n") +
                    ((& docker history --no-trunc $imageId) -join "`n")
        foreach ($secret in $secretValues) {
            if ($metadata.Contains($secret, [StringComparison]::Ordinal)) {
                throw 'A generated secret was found in final image metadata or history.'
            }
        }

        $secretValues -join "`n" | & docker run --rm -i --entrypoint /bin/sh $imageId -ec 'if grep -R -a -F -l -f - /app >/dev/null 2>&1; then exit 42; fi'
        if ($LASTEXITCODE -ne 0) {
            throw 'A generated secret was found in a final application image filesystem.'
        }
    }
}

function Assert-ScopedProject {
    $unexpected = @(& docker ps -a --filter "label=com.docker.compose.project=$ProjectName" --format '{{.Names}}' |
        Where-Object { $_ -notlike "$ProjectName-*" })
    if ($unexpected.Count -gt 0) {
        throw 'The requested Compose project label includes an unexpected container name; refusing cleanup.'
    }
}

Push-Location $repositoryRoot
try {
    $revisionOutput = @(& git rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0) {
        $revisionCandidate = ($revisionOutput -join '').Trim()
        if ($revisionCandidate -match '^[0-9a-fA-F]{40}$') { $sourceRevision = $revisionCandidate.ToLowerInvariant() }
    }
    $statusOutput = @(& git status --porcelain --untracked-files=all 2>$null)
    $workingTreeClean = $LASTEXITCODE -eq 0 -and $statusOutput.Count -eq 0
    if ($RequireCleanRevision -and
        ([string]::IsNullOrWhiteSpace([string]$sourceRevision) -or -not $workingTreeClean)) {
        throw 'A clean Git revision is required for publishable full-development evidence.'
    }

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required.'
    }
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Engine is unavailable.'
    }

    if ($ResetData) {
        Write-Phase 'Resetting only the explicitly scoped post-platform-final project.'
        Assert-ScopedProject
        Invoke-Compose -Arguments @(
            '--profile', 'connected-edge',
            '--profile', 'observability',
            '--profile', 'ha',
            'down', '--volumes', '--remove-orphans')
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'initialize-full-dev-secrets.ps1') -Force
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic secret initialization failed.' }
    } elseif (-not (Test-Path -LiteralPath (Join-Path $secretDirectory 'core.env'))) {
        $projectVolumes = @(& docker volume ls -q --filter "label=com.docker.compose.project=$ProjectName")
        if ($projectVolumes.Count -gt 0) {
            throw 'Project volumes exist but their matching ignored secret files are missing; reset explicitly.'
        }
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'initialize-full-dev-secrets.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic secret initialization failed.' }
    }

    $requiredSecrets = @('core.env', 'sql.env', 'redis.env', 'minio.env', 'edge.env', 'grafana.env')
    foreach ($file in $requiredSecrets) {
        if (-not (Test-Path -LiteralPath (Join-Path $secretDirectory $file) -PathType Leaf)) {
            throw "Required ignored secret file '$file' is missing."
        }
    }

    Invoke-Compose -Arguments @('config', '--quiet')
    Write-Phase 'Starting auth-on Core, SQL, Redis, Kafka-compatible, and S3-compatible services.'
    $up = @('up', '-d', '--wait', '--wait-timeout', '900')
    if (-not $SkipBuild) { $up += '--build' }
    Invoke-Compose -Arguments $up
    Wait-HttpStatus -Uri "$gatewayBase/health/ready" -TimeoutSeconds 60

    $coreSecrets = Read-PrivateEnvFile (Join-Path $secretDirectory 'core.env')
    $edgeSecrets = Read-PrivateEnvFile (Join-Path $secretDirectory 'edge.env')
    $sqlSecrets = Read-PrivateEnvFile (Join-Path $secretDirectory 'sql.env')
    $redisSecrets = Read-PrivateEnvFile (Join-Path $secretDirectory 'redis.env')
    $minioSecrets = Read-PrivateEnvFile (Join-Path $secretDirectory 'minio.env')
    $grafanaSecrets = Read-PrivateEnvFile (Join-Path $secretDirectory 'grafana.env')

    Write-Phase 'Proving an unprovisioned Edge cannot pass readiness.'
    $unprovisionedEdgeUp = @('--profile', 'connected-edge', 'up', '-d', '--no-deps', '--force-recreate')
    if (-not $SkipBuild) { $unprovisionedEdgeUp += '--build' }
    $unprovisionedEdgeUp += 'edge'
    Invoke-Compose -Arguments $unprovisionedEdgeUp
    Start-Sleep -Seconds 8
    $prematureStatus = 0
    try {
        $premature = Invoke-WebRequest -Uri "$edgeBase/health/ready" -SkipHttpErrorCheck -TimeoutSec 10
        $prematureStatus = [int]$premature.StatusCode
    } catch {
        # Connection reset/termination is also an acceptable fail-closed result:
        # an unprovisioned Edge must never be mistaken for ready by orchestration.
        $prematureStatus = 0
    }
    if ($prematureStatus -eq 200) {
        throw 'An Edge with an unprovisioned synthetic credential incorrectly passed readiness.'
    }
    Invoke-Compose -Arguments @('stop', 'edge')

    Write-Phase 'Bootstrapping the administrator and rotating Edge credential without logging raw values.'
    $adminAuth = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/auth/token" -ExpectedStatus @(200) -Body @{
        userName = $coreSecrets['Identity__InitialAdmin__UserName']
        password = $coreSecrets['Identity__InitialAdmin__Password']
        tokenLifetimeSeconds = 1800
    }
    $adminToken = [string]$adminAuth.Body.token
    if ([string]::IsNullOrWhiteSpace($adminToken)) { throw 'Administrator token response was empty.' }
    $adminHeaders = @{ Authorization = "Bearer $adminToken"; 'Idempotency-Key' = "edge-provision-$runId" }
    $provision = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/admin/edges" -ExpectedStatus @(201, 409) -Headers $adminHeaders -Body @{
        deviceId = $edgeId
        logicalDeviceCode = $edgeLogicalCode
        exchangeCenterCode = $centerCode
    }
    $edgeSecret = if ($provision.Status -eq 201) { [string]$provision.Body.deviceSecret } else { $null }
    if ([string]::IsNullOrWhiteSpace($edgeSecret)) {
        $rotateHeaders = @{ Authorization = "Bearer $adminToken"; 'Idempotency-Key' = "edge-rotate-$runId" }
        $rotated = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/admin/edges/$edgeId/rotate-secret" -ExpectedStatus @(200) -Headers $rotateHeaders
        $edgeSecret = [string]$rotated.Body.deviceSecret
    }
    if ([string]::IsNullOrWhiteSpace($edgeSecret)) { throw 'The one-time Edge secret was not returned.' }
    Set-PrivateEnvValue -Path (Join-Path $secretDirectory 'edge.env') -Name 'Edge__CoreApi__DeviceSecret' -Value $edgeSecret
    $edgeSecrets['Edge__CoreApi__DeviceSecret'] = $edgeSecret

    $edgeAuth = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/auth/edge-token" -ExpectedStatus @(200) -Body @{
        logicalDeviceCode = $edgeLogicalCode
        deviceSecret = $edgeSecret
        tokenLifetimeSeconds = 900
    }
    $edgeToken = [string]$edgeAuth.Body.token
    if ([string]::IsNullOrWhiteSpace($edgeToken)) { throw 'Edge token response was empty.' }
    $edgeHeaders = @{ Authorization = "Bearer $edgeToken" }

    Write-Phase 'Registering a sorting device and publishing the center configuration snapshot.'
    $deviceHeaders = @{ Authorization = "Bearer $adminToken"; 'Idempotency-Key' = "sorter-register-$runId" }
    $deviceResponse = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/admin/devices" -ExpectedStatus @(201, 409) -Headers $deviceHeaders -Body @{
        name = 'Full Development Sorter'
        deviceType = 'AutomaticSorter'
        logicalCode = 'FULLDEV-SORTER-01'
        owner = 'Post Full Development'
        exchangeCenterCode = $centerCode
        description = 'Synthetic E2E sorting device'
    }
    if ($deviceResponse.Status -eq 201) {
        $sortingDeviceId = [string]$deviceResponse.Body.deviceId
        $sortingDeviceSecret = [string]$deviceResponse.Body.deviceSecret
    } else {
        $list = Invoke-JsonRequest -Method Get -Uri "$gatewayBase/api/admin/devices?exchangeCenterCode=$centerCode&page=1&pageSize=200" -ExpectedStatus @(200) -Headers @{ Authorization = "Bearer $adminToken" }
        $device = @($list.Body.items | Where-Object logicalCode -eq 'FULLDEV-SORTER-01') | Select-Object -First 1
        if ($null -eq $device) { throw 'Existing sorting device could not be resolved.' }
        $sortingDeviceId = [string]$device.deviceId
        $rotateDevice = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/admin/devices/$sortingDeviceId/rotate-secret" -ExpectedStatus @(200) -Headers @{
            Authorization = "Bearer $adminToken"
            'Idempotency-Key' = "sorter-rotate-$runId"
        }
        $sortingDeviceSecret = [string]$rotateDevice.Body.deviceSecret
    }
    if ([string]::IsNullOrWhiteSpace($sortingDeviceSecret)) { throw 'The one-time sorting-device secret was not returned.' }

    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/admin/config" -ExpectedStatus @(201) -Headers @{
        Authorization = "Bearer $adminToken"
        'Idempotency-Key' = "config-publish-$runId"
    } -Body @{
        exchangeCenterCode = $centerCode
        postalCodes = @('12345')
        shortReadingThresholdSeconds = 60
        returnThresholdSeconds = 120
        weightToleranceKg = 1
        dimensionToleranceCm = 2
        autoDispatchEnabled = $false
        maximumOpenBagMinutes = 60
    })

    Write-Phase 'Starting the provisioned Edge and requiring configuration-backed readiness.'
    Invoke-Compose -Arguments @('--profile', 'connected-edge', 'up', '-d', '--force-recreate', '--wait', '--wait-timeout', '240', 'edge')
    Wait-HttpStatus -Uri "$edgeBase/health/ready" -TimeoutSeconds 180

    Write-Phase 'Running a real sorting-device WebSocket scenario through Edge, Gateway, Core, Kafka, S3, and SQL.'
    $dotnet = Get-DotNetExecutable
    $previousToken = $env:POST_DEVICE_TOKEN
    try {
        $env:POST_DEVICE_TOKEN = $sortingDeviceSecret
        $simulatorArguments = @(
            'run',
            '--project', (Join-Path $repositoryRoot 'tools\Post.DeviceSimulator\Post.DeviceSimulator.csproj'),
            '-c', 'Release',
            '--',
            '--url', "$edgeBase/ws/device",
            '--device', 'FULLDEV-SORTER-01',
            '--scenario', 'full',
            '--timeout-seconds', '180')
        & $dotnet @simulatorArguments
        if ($LASTEXITCODE -ne 0) { throw 'The real device simulator scenario failed.' }
    } finally {
        if ($null -eq $previousToken) { Remove-Item Env:POST_DEVICE_TOKEN -ErrorAction SilentlyContinue }
        else { $env:POST_DEVICE_TOKEN = $previousToken }
    }

    Wait-SqlScalar -Expected '1' -Query "SET NOCOUNT ON; SELECT CASE WHEN EXISTS (SELECT 1 FROM [PostParcelLifecycle].[dbo].[ReadingRecords]) AND EXISTS (SELECT 1 FROM [PostParcelLifecycle].[dbo].[SupplementaryAttachments]) AND EXISTS (SELECT 1 FROM [PostParcelLifecycle].[dbo].[Bags]) AND EXISTS (SELECT 1 FROM [PostParcelLifecycle].[dbo].[Dispatches]) THEN 1 ELSE 0 END"

    Write-Phase 'Asserting encrypted Edge DB/WAL/SHM and persistence across restart.'
    $localAdmin = Invoke-JsonRequest -Method Post -Uri "$edgeBase/admin/auth/login" -ExpectedStatus @(200) -Body @{
        userName = 'admin'
        password = $edgeSecrets['Edge__LocalAdmin__InitialPassword']
    }
    $localAdminToken = [string]$localAdmin.Body.accessToken
    if ([string]::IsNullOrWhiteSpace($localAdminToken)) { throw 'Edge local administrator token was empty.' }
    $edgeStatusBefore = Invoke-JsonRequest -Method Get -Uri "$edgeBase/api/edge/status" -ExpectedStatus @(200) -Headers @{
        Authorization = "Bearer $localAdminToken"
    }
    if ([string]::IsNullOrWhiteSpace([string]$edgeStatusBefore.Body.configurationVersion) -or
        [int]$edgeStatusBefore.Body.activeDeviceCount -lt 1) {
        throw 'Edge did not persist the downloaded configuration before encryption inspection.'
    }
    Assert-EncryptedEdgeStorage -PlaintextSentinel 'FULLDEV-SORTER-01'
    Invoke-Compose -Arguments @('--profile', 'connected-edge', 'restart', 'edge')
    Wait-HttpStatus -Uri "$edgeBase/health/ready" -TimeoutSeconds 180
    $localAdminAfterRestart = Invoke-JsonRequest -Method Post -Uri "$edgeBase/admin/auth/login" -ExpectedStatus @(200) -Body @{
        userName = 'admin'
        password = $edgeSecrets['Edge__LocalAdmin__InitialPassword']
    }
    $edgeStatusAfter = Invoke-JsonRequest -Method Get -Uri "$edgeBase/api/edge/status" -ExpectedStatus @(200) -Headers @{
        Authorization = "Bearer $([string]$localAdminAfterRestart.Body.accessToken)"
    }
    if ([string]$edgeStatusAfter.Body.configurationVersion -ne [string]$edgeStatusBefore.Body.configurationVersion -or
        [int]$edgeStatusAfter.Body.activeDeviceCount -ne [int]$edgeStatusBefore.Body.activeDeviceCount) {
        throw 'Encrypted Edge state did not persist unchanged across restart.'
    }
    Assert-EncryptedEdgeStorage -PlaintextSentinel 'FULLDEV-SORTER-01'

    Write-Phase 'Measuring the documented capacity baseline with 200 real parcel registrations.'
    $readingCountBefore = [int](Invoke-SqlScalar -Query 'SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[ReadingRecords]')
    $previousToken = $env:POST_DEVICE_TOKEN
    try {
        $env:POST_DEVICE_TOKEN = $sortingDeviceSecret
        $loadArguments = @(
            'run',
            '--project', (Join-Path $repositoryRoot 'tools\Post.DeviceSimulator\Post.DeviceSimulator.csproj'),
            '-c', 'Release',
            '--no-restore',
            '--',
            '--url', "$edgeBase/ws/device",
            '--device', 'FULLDEV-SORTER-01',
            '--scenario', 'load',
            '--count', '200',
            '--timeout-seconds', '300')
        $loadOutput = @(& $dotnet @loadArguments 2>&1)
        if ($LASTEXITCODE -ne 0) { throw 'The real device capacity baseline failed.' }
    } finally {
        if ($null -eq $previousToken) { Remove-Item Env:POST_DEVICE_TOKEN -ErrorAction SilentlyContinue }
        else { $env:POST_DEVICE_TOKEN = $previousToken }
    }

    $loadLine = @($loadOutput | Where-Object { $_ -match '^LOAD_RESULT ' }) | Select-Object -Last 1
    $loadPattern = '^LOAD_RESULT count=(?<count>\d+) errors=(?<errors>\d+) elapsedMs=(?<elapsed>[0-9.]+) throughputPerSecond=(?<throughput>[0-9.]+) latencyP50Ms=(?<p50>[0-9.]+) latencyP95Ms=(?<p95>[0-9.]+) latencyMaxMs=(?<max>[0-9.]+)$'
    if ([string]::IsNullOrWhiteSpace([string]$loadLine) -or [string]$loadLine -notmatch $loadPattern) {
        throw 'The device capacity baseline did not emit the expected redacted metric contract.'
    }
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $loadCount = [int]$Matches['count']
    $errorCount = [int]$Matches['errors']
    $elapsedMilliseconds = [decimal]::Parse($Matches['elapsed'], $culture)
    $throughput = [decimal]::Parse($Matches['throughput'], $culture)
    $latencyP50 = [decimal]::Parse($Matches['p50'], $culture)
    $latencyP95 = [decimal]::Parse($Matches['p95'], $culture)
    $latencyMaximum = [decimal]::Parse($Matches['max'], $culture)
    if ($loadCount -ne 200 -or $errorCount -ne 0 -or $elapsedMilliseconds -le 0 -or
        $throughput -lt [decimal]2 -or $latencyP50 -lt 0 -or
        $latencyP50 -gt $latencyP95 -or $latencyP95 -gt $latencyMaximum) {
        throw 'The capacity smoke baseline requires 200 results, zero errors, ordered non-negative latency metrics, and at least two parcels per second.'
    }
    $expectedReadingCount = $readingCountBefore + $loadCount
    Wait-SqlScalar -Expected '1' -TimeoutSeconds 300 -Query "SET NOCOUNT ON; SELECT CASE WHEN COUNT(*) >= $expectedReadingCount THEN 1 ELSE 0 END FROM [PostParcelLifecycle].[dbo].[ReadingRecords]"
    $readingCountAfter = [int](Invoke-SqlScalar -Query 'SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[ReadingRecords]')
    $performanceEvidence = [ordered]@{
        classification = 'capacity-smoke-baseline-not-an-slo'
        count = $loadCount
        errors = $errorCount
        elapsedMs = $elapsedMilliseconds
        throughputPerSecond = $throughput
        latencyP50Ms = $latencyP50
        latencyP95Ms = $latencyP95
        latencyMaxMs = $latencyMaximum
        sqlReadingCountBefore = $readingCountBefore
        sqlReadingCountAfter = $readingCountAfter
    }

    Write-Phase 'Exercising SQL-authoritative Redis hit, stale repair, outage rebuild, and two replicas.'
    $redisKey = "redis-$runId"
    $redisBarcode = '595440000000009999999991'
    $inboundBody = @{
        idempotencyKey = $redisKey
        barcodes = @($redisBarcode)
        parcelType = 'E2E'
        serviceType = 'Standard'
        originCenterCode = $centerCode
        destinationCenterCode = $centerCode
        deviceId = $sortingDeviceId
        exchangeCenterCode = $centerCode
        physicalAttributes = @{ weightKg = 1; lengthCm = 2; widthCm = 3; heightCm = 4 }
        edgeEventTime = [DateTimeOffset]::UtcNow.ToString('O')
        correlationId = "redis-$runId"
    }
    $redisHeaders = @{ Authorization = "Bearer $edgeToken"; 'Idempotency-Key' = $redisKey }
    $firstInbound = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcels/inbound-query" -ExpectedStatus @(200) -Headers $redisHeaders -Body $inboundBody
    $secondInbound = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcels/inbound-query" -ExpectedStatus @(200) -Headers $redisHeaders -Body $inboundBody
    if (-not [bool]$secondInbound.Body.isReplay) { throw 'The SQL idempotency replay was not returned.' }
    $readingRecordId = [string]$firstInbound.Body.readingRecordId
    $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($redisKey))).ToLowerInvariant()
    $cacheKey = "post:full-dev:idempotency:parcel-lifecycle:$hash"
    $exists = Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$cacheKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning EXISTS "$POST_CACHE_KEY"')
    if ($exists.Trim() -ne '1') { throw 'Redis mirror was not populated after SQL commit.' }
    $ttl = [int](Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$cacheKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning TTL "$POST_CACHE_KEY"'))
    if ($ttl -lt 1 -or $ttl -gt 1800) { throw 'Redis mirror did not carry the configured bounded TTL.' }

    $cleanupKey = "post:full-dev:idempotency:parcel-lifecycle:cleanup-$runId"
    [void](Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$cleanupKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning SET "$POST_CACHE_KEY" synthetic EX 1800 >/dev/null; redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning ZADD post:full-dev:idempotency:expiry 0 "$POST_CACHE_KEY" >/dev/null'))
    $cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        $cleanupExists = Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$cleanupKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning EXISTS "$POST_CACHE_KEY"')
        if ($cleanupExists.Trim() -eq '0') { break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $cleanupDeadline)
    if ($cleanupExists.Trim() -ne '0') { throw 'The live Redis TTL cleanup worker did not remove an expired-index entry.' }

    $stale = '{"schemaVersion":1,"requestHash":"stale","responseSnapshot":"stale","status":1,"updatedAt":"2026-08-16T00:00:00+00:00"}'
    [void](Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$cacheKey", '-e', "POST_CACHE_VALUE=$stale", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning SET "$POST_CACHE_KEY" "$POST_CACHE_VALUE" EX 1800 >/dev/null'))
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcels/inbound-query" -ExpectedStatus @(200) -Headers $redisHeaders -Body $inboundBody)
    $repaired = Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$cacheKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning GET "$POST_CACHE_KEY"')
    if ($repaired.Contains('"requestHash":"stale"', [StringComparison]::Ordinal)) { throw 'Stale Redis mirror was not repaired from SQL.' }

    $operationalKey = "operational-$runId"
    $operationalCalledAt = [DateTimeOffset]::UtcNow.AddMilliseconds(-25)
    $operationalBody = @{
        edgeId = $edgeId
        exchangeCenterCode = $centerCode
        idempotencyKey = $operationalKey
        correlationId = "operational-$runId"
        parcelBarcode = $redisBarcode
        callResult = 'accepted'
        success = $true
        errorCode = $null
        errorMessage = $null
        calledAt = $operationalCalledAt.ToString('O')
        respondedAt = $operationalCalledAt.AddMilliseconds(20).ToString('O')
        attempts = 1
        finalStatus = 'Succeeded'
    }
    $operationalHeaders = @{ Authorization = "Bearer $edgeToken"; 'Idempotency-Key' = $operationalKey }
    $firstOperational = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/operational-results" -ExpectedStatus @(201) -Headers $operationalHeaders -Body $operationalBody
    $replayedOperational = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/operational-results" -ExpectedStatus @(200) -Headers $operationalHeaders -Body $operationalBody
    if ([bool]$firstOperational.Body.isReplay -or -not [bool]$replayedOperational.Body.isReplay) {
        throw 'OperationalResult did not preserve SQL-authoritative first-write/replay semantics.'
    }
    $conflictingOperational = $operationalBody.Clone()
    $conflictingOperational.callResult = 'different'
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/operational-results" -ExpectedStatus @(409) -Headers $operationalHeaders -Body $conflictingOperational)
    $operationalHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($operationalKey))).ToLowerInvariant()
    $operationalCacheKey = "post:full-dev:idempotency:operational-result:$operationalHash"
    $operationalExists = Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$operationalCacheKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning EXISTS "$POST_CACHE_KEY"')
    if ($operationalExists.Trim() -ne '1') { throw 'OperationalResult Redis mirror was not populated after SQL commit.' }
    $escapedOperationalKey = $operationalKey.Replace("'", "''", [StringComparison]::Ordinal)
    if ((Invoke-SqlScalar -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM [PostOperationalResult].[dbo].[OperationalResults] WHERE [IdempotencyKey]=N'$escapedOperationalKey'") -ne '1') {
        throw 'OperationalResult SQL authority did not persist exactly one result.'
    }

    Invoke-Compose -Arguments @('stop', 'redis')
    $outageKey = "redis-outage-$runId"
    $outageBody = $inboundBody.Clone()
    $outageBody.idempotencyKey = $outageKey
    $outageBody.barcodes = @('595440000000009999999992')
    $outageHeaders = @{ Authorization = "Bearer $edgeToken"; 'Idempotency-Key' = $outageKey }
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcels/inbound-query" -ExpectedStatus @(200) -Headers $outageHeaders -Body $outageBody)
    $operationalOutageKey = "operational-outage-$runId"
    $operationalOutageBody = $operationalBody.Clone()
    $operationalOutageBody.idempotencyKey = $operationalOutageKey
    $operationalOutageBody.correlationId = "operational-outage-$runId"
    $operationalOutageHeaders = @{ Authorization = "Bearer $edgeToken"; 'Idempotency-Key' = $operationalOutageKey }
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/operational-results" -ExpectedStatus @(201) -Headers $operationalOutageHeaders -Body $operationalOutageBody)
    Invoke-Compose -Arguments @('up', '-d', '--wait', '--wait-timeout', '90', 'redis')
    $outageHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($outageKey))).ToLowerInvariant()
    $outageCacheKey = "post:full-dev:idempotency:parcel-lifecycle:$outageHash"
    $reconnectDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcels/inbound-query" -ExpectedStatus @(200) -Headers $outageHeaders -Body $outageBody)
        $rebuilt = Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$outageCacheKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning EXISTS "$POST_CACHE_KEY"')
        if ($rebuilt.Trim() -eq '1') { break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $reconnectDeadline)
    if ($rebuilt.Trim() -ne '1') { throw 'Redis outage recovery did not rebuild the mirror from SQL.' }
    $operationalOutageHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($operationalOutageKey))).ToLowerInvariant()
    $operationalOutageCacheKey = "post:full-dev:idempotency:operational-result:$operationalOutageHash"
    $reconnectDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $operationalOutageReplay = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/operational-results" -ExpectedStatus @(200) -Headers $operationalOutageHeaders -Body $operationalOutageBody
        if (-not [bool]$operationalOutageReplay.Body.isReplay) { throw 'OperationalResult outage recovery did not replay from SQL.' }
        $operationalRebuilt = Invoke-Compose -Capture -Arguments @('exec', '-T', '-e', "POST_CACHE_KEY=$operationalOutageCacheKey", 'redis', '/bin/sh', '-ec', 'redis-cli --user "$POST_REDIS_USER" -a "$POST_REDIS_PASSWORD" --no-auth-warning EXISTS "$POST_CACHE_KEY"')
        if ($operationalRebuilt.Trim() -eq '1') { break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $reconnectDeadline)
    if ($operationalRebuilt.Trim() -ne '1') { throw 'OperationalResult outage recovery did not rebuild its Redis mirror.' }

    Invoke-Compose -Arguments @('up', '-d', '--scale', 'parcel-lifecycle=2', '--no-recreate', '--wait', '--wait-timeout', '180', 'parcel-lifecycle')
    $replicaIds = @(Invoke-Compose -Capture -Arguments @('ps', '-q', 'parcel-lifecycle') -split "`r?`n" | Where-Object { $_ })
    if ($replicaIds.Count -ne 2) { throw 'Exactly two ParcelLifecycle replicas were not running.' }
    foreach ($replicaId in $replicaIds) {
        $ip = (& docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $replicaId).Trim()
        if ([string]::IsNullOrWhiteSpace($ip)) { throw 'A ParcelLifecycle replica IP could not be resolved.' }
        $internalResponse = Invoke-ToolShell -Environment @{
            POST_E2E_TOKEN = $edgeToken
            POST_E2E_BODY = ($inboundBody | ConvertTo-Json -Depth 10 -Compress)
            POST_E2E_KEY = $redisKey
            POST_E2E_URL = "http://$ip`:8080/api/edge/parcels/inbound-query"
        } -Script 'curl --fail --silent --show-error -H "Authorization: Bearer $POST_E2E_TOKEN" -H "Idempotency-Key: $POST_E2E_KEY" -H "Content-Type: application/json" --data "$POST_E2E_BODY" "$POST_E2E_URL"'
        if (-not $internalResponse.Contains('"isReplay":true', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A ParcelLifecycle replica did not return the shared SQL idempotency replay.'
        }
    }
    $escapedRedisKey = $redisKey.Replace("'", "''", [StringComparison]::Ordinal)
    if ((Invoke-SqlScalar -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[IdempotencyRecords] WHERE [Key]=N'$escapedRedisKey'") -ne '1') {
        throw 'Two replicas created more than one SQL-authoritative idempotency row.'
    }

    Write-Phase 'Proving presign SSE header enforcement, PUT, metadata Kafka delivery, and SQL attachment link.'
    $uploadEventId = [Guid]::NewGuid().ToString('D')
    $presign = Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/images/presigned-url" -ExpectedStatus @(200) -Headers $edgeHeaders -Body @{
        eventId = $uploadEventId
        readingRecordId = $readingRecordId
        attachmentType = 'e2e-proof'
        contentType = 'image/jpeg'
        contentLength = 3
        lifetimeSeconds = 300
    }
    $requiredEncryption = [string]$presign.Body.requiredHeaders.'x-amz-server-side-encryption'
    if (-not [string]::Equals($requiredEncryption, 'AES256', [StringComparison]::Ordinal)) {
        throw 'Presign response did not require AES256 server-side encryption.'
    }
    $uploadEnvironment = @{ POST_UPLOAD_URL = [string]$presign.Body.uploadUrl }
    $omitted = (Invoke-ToolShell -Environment $uploadEnvironment -Script 'curl --silent --output /dev/null --write-out "%{http_code}" -X PUT -H "Content-Type: image/jpeg" --data-binary abc "$POST_UPLOAD_URL"').Trim()
    if ([int]$omitted -lt 400) { throw 'MinIO accepted a signed PUT that omitted the required SSE header.' }
    $tampered = (Invoke-ToolShell -Environment $uploadEnvironment -Script 'curl --silent --output /dev/null --write-out "%{http_code}" -X PUT -H "Content-Type: image/jpeg" -H "x-amz-server-side-encryption: AES128" --data-binary abc "$POST_UPLOAD_URL"').Trim()
    if ([int]$tampered -lt 400) { throw 'MinIO accepted a signed PUT with a tampered SSE header.' }
    $accepted = (Invoke-ToolShell -Environment $uploadEnvironment -Script 'curl --silent --output /dev/null --write-out "%{http_code}" -X PUT -H "Content-Type: image/jpeg" -H "x-amz-server-side-encryption: AES256" --data-binary abc "$POST_UPLOAD_URL"').Trim()
    if ([int]$accepted -notin @(200, 201)) { throw 'MinIO rejected the correctly signed AES256 PUT.' }

    $metadataKey = "metadata-$runId"
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/images/metadata" -ExpectedStatus @(202) -Headers @{
        Authorization = "Bearer $edgeToken"
        'Idempotency-Key' = $metadataKey
    } -Body @{
        eventId = $uploadEventId
        edgeId = $edgeId
        exchangeCenterCode = $centerCode
        parcelBarcode = $redisBarcode
        readingRecordId = $readingRecordId
        objectKey = [string]$presign.Body.objectKey
        contentType = 'image/jpeg'
        attachmentType = 'e2e-proof'
        idempotencyKey = $metadataKey
        correlationId = "metadata-$runId"
        receivedAt = [DateTimeOffset]::UtcNow.ToString('O')
    })
    $escapedObjectKey = ([string]$presign.Body.objectKey).Replace("'", "''", [StringComparison]::Ordinal)
    Wait-SqlScalar -Expected '1' -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[SupplementaryAttachments] WHERE [ObjectKey]=N'$escapedObjectKey'"
    $stat = Invoke-Compose -Capture -Arguments @('run', '--rm', '--no-deps', '--entrypoint', '/bin/sh', '-e', "POST_OBJECT_KEY=$($presign.Body.objectKey)", 'minio-init', '-ec', 'mc alias set post-minio http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null; mc stat --json "post-minio/post-images/$POST_OBJECT_KEY"')
    if ($stat -notmatch '(?i)(AES256|SSE-S3|server-side-encryption)') {
        throw 'The uploaded MinIO object did not report server-side encryption metadata.'
    }

    Write-Phase 'Proving Kafka restart recovery, duplicate delivery idempotency, retry, and DLQ.'
    Invoke-Compose -Arguments @('restart', 'parcel-lifecycle')
    Invoke-Compose -Arguments @('up', '-d', '--wait', '--wait-timeout', '180', 'parcel-lifecycle')
    $eventId = [Guid]::NewGuid().ToString('D')
    $eventKey = "parcel-event-$runId"
    $eventBody = @{
        eventId = $eventId
        edgeId = $edgeId
        exchangeCenterCode = $centerCode
        topic = 'parcel.events.received'
        idempotencyKey = $eventKey
        correlationId = "event-$runId"
        payload = @{ parcelBarcode = $redisBarcode; finalStatus = 'Success' }
        occurredAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $eventHeaders = @{ Authorization = "Bearer $edgeToken"; 'Idempotency-Key' = $eventKey }
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcel-events" -ExpectedStatus @(202) -Headers $eventHeaders -Body $eventBody)
    [void](Invoke-JsonRequest -Method Post -Uri "$gatewayBase/api/edge/parcel-events" -ExpectedStatus @(202) -Headers $eventHeaders -Body $eventBody)
    Wait-SqlScalar -Expected '1' -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[InboxMessages] WHERE [EventId]=N'$eventId'"

    $dlqMarker = "malformed-$runId"
    Invoke-RpkProduce -Topic 'parcel.events.received' -Payload "{\"marker\":\"$dlqMarker\"}"
    $dlq = Wait-KafkaPayload -Topic 'parcel.events.received.dlq' -Marker $dlqMarker
    if ($dlq -notmatch '"Attempts"\s*:\s*2') { throw 'DLQ record did not prove the configured two delivery attempts.' }
    [void](Wait-KafkaPayload -Topic 'parcel.lifecycle.reading-recorded' -Marker $redisKey)

    Write-Phase 'Asserting SQL TDE is active for all five authoritative databases.'
    Wait-SqlScalar -Expected '5' -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN ('PostParcelLifecycle','PostDeviceConfig','PostEdgeHealth','PostOperationalResult','PostIdentity') AND is_encrypted=1" -TimeoutSeconds 300

    if ($IncludeObservability) {
        Write-Phase 'Starting Prometheus, Loki, Alloy, and Grafana and asserting real scrape/log/dashboard data.'
        Invoke-Compose -Arguments @('--profile', 'observability', 'up', '-d', '--wait', '--wait-timeout', '240', 'prometheus', 'loki', 'alloy', 'grafana')
        Start-Sleep -Seconds 20
        $metrics = Invoke-ToolShell -Script 'curl --fail --silent "http://prometheus:9090/api/v1/query?query=up%7Bjob%3D%22post-platform%22%7D"'
        $metricsJson = $metrics | ConvertFrom-Json -Depth 20
        $upTargets = @($metricsJson.data.result | Where-Object { [string]$_.value[1] -eq '1' })
        if ($upTargets.Count -lt 9) { throw 'Prometheus did not report all nine application targets up.' }

        $correlationId = [Guid]::NewGuid().ToString('D')
        [void](Invoke-JsonRequest -Method Get -Uri "$gatewayBase/api/admin/devices?exchangeCenterCode=$centerCode&page=1&pageSize=1" -ExpectedStatus @(200) -Headers @{
            Authorization = "Bearer $adminToken"
            'X-Correlation-ID' = $correlationId
        })
        Start-Sleep -Seconds 10
        $lokiQuery = [Uri]::EscapeDataString("{service=`"gateway`"} |= `"$correlationId`"")
        $logs = Invoke-ToolShell -Script "curl --fail --silent 'http://loki:3100/loki/api/v1/query_range?query=$lokiQuery&limit=20'"
        if (-not $logs.Contains($correlationId, [StringComparison]::Ordinal)) {
            throw 'Loki did not return the real gateway correlation-scoped log.'
        }

        $dashboard = Invoke-Compose -Capture -Arguments @('exec', '-T', 'grafana', '/bin/sh', '-ec', 'wget -qO- --user="$GF_SECURITY_ADMIN_USER" --password="$GF_SECURITY_ADMIN_PASSWORD" "http://localhost:3000/api/search?query=Post"')
        if (-not $dashboard.Contains('post-platform-overview', [StringComparison]::Ordinal)) {
            throw 'Grafana did not provision the Post platform dashboard.'
        }

        Invoke-Compose -Arguments @('stop', 'prometheus', 'alloy')
        Wait-HttpStatus -Uri "$gatewayBase/health/ready" -TimeoutSeconds 30
        Invoke-Compose -Arguments @('--profile', 'observability', 'up', '-d', '--wait', '--wait-timeout', '120', 'prometheus', 'alloy')
    }

    if ($IncludeHa) {
        Write-Phase 'Running two-node SQL Always On synchronization, failover query, and failback sequentially.'
        Invoke-Compose -Arguments @('stop', 'gateway', 'identity', 'data-ingestion', 'parcel-lifecycle', 'device-config', 'object-storage', 'edge-health', 'operational-result', 'edge', 'redpanda', 'redis', 'minio')
        Invoke-Compose -Arguments @('--profile', 'ha', 'up', '-d', '--wait', '--wait-timeout', '600', 'sql-secondary')
        Invoke-Compose -Arguments @('--profile', 'ha', 'run', '--rm', '--no-deps', 'sql-ag-configure')
        $synchronized = Invoke-SqlScalar -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_hadr_database_replica_states WHERE is_local=0 AND synchronization_state_desc='SYNCHRONIZED'"
        if ($synchronized -ne '5') { throw 'The local Always On AG did not synchronize all five TDE databases.' }
        $authoritativeCount = Invoke-SqlScalar -Query 'SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[ReadingRecords]'

        # CLUSTER_TYPE=NONE has no cluster manager/listener. Microsoft documents
        # an offline + forced-promotion sequence for its manual read-scale role
        # change. Writes are stopped and all five DBs are synchronized first;
        # exact counts before/after guard against any data loss.
        [void](Invoke-SqlScalar -Service sql-primary -Query "ALTER AVAILABILITY GROUP [post_dev_ag] OFFLINE; SET NOCOUNT ON; SELECT 'OFFLINE'")
        [void](Invoke-SqlScalar -Service sql-secondary -Query "ALTER AVAILABILITY GROUP [post_dev_ag] FORCE_FAILOVER_ALLOW_DATA_LOSS; SET NOCOUNT ON; SELECT 'PRIMARY'")
        Wait-SqlScalar -Service sql-secondary -Expected 'PRIMARY' -TimeoutSeconds 180 -Query "SET NOCOUNT ON; SELECT role_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local=1 AND group_id=(SELECT group_id FROM sys.availability_groups WHERE name=N'post_dev_ag')"
        $failoverCount = Invoke-SqlScalar -Service sql-secondary -Query 'SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[ReadingRecords]'
        if ($failoverCount -ne $authoritativeCount) { throw 'AG failover query did not preserve the authoritative parcel count.' }
        $failoverTdeCount = Invoke-SqlScalar -Service sql-secondary -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN ('PostParcelLifecycle','PostDeviceConfig','PostEdgeHealth','PostOperationalResult','PostIdentity') AND is_encrypted=1"
        if ($failoverTdeCount -ne '5') { throw 'All five failover databases were not encrypted with TDE on the secondary.' }
        [void](Invoke-SqlScalar -Service sql-primary -Query "ALTER AVAILABILITY GROUP [post_dev_ag] SET (ROLE = SECONDARY); ALTER DATABASE [PostParcelLifecycle] SET HADR RESUME; ALTER DATABASE [PostDeviceConfig] SET HADR RESUME; ALTER DATABASE [PostEdgeHealth] SET HADR RESUME; ALTER DATABASE [PostOperationalResult] SET HADR RESUME; ALTER DATABASE [PostIdentity] SET HADR RESUME; SET NOCOUNT ON; SELECT 'SECONDARY'")
        Wait-SqlScalar -Service sql-secondary -Expected '5' -TimeoutSeconds 300 -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_hadr_database_replica_states WHERE is_local=0 AND synchronization_state_desc='SYNCHRONIZED'"

        [void](Invoke-SqlScalar -Service sql-secondary -Query "ALTER AVAILABILITY GROUP [post_dev_ag] OFFLINE; SET NOCOUNT ON; SELECT 'OFFLINE'")
        [void](Invoke-SqlScalar -Service sql-primary -Query "ALTER AVAILABILITY GROUP [post_dev_ag] FORCE_FAILOVER_ALLOW_DATA_LOSS; SET NOCOUNT ON; SELECT 'PRIMARY'")
        Wait-SqlScalar -Service sql-primary -Expected 'PRIMARY' -TimeoutSeconds 180 -Query "SET NOCOUNT ON; SELECT role_desc FROM sys.dm_hadr_availability_replica_states WHERE is_local=1 AND group_id=(SELECT group_id FROM sys.availability_groups WHERE name=N'post_dev_ag')"
        $failbackCount = Invoke-SqlScalar -Service sql-primary -Query 'SET NOCOUNT ON; SELECT COUNT(*) FROM [PostParcelLifecycle].[dbo].[ReadingRecords]'
        if ($failbackCount -ne $authoritativeCount) { throw 'AG failback did not restore the authoritative parcel count.' }
        [void](Invoke-SqlScalar -Service sql-secondary -Query "ALTER AVAILABILITY GROUP [post_dev_ag] SET (ROLE = SECONDARY); ALTER DATABASE [PostParcelLifecycle] SET HADR RESUME; ALTER DATABASE [PostDeviceConfig] SET HADR RESUME; ALTER DATABASE [PostEdgeHealth] SET HADR RESUME; ALTER DATABASE [PostOperationalResult] SET HADR RESUME; ALTER DATABASE [PostIdentity] SET HADR RESUME; SET NOCOUNT ON; SELECT 'SECONDARY'")
        Wait-SqlScalar -Service sql-primary -Expected '5' -TimeoutSeconds 300 -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_hadr_database_replica_states WHERE is_local=0 AND synchronization_state_desc='SYNCHRONIZED'"
        Invoke-Compose -Arguments @('--profile', 'ha', 'stop', 'sql-secondary')
        Invoke-Compose -Arguments @('up', '-d', '--wait', '--wait-timeout', '300', 'redis', 'redpanda', 'redpanda-init', 'minio', 'minio-init', 'identity', 'data-ingestion', 'parcel-lifecycle', 'device-config', 'object-storage', 'edge-health', 'operational-result', 'gateway')
        Invoke-Compose -Arguments @('--profile', 'connected-edge', 'up', '-d', '--wait', '--wait-timeout', '240', 'edge')
    }

    Write-Phase 'Scanning final app image history, config, and filesystem for generated secret literals.'
    Assert-FinalImagesContainNoSecrets -SecretSets @($coreSecrets, $edgeSecrets, $sqlSecrets, $redisSecrets, $minioSecrets, $grafanaSecrets)

    $evidenceDirectory = Join-Path $repositoryRoot 'artifacts\full-dev'
    [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
    $evidenceName = "full-dev-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))-$runId.json"
    $assertions = [Collections.Generic.List[string]]::new()
    $assertions.AddRange([string[]]@(
        'auth-on-admin-edge-bootstrap',
        'unprovisioned-edge-not-ready',
        'edge-sqlite-encrypted-db-wal-shm-restart-persistence',
        'edge-gateway-core-sync-async',
        'capacity-baseline-two-parcels-per-second',
        'redis-sql-authoritative-stale-outage-two-replicas',
        'operational-result-sql-authoritative-redis-conflict-outage-rebuild',
        's3-sse-signed-header-negative-positive-metadata-sql',
        'kafka-publish-consume-restart-idempotency-redelivery-dlq',
        'sql-tde-five-databases',
        'generated-secrets-absent-from-images'))
    if ($IncludeObservability) { $assertions.Add('prometheus-loki-grafana-exporter-outage-nonfatal') }
    if ($IncludeHa) { $assertions.Add('sql-always-on-sync-failover-query-failback') }

    $evidence = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sourceRevision = $sourceRevision
        workingTreeClean = $workingTreeClean
        composeProject = $ProjectName
        result = 'PASS'
        command = [ordered]@{
            script = 'scripts/infra/test-full-dev.ps1'
            resetData = [bool]$ResetData
            includeObservability = [bool]$IncludeObservability
            includeHighAvailability = [bool]$IncludeHa
            buildImages = -not [bool]$SkipBuild
            requireCleanRevision = [bool]$RequireCleanRevision
        }
        requestedProfiles = [ordered]@{
            connectedEdge = $true
            observability = [bool]$IncludeObservability
            highAvailability = [bool]$IncludeHa
        }
        executedProfiles = [ordered]@{
            connectedEdge = $true
            observability = [bool]$IncludeObservability
            highAvailability = [bool]$IncludeHa
        }
        performance = $performanceEvidence
        sql = [ordered]@{
            authoritativeDatabaseCount = 5
            tdeEncryptedDatabaseCount = 5
        }
        assertions = @($assertions)
        resourceSnapshot = @(Get-ScopedResourceSnapshot)
    }
    [IO.File]::WriteAllText(
        (Join-Path $evidenceDirectory $evidenceName),
        ($evidence | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    Write-Phase "Redacted runtime evidence: artifacts/full-dev/$evidenceName"

    $succeeded = $true
    Write-Phase ("PASS: real full-development gate completed in {0:N1} minutes." -f $started.Elapsed.TotalMinutes)
} finally {
    Pop-Location
    if (-not $KeepRunning) {
        Push-Location $repositoryRoot
        try {
            Assert-ScopedProject
            Invoke-Compose -Arguments @('--profile', 'connected-edge', '--profile', 'observability', '--profile', 'ha', 'down', '--remove-orphans')
        } finally {
            Pop-Location
        }
    } elseif (-not $succeeded) {
        Write-Warning 'The scoped stack was left running for diagnosis because -KeepRunning was requested.'
    }
}
