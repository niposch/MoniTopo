# Manual display acceptance

Run this checklist manually on the build being evaluated, with a keyboard path available on every intended display. Rider and installed builds must be tested separately. Profile activation changes the real desktop; automated tests use synthetic profiles and fake display writers instead.

Local validation on 22 October 2026 completed two round trips between the existing one- and two-display profiles and one deliberate pre-HDR failure with exact rollback. The original captured setup was verified after each run. This does not certify other monitors, docks, clones, DRR, mixed-DPI placement, or the installed update flow; the remaining checklist still applies.

## Preparation

- Record the current Windows display arrangement in screenshots or notes.
- Confirm `MoniTopo.Recovery.exe` is beside `MoniTopo.App.exe` in the build or install directory.
- Start MoniTopo through the explicit manual-test path with `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1` set by the tester.
- Keep Windows Display Settings available. Do not create an intentionally all-displays-off configuration.

## Profiles and switching

- Configure and capture Desktop, Movie, and Gaming profiles using Windows Settings or the GPU control panel.
- Activate every profile from the tray popup and confirm inline progress/result feedback.
- Assign and use a direct hotkey for every profile; confirm the short notification and no key-repeat activation.
- Confirm Movie disables both desk monitors and makes the television primary.
- Confirm Desktop restores the intended primary display and active desk-monitor set.
- For each profile, compare position, clone/extended topology, resolution, exact friendly refresh rate, orientation, Windows scale, and HDR with its captured state.
- Exercise available 100%, 150%, and 300% scale values, including popup keyboard use at 300% DPI on a 4K display.
- Repeat switching in both directions several times, including a profile that enables a currently inactive monitor. Confirm the active indicator agrees with Windows after each switch.
- After a switch, use another profile hotkey without restarting MoniTopo; it must still work.
- If existing profiles predate complete virtual-mode capture, update each from its intended Windows setup before evaluating virtual desktop-image or dynamic-refresh restoration.

## Profile setting edits (development editor)

- Open **Edit display settings** for a connected active display. Compare available resolution/rate pairs, Windows scale steps, and HDR choices against Windows Settings. Verify fractional rates remain distinct where reported.
- Change a resolution and confirm its refresh-rate list updates. Save and confirm the desktop has not changed; then explicitly activate and verify the requested source resolution, refresh, scale, HDR, and primary display.
- Resize the primary display in a side-by-side profile and confirm its neighbor remains attached on the same edge; repeat with a display left/above the primary. Complex conflicting attachments must give an error without saving or applying.
- Cancel a changed draft and confirm the profile is unchanged. Disconnect/disable a saved display and confirm missing options are explained rather than replaced with guessed values.
- Change HDR only and scale only and confirm captured mode data remains intact. For clones, source mode and scale controls should be read-only while supported per-target HDR remains available.
- Change the connected setup while an editor is open. A stale capability or changed profile must be rejected on Save; no display changes may happen during this check.
- Activation failure must use the existing validated rollback/recovery path. Do not deliberately choose a destructive configuration or automate real writes to test the editor.

## Missing, extra, and changed displays

- Disconnect or fully power off the television while Desktop is active; Desktop should remain matched.
- Attempt Movie while the television is disconnected; activation must fail before changing another display and name the missing television.
- Enable an extra display; the current state must become Custom.
- Change one managed setting manually in Windows; state must become Custom and MoniTopo must not revert it automatically.
- If the hardware permits, connect two indistinguishable same-model monitors and confirm MoniTopo requests a manual binding instead of guessing.
- Reconnect or change a connector after remembering a binding. A different monitor with a conflicting serial must not silently inherit that binding.
- Leave the popup open while changing a managed setting in Windows; its state and tray tooltip should follow the observed setup.

## Lifecycle and layout

- Launch a second MoniTopo instance and confirm the existing instance opens its Profiles page.
- Close the main window and confirm MoniTopo remains in the tray; use Exit MoniTopo and confirm it terminates.
- Test the popup with bottom, top, left, and right taskbar positions where Windows permits them.
- Move the pointer between monitors and open the popup both by clicking the icon and by its configured hotkey. It must stay beside the primary monitor's notification area, within its work area, regardless of pointer location. Repeat after changing the primary monitor and at mixed DPI.
- Enable run at login, sign out/in, and confirm background startup does not open the main window. Disable it and repeat.
- Enable the show-window preference and compare an ordinary launch with a `--background` launch. Only the ordinary launch should honor that preference.
- Reorder profiles in the main window and confirm the popup uses the same order.
- Tab to the popup's Open MoniTopo and update buttons and press Enter. Each must invoke its own action without activating a profile.
- Confirm the main window can be restored on a secondary monitor and the popup remains usable after changing the primary monitor or DPI.
- In Settings, change **Open tray popup** from Ctrl+Alt+M and confirm it toggles the popup with a profile selected; arrows and Enter must still work.
- Choose an unmodified key: verify the system-wide interception warning, cancel it once, then confirm it. Check conflict rejection, no repeat while held, and removal/restoration of the previous shortcut. Use a non-typing key such as F8 for this test.
- Confirm profile hotkeys appear in both lists, tray schematics reflect distinct saved layouts, and the main window/taskbar use the tray icon.
- Select System, Light, and Dark in Settings. Both open windows, dialogs, selected tiles, and release notes must remain legible immediately and after restarting; System must follow Windows.
- Confirm profile update/hotkey/binding actions appear above the monitor preview, not below the display details.

## Recovery

- Run the automated recovery protocol tests with fake writers first. They must cover slow/incomplete activation, main-process disappearance, failed rollback, fallback, and two transactions competing for writer ownership.
- Do not point synthetic recovery payloads at the real desktop or fabricate an all-displays-off configuration. Real failure injection requires a separately reviewed manual path and a known working rollback setup.
- If a real activation fails, confirm the message distinguishes a restored setup, an emergency fallback, and unresolved recovery. An unresolved recovery must retain its evidence.
- After a companion recovery, restart MoniTopo and confirm it reports the saved outcome. Acknowledging the notice must not silently delete an unresolved rollback snapshot.

## Installer and update

- Install per-user without administrator rights; record the expected unsigned SmartScreen warning.
- Confirm Start Menu launch, first-run autostart choice, repair/reinstall behavior, and uninstall cleanup including the startup entry.
- Check for an update against a published stable test release, or use a local fake feed for isolated tests. Drafts and prereleases must not be offered by the production updater. Confirm checks do not download automatically, clicking the popup update hint starts the download, and the staged update is applied by either **Restart now** or the next application launch.
- Before downloading, open **Release notes** from both the tray and Settings. Confirm the available version, complete scrollable notes, selectable text, and no download or restart. If the feed omits notes, check the explicit unavailable message. Notes must remain accessible while downloading and after staging.
- After a download, run Check for updates again; Restart now must remain available for the staged update. Leave an installed copy running across a daily interval and confirm automatic checks continue without restarting the app.
- Confirm the installed and portable builds display the expected date-formatted version.

Record Windows version, GPU/driver, display models, connection types, and pass/fail notes without committing real monitor identifiers, EDID data, screenshots, or logs to the repository.
