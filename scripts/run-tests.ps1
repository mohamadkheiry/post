[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = "artifacts/test-results/$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))",
    [ValidateRange(0, 100)]
    [decimal]$MinimumLineCoverage = 80,
    [ValidateRange(0, 100)]
    [decimal]$MinimumBranchCoverage = 70,
    [ValidateRange(0, 100)]
    [decimal]$MinimumMethodCoverage = 85,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repositoryRoot $OutputRoot
$dotnetCommand = if ($env:POST_DOTNET) { $env:POST_DOTNET } else { 'dotnet' }

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$restoreArguments = @('restore', (Join-Path $repositoryRoot 'Post.sln'), '--locked-mode')
if (-not $NoRestore) {
    & $dotnetCommand @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
}

& $dotnetCommand tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE." }

$buildArguments = @('build', (Join-Path $repositoryRoot 'Post.sln'), '--configuration', $Configuration, '--no-restore')
& $dotnetCommand @buildArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

$testExitCode = 0
$testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Filter '*.csproj' -Recurse | Sort-Object FullName)
if ($testProjects.Count -eq 0) { throw 'No test projects were found.' }
foreach ($testProject in $testProjects) {
    $projectName = [IO.Path]::GetFileNameWithoutExtension($testProject.Name)
    $projectOutput = Join-Path $outputPath $projectName
    New-Item -ItemType Directory -Force -Path $projectOutput | Out-Null
    $testArguments = @(
        'test', $testProject.FullName,
        '--configuration', $Configuration,
        '--no-build',
        '--logger', "trx;LogFileName=$projectName.trx",
        '--results-directory', $projectOutput,
        '--collect', 'XPlat Code Coverage',
        '--',
        'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura',
        'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.SkipAutoProps=true',
        'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByAttribute=GeneratedCodeAttribute,ExcludeFromCodeCoverageAttribute',
        'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile=**/Migrations/**/*.cs,**/*ModelSnapshot.cs'
    )
    & $dotnetCommand @testArguments
    if ($LASTEXITCODE -ne 0) { $testExitCode = $LASTEXITCODE }
}

$trxFiles = @(Get-ChildItem -Path $outputPath -Filter '*.trx' -Recurse)
if ($trxFiles.Count -ne $testProjects.Count) {
    throw "Expected $($testProjects.Count) TRX files but found $($trxFiles.Count)."
}

$rawCoverageFileCount = 0
$coverageFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($testProject in $testProjects) {
    $projectName = [IO.Path]::GetFileNameWithoutExtension($testProject.Name)
    $projectOutput = Join-Path $outputPath $projectName
    $projectCoverageFiles = @(
        Get-ChildItem -LiteralPath $projectOutput -Filter 'coverage.cobertura.xml' -Recurse |
            Sort-Object FullName)
    $rawCoverageFileCount += $projectCoverageFiles.Count
    if ($projectCoverageFiles.Count -eq 0) {
        throw "Coverage output is missing for test project '$projectName'."
    }

    $coverageHashes = @(
        $projectCoverageFiles |
            ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } |
            Sort-Object -Unique)
    if ($coverageHashes.Count -ne 1) {
        throw "Test project '$projectName' produced conflicting Cobertura artifacts."
    }

    # xUnit v3 may materialize the same collector payload in both its UUID result
    # directory and the Microsoft Testing Platform result hierarchy. Select one
    # only after proving every copy is byte-identical.
    $coverageFiles.Add($projectCoverageFiles[0])
}
if ($coverageFiles.Count -ne $testProjects.Count) {
    throw "Expected $($testProjects.Count) canonical Cobertura files but found $($coverageFiles.Count)."
}

$totals = [ordered]@{ total = 0; executed = 0; passed = 0; failed = 0; skipped = 0 }
$projectSummaries = [System.Collections.Generic.List[object]]::new()
$testInventory = [System.Collections.Generic.List[object]]::new()
foreach ($trxFile in $trxFiles) {
    [xml]$trx = Get-Content -LiteralPath $trxFile.FullName
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) { continue }
    $totals.total += [int]$counters.total
    $totals.executed += [int]$counters.executed
    $totals.passed += [int]$counters.passed
    $totals.failed += [int]$counters.failed
    $totals.skipped += [int]$counters.notExecuted

    $project = [IO.Path]::GetFileNameWithoutExtension($trxFile.Name)
    $projectSummaries.Add([pscustomobject]@{
        Project = $project
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        Skipped = [int]$counters.notExecuted
        Total = [int]$counters.total
    })
    foreach ($result in @($trx.TestRun.Results.UnitTestResult)) {
        if ($null -eq $result) { continue }
        $testInventory.Add([pscustomobject]@{
            Project = $project
            Test = [string]$result.testName
            Outcome = [string]$result.outcome
            Duration = [string]$result.duration
        })
    }
}

$testInventory |
    Sort-Object Project, Test |
    Export-Csv -LiteralPath (Join-Path $outputPath 'test-inventory.csv') -NoTypeInformation -Encoding utf8

