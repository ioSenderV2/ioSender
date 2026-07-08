# ioSender demo videos — production procedure

The repeatable process for producing the manual's demo videos: capture the app and the machine
**separately**, then composite them with **ffmpeg** keyed off a **RUN-state marker** the app emits. Written to
be followed per video without re-deciding the pipeline each time.

> **Decisions this procedure encodes** (2026-07-07):
> - **Post-composite with ffmpeg**, not a live single-shot — so you're never adjusting visuals while operating
>   the machine. Record each source cleanly, combine afterward.
> - **App emits a RUN-state marker** — ioSender timestamps the Cycle-Start / job-end transitions to a file;
>   ffmpeg uses those timestamps both to **sync** the app track to the machine track and to **shrink** the app
>   overlay at the RUN instant. No hand-keyframing.
> - **Two cameras** on the machine (wide + close-up).
> - **Host:** YouTube **unlisted**. **Wire-in:** `data-video` in the manual + `publish-pages.ps1`.

---

## 1. The target format

- **Base layer:** **Camera A = GoPro Black**, mounted in the **back-right corner** (out of reach of the gantry
  and spindle), wide FOV covering the whole machine — the fixed establishing shot.
- **Overlays (picture-in-picture):**
  - **App screen recording** — the main overlay. **Full app** during setup; **shrinks to the bottom
    run-control strip** the instant the job goes to RUN, so the cut is mostly unobscured while cutting.
  - **Camera B = iPhone** on a tripod at the **front-left**, angled down toward the **center of the
    spoilboard** — the work-area/cutter detail PiP. Optionally grows when the app overlay shrinks (RUN),
    since that's when the cut detail matters most.

```
 SETUP phase                              RUN phase (Cycle Start pressed)
┌───────────────────────────┐            ┌───────────────────────────┐
│  Camera A (wide)   ┌─────┐ │            │  Camera A (wide)          │
│                    │ App │ │            │                  ┌──────┐ │
│                    │full │ │   ──▶      │   ┌───────────┐  │ Cam  │ │
│          ┌──────┐  └─────┘ │            │   │ App: run  │  │ B    │ │
│          │ Cam B│          │            │   │ strip only│  │ big  │ │
│          └──────┘          │            │   └───────────┘  └──────┘ │
└───────────────────────────┘            └───────────────────────────┘
   app overlay = whole window               app overlay = bottom strip only
```

