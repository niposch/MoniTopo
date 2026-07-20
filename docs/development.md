# Development

MoniTopo requires Windows and the .NET 10 SDK pinned by `global.json`.

## Safety boundary

Ordinary application runs, tests, and CI use read-only or fake display backends. A production display writer must require both a dedicated manual command and `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`. Tests fail if that variable is present, and CI never sets it.

## Commands

```powershell
dotnet restore
dotnet format MoniTopo.slnx --verify-no-changes --no-restore
dotnet build MoniTopo.slnx --configuration Release --no-restore
dotnet test MoniTopo.slnx --configuration Release --no-build --collect:"XPlat Code Coverage"
```

The solution separates portable domain code (`MoniTopo.Core`), Windows integration (`MoniTopo.Windows`), WPF UI (`MoniTopo.App`), and the recovery companion (`MoniTopo.Recovery`). Test projects contain no real display mutation path.

The Windows tests construct CCD path/mode arrays, EDID bytes, HDR flags, and DPI packets in memory. They do not instantiate the production capture service or query monitor state. The app may use the production service for a user-initiated read-only capture, but any future mutation backend remains behind the separate safety gate.
