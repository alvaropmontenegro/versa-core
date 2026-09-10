. (Join-Path $PSScriptRoot 'WritingExpectations.ps1')
$actual = [pscustomobject]@{quote='A typo.'; problemSpan='typo'; errorType='spelling'; correctionStatus='safe'; correctedQuote='A type.'; blockId='block-1'}
$expected = [pscustomobject]@{quote='A typo.'; problemSpan='typo'; errorType='spelling'; correctionStatus='safe'; correctedQuote='A type.'}
if (-not (Test-WritingExpectations @($actual) @($expected) -Exact).Passed) { throw 'Exact match rejected' }
if ((Test-WritingExpectations @($actual,$actual) @($expected) -Exact).Passed) { throw 'Duplicate accepted' }
if ((Test-WritingExpectations @($actual) @($expected,$expected) -Exact).Passed) { throw 'Multiplicity ignored' }
$wrong = [pscustomobject]@{quote='A typo.'; problemSpan='A'; correctedQuote='A type.'}
if ((Test-WritingExpectations @($actual) @($wrong) -Exact).Passed) { throw 'Wrong span accepted' }
if ((Test-WritingExpectations @($actual) @() -ForbiddenWritingSpans @($expected)).Passed) { throw 'Forbidden evidence accepted' }
$review = [pscustomobject]@{quote='Ambiguous repair'; problemSpan='repair'; correctionStatus='requires_review'; correctedQuote=$null}
if (-not (Test-WritingExpectations @($review) @($review) -Exact).Passed) { throw 'Review rejected' }
if ((Test-WritingExpectations @($actual) @($review) -Exact).Passed) { throw 'Review incorrectly matched' }
'7 benchmark matcher checks passed.'
