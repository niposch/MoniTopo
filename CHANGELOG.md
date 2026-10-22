# Changelog

## Reliability fixes awaiting hardware validation

- Coordinate activation and recovery through exclusive display-writer ownership, explicit recovery outcomes, and startup recovery reports.
- Wait for complete display-setting convergence, verify rollback before saving it, and preserve virtual desktop-image and refresh-boost data in new profiles.
- Reject conflicting physical monitor identities, ignore untrusted runtime-only bindings, and tolerate transient capture/device changes.
- Preserve damaged configuration and offer validated backup recovery; prevent profile edits from overwriting concurrent settings changes.
- Restore hotkeys on their owning UI thread, refresh visible tray state, bound stalled launch messages, and drain work before exit.
- Recover from invalid saved window sizes and avoid saving zero bounds when exiting without opening the main window.
- Preserve staged updates and retry actions, check daily during long-running sessions, and improve keyboard and secondary-monitor popup behavior.

## 22.07.26 release candidate

- Initial project foundation for the Windows tray application.
- Capture, identity matching, profile management, tray switching, global hotkeys, exact activation orchestration, and out-of-process recovery.
- First-run sign-in choice, tray-first startup preference, and manual staged updates.
- Theme-aware Fluent surfaces, dedicated Settings navigation, and actionable update controls in the tray popup.
- Quick-access profile tiles in the tray popup and monitor previews with in-layout resolution, refresh, Windows scale, and HDR details.
- Correct Windows 11 HDR-toggle capture and activation of profiles that enable a previously inactive display source.
- Acknowledged, framed single-instance launch messages that remain reliable across repeated launches.
- Per-user installer and portable package with a non-destructive test and CI safety boundary.
