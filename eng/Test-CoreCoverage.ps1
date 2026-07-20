param(
    [Parameter(Mandatory = $true)]
    [string]$CoverageDirectory,

    [double]$MinimumLineRate = 0.85
)

$reports = @(Get-ChildItem -LiteralPath $CoverageDirectory -Recurse -Filter 'coverage.cobertura.xml')
if ($reports.Count -ne 1) {
    throw "Expected one Core coverage report under '$CoverageDirectory', found $($reports.Count)."
}

[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName
$packages = @($coverage.coverage.packages.package | Where-Object { $_.name -eq 'MoniTopo.Core' })
if ($packages.Count -ne 1) {
    throw "The Core coverage report does not contain exactly one MoniTopo.Core package."
}

$lineRate = [double]::Parse(
    $packages[0].'line-rate',
    [System.Globalization.CultureInfo]::InvariantCulture)
if ($lineRate -lt $MinimumLineRate) {
    throw ('MoniTopo.Core line coverage {0:P2} is below the required {1:P2}.' -f $lineRate, $MinimumLineRate)
}

Write-Host ('MoniTopo.Core line coverage: {0:P2} (minimum {1:P2})' -f $lineRate, $MinimumLineRate)
