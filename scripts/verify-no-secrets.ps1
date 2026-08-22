[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$candidateFiles = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate repository files.' }
if ($candidateFiles.Count -eq 0) { throw 'No tracked or untracked repository files were found to scan.' }

$forbiddenFilePatterns = @(
    '(^|/|\\)\.env($|\.)',
    '(^|/|\\)secrets(/|\\)',
    '\.(pfx|p12|key)$',
    'appsettings\.Local\.json$'
)

$violations = [Collections.Generic.List[string]]::new()
foreach ($file in $candidateFiles) {
    foreach ($pattern in $forbiddenFilePatterns) {
        if ($file -match $pattern -and $file -notmatch '\.env\.example$') {
            $violations.Add("Forbidden secret-bearing path is tracked: $file")
        }
    }
}

$contentPatterns = @(
    '(?i)-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)gh[pousr]_[A-Za-z0-9]{30,}',
    '(?i)AKIA[0-9A-Z]{16}',
    '(?i)(password|client_secret|secret_key|signing_key)\s*[=:]\s*(?<quote>["''])(?!(invalid-|dummy|replace|example|test))(?![^"'']*[$<{])[^"'']{8,}\k<quote>',
    '(?i)Server\s*=.+;(User Id|UID)\s*=.+;(Password|PWD)\s*=\s*(?![$<{]|invalid|dummy|replace|example|test|synthetic)[^;\r\n]{8,}'
)

foreach ($file in $candidateFiles) {
    if ($file -eq 'scripts/verify-no-secrets.ps1') { continue }
    $fullPath = Join-Path $repositoryRoot $file
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
    $extension = [IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($extension -notin @(
            '.cs', '.json', '.yml', '.yaml', '.md', '.ps1', '.sh', '.xml',
            '.props', '.targets', '.http', '.txt', '.csv', '.example', '.config',
            '.mmd', '.svg', '.toml')) { continue }
    $content = Get-Content -LiteralPath $fullPath -Raw -ErrorAction SilentlyContinue
    foreach ($pattern in $contentPatterns) {
        if ($content -match $pattern) {
            $violations.Add("Potential inline secret in $file (pattern intentionally not echoed).")
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw 'Secret hygiene verification failed.'
}

Write-Output "Secret hygiene verification passed for $($candidateFiles.Count) repository files."
