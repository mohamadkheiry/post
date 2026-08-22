[CmdletBinding()]
param(
    [string]$UserSecretsId = 'a8a90ac6-f781-4c07-b2ce-7e9f7a9234d0',
    [string[]]$SecretKeys = @(
        'Edge:DestinationApi:Username',
        'Edge:DestinationApi:Password',
        'Edge:CoreApi:DeviceSecret',
        'ADMIN_INITIAL_PASSWORD',
        'Edge:LocalAdmin:InitialPassword'
    ),
    [string[]]$RequiredKeys = @(
        'Edge:DestinationApi:Username',
        'Edge:DestinationApi:Password'
    ),
    [string[]]$ProcessEnvironmentSecretNames = @(
        'POST_EDGE_SQLITE_KEY',
        'POST_EDGE_SQLITE_PREVIOUS_KEY'
    ),
    [string[]]$AdditionalPaths = @('artifacts/local-runtime/logs'),
    [string]$ComposeProject = '',
    [string]$EnvSecretDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$secretRoot = if ($IsWindows) {
    Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\UserSecrets'
}
else {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.microsoft/usersecrets'
}
$secretFile = Join-Path (Join-Path $secretRoot $UserSecretsId) 'secrets.json'
if (-not (Test-Path -LiteralPath $secretFile -PathType Leaf)) {
    throw 'The configured Edge user-secret store does not exist.'
}

$document = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json -Depth 20
$secrets = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($key in $SecretKeys) {
    $property = $document.PSObject.Properties[$key]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { continue }
    $value = [string]$property.Value
    $minimumLength = if ($key.EndsWith('Username', [StringComparison]::OrdinalIgnoreCase)) { 3 } else { 8 }
    if ($value.Length -lt $minimumLength) {
        throw "Operational secret '$key' is too short for a reliable exact-match hygiene gate."
    }
    $secrets[$key] = $value
}
foreach ($required in $RequiredKeys) {
    if (-not $secrets.ContainsKey($required)) {
        throw "Required operational secret '$required' is not configured in the Edge user-secret store."
    }
}

foreach ($variableName in $ProcessEnvironmentSecretNames) {
    $value = [Environment]::GetEnvironmentVariable($variableName)
    if ([string]::IsNullOrWhiteSpace($value)) { continue }
    if ($value.Length -lt 8) {
        throw "Operational process secret '$variableName' is too short for exact-match scanning."
    }
    $secrets["process:$variableName"] = $value
}

if (-not [string]::IsNullOrWhiteSpace($EnvSecretDirectory)) {
    $resolvedSecretDirectory = if ([IO.Path]::IsPathRooted($EnvSecretDirectory)) {
        $EnvSecretDirectory
    }
    else {
        Join-Path $repositoryRoot $EnvSecretDirectory
    }
    if (-not (Test-Path -LiteralPath $resolvedSecretDirectory -PathType Container)) {
        throw 'The supplied generated-secret directory does not exist.'
    }
    foreach ($envFile in Get-ChildItem -LiteralPath $resolvedSecretDirectory -Filter '*.env' -File) {
        foreach ($line in Get-Content -LiteralPath $envFile.FullName) {
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
            $parts = $line -split '=', 2
            if ($parts.Count -ne 2) { throw "Invalid env entry in '$($envFile.Name)'." }
            $name = $parts[0]
            $value = $parts[1]
            if ($name -notmatch '(?i)(password|secret|signingkey|accesskey|connectionstring|kms)' -and
                $name -ne 'POST_EDGE_SQLITE_KEY' -and
                $name -ne 'POST_EDGE_SQLITE_PREVIOUS_KEY' -and
                $name -ne 'MINIO_ROOT_USER') {
                continue
            }
            if ($value.Length -lt 8) { throw "Generated secret '$name' is too short for exact-match scanning." }
            $secrets["generated:$($envFile.Name):$name"] = $value
            if ($name -match '(?i)connectionstring' -and $value -match '(?i)(?:password|pwd)=([^;,]+)') {
                $connectionPassword = $Matches[1]
                if ($connectionPassword.Length -ge 8) {
                    $secrets["generated:$($envFile.Name):${name}:password"] = $connectionPassword
                }
            }
        }
    }
}

function Test-OperationalValuePresent {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Value
    )

    if ($Key.EndsWith('Username', [StringComparison]::OrdinalIgnoreCase)) {
        # Usernames can be short, low-entropy business identifiers (for example a
        # device role) that legitimately occur as ordinary prose or test labels.
        # Treat the value as leaked only when it appears in a username/destination
        # configuration context; the high-entropy password is still scanned as an
        # unconditional exact value everywhere.
        $escaped = [Regex]::Escape($Value)
        $valueBoundary = '(?<![A-Za-z0-9_])' + $escaped + '(?![A-Za-z0-9_])'
        $pattern = '(?is)(?:Destination(?:__|:)?Api.{0,160}' + $valueBoundary +
            '|(?:user(?:name)?|user_name)\s*[=:]\s*["'']?' + $valueBoundary + ')'
        return [Regex]::IsMatch($Content, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    }
    return $Content.Contains($Value, [StringComparison]::Ordinal)
}

$violations = [Collections.Generic.List[string]]::new()
$filesScanned = 0
$candidateFiles = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate repository files.' }
foreach ($relativePath in $candidateFiles) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $filesScanned++
    $content = [IO.File]::ReadAllText($path)
    foreach ($entry in $secrets.GetEnumerator()) {
        if (Test-OperationalValuePresent -Content $content -Key $entry.Key -Value $entry.Value) {
            $violations.Add("Operational secret '$($entry.Key)' is present in repository file '$relativePath'.")
        }
    }
}

