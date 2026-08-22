[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$diagramRoot = Join-Path $repositoryRoot 'docs/diagrams'

if (-not (Test-Path -LiteralPath $diagramRoot)) {
    throw "Diagram directory does not exist: $diagramRoot"
}

$sources = @(Get-ChildItem -LiteralPath $diagramRoot -Filter '*.mmd' -Recurse)
if ($sources.Count -eq 0) { throw 'No Mermaid diagram sources were found.' }

foreach ($source in $sources) {
    $svg = [IO.Path]::ChangeExtension($source.FullName, '.svg')
    if (-not (Test-Path -LiteralPath $svg)) {
        throw "Rendered SVG is missing for $($source.FullName)."
    }
    if ((Get-Item -LiteralPath $svg).LastWriteTimeUtc -lt $source.LastWriteTimeUtc) {
        throw "Rendered SVG is older than its Mermaid source: $($source.FullName)."
    }
}

Write-Output "Validated $($sources.Count) Mermaid source/render pairs."
