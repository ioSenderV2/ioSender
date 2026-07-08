# Compose a demo video

**When:** you've captured CNC + app footage and want the composited demo clip.
**Full recipe:** `docs/demo-videos/README.md` (grounded in real code refs — that doc is authoritative;
this is the quick map).
**Memory context:** `iosender-demo-videos.md`.

Composite model: **CNC video = base layer**, **app screen recording = PiP overlay**. The overlay is
state-driven — full app during setup, shrinks to the run-control strip during RUN. Combine **after**
recording with ffmpeg (NOT live single-shot — don't adjust visuals while running the machine).

## Capture (proven rig)

- **OBS 32**, Simple / QSV H.264, 1920x1200, Hybrid MP4 → `C:\Users\steve\Videos`.
- **Exeldro "Source Record" plugin** — per-source filter (Record Mode = "Recording") on each source →
  3 named files from ONE Record button (App / Front Left / Back Right).
- **App source = Display Capture** (Window Capture comes up black under WGC/BitBlt on the Iris Xe).
- 2× **Tapo C101 RTSP** cams: `rtsp://RTSPUser:<pw>@192.168.1.{45,105}:554/stream1`.
- **iPhone** = handheld close-up inserts (b-roll, no marker sync).

## Markers & auto-record (built into ioSender)

- Launch with **`-demomarker`** → writes `%AppData%\ioSender\ioSender.demo-markers.csv` rows
  (`timestamp,event`): SESSION_START, RUN_START/RUN_END, TIMELAPSE_ON/OFF, OBS_*. `RUN_START` is the
  universal 3-track sync point and drives the overlay shrink.
- **Timelapse toggle** on the run bar (visible only under `-demomarker`) — operator flips it at the
  camera-switch instants; marks feed the ffmpeg speedup window.
- **OBS bridge** (`ObsBridge.cs`, armed under `-demomarker`) auto-starts recording on program load and
  stops on M2/M30. Needs obs-websocket enabled and OBS started **before** ioSender (v1: no reconnect).
  Env: `IOSENDER_OBSWS_PASSWORD` (+ `_HOST`/`_PORT`, default localhost:4455).

## Composite

- ffmpeg 8.1.2 (`%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe`). Overlay+scale/crop per `docs/demo-videos/README.md` §8 defaults (1080p30 / CRF20 / crop rect for the run strip).
- Then wire the result into the manual → [wire_in_video.md](wire_in_video.md).
