# VaultSync 1.8.6 complete app walkthrough

Target runtime: **9–11 minutes**. Record every numbered scene as a separate,
app-content-only clip. The visual actions and narration cues are the production
timeline; do not substitute old 1.8.5 footage.

Use only the isolated `Client Portal` demo profile, with at least two completed
restore points, one passed recovery drill, and destinations named `Local SSD`
and `Offsite NAS`. Never expose real names, paths, credentials, notifications,
or macOS chrome.

## 01 — Welcome and task-based onboarding

**On screen:**

1. Begin on Dashboard with the first-run card open.
2. Select **Next**, then **Open Settings**.
3. Scroll the page behind the card slightly, then use **Back** and **Next**.
4. Hold on the checklist without selecting **Skip**.

**Narration cues:**

- **00:02** — Welcome to VaultSync. This narration is AI-generated locally.
  Version 1.8.6 guides source, destination, project, schedule, restore point,
  and recovery proof.
- **00:17** — The guide is a compact card, not a blocking overlay. The page
  behind it stays usable, and completed steps come from actual app state.
- **00:32** — Continue later preserves progress, while Back explains any step
  again without changing configuration.

## 02 — Finish a recoverable setup

**On screen:**

1. Show the prepared Projects root and fallback destination steps.
2. Open **Projects**, register `Client Portal`, and show its policy card.
3. Open **Schedule** and show the prepared automatic timing.
4. Open **Backups**, create the first restore point, and review it.
5. Open **Recovery**, run the prepared read-only drill, and hold on **Passed**.
6. Select **Finish** only after the onboarding card reports recovery proved.

**Narration cues:**

- **00:02** — Client Portal is registered only after VaultSync discovers it
  and I confirm its policy. Discovery alone never starts copying files.
- **00:15** — Schedule makes the protection timing explicit before the first
  run. Backups then creates a stored restore point at the chosen destination.
- **00:30** — Setup is not complete merely because a backup exists. I review
  the restore point and run a read-only recovery drill.
- **00:44** — Finish appears only after the drill passes, so onboarding ends
  with evidence rather than an assumption.

## 03 — Dashboard decisions first

**On screen:**

1. Open **Dashboard**.
2. Point to Restore readiness, Required action, Next run, and Known good.
3. Follow one overview link and return.
4. Move across KPI, recent activity, weekly trend, and storage sections.

**Narration cues:**

- **00:02** — Dashboard now starts with four decisions: can I restore, what
  needs action, when protection runs next, and which restore point is known good.
- **00:17** — Each card opens the page that explains or resolves its state.
  Empty states say what evidence is missing instead of implying failure.
- **00:31** — Activity, weekly trends, and storage detail remain below the
  action-first overview for routine monitoring.

## 04 — Projects, optional folders, and safe removal

**On screen:**

1. Open **Projects**, select `Client Portal`, and point to its health.
2. Show destination, preset, exclusions, tags, encryption, and auto-backup.
3. Pause automatic backup, show the state, then resume it.
4. Expand the prepared `Client work` folder and point to its membership,
   health summary, batch actions, and compact management menu.
5. Select **Remove**, read the preview, and cancel.

**Narration cues:**

- **00:02** — Projects is now the complete management workspace. Each project
  exposes health, destination, preset, exclusions, tags, encryption, and its
  own automatic-backup state.
- **00:20** — Pausing a project does not delete its history or stored data.
  Optional folders keep members together, summarize health, and expose
  deliberate folder-wide actions without replacing each project card.
- **00:36** — Remove shows the exact boundary before it runs: local
  registration and indexed history are removed, while source files and stored
  backup payloads remain untouched.

## 05 — Operational schedule and constraints

**On screen:**

1. Open **Schedule**.
2. Point to the readiness verdict, next run, destination, and power condition.
3. Show upcoming opportunities and per-project coverage.
4. Scroll to quick policy edits, switch to manual mode, and return to automatic.
5. Point to interval and quiet hours, then finish with the prepared policy restored.

**Narration cues:**

- **00:02** — Schedule is the operational view of automatic protection. It
  explains whether work can run now and which runtime rule is decisive.
- **00:15** — The forecast shows upcoming timer opportunities after quiet-hour
  deferrals, while project coverage shows what participates and its latest
  stored backup.
- **00:30** — Quick edits update the same scheduling policy as Settings.
  Unchanged projects are checked and skipped without writing another backup.

## 06 — Backups, policies, and compare

**On screen:**

1. Open **Backups**, select `Client Portal`, and point to **Backup all**.
2. Show destination, encryption, restore, and verification policies.
3. Point to queued, scanning, hashing, writing, and verifying activity states.
4. Select a restore point and show Keep, Open, Explore, Restore, Compare, and Delete.
5. Open **Compare**, show the summary and a text diff, then return.

**Narration cues:**

- **00:02** — Backups combines project policy with the restore points already
  stored. Manual and automatic work use the same explicit activity states.
- **00:17** — Scanning, hashing, writing, and verifying stay distinct, so
  post-backup verification never masquerades as ordinary copy progress.
- **00:32** — Restore points can be protected, explored, restored, compared,
  or deleted. Compare explains added, modified, and removed content before a
  restore decision.

## 07 — Searchable History

**On screen:**

