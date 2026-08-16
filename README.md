# ioSender V2 User Manual — authoring guide

Single-file, self-contained HTML manual (`index.html`) with linked screenshots under `img/`.
No build toolchain — open `index.html` in any browser. Matches the repo's `Overview.html` idiom.

## Why single-file HTML + linked PNGs
- One `index.html` (inline CSS/JS incl. live search, three reading tracks, A–Z subject index).
- Screenshots are **linked** `img/*.png`, *not* base64 — keeps the HTML small/editable and git
  diffs sane. Only downside (emailing one loose file) doesn't apply: this ships bundled in the
  app folder and lives in the repo / GitHub Pages.
- "Online": serve `/docs` via GitHub Pages → manual at `/manual/`. Also bundleable with the app
  for offline in-app deep-linking.
- If a truly portable single file is ever needed, a small script can inline `img/*` → base64 at
  release time (deferred until needed).

## Adding / editing a topic
Each topic is one `<section>`:
```html
<section class="topic" id="ANCHOR" data-title="Human title" data-level="nov|int|mac"
         data-track="novice:N,intermediate:N,machinist:N">
  <h2>Human title <a class="anchor" href="#ANCHOR">#</a></h2>
  <div class="tags"><span class="tag nov">Novice</span></div>
  ...content...
  <div class="xref">Related: <a href="#other">Other</a></div>
</section>
```
- **`id` / ANCHOR** = the deep-link target. It **must equal the view's `HelpTopic`** (see anchor map
  below) so the in-app F1/"?" hook lands on the right page.
- **`data-level`** — `nov`/`int`/`mac`: sets the sidebar dot colour.
- **`data-track`** — per-track ordering. `novice:5` = 5th in the Novice reading path; `0` (or omit)
  = not in that track's guided sequence (still reachable via A–Z + search). The foot-of-page
  Next/Prev walks the active track in this order.
- The A–Z index, live search, scroll-spy and track Next/Prev are all built by JS from these
  attributes — no separate nav to maintain.

## Screenshots
`<figure><img src="img/ANCHOR-thing.png" alt="…"><figcaption>…</figcaption></figure>`.
Name files by the topic anchor (`start-job-panel.png`, `connect-dialog.png`). Until a shot exists,
leave a `<div class="shot-todo">screenshot: …</div>` placeholder so the gap is visible.
Capture at 100% scaling; crop tight; PNG. Keep them optimized (they land in git).

## Task videos (machine-side walkthroughs)
A topic `<section>` with a `data-video` attribute gets a video panel (built by JS, placed after the
screenshot):
- `data-video="pending"` → a "🎬 coming soon" note (currently on connect, start-job, machine-setup,
  probing, tools).
- `data-video="<YouTube id>"` → a **"▶ Watch on the machine"** CTA; the iframe (youtube-nocookie) is
  injected only on click (lazy) so pages stay fast.

To attach a real video: record the task (OBS Studio scene = Display Capture of the app + a camera on the
machine, arranged picture-in-picture → record, no editing), upload **unlisted to YouTube**, then set
`data-video="<id>"` on that topic's section and republish. gh-pages is served over https so the embed
works with no CSP issues. Also repoint the app's Help → Video tutorials at the V2 playlist.

## Reading tracks
Same topic pages, three curated orders:
- **Novice** — first-time path: getting-started → connect → jogging → job → start-job → errors.
- **Intermediate** — machine-setup → job → start-job → probing → tools → offsets → settings →
  sdcard → viewer → errors.
- **Machinist** — depth: machine-setup → settings → tools → probing → offsets → heightmap → lathe → errors.

## In-app deep-link hook (design — full per-view F1/"?")
Attach point: `ICNCView` (`CNC Controls/CNC Controls/ICNCView.cs`) — every tab implements it.
1. Add `string HelpTopic { get; }` to `ICNCView`; each view returns its anchor (table below).
   Views with nothing specific return `""` → opens the manual at the top.
2. `MainWindow`: an `F1` `KeyBinding` (and an optional per-tab "?" button) reads the active view's
   `HelpTopic` and opens `<manual>/index.html#<HelpTopic>`.
3. `<manual>` resolution: bundle `docs/manual/` into the install dir at build/package time and open
   the local `file://…/index.html#topic` (works offline); fall back to the GitHub Pages URL.
4. Repoint the existing **Help** menu (`MainWindow.xaml.cs` ~L885–905) — Wiki / Usage tips / Brief
   tour / Error and alarm codes currently point at `terjeio/ioSender` upstream — at the V2 manual
   anchors (`#getting-started`, `#errors-alarms`, …). `Help → Error and alarm codes` already opens
   the in-app `ErrorsAndAlarms` dialog; the manual's `#errors-alarms` complements it.
5. All new UI strings get an `x:Uid` + a row in each `Locale/<loc>/csv/*.csv` (see CLAUDE.md).

### Anchor map (ViewType → HelpTopic → manual anchor)
| ViewType        | HelpTopic / anchor | Manual section            |
|-----------------|--------------------|---------------------------|
| StartJob        | `start-job`        | Start Job                 |
| GRBL (job)      | `job`              | The Job screen            |
| MachineSetup    | `machine-setup`    | Machine Setup             |
| Tools           | `tools`            | Tools                     |
| Probing         | `probing`          | Probing                   |
| Offsets         | `offsets`          | Work offsets              |
| GRBLConfig      | `settings`         | Settings                  |
| SDCard          | `sdcard`           | SD card jobs              |
| GCodeViewer     | `gcode-viewer`     | 3D viewer                 |
| HeightMap       | `heightmap`        | Height map                |
| LatheWizards    | `lathe`            | Lathe                     |
| (connect dialog)| `connect`          | Connecting                |
| (jog UI)        | `jogging`          | Jogging & the DRO         |
| (errors dialog) | `errors-alarms`    | Errors & alarms           |

## Status
- **Scaffold + 3 seed topics written:** getting-started, connect, start-job (full).
- All other topics are stubbed with correct anchors so deep-linking already resolves.
- **TODO:** flesh stubs; capture screenshots; implement the in-app `HelpTopic`/F1 hook + repoint
  the Help menu; optional PDF/Pages publish.