$reportOutput = Join-Path $outputPath 'coverage-report'
$reportArguments = @(
    'tool', 'run', 'reportgenerator', '--',
    "-reports:$($coverageFiles.FullName -join ';')",
    "-targetdir:$reportOutput",
    '-reporttypes:Cobertura;JsonSummary;MarkdownSummaryGithub'
)
& $dotnetCommand @reportArguments
if ($LASTEXITCODE -ne 0) { throw "Coverage report merge failed with exit code $LASTEXITCODE." }

$mergedCoveragePath = Join-Path $reportOutput 'Cobertura.xml'
if (-not (Test-Path -LiteralPath $mergedCoveragePath)) {
    throw "Merged Cobertura report was not created at $mergedCoveragePath."
}
[xml]$coverage = Get-Content -LiteralPath $mergedCoveragePath
$coveredLines = [int]$coverage.coverage.'lines-covered'
$validLines = [int]$coverage.coverage.'lines-valid'
$coveredBranches = [int]$coverage.coverage.'branches-covered'
$validBranches = [int]$coverage.coverage.'branches-valid'
$productionProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Filter '*.csproj' -Recurse
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tools') -Filter '*.csproj' -Recurse
)
$coverageBypassAttributes = @(
    Get-ChildItem -Path (Join-Path $repositoryRoot 'src'), (Join-Path $repositoryRoot 'tools') -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/]Migrations[\\/]' } |
        Select-String -Pattern 'ExcludeFromCodeCoverage(Attribute)?|GeneratedCode(Attribute)?|CompilerGenerated(Attribute)?'
)
if ($coverageBypassAttributes.Count -gt 0) {
    throw 'Production source contains a coverage-bypass attribute; refactor or test the code instead.'
}
$instrumentedAssemblies = [Collections.Generic.HashSet[string]]::new(
    [string[]]@($coverage.coverage.packages.package | ForEach-Object { [string]$_.name }),
    [StringComparer]::Ordinal)
$missingProductionAssemblies = @(
    $productionProjects |
        Where-Object { -not $instrumentedAssemblies.Contains($_.BaseName) } |
        ForEach-Object BaseName |
        Sort-Object -Unique
)

$instrumentedMethods = @(
    $coverage.coverage.packages.package.classes.class |
        ForEach-Object {
            $coverageClass = $_
            @($coverageClass.methods.method) | ForEach-Object {
                [pscustomobject]@{
                    Class = [string]$coverageClass.name
                    File = [string]$coverageClass.filename
                    Name = [string]$_.name
                    Signature = [string]$_.signature
                    Line = [int]@($_.lines.line)[0].number
                    Covered = [double]$_.'line-rate' -gt 0
                }
            }
        }
)
$compilerGeneratedStaticConstructors = @(
    $instrumentedMethods |
        Where-Object { -not $_.Covered -and $_.Name -eq '.cctor' } |
        Where-Object {
            $sourcePath = if ([IO.Path]::IsPathRooted($_.File)) {
                $_.File
            }
            else {
                Join-Path $repositoryRoot $_.File
            }
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                return $false
            }

            $className = ($_.Class -split '[.+]')[-1]
            $escapedClassName = [Regex]::Escape($className)
            $source = Get-Content -LiteralPath $sourcePath -Raw
            return $source -notmatch "(?m)\bstatic\s+$escapedClassName\s*\("
        }
)
$compilerGeneratedStaticConstructorKeys = [Collections.Generic.HashSet[string]]::new(
    [string[]]($compilerGeneratedStaticConstructors | ForEach-Object {
        "$($_.Class)|$($_.Name)|$($_.Signature)|$($_.Line)"
    }),
    [StringComparer]::Ordinal)
$handwrittenMethods = @(
    $instrumentedMethods |
        Where-Object {
            -not $compilerGeneratedStaticConstructorKeys.Contains(
                "$($_.Class)|$($_.Name)|$($_.Signature)|$($_.Line)")
        }
)
$uncoveredHandwrittenMethods = @($handwrittenMethods | Where-Object { -not $_.Covered })
$handwrittenMethodCount = $handwrittenMethods.Count
$coveredHandwrittenMethodCount = @($handwrittenMethods | Where-Object Covered).Count

$lineRate = if ($validLines -eq 0) { 0 } else { [math]::Round(100 * $coveredLines / $validLines, 2) }
$branchRate = if ($validBranches -eq 0) { 0 } else { [math]::Round(100 * $coveredBranches / $validBranches, 2) }
$jsonSummaryPath = Join-Path $reportOutput 'Summary.json'
if (-not (Test-Path -LiteralPath $jsonSummaryPath)) {
    throw "ReportGenerator summary was not created at $jsonSummaryPath."
}
$jsonSummary = Get-Content -LiteralPath $jsonSummaryPath -Raw | ConvertFrom-Json -Depth 100
$methodRate = [math]::Round([decimal]$jsonSummary.summary.methodcoverage, 2)
$sdkVersion = (& $dotnetCommand --version).Trim()
$commit = (& git -C $repositoryRoot rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = 'uncommitted' }
$workingTreeStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
$generatedAt = [DateTimeOffset]::UtcNow.ToString('O')

