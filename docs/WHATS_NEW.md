# What's New

## [1.4.1]

### macOS freeze fix
- Launch-on-login updates no longer block the UI thread.
- Autosave no longer triggers long UI stalls on macOS.

### Startup reliability
- Windows and macOS login/startup entries now use the correct launch command.
- macOS LaunchAgent includes a working directory for more consistent startup.

### Diagnostics (kept small)
- Automatic diagnostics are kept, but only the most recent 5 sessions are retained.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
