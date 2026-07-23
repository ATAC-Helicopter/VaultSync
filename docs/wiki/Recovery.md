# Recovery

Recovery answers a harder question than “did the backup finish?”: can VaultSync still find, open, and validate the stored recovery point?

## Run a recovery drill

1. Open **Recovery**.
2. Find the project you want to check.
3. Select **Run drill**.
4. Expand the result to review every check and any suggested action.

The drill is read-only. It does not restore files into the project or change the backup.

For an available folder or ZIP recovery point, VaultSync checks:

- project, backup, and snapshot linkage;
- destination and payload reachability;
- a bounded inventory of stored files;
- expected file count when complete metadata is available;
- up to 5,000 files and 2 GiB against recorded size and SHA-256 values;
- a read-only restore plan for identical files, overwrites, and newer destination conflicts.

## Understand the result

- **Passed:** every check that could run succeeded.
- **Attention:** the point is present, but a limitation or warning needs review.
- **Failed:** at least one required check failed; use the evidence action to investigate.

Encrypted recovery points remain limited unless unlocked. VaultSync does not request or retain an encryption password just to improve a readiness score.

A passing drill proves only the checks that ran. Periodically perform a real restore for important data.

## Review 3-2-1 coverage

The advisor measures:

- three copies, including the live project;
- two distinct storage media;
- one reachable destination explicitly marked as offsite.

VaultSync never guesses physical location from a path, hostname, mount, or protocol. Enable **Count as offsite copy** in destination settings only when you know the storage is physically elsewhere.

## Protect important points

Protected points are excluded from automatic retention cleanup. VaultSync can recommend a point after a release label, large deletion, high churn, or when no recent protected baseline exists, but it never protects one automatically.

Automatic retention also preserves the newest point with a passing byte-level proof. This safety floor moves when a newer point passes and is separate from manual protection.

## Export a report

Select **Export report** to write a Markdown summary under `Documents/VaultSync/Exports/Recovery`. The report includes readiness, coverage, 3-2-1 status, recommendations, drill history, and bounded evidence. It remains local until you choose to share it.

Technical details:

- [Disaster recovery](../DISASTER_RECOVERY.md)
- [Recoverability engine](../RECOVERABILITY_ENGINE.md)
- [Privacy](../PRIVACY.md)
