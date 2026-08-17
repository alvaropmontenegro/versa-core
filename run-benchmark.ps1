param(
    [string]$CasesPath = "$PSScriptRoot\benchmark.json",
    [string]$ResultsPath = "$PSScriptRoot\benchmark-results.json",
    [string[]]$Labels,
    [int]$CaseTimeoutSeconds = 180,
    [switch]$SecondaryPipeline
)

$cases = Get-Content $CasesPath -Raw | ConvertFrom-Json
if ($Labels.Count -gt 0) {
    $cases = @($cases | Where-Object { $_.label -in $Labels })
}
if ($cases.Count -eq 0) {
    throw 'No benchmark cases matched the requested labels.'
}
$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $expectedIssuesAnyOf = @($case.expectedIssuesAnyOf | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void]$startInfo.ArgumentList.Add('run')
    [void]$startInfo.ArgumentList.Add('--project')
    [void]$startInfo.ArgumentList.Add($PSScriptRoot)
    [void]$startInfo.ArgumentList.Add('--no-build')
    [void]$startInfo.ArgumentList.Add('--')
    if ($SecondaryPipeline) {
        [void]$startInfo.ArgumentList.Add('--secondary-pipeline')
    }
    [void]$startInfo.ArgumentList.Add($case.url)
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
            url = $case.url
            expectedClassification = $case.expectedClassification
            classification = $null
            expectedIssue = $case.expectedIssue
            expectedIssuesAnyOf = $expectedIssuesAnyOf
            issues = @()
            passed = $false
            error = "Benchmark case timed out after $CaseTimeoutSeconds seconds."
        })
        $results | ConvertTo-Json -Depth 5 | Set-Content $ResultsPath -Encoding utf8
        continue
    }

    $raw = $standardOutput.GetAwaiter().GetResult()
    $null = $standardError.GetAwaiter().GetResult()
    try {
        $result = $raw.Trim() | ConvertFrom-Json -ErrorAction Stop
        $analysis = $result.analysis
        $issues = @($analysis.issues | ForEach-Object {
            if ($_ -is [string]) { $_ } elseif ($null -ne $_.code) { $_.code }
        })
        $entry = [pscustomobject]@{
            label = $case.label
            url = $case.url
            expectedClassification = $case.expectedClassification
            classification = $analysis.classification.type
            expectedIssue = $case.expectedIssue
            expectedIssuesAnyOf = $expectedIssuesAnyOf
            issues = $issues
            passed = $analysis.classification.type -eq $case.expectedClassification -and
                ($null -eq $case.expectedIssue -or $issues -contains $case.expectedIssue) -and
                ($expectedIssuesAnyOf.Count -eq 0 -or @($issues | Where-Object { $expectedIssuesAnyOf -contains $_ }).Count -gt 0)
            error = $result.llm.error
        }
    } catch {
        $entry = [pscustomobject]@{
            label = $case.label
            url = $case.url
            expectedClassification = $case.expectedClassification
            classification = $null
            expectedIssue = $case.expectedIssue
            expectedIssuesAnyOf = $expectedIssuesAnyOf
            issues = @()
            passed = $false
            error = $_.Exception.Message
        }
    }

    $results.Add($entry)
    $results | ConvertTo-Json -Depth 5 | Set-Content $ResultsPath -Encoding utf8
}

$results | Select-Object label, classification, issues, passed, error | Format-Table -AutoSize
