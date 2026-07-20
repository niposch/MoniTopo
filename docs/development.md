# Development

MoniTopo requires Windows and the .NET 10 SDK pinned by `global.json`.

## Safety boundary

Ordinary application runs, tests, and CI use read-only or fake display backends. A production display writer requires both an explicit user/manual activation command and `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`. The recovery companion accepts only `--recover-display-transaction <directory>` and requires the same inherited opt-in. Never set the variable in test scripts or CI.

Windows tests create an authorization token through an internal fake environment reader and route every write through a recording fake native API. They do not call `SetDisplayConfig`, a DPI setter, or an HDR setter. Merely setting the environment variable does not activate a profile; a deliberate application/manual command is still required.

## Commands

```powershell
dotnet restore
dotnet format MoniTopo.slnx --verify-no-changes --no-restore
dotnet build MoniTopo.slnx --configuration Release --no-restore
dotnet test MoniTopo.slnx --configuration Release --no-build --collect:"XPlat Code Coverage"
```

The solution separates portable domain code (`MoniTopo.Core`), Windows integration (`MoniTopo.Windows`), WPF UI (`MoniTopo.App`), and the recovery companion (`MoniTopo.Recovery`). Test projects contain no real display mutation path.

The Windows tests construct CCD path/mode arrays, rollback files, EDID bytes, HDR flags, and DPI packets in memory. They do not instantiate the default production backend or query monitor state. The app may use the production service for user-initiated read-only capture; activation composition must pass the guarded authorization object explicitly.
