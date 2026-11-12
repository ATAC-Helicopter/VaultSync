# VaultSync Recovery Lab: first safe backup-and-restore check

**Status:** community test protocol v1 · applicable to the **currently published stable desktop release** on Windows, macOS and Linux. This is **not** a system-disk recovery or 1.9 qualification test.

A completed backup does not by itself prove that data can be recovered. This lab makes one small, reproducible, **non-destructive** check using entirely disposable files. It separates a read-only Recovery drill from a real restore into a different, empty folder.

> **Safety boundary:** never select a real project, system disk, production NAS share, or folder containing important data for the lab. Do not erase, overwrite, reformat, or unplug any device. Stop if VaultSync proposes writing to the original source folder or an unexpected path. A Recovery drill is read-only; a real restore is not.

## Prerequisites

- A [published stable VaultSync build](https://github.com/ATAC-Helicopter/VaultSync/releases/latest). Record its exact version and operating system.
- Three distinct **empty** locations that you own and can discard:
  1. **Lab source**, e.g. `VaultSync-Lab-Source` in your Documents folder.
  2. **Lab backup destination**, e.g. `VaultSync-Lab-Backup` in another location, **not inside** the source.
  3. **Lab restore target**, e.g. `VaultSync-Lab-Restored`, also **outside** the source and backup location.
- Sufficient storage space for a few kilobytes. Local folders are best for the first run.

Keep these three locations separate. If a folder already exists or is not empty, **choose a different folder** rather than reusing it.

## Protocol A · create a known fixture

In the empty **Lab source** folder, create two UTF-8 text files with the **exact** following contents, including each line break:

`readme.txt`:

```text
VaultSync Recovery Lab
case: synthetic-backup-check
expected: separate-folder-restore
```

`notes.txt`:

```text
alpha
bravo
charlie
```

Create a subfolder named `nested` with a third file `nested/check.txt`:

```text
nested-file-present
```

Optional: compute and save SHA-256 hashes of those three source files **outside the source folder** before backing up. On Windows use `Get-FileHash -Algorithm SHA256` in PowerShell; on macOS use `shasum -a 256`; on Linux use `sha256sum`. Record the commands and results without including a full private local path in any public report.

**Pass condition A:** the source contains exactly three small files, one of them in a nested directory. All files contain only synthetic text.

## Protocol B · back up and inspect

1. Follow the [Quick Start](Quick-Start.md) to register the source as a VaultSync project. Choose **No preset** so this tiny fixture is not excluded.
2. Configure the **Lab backup destination** as a dedicated destination (simple or advanced), checking that it is not inside the source or restore target.
3. Run a **manual backup** of the project. Wait for an explicit completed result.
4. Confirm the backup appears in **Backups** and **History** and record its type and timestamp.
5. Use **Snapshot Explorer** to check that `readme.txt`, `notes.txt` and `nested/check.txt` are present.
6. Open **Recovery**, select the point and run **Run drill**. Record the exact outcome (Passed, Attention or Failed) and any limitations.

**Pass condition B:** the backup finishes, all three expected files are recorded, and the read-only drill produces an interpretable result. **A passing drill is not equivalent to a successful real restore.** A warning or failure is a useful result to report, not a reason to conceal the test.

## Protocol C · real restore into a disposable target

1. Make sure the **Lab restore target** is empty and does not point to the original source.
2. From the selected restore point, open the app's restore workflow and **inspect the destination and preview** carefully. Choose the empty **Lab restore target** only if your published build supports selecting it.
3. If the UI cannot restore to a separate empty target, **stop here** and mark Protocol C **Not performed (safe alternate target unavailable)**. **Never** restore over the lab source merely to complete this exercise.
4. If the preview confirms the intended independent target, perform the restore.
5. Inspect the resulting files using your file manager. Verify all three relative paths, text contents, and hashes if recorded. Note whether any unexpected files were created.

**Pass condition C:** all three files are present at the intended separate target, their contents match the fixture, and the original source remains unchanged. A restore-preview-only result must be marked as **not a completed restore**.

## Optional Protocol D · changed source

Only after Protocols A–C, edit the **disposable source** `notes.txt` by adding `delta` on a new line. Run a second snapshot and backup, and compare the two points. The comparison should identify the changed file. Do not run retention, deletion, overwrite or disk-recovery experiments for this lab.

## What to report

Copy the short checklist below into a Discord `#beta-discussion` message, a public Forum post, or a [GitHub beta-feedback issue](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose):

```text
VaultSync Recovery Lab v1
Build/version:
Platform / OS version:
Package / installation channel:
Backup destination: local / external / NAS (no real path)
Backup type:
A · Fixture created: Pass / Fail
B · Backup completed: Pass / Fail
B · Explorer found all 3 files: Pass / Fail
B · Recovery drill: Passed / Attention / Failed / Not available
C · Separate target restore: Pass / Fail / Not performed (safe alternate target unavailable)
C · Restored file contents match: Yes / No / Not checked
D · Optional change comparison: Pass / Fail / Not performed
Unexpected behavior:
Reproduction steps:
Safe screenshot / redacted logs (optional):
```

Include a **published build identifier** and observable evidence. Never post secrets, tokens, encryption material, raw personal file paths, sensitive backup contents, or private ticket transcripts. If something may be a security vulnerability, use [private security reporting](../../SECURITY.md), not a public issue.

## What this exercise does **not** establish

- It does not prove disaster recovery of a real machine or a bootable disk image.
- It does not qualify 1.9 system recovery on Linux, Windows or macOS.
- It does not cover large data sets, encryption, a NAS failure, power-loss safety or long-term retention.
- Its three-file test is intentionally simple; different workloads require their own risk-appropriate tests.
- Running the lab is voluntary. Do not share or upload any personal data to participate.

The project uses these results to improve procedures and reproducibility. A negative result is just as valuable as a positive one when accompanied by the exact build and reproduction steps.

For the boundaries of current read-only checks, see [Recovery](Recovery.md). For the public support/community entry points, see [Community and Support](../COMMUNITY.md).
