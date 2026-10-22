# Display identity and matching

Windows CCD adapter, source, and target IDs can change after reconnects, driver updates, docking, and topology changes. MoniTopo therefore stores a composite fingerprint and resolves every required profile display against the currently connected targets as one assignment.

## Signals and scores

Scores are additive. String comparisons are case-insensitive and empty values never match.

| Signal | Points |
| --- | ---: |
| Exact monitor device path | 110 |
| Exact device container ID | 105 |
| Exact device instance ID | 100 |
| Exact credible EDID serial | 95 |
| EDID manufacturer and product pair | 35 |
| Output technology and connector instance | 30 |
| Friendly model name | 15 |
| Physical dimensions | 10 |
| Output technology alone | 10 |
| Preferred mode | 8 |
| Supported-mode signature | 8 |
| Previously confirmed fingerprint with a matching identity anchor | 200 |

A candidate must reach 75 points. Conflicting credible EDID serials, manufacturer/product identities, or conflicting container IDs without an agreeing credible serial exclude a candidate. A port, path, or remembered-binding bonus cannot cancel evidence of a different physical panel. Empty and common placeholder serials are not treated as credible serials.

Device paths and instance IDs are strong when stable, but a mismatch is not an automatic rejection because a driver reinstall can replace them. A unique combination of manufacturer/product, model, dimensions, connector, and mode data can resolve a serial-less display. MoniTopo never treats a model name alone as sufficient.

## Global assignment and ambiguity

The resolver builds the complete saved-display × connected-target score matrix, removes candidates below the confidence threshold, and applies the Hungarian assignment algorithm. This maximizes the total score while guaranteeing that one target cannot satisfy two saved displays.

After finding the best assignment, the resolver forbids each selected edge in turn and recomputes the global optimum. If another complete assignment is within 10 total points, the result is ambiguous and activation/capture update stops for user resolution. This catches indistinguishable same-model displays and swaps that independent greedy selection misses.

A manual binding records the selected target's fingerprint as its identity baseline; the runtime adapter/target address is only a hint. The bonus requires an agreeing identity anchor and remains subject to the physical-identity conflict checks. Older bindings containing only a runtime address remain readable but receive no authority from that address alone. Rebinding a display is an explicit user action, and a target that disappears or changes identity before the binding is saved must be selected again.

A remembered binding cannot make an absent target appear present. If any required display has no valid complete assignment, the result names the saved friendly label and no display change may begin.

## Active profile matching

Once identity resolution succeeds, matching requires exactly the saved active set, primary display, pairwise source/clone relationships, canonical primary-relative positions, resolution, equivalent refresh rational, physical target-signal timing, orientation, CCD scaling, Windows UI scale, and HDR state. Virtual desktop-image and refresh-boost metadata are compared when present in the capture. Target-signal comparison permits at most 0.1 Hz VSync difference. Pixel and horizontal clocks may differ by the relative VSync difference plus 0.01% rounding, capped at 0.2%, so consistent fractional/rounded forms such as 59.94/60 remain equivalent. Active/total pixel dimensions, scanline ordering, and the Miracast VSync divider must agree; driver timing-standard labels and reserved bits do not determine a match. The stored fields follow Microsoft's [video-signal](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_video_signal_info) and [desktop-image](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_desktop_image_info) contracts.

Extra connected inactive targets are ignored. Extra active targets, a missing required target, or any managed setting difference produces `Custom`; MoniTopo does not automatically reapply the last profile. Capture schema 4 adds virtual desktop-image and refresh-boost metadata. Corrected HDR captures from schema 3 remain usable, but should be recaptured before testing full virtual-mode fidelity.

When migrated data contains two profiles that both match, `lastActivatedProfileId` wins if it is one of them; otherwise profile order wins.

## Privacy

Fingerprints remain in the local configuration file. MoniTopo has no telemetry or sync. Logs should use friendly labels and short transaction IDs, not raw device paths, instance IDs, EDID bytes, or serials. Synthetic tests use invented identifiers and constructed EDID bytes only.
