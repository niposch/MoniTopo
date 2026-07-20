# MoniTopo

MoniTopo is a Windows tray application for saving and switching complete display configurations. A profile records the active displays, topology, positions, primary display, resolution, refresh rate, orientation, Windows UI scale, and HDR state.

The first release targets Windows 11 x64. Windows 10 support is best effort. Display scaling support is runtime-probed because Windows does not publish the per-monitor scaling setter as a stable public contract.

## Status

MoniTopo is under active development. Automated tests use synthetic display fixtures and cannot change the machine's real display configuration. A release candidate will remain a prerelease until its manual hardware checklist is complete.

## Development

Install the .NET 10 SDK, then run:

```powershell
dotnet restore --locked-mode
dotnet build MoniTopo.slnx --configuration Release --no-restore
dotnet test MoniTopo.slnx --configuration Release --no-build
```

See [development notes](docs/development.md) for the safety gate and project layout.

## License

MIT. See [LICENSE](LICENSE).
