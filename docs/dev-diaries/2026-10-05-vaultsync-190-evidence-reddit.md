# VaultSync 1.9.0: the disk recovery checker passes, but the support gate correctly fails

An image can verify successfully and still leave you with a machine that will not boot. I am building VaultSync, and this week's 1.9.0 work is about making that gap explicit before adding disk imaging.

The repository now has two different checks:

```text
$ python3 scripts/disk_support.py check
Disk qualification plan valid: 4 profiles, 0 qualified; structural checks do not prove recovery.
[exit 0]

$ python3 scripts/disk_support.py check --require-qualified
No qualified disk profile; stable disk support remains gated.
[exit 1]
```

The plan is valid. Disk recovery is not qualified. The second result is supposed to stay red.

To qualify a profile, the evidence must cover **capture → validate image → independent boot → restore → validate the restored system**, all bound to the same run, image, builds and environment. You cannot borrow a successful boot from another experiment. A simulation does not count, and a VM test does not qualify physical hardware.

Today I also fixed a hole in the checker: it accepted a capture performed before the recorded engine approval. It now rejects that order and invalid approval artifacts. All 124 Python tests pass locally. Those tests use synthetic reports to test the rules, not to pretend a restore happened. A hash can prove a report has not changed; it cannot prove the report is true.

The first engine proposal is deliberately limited: offline Linux recovery media, GPT/UEFI ext4 or NTFS, 512/4096-byte sectors. Engine choice still needs approval. Live capture, Secure Boot, encryption and other layouts are outside that first proposal. **No disk imaging engine or boot medium is implemented yet. Stable remains 1.8.9.**

The earlier CLI work is preserved, with discovery in 1.9.1 and resource automation in 1.9.5. 1.9.0 is the first release in that sequence, and [its PR remains draft](https://github.com/ATAC-Helicopter/VaultSync/pull/736).

If you have restored an entire machine, what broke after the image verified: boot, disk discovery, encryption, or the actual application state? That is the failure I want the qualification process to catch.

[Full dev update and evidence links](https://fglabs.dev/devlog/vaultsync-190-when-a-green-check-is-not-recovery).
