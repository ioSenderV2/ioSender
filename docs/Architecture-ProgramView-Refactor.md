# ProgramView refactor — a standalone, streamer‑connectable program view

*Design note. Companion to `Architecture-Registration-Refactor.md` — this removes another shared
collision point (the single program overlay) using the same "own it, don't borrow it" principle.*

## Motivation

Today there is exactly **one** program view in the app: `overlayProgramView`, a `GCodeListControl`
pinned at the bottom of `MainWindow`. Every Generate → Cycle Start tool borrows it through static hooks
on `MacroProcessor`:

- `ProgramPreview(name, text)` → `MainWindow.ShowProgramPreview` — pop the overlay open and show the
  generated text (Generate's feedback).
- `SetActiveProgram(name, text)` → show the tool's program when its tab is entered.
- `ClearActiveProgram()` → `ClearProgramPreview` — put the overlay back the way it was when the tab is
  left (active follows the focused tab).

Because the view is shared, generating in one tool **clobbers** whatever the overlay was showing, and
leaving the tab must **restore** it. That clobber‑and‑restore dance is fragile — it is the source of the
stay‑put finalization hang and the "close the overlay, don't replace it" bug — and it is exactly the kind
of shared‑singleton collision the registration refactor is dissolving. It also special‑cases "the loaded
job" everywhere (`SetProgram(null)` means "revert to `GCode.File`"), which the new model discards.

**Tools on the Generate → Cycle Start paradigm** (all sharing the one overlay today):

| Tool | View / file | Generated program |
|---|---|---|
| Load Stock | `LoadStockView` | pcorner probe program |
| Surface Spoilboard | `SurfaceSpoilboardWizard` | facing program |
| Auto Square | `AutoSquareWizard` | squaring program |
| Stepper Calibration (scratch) | `StepperCalibrationScratchWizard` | scratch program |

(There is no persistent "job view" either: the **Load / Load Folder commands create** a `ProgramView`
hosted in the Job tab, exactly as a wizard's **Generate** creates one — same object, different producer.)

## Goal

Make **`ProgramView` a standalone, reusable object** that owns a G‑code program, renders it, and can be
**explicitly connected to the streamer** to run. **Multiple instances exist independently**, each **created on demand by the command that produces the
G‑code** — Load File, Load Folder, or a wizard's Generate. There is **no special "loaded job" view** and
**no pre‑instantiated root**: the stack starts empty and nothing exists until a command creates it.
The streamer is allocated to a view by an explicit **push/pop** connect API; nothing is implicit.

## The object

`ProgramView` (UserControl, `CNC.Controls`) — wraps the existing `GCodeListControl`:

- **State:** `ObservableCollection<GCodeBlock> Blocks`, `string Title`, `bool IsOpen` (show/hide).
- **Content API:**
  - `SetProgram(IEnumerable<GCodeBlock> blocks)`
  - `SetProgramText(string ngc)` (via the existing `BlocksFromText`)
  - `Clear()`
- **Display:** the inner `GCodeListControl` renders the block list and the live per‑line `ok`/executing
  markers. A "Program" toggle button shows/hides the view (an overlay for Load Stock, likewise for the
  other tools).
- **Streamer API (explicit push/pop — this is the allocation):**
  - `Connect()` — **push** this view onto the streamer: it becomes the active program, the stream points
    at its `Blocks`, and all run state routes here. The previously‑connected view is remembered beneath.
  - `Disconnect()` — **pop**: release this view and restore the previously‑connected one (whatever it
    was). Never reaches into "the loaded job" — it just pops the stack.
  - `bool IsConnected { get; }`

## Streamer connection — an explicit push/pop stack

The streamer holds a **stack** of connected `ProgramView`s. The **top of the stack is active** and is
what streams. The connection is explicit — no implicit default, no special‑cased "job" target.

`Streamer.Connect(view)` — **push**:
- `view` becomes active; the stream points at `view.Blocks` (the *same* block objects the list displays,
  so markers are live — never a copy),
- run/idle/progress and per‑line execution state route to `view`,
- the previously‑active view is pushed down (remembered), not discarded,
- `view` drives Cycle Start enable + the mint source highlight.

`Streamer.Disconnect()` — **pop**:
- discard the top view and restore the one beneath it, whatever it is (another tool's view, or the root),
- **fires on the TRUE terminal only** (`Idle`/`NoFile`) — never at `JobFinished`/`Stop` (see Watch‑outs).

**The stack starts empty** — no view is instantiated or pushed at startup. A view is created and pushed
only by the command that produces its G‑code: **Load File** / **Load Folder** create one hosted in the
Job tab; a wizard's **Generate** creates its own. A loaded‑file view is simply first‑in (so it sits at
the bottom and is what a wizard pops back to), but it is not special — same object, same lifecycle.

**Cycle Start:** streams the **top** of the stack; **empty stack → disabled** (nothing loaded, nothing
to run — the behavior you want, so no artificial root is needed).

Convenience: `ProgramView.Connect()` / `Disconnect()` forward to the streamer; a tool can expose
`Run()` = `Connect()` + request Cycle Start.

## Folding in the existing run path

- `MacroProcessor.ActiveRun` / `ActiveProgramName` / `ActiveProgramChanged` collapse into "the top‑of‑stack
  `ProgramView`." `ActiveRun`'s generate‑and‑run becomes `view.Connect()` + Cycle Start.
- `RunStreamedJobInPlace` streams the top view's blocks; markers already route there, so the hardcoded
  `overlayProgramView.SetProgram(prog.Data)` line goes away.
- The `SetProgram(null) == "the loaded job"` convention is deleted — the root view carries its program
  explicitly like every other instance.
- `IsCycleStartEnabled`, `IsActiveProgramReady`, and the mint source highlight key off *"the stack top"*
  instead of `ActiveRun != null` / "is this the job view."
- Per producer: **Load File / Load Folder** create a view + connect it (hosted in the Job tab). A
  **wizard** creates its view on `Generate` (`view.SetProgram(...)` + `view.IsOpen = true`) and
  connects/disconnects with the tab (`Activate(true)` → push, `Activate(false)` → pop). Tab switches stay
  a balanced push/pop, so the stack normally holds just { loaded‑file view, current wizard }.

## Migration order

1. **Extract `ProgramView`** wrapping `GCodeListControl` + title + toggle; add `SetProgram` /
   `Connect` / `Disconnect` and the streamer's push/pop stack. No behavior change yet — nothing uses it.
2. **Overlay → command‑created instance.** Replace `overlayProgramView` + `ShowProgramPreview` /
   `SetActiveProgram` / `ClearProgramPreview` — have **Load File / Load Folder create** a `ProgramView`
   (hosted in the Job tab) and connect it (stack empty until then); route the streamer through the stack.
   Delete the `null == loaded job` convention. Verify file / folder / job runs + markers unchanged.
3. **Convert the four wizards**, one at a time (start with **Load Stock** — best understood): each gets
   its own `ProgramView` + a Program toggle; `Generate` writes its own view; `Activate` push/pops.
4. **Drop the shared plumbing.** Remove `MacroProcessor.ProgramPreview` / `SetActiveProgram` /
   `ClearActiveProgram` once no caller remains.

Each step is independently shippable and testable.

## Watch‑outs (learned the hard way in the unified‑run work)

- **Teardown timing:** the `Disconnect` pop must fire on the TRUE terminal (`Idle`/`NoFile`), *not*
  `JobFinished`/`Stop`, or a stay‑put run hangs mid‑finalization (the controller's final `Idle` is only
  delivered while a program is active).
- **Run‑bar `isActive`:** the fixed bottom run bar only ticks `isActive` on the Grbl tab; keep the
  relaxed `GrblStateChanged` guard (`isActive || stack not empty`) so Cycle Start works on wizard tabs.
- **Marker identity:** the connected view MUST display the *same* block objects being streamed, or the
  markers scroll but never light.
- **One active at a time:** the stack top is the sole owner of the streamer — pushing view B suspends
  A's run affordance; popping restores it. Two views can't both think they own the stream.
- **Balanced push/pop:** every `Connect` needs its `Disconnect` (tab leave, run terminal) or the stack
  leaks; guard against popping an empty stack.

## Non‑goals

- Not changing the streamer/flow‑control itself (character counting, `StreamPump`), only *what it points
  at*.
- Not adding concurrent streaming — still one program runs at a time; the change is that each program
  view is an independent object on a stack, not that multiple run at once.
