[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$excludedSegments = @(
    "$([IO.Path]::DirectorySeparatorChar).git$([IO.Path]::DirectorySeparatorChar)",
    "$([IO.Path]::DirectorySeparatorChar)bin$([IO.Path]::DirectorySeparatorChar)",
    "$([IO.Path]::DirectorySeparatorChar)obj$([IO.Path]::DirectorySeparatorChar)",
    "$([IO.Path]::DirectorySeparatorChar)artifacts$([IO.Path]::DirectorySeparatorChar)",
    "$([IO.Path]::DirectorySeparatorChar)_requirements$([IO.Path]::DirectorySeparatorChar)"
)

$markdownFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.md' -File -Recurse |
    Where-Object {
        $candidate = $_.FullName
        -not ($excludedSegments | Where-Object { $candidate.Contains($_, [StringComparison]::OrdinalIgnoreCase) })
    })
if ($markdownFiles.Count -eq 0) { throw 'No Markdown documents were found.' }

$knownPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($item in Get-ChildItem -LiteralPath $repositoryRoot -Force -Recurse) {
    $candidate = $item.FullName
    if ($excludedSegments | Where-Object { $candidate.Contains($_, [StringComparison]::OrdinalIgnoreCase) }) {
        continue
    }

    $relative = [IO.Path]::GetRelativePath($repositoryRoot, $candidate).Replace('\', '/')
    [void]$knownPaths.Add($relative)
}

$inlinePattern = [regex]('!?\[[^\]]*\]\((?<target>[^)\r\n]+)\)')
$referencePattern = [regex]'(?m)^\s*\[[^\]]+\]:\s*(?<target>\S+)'
$violations = [Collections.Generic.List[string]]::new()
$checkedLinks = 0

foreach ($file in $markdownFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    $matches = @($inlinePattern.Matches($content)) + @($referencePattern.Matches($content))
    foreach ($match in $matches) {
        $target = $match.Groups['target'].Value.Trim()
        if ($target.StartsWith('<') -and $target.Contains('>')) {
            $target = $target[1..($target.IndexOf('>') - 1)] -join ''
        }
        else {
            $target = ($target -split '\s+["'']', 2)[0]
        }

        if ($target -match '^(?i:https?|mailto|tel|data):' -or $target.StartsWith('#')) {
            continue
        }

        $target = ($target -split '#', 2)[0]
        $target = ($target -split '\?', 2)[0]
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $checkedLinks++

        try {
            $decoded = [Uri]::UnescapeDataString($target).Replace('/', [IO.Path]::DirectorySeparatorChar)
            $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $decoded))
        }
        catch {
            $line = 1 + [regex]::Matches($content.Substring(0, $match.Index), "`n").Count
            $violations.Add("$([IO.Path]::GetRelativePath($repositoryRoot, $file.FullName)):$line invalid path '$target'")
            continue
        }

        $rootPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $line = 1 + [regex]::Matches($content.Substring(0, $match.Index), "`n").Count
            $violations.Add("$([IO.Path]::GetRelativePath($repositoryRoot, $file.FullName)):$line link escapes repository '$target'")
            continue
        }

        $relativeTarget = [IO.Path]::GetRelativePath($repositoryRoot, $resolved).Replace('\', '/')
        if (-not $knownPaths.Contains($relativeTarget)) {
            $line = 1 + [regex]::Matches($content.Substring(0, $match.Index), "`n").Count
            $reason = if (Test-Path -LiteralPath $resolved) { 'path case does not match Git' } else { 'target does not exist' }
            $violations.Add("$([IO.Path]::GetRelativePath($repositoryRoot, $file.FullName)):$line $reason '$target'")
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw 'Documentation link verification failed.'
}

Write-Output "Validated $checkedLinks local links across $($markdownFiles.Count) Markdown files."
