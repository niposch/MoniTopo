# Architecture

MoniTopo separates portable decision-making from Windows integration and WPF presentation.

## Boundaries

- `MoniTopo.Core` owns persisted records, normalization, validation, matching, activation orchestration, and update state. It has no WPF or Win32 dependency.
- `MoniTopo.Windows` owns CCD, SetupAPI, hotkey, startup, session, and shell integration behind interfaces.
- `MoniTopo.App` owns WPF lifecycle, tray behavior, windows, view models, and user feedback.
- `MoniTopo.Recovery` is a small companion that can restore a transient rollback snapshot if the main process fails during activation.

WPF is used because MoniTopo is a compact Windows-only desktop utility that needs native input, DPI, theme, tray, and window behavior. GPU-vendor SDKs are excluded because saved profiles cover Windows display state rather than vendor-specific policy.

## Capture and persistence model

A profile stores only its desired active paths. Each path includes a composite monitor fingerprint, topology grouping, canonical position relative to the primary display, exact refresh rational, orientation, path scaling, Windows UI scale, HDR state, and a friendly label. Inactive or disconnected displays are not profile requirements.

The configuration is a versioned JSON document under the user's local application-data directory. Writes go to a same-directory temporary file, flush to disk, and replace the current file while retaining one `.bak` file. Invalid documents are moved to a timestamped corruption file and reported; they are not silently reset.

## Safety boundary

Core tests operate on immutable synthetic records. Windows mutation services will require a dedicated manual entry point plus `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`. CI has no display mutation command and never sets the opt-in.

Further sections will document identity scoring, the activation state machine, recovery protocol, event handling, DPI scaling research, and updates as those milestones are implemented.
