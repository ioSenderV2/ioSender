# Render harness

Standalone in-process renderer for WPF `Window`s that the [UI test server](../../docs/playbooks/drive_ui_test_server.md)
can't reach - chiefly **modal dialogs opened via `ShowDialog()`** from a parent window (the test
server's screenshot endpoint only covers `mainWindow`; a separate top-level `Window` is a different
visual tree). Renders the **real dialog class** against the **real on-disk `AppConfig`** via
`RenderTargetBitmap` - no visible window needed, nothing shown on screen.

**Why this exists:** built 2026-07-18 while fixing `FixtureEditDialog`'s corner schematic, after
guessing at layout fixes from hand-copied XAML mocks went wrong *twice* - the mock silently drifted
from what the real dialog (real control templates, real `AppConfig.Settings.Base.UiScale`, real
`DialogScaling`) actually produced. This renders the truth instead of a guess.

## Usage

```powershell
cd tools\render-harness
dotnet build                                    # also syncs fresh DLLs/en-US/config - see below
dotnet bin\Debug\net462\render-harness.exe FixtureEditDialog
dotnet bin\Debug\net462\render-harness.exe FixtureEditDialog.Vise out.png
```

Run with no args (or an unknown scenario name) to list what's currently registered.

## Adding a new scenario

Add an entry to the `Scenarios.All` dictionary in `Program.cs` - a name and a `Func<Window>` that
constructs the real dialog with whatever minimal real objects it needs (a `Fixture`, a `Probe...`,
etc.). `GrblViewModel` can usually be passed as `null` for a pure layout/rendering check, since
dialog constructors typically only touch it from button-click handlers, not from the constructor
itself - if a specific dialog's constructor *does* dereference it eagerly, you'll need a minimal
real instance instead.

## Gotchas (the reasons this file exists instead of a five-line script)

- **Must be net462, not net8/9/10.** The app's own WPF assemblies are net462 - a modern .NET host
  cannot load net462-compiled WPF types (different `PresentationFramework` binaries entirely). SDK-style
  `<TargetFramework>net462</TargetFramework>` with `<UseWPF>true</UseWPF>` works fine for building/running,
  just don't "modernize" the TFM.
- **`CNC.Core` has its own `enum Action`**, colliding with `System.Action` in any file that does
  `using CNC.Core;` - qualify as `System.Action` explicitly (bit `ObsBridge.cs` the same way, see its
  git history).
- **Call `AppConfig.Settings.LoadConfig("ioSender")` before touching `.Base`** - just referencing the
  `Settings` singleton doesn't load anything; `Base` is null until `LoadConfig` runs (mirrors
  `MainWindow.xaml.cs`'s own `AppConfig.Settings.LoadConfig(Title)` call). Reads the real profile on
  this machine - read-only, this harness never calls `Save()`.
- **The compiled BAML for `CNC.Controls.WPF` lives in the `en-US` *satellite* resource DLL**, not the
  main assembly (`[assembly: NeutralResourcesLanguage(...UltimateResourceFallbackLocation.Satellite)]`).
  The csproj's `SyncAppBinaries` post-build target copies it automatically on every `dotnet build` - if
  you ever see the OLD layout with no error, the satellite went stale; rebuild.
- **Set `Thread.CurrentThread.CurrentUICulture` before creating any WPF object** - otherwise the
  satellite-culture resource probe throws `MissingSatelliteAssemblyException` even for a plain
  invariant-culture run.
- **Set `Application.ShutdownMode = ShutdownMode.OnExplicitShutdown`** - the default
  (`OnLastWindowClose`) tears down the dispatcher the moment the rendered window closes, silently
  truncating anything rendered after it.
- **Pump layout until it settles, not just once** - `LayoutTransform`-driven regrowth (UiScale zoom)
  can take several dispatcher passes; render too early and you get a plausible-looking but wrong
  (smaller/clipped) capture. `Program.cs`'s render loop already does this (10 passes) - don't cut it
  down without re-verifying.
- **The `SyncAppBinaries` target copies from `CNC Controls`'s own `bin\Debug`** - if that's stale
  (you edited XAML/code-behind but haven't rebuilt ioSender), this harness renders the STALE version
  with no warning. Run `.\build.ps1 -Configuration Debug` in the repo root first.
