$ErrorActionPreference = 'Stop'

$cases = @(
    @('2026.1022.0', '2026.10.22'),
    @('2026.1022.1', '2026.10.22.1'),
    @('2026.720.0', '2026.07.20'),
    @('2026.1231.0', '2026.12.31'),
    @('2027.101.0', '2027.01.01'),
    @('2027.205.0', '2027.02.05'),
    @('2028.229.0', '2028.02.29')
)

foreach ($case in $cases) {
    $release = & "$PSScriptRoot/Get-ReleaseVersion.ps1" -PackageVersion $case[0]
    if ($release.PackageVersion -cne $case[0] -or
        $release.DisplayVersion -cne $case[1] -or
        $release.Tag -cne "v$($case[0])") {
        throw "Incorrect release conversion for '$($case[0])'."
    }
}

foreach ($invalid in @('2026.10.22', '2026.10.22.1', '2026.0720.0', '2027.229.0', '1999.101.0', '2026.720.-1', 'v2026.720.0')) {
    $rejected = $false
    try {
        $null = & "$PSScriptRoot/Get-ReleaseVersion.ps1" -PackageVersion $invalid
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Invalid package version '$invalid' was accepted."
    }
}

Write-Output 'Release version conversion checks passed.'
