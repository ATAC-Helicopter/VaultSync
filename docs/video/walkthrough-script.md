# VaultSync complete app walkthrough

Target runtime: **8–10 minutes**. Record each numbered scene as a separate
app-only clip. The action list is the visual timeline. Narration is split into
timestamped cues so each explanation begins beside the control or result it
describes.

The recording account must contain the prepared `Client Portal` demo project,
at least two completed restore points, and two destinations named `Local SSD`
and `Offsite NAS`. Never expose real usernames, paths, credentials, project
names, notifications, or menu-bar content.

## 01 — Welcome and interactive onboarding

**On screen:**

1. Begin on Dashboard with the first-run onboarding card open.
2. Move the pointer over the card, then click **Next**.
3. Click **Open Settings** through the still-visible card.
4. Scroll Settings by roughly one card to prove that the app remains
   interactive.
5. Click **Back** once, then **Next**. Do not click **Skip** in the final take.
6. End with the pointer resting on the current onboarding action.

**Narration cues:**

- **00:02** — Welcome to VaultSync. We’ll set up one project, create a backup,
  and finish by proving the stored copy is readable.
- **00:14** — The setup card is a guide, not a modal. I can move through its
  steps while the page behind it remains fully usable.
- **00:27** — Completed steps stay checked, so it is always clear what is ready
  and what still needs attention.

## 02 — Complete the first backup

**On screen:**

1. On the onboarding **Projects root** step, click **Select**, choose the
   prepared demo root, and confirm.
2. Scroll to **Fallback backup location**, click **Select**, choose the prepared
   demo destination, and confirm.
3. Use the sidebar to open **Projects**.
4. Select the `Client Portal` candidate, choose the detected preset, and click
   the registration action.
5. Open **Backups** from the sidebar and click the project-level backup action.
6. Hold on visible progress, then on the completed restore point.
7. Click **Finish** on the onboarding card only after it reports completion.

**Narration cues:**

- **00:02** — Client Portal has been discovered, but VaultSync does not protect
  it until I register it.
- **00:11** — I create a snapshot first. That records the project’s current file
  state without copying data yet.
- **00:19** — On Backups, I start the first real copy. Progress and destination
  health remain visible while it runs.
- **00:27** — The setup step completes only after a restore point actually
  exists.

## 03 — Dashboard

**On screen:**

1. Open **Dashboard**.
2. Point in order to Projects, Backups, Storage, and Restore readiness.
3. Slowly move across **Backups this week** and **Recent activity**.
4. Scroll to **Storage usage** and **Backup storage**.
5. Click **Review** on Restore readiness, wait for Recovery to open, then return
   to Dashboard.

**Narration cues:**

- **00:02** — The Dashboard is the daily overview: registered projects, backup
  activity, storage use, and recovery readiness.
- **00:11** — Recent activity explains what happened, while the weekly chart
  separates manual, scheduled, and imported work.
- **00:21** — Lower on the page, storage shows what is growing and how much room
  remains.

## 04 — Projects

**On screen:**

1. Open **Projects** and click the `Client Portal` project.
2. Point to discovery and refresh controls, then the registered-project list.
3. Open the preset selector; move through several presets without changing the
   saved selection.
4. Open the tag editor, demonstrate an existing tag and color, then close it.
5. Expand the preset editor, scroll through its preview controls, then collapse
   it without saving.
6. Point to the snapshot action.

**Narration cues:**

- **00:02** — Projects is the library of folders VaultSync knows about. The
  selected card shows snapshot age, size, and current health.
- **00:13** — A preset removes disposable output from the backup. I can inspect
  or change it without touching the project files.
- **00:24** — Tags and colors make a larger library easier to scan, and the
  destination choice can stay global or be overridden here.
- **00:34** — Snapshot now records a fresh point in time. The actual stored copy
  is created from Backups.

## 05 — Backups, policies, and restore points

**On screen:**

1. Open **Backups** and point to **Backup all**.
2. Select `Client Portal`.
3. Toggle **Automatic backup** off and immediately back on.
4. Open and close the preferred destination, encryption policy, restore mode,
   and verification policy selectors without changing their final values.
5. Point to the project backup action.
6. Select a completed restore point and point to **Keep**, **Open**, **Explore**,
   **Restore**, **View diff/Compare**, and **Delete**.
7. Click **Compare**, select two prepared points, scroll through the summary and
   a text preview, then return.
