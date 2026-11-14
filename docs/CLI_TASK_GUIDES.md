# VaultSync CLI task guides — 1.9.1

## setup

Build the development CLI and install its local tool package; configure PATH.
Check `vaultsync --version --json` and `vaultsync --help` before running scripts.
This version is not published yet. Never assume future commands exist.

## inspect

Use `mktemp -d` for disposable test data. Register with `add-project NAME PATH
--db DB`. Inspect with `list-projects --db DB --json`, `history NAME --db DB`
and `diff NAME --db DB`. Snapshot hashes do not establish stored payload recovery.

## mirror

Run `sync NAME DEST --db DB --dry-run` before a real transfer.
Mirroring uses the live source; it creates no recorded backup recovery point.
Read `sync --help` and review the exact source/target before transfer.

## restore

Restore a recorded folder backup with `restore NAME TARGET --db DB --dry-run`.
Stored-byte verification precedes changes; use disposable targets during tests.
Review `restore --help` for supported selectors and cleanup. Never use a live
source tree as a substitute for recorded recovery evidence.

## automate

A systemd user timer can invoke supported flat commands with explicit database
and paths. Check each exit code before continuing; direct logs to a private file.
Run `watch NAME --db DB --dry-run` for a finite preview; live watchers are
long-running and stop/drain on cancellation. Removal always needs `--yes`.

## migrate

The future 1.9.5 CLI adds resource routes and `--output json` v1 envelopes.
Do not send those flags to this executable. Keep legacy scripts on documented
flat commands, pin the version and migrate only after the future contracts are
qualified. Current `--json` outputs retain their older shapes.
