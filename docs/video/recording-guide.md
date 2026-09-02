# VaultSync walkthrough production guide

This runbook produces a real walkthrough: the native app is clicked, scrolled,
and operated on screen. It follows the ProofRestore production pattern—fixed
demo data, deterministic chapters, visible pointer/click feedback, neural
narration, and selectable captions—with native macOS capture replacing
Playwright.

## 1. What is and is not automated

ProofRestore is a browser app, so Playwright could identify controls by role,
move a synthetic pointer, scroll the page, and record Chromium directly.
VaultSync is a native Avalonia app. Playwright cannot control it.

For VaultSync:

- macOS `screencapture` records only the VaultSync rectangle;
- `-k` adds visible click feedback;
- the operator follows the checked-in scene actions;
- macOS Accessibility scripting may be added later for stable controls, but
  screen coordinates must not become the source of truth because window
  scaling and Settings layout can change;
- local Kokoro speech creates action-cued narration and captions without an API
  key; and
- `ffmpeg` aligns each real recording with the matching cues.

This is deliberately chapter-based. A UI change requires rerecording one short
scene, not the whole video.

## 2. Prepare an isolated demo

Never record the normal VaultSync profile. The final take must contain only
synthetic names and paths.

1. Create a temporary recording root outside the repository:

   ```bash
   recording_root="$(mktemp -d /tmp/vaultsync-video.XXXXXX)"
   mkdir -p \
     "$recording_root/config" \
     "$recording_root/projects/Client Portal/src" \
     "$recording_root/destinations/Local SSD" \
     "$recording_root/destinations/Offsite NAS" \
     "$recording_root/exports"
   ```

2. Put a few small, obviously synthetic files in `Client Portal`, including
   two text files that can produce a readable comparison. Do not copy a real
   project.
3. Start VaultSync with the isolated config directory. A fresh profile keeps
   both `appsettings.json` and its default `vaultsync.db` inside this directory:

   ```bash
   VAULTSYNC_CONFIG_DIR="$recording_root/config" \
   VAULTSYNC_FORCE_ONBOARDING=1 \
   dotnet run --project src/VaultSync.UI/VaultSync.UI.csproj \
     --framework net10.0
   ```

   Verify that `DbPath`, `ProjectsRoot`, and every destination path point
   inside `recording_root`.

4. Complete a rehearsal setup and create at least two restore points. Modify
   one synthetic text file between them so Compare has meaningful content.
5. Configure:

   - project: `Client Portal`;
   - destinations: `Local SSD` and `Offsite NAS`;
   - one destination marked offsite;
   - non-secret demo tags and notes;
   - no saved network username or password;
   - no real email, machine name, notification text, or mounted share path.

6. Quit VaultSync and archive the prepared recording root so every failed take
   can restart from the same state. Keep this archive outside Git.

## 3. Prepare the desktop

1. Use a clean macOS desktop or recording Space.
2. Set display scaling before determining the capture rectangle. Do not change
   scaling during production.
3. Set the VaultSync **content area** to exactly `1600×900` logical pixels if
   the display allows it. `1440×810` is the supported smaller alternative.
4. Disable notifications and Focus-sensitive popups at the operating-system
   level.
5. Close or move every other window behind VaultSync.
6. Use the prepared light or dark theme consistently.
7. Keep the dock, desktop, menu bar, title-bar traffic lights, and “What’s new”
   window outside the recorded rectangle.
8. Grant Screen Recording permission to the terminal that runs
   `record-macos.sh`. If interaction is scripted, grant Accessibility
   permission as well.

## 4. Determine the app-only capture rectangle

The required value is `x,y,width,height` in screen coordinates.

1. Bring VaultSync to the front.
2. Press Shift-Command-4 and drag exactly around the app content that should
   appear in the finished video. The measured width and height must be exactly
   16:9; do not approximate the bottom or side edge.
3. Note the displayed origin and size, then press Escape instead of capturing.
4. Save the value for the terminal session:

   ```bash
   export VAULTSYNC_CAPTURE_RECT=260,120,1600,900
   ```

5. Record a five-second framing test:

   ```bash
   bash docs/video/record-macos.sh 00 5
   ```