8. Do not execute Restore or Delete in the final recording.

**Narration cues:**

- **00:02** — Backups starts with a quick health summary: recent work, storage,
  restore readiness, and the active destination.
- **00:13** — Each project can inherit the global policy or override its
  destination, encryption, restore mode, and verification level.
- **00:25** — Choose two restore points and Compare to understand what changed
  before deciding which one to use.
- **00:35** — This view separates added, modified, and deleted files, then shows
  readable text differences without restoring the project.

## 06 — History

**On screen:**

1. Open **History**.
2. Type `Client Portal` in search, then clear it.
3. Open each filter—project, event type, date, and protected state—then restore
   the default.
4. Select one restore point.
5. Edit its label or note with prepared non-sensitive text and save.
6. Toggle protection on, show the protected badge, then restore its prepared
   state.
7. Point to compare, open-backup, and Recovery actions.

**Narration cues:**

- **00:02** — History brings snapshots, backups, restores, and imported events
  into one searchable timeline.
- **00:13** — Selecting an event opens its recovery facts and the actions that
  are safe for that point.
- **00:24** — A label or note records why the point matters. Protection keeps an
  important baseline out of routine cleanup.
- **00:33** — From here I can open the stored copy, compare it, or continue into
  Recovery.

## 07 — Recovery and proof

**On screen:**

1. Open **Recovery**.
2. Point to the score and ready, attention, risk, and unavailable cards.
3. Scroll through coverage windows and the 3-2-1 advisor.
4. Select `Client Portal` in the project matrix.
5. Click **Run drill** and wait for the result.
6. Expand enough detail to show linkage, destination, inventory, bytes, and the
   read-only restore plan.
7. Click **Export report**, save to the prepared recording folder, and return
   to the app.

**Narration cues:**

- **00:02** — Recovery answers the important question: can this project be
  recovered now, and what evidence supports that answer?
- **00:13** — Coverage looks across several time windows. The three-two-one
  advisor counts only reachable copies and explicitly confirmed offsite storage.
- **00:25** — Run drill reads the stored point without writing into the live
  project.
- **00:35** — A passing result covers linkage, destination access, inventory,
  stored bytes, and a read-only restore plan.
- **00:44** — Export report saves this evidence as a local Markdown document.

## 08 — Settings: general, scheduling, and transfer

**On screen:**

1. Open **Settings** and return its scroll position to the top.
2. Point to **Projects root**.
3. Toggle **Resume where you left off** twice, finishing at its original value.
4. Repeat that reversible demonstration for **Launch at system startup**, **Show
   tray icon**, **Run in background when closing**, **Show main window for tray
   actions**, and **Show mini backup widget**.
5. Scroll to backup scheduling. Toggle **Enable automatic backups** twice.
6. Point to interval and retention; click **Simulate retention**, show the
   preview, and close it.
7. Toggle **Confirm before deleting backups** twice, ending enabled.
8. Toggle history sync, auto-import, and prompt-to-restore twice each.
9. Toggle bandwidth limit and quiet hours on, show their revealed fields, then
   return them to their prepared values.
10. Point to **Refresh history now** and **Pause auto-backups on battery**.

**Narration cues:**

- **00:02** — Settings saves ordinary changes immediately. The projects root
  controls discovery; it does not start a backup by itself.
- **00:16** — Startup and tray controls decide whether VaultSync resumes the
  last page, stays in the background, or shows the small progress widget.
- **00:31** — Notifications can stay quiet on success while still warning about
  failures or low disk space.
- **00:47** — Automatic backup timing and retention live together. Simulate
  retention previews cleanup before any restore point is removed.

## 09 — Settings: destination, encryption, performance, and storage

**On screen:**

1. Point to the fallback backup location.
2. Toggle **Advanced destinations** on if needed and expand `Offsite NAS`.
3. Point to Alias, Active, Path, Select, Test, and Credential.
4. Demonstrate Pre-mounted, Auto-mount, Auto-unmount, and Count as offsite by
   toggling each twice; preserve the prepared values.
5. Do the same for destination history sync, auto-import, force full export,
   checkpoint resume, retry/backoff, soft quota, and warning percent.
6. Scroll to credentials. Point to Name, keychain storage, Username, Password,
   and Show; never reveal or type a real secret.
