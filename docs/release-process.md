# Release process

MoniTopo uses a date-based package version compatible with SemVer:

- package `YYYY.MDD.R`;
- display `DD.MM.YY` when revision `R` is zero;
- display `DD.MM.YY.R` for later builds on the same day;
- tag `v<package-version>`.

`eng/Get-ReleaseVersion.ps1` validates and converts the value. `eng/Pack-Release.ps1` invokes the pinned Velopack 1.2.0 tool, creates a self-contained win-x64 per-user setup executable, portable ZIP, full update package, and feed, and requests only a Start Menu shortcut.

The release workflow accepts a manual package version or a matching version tag. It repeats formatting, build, non-destructive tests, and publish; downloads the prior prerelease feed when available for delta generation; packages; retains the output as a workflow artifact; and creates a draft GitHub prerelease. Draft is intentional. Do not publish the first stable release until the manual display checklist is complete.

Release notes come from `CHANGELOG.md`. Before dispatch, ensure the dated release-candidate section describes user-visible changes. After workflow completion, download and inspect the setup and portable artifacts, run `docs/manual-display-test.md`, and only then decide whether the draft can be published as a prerelease. A later stable release must change the updater to ignore GitHub prereleases and must not reuse a released package version.

No signing certificate is available. The executable and installer are therefore unsigned, and SmartScreen warnings are expected. Do not describe them as signed or suppress that limitation in release notes.
