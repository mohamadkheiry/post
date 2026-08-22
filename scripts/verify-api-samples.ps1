[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$jsonFiles = @(
    'docs/api/post-platform.postman_collection.json',
    'docs/api/post-platform.postman_environment.example.json'
)

$parsedJson = @{}
foreach ($relativePath in $jsonFiles) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required API sample is missing: $relativePath"
    }

    $json = Get-Content -LiteralPath $path -Raw
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        $parsedJson[$relativePath] = $json | ConvertFrom-Json -Depth 100
    }
    else {
        # Windows PowerShell 5.1 has no ConvertFrom-Json -Depth parameter.
        $parsedJson[$relativePath] = $json | ConvertFrom-Json
    }
}

$httpPath = Join-Path $repositoryRoot 'docs/api/post-platform.http'
if (-not (Test-Path -LiteralPath $httpPath -PathType Leaf)) {
    throw 'Required HTTP request collection is missing.'
}

$httpRequests = @(
    Select-String -LiteralPath $httpPath -Pattern '^(GET|POST|PUT|PATCH|DELETE)\s+(\S+)' |
        ForEach-Object {
            $match = [regex]::Match($_.Line, '^(GET|POST|PUT|PATCH|DELETE)\s+(\S+)')
            [pscustomobject]@{
                Method = $match.Groups[1].Value.ToUpperInvariant()
                Url = $match.Groups[2].Value.TrimEnd('/')
            }
        }
)
if ($httpRequests.Count -eq 0) {
    throw 'The HTTP request collection contains no executable requests.'
}

function Get-PostmanRequests {
    param([Parameter(Mandatory)] [object[]] $Items)

    foreach ($item in $Items) {
        if ($null -ne $item.request) {
            $url = if ($item.request.url -is [string]) {
                $item.request.url
            }
            elseif ($null -ne $item.request.url.raw) {
                $item.request.url.raw
            }
            else {
                throw "Postman request '$($item.name)' has no raw URL."
            }

            [pscustomobject]@{
                Name = [string]$item.name
                Method = ([string]$item.request.method).ToUpperInvariant()
                Url = ([string]$url).Trim().TrimEnd('/')
            }
        }

        if ($null -ne $item.item) {
            Get-PostmanRequests -Items @($item.item)
        }
    }
}

function Get-RequestInventory {
    param([Parameter(Mandatory)] [object[]] $Requests)

    $inventory = @{}
    foreach ($request in $Requests) {
        if ([string]::IsNullOrWhiteSpace($request.Method) -or [string]::IsNullOrWhiteSpace($request.Url)) {
            throw 'An API sample contains an empty method or URL.'
        }

        $key = "$($request.Method) $($request.Url)"
        if (-not $inventory.ContainsKey($key)) {
            $inventory[$key] = 0
        }

        $inventory[$key]++
    }

    return $inventory
}

$collectionPath = 'docs/api/post-platform.postman_collection.json'
$collection = $parsedJson[$collectionPath]
$postmanRequests = @(Get-PostmanRequests -Items @($collection.item))
if ($postmanRequests.Count -eq 0) {
    throw 'The Postman collection contains no executable requests.'
}

$duplicateNames = @(
    $postmanRequests |
        Group-Object -Property Name |
        Where-Object Count -gt 1 |
        ForEach-Object Name
)
if ($duplicateNames.Count -gt 0) {
    throw "Postman request names must be unique. Duplicates: $($duplicateNames -join ', ')"
}

$httpInventory = Get-RequestInventory -Requests $httpRequests
$postmanInventory = Get-RequestInventory -Requests $postmanRequests
$allKeys = @($httpInventory.Keys + $postmanInventory.Keys | Sort-Object -Unique)
$differences = @(
    foreach ($key in $allKeys) {
        $httpCount = if ($httpInventory.ContainsKey($key)) { $httpInventory[$key] } else { 0 }
        $postmanCount = if ($postmanInventory.ContainsKey($key)) { $postmanInventory[$key] } else { 0 }
        if ($httpCount -ne $postmanCount) {
            "$key (HTTP=$httpCount, Postman=$postmanCount)"
        }
    }
)

if ($differences.Count -gt 0) {
    throw "HTTP and Postman request inventories differ:`n$($differences -join [Environment]::NewLine)"
}

Write-Output "Validated $($jsonFiles.Count) Postman JSON files and matched $($httpRequests.Count) HTTP/Postman requests by method and URL."
