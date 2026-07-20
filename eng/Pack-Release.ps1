param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion,

    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$null = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -PackageVersion $PackageVersion

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
