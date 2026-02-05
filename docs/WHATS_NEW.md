# What's New

## [1.4.1]

### Faster, steadier startup
- Initial view is now guaranteed to render immediately (no empty shell on launch).
- Settings load happens synchronously so the UI doesn’t appear blank.
- Database schema setup runs off the UI thread to prevent startup stalls.

### Smoother tray/menu bar behavior (macOS)
- Tray menu opening no longer hangs the app.
- Tray menu refreshes are deferred while the menu is opening.

### Performance cleanup
- Log capture setup is delayed slightly to avoid blocking UI init.
- Backups/dashboards load avoids blocking the UI thread.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
