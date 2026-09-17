# Peek — step away from a paused job and come back

**Status: SPEC. Nothing built yet.** Written 2026-09-17, **revised the same day** after the operator
settled the three open questions. Code references are at `91312a8b`.

Everything marked **VERIFIED** was read out of the source named beside it — grblHAL core at
`c:\github\iMXRT1062\grblHAL_Teensy4\src\grbl`, or this repo. Everything else is design. The
distinction matters here more than usual: this feature moves a spindle while a job is half-finished.

---

## 0. What changed in the revision, and why it matters

The first draft paused **mid-block** with a feed hold, which forced a soft reset to regain control of
the machine, which in turn forced reconstructing the entire modal frame from `$G` before the reset
destroyed it.

The operator's answer — **pause only at block boundaries, a slight delay is fine** — removes all of
that:

> Stop dispatching lines. Let the controller drain its own planner and report `Idle`. Nothing was
> interrupted, so nothing is at risk, **no reset is needed, and the controller's parser state is
> never lost.**

The frame to capture shrinks from "everything" to "the two things the park itself disturbs". The
most dangerous step in the original design is simply gone.

The cost is the delay: Peek takes effect at the end of the block in flight plus whatever is already
in the planner. Usually well under a second; on a single long cutting move it is that move. Feed
Hold remains available as the immediate stop, and is unchanged.

**This is the tool-change pause without the tool change** — and §7.1 is the one place that analogy
breaks in a way that can hurt someone.

### Settled

| Question | Answer |
|---|---|
| Park where? | **`G30`.** Reuse `MacroRunner.EmitGotoG30`. |
| Spindle while parked? | **Off.** Restored and given time to spin up before the job resumes. |
| Interrupt mid-block? | **No — block boundaries only.** The delay is acceptable. |

---

## 1. What it is

Mid-job, the operator wants to *look* — at the cut, at the chips, at whether the tab is holding.
Today that means Stop and lose the run, or Feed Hold and peer past the gantry.

**Peek**: pause at the next block boundary, park at `G30` with the spindle off, look, then Cycle
Start — spindle back on, back to where it was, job carries on.

---

## 2. Why the mid-block version was abandoned

Kept because it explains why Peek is shaped the way it is, and because someone will propose the
"obvious" version again.

| # | Fact | Source |
|---|---|---|
| 1 | **Jogging is refused during Hold.** The jog handler gates on `state == STATE_IDLE \|\| (state & (STATE_JOG\|STATE_TOOL_CHANGE))`. `STATE_HOLD` is absent → `Status_IdleError` (error:8). | **VERIFIED** — `system.c`, `jog()` |
| 2 | **G-code sent during Hold queues, it does not execute.** It is planned *behind* the interrupted block, so Cycle Start finishes the cut first and *then* drives to the park. | **VERIFIED** — planner is only drained by the cycle |
| 3 | **grblHAL's own parking (`$41`) is not this.** One axis (Z), positive retract only, requires homing, and its own config comment excludes `DEFAULT_HOMING_FORCE_SET_ORIGIN`. Door-driven. | **VERIFIED** — `config.h` |
| 4 | Mid-block recovery therefore needs a soft reset, which is safe **only** from a *completed* hold: `sys.position_lost = st_is_stepping()`. | **VERIFIED** — `protocol.c` |

All four still hold. They are simply no longer on Peek's path.

---

## 3. The mechanism

**The job is never interrupted; it is starved.**

`StreamingSendFile` already ignores a mid-stream `Idle`:

```csharp
case StreamingState.Idle:
    if (streamingState == StreamingState.Error) { … }
    else
        changed = false; // ignore
```

**VERIFIED** — `JobRunner.cs:1504`. A job ends on `JobFinished`, which the **pump** raises when it
reaches the end of the program — not on the controller going quiet. So a controller that drains to
`Idle` mid-program does not tear the job down. That is the property the whole feature rests on.

And with the machine genuinely `Idle`:

- ordinary g-code executes immediately — no queueing behind anything;
- jogging works (`CanJog` already includes `Idle` — **VERIFIED**, `JobRunner.cs:483`);
- the DRO and MDI behave normally;
- **the controller's parser state is untouched** — WCS, tool-length offset, feed rate, units, plane
  and distance mode are all exactly as the program left them.

---

## 4. Sequence

