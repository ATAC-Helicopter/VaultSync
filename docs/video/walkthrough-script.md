# VaultSync complete app walkthrough

Target runtime: **7–9 minutes**. Record each numbered scene as a separate
app-only clip. The action list is the visual timeline; the quoted text is the
canonical AI narration.

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

**Narration:**

> Let’s set up VaultSync, make a real backup, and check that it can actually be recovered. This small guide card stays out of the way, so I can use the page behind it without closing the tour. Next moves forward, Back lets me check an earlier step, and Skip only dismisses the guide. The checklist is tied to the app: choose where projects live, choose where backups go, register one project, and finish its first backup.

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

**Narration:**

> I’ll start with two locations. The projects root tells VaultSync where to look; it does not silently back up every folder underneath it. The destination must be somewhere else—a separate folder, external disk, or mounted network share. On Projects, I register Client Portal and keep its suggested preset, so build output and disposable caches stay out of the backup. Then I open Backups and run it. The guide completes only after the restore point really exists.

## 03 — Dashboard

**On screen:**

1. Open **Dashboard**.
2. Point in order to Projects, Backups, Storage, and Restore readiness.
3. Slowly move across **Backups this week** and **Recent activity**.
4. Scroll to **Storage usage** and **Backup storage**.
5. Click **Review** on Restore readiness, wait for Recovery to open, then return
   to Dashboard.

**Narration:**

> The Dashboard is where I’d check in day to day. These cards answer four quick questions: how many projects are registered, whether backups are running, how much space they use, and whether a recent restore point is ready. The weekly chart separates scheduled, manual, and imported work. Recent activity explains what happened, while storage shows what is growing. If readiness looks wrong, Review opens the evidence behind the score.

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

**Narration:**

> Projects is the library of folders VaultSync knows about. Refresh searches the projects root, but nothing is tracked until I register it. A preset supplies sensible ignore rules for tools such as .NET, Unity, Blender, or Node, and I can inspect those rules before using them. Tags, colors, and avatars make a larger library easier to scan. For unusual projects, the editor can preview, clone, import, or export a custom preset. Snapshot records the current file state; copying that state to storage happens from Backups.

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

**Narration:**

> Backups is the working page. Backup all handles every eligible project, or I can run just this one. Each project may follow the global policy or override its schedule, destination, encryption, restore mode, and verification level. Down here are the completed restore points. Keep protects an important baseline from routine cleanup. Open shows its folder, Explore browses its contents, and Restore always asks for a target. Compare explains which files were added, changed, or removed and previews supported text differences. Delete is explicit, and a failed verification is never discarded without asking.

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

**Narration:**

> History brings every recorded event into one timeline. I can search by project or detail, then narrow the list by event type, date, or protected state. Selecting an item opens its facts on the right. A useful label and short note explain why a restore point matters months later; protection keeps it out of normal retention cleanup. From here I can compare it, open the stored copy, or continue into Recovery. Records imported from another machine are marked clearly, so they are not confused with backups made on this Mac.

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

**Narration:**

> Recovery is the part that proves the backup is useful. The summary separates ready projects from those needing attention, at risk, or currently unavailable. Coverage checks for usable restore points across one day, one week, one month, and three months. The three-two-one advisor looks for three copies, two types of storage, and one genuinely offsite copy; VaultSync only grants offsite credit when I mark a destination that way. A drill checks the database link, destination, inventory, stored bytes, and a read-only restore plan. It does not touch the live project. Export report saves the result as Markdown.

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

**Narration:**

> Settings saves ordinary changes as I make them. General controls discovery and what happens when the app opens or closes: resume the last page, launch at sign-in, stay in the tray, or show the small progress widget. Scheduling turns automatic backups on and sets the interval. Retention limits ordinary points, but Simulate retention shows the proposed cleanup before anything is removed; I keep delete confirmation enabled. History sync moves non-secret backup metadata between machines, while auto-import and the restore prompt help avoid divergent histories. Bandwidth limits protect a busy network. Quiet hours postpone scheduled work, not manual backups, and battery pause avoids a heavy run while the laptop is unplugged.

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

**Narration:**

> Simple mode uses one fallback location. Advanced mode is for separate local, removable, or network destinations. Each gets a unique name, a path, and a Test result. Pre-mounted means macOS owns the connection. Auto-mount can use a saved credential, and auto-unmount only removes a mount VaultSync created. “Offsite” should mean physically somewhere else. Each destination can sync history, resume interrupted transfers, retry with backoff, and warn against a soft quota. Credentials can live in the operating-system keychain, so there is no reason to expose a password here. Archive encryption happens before upload, and a forgotten encryption password cannot be recovered. Compression suits many small files. Delta sync sends changed blocks; incremental mode hard-links unchanged files where supported, so those modes do not run together. Auto-tuning and parallel uploads can improve throughput. Verification and full hashing trade speed for confidence. The scan cache is faster; aggressive timestamp trust is the least conservative option. Finally, storage rules can prefer external drives, report health warnings, and reserve free space.

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

**Narration:**

> Appearance can follow the system, stay light or dark, or use a custom theme. Custom themes start from a preset and expose practical roles such as surfaces, text, accent, success, warning, and danger. Compact mode fits more on screen; avatars add icons or initials. Notifications has a master switch plus choices for success, failure, and low disk space. Turning off the master preserves those choices.

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

**Narration:**

> Advanced settings are mostly for diagnosis and maintenance. Verbose logging adds detail; disk logging keeps it locally. I can open the console, export logs, or create a support bundle that I can review before sharing. Crash assistance also prepares a local report—it never sends one by itself. A maintenance window can run consistency checks, prepare a repair plan, and refresh metadata. I always inspect the dry-run first; Fix now applies only that exact current plan. When imported metadata conflicts, I choose whether local or imported values win. Direct builds can follow stable or preview updates, while Store builds stay Store-managed. Language changes the interface, and Reset restores defaults. The Danger zone clears caches or forgets the local index, never the project or backup files, but forgetting that index cannot be undone inside the app.

## 12 — Tray, readiness checklist, and close

**On screen:**

1. Return to Dashboard.
2. If the prepared build exposes the tray/menu item without revealing the macOS
   menu bar, open its menu and point to Open VaultSync, Snapshot all, Backup all,
   recent actions, Settings, and Quit. Otherwise use a tight secondary capture
   and crop it into the app frame during editing.
3. Trigger the prepared mini widget, show progress briefly, then close it.
4. Return to Recovery and end on the successful `Client Portal` drill.

**Narration:**

> The tray keeps common actions close: open VaultSync, snapshot or back up everything, revisit recent work, open Settings, or quit. The mini widget follows a tray backup without opening the full window. Before I call setup finished, I want four things: a recent restore point, a destination that passes Test, a drill that reads stored data, and a plan for second-media and offsite copies. The illustrated tour and Settings reference are in the documentation. That is the loop: create it, find it, verify it, and know how to restore it.
