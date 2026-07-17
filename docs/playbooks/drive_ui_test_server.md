# Drive the UI via the test server

**When:** verifying a UI change end-to-end without a human clicking through it, or without a
physical/interactive desktop session available (screen-scrape screenshots are unreliable in some
harness environments — this route captures/drives the app in-process instead).
**Script:** `tools\run-iosender.ps1` to launch; then plain HTTP against the in-process server.
**Package:** `WpfUiTestServer` (NuGet, `stevenrwood/WpfUiTestServer`) — README at
`~/.nuget/packages/wpfuitestserver/<ver>/README.md` is the full API reference; this is the
ioSender-specific invocation + gotchas on top of it.
**Memory context:** `iosender-uitestserver-selector-gap.md` (known limitation), `gh-cli-setup.md`
(unrelated tool, same "wrapper scripts over raw commands" pattern).

## 1. Launch with the server enabled

```powershell
.\tools\run-iosender.ps1 -TestServer -Headless
# or an explicit port:
.\tools\run-iosender.ps1 -TestServer -Port 8761 -Headless
```

- `-TestServer` passes `-testserver` to ioSender.exe (default port **8760** if you don't `-Port`).
- `-Headless` sets `IOSENDER_HEADLESS=1` so a crash dumps to the log instead of blocking on a modal —
  use it for unattended runs; omit if you want to watch it interactively too.
- This kills any running `ioSender`/`AppLaunch` first and polls up to 5s for the new PID. It does
  **not** rebuild — run `.\build.ps1` first if you have source changes.
- A "UNDER TEST-SERVER CONTROL" banner docks across the top of the window while it's live, so anyone
  watching the physical machine knows automated input is in play.

Confirm it's up:
```bash
curl -s http://localhost:8760/ping
# {"ok":true,"server":"ioSender UiTestServer","port":8760}
```

## 2. Discover what you can address

```bash
curl -s http://localhost:8760/uids          # every realized x:Uid, with type + count
curl -s http://localhost:8760/tree          # full state of every realized element
curl -s "http://localhost:8760/state/btnConnect"   # one element
```

**Gotcha — realized elements only.** A control on a tab that hasn't been shown yet doesn't exist in
the visual tree and won't appear in `/uids`/`/tree` until you `POST /invoke/{tabUid}` to select that
tab first. The static catalog of every *declared* `x:Uid` lives in the XAML/locale CSVs, not at
runtime — `/uids` only shows what's currently built.

## 3. Drive it

```bash
curl -s -X POST "http://localhost:8760/invoke/btnConnect"          # button/tab/list-item
curl -s -X POST "http://localhost:8760/set/txtFeedRate?value=500"  # text/checkbox/range
curl -s -X POST "http://localhost:8760/select/cbxProbeType?text=Touch%20plate"
curl -s -X POST "http://localhost:8760/key/Escape"                 # plain keys only, no Ctrl/Shift/Alt
curl -s -X POST "http://localhost:8760/menu/mnuFile"                # open + list a context menu
```

**Known gap:** `DataGrid` rows and `ComboBox` items that haven't been scrolled/dropped-open aren't
walkable `AutomationPeer`s — `/select` bypasses that by setting `Selector.SelectedIndex`/`SelectedItem`
directly, but it only covers `Selector`-derived controls. See `iosender-uitestserver-selector-gap.md`
for the one case that's still awkward (deferred, not yet fixed).

## 4. Message boxes (AppDialogs.Show / AppMessageBox)

Every `AppDialogs.Show` call (146+ call sites) is answerable instead of blocking on a modal:

```bash
curl -s -X POST "http://localhost:8760/dialog/arm?answer=Yes"        # pre-answer the next prompt once
curl -s -X POST "http://localhost:8760/dialog/arm?standing=No"       # answer EVERY prompt until cleared
curl -s -X POST "http://localhost:8760/dialog/arm?capture=true"      # intercept with no preset -> pending
curl -s http://localhost:8760/dialogs                                 # pending + recent (readback of what was shown)
curl -s -X POST "http://localhost:8760/dialog?answer=OK"             # answer the oldest pending prompt
curl -s -X POST "http://localhost:8760/dialog/arm?clear=true"        # clear armed/standing/capture
```

If nothing is armed and capture is off, the prompt falls through to the real box (as of 2026-07-16,
that's `CNC.Controls.AppMessageBox` — the app's own UiScale-aware box, not the native
`System.Windows.MessageBox`). A captured-but-unanswered prompt resolves to the safe default after
its timeout.

## 5. Sync before asserting

```bash
curl -s "http://localhost:8760/idle"                                  # Dispatcher-drained + one frame rendered
curl -s "http://localhost:8760/waitfor?status=connected&equals=true&timeout=8000"
curl -s "http://localhost:8760/waitfor?uid=btnConnect&enabled=true"
```

`/idle` doesn't know about async I/O (a connection, a running job) — use `/waitfor` on a `/status`
field for those. Domain state not exposed by any one control (connection state, controller mode,
job progress) comes from `IUiTestStatusProvider` and shows up under `/status`.

## 6. See it (without a physical screen)

```bash
curl -s http://localhost:8760/screenshot -o window.png                # whole mainWindow, PNG
curl -s "http://localhost:8760/screenshot/btnConnect" -o btn.png      # one element's bounds
```

This is an in-process render capture (like `RenderTargetBitmap`), not a screen-scrape — it works
even when the physical desktop isn't showing the window (headless/non-interactive session, remote
box, whatever). **It only covers windows the server was started against (mainWindow)** — a separate
top-level `Window` (e.g. a modal dialog opened via `ShowDialog`) is a different visual tree and isn't
reachable this way. For those, use the dialog broker (§4) to read back *what* was shown/answered
rather than a pixel capture, or drive/verify the owning control before the dialog opens.