7. Scroll to encryption. Toggle **Encrypt archive backups** on only if the demo
   credential is prepared; otherwise point without clicking. Point to session
   fallback, timeout, Set/Clear password, enrollment, rotation, and Lock now.
8. Scroll through performance. Toggle Compress, Delta sync, Incremental,
   Auto-tune, Parallel uploads, Verify after creation, Full hashing, Scan cache,
   Aggressive cache, and Pause on battery twice each, preserving dependencies
   and final values.
9. Point to external-drive preference, drive warnings, and reserve free space.

**Narration cues:**

- **00:02** — History refresh, bandwidth limits, and quiet hours control when
  background work can use the network.
- **00:13** — Advanced mode adds separate local, removable, or network
  destinations. Every destination gets a clear name, path, and Test result.
- **00:28** — Pre-mounted leaves the connection to the operating system.
  Offsite should be enabled only when the storage is physically elsewhere.
- **00:43** — History sync, resume, retries, and quota warnings can be tuned per
  destination.
- **00:58** — Archive encryption happens locally before upload. Passwords belong
  in the operating system’s secure credential store.
- **00:72** — Compression, incremental copies, verification, hashing, and scan
  caching trade speed, storage, and confidence in explicit ways.

## 10 — Settings: appearance and notifications

**On screen:**

1. Scroll to **Appearance**.
2. Open the Theme selector and preview System, Light, Dark, and Custom, ending
   on the prepared theme.
3. With Custom selected temporarily, open preset choices, palette roles, and
   advanced editor; do not save an unintended custom theme.
4. Toggle **Compact mode** twice and **Show project avatars** twice.
5. Scroll to Notifications.
6. Toggle the master, success, failure, and low-disk switches twice each,
   ending with failure and low disk enabled.

**Narration cues:**

- **00:02** — Appearance can follow the system, stay light or dark, or use a
  custom theme.
- **00:13** — Compact mode fits more on screen, while project avatars make a
  larger library easier to recognize.
- **00:22** — Notification choices remain separate, so success can stay quiet
  while failure and low disk space remain visible.

## 11 — Settings: diagnostics, maintenance, updates, and danger zone

**On screen:**

1. Scroll to **Advanced**.
2. Toggle verbose logging twice. With it temporarily on, toggle saving logs,
   then return both to their prepared values.
3. Point to Open console, Export logs, Export support bundle, Import support
   bundle, and crash report assistance.
4. Show the maintenance window fields and daily consistency, repair-plan, and
   metadata-refresh choices; restore their original state.
5. Click **Run consistency scan** if the demo database is isolated. Show **Run
   repair dry-run**, but do not click **Fix now**.
6. Point to metadata conflict choices without applying either.
7. Scroll to Updates. Point to startup checks, interval, Check now, preview
   channel, and Language.
8. Point to **Reset to defaults** without confirming.
9. Scroll to Danger zone. Point to **Clear local cache** and **Forget all
   projects** without clicking.

**Narration cues:**

- **00:02** — Advanced settings begin with diagnostics. Logs and support bundles
  stay local until I choose to share them.
- **00:15** — Maintenance can scan consistency, prepare an exact repair plan,
  and refresh imported metadata.
- **00:28** — I review the dry-run before applying a repair. Metadata conflicts
  also wait for an explicit local-or-imported choice.
- **00:42** — Update checks, preview channel, and language are visible beside
  their current status.
- **00:52** — The Danger zone clears local state, not project or backup files,
  but forgetting the index still requires deliberate confirmation.

## 12 — Tray, readiness checklist, and close

**On screen:**

1. Return to Dashboard.
2. If the prepared build exposes the tray/menu item without revealing the macOS
   menu bar, open its menu and point to Open VaultSync, Snapshot all, Backup all,
   recent actions, Settings, and Quit. Otherwise use a tight secondary capture
   and crop it into the app frame during editing.
3. Trigger the prepared mini widget, show progress briefly, then close it.
4. Return to Recovery and end on the successful `Client Portal` drill.

**Narration cues:**

- **00:02** — That covers the main controls. The Dashboard brings the important
  health signals back into one place.
- **00:11** — Before calling setup complete, check for a recent restore point
  and a destination that passes Test.
- **00:20** — Then run a drill that reads stored data and review the plan for a
  second storage type and an offsite copy.
- **00:29** — That is the VaultSync loop: create it, find it, verify it, and know
  how to restore it.