$environment = [ordered]@{
    generatedAtUtc = $generatedAt
    commit = $commit
    configuration = $Configuration
    dotnetSdk = $sdkVersion
    operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    workingTreeDirty = $workingTreeStatus.Count -gt 0
    command = 'One dotnet test invocation per tests/*.csproj with XPlat Code Coverage'
    minimumLineCoverage = $MinimumLineCoverage
    minimumBranchCoverage = $MinimumBranchCoverage
    minimumMethodCoverage = $MinimumMethodCoverage
    handwrittenMethods = $handwrittenMethodCount
    coveredHandwrittenMethods = $coveredHandwrittenMethodCount
    uncoveredHandwrittenMethods = $uncoveredHandwrittenMethods.Count
    excludedCompilerGeneratedStaticConstructors = $compilerGeneratedStaticConstructors.Count
    rawCoverageFiles = $rawCoverageFileCount
    canonicalCoverageFiles = $coverageFiles.Count
    productionAssemblies = $productionProjects.Count
    instrumentedProductionAssemblies = $productionProjects.Count - $missingProductionAssemblies.Count
}
$environment | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'environment.json') -Encoding utf8

$projectRows = ($projectSummaries | Sort-Object Project | ForEach-Object {
    "| $($_.Project) | $($_.Passed) | $($_.Failed) | $($_.Skipped) | $($_.Total) |"
}) -join [Environment]::NewLine

$summary = @"
# Automated test run

- Generated (UTC): $generatedAt
- Commit: $commit
- .NET SDK: $sdkVersion
- Configuration: $Configuration
- Passed: $($totals.passed)
- Failed: $($totals.failed)
- Skipped/not executed: $($totals.skipped)
- Total: $($totals.total)
- Aggregated line coverage: $lineRate%
- Aggregated branch coverage: $branchRate%
- Aggregated method coverage: $methodRate%
- Covered handwritten methods: $coveredHandwrittenMethodCount/$handwrittenMethodCount
- Entirely uncovered handwritten methods: $($uncoveredHandwrittenMethods.Count)
- Excluded compiler-generated static constructors: $($compilerGeneratedStaticConstructors.Count)
- Instrumented production assemblies: $($productionProjects.Count - $missingProductionAssemblies.Count)/$($productionProjects.Count)
- TRX files: $($trxFiles.Count)
- Cobertura files: $($coverageFiles.Count) canonical / $rawCoverageFileCount raw byte-identical copies
- Test projects: $($testProjects.Count)
- Required line coverage: $MinimumLineCoverage%
- Required branch coverage: $MinimumBranchCoverage%
- Required method coverage: $MinimumMethodCoverage%

| Test project | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
$projectRows

Coverage is merged by ReportGenerator before calculating percentages, so assemblies exercised by multiple suites are counted once. Generated code, EF migrations, and trivial auto-properties are excluded from behavioral coverage. Composition roots and endpoint handlers remain instrumented and must be exercised through host/integration tests. No failed or silently skipped test is accepted.
"@
$summary | Set-Content -LiteralPath (Join-Path $outputPath 'summary.md') -Encoding utf8

$uncoveredReportPath = $null
if ($uncoveredHandwrittenMethods.Count -gt 0) {
    $uncoveredReportPath = Join-Path $outputPath 'uncovered-handwritten-methods.csv'
    $uncoveredHandwrittenMethods |
        Select-Object Class, Name, Signature, File, Line |
        Export-Csv -LiteralPath $uncoveredReportPath -NoTypeInformation -Encoding utf8
}

$hashTargets = @(Get-ChildItem -Path $outputPath -File -Recurse | Where-Object { $_.Name -ne 'checksums.sha256' })
$hashLines = foreach ($target in $hashTargets) {
    $relative = [IO.Path]::GetRelativePath($outputPath, $target.FullName).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
$hashLines | Sort-Object | Set-Content -LiteralPath (Join-Path $outputPath 'checksums.sha256') -Encoding ascii

if ($testExitCode -ne 0 -or $totals.failed -gt 0 -or $totals.skipped -gt 0) {
    throw "Automated tests failed. See $outputPath."
}
if ($missingProductionAssemblies.Count -gt 0) {
    throw "Coverage did not instrument production assemblies: $([string]::Join(', ', $missingProductionAssemblies))."
}
if ($uncoveredHandwrittenMethods.Count -gt 0) {
    throw "Handwritten method coverage failed: $($uncoveredHandwrittenMethods.Count) methods were never exercised. See $uncoveredReportPath."
}
if ($lineRate -lt $MinimumLineCoverage -or
    $branchRate -lt $MinimumBranchCoverage -or
    $methodRate -lt $MinimumMethodCoverage) {
    throw "Coverage gate failed (line $lineRate% / branch $branchRate% / method $methodRate%). See $outputPath."
}

Write-Output "Tests passed: $($totals.passed); line coverage: $lineRate%; branch coverage: $branchRate%; method coverage: $methodRate%."
