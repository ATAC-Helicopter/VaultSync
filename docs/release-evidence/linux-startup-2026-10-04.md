# Linux desktop startup investigation — BUG-19012 / #751



- The original login-started process remained before Avalonia application initialization. Startup logs stopped after native platform options.
- Managed debugger attachment to that actual process located the main thread inside SkiaSharp 4.152.0 `SKFontManager.get_Default`, awaiting `sk_fontmgr_create_default`; the lazy default manager was still uninitialized.
- A host fontconfig rule excluding installed web fonts already existed when investigated, but was written after the original process started. It was not created or changed by this repository work.
- Isolated native font-manager probes on the current host succeed for both installed and updated libraries. This does not establish which earlier font/configuration caused the original hang.
- After stopping only the stuck application process and relaunching the same stable executable, startup proceeds through application initialization and metadata activity. Desktop visibility still needs maintainer confirmation; logs/process existence are insufficient evidence.
- No user data, installed binary, system security settings or font configuration was changed. No product fix or root-cause qualification is claimed.

## Acceptance

- [ ] Reproduce and identify the triggering font/configuration and login environment without changing the live user profile.
- [ ] Provide a durable, scoped fix or supported remediation, preserving normal font fallback and existing functionality.
- [ ] Verify a visible, interactive window on the affected Linux desktop, including restart/login startup; retain environment and timing evidence.
- [ ] Check packaged Linux builds and prevent startup qualification from treating a living process as a successful desktop launch.
- [ ] Keep Windows/macOS behavior qualified separately.

Refs #736. Diagnostic evidence and limits: docs/release-evidence/linux-startup-2026-10-04.md. Keep this issue open until the actual acceptance evidence exists.

Local full diagnostics: installed startup session log and /tmp/vaultsync-installed-relaunch-20261004.log. These are temporary/private host evidence, not public artifacts.

## Current-host recheck — 2026-10-07

Read-only managed debugger attachment to the installed 1.8.9 process found the
main thread in `Avalonia.Controls.Platform.ManagedDispatcherImpl.RunLoop` at
line 143, rather than font-manager initialization. The `this` runtime value was
`ManagedDispatcherImpl`. Current startup/session logs and the heartbeat show
ongoing application activity. Explicit property evaluation was disabled by the
IDE, so no window visibility or interactivity conclusion is drawn.

The attach session was stopped/detached and the same process remained alive. No
font configuration, installed binary, credentials or application behavior was
changed. This run did not reproduce the earlier startup block; it supplies no
durable fix or release qualification. The original trigger and visible-window,
restart/login acceptance remain open.

## Third-party artifact inspection — 2026-10-09

REA 6.1.0's read-only managed PE provider inspects the installed stable
`SkiaSharp.dll` without loading its code. The retained [sanitized evidence](skiasharp-managed-boundary-2026-10-09.json)
binds SHA-256, MVID and assembly version `4.152.0.0`, identifies managed AnyCPU
metadata and records the `sk_fontmgr_create_default` P/Invoke declaration against
`libSkiaSharp`. The PE container's x86 machine field is not proof of a 32-bit
managed runtime or native-library architecture.

This confirms the declared managed/native boundary relevant to the previous
stack; it does not identify a triggering font, prove actual native export
resolution, decompile the native library or qualify the 1.9.0 build. Native
providers are not configured on this host. No target code was executed, no
proprietary code/assets were copied, and no product fix or live startup result
is claimed. BUG-19012 remains open.