| Step | Action | Gate / why |
|---|---|---|
| **P1** | `peekState = Requested`. **Suspend the idle-kick watchdog** (§7.1). Stop dispatching new lines. | Offered only while streaming — same condition as `runner.CanStop`. |
| **P2** | Let the controller finish what it already has. Wait for `Idle` in a status report. | This is the "slight delay". Nothing is interrupted; the machine stops where a block ends. |
| **P3** | Capture the **resume frame** (§5) — two items, from `$G`. | `$G` answers here trivially (the machine is Idle), and nothing is destroying it. Kept as an explicit step because the park is about to change both items. |
| **P4** | Emit `M5`, then `MacroRunner.EmitGotoG30`. | Spindle off per the decision. Park is the existing emitter — **do not write a second one** (#339). |
| **P5** | Wait for `Idle` again. `peekState = Parked`. Run strip shows **Resume**. | The operator now has a fully normal idle machine: jog, look, MDI, whatever. |
| **P6** | Operator presses **Cycle Start / Resume**. Emit the return: `G53 G0 Z0` → `G53 G0 X… Y…` (the captured position) → spindle back on → **dwell** → `G53 G0 … Z…` → restore motion mode. | Z clear, then XY, then spindle, then plunge — the order `tool_change.c`'s `restore()` uses, for the same reasons. |
| **P7** | `peekState = None`, re-arm the idle-kick watchdog, resume dispatch. | The stream continues at the next un-dispatched line. No `Run(fromBlock)`, no re-parse, no prolog. |

### Why there is no `Run(fromBlock)` any more

The first draft resumed through the run-from-block path. That is no longer needed and should **not**
be used: nothing was torn down, `job.CurrBlock` and the pump's `sendIdx` are still valid, and
re-entering `Run` would re-evaluate `PREREQ`, re-log a `Program start`, and re-run the
generate-first branch logic. Peek resumes by *un-suspending what it suspended*.

This also means §5 of the first draft — the `DefaultProlog` gap — no longer blocks Peek. It is still
a real defect and is recorded in §9.

---

## 5. The resume frame — two items

Because the machine is never reset, only what the **park itself** disturbs must be restored.

| Restore | Why | From |
|---|---|---|
| **Spindle** `M3`/`M4` + `S` | We turn it off at P4 by decision. | `GrblParserState.SpindleState`, and `S` from `$G` |
| **Motion mode** `G0`/`G1`/`G2`/`G3` | `EmitGotoG30` ends in `G0`, and `G0` is **modal**. If the program's next line is a bare `X… Y…` that relied on a live `G1`, it would **rapid into the cut**. | `GrblParserState.MotionMode` |

Everything else is deliberately untouched, and the park must keep it that way:

- `EmitGotoG30` emits only `G53 G0` lines — `G53` is non-modal, and no `F` word appears, so feed rate
  survives. **VERIFIED** — `MacroRunner.cs:126`.
- Do **not** emit `G90`, `G21` or `G17` "to be safe". Each would clobber live modal state that is
  currently correct. This is the opposite of the mid-block design's needs.
- **`G92` is never touched.** A live `G92` is still live; nothing resets it and nothing should
  replay it.
- Coolant is left running. It was not turned off, so it does not need restoring — and a coolant
  restart has its own delay nobody asked for.

---

## 6. Where it hooks in

| Piece | Where | What |
|---|---|---|
| State | `JobRunner` | `PeekState { None, Requested, Parked, Returning }`, beside the existing `pendingOffsetClear` field. |
| Waits for `Idle` | `JobRunner`, off `GrblStateChanged` | **Copy `FlushPendingOffsetClear`'s shape** (`JobRunner.cs:1128`) — it exists for this same class of race and is proven. |
| Dispatch suspend | `pump.Suspended` | Already used by the tool-change path and proven there. |
| **Idle-kick guard** | `JobRunner.OnIdleKick` | **§7.1 — the one genuinely new safety requirement.** |
| Park / return | `MacroRunner.EmitGotoG30` + a small return emitter | Return **names X and Y** (§7.2). |
| Button | `JobControl.xaml` beside Feed Hold / Stop | New `CanPeek` → `IsPeekEnabled` + visibility, mirroring `CanFeedHold`/`CanStop`. **Collapsed** when meaningless, per #355. Needs an `x:Uid` + a row in all 7 locale CSVs. |
| Key binding | `ActionKeyBinder`, **Program** group | Beside MDI and Status (#254) — that group exists precisely because its members must stay live *during a run*. Unbound by default. |
| **Not** a menu item | — | The menu bar is disabled while a job streams (#307 relies on this), so a menu entry would be unreachable exactly when Peek is wanted. |
| Record | `model.LogDetail` | §8. |

---

## 7. Traps

### 7.1 🔴 The idle-kick watchdog will restart the job under you

**The one hazard the block-boundary design introduces, and it is not obvious.**

`JobRunner.OnIdleKick` nudges a pump that appears stalled:

```csharp
if (pumpActive && grblState.State == GrblStates.Idle)
    pump?.KickIdle();
```

and the pump's handler drops stale accounting, clears barriers and calls `SendNext()`:

```csharp
pacer.ResetAccounting();
probePending = false;
SendNext();
```

**VERIFIED** — `JobRunner.cs:501`, `StreamPump.cs:663`.

`pump.Suspended` does **not** stop this. `Suspended` is checked only in `OnReplyClassified`, which
gates incoming *replies*; `KickIdle` arrives via `pacer.Post()`, which has no `Suspended` check at
all. **VERIFIED** — `WirePacer.cs:198,207`.

A parked machine is `pumpActive` **and** `Idle` — exactly the watchdog's trigger condition. Left
alone, Peek would park the machine, the operator would lean in to look, and the watchdog would
resume the program.

**Why the tool-change pause is not exposed:** the controller reports `Tool`, not `Idle`, so the
guard's `== GrblStates.Idle` is false and the kick never fires. That is luck, not design, and Peek
does not inherit it.

**Fix:** guard the kick on the peek state —
`if (pumpActive && grblState.State == GrblStates.Idle && peekState == PeekState.None)`.
Local, and at the one place that decides. Do **not** "fix" it by adding a `Suspended` check to
`WirePacer.Post` — that path also carries abort and barrier signals, and suppressing those is how a
stop gets swallowed.

### 7.2 🔴 A `G53` Z-only lift is not a safe lift

If a rotation write has touched the active WCS, the firmware's parser holds a corrupted position and
the next move leaving an axis **unnamed** flies to it — 662 mm across a table, observed (#358,
#359). `EmitGotoG30`'s first line is exactly such a move (`G53 G0 Z0`).

In Peek's sequence no rotation write precedes the park, so it is safe — but it is safe *by
circumstance*. The **return** move is ours to write and must name X and Y explicitly. Do not
copy the lift's shape.

### 7.3 `EmitGotoG30` needs expression support

It emits `G53 G0 X[#5181] Y[#5182] Z[#5183]`. A controller not reporting `EXPR` cannot run it.
Gate `CanPeek` on `GrblInfo.ExpressionsSupported`, or Peek fails at the moment it parks.

### 7.4 An unanswered `$G` is unknown, not absent (#243)

If `$G` does not answer at P3, **do not park**. Abort the peek with the job still streaming and say
so. Parking on a default frame would resume with the wrong motion mode.

### 7.5 The spindle must reach speed before the cut resumes

The return needs a dwell between `M3 S…` and the final plunge. `tool_change.c` uses
`settings.spindle.on_delay`; we have no equivalent, so it is ours to choose and to state.

---

## 8. Honesty about what it is

Unlike the first draft, Peek no longer splits the run in two — it is one run with a gap in it. So
the log should say that, rather than nothing:

- `Peek - paused at line N, parked at G30` and `Peek - resumed at line N`, through `model.LogDetail`.
- No `[RESUMED]` qualifier is needed on `Program start`, because there is no second `Program start`.

---

## 9. Refusals

Peek declines, with a reason, when:

- nothing is streaming (it is not a jog helper);
- the program is an **SD-card job** — the controller streams those itself, so the sender cannot
  starve it. Same visibility rule that already excludes dry run;
- the controller does not report `EXPR` (§7.3);
- `G30` is unset or outside the soft-limit envelope — the stored-position check already knows how to
  say this;
- `$G` did not answer (§7.4).

---

## 10. Carried over — a real defect Peek no longer depends on

Recorded so it is not lost with the redesign.

`GCodeJob.DefaultProlog` is `{ "G90 G94", "G17", "G21" }` — **VERIFIED**, `GCodeJob.cs:292`. It
restores no WCS, no spindle, no coolant, no tool-length offset and no feed rate, and **only one of
the two mid-program start paths sends it**: `StartSection` enqueues it, `StartHere_Click` does not
(**VERIFIED** — `GCodeListControl.xaml.cs`). Start-from-a-line therefore resumes on whatever modal
state happens to be live.

Separately, `GrblParserState.IsPositionOffset` is inverted — it returns true when the value **is**
zero (**VERIFIED**, `Grbl.cs`). Reached only via the vanilla-Grbl workaround, so grblHAL is
unaffected, but it would misreport tool-length offset on a plain-Grbl machine.

Neither blocks Peek now. Both are worth their own fix.

---

## 11. First hardware test

On scrap, spindle **off** for the first four:

1. Peek with nothing running → refused with a reason.
2. Peek during an air-cut program → confirm it pauses at a block *boundary*, parks at `G30`, and the
   DRO matches the captured position on return.
3. **Leave it parked for two minutes.** This is the test for §7.1 — the watchdog must not restart
   the job. Watch the wire log for any line going out while parked.
4. Peek during a long single move → confirm the delay is the rest of that move and nothing jerks.
5. Peek with the program in `G1` and a following bare `X… Y…` line → confirm the return restores
   `G1` and the next move is a **feed**, not a rapid. This is §5's second row, and the one that
   would otherwise plough.
6. Only then, spindle on, in wood.
