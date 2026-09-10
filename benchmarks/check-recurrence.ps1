$ErrorActionPreference = 'Stop'
$checkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('versa-recurrence-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot | Out-Null
$rows = @(
    [pscustomobject]@{url='https://example.test/a';issueDetails=@([pscustomobject]@{code='writing_errors';evidence=@([pscustomobject]@{quote='The .NET developmer works.';problemSpan='developmer'})})},
    [pscustomobject]@{url='https://example.test/b';issueDetails=@([pscustomobject]@{code='writing_errors';evidence=@([pscustomobject]@{quote='The .NET developmer works. Contact us.';problemSpan='developmer works'})})}
)
$rows | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $checkRoot 'input.json')
& (Join-Path $PSScriptRoot 'summarize-writing-findings.ps1') -ResultsPath (Join-Path $checkRoot 'input.json') -OutputPath (Join-Path $checkRoot 'output.json')
$summary = Get-Content (Join-Path $checkRoot 'output.json') -Raw | ConvertFrom-Json
if (@($summary.recurringContexts).Count -ne 1 -or $summary.recurringContexts[0].pageCount -ne 2) { throw 'Recurring context split by span or quote variation' }
if ($summary.recurringContexts[0].sentenceContext -cne 'The .NET developmer works.') { throw 'Dot in framework name split the sentence' }
if ($summary.recurringContexts[0].componentIdentity -ne 'unverified' -or @($summary.recurringContexts[0].occurrences).Count -ne 2) { throw 'Occurrence provenance lost' }
'3 recurrence checks passed.'
