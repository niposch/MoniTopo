# Manual display acceptance

Run this checklist only on the release-candidate installer, with a keyboard path available on every intended display. These tests intentionally change the real desktop and are deferred until the user receives the release candidate.

## Preparation

- Record the current Windows display arrangement in screenshots or notes.
- Confirm the recovery companion is installed beside `MoniTopo.exe`.
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

## Missing, extra, and changed displays

- Disconnect or fully power off the television while Desktop is active; Desktop should remain matched.
- Attempt Movie while the television is disconnected; activation must fail before changing another display and name the missing television.
- Enable an extra display; the current state must become Custom.
- Change one managed setting manually in Windows; state must become Custom and MoniTopo must not revert it automatically.
- If the hardware permits, connect two indistinguishable same-model monitors and confirm MoniTopo requests a manual binding instead of guessing.

## Lifecycle and layout

- Launch a second MoniTopo instance and confirm the existing instance opens its settings window.
- Close the main window and confirm MoniTopo remains in the tray; use Exit MoniTopo and confirm it terminates.
- Test the popup with bottom, top, left, and right taskbar positions where Windows permits them.
- Test popup placement and keyboard operation on each monitor in a mixed-DPI setup; confirm it remains inside the work area.
- Enable run at login, sign out/in, and confirm background startup does not open the main window. Disable it and repeat.

## Recovery

- Use the documented synthetic/manual-test failure injection after temporary topology application. Confirm the prior setup is restored and the failure is reported.
- Repeat with the main process terminated after the recovery-ready signal, using a safe working profile rather than an all-displays-off payload. Confirm the companion restores the prior state and MoniTopo reports the saved result on next launch.

## Installer and update

- Install per-user without administrator rights; record the expected unsigned SmartScreen warning.
- Confirm Start Menu launch, first-run autostart choice, repair/reinstall behavior, and uninstall cleanup including the startup entry.
- Check for an update against a draft test release. Confirm checks do not download automatically, Download is explicit, and Install and restart requires a second explicit action.
- Confirm the installed and portable builds display the expected date-formatted version.

Record Windows version, GPU/driver, display models, connection types, and pass/fail notes without committing real monitor identifiers, EDID data, screenshots, or logs to the repository.
