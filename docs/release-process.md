# Release process

MoniTopo uses a date-based package version compatible with SemVer:

- package `YYYY.MDD.R`;
- display `YYYY.MM.DD` when revision `R` is zero;
- display `YYYY.MM.DD.R` for later builds on the same day;
- tag `v<package-version>`.

`eng/Get-ReleaseVersion.ps1` validates and converts the value. `eng/Pack-Release.ps1` invokes the pinned Velopack 1.2.0 tool, creates a self-contained win-x64 per-user setup executable, portable ZIP, full update package, and feed, and requests only a Start Menu shortcut.

The same package version must be passed to build, publish, and Velopack packaging. It becomes the assembly informational version, Windows file version (with a fourth zero component), and Velopack update identity. The app presents the date-form display version; Windows Installed Apps shows Velopack's ordered package version. Those two strings intentionally represent the same release in different formats and do not create a second update identity. Unpackaged development builds are excluded from update checks.

The Release workflow requires an explicit manual package version or takes it from a matching version tag; it has no stale default release number. It repeats formatting, build, non-destructive tests, and publish; downloads the prior matching-channel feed when available for delta generation; packages; retains the output as a workflow artifact; and creates a draft GitHub release. Tag-triggered drafts are stable; manual dispatch can select `prerelease` for an evaluation build. Draft is intentional: publication is a separate step after green checks, artifact inspection, and explicit release approval. Never reuse a released package version.

Release notes come from `CHANGELOG.md`. Before dispatch, ensure the dated section describes user-visible changes and limitations. After workflow completion, download and inspect the setup, portable package, version metadata, release notes, and update feed. Run `docs/manual-display-test.md` on the intended hardware; do not infer hardware coverage from CI. With explicit release approval, publish the inspected draft as stable/latest (or as a prerelease when requested). The production updater only reads stable GitHub releases; prerelease builds are distributed manually.

README screenshots are app-only WPF renders using synthetic example profiles and generic display labels. Never include desktop captures, real display identity data, local configuration, or logs in release assets or documentation.

## First-release label correction

The first stable release displays `2026.10.22`. Its package version remains `2026.1022.0`, and its tag remains `v2026.1022.0`. Never pass the display string as a package version: `2026.10.22` would sort below `2026.1022.0` and break the intended update ordering. Unit tests exercise the actual Velopack version selection with a synthetic feed across same-day revisions, the next day, and the next year; both workflows also verify the PowerShell conversion.

At the owner's explicit request, and before any user installations, the first release's assets were rebuilt once to correct the date labels and embedded notes. All package checksums and feeds must be replaced together. The original Git tag is preserved; the release body identifies the correction's build commit so it is clear which source produced the replacement binaries. Anyone retaining an earlier download must download it again; the updater does not offer a same-version package as an upgrade. This is a one-time pre-distribution exception, not permission to replace future released versions.

For this correction, dispatch Release with `package_version=2026.1022.0` and `package_only=true`. This runs the same tests/publish/pack steps and retains a fresh artifact without downloading old channel packages, generating deltas against a same-version build, or uploading to GitHub. Inspect the artifact before briefly making the existing release a draft, replacing all five assets and notes, and republishing it as stable/latest. Do not move the original tag, merge unrelated feature branches, or silently retain a stale update feed.

No signing certificate is available. The executable and installer are therefore unsigned, and SmartScreen warnings are expected. Do not describe them as signed or suppress that limitation in release notes.