1. Open **History**, search for `Client Portal`, then clear the search.
2. Open the filters and return them to the prepared state.
3. Select a restore point, edit its safe demo note, and save.
4. Toggle protection and return it to the prepared state.
5. Point to Compare, Open backup, and Recovery.

**Narration cues:**

- **00:02** — History brings snapshots, backups, restores, proofs, and reports
  into one searchable timeline.
- **00:16** — The inspector records why a restore point matters through labels,
  notes, tags, known-good state, and protection from routine cleanup.
- **00:31** — From the same evidence I can compare content, open the stored
  copy, or continue into Recovery.

## 08 — Recovery evidence and drill

**On screen:**

1. Open **Recovery** and point to the decisive state and checklist.
2. Show coverage windows and the 3-2-1 advisor.
3. Select `Client Portal`, run a drill, and expand its evidence.
4. Point to linkage, destination, inventory, bytes, and restore plan.
5. Export the report into the isolated demo folder.

**Narration cues:**

- **00:02** — Recovery answers whether the project can be recovered now and
  names the evidence behind that answer.
- **00:16** — Coverage and three-two-one guidance count only reachable copies
  and destinations explicitly confirmed as offsite.
- **00:31** — A drill reads stored data without writing into the live project.
  Passing evidence covers linkage, access, inventory, bytes, and a read-only
  restore plan.
- **00:47** — Export evidence package saves a portable, redacted, checksummed
  record for review.

## 09 — Guide and shared terminology

**On screen:**

1. Open **Guide**.
2. Move through the workflow topic cards.
3. Scroll through Backup, Snapshot, Restore point, Verification, Known good,
   Protected, and Recovery drill.
4. Resize or show the prepared narrow layout briefly if practical.

**Narration cues:**

- **00:02** — Guide keeps help inside the app and connects each everyday task
  to the page where it happens.
- **00:15** — The glossary separates a snapshot of file state from a stored
  restore point, and verification from a recovery drill.
- **00:30** — Known good describes evidence; protected describes retention.
  Those labels are related, but they are not interchangeable.

## 10 — Settings: general and transfer

**On screen:**

1. Open **Settings** at the top.
2. Point to Projects root, startup, tray, background-close, and mini widget.
3. Toggle one reversible option twice, preserving its final value.
4. Show retention simulation, history sync, bandwidth, quiet hours, and battery behavior.

**Narration cues:**

- **00:02** — Settings saves ordinary changes immediately. The projects root
  controls discovery, while startup and tray choices control app behavior.
- **00:19** — Retention simulation previews cleanup before any restore point is
  removed. Transfer limits, quiet hours, and battery rules make background work
  predictable.

## 11 — Settings: destinations and encryption

**On screen:**

1. Expand `Offsite NAS` in advanced destinations.
2. Point to alias, path, Test, mount ownership, offsite, sync, resume, retry, and quota.
3. Show credential controls without revealing a password.
4. Show archive encryption, password enrollment, rotation, and Lock now.

**Narration cues:**

- **00:02** — Advanced destinations can represent local, removable, or network
  storage. Test results, mount ownership, offsite status, retries, resume, and
  quota remain visible per destination.
- **00:23** — Archive encryption happens locally before upload. Passwords stay
  in the operating system credential store, and the app never needs to reveal
  them in a recording.

## 12 — Settings: performance, appearance, and notifications

**On screen:**

1. Show compression, incremental transfer, verification, hashing, and cache controls.
2. Open Appearance and preview theme choices without saving a change.
3. Toggle compact mode twice.
4. Show the separate success, failure, and low-disk notification controls.

**Narration cues:**

- **00:02** — Performance controls expose the tradeoff between speed, storage,
  and confidence through compression, incremental copies, verification,
  hashing, and scan caching.
- **00:21** — Appearance can follow the system or use a curated theme. Compact
  mode changes density, while notification choices keep failures and low disk
  warnings independent from routine success messages.

## 13 — Settings: maintenance and destructive previews

**On screen:**

1. Show diagnostics, support bundle, consistency scan, and repair dry-run.
2. Point to update status, preview channel, and language.
3. Select **Reset to defaults**, read its preview, then cancel.
4. Repeat the preview-and-cancel flow for **Clear local cache** and **Forget all projects**.
5. Never confirm a destructive action.

**Narration cues:**

- **00:02** — Diagnostics and maintenance stay local until I choose to export
  them. Consistency scans and repair dry-runs separate review from execution.
- **00:20** — Every destructive Settings action now explains its exact effect
  before it can run.
- **00:32** — Clearing cache or forgetting the local index does not delete
  project source files or stored backups, but the confirmation remains
  deliberate because local state will change.

## 14 — Readiness close

**On screen:**

1. Return to Dashboard and hold on the four overview cards.
2. Show the mini widget or tray action only if it fits inside an app-only crop.
3. Return to Recovery and end on the passed `Client Portal` drill for three seconds.

**Narration cues:**

- **00:02** — That is the VaultSync 1.8.6 loop: schedule protection, see what
  needs action, find the stored point, and prove it can be read.
- **00:17** — Before relying on a backup, confirm a reachable destination, a
  recent restore point, and a passed recovery drill.
- **00:31** — Create it, find it, verify it, and know how to restore it.