foreach ($additional in $AdditionalPaths) {
    $resolved = if ([IO.Path]::IsPathRooted($additional)) { $additional } else { Join-Path $repositoryRoot $additional }
    if (-not (Test-Path -LiteralPath $resolved)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $resolved -File -Recurse) {
        $filesScanned++
        $content = [IO.File]::ReadAllText($file.FullName)
        foreach ($entry in $secrets.GetEnumerator()) {
            if (Test-OperationalValuePresent -Content $content -Key $entry.Key -Value $entry.Value) {
                $violations.Add("Operational secret '$($entry.Key)' is present in a local runtime log.")
            }
        }
    }
}

$containerLogsScanned = 0
if (-not [string]::IsNullOrWhiteSpace($ComposeProject)) {
    $docker = Get-Command docker -ErrorAction Stop
    $containerIds = @(& $docker.Source ps --filter "label=com.docker.compose.project=$ComposeProject" --format '{{.ID}}')
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate scoped Docker containers.' }
    foreach ($containerId in $containerIds) {
        if ([string]::IsNullOrWhiteSpace($containerId)) { continue }
        $containerLogsScanned++
        $content = @(& $docker.Source logs $containerId 2>&1) -join "`n"
        foreach ($entry in $secrets.GetEnumerator()) {
            if (Test-OperationalValuePresent -Content $content -Key $entry.Key -Value $entry.Value) {
                $violations.Add("Operational secret '$($entry.Key)' is present in scoped Docker logs.")
            }
        }
    }
}

$historyBlobsScanned = 0
& git -C $repositoryRoot rev-parse --verify HEAD *> $null
if ($LASTEXITCODE -eq 0) {
    $objects = @(& git -C $repositoryRoot rev-list --objects --all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate Git history objects.' }
    foreach ($object in $objects) {
        $sha = ($object -split ' ', 2)[0]
        if ((& git -C $repositoryRoot cat-file -t $sha 2>$null) -ne 'blob') { continue }
        $historyBlobsScanned++
        $content = @(& git -C $repositoryRoot cat-file -p $sha 2>$null) -join "`n"
        foreach ($entry in $secrets.GetEnumerator()) {
            if (Test-OperationalValuePresent -Content $content -Key $entry.Key -Value $entry.Value) {
                $violations.Add("Operational secret '$($entry.Key)' is present in Git history blob '$sha'.")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw 'Exact operational-secret hygiene verification failed.'
}

Write-Output (
    "Exact operational-secret hygiene passed: {0} configured values; {1} files; {2} history blobs; {3} container logs." -f
    $secrets.Count,
    $filesScanned,
    $historyBlobsScanned,
    $containerLogsScanned)
