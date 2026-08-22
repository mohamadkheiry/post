[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\.secrets\full-dev'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-RandomHex {
    param([ValidateRange(8, 128)][int]$Bytes)

    $buffer = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    return [Convert]::ToHexString($buffer).ToLowerInvariant()
}

function New-StrongPassword {
    return 'Aa1!' + (New-RandomHex -Bytes 28)
}

function Write-PrivateEnvFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Values
    )

    $lines = foreach ($entry in $Values.GetEnumerator()) {
        $value = [string]$entry.Value
        if ($value.Contains("`r", [StringComparison]::Ordinal) -or
            $value.Contains("`n", [StringComparison]::Ordinal)) {
            throw "Environment value '$($entry.Key)' contains a line break."
        }

        '{0}={1}' -f $entry.Key, $value
    }

    [System.IO.File]::WriteAllLines(
        $Path,
        [string[]]$lines,
        [System.Text.UTF8Encoding]::new($false))
    if (-not $IsWindows) {
        [System.IO.File]::SetUnixFileMode(
            $Path,
            [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    $existing = Get-ChildItem -LiteralPath $resolvedOutput -File -ErrorAction SilentlyContinue
    if ($existing.Count -gt 0) {
        throw "The full-dev secret directory already contains files. Use -Force to rotate all synthetic values."
    }
}

[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$sqlPassword = New-StrongPassword
$redisPassword = New-StrongPassword
$minioAccessKey = 'post' + (New-RandomHex -Bytes 8)
$minioSecretKey = New-StrongPassword
$minioKmsKey = [Convert]::ToBase64String(
    [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$signingKey = [Convert]::ToBase64String(
    [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$adminPassword = New-StrongPassword
$edgeAdminPassword = New-StrongPassword
$edgeSqliteKey = New-RandomHex -Bytes 32
$grafanaPassword = New-StrongPassword
$agCertificatePassword = New-StrongPassword
$sqlTdeCertificatePassword = New-StrongPassword

$sqlConnectionTemplate = 'Server=sql-primary,1433;Database={0};User ID=sa;Password={1};Encrypt=True;TrustServerCertificate=True;Connect Timeout=30;MultipleActiveResultSets=False'
$redisUser = 'post-core'
$coreValues = [ordered]@{
    Auth__Issuer = 'post-core-identity'
    Auth__Audience = 'post-platform'
    Auth__SigningKey = $signingKey
    ConnectionStrings__ParcelLifecycle = ($sqlConnectionTemplate -f 'PostParcelLifecycle', $sqlPassword)
    ConnectionStrings__DeviceConfig = ($sqlConnectionTemplate -f 'PostDeviceConfig', $sqlPassword)
    ConnectionStrings__EdgeHealth = ($sqlConnectionTemplate -f 'PostEdgeHealth', $sqlPassword)
    ConnectionStrings__OperationalResult = ($sqlConnectionTemplate -f 'PostOperationalResult', $sqlPassword)
    ConnectionStrings__Identity = ($sqlConnectionTemplate -f 'PostIdentity', $sqlPassword)
    Redis__ConnectionString = "redis:6379,user=$redisUser,password=$redisPassword,abortConnect=false,connectTimeout=5000,asyncTimeout=2000,syncTimeout=2000"
    Core__S3__AccessKey = $minioAccessKey
    Core__S3__SecretKey = $minioSecretKey
    Identity__InitialAdmin__Enabled = 'true'
    Identity__InitialAdmin__UserName = 'full-dev-admin'
    Identity__InitialAdmin__Password = $adminPassword
    Identity__InitialAdmin__Roles__0 = 'SystemAdministrator'
}
$sqlValues = [ordered]@{
    ACCEPT_EULA = 'Y'
    MSSQL_PID = 'Developer'
    MSSQL_SA_PASSWORD = $sqlPassword
    POST_AG_CERT_PASSWORD = $agCertificatePassword
    POST_SQL_TDE_CERT_PASSWORD = $sqlTdeCertificatePassword
}
$redisValues = [ordered]@{
    POST_REDIS_USER = $redisUser
    POST_REDIS_PASSWORD = $redisPassword
}
$minioValues = [ordered]@{
    MINIO_ROOT_USER = $minioAccessKey
    MINIO_ROOT_PASSWORD = $minioSecretKey
    MINIO_KMS_SECRET_KEY = "post-full-dev-key:$minioKmsKey"
}
$edgeValues = [ordered]@{
    Edge__LocalAdmin__InitialPassword = $edgeAdminPassword
    Edge__CoreApi__LogicalDeviceCode = 'FULLDEV-EDGE-01'
    Edge__CoreApi__DeviceSecret = (New-StrongPassword)
    POST_EDGE_SQLITE_KEY = $edgeSqliteKey
}
$grafanaValues = [ordered]@{
    GF_SECURITY_ADMIN_USER = 'full-dev-admin'
    GF_SECURITY_ADMIN_PASSWORD = $grafanaPassword
    GF_SECURITY_SECRET_KEY = (New-StrongPassword)
}

Write-PrivateEnvFile -Path (Join-Path $resolvedOutput 'core.env') -Values $coreValues
Write-PrivateEnvFile -Path (Join-Path $resolvedOutput 'sql.env') -Values $sqlValues
Write-PrivateEnvFile -Path (Join-Path $resolvedOutput 'redis.env') -Values $redisValues
Write-PrivateEnvFile -Path (Join-Path $resolvedOutput 'minio.env') -Values $minioValues
Write-PrivateEnvFile -Path (Join-Path $resolvedOutput 'edge.env') -Values $edgeValues
Write-PrivateEnvFile -Path (Join-Path $resolvedOutput 'grafana.env') -Values $grafanaValues

Write-Output "Synthetic full-dev secret files initialized at the ignored .secrets/full-dev path."