The **run-control strip** is the fixed bottom bar in the app — `JobControl` (Cycle Start / Feed Hold / Stop /
Rewind) + the `StatusControl` DRO/state readout ([MainWindow.xaml:183](../../ioSender%20XL/ioSender%20XL/MainWindow.xaml#L183)).
It's a horizontal band across the bottom of the window, so the "shrunk" overlay is just a **crop of the bottom
strip** from the same full-app recording — one screen recording yields both the full view and the strip view.

---

## 2. Gear & recorders

| Source | Tool | Output |
|---|---|---|
| App screen | **OBS Studio** Display/Window Capture of ioSender (or Xbox Game Bar) | `app.mp4` |
| Camera A (wide, base) | **GoPro Black**, back-right corner, whole machine in frame | `camA.mp4` |
| Camera B (detail) | **iPhone** on a tripod, front-left, angled down at spoilboard center | `camB.mp4` |
| RUN marker | **ioSender** (see §3) | `ioSender.demo-markers.csv` |

**Fixed capture settings** (keep constant so the ffmpeg recipe's crop rect stays valid):
- App recording: **1920×1080, 30 fps**, ioSender **maximized** (so the run strip is always at the same Y).
- Cameras: **1080p, 30 fps**, landscape, locked exposure/focus.

**Single-camera fallback** (e.g. the first **start-job** shoot — iPhone only, GoPro not yet mounted): use the
**iPhone as Camera A (the base)** and skip Camera B entirely. In the ffmpeg recipe just drop the two `[2:v]`
Camera-B overlays; the app overlay (full → run-strip) still works unchanged. Re-add Camera B once the GoPro is
in place.

**Mounting caution:** the corner camera must be clamped **rigidly** and **out of the gantry/spindle sweep** —
the table can quiver on deeper cuts, so a loose tripod will show visible shake exactly during the RUN footage
you most want steady. Rubber feet / a mass-loaded base or a bolted corner bracket help; avoid resting it on the
machine frame if the frame is what's vibrating.

---

## 3. The RUN-state marker (app side)

**Purpose:** give the compositor exact timestamps for (a) when to shrink the app overlay and (b) how to align
the app track to the machine track — without eyeballing.

**Design** (mirrors the existing `DebugLog` facility — opt-in flag, `%AppData%\ioSender\`, never throws):
- New flag **`-demomarker`** (and `IOSENDER_DEMOMARKER` env), parsed in `App.OnStartup` next to `-debuglog`.
- Writes **`%AppData%\ioSender\ioSender.demo-markers.csv`**, one row per event:
  ```
  iso_timestamp,event
  2026-07-08T14:32:07.412,SESSION_START
  2026-07-08T14:35:11.008,RUN_START
  2026-07-08T14:41:53.770,RUN_END
  ```
- **Events:**
  - `SESSION_START` — at init (launch).
  - `RUN_START` / `RUN_END` — the single edge-transition in the model, the `IsJobRunning` setter at
    [GrblViewModel.cs:595](../../CNC%20Core/CNC%20Core/GrblViewModel.cs#L595) (`false→true` / `true→false`).
  - `TIMELAPSE_ON` / `TIMELAPSE_OFF` — the in-app Timelapse toggle (below).
- No-op when the flag is off — normal runs write nothing.

**Timelapse toggle (run bar).** With `-demomarker` on, a **Timelapse** toggle appears on the bottom run bar
next to the state cluster (hidden in normal use). Flip it **ON at the instant you switch the camera to
timelapse**, **OFF when you switch back** — the exact window is marked live, so there's no post-hoc guessing of
where to speed up. Fallbacks: if you never flip it, it **auto-ON**s `TimeLapseAutoOnMinutes` (default **5 min**)
after `RUN_START`; `RUN_END` forces it **OFF** if still on. (No feed-based runtime estimate exists in the app,
so the auto-ON delay is a fixed guess matching the hands-on-for-the-first-few-minutes workflow — swap for a real
estimate later.)

> **Status: BUILT** (held, uncommitted): `DemoMarker.cs`, the `IsJobRunning` hook, the `-demomarker`
> flag/env in `App.OnStartup`, and the run-bar toggle in `MainWindow`. Debug & Release build clean.

**Sync model:** `RUN_START` is the universal sync point — it is the exact instant the app fires the job **and**
the instant the machine first moves under Cycle Start, so it's visible in the camera footage too. Align all
three tracks on it (see §5).

---

## 4. Capture procedure (per video)

1. Launch ioSender with the marker on: `ioSender.exe -demomarker` (or set the env var). Maximize the window.
2. Start all three recorders (OBS `app.mp4`, Camera A, Camera B). Order doesn't matter — you'll sync in post.
3. **Clapper for the cameras:** clap once in view of **both** cameras (a sharp visual+audio edge to align
   Camera A↔B). Do this before touching the app.
4. Do the demo: connect / set up / probe as needed, narrating or silent (voiceover can be added later).
5. Press **Cycle Start** to run the job — this emits `RUN_START`. Let it cut.
6. On completion (`RUN_END`), stop all three recorders.
7. Copy `app.mp4`, `camA.mp4`, `camB.mp4`, and `%AppData%\ioSender\ioSender.demo-markers.csv` into a per-video
   folder, e.g. `work/connect/`.

---

## 5. Composite with ffmpeg

Timeline math (do once per video):
- `T_run` = seconds from the **start of `app.mp4`** to the `RUN_START` row. Get it from the app recording's
  own start time vs the marker timestamp (OBS filenames are timestamped; or add a `SESSION_START` clap in-app).
- `Aoff`, `Boff` = offsets that align each camera's clapper frame to `app.mp4`'s timeline (from the §3 clap).
- Measure **once**, at 1920×1080 maximized: the run strip occupies the bottom band — call it `STRIP_H` px tall
  starting at `STRIP_Y`. (Screenshot the maximized window, measure the JobControl/StatusControl band. Typically
  ~120–150 px.) These stay constant across videos as long as capture settings don't change.

**Recipe** (base = Camera A; app full then strip-cropped at `T_run`; Camera B small, larger after `T_run`):

```bash
ffmpeg \
  -i camA.mp4 -itsoffset $Aoff -i app.mp4 -itsoffset $Boff -i camB.mp4 \
  -filter_complex "
    [0:v]scale=1920:1080,setsar=1[base];
    # App, FULL, only before RUN:
    [1:v]scale=760:-1[appfull];
    [base][appfull]overlay=x=W-w-24:y=24:enable='lt(t,$T_run)'[s1];
    # App, run-strip CROP, only during RUN:
    [1:v]crop=iw:${STRIP_H}:0:${STRIP_Y},scale=900:-1[appstrip];
    [s1][appstrip]overlay=x=(W-w)/2:y=H-h-24:enable='gte(t,$T_run)'[s2];
    # Camera B: small before RUN, larger during RUN:
    [2:v]scale=380:-1[bsmall]; [2:v]scale=560:-1[bbig];
    [s2][bsmall]overlay=x=24:y=H-h-24:enable='lt(t,$T_run)'[s3];
    [s3][bbig]overlay=x=24:y=H-h-24:enable='gte(t,$T_run)'[out]
  " -map "[out]" -map 1:a? -c:v libx264 -crf 20 -preset medium -c:a aac out.mp4
```

Tune the `scale`/`overlay x,y` numbers to taste; they're the only per-look knobs. Everything else is mechanical.

> **Follow-up tooling (offered, not yet built):** a `compose-demo.ps1` that reads `ioSender.demo-markers.csv`,
> computes `T_run`, and fills the recipe template automatically — so producing a video is "drop the 4 files in a
> folder, run the script." Add once the marker exists and the recipe is proven on the first real shoot.

---

## 5b. Long toolpaths → timelapse

The prop (a serving platter: **5 toolpaths, 2 tools — ¼" end mill + ¼" ball — some paths ~40 min**) means some
RUN phases are far too long to show real-time. The goal: show the **complete** job but not write GB of footage.

**Where to compress time — at the camera, not in ffmpeg.** ffmpeg *can* speed up footage (`setpts=PTS/N`), and
it does so **faster than real-time** (software `libx264 medium` ~3–8× real-time; hardware NVENC/QSV ~10–20×), so
CPU is never the bottleneck. **But** ffmpeg needs the full-rate file on disk first — so post-hoc speedup does
**not** save storage; the GB is already written. To actually avoid the GB, **timelapse at the camera** during
the long stretch. (The app screen recording stays real-time — a mostly-static UI H.264-compresses to a few
hundred MB even over 40 min, so it's never the storage problem; only the machine camera is.)

**Default pattern — real-time ends, timelapse the middle.** Don't timelapse the whole cut: the opening plunge
and the final passes are the interesting bits. So per long path:
1. **RUN_START → +N min:** real-time (show the job starting, first cuts).
2. **middle:** switch the camera to timelapse (fixed rate) — this is the bulk of the ~40 min.
3. **last ~M min → RUN_END:** switch back to real-time (show the finish / final surface).

`N` and `M` are **guesses from the estimated runtime** (ioSender shows an estimate for the loaded program). Start
with something like N≈2 min, M≈2 min and adjust per path.

**Sync in post:** the `RUN_START`/`RUN_END` markers bound the RUN phase; you hand-mark the two camera
mode-switch instants (they're visible in the footage). For the timelapsed middle, speed the **app run-strip
overlay** by the **same factor** (`setpts=PTS/factor`) so the DRO fast-forwards in lockstep with the camera;
the two real-time ends play at 1×.

**Camera notes:**
- **GoPro:** use a **fixed-rate** Time-Lapse Video / TimeWarp (e.g. 15×/30×) so the factor is known — clean sync.
- **iPhone:** native Time-lapse uses a **variable auto factor** (2×–30× by clip length), which fights precise
  sync. Prefer a fixed-interval capture app, or record the middle real-time and `setpts` it in ffmpeg (accepts
  the GB), or just accept the variable rate and align by eye on the markers.

**Prerequisite:** install **ffmpeg** (`winget install Gyan.FFmpeg` or the gyan.dev build) and put it on `PATH`.

> **Future automation (see §10):** the camera mode-switches at +N / −M are currently a **manual** operator
> action. ioSender already knows `RUN_START` and can estimate runtime, so it could instead **emit
> timelapse-window events** over the LAN and drive the camera automatically — that's the app/remote-control
> question tracked in §10.

## 6. Publish & wire into the manual

1. Upload `out.mp4` to YouTube as **Unlisted**; copy its video id.
2. In [docs/manual/index.html](../manual/index.html) set `data-video="<id>"` on the topic `<section>` (the five
   `data-video="pending"` slots are at lines **553, 842, 882, 933, 968** — connect, start-job, machine-setup,
   probing, tools). The lazy hook injects the iframe on click ([index.html:1381](../manual/index.html#L1381)).
3. Run **`docs/manual/publish-pages.ps1`** to push the updated site (live at
   https://iosenderv2.github.io/ioSender/).
4. Once a few exist, repoint **Help → Video tutorials** from terjeio's playlist to the V2 playlist:
   [MainWindow.xaml.cs:904](../../ioSender%20XL/ioSender%20XL/MainWindow.xaml.cs#L904).

---

## 7. Shot list — the five placeholder topics

| Topic | Slot | What to show | Cuts? |
|---|---|---|---|
| **connect** | novice:3 | Connection dialog → Network default → LAN discovery → connect | no |
| **start-job** | novice:6 | Start Job tab → measure stock + set origin → auto 3D-probe → Cycle Start | yes |
| **machine-setup** | novice:7 | Machine Setup wizard steps → gate → homing | no |
| **probing** | int:5 | Edge finder + tool-length probe from the Probing tabs | brief |
| **tools** | int:7 | Tool table + a Tools op (e.g. Surface Spoilboard / Auto Square) | yes |

Extra task videos worth doing beyond the 5: **Surface Spoilboard**, **Auto Square**, a **tool change**.
"Cuts? = no" videos have no RUN phase, so the app overlay just stays full (skip the `T_run` shrink — set
`T_run` past the end).

---

## 8. Defaults chosen (change freely)
- App overlay 760 px wide, top-right, 24 px margin; run strip 900 px wide, bottom-center.
- Camera B 380 px (setup) → 560 px (run), bottom-left.
- 1080p30 everywhere; H.264 CRF 20. These keep the crop rect stable and files YouTube-ready.

## 9. Build order
1. **Implement the `-demomarker` facility** (§3) — the only blocker. **DONE** (file marker; LAN emitter = §10).
2. Shoot **connect** first (no RUN phase → simplest) to prove capture + the ffmpeg overlay.
3. Shoot **start-job** to prove the `RUN_START` shrink end-to-end.
4. Extract `compose-demo.ps1` from the proven recipe, then batch the rest.

---

## 10. Future automation — remote camera control (feasibility)

Goal: instead of the operator manually switching the camera to timelapse at +N min and back at −M (§5b),
**ioSender drives the camera over the LAN** — it already knows `RUN_START`/`RUN_END` and can estimate runtime,
so it's the natural brain. Two client paths, not mutually exclusive:

**A. ioSender → GoPro directly (least code, if the model supports it).**
GoPro HERO cameras expose the **Open GoPro API** (official): BLE for control + an **HTTP** command API over
WiFi (start/stop record, set preset/mode incl. Time-Lapse/TimeWarp). The catch is the network topology:
classic GoPros make their *own* WiFi AP (you'd have to join it, losing your LAN), but **recent HEROs (≈11/12/13)
support COHN — "Camera On the Home Network"** — the camera joins your home WiFi and is controllable over the LAN
via HTTPS. **If this GoPro supports COHN, ioSender can command it directly with plain HTTP calls — no phone app
at all.** *Action: identify the exact GoPro model and confirm COHN / Open-GoPro HTTP support before designing
this.*

**B. Custom iOS app ↔ ioSender over LAN (needed to automate the iPhone).**
iOS does **not** let arbitrary LAN traffic drive the built-in Camera app, so automating the **iPhone** as the
timelapse cam means a small **custom Swift app** using **AVFoundation**: it records, and — crucially — a
custom capture session can do **fixed-interval timelapse** (solving the native variable-rate gotcha in §5b) and
switch modes mid-capture on a network command. It connects to a lightweight event socket in ioSender (below)
and reacts to `RUN_START` / `TIMELAPSE_ON` / `TIMELAPSE_OFF` / `RUN_END`. Cost: a separate Xcode/Swift project,
an Apple Developer account, and device provisioning — a real but self-contained build.

**The ioSender side (shared by both).** Extend the marker facility from a file-writer to also **emit events on
the LAN**: a small local **TCP line / WebSocket** server (discoverable via mDNS/Bonjour) that pushes the same
events plus a computed **timelapse window** (`TIMELAPSE_ON` at `RUN_START`+N, `TIMELAPSE_OFF` at
`estimatedEnd`−M — runtime estimate already available from the loaded program). Clients: the iOS app (B),
and/or ioSender's own GoPro-HTTP driver (A). This keeps the "when to timelapse" logic in one place — the app
that actually knows the job.

**Recommendation.** Near-term keep it manual (§5b). If/when automating: **GoPro-direct HTTP (A) is the smallest,
most robust win** for the machine cam — do that first and only build the iOS app (B) if the iPhone must be a
hands-free timelapse cam too. Gate both behind confirming the GoPro's model/COHN support.
