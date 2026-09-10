param(
    [string]$CasesPath = "$PSScriptRoot\intrinsic.json",
    [string[]]$AdditionalCasesPath,
    [string]$ResultsPath = "$PSScriptRoot\results\benchmark-intrinsic-results.json",
    [string[]]$Labels,
    [string]$AssemblyPath,
    [switch]$IncludeLive,
    [int]$CaseTimeoutSeconds = 180,
    [ValidateSet('intrinsic', 'intrinsic-sensor', 'taxonomy-discovery', 'fact-check')]
    [string]$Pipeline = 'intrinsic'
)

$CoreRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'WritingExpectations.ps1')
if (-not [IO.Path]::IsPathRooted($ResultsPath) -and -not [IO.Path]::GetDirectoryName($ResultsPath)) { $ResultsPath = Join-Path $PSScriptRoot ('results/' + $ResultsPath) }
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ResultsPath))) -Force | Out-Null

$cases = @((Get-Content $CasesPath -Raw | ConvertFrom-Json))
foreach ($additionalPath in $AdditionalCasesPath) {
    $cases += @(Get-Content $additionalPath -Raw | ConvertFrom-Json)
}
if ($Labels.Count -gt 0) {
    $cases = @($cases | Where-Object { $_.label -in $Labels })
} else {
    $cases = @($cases | Where-Object { $_.scored -or $IncludeLive })
}
if ($cases.Count -eq 0) {
    throw 'No benchmark cases matched the requested labels.'
}
$results = [System.Collections.Generic.List[object]]::new()
$artificialCases = @($cases | Where-Object { $_.source -eq 'artificial' })
$snapshotCases = @($cases | Where-Object { $_.source -eq 'snapshot' })
$artificialServer = $null
$snapshotServer = $null
if ($artificialCases.Count -gt 0) {
    $fixtureRoot = 'C:\Dev\versa\versa-web\test-site'
    $artificialServer = Start-Process -FilePath python -ArgumentList '-m', 'http.server', '8088', '--directory', $fixtureRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
}
if ($snapshotCases.Count -gt 0) {
    $snapshotServer = Start-Process -FilePath python -ArgumentList '-m', 'http.server', '8089', '--directory', "$PSScriptRoot\snapshots" -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
}
try {
foreach ($case in $cases) {
    $url = if ($case.source -eq 'artificial') { "http://127.0.0.1:8088/pages/$($case.artifact)" } elseif ($case.source -eq 'snapshot') { "http://127.0.0.1:8089/$($case.artifact)" } else { $case.url }
    $requiredIssues = @($case.expectedFindings | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $allowedIssues = @($case.allowedFindings | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $forbiddenIssues = @($case.forbiddenIssues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $scored = [bool]$case.scored -and $Pipeline -ne 'taxonomy-discovery'
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $CoreRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($AssemblyPath) {
        [void]$startInfo.ArgumentList.Add((Resolve-Path -LiteralPath $AssemblyPath).Path)
    } else {
        [void]$startInfo.ArgumentList.Add('run')
        [void]$startInfo.ArgumentList.Add('--project')
        [void]$startInfo.ArgumentList.Add($CoreRoot)
        [void]$startInfo.ArgumentList.Add('--no-build')
        [void]$startInfo.ArgumentList.Add('--')
    }
    [void]$startInfo.ArgumentList.Add('--pipeline')
    [void]$startInfo.ArgumentList.Add($Pipeline)
    if ($case.source -eq 'frozen') {
        [void]$startInfo.ArgumentList.Add('--snapshot')
        $capturePath = if ([System.IO.Path]::IsPathRooted($case.captureArtifact)) { $case.captureArtifact } else { Join-Path $CoreRoot $case.captureArtifact }
        [void]$startInfo.ArgumentList.Add($capturePath)
    } else { [void]$startInfo.ArgumentList.Add($url) }
    [void]$startInfo.ArgumentList.Add('en')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $completed = $process.WaitForExit($CaseTimeoutSeconds * 1000)
    if (-not $completed) {
        $process.Kill($true)
        $process.WaitForExit()
        $results.Add([pscustomobject]@{
            label = $case.label
            source = $case.source
            url = $url
            expectedClassification = $case.expectedClassification
            classification = $null
            requiredIssues = $requiredIssues
            allowedIssues = $allowedIssues
            forbiddenIssues = $forbiddenIssues
            issues = @()
            scored = $scored
            passed = if ($scored) { $false } else { $null }
            error = "Benchmark case timed out after $CaseTimeoutSeconds seconds."
        })
        $results | ConvertTo-Json -Depth 12 | Set-Content $ResultsPath -Encoding utf8
        continue
    }

    $raw = $standardOutput.GetAwaiter().GetResult()
    $stderr = $standardError.GetAwaiter().GetResult()
    try {
        $result = $raw.Trim() | ConvertFrom-Json -ErrorAction Stop
        $analysis = $result.analysis
        $executionError = $result.llm.error
        if (-not $executionError) { $executionError = $analysis.pipelineError }
        if (-not $executionError -and $process.ExitCode -ne 0) { $executionError = "Process exited with code $($process.ExitCode): $stderr" }
        $assessmentInvalid = $analysis.titlePromiseValidation.status -eq 'invalid'
        $pageSummary = [string]$analysis.observations.pageSummary
        $recommendationsComplete = @($analysis.issues | Where-Object {
            $recommendation = $_.recommendation
            $null -eq $recommendation -or
            [string]::IsNullOrWhiteSpace([string]$recommendation.summary) -or
            -not ($recommendation.PSObject.Properties.Name -contains 'suggestedCorrections')
        }).Count -eq 0
        $issues = @($analysis.issues | ForEach-Object {
            if ($_ -is [string]) { $_ } elseif ($null -ne $_.code) { $_.code }
        })
        # Optional evidence-level expectations belong to fixtures, never to production language rules.
        $writingEvidence = @($analysis.issues | Where-Object { $_.code -eq 'writing_errors' } | ForEach-Object { $_.evidence })
        $expectedCorrections = @($case.expectedWritingCorrections | Where-Object { $null -ne $_ })
        $validation = Test-WritingExpectations -WritingEvidence $writingEvidence -ExpectedCorrections $expectedCorrections -Exact:([bool]$case.exactWritingCorrections) -ForbiddenWritingSpans @($case.forbiddenWritingSpans)
        $missingCorrections = $validation.Missing
        $unexpectedCorrections = $validation.Unexpected
        $forbiddenEvidence = $validation.Forbidden
        $duplicateEvidence = $validation.Duplicates
        $evidencePassed = $validation.Passed
        $entry = [pscustomobject]@{
            label = $case.label
            source = $case.source
            url = $url
            expectedClassification = $case.expectedClassification
            classification = $analysis.classification.type
            insufficientEvidence = [bool]$analysis.classification.insufficientEvidence
            assessmentInvalid = $assessmentInvalid
            model = $result.llm.model
            pageSummary = $pageSummary
            recommendationsComplete = $recommendationsComplete
            titlePromiseAssessment = $analysis.titlePromiseAssessment
            titlePromiseValidation = $analysis.titlePromiseValidation
            requiredIssues = $requiredIssues
            allowedIssues = $allowedIssues
            forbiddenIssues = $forbiddenIssues
            issues = $issues
            issueDetails = @($analysis.issues)
            rejectedIssues = @($analysis.rejectedIssues)
            taxonomyVersion = $analysis.taxonomyVersion
            auditScope = $analysis.auditScope
            pipelineStages = @($analysis.pipelineStages)
            writingAssessment = $analysis.writingAssessment
            writingCandidates = @($analysis.writingCandidates)
            evidencePassed = $evidencePassed
            forbiddenWritingEvidence = $forbiddenEvidence
            duplicateWritingEvidence = $duplicateEvidence
            missingCorrections = $missingCorrections
            unexpectedCorrections = $unexpectedCorrections
            scored = $scored
            passed = if ($scored) {
                -not $executionError -and
                -not $analysis.classification.insufficientEvidence -and
                -not [string]::IsNullOrWhiteSpace($pageSummary) -and
                $recommendationsComplete -and
                $evidencePassed -and
                $analysis.classification.type -eq $case.expectedClassification -and
                @($requiredIssues | Where-Object { $issues -notcontains $_ }).Count -eq 0 -and
                @($issues | Where-Object { $_ -notin @($requiredIssues + $allowedIssues) }).Count -eq 0 -and
                @($forbiddenIssues | Where-Object { $issues -contains $_ }).Count -eq 0
            } else { $null }
            error = $executionError
        }
    } catch {
        $entry = [pscustomobject]@{
            label = $case.label
            source = $case.source
            url = $url
            expectedClassification = $case.expectedClassification
            classification = $null
            requiredIssues = $requiredIssues
            allowedIssues = $allowedIssues
            forbiddenIssues = $forbiddenIssues
            issues = @()
            scored = $scored
            passed = if ($scored) { $false } else { $null }
            error = $_.Exception.Message
        }
    }

    $results.Add($entry)
    $results | ConvertTo-Json -Depth 12 | Set-Content $ResultsPath -Encoding utf8
}
} finally {
    if ($null -ne $artificialServer -and -not $artificialServer.HasExited) { Stop-Process -Id $artificialServer.Id }
    if ($null -ne $snapshotServer -and -not $snapshotServer.HasExited) { Stop-Process -Id $snapshotServer.Id }
}

$results | Select-Object source, label, classification, issues, scored, passed, error | Format-Table -AutoSize
