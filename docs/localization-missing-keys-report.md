# Localization Missing Keys Report

Baseline: `Localization/strings.en.json`

Generated: 2026-02-28 11:12:20 UTC

## strings.ar.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **0**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- None

## strings.bn.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **0**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- None

## strings.de.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **28**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- `Backups.Status.Deleting`
  - Reason: possible mojibake/encoding issue
  - English: `Deleting backup files...`
  - Current: `Sicherungsdateien werden gelÃ¶scht...`
- `Backups.Status.LowDisk`
  - Reason: possible mojibake/encoding issue
  - English: `Backup skipped: low disk space.`
  - Current: `Sicherung Ã¼bersprungen: wenig Speicherplatz.`
- `DriveHealth.BlockedMessage`
  - Reason: possible mojibake/encoding issue
  - English: `Backup skipped: drive health failing on {0} ({1}).`
  - Current: `Sicherung Ã¼bersprungen: Laufwerk fehlerhaft auf {0} ({1}).`
- `LogConsole.ExportReady`
  - Reason: possible mojibake/encoding issue
  - English: `Log export ready. You can share the file.`
  - Current: `Log-Export bereit. Sie kÃ¶nnen die Datei teilen.`
- `Logs.Snippet.MetadataImportRootFailure`
  - Reason: possible mojibake/encoding issue
  - English: `Metadata import failed for projects root.`
  - Current: `Metadatenimport fÃ¼r Projektwurzel fehlgeschlagen.`
- `MetadataSync.Review.LinkProjects`
  - Reason: possible mojibake/encoding issue
  - English: `Projects to link`
  - Current: `Projekte verknÃ¼pfen`
- `Projects.Notification.SnapshotSuccess`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot created for '{0}'.`
  - Current: `Snapshot fÃ¼r '{0}' erstellt.`
- `Settings.Advanced.LanguageDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Choose the language used across the UI.`
  - Current: `Sprache der BenutzeroberflÃ¤che wÃ¤hlen.`
- `Settings.Advanced.SaveVerboseLogsDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Write logs to a local file while verbose logging is enabled.`
  - Current: `Schreibt Protokolle in eine lokale Datei, wenn die ausfÃ¼hrliche Protokollierung aktiv ist.`
- `Settings.Appearance.CompactDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Use tighter spacing for dense project lists.`
  - Current: `Engeren Abstand fÃ¼r dichte Listen verwenden.`
- `Settings.Backups.ConfirmDeleteDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Shows a warning before removing backup data on the destination.`
  - Current: `Zeigt eine Warnung, bevor Backup-Daten am Ziel gelÃ¶scht werden.`
- `Settings.Backups.HistorySyncDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Store portable metadata in destinations so other machines can merge history.`
  - Current: `Speichere portable Metadaten in den Zielen, damit andere Rechner die Historie zusammenfÃ¼hren kÃ¶nnen.`
- `Settings.Backups.KeepSnapshotsDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Older snapshots are removed once this limit is reached.`
  - Current: `Ã„ltere Snapshots werden entfernt, sobald dieses Limit erreicht ist.`
- `Settings.Credentials.AddProfile`
  - Reason: possible mojibake/encoding issue
  - English: `Add credential`
  - Current: `Anmeldeinfo hinzufÃ¼gen`
- `Settings.Danger.ClearCacheDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Removes cached metadata and state. Project data is not deleted.`
  - Current: `Entfernt Zwischenspeicher und Status. Projektdaten werden nicht gelÃ¶scht.`
- `Settings.Danger.ResetDatabase`
  - Reason: possible mojibake/encoding issue
  - English: `Resets VaultSync's internal database (projects, snapshots, backups). No files on disk or NAS are removed.`
  - Current: `Setzt die interne VaultSync-Datenbank zurÃ¼ck (Projekte, Snapshots, Sicherungen). Keine Dateien auf Platte oder NAS werden gelÃ¶scht.`
- `Settings.Destinations.AutoUnmount`
  - Reason: possible mojibake/encoding issue
  - English: `Auto-unmount after backup`
  - Current: `Automatisch aushÃ¤ngen`
- `Settings.Destinations.AutoUnmountTooltip`
  - Reason: possible mojibake/encoding issue
  - English: `Unmount after backup only if VaultSync mounted it.`
  - Current: `Nach der Sicherung wieder aushÃ¤ngen, falls wir gemountet haben.`
