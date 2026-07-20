# MoniTopo

MoniTopo is a Windows tray application for saving and switching complete display configurations. A profile records the active displays, topology, positions, primary display, resolution, refresh rate, orientation, Windows UI scale, and HDR state.

The first release targets Windows 11 x64. Windows 10 support is best effort. Display scaling support is runtime-probed because Windows does not publish the per-monitor scaling setter as a stable public contract.

## Status

MoniTopo is under active development. Automated tests use synthetic display fixtures and cannot change the machine's real display configuration. A release candidate will remain a prerelease until its manual hardware checklist is complete.

## Installation and use

Use the `MoniTopo-win-Setup.exe` asset from a release candidate, or extract the portable ZIP. The installer is per-user and requires no administrator rights. It is not code-signed, so Windows SmartScreen may show an unknown-publisher warning.

On first run, choose whether MoniTopo should start when you sign in, then capture the setup currently configured in Windows as a named profile. Activate saved profiles from the tray popup or an assigned hotkey. MoniTopo starts in the tray by default; the main window can be enabled at launch in General settings.

Update checks run at most once per day when enabled. MoniTopo does not download an update until **Download update** is selected and does not install it until **Install and restart** is selected.

Current limitations include the runtime-probed Windows scale contract, ambiguous identical monitors requiring manual binding, unsigned packages, and hardware behavior that still needs the [manual display checklist](docs/manual-display-test.md).

## Development

Install the .NET 10 SDK, then run:

```powershell
dotnet restore --locked-mode
dotnet build MoniTopo.slnx --configuration Release --no-restore
dotnet test MoniTopo.slnx --configuration Release --no-build
```

See [development notes](docs/development.md) for the safety gate and project layout.
See the [release process](docs/release-process.md) for versioning and packaging.

## License

MIT. See [LICENSE](LICENSE).
