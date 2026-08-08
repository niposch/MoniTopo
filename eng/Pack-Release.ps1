param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion,

    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$null = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -PackageVersion $PackageVersion

$mainExecutable = Join-Path $PublishDirectory 'MoniTopo.App.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "Published main executable was not found at '$mainExecutable'."
}

$publishedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($mainExecutable).ProductVersion
$publishedPackageVersion = ($publishedVersion -split '\+', 2)[0]
if ($publishedPackageVersion -cne $PackageVersion) {
    throw "Published app version '$publishedPackageVersion' does not match package version '$PackageVersion'. Publish and package with the same -p:Version value."
}

& dotnet tool run vpk -- pack `
    --packId MoniTopo `
    --packVersion $PackageVersion `
    --packDir $PublishDirectory `
    --mainExe MoniTopo.App.exe `
    --packTitle MoniTopo `
    --packAuthors "MoniTopo contributors" `
    --releaseNotes "$PSScriptRoot\..\CHANGELOG.md" `
    --runtime win-x64 `
    --shortcuts StartMenuRoot `
    --outputDir $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Velopack failed with exit code $LASTEXITCODE."
}
