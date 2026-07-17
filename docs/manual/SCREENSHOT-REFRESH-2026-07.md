# Manual screenshot/text refresh — 2026-07 session notes

This documents work done against `SCREENSHOT-AUDIT.md` (text fixes + stale
screenshot reshoots for `docs/manual/index.html`) and what's left for a
follow-up session.

## Done

### Text fixes (all applied to `docs/manual/index.html`)
- `#connect`: "File → Connect…" → "the **Connect…** menu"
- `#job`: "File → Load" / "File → Load Folder" → "Load File..." / "Load
  Folder..." button descriptions
- `#job`: "File → Transform" → "Right-click → Transform"
- `#machine-setup`: step table updated from six to eight steps (added
  "6 · Fixture definitions", renumbered "Controller macros" to 7, added
  "8 · Build simulator")
- `#settings`: added the 8th "Simulator" settings tab row
- Optional/nice-to-have additions: Console double-click pop-out note, Camera
  opt-in note
- Swept the rest of the file for other stale "File →"/"file menu" references
  (none found beyond the ones above)

### Screenshots reshot (10 of 11), all under `docs/manual/img/`, current chrome
(`Reconnect… Camera Help` menu bar, no toolbar icon row):

- `machine-setup-overview.png`
- `job-runscreen.png` (loaded `macros/spoilboard_surface.nc` via `--loadfile`
  so the run screen shows a real program)
- `gcode-viewer.png` (3D View sub-tab)
- `start-job-panel.png`
- `settings-grbl.png`
- `probing-tabs.png`
- `tools-tab.png` (Surface Spoilboard sub-tab)
- `offsets-table.png`
- `sdcard.png`
- `heightmap.png`

`connect-dialog.png` and `errors-dialog.png` were left as-is per the task
(not stale).

### Source changes made to enable test-server automation

Some newer tabs are built dynamically in code and had no `x:Uid`, so the
`WpfUiTestServer` couldn't select them (`x:Uid` is a markup-only directive).
Added a `Uid = "tab_" + node.Component` (matching the existing convention
used for other dynamic tabs, e.g. `MainWindow.xaml.cs`'s `BuildTabs()`) in:

- `ioSender XL/ioSender XL/JobWorkspace.xaml.cs` (`BuildCenter()`) — makes
  `tab_Program` / `tab_Toolpath3D` / `tab_Console` addressable.
- `CNC Controls/CNC Controls/ToolsView.xaml.cs` (`BuildTools()`) — makes
  `tab_SurfaceSpoilboard` etc. addressable.

Both are minimal, additive, and follow the existing pattern already used
elsewhere in the codebase — kept since they were needed to drive the
screenshots and have no behavioral effect otherwise.

## Not done: `lathe-wizard.png`

Blocked. The Lathe Wizards top-level tab is gated by
`AppConfig.Settings.Base.Lathe.LatheEnabled`, which is **no longer a manual
toggle** — `MainWindow.xaml.cs` (`CompleteStartup`) forces it to match
`GrblViewModel.LatheModeEnabled`, which in turn is set purely from parsing
the connected controller's `$I` NEWOPT response (`Grbl.cs`, looks for a
`LATHE` flag). Manually editing the persisted `App.config` (`LatheEnabled` +
adding a `LatheWizards` layout node) gets silently reverted the moment the
app connects to a controller that doesn't report lathe support — which the
bundled simulator does not.

To get this screenshot, one of the following is needed in a follow-up
session:
- Configure/patch the simulator (or a real controller) to report the
  `LATHE` NEWOPT capability flag, then let the app's normal sync pick it up
  (one restart), or
- Temporarily stub `GrblInfo.LatheModeEnabled` / the NEWOPT parse for a
  single screenshot session, or
- Accept a manual mid-session UI screenshot taken by a human (outside test-
  server automation) with lathe firmware settings genuinely enabled.

The old `lathe-wizard.png` in `docs/manual/img/` was left untouched.

## Bug discovered (not fixed, out of scope for this task): tab overflow

`StretchTabControl` (`CNC Controls/CNC Controls/StretchTabControl.cs`)
stretches tab headers to fill the strip width when there's room, but when
the tabs' combined natural minimum width exceeds the available strip width
it just falls back to natural widths with **no horizontal-scroll/overflow
affordance** — tabs that don't fit are simply not rendered in the header
row (though still selectable/functional programmatically, e.g. via the test
server, and their content still displays fine once selected).

This is reproducible today, unrelated to screenshot capture, and confirmed
in two places that each recently grew by one tab:

- **Machine Setup wizard** (`MachineSetupWizard.xaml`): 9 header items
  (Overview + 8 steps) — "8 · Build simulator" doesn't render in the header.
  See `machine-setup-overview.png`.
- **Settings** (`GrblConfigView.xaml`): 8 tabs — "Simulator" doesn't render
  in the header. See `settings-grbl.png`.

Both screenshots above show the actual, current state of the app (not
touched up), so the manual is accurate — but this is worth a real fix
(add overflow scrolling/chevrons to `StretchTabControl`) in a follow-up
session, since it will keep recurring as more tabs are added.

## Also noticed (separate, minor, not fixed): stale in-app Overview text

The Machine Setup wizard's own **Overview** tab content (in the running
app, not the manual) lists only 6 steps in its description text and doesn't
mention "Fixture definitions" or "Build simulator" at all, even though the
step tabs themselves now go up to 8. This is a separate small bug in the
app's bundled help text, independent of the manual — flagging it here
since it was noticed while reshooting `machine-setup-overview.png`.

## Verification status

- All 10 completed screenshots were spot-checked for correct chrome
  (no old toolbar row, `Reconnect… Camera Help` menu) either at capture time
  or via a final visual re-check.
- Not yet done in this session: opening `docs/manual/index.html` in an
  actual browser to click through and confirm every image loads and all
  text reads correctly end-to-end. Recommended as the first step of a
  follow-up session before considering this fully closed out.

## Environment note (not a code issue)

This session ran in a shared `%AppData%\ioSender` / shared `ioSender.exe`
process-name environment alongside other concurrent sessions. A couple of
unexpected app exits during this session were initially suspected to be
caused by sibling sessions killing the shared process by name; they were
just relaunched and work continued. (The App.config-revert issue above
turned out to have a real, different, in-app cause — see the Lathe section
— not a sibling collision.)
