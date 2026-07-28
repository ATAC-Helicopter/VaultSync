# Walkthrough video

This is a real native-app recording workflow, modeled on the published
ProofRestore demo. It does not build a slideshow and does not require an API
key.

- [`walkthrough-script.md`](walkthrough-script.md) is the exact click, scroll,
  toggle, and narration timeline.
- [`recording-guide.md`](recording-guide.md) is the production runbook.
- `record-macos.sh` captures one app-only macOS scene with visible click pulses.
- `render-narration.py` uses Microsoft Edge's conversational
  `en-US-BrianNeural` voice by default. Set `VAULTSYNC_NARRATION_VOICE` to
  choose another installed Edge voice.
- `build-video.sh` aligns the scene recordings with narration, normalizes audio,
  and creates a 1080p H.264 MP4 with selectable captions.
- `build/` contains generated recordings, audio, captions, and final exports and
  is ignored by Git.

## Requirements

- macOS Screen Recording permission for the terminal used to record;
- macOS Accessibility permission for the operator or any automation used to
  control VaultSync;
- Python 3 and the free `edge-tts` package;
- `ffmpeg` and `ffprobe`.

Install the narration tool once (a virtual environment is also fine):

```bash
python3 -m pip install edge-tts
```

No OpenAI, Azure, or Microsoft API key is used.

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

   Use the narration duration shown by `ffprobe` as the scene duration and add
   three to five seconds for recording margin.

4. Assemble and verify:

   ```bash
   bash docs/video/build-video.sh
   ```

Output:
`docs/video/build/vaultsync-guided-walkthrough.mp4`.

The final publication description must disclose that the narration is
AI-generated. Upload `docs/video/build/captions.srt` separately to video hosts
that discard embedded caption tracks.
