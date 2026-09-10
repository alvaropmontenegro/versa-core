function Test-WritingExpectations {
    param([object[]]$WritingEvidence, [object[]]$ExpectedCorrections, [switch]$Exact, [object[]]$ForbiddenWritingSpans)
    $remaining = [System.Collections.Generic.List[object]]::new()
    foreach ($actual in $writingEvidence) { $remaining.Add($actual) }
    $missingCorrections = @(
        foreach ($expected in $expectedCorrections) {
            $match = -1
            for ($i = 0; $i -lt $remaining.Count; $i++) {
                $actual = $remaining[$i]
                $different = @($expected.PSObject.Properties | Where-Object {
                    $_.Name -in @('quote','problemSpan','errorType','correctionStatus','correctedQuote') -and
                    ($actual.($_.Name) -cne $_.Value)
                })
                if ($different.Count -eq 0) { $match = $i; break }
            }
            if ($match -ge 0) { $remaining.RemoveAt($match) } else { $expected }
        }
    )
    $unexpectedCorrections = @(if ($Exact) { $remaining.ToArray() })
    $forbiddenEvidence = @(
        foreach ($actual in $writingEvidence) {
            foreach ($forbidden in $ForbiddenWritingSpans) {
                if ($actual.quote -ceq $forbidden.quote -and $actual.problemSpan -ceq $forbidden.problemSpan) { $actual; break }
            }
        }
    )
    $duplicateEvidence = @($writingEvidence | Group-Object -Property { @($_.blockId, $_.quote, $_.problemSpan) | ConvertTo-Json -Compress } | Where-Object Count -gt 1)
    $evidencePassed = $missingCorrections.Count -eq 0 -and $unexpectedCorrections.Count -eq 0 -and $forbiddenEvidence.Count -eq 0 -and $duplicateEvidence.Count -eq 0
    [pscustomobject]@{
        Passed = $evidencePassed
        Missing = @($missingCorrections)
        Unexpected = @($unexpectedCorrections)
        Forbidden = @($forbiddenEvidence)
        Duplicates = @($duplicateEvidence)
    }
}