6. Inspect `docs/video/build/capture/00.mov` at 100% size. It must contain the
   complete intended content area with no clipped sidebar, header, or lower
   controls, and only VaultSync:
   no menu bar, dock, desktop, other app, notification, or modal unrelated to
   the scene.

## 5. Render the local voice first

Install and render:

```bash
python3 -m pip install -r docs/video/requirements.txt
mkdir -p docs/video/build/models
curl -L \
  https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.int8.onnx \
  -o docs/video/build/models/kokoro-v1.0.int8.onnx
curl -L \
  https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin \
  -o docs/video/build/models/voices-v1.0.bin
python3 docs/video/render-narration.py
```

The renderer uses Kokoro's `af_heart` voice by default. It synthesizes each
timestamped cue separately, places it on the capture timeline, and writes one
WAV and one SRT per chapter. Speech generation is local after the model files
are downloaded.

Check the prepared timeline for a scene:

```bash
ffprobe -v error -show_entries format=duration -of csv=p=0 \
  docs/video/build/audio/01.wav
```

The WAV duration must match the scene capture. Narration begins at the cue times
in [`walkthrough-script.md`](walkthrough-script.md), so visual actions must be
rehearsed against those timestamps.

## 6. Record the scenes

For every numbered section in
[`walkthrough-script.md`](walkthrough-script.md):

1. Restore the isolated demo profile to the scene's starting state.
2. Put the correct page and scroll position on screen.
3. Rehearse the action list once without recording.
4. Start capture:

   ```bash
   bash docs/video/record-macos.sh 01 55
   ```

5. Wait through the macOS countdown with the pointer outside the important
   content.
6. Perform the actions at a relaxed, readable pace:

   - move before clicking;
   - dwell for roughly one second after a result appears;
   - use smooth trackpad or mouse-wheel scrolling;
   - avoid fast pointer circles and repeated correction movements;
   - allow two seconds on important verdicts and safety text.

7. Review the clip immediately at full size.
8. Repeat the scene if the pointer obscures a label, scrolling jumps, a real
   path appears, a popup enters the crop, or an action finishes outside the
   narration order.

The scene list states which controls may be toggled twice and which must only
be indicated. Preserve all prepared values. Never execute Delete, Restore,
Forget all projects, Fix now, password reveal, or Reset confirmation in the
final take.

## 7. Assemble

When `build/capture/01.mov` through `build/capture/14.mov` exist:

```bash
bash docs/video/build-video.sh
```

The assembler:

- rejects non-16:9 or mismatched captures instead of hiding framing mistakes;
- scales each complete clip directly to 1920×1080 without cropping;
- uses the locally rendered narration instead of recording microphone audio;
- normalizes speech to approximately -16 LUFS;
- preserves the full scene recording and rejects stale narration timelines;
- blends scene boundaries with short picture and audio crossfades;
- creates H.264 video and 48 kHz stereo AAC audio; and
- embeds a selectable English caption track and writes an SRT sidecar.

## 8. Quality gate

Watch the complete MP4 once without skipping and verify:

- every click in narration is visible and occurs in the same order;
- every Settings toggle named in narration is on screen;
- toggle demonstrations finish at their prepared values;
- all scrolling is slow enough to read section headings;
- onboarding remains visible while Settings and sidebar controls work;
- no “What’s new” modal or macOS chrome appears;
- no real name, path, credential, notification, or machine detail appears;
- captions match the spoken words and can be disabled;
- speech is clear on headphones and laptop speakers;
- the first caption discloses AI-generated narration;
- no music or third-party copyrighted media is present; and
- the ending holds on a successful Recovery drill for at least two seconds.

Also inspect representative frames:

```bash
mkdir -p docs/video/build/review
ffmpeg -y -i docs/video/build/vaultsync-guided-walkthrough.mp4 \
  -vf "fps=1/30,scale=960:-1" docs/video/build/review/frame-%03d.jpg
```

## 9. Publish

Suggested title:

> VaultSync Complete Walkthrough — Setup, Backups, Recovery, and Every Setting

Suggested description:

> Set up VaultSync, create and inspect backups, compare restore points, run a
> read-only recovery drill, and understand every settings group. Narration is
> AI-generated locally with Kokoro. No copyrighted music is used.

Upload `vaultsync-guided-walkthrough.mp4`. If the host removes embedded
subtitles, upload `captions.srt` separately and verify that captions remain
optional rather than burned in.