## 7. Hand off to a human

```bash
curl -s -X POST localhost:8760/handoff --data-raw "Jog to the corner and touch off Z manually, then run the probe from the Probing tab."
```

Shows a non-modal popup with the instructions, drops the banner, and **stops the server** — this is a
real stop, not a pause. To automate again in the same running process you'd need the app to call
`UiTestServer.Start(...)` again (it doesn't currently expose a way to trigger that from outside).

## Gotchas hit in practice (2026-07-16)

- **Bringing the real app window to the foreground / physical screen-capturing it does not reliably
  work in this harness environment** (SetForegroundWindow succeeds but the screenshot still shows
  whatever was already on top — looks like a non-interactive window-station issue). Don't burn time
  on `user32.dll` P/Invoke window-activation tricks; go straight to `/screenshot` (§6) or, for content
  that isn't the mainWindow (e.g. a modal dialog), a small standalone in-process render harness (below).
- **`Start-Process` + physical mouse/keyboard simulation is invasive** — it moves the user's real
  cursor. Don't do this without explicit confirmation; the test-server HTTP API exists precisely so
  you never need to.

## Standalone in-process render (for content the test server can't reach)

For a WPF `Window` that isn't the mainWindow (e.g. verifying `AppMessageBox` scaling in isolation),
a throwaway console harness referencing the built `CNC.Core.dll`/`CNC.Controls.WPF.dll` and rendering
via `RenderTargetBitmap` works, but has real gotchas — expect to spend time on these if you build one:

- **Copy the whole `bin\Debug` folder's DLLs next to the harness**, not just the two you reference —
  transitive deps (RP.Math, websocket-sharp, etc.) need to resolve too.
- **Copy `ioSender.exe.config` → `<harness>.exe.config`.** Without it, `Properties.Settings.Default`
  has no schema and `AppConfig.Settings.Base` throws/stays null.
- **Call `AppConfig.Settings.LoadConfig("name")` before touching `.Base`** — just accessing the
  `Settings` singleton doesn't load anything; `Base` is null until `LoadConfig` runs (mirrors
  `MainWindow.xaml.cs`'s `AppConfig.Settings.LoadConfig(Title)` call). This reads the real on-disk
  config (read-only, since the harness never calls `Save()`) — safe.
- **The compiled BAML for `CNC.Controls.WPF` lives in the `en-US` *satellite* resource DLL**
  (`en-US\CNC.Controls.WPF.resources.dll`), not the main assembly — this project has
  `[assembly: NeutralResourcesLanguage("en-US", UltimateResourceFallbackLocation.Satellite)]`. Copy
  that `en-US` subfolder too, **and re-copy it after every rebuild** — a stale satellite DLL silently
  keeps showing the *old* XAML with no error, which reads exactly like a real layout bug (this cost
  real time: a `Window MaxWidth` removal appeared to do nothing until the satellite was refreshed).
- **Set `Thread.CurrentThread.CurrentUICulture` before creating any WPF object** — otherwise the
  satellite-culture resource probe throws `MissingSatelliteAssemblyException` even for a plain
  invariant-culture run.
- **Set `Application.ShutdownMode = ShutdownMode.OnExplicitShutdown`** before showing/closing windows
  — the default (`OnLastWindowClose`) tears down the dispatcher the moment your first test window
  closes, silently truncating any window you render after it (0×0 output, no exception).
- **Pump layout until the size stabilizes, not just N fixed iterations** — `LayoutTransform`-driven
  `SizeToContent` regrowth (e.g. a UiScale zoom) can take several dispatcher passes to settle; render
  too early and you get a plausible-looking but wrong (smaller/clipped) capture.
