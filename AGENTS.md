# MoniTopo contributor rules

- If `MONITOPO_PROJECT_SPEC.private.md` exists, read it before work and treat it as the authoritative local plan. Never commit it.
- Never mutate the real display configuration during automated development or tests. Real display writes require an explicit manual command and `MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1`; never set that variable in automation.
- Keep Windows platform calls behind interfaces and test behavior with synthetic fixtures and hand-written fakes.
- Run formatting, build, relevant tests, and `git diff --check` before committing.
- Keep commits coherent and include tests with behavior changes.
- Do not add display-vendor SDKs, telemetry, Electron/WebView UI, or unrelated features.
- Keep user-facing writing factual and free of generic promotional wording.
- Update architecture and development documentation when behavior or constraints change.
- Never commit real monitor identifiers, EDID blobs, usernames, machine-specific paths, secrets, logs, snapshots, or recovery payloads.
- Continue through the next incomplete milestone instead of stopping after a partial implementation.
