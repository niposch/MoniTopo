# Architecture

MoniTopo separates portable decision-making from Windows integration and WPF presentation.

## Boundaries

- `MoniTopo.Core` owns persisted records, normalization, validation, matching, activation orchestration, and update state. It has no WPF or Win32 dependency.
- `MoniTopo.Windows` owns CCD, SetupAPI, hotkey, startup, session, and shell integration behind interfaces.
- `MoniTopo.App` owns WPF lifecycle, tray behavior, windows, view models, and user feedback.
- `MoniTopo.Recovery` is a small companion that can restore a transient rollback snapshot if the main process fails during activation.

WPF is used because MoniTopo is a compact Windows-only desktop utility that needs native input, DPI, theme, tray, and window behavior. GPU-vendor SDKs are excluded because saved profiles cover Windows display state rather than vendor-specific policy.

## Capture and persistence model

A profile stores only its desired active paths. Each path includes a composite monitor fingerprint, topology grouping, canonical position relative to the primary display, exact source and target signal data, refresh rational, orientation, path scaling, Windows UI scale, HDR state, and a friendly label. Inactive or disconnected displays are not profile requirements.

The configuration is a versioned JSON document under the user's local application-data directory. Writes go to a same-directory temporary file, flush to disk, and replace the current file while retaining one `.bak` file. Invalid documents are moved to a timestamped corruption file and reported; they are not silently reset.

## Safety boundary

Core tests operate on immutable synthetic records. `DisplayMutationAuthorization` can be created only when the caller marks an explicit user/manual activation command and the inherited environment contains the exact value `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`. The production mutation facade has no unguarded constructor. Tests inject a fake environment reader and fake native API; CI has no display mutation command and never sets the opt-in.

## Windows capture

`CcdCaptureService` queries `QDC_ALL_PATHS` with virtual-mode and Windows 11 virtual-refresh awareness. It retries a changed-buffer result, converts active source modes, exact target video signals, and target paths, identifies the primary GDI source, records clone groups, and separately enumerates connected inactive targets. Microsoft documents that buffer sizes can become stale between `GetDisplayConfigBufferSizes` and `QueryDisplayConfig`, that inactive all-path entries lack complete mode data, and that CCD coordinates are physical pixels rather than DPI-virtualized values:

- [QueryDisplayConfig](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- [GetDisplayConfigBufferSizes](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getdisplayconfigbuffersizes)
- [DisplayConfigGetDeviceInfo](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-displayconfiggetdeviceinfo)
- [DISPLAYCONFIG_TARGET_DEVICE_NAME](https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name)

Target identity starts with the CCD monitor path, friendly name, EDID manufacturer/product fields, connector type, and connector instance. A read-only SetupAPI pass matches the monitor interface path, obtains the device instance ID, and reads EDID from the device registry key. The parser retains normalized identity fields and a short mode signature, not the raw EDID. Native layout tests are based on the installed Windows SDK 10.0.26100 header definitions.

Advanced Color capture uses documented request type 9 and records the supported, enabled, and policy-disabled flags. The app treats an unsupported display as HDR off; a failed query is a capture error rather than a partially saved profile.

### Per-monitor scale compatibility

Windows does not document request types `-3` and `-4`, despite Windows Settings and maintained tools using them. MoniTopo isolates the two packets in `UndocumentedDpiScalePackets.cs`, asserts their 32-byte and 24-byte layouts, runtime-probes every source, and rejects capture when a complete current value cannot be mapped. The percentage table and relative-index mapping were cross-checked on 20 July 2026 against three independent implementations:

- [Firefox `WinUtils.cpp`](https://searchfox.org/firefox-main/source/widget/windows/WinUtils.cpp)
- [MartinGC94/DisplayConfig DPI structures](https://github.com/MartinGC94/DisplayConfig/tree/main/src/DisplayConfig/Native/Structs)
- [lihas/windows-DPI-scaling-sample](https://github.com/lihas/windows-DPI-scaling-sample/blob/master/DPIHelper/DpiHelper.h)

The corresponding setter maps a saved percentage back to the runtime-probed relative index. An unavailable percentage fails preflight; scaling is never silently skipped.

## Activation and recovery

`ProfileActivationService` owns the platform-neutral state machine. It serializes activation, resolves every required identity, captures rollback data, runs capability preflight, validates the complete CCD plan, and starts recovery before the first mutation. It then applies topology temporarily, waits for two stable CCD observations, re-resolves addresses, applies scale and HDR, verifies the normalized profile, persists the supplied topology, verifies again, updates `lastActivatedProfileId`, and signals recovery success. Any failure after the first mutation attempts exact rollback; a failed exact rollback invokes Windows' extended-topology fallback and is reported as a fallback rather than a successful restoration.

`DisplayConfigurationPlanBuilder` reconstructs one source mode per saved source group and one target mode per display. It assigns currently exposed CCD sources one-to-one, preserves clone groups, and refuses legacy/incomplete profiles without a captured target signal. `SetDisplayConfig` is called with `SDC_USE_SUPPLIED_DISPLAY_CONFIG` and either `SDC_VALIDATE` or `SDC_APPLY`. Verified persistence adds `SDC_SAVE_TO_DATABASE`. Virtual-mode and virtual-refresh awareness are always declared; `SDC_ALLOW_CHANGES` is deliberately absent so Windows cannot substitute a nearby mode.

The transient rollback file contains versioned raw CCD paths/modes plus the active targets' scale and Advanced Color states. `MoniTopo.Recovery` takes a per-transaction lock, opens the named success event, signals a readiness handshake, and watches both the main process and a bounded deadline. A missing success signal triggers the same guarded rollback implementation in the companion process. Corrupt payloads do not invoke a display writer. Successful or handled transactions remove the payload and rollback data; a recovery outcome remains available as `result.json` when the companion had to act.

The flag combinations and device-info packets follow the current Windows CCD contracts:

- [SetDisplayConfig](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setdisplayconfig)
- [DisplayConfigSetDeviceInfo](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-displayconfigsetdeviceinfo)
- [DISPLAYCONFIG_DEVICE_INFO_TYPE](https://learn.microsoft.com/windows/win32/api/wingdi/ne-wingdi-displayconfig_device_info_type)

## Derived active state and refresh events

Identity resolution produces a deterministic one-to-one map before profile comparison. Active matching then compares the exact active set and every managed property; it never trusts the last button pressed. See [display identity](display-identity.md) for the scoring and alternative-assignment ambiguity check.

Windows display, device, and settings messages feed `DisplayStateRefreshService`. Bursts are debounced for 350 ms, refreshes are serialized, and a 15-second fallback consistency check covers driver changes that emit no useful message. Polling and pending work are suppressed while the session is locked or the app is shutting down. A changed state becomes `Custom`; the refresh service has no activation path.
