# Development

MoniTopo requires Windows, Git, PowerShell 7, and the .NET 10 SDK pinned by `global.json`. Windows 11 x64 is the supported development target; Windows 10 is best effort. Rider, Visual Studio, and VS Code are optional because all required operations are available through the .NET CLI.

## Safety boundary

Ordinary application runs, tests, and CI use read-only or fake display backends. A production display writer requires both an explicit user/manual activation command and `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`. The recovery companion accepts only `--recover-display-transaction <directory>` and requires the same inherited opt-in. Never set the variable in test scripts or CI.

Windows tests create an authorization token through an internal fake environment reader and route every write through a recording fake native API. They do not call `SetDisplayConfig`, a DPI setter, or an HDR setter. Merely setting the environment variable does not activate a profile; a deliberate application/manual command is still required.

## Clone and build

```powershell
git clone https://github.com/niposch/MoniTopo.git
Set-Location MoniTopo
dotnet restore --locked-mode
dotnet tool restore
dotnet format MoniTopo.slnx --verify-no-changes --no-restore
dotnet build MoniTopo.slnx --configuration Release --no-restore
dotnet test MoniTopo.slnx --configuration Release --no-build --collect:"XPlat Code Coverage"
```

Run the WPF project with `dotnet run --project src/MoniTopo.App`. Normal launches start in the tray after first-run setup; use the tray popup or right-click **Open MoniTopo** to show the Profiles page. Starting the app reads the current display state but does not activate a profile by itself.

Unpackaged Rider/CLI builds use the repository's default package version and show its date form with a `dev` suffix. They do not participate in Velopack update checks because `UpdateManager.IsInstalled` is false. Installed builds take their package version from Velopack. Both write to the same local log directory and identify their channel and complete build version in the first record. Fully exit an installed tray instance before launching from Rider because single-instance coordination is intentionally shared.

Profiles captured by builds before capture schema 3 must be updated from the current Windows setup before activation. Those builds stored generic Advanced Color activity instead of the Windows HDR user toggle, so MoniTopo cannot infer the correct saved HDR state during migration. New captures use schema 4 to retain virtual desktop-image and refresh-boost metadata. Schema-3 profiles remain activatable; recapture them to gain the additional fidelity checks.

The solution separates portable domain code (`MoniTopo.Core`), Windows integration (`MoniTopo.Windows`), WPF UI (`MoniTopo.App`), and the recovery companion (`MoniTopo.Recovery`). Test projects contain no real display mutation path.

The Windows tests construct CCD path/mode arrays, rollback files, EDID bytes, HDR flags, and DPI packets in memory. They do not instantiate the default production backend or query monitor state. Run the synthetic suites directly with `dotnet test tests/MoniTopo.Windows.Tests` and `dotnet test tests/MoniTopo.Core.Tests`; sanitized fixture files are under `tests/Fixtures/Synthetic`. The app may use the production service for user-initiated read-only capture; activation composition must pass the guarded authorization object explicitly.

WPF startup smoke tests show and lay out the first-run, popup, and main windows on STA threads with fake configuration and update services. These tests catch invalid binding modes without invoking a display writer.

Presentation tests also exercise single-key confirmation, realized hotkey labels/layout schematics, theme settings, shared icon decoding, and release notes independently of download/restart. To regenerate the committed multi-resolution icon after editing its geometry, run `./eng/Generate-AppIcon.ps1`; keep the source SVG and generator geometry in sync.

CI enforces at least 85% line coverage for `MoniTopo.Core`. `eng/Test-CoreCoverage.ps1` reads the isolated Core Cobertura report. Windows and application coverage is retained as an artifact for review but is not used to hide platform or UI code behind exclusions.

Recovery tests use synthetic payloads, isolated event/lease names, fake process handles, and fake native writers. They cover takeover, PID reuse, held leases, completion at the deadline, unsuccessful recovery, and pending-result reporting without terminating a real process or changing a display. Recovery has one writer lease per recovery root, not just per transaction. Never remove that ownership handshake to make a timeout test pass.

Malformed configuration is left in place and copied to a timestamped corruption file. Startup can explicitly restore a validated `config.json.bak`; it never silently discards profiles. Do not delete either file as a debugging shortcut. The app reports and acknowledges pending recovery results on startup while retaining unresolved rollback evidence.

## Local packaging

Velopack 1.2.0 is pinned in the local tool manifest and application package reference. Publish and package a release candidate with:

```powershell
dotnet restore src/MoniTopo.App/MoniTopo.App.csproj --runtime win-x64 --locked-mode --no-dependencies -p:SelfContained=true
dotnet publish src/MoniTopo.App/MoniTopo.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:Version=2026.720.0 -o artifacts/publish
./eng/Pack-Release.ps1 -PackageVersion 2026.720.0 -PublishDirectory artifacts/publish -OutputDirectory artifacts/releases
```

Replace the sample version with the release date/revision. Outputs include the per-user setup executable, portable ZIP, update package, and release feeds. They are unsigned; SmartScreen or antivirus reputation warnings are expected. Configuration and logs live below `%LocalAppData%\MoniTopo`, outside Velopack's replaceable install directory.

## Native debugging

- Start with the focused conversion or facade test for the failing API. Tests accept synthetic native structures and report translated Win32 codes without changing displays.
- Read the newest bounded log under `%LocalAppData%\MoniTopo\logs`; development and installed builds use this same directory, every primary-process log starts with its build identity, logs retain at most seven 1 MiB files, and they contain no telemetry. A rejected CCD plan records non-identifying path flags, mode indices, dimensions, positions, and signal timing, but omits adapter/target IDs and monitor identity data.
- CCD buffer changes during capture are expected and retried. Repeated failures should be investigated with the native error code in the log and the documented `QueryDisplayConfig` contract.
- Identity and mode investigations must use synthetic fixtures. Remembered bindings include the physical fingerprint; a runtime adapter/target ID alone is not permission to select a panel. Legacy runtime-only remembered bindings are ignored and may need confirmation again.
- The recovery companion can end the original MoniTopo process if it remains blocked inside a native display call beyond the ownership grace period. It checks PID and creation time and confirms exit before acquiring the writer lease. Failed ownership acquisition retains recovery evidence and never permits a competing write.
- Velopack's update-check API has no cancellation parameter. Shutdown waits for an in-flight SDK check instead of detaching it, so an unavailable network can delay exit until the SDK timeout.
- A Rider or Visual Studio XAML designer warning about `Application.ThemeMode` is suppressed only as `WPF0001` in the WPF project because the built-in Fluent System theme remains experimental in .NET 10.
- Do not enable the real-display opt-in merely to debug marshalling, conversion, identity, recovery, HDR, or scaling. Each of those paths has a fake-backed focused test.
