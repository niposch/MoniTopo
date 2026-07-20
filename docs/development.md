# Development

MoniTopo requires Windows and the .NET 10 SDK pinned by `global.json`.

## Safety boundary

Ordinary application runs, tests, and CI use read-only or fake display backends. A production display writer requires both an explicit user/manual activation command and `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`. The recovery companion accepts only `--recover-display-transaction <directory>` and requires the same inherited opt-in. Never set the variable in test scripts or CI.

Windows tests create an authorization token through an internal fake environment reader and route every write through a recording fake native API. They do not call `SetDisplayConfig`, a DPI setter, or an HDR setter. Merely setting the environment variable does not activate a profile; a deliberate application/manual command is still required.

## Commands

```powershell
dotnet restore
dotnet tool restore
dotnet format MoniTopo.slnx --verify-no-changes --no-restore
dotnet build MoniTopo.slnx --configuration Release --no-restore
dotnet test MoniTopo.slnx --configuration Release --no-build --collect:"XPlat Code Coverage"
```

The solution separates portable domain code (`MoniTopo.Core`), Windows integration (`MoniTopo.Windows`), WPF UI (`MoniTopo.App`), and the recovery companion (`MoniTopo.Recovery`). Test projects contain no real display mutation path.

The Windows tests construct CCD path/mode arrays, rollback files, EDID bytes, HDR flags, and DPI packets in memory. They do not instantiate the default production backend or query monitor state. The app may use the production service for user-initiated read-only capture; activation composition must pass the guarded authorization object explicitly.

WPF startup smoke tests show and lay out the first-run, popup, and main windows on STA threads with fake configuration and update services. These tests catch invalid binding modes without invoking a display writer.

## Local packaging

Velopack 1.2.0 is pinned in the local tool manifest and application package reference. Publish and package a release candidate with:

```powershell
dotnet restore src/MoniTopo.App/MoniTopo.App.csproj --runtime win-x64 --locked-mode --no-dependencies -p:SelfContained=true
dotnet publish src/MoniTopo.App/MoniTopo.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:Version=2026.720.0 -o artifacts/publish
./eng/Pack-Release.ps1 -PackageVersion 2026.720.0 -PublishDirectory artifacts/publish -OutputDirectory artifacts/releases
```

Replace the sample version with the release date/revision. Outputs include the per-user setup executable, portable ZIP, update package, and release feeds. They are unsigned; SmartScreen or antivirus reputation warnings are expected. Configuration and logs live below `%LocalAppData%\MoniTopo`, outside Velopack's replaceable install directory.
