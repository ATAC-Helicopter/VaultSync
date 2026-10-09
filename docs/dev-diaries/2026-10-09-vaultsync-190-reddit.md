# I added Back and Forward to VaultSync. They found four bugs.

Hi everyone — a small, visible step toward VaultSync 1.9.0 this time.

For anyone new here, VaultSync is an open-source desktop backup application. Most of the recent development work has been recovery foundations. This week I wanted to answer a much simpler question: can I leave backup History, check Settings, and come back without losing what I was doing?

The development window now has **Back and Forward buttons**, plus **Alt+Left / Alt+Right**. They follow the pages visited during the current session. Going back and opening a different page clears the forward branch. Restarting resumes the saved last page; it does not restore a whole navigation history or unfinished drafts.

Apparently adding two arrows also buys a guided tour of everything that forgets where you were.

The checks uncovered four real problems:

- **Unfinished snapshot labels, notes and tags disappeared during a History refresh.** Replacing timeline rows briefly cleared the list selection, which threw away the draft. Refresh now retains edits for the same project and snapshot. Choosing another snapshot loads its own metadata.
- **A delayed startup callback could redirect the first page you opened.** Saved-page restoration now happens before that first navigation.
- **A slow page save could overwrite a newer resume location.** Those writes now run in order, with queued intermediate visits coalesced.
- **Snapshot statistics crowded out project names in narrower windows.** Card headers now use two lines, so the name remains readable.

The screenshots are the real Linux development application with two synthetic projects. The History entry is a metadata-only snapshot of a 52-byte fixture file: **there is no stored backup payload or disk image in this demo**. The interface says “Metadata only” for exactly that reason.

The current local Release check passes **1,012 .NET tests and 144 Python tests**. Hosted Windows, Linux and macOS build/test checks pass on the development commit. The recorded native Linux checks cover navigation, keyboard shortcuts, the retained filter and unfinished draft. Broader desktop and accessibility qualification remains open, and Sonar is still failing while retrieving the PR.

**This is development work in draft PR #736, not a released 1.9.0 build. Stable is still 1.8.9.** Disk capture, independent boot media and disk restore still need implementation and measured recovery runs. This is the first navigation increment on the existing pages, not the complete planned interface redesign.

[Full devlog and both screenshots](https://fglabs.dev/devlog/vaultsync-190-keeping-your-place) · [Development PR](https://github.com/ATAC-Helicopter/VaultSync/pull/736)

When you leave backup history to check something else, what is the one thing you most want waiting when you come back: **the selected recovery point, filters, unfinished notes, or scroll position**?
