# Walkthrough video

This is a real native-app recording workflow, modeled on the published
ProofRestore demo. It does not build a slideshow and does not require an API
key.

- [`walkthrough-script.md`](walkthrough-script.md) is the exact click, scroll,
  toggle, and narration timeline.
- [`recording-guide.md`](recording-guide.md) is the production runbook.
- `record-macos.sh` captures one app-only macOS scene with visible click pulses.
- `render-narration.py` uses the local, open Kokoro speech model and places
  every narration cue at its scripted on-screen time. Set
  `VAULTSYNC_NARRATION_VOICE` to choose another voice from the model bundle.
- `build-video.sh` preserves each complete spoken cue, blends scene boundaries,
  normalizes audio, and creates a 1080p H.264 MP4 with selectable captions.
- `build/` contains generated recordings, audio, captions, and final exports and
  is ignored by Git.

## Requirements

- macOS Screen Recording permission for the terminal used to record;
- macOS Accessibility permission for the operator or any automation used to
  control VaultSync;
- Python 3, `kokoro-onnx`, and `soundfile`;
- `ffmpeg` and `ffprobe`.

Install the narration tools once (a virtual environment is recommended):

```bash
python3 -m pip install -r docs/video/requirements.txt
mkdir -p docs/video/build/models
curl -L \
  https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.int8.onnx \
  -o docs/video/build/models/kokoro-v1.0.int8.onnx
curl -L \
  https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin \
  -o docs/video/build/models/voices-v1.0.bin
```

Kokoro runs locally after this one-time download. No narration API, account, or
usage fee is involved.

## Build sequence

1. Follow [`recording-guide.md`](recording-guide.md) to prepare the safe demo
   profile and determine the app-only capture rectangle.
2. Render narration and captions:

   ```bash
   python3 docs/video/render-narration.py
   ```

3. Record each scene from [`walkthrough-script.md`](walkthrough-script.md):

   ```bash
   VAULTSYNC_CAPTURE_RECT=260,120,1600,900 \
     bash docs/video/record-macos.sh 01 55
   ```

   The narration is padded to the existing capture duration. If a cue cannot
   finish naturally before the next scripted action, rendering stops and names
   the scene and cue that need adjustment.

4. Assemble and verify:

   ```bash
   bash docs/video/build-video.sh
   ```

Output:
`docs/video/build/vaultsync-guided-walkthrough.mp4`.

The final publication description must disclose that the narration is
AI-generated. Upload `docs/video/build/captions.srt` separately to video hosts
that discard embedded caption tracks.
