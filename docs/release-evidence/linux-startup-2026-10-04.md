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
