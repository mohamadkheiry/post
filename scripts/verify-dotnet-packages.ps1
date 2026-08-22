[CmdletBinding()]
param(
    [string]$Solution = (Join-Path $PSScriptRoot '..\Post.sln')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dotnetCommand = if ($env:POST_DOTNET) { $env:POST_DOTNET } else { 'dotnet' }
$solutionPath = [IO.Path]::GetFullPath($Solution)
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution was not found: $solutionPath"
}
$repositoryRoot = Split-Path -Parent $solutionPath

$projectFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.csproj' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](?:artifacts|bin|obj|\.secrets)[\\/]' })
$directPackageReferences = [Collections.Generic.List[object]]::new()
foreach ($projectFile in $projectFiles) {
    [xml]$project = Get-Content -LiteralPath $projectFile.FullName -Raw
    foreach ($reference in @($project.SelectNodes('//PackageReference'))) {
        $version = if ($reference.HasAttribute('Version')) {
            $reference.GetAttribute('Version')
        }
        else {
            [string]$reference.Version
        }
        $directPackageReferences.Add([pscustomobject]@{
            Project = $projectFile.BaseName
            Package = $reference.GetAttribute('Include')
            Version = $version
        })
    }
}
$missingLockFiles = @($projectFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'packages.lock.json') -PathType Leaf)
})
if ($missingLockFiles.Count -gt 0) {
    throw "Every project must commit packages.lock.json; missing for: $([string]::Join(', ', @($missingLockFiles.BaseName)))."
}
$nonReproducibleReferences = @(
    $directPackageReferences |
        Where-Object {
            [string]::IsNullOrWhiteSpace($_.Package) -or
            $_.Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$'
        }
)
if ($nonReproducibleReferences.Count -gt 0) {
    $descriptions = @($nonReproducibleReferences | ForEach-Object {
        "$($_.Project):$($_.Package)@$($_.Version)"
    })
    throw "Direct PackageReference versions must be exact stable values: $([string]::Join(', ', $descriptions))."
}

function Find-ObjectsWithNonEmptyProperty {
    param(
        [AllowNull()][object]$Node,
        [Parameter(Mandatory)][string]$PropertyName
    )

    if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsPrimitive) {
        return
    }
    if ($Node -is [Collections.IEnumerable] -and $Node -isnot [Management.Automation.PSCustomObject]) {
        foreach ($item in $Node) {
            Find-ObjectsWithNonEmptyProperty -Node $item -PropertyName $PropertyName
        }
        return
    }

    $property = $Node.PSObject.Properties[$PropertyName]
    if ($null -ne $property -and @($property.Value).Count -gt 0) {
        Write-Output $Node
    }
    foreach ($childProperty in $Node.PSObject.Properties) {
        Find-ObjectsWithNonEmptyProperty -Node $childProperty.Value -PropertyName $PropertyName
    }
}

function Invoke-PackageAudit {
    param(
        [Parameter(Mandatory)][ValidateSet('vulnerable', 'deprecated')][string]$Kind,
        [Parameter(Mandatory)][string]$FindingProperty
    )

    $standardErrorPath = [IO.Path]::GetTempFileName()
    try {
        $arguments = @(
            'package', 'list',
            '--project', $solutionPath,
            "--$Kind",
            '--include-transitive',
            '--format', 'json',
            '--output-version', '1',
            '--no-restore'
        )
        $standardOutput = @(& $dotnetCommand @arguments 2> $standardErrorPath)
        if ($LASTEXITCODE -ne 0) {
            $standardError = Get-Content -LiteralPath $standardErrorPath -Raw
            throw "NuGet $Kind audit failed with exit code $LASTEXITCODE. $standardError"
        }

        $jsonText = $standardOutput -join [Environment]::NewLine
        if ([string]::IsNullOrWhiteSpace($jsonText)) {
            throw "NuGet $Kind audit returned no machine-readable result."
        }
        $report = $jsonText | ConvertFrom-Json -Depth 100
        $problemsProperty = $report.PSObject.Properties['problems']
        if ($null -ne $problemsProperty -and @($problemsProperty.Value).Count -gt 0) {
            throw "NuGet $Kind audit reported $(@($problemsProperty.Value).Count) acquisition/analysis problem(s)."
        }
        $projectsProperty = $report.PSObject.Properties['projects']
        if ($null -eq $projectsProperty -or @($projectsProperty.Value).Count -eq 0) {
            throw "NuGet $Kind audit did not inspect any project."
        }

        $findings = @(Find-ObjectsWithNonEmptyProperty -Node $report -PropertyName $FindingProperty)
        if ($findings.Count -gt 0) {
            $packageNames = @(
                $findings |
                    ForEach-Object {
                        $packageId = if ($_.PSObject.Properties['id']) {
                            [string]$_.id
                        }
                        else {
                            '<unknown-package>'
                        }
                        $versionProperty = $_.PSObject.Properties['resolvedVersion']
                        if ($null -ne $versionProperty -and
                            -not [string]::IsNullOrWhiteSpace([string]$versionProperty.Value)) {
                            "$packageId@$($versionProperty.Value)"
                        }
                        else {
                            $packageId
                        }
                    } |
                    Sort-Object -Unique
            )
            throw "NuGet $Kind audit found $($findings.Count) affected package reference(s): $([string]::Join(', ', $packageNames))."
        }

        return @($projectsProperty.Value).Count
    }
    finally {
        Remove-Item -LiteralPath $standardErrorPath -Force -ErrorAction SilentlyContinue
    }
}

$vulnerabilityProjectCount = Invoke-PackageAudit -Kind vulnerable -FindingProperty vulnerabilities
$deprecatedProjectCount = Invoke-PackageAudit -Kind deprecated -FindingProperty deprecationReasons
if ($vulnerabilityProjectCount -ne $deprecatedProjectCount) {
    throw 'NuGet vulnerability and deprecation audits inspected different project counts.'
}

Write-Output "NuGet audit passed for $vulnerabilityProjectCount projects and $($directPackageReferences.Count) exact direct references: no known vulnerable or deprecated packages."