- `Settings.DestinationsMode.Tooltip`
  - Reason: possible mojibake/encoding issue
  - English: `Turn on Advanced to configure multiple destinations and credentials; leave off to use one simple backup folder.`
  - Current: `Erweitert aktivieren, um mehrere Ziele und Anmeldedaten zu konfigurieren; deaktivieren fÃ¼r einen einzelnen Backup-Ordner.`
- `Settings.General.RunInBackground`
  - Reason: possible mojibake/encoding issue
  - English: `Run in background when closing`
  - Current: `Beim SchlieÃŸen im Hintergrund weiterlaufen`
- `Settings.General.RunInBackgroundDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Hide to tray instead of quitting when closing the window.`
  - Current: `Beim SchlieÃŸen in die Taskleiste statt Beenden.`
- `Settings.General.ShowTrayIconDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Keep a tray/menu icon for quick actions.`
  - Current: `Ein Tray/Menu-Symbol fÃ¼r schnelle Aktionen behalten.`
- `Settings.General.ShowWindowOnTray`
  - Reason: possible mojibake/encoding issue
  - English: `Show main window for tray actions`
  - Current: `Fenster fÃ¼r Tray-Aktionen anzeigen`
- `Settings.Storage.ReserveFreeSpaceDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Stop creating new backups once a disk is below this threshold.`
  - Current: `Neue Sicherungen stoppen, sobald ein Laufwerk unter diesen Wert fÃ¤llt.`
- `Tray.Health.Error`
  - Reason: possible mojibake/encoding issue
  - English: `Unable to check drive health.`
  - Current: `Laufwerkszustand kann nicht geprÃ¼ft werden.`
- `Tray.Health.NoPathDetail`
  - Reason: possible mojibake/encoding issue
  - English: `Backup path not set. Set a backup location to check drive health.`
  - Current: `Sicherungspfad nicht gesetzt. Lege einen Ort fest, um den Zustand zu prÃ¼fen.`
- `Tray.Health.Recheck`
  - Reason: possible mojibake/encoding issue
  - English: `Recheck now`
  - Current: `Erneut prÃ¼fen`
- `Update.Banner`
  - Reason: possible mojibake/encoding issue
  - English: `New update available: {0} ({1})`
  - Current: `Neues Update verfÃ¼gbar: {0} ({1})`

## strings.es.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **0**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- None

## strings.fr.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **1**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- `Backups.Summary.AgeLabel`
  - Reason: possible mojibake/encoding issue
  - English: `Age`
  - Current: `Âge`

## strings.hi.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **0**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- None

## strings.it.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **1**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- `Backups.Activity.Tooltip`
  - Reason: possible mojibake/encoding issue
  - English: `{0}: {1} backups · {2}`
  - Current: `{0}: {1} backup Â· {2}`

## strings.pt.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **35**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- `Backups.Activity.Tooltip`
  - Reason: possible mojibake/encoding issue
  - English: `{0}: {1} backups · {2}`
  - Current: `{0}: {1} backups Â· {2}`
- `Backups.Section.Group.Global`
  - Reason: possible mojibake/encoding issue
  - English: `Global snapshots`
  - Current: `Instantâneos globais`
- `Dashboard.Activity.SnapshotCreated`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot created`
  - Current: `Instantâneo criado`
- `Main.HeaderBackups`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshots & history`
  - Current: `Instantâneos e histórico`
- `Projects.Health.NoSnapshots`
  - Reason: possible mojibake/encoding issue
  - English: `No snapshots yet`
  - Current: `Nenhum instantâneo ainda`
- `Projects.LastSnapshot.NoneShort`
  - Reason: possible mojibake/encoding issue
  - English: `No snapshots yet`
  - Current: `Ainda não há instantâneos`
- `Projects.Notification.Registered`
  - Reason: possible mojibake/encoding issue
  - English: `Project '{0}' registered. Next click will create a snapshot.`
  - Current: `Projeto '{0}' registrado. O próximo clique criará um instantâneo.`
- `Projects.Notification.SnapshotFailure`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot failed. Check logs for details.`
  - Current: `Instantâneo falhou. Veja os logs para detalhes.`
- `Projects.Notification.SnapshotSuccess`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot created for '{0}'.`
  - Current: `Instantâneo criado para '{0}'.`
- `Projects.Sort.Latest`
  - Reason: possible mojibake/encoding issue
  - English: `Sort: Latest snapshot`
  - Current: `Ordenar: Último instantâneo`
- `Projects.Stat.LastSnapshotLabel`
  - Reason: possible mojibake/encoding issue
  - English: `Last snapshot taken`
  - Current: `Último instantâneo`
