# 3D carve view in ioSender — design note (scoping)

Status: **scoping only** — no code yet. Decide approach (below) before building.
Author: 2026-06-28. Context: the user finds the simulator's 3D view far more useful than ioSender's
toolpath viewer and wants it "moved" into ioSender's **3D View** tab, after which the simulator would no
longer need its own viewer window.

## 1. The two things that are not the same

| | Simulator `sim_view` (the thing we like) | ioSender 3D View today (`RenderControl`) |
|---|---|---|
| Tech | Native **Win32 + OpenGL** (immediate mode), C, ~1080 lines, runs on its own thread in the **simulator process** | **WPF + HelixToolkit.Wpf** (`CNC GCodeViewer`), C# |
| Shows | Machine envelope, fixtures (spoilboard / stock / toolsetter puck), live **tool cone**, and **real-time material removal** of the stock | The program's **toolpath as lines** + a tool marker. No stock, no carving. |
| Fed by | `-setup` geometry + **live tool tip each realtime tick** + active **tool geometry** (dia/shape/V-angle) | The loaded program's parsed `GCodeToken`s (static, once at load) |

So "move it in" is **not** a file copy: different language *and* a different rendering model (live
dexel carve vs static toolpath lines). The valuable part is the **real-time material-removal carve**.

## 2. How `sim_view` is fed (the contract to reproduce)

From `src/sim_view.h` / its setters (called from the sim's `driver.c`/`main.c`):
- `sim_view_set_geometry(env_min/max, spoil_z, stock_min/max, puck_min/max, cell_size)` — envelope + fixtures, pushed once when settings + setup are live.
- `sim_view_set_tool(x,y,z)` — **live tool tip in machine coords, every realtime tick**.
- `sim_view_set_tool_geometry(diameter, shape{flat|ball|vbit}, vangle, toolNo)` — the active cutter, used to carve.
- `sim_view_reset_stock()` — uncut block again (before a re-run).
- title / message / log-append — cosmetic.

Fixture/setup source on the sim side = `sim_setup_values_t` (`sim_setup.h`): spoilboard Z, stock corner+size,
toolsetter x/y/height, toolchange x/y, **material-removal cell size**.

### Carve model (the dexel heightmap)
The stock is a grid of Z-heights over its XY footprint. Each tick, cells within the tool radius of the swept
path are lowered to the cutter's bottom profile (flat / ball / V). It's owned entirely by the render thread:
the realtime thread only publishes the tip + cutter geometry (cheap), and the renderer carves the swept
segment per frame and rebuilds a GPU display list a few times/second. This keeps all carving cost off the
realtime loop. A true 3-axis carve; no undercuts (heightmap, not voxels).

## 3. What ioSender already has to feed an equivalent (and the gaps)

- **Live tool position** — ✅ `GrblViewModel` machine position (MPos), updated every status poll. (Poll rate is
  coarser than the sim's per-tick, but fine for a visual carve; interpolate between samples if needed.)
- **Machine envelope** — ✅ `$130/$131/$132` (max travel) via `GrblSettings` / `GrblInfo`.
- **Active tool diameter** — ✅ tool table.
- **Tool shape + V-angle** — ⚠️ **GAP.** ioSender's tool model has no flat/ball/V shape or included angle.
  Options: default everything to flat; or extend the tool table; or infer from the tool (the Scratch/Surface
  tools already know "V-bit"). MVP = flat-only carve.
- **Fixtures (spoilboard Z, stock box, toolsetter)** — ⚠️ **PARTIAL/SCATTERED.** Load Stock *measures* a stock
  box, Surface Spoilboard knows the surfaced area, Machine Setup knows the envelope/probe — but there is no
  single "current setup/fixtures" model the way the sim's `-setup` file is. Bringing the view in cleanly wants
  a small shared **fixtures/setup source** (spoilboard Z + stock box + toolsetter), populated from those tools
  (Load Stock's measured box is the natural primary source). MVP could start with just envelope + a stock box
  entered/!measured.

## 4. Approaches

### A. Port the carve view to WPF/Helix   — *recommended*
Reimplement the dexel carve + fixtures + tool cone as a C# control in `CNC GCodeViewer`, fed by
`GrblViewModel` position + a small feed API mirroring §2. Register it as the `Toolpath3D` component (the
2b-step-4 factory just swaps `RenderControl` → the new control, or the new control gains a "carve" mode).
- **Pros:** works for **any** controller (not just the sim), no native deps, integrates as the 3D tab via the
  registry, single language/build, testable.
- **Cons:** largest effort. Carve perf in WPF/Helix needs care — represent the heightmap as a
  `MeshGeometry3D` grid and rebuild it **throttled** (a few times/sec), at a sane dexel resolution; naive
  per-tick mesh rebuilds will stutter. The tool-shape + fixtures gaps (§3) must be filled.

### B. Embed the native viewer via `HwndHost`
Compile `sim_view.c` into a small native DLL and host its OpenGL `HWND` inside the 3D tab through `HwndHost`,
fed from ioSender via P/Invoke.
- **Pros:** reuses the proven renderer as-is.
- **Cons:** C-in-a-C#-app build, P/Invoke feed, **Windows-only**, classic WPF/HwndHost *airspace* issues
  (the native child won't compose with WPF overlays/the program-view overlay), separate GL thread lifetime
  and repaint quirks. Brittle; least aligned with the registration/WPF architecture.

## 5. The "move" on the simulator side
Once ioSender renders the carve from the live position, the simulator's `-view` is redundant: drop/disable it
(run the sim headless; ioSender shows the cut). `sim_view.c` + the dexel code can stay as a fallback or be
removed from the sim later. Net: one viewer (ioSender), fed for **any** controller, not just the sim.

## 6. Recommendation + phased plan (approach A)
Build it as its **own focused project** (not folded into other todos), phased so each phase is usable:
1. **MVP** — envelope box + a static stock block + live **tool cone** following MPos (no carve). Proves the
   feed (GrblViewModel → view) and camera. Small.
2. **Carve** — dexel heightmap + flat-tool material removal driven by the swept tip; throttled mesh rebuild;
   Reset-stock. The core value.
3. **Fixtures + tool shapes** — shared fixtures/setup source (spoilboard/stock/toolsetter from Load Stock etc.);
   ball/V profiles once the tool model carries shape.
4. **Retire the sim `-view`** once 1–3 are validated.

**Effort:** large (multi-session); **risk:** medium — carve performance in Helix and the fixtures/tool-shape
data gaps are the unknowns. Phase 1 is cheap and de-risks the feed/camera; decide on phase 2 after seeing it.

## 7. Open questions for the user
- Tool **shape** source — flat-only to start (simplest), or extend the tool table with shape/V-angle now?
- **Fixtures**: start from Load Stock's measured stock box only, or build the shared setup model up front?
- Keep the simulator `-view` as a fallback, or remove it once ioSender's view lands?
- Carve **resolution** default (sim default cell size vs a coarser default for WPF perf)?
