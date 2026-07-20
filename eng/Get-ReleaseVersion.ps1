param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion
)

$parts = $PackageVersion.Split('.')
if ($parts.Count -ne 3 -or $parts[1].Length -lt 3 -or $parts[1].Length -gt 4) {
    throw "Package version '$PackageVersion' must use YYYY.MDD.R."
}

$year = 0
$month = 0
$day = 0
$revision = 0
$monthLength = $parts[1].Length - 2
if (-not [int]::TryParse($parts[0], [ref]$year) -or
    -not [int]::TryParse($parts[1].Substring(0, $monthLength), [ref]$month) -or
    -not [int]::TryParse($parts[1].Substring($monthLength), [ref]$day) -or
    -not [int]::TryParse($parts[2], [ref]$revision) -or
    $year -lt 2000 -or
    $revision -lt 0) {
    throw "Package version '$PackageVersion' must use numeric YYYY.MDD.R components."
}

try {
    $date = [DateTime]::new($year, $month, $day)
}
catch {
    throw "Package version '$PackageVersion' contains an invalid date."
}

$canonical = '{0}.{1}{2:D2}.{3}' -f $year, $month, $day, $revision
if ($canonical -cne $PackageVersion) {
    throw "Package version '$PackageVersion' is not canonical; expected '$canonical'."
}

$display = $date.ToString('dd.MM.yy', [System.Globalization.CultureInfo]::InvariantCulture)
if ($revision -gt 0) {
    $display = "$display.$revision"
}

[pscustomobject]@{
    PackageVersion = $PackageVersion
    DisplayVersion = $display
    Tag = "v$PackageVersion"
}
