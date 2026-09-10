param(
    [Parameter(Mandatory)][string]$ResultsPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutputPath) -and -not [IO.Path]::GetDirectoryName($OutputPath)) { $OutputPath = Join-Path $PSScriptRoot ('results/' + $OutputPath) }
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath))) -Force | Out-Null
$results = @(Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json)
$occurrences = @(
    foreach ($page in $results) {
        foreach ($issue in $page.issueDetails | Where-Object code -eq 'writing_errors') {
            foreach ($evidence in $issue.evidence) {
                $sentences = @([regex]::Split($evidence.quote, '(?<=[.!?])\s+(?=\p{Lu})') | Where-Object { $_.Contains([string]$evidence.problemSpan) })
                # This is a reporting key only, never a component identifier or an audit verdict.
                $context = if ($sentences.Count -eq 1) { $sentences[0].Trim() } else { $evidence.quote }
                [pscustomobject]@{
                    sentenceContext = $context
                    host = ([uri]$page.url).Host
                    url = $page.url
                    quote = $evidence.quote
                    problemSpan = $evidence.problemSpan
                    blockId = $evidence.blockId
                    blockRole = $evidence.blockRole
                    correctionStatus = $evidence.correctionStatus
                    correctedQuote = $evidence.correctedQuote
                    explanation = $evidence.explanation
                }
            }
        }
    }
)
$groups = @(
    $occurrences | Group-Object -Property { @($_.host, $_.sentenceContext, $_.problemSpan) | ConvertTo-Json -Compress } | ForEach-Object {
        $pages = @($_.Group.url | Sort-Object -Unique)
        [pscustomobject]@{
            sentenceContext = $_.Group[0].sentenceContext
            quote = $_.Group[0].quote
            problemSpan = $_.Group[0].problemSpan
            recurrence = if ($pages.Count -gt 1) { 'observed_across_pages' } else { 'unknown' }
            componentIdentity = 'unverified'
            pageCount = $pages.Count
            occurrenceCount = $_.Count
            occurrences = @($_.Group)
        }
    }
)
$recurringContexts = @(
    $occurrences | Group-Object -Property { @($_.host, $_.sentenceContext) | ConvertTo-Json -Compress } | ForEach-Object {
        $pages = @($_.Group.url | Sort-Object -Unique)
        if ($pages.Count -gt 1) {
            [pscustomobject]@{
                sentenceContext = $_.Group[0].sentenceContext
                recurrence = 'observed_across_pages'
                componentIdentity = 'unverified'
                pageCount = $pages.Count
                occurrences = @($_.Group)
            }
        }
    }
)
[pscustomobject]@{
    recurringContexts = $recurringContexts
    pages = $results.Count
    writingOccurrences = $occurrences.Count
    uniqueFindings = $groups.Count
    recurringFindings = @($groups | Where-Object pageCount -gt 1).Count
    note = 'Recurrence does not prove shared component identity. Page verdicts are preserved. Span boundaries may vary, so uniqueFindings counts evidence signatures, not independently established defects. Precision requires manual annotations.'
    findings = $groups
} | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $OutputPath -Encoding utf8