- `Projects.Stat.RecentSnapshots`
  - Reason: possible mojibake/encoding issue
  - English: `Recent snapshots`
  - Current: `Instantâneos recentes`
- `Projects.Stat.SnapshotPreset`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot preset`
  - Current: `Predefinição de instantâneo`
- `Projects.Stat.SnapshotStorage`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot storage`
  - Current: `Armazenamento de instantâneos`
- `Projects.Stat.SnapshotTrend`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot size trend`
  - Current: `Tendência do tamanho do instantâneo`
- `Projects.Stat.TimeSinceLast`
  - Reason: possible mojibake/encoding issue
  - English: `Time since last snapshot`
  - Current: `Tempo desde o último instantâneo`
- `Projects.Stat.TotalSnapshots`
  - Reason: possible mojibake/encoding issue
  - English: `Total snapshots`
  - Current: `Total de instantâneos`
- `Projects.Stat.Unchanged`
  - Reason: possible mojibake/encoding issue
  - English: `First snapshot / unchanged`
  - Current: `Primeiro instantâneo / sem mudanças`
- `Settings.Backups.FullHash`
  - Reason: possible mojibake/encoding issue
  - English: `Full snapshot hashing`
  - Current: `Hash completo dos instantâneos`
- `Settings.Backups.KeepSnapshots`
  - Reason: possible mojibake/encoding issue
  - English: `Keep last N snapshots per project`
  - Current: `Manter os últimos N instantâneos por projeto`
- `Settings.Backups.ScanCache`
  - Reason: possible mojibake/encoding issue
  - English: `Use scan cache for snapshots`
  - Current: `Usar cache de varredura para instantâneos`
- `Settings.Backups.ScanCacheDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Skips unchanged folders to speed up snapshot scans.`
  - Current: `Ignora pastas inalteradas para acelerar a varredura de instantâneos.`
- `Settings.Danger.ForgetProjectsDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Resets VaultSync's internal database (projects, snapshots, backups). No files on disk or NAS are removed.`
  - Current: `Reseta o banco interno do VaultSync (projetos, instantâneos, backups). Nenhum arquivo em disco ou NAS é removido.`
- `Settings.Danger.ResetDatabase`
  - Reason: possible mojibake/encoding issue
  - English: `Resets VaultSync's internal database (projects, snapshots, backups). No files on disk or NAS are removed.`
  - Current: `Reseta o banco interno do VaultSync (projetos, instantâneos, backups). Nenhum arquivo em disco ou NAS é removido.`
- `Settings.General.ShowWindowOnTrayDescription`
  - Reason: possible mojibake/encoding issue
  - English: `Bring VaultSync to front when starting backups or snapshots from the tray.`
  - Current: `Trazer o VaultSync para frente ao iniciar backups ou instantâneos pela bandeja.`
- `Snapshots.Action.Default`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot now`
  - Current: `Criar instantâneo agora`
- `Snapshots.Notification.FailureTitle`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot failed`
  - Current: `Falha no instantâneo`
- `Snapshots.Notification.SuccessTitle`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot completed`
  - Current: `Instantâneo concluído`
- `Tray.Snapshot.All`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot all projects`
  - Current: `Instantâneo de todos os projetos`
- `Tray.Snapshot.Title`
  - Reason: possible mojibake/encoding issue
  - English: `Snapshot`
  - Current: `Instantâneo`
- `Tray.Tooltip`
  - Reason: possible mojibake/encoding issue
  - English: `VaultSync - snapshots & backups`
  - Current: `VaultSync - instantâneos e backups`
- `Dashboard.Hint.NoSnapshots`
  - Reason: possible mojibake/encoding issue
  - English: `No snapshots yet`
  - Current: `Ainda sem instantâneos`
- `Dashboard.Hint.StorageLatest`
  - Reason: possible mojibake/encoding issue
  - English: `Total across latest snapshots`
  - Current: `Total dos últimos instantâneos`
- `Projects.LastSnapshot.None`
  - Reason: possible mojibake/encoding issue
  - English: `No snapshots yet`
  - Current: `Ainda não há instantâneos`
- `Projects.Stat.AverageSnapshot`
  - Reason: possible mojibake/encoding issue
  - English: `Average snapshot size`
  - Current: `Tamanho médio do instantâneo`

## strings.ru.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **0**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- None

## strings.zh.json

- Missing keys: **0**
- Extra keys: **0**
- Possibly broken translations: **0**

### Missing (with English source text)
- None

### Extra (present in target, not in English)
- None

### Possibly broken translations
- None

