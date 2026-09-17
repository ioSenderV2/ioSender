# Peek — step away from a paused job and come back

**Status: SPEC ONLY. Nothing here is built.** Written 2026-09-17 against the code at `c42c7065`.

Everything marked **VERIFIED** was read out of the source named beside it — grblHAL core at
`c:\github\iMXRT1062\grblHAL_Teensy4\src\grbl`, or this repo. Everything else is design and is
labelled as such. The distinction matters here more than usual: this feature moves a spindle while a
job is half-finished.

---

## 1. What it is

Mid-job, the operator wants to *look* — at the cut, at the chips, at whether the tab is holding.
Today that means Stop and lose the run, or Feed Hold and peer past the gantry.

**Peek**: pause, stand the machine somewhere you can see the work, look, then Cycle Start and the
job carries on from where it stopped.

---

## 2. Why the obvious implementation cannot work

The natural reading is "feed hold, then send a park move, then resume". Both halves of that are
blocked in firmware.

| # | Fact | Source |
|---|---|---|
| 1 | **Jogging is refused during Hold.** The jog handler gates on `state == STATE_IDLE \|\| (state & (STATE_JOG\|STATE_TOOL_CHANGE))`. `STATE_HOLD` is absent, so `$J=` returns `Status_IdleError` (error:8). | **VERIFIED** — `system.c`, `jog()` |
| 2 | **G-code sent during Hold does not execute — it queues.** A line arriving in Hold is planned *behind* the interrupted block. On Cycle Start the machine finishes the cut it was in the middle of and *then* drives to the park. That is not a peek, it is a surprise. | **VERIFIED** — the planner is only drained by the cycle; `protocol.c` reloads the step segment buffer for `STATE_CYCLE\|STATE_HOLD\|…` but never starts new motion in Hold |
| 3 | **grblHAL's own parking (`$41`) is not this.** One axis only (Z), positive retract only, requires homing, and its config comment states "machine coordinates must be in all negative space and does not work with `DEFAULT_HOMING_FORCE_SET_ORIGIN` enabled". It is driven by the safety-door state. | **VERIFIED** — `config.h`, Setting_ParkingEnable block |

### The mechanism that already does exactly this

grblHAL's **manual tool change**. `tool_change.c`'s `restore()`: lift Z to `sys.home_position`, move
XY back to the saved `previous` at that height, `protocol_buffer_synchronize()`, restore coolant and
spindle with their configured start delays, then plunge Z back to `previous`. It runs on the Cycle
Start event, and `STATE_TOOL_CHANGE` is one of only two non-idle states where jogging is permitted.
**VERIFIED** — `tool_change.c`.

That is Peek, complete, with a tool change welded to it. **We are not using it**, for two reasons:
it can only be entered from an `M6` *in the program*, so peeks would have to be planned in advance;
and on this machine `M6` runs `tc.macro`, which takes a toolsetter reference. A peek that touches
off the puck is not a peek.

---

## 3. The mechanism we do use

**A soft reset from a *completed* hold keeps machine position.**

```c
sys.position_lost = st_is_stepping();
```

**VERIFIED** — `protocol.c`, in the reset path. Position is marked lost *only if the steppers were
still stepping*. After a hold has finished decelerating, they are not: the reset leaves the machine
Idle, position intact, no `Alarm:3`.

So Peek is:

> **hold → wait for hold complete → capture the frame → soft reset → the machine is Idle and free →
> move anywhere → come back → replay the frame → resume through the existing run-from-block path.**

It ends the run and starts a new one. That is not a workaround to be hidden; it is what happens, and
§8 says how to make it legible.

---

## 4. Sequence

Each step names its gate. The gates are the feature.

| Step | Action | Gate / why |
|---|---|---|
| **P1** | Send `CMD_FEED_HOLD`. | Peek is offered only while streaming — same condition as `runner.CanStop`. |
| **P2** | **Wait for `Hold:0` in a status report.** Not a timer. | §3 holds only once motion has stopped. `Hold:1` is *decelerating*; resetting there loses position. `GrblState.Substate` is already parsed and available (`GrblViewModel`, the `newstate/substate` update path). Inferring completion from "I sent `!` 200 ms ago" is the failure this whole feature turns on. |
| **P3** | Capture the **resume frame** (§5) — DRO, and `$G`. | `$G` answers in Hold: `output_parser_state` has **no state check at all** and is flagged `allow_blocking`. **VERIFIED** — `system.c` command table. This must happen *before* P4, because a soft reset returns the controller's parser to its power-up defaults. |
| **P4** | Soft reset. | Clears the hold, stops the stream, leaves the machine Idle with position intact. |
| **P5** | **Only now** emit the outbound park as ordinary g-code. | See the realtime-byte trap in §7.1. Nothing may be queued before P4. |
| **P6** | Machine is Idle and unrestricted. Operator jogs, looks, whatever. | Idle means the jog pad, the DRO and the MDI all work normally — no special mode to maintain. |
| **P7** | Operator presses **Cycle Start**. Emit the return, then re-run from the interrupted block. | Return is Z-clear → XY → plunge, mirroring `restore()`. |

### The park target

**Not machine home. Use `G30`.** Home is where the limit switches are and is rarely a good vantage
point. `G30` is already this app's park, it is already checked against the soft-limit envelope
before a program runs (`MacroRunner`'s stored-position prerequisite), and `MacroRunner.EmitGotoG30`
already exists and already carries the fixes from #316 and #359. **Reuse it. Do not write a second
park emitter** — that is how the tool-length sequence came to have two copies that drifted three
ways (#339).

---

## 5. The resume frame — and the gap this exposes

`JobRunner.Run(fromBlock, …)` already exists and already works: it sets `model.BlockExecuting`,
`job.CurrBlock/ACKPending/PendingLine` to `fromBlock` and streams from there. **VERIFIED.**

What it does **not** do is restore modal state. There is a prolog:

```csharp
public static readonly string[] DefaultProlog = { "G90 G94", "G17", "G21" };
```

**VERIFIED** — `GCodeJob.cs:292`. Distance mode, feed mode, plane, units. That is all.

Two problems with leaning on it:

1. **It is missing everything Peek needs.** No WCS, no spindle, no coolant, no tool-length offset, no
   feed rate. Resume mid-cut on that and the spindle is *off*. For "start from this toolpath" it is
   survivable, because a CAM section boundary usually re-declares `T`/`M6`/`S`/`M3` itself. Mid-block
   nothing is re-declared.
2. **Only one of the two start-from paths even sends it.** `StartSection` (the toolpath-group
   right-click) enqueues it; `StartHere_Click` (start from the *selected line*) calls
   `StartFromBlock.Execute` with no prolog at all. **VERIFIED** —
   `GCodeListControl.xaml.cs`. That is a pre-existing inconsistency, not something Peek introduces,
   but Peek would inherit it.

**Design:** Peek builds its own resume prolog from what `$G` reported at P3, and the natural home for
it is beside `DefaultProlog` — `GCodeJob.ResumeProlog(GrblParserState)` — so the two mid-program
start paths can converge on it later.

| Restore | From | Note |
|---|---|---|
| Units / distance / feed mode / plane | `$G` | What `DefaultProlog` already hardcodes; take the real values instead of assuming. |
| **Work coordinate system** | `GrblParserState.WorkOffset` | The one that silently ruins the part if wrong. |
| **Tool length offset** | `GrblParserState.ToolLengthOffset` | `G43.1`/`G49`. Getting this wrong is #340 — a machine handed back a whole tool length out. |
| **Feed rate** `F` | `$G` | |
| **Spindle** `M3`/`M4` + `S` | `GrblParserState.SpindleState` | Must be restarted **and allowed to reach speed** before re-entering the cut. `tool_change.c` uses `settings.spindle.on_delay` for exactly this; we have no equivalent and will need a dwell. |
| **Coolant** `M7`/`M8` | `GrblParserState.CoolantState` | |
| `G92` | — | **Deliberately not replayed.** A live `G92` survives the soft reset in the controller (and with `$384=0` it is persisted to NVS — #264). Re-issuing it would double it. Peek must *not* touch `G92`, and should refuse outright if one is live, because a peek is not the place to reason about it. |

⚠️ **Known defect in the class Peek would lean on.** `GrblParserState.IsPositionOffset` is inverted:

```csharp
isOffset |= !(double.IsNaN(pos.Values[i]) || pos.Values[i] != 0d);
```

which is true when the value **is zero**. **VERIFIED** — `Grbl.cs`. It is reached only through
`Get(bool addMissing)`, the vanilla-grbl workaround that synthesises `G43.1`/`G49`/`G92` entries for
controllers that do not report them — so grblHAL is unaffected today. Fix it before Peek is offered
on a plain-Grbl machine, or Peek will conclude "no tool offset" precisely when there is one.

---

## 6. Where it hooks in

| Piece | Where | What |
|---|---|---|
| State machine | `JobRunner` | A `PeekState` (None / Holding / Parked / Returning) beside the existing `pendingOffsetClear` pattern. |
| The wait for `Hold:0` and for `Idle` | `JobRunner`, driven off `GrblStateChanged` | **Copy `FlushPendingOffsetClear` exactly.** It exists because of this same class of race and is the proven shape (`JobRunner.cs:1128` and its comment). |
| Outbound / return moves | `MacroRunner.EmitGotoG30` + a new return emitter | Return must **name X and Y**, never a bare Z-only `G53` — §7.2. |
| Resume | `JobRunner.Run(peekBlock, honorActiveProgram: false)` | `honorActiveProgram: false` — a Generate-first tab must not hijack the resume. The existing `Run` already branches on Hold for plain resume; Peek's resume arrives with the machine **Idle**, so it takes the `Source.IsLoaded` branch. |
| Program-fits check | already correct | `Run` runs `ProgramFitsMachine()` only when `fromBlock == 0`, so a resume does not re-litigate it. **VERIFIED.** |
| `PREREQ` | already correct, and worth knowing | `PREREQ` rows are re-evaluated on every Cycle Start "including a mid-program start" — so a peek resume re-checks homed/`G30`/build options. That is right. |
| Button | `JobControl.xaml`, beside Feed Hold and Stop | New `CanPeek` on `JobRunner` feeding an `IsPeekEnabled`/visibility pair, mirroring `CanFeedHold`/`CanStop`. Per #355 it should be **collapsed** when it means nothing, not greyed. Needs an `x:Uid` and a row in all 7 locale CSVs. |
| Record | `model.LogDetail` | §8. |

---

## 7. Traps, each one already paid for

1. **🔴 A queued line cannot survive a realtime byte.** `CMD_STOP` is realtime: it bypasses the line
   queue and flushes the controller's RX buffer. A `G92.1` written 4 ms earlier was discarded, and
   with `$384=0` the orphaned offset then survived every power cycle and silently shifted work Z for
   hours (#264; the comment lives at `JobRunner.cs:1119`). **Peek must never queue a move before the
   reset.** Every motion in §4 is emitted *after* the controller is confirmed Idle.
2. **🔴 A `G53` Z-only lift is not a safe lift.** If a rotation write has touched the active WCS, the
   firmware's parser holds a corrupted position and the next move leaving an axis *unnamed* flies to
   it — 662 mm across a table, observed (#358, #359). Peek's return **names X and Y**.
3. **🔴 An unanswered query is unknown, not absent** (#243). If `$G` does not answer at P3, Peek
   **aborts and leaves the machine held**. It must not park on a default frame — the whole point of
   P3 is that the frame is unrecoverable afterwards.
4. **The ack wait behind `$G`.** `GrblParserState.Get` uses `WaitFor.AckResponse` with a 400 ms
   timeout and brackets it in `PollGrbl.Suspend()/Resume()`. That suspend is a **shared flag** an
   inner helper can clear underneath you (#346), and this family of wait has been wrong three times
   (#244 error-is-an-answer, #258 unbounded, #370 raced the poller). Do not assume it is reliable
   here; instrument it on the first hardware run.
5. **Spindle up to speed before re-entry.** The return must dwell. `tool_change.c` has
   `settings.spindle.on_delay`; we do not, so it is ours to choose and to state.
6. **The interrupted block is re-cut from its start.** Resume is per *line*, not per *point*. Usually
   harmless, occasionally a witness mark. Say so in the prompt rather than letting it be discovered.

---

## 8. Honesty about what it is

Peek ends one run and starts another. The 3D view, the elapsed timer and the status log will all see
two runs. Rather than disguise that:

- Log `Peek - paused at line N, parked at G30` and `Peek - resumed at line N` through `model.LogDetail`,
  so the status log (#280) explains a job that has two starts.
- The second `Program start` line should carry a `[RESUMED]` qualifier, the same way a dry run carries
  `[DRY RUN]` — for the same reason: two runs that look identical afterwards is how "it ran fine" gets
  said about something that did not.

---

## 9. Refusals

Peek declines, with a reason, when:

- the machine is not streaming a loaded program (it is not a jog helper);
- the program is an **SD-card job** — the sender never sees those lines, so there is no block to
  resume from. Same visibility rule that already excludes dry run;
- a **`G92` offset is live** (§5);
- `$G` did not answer (§7.3);
- `G30` is unset or outside the soft-limit envelope — the existing stored-position check already
  knows how to say this.

---

## 10. Open questions — for the user, not for me

1. **Park at `G30`, or at a Peek-specific position?** `G30` is reused and already envelope-checked,
   but on this machine it is the tool-change park, which may not be where you want to *look* from.
2. **Should Peek stop the spindle while parked?** Safer, and adds a spin-up to every resume. Or leave
   it running, which is faster and is what a tool change does not do.
3. **Is re-cutting the interrupted line acceptable**, or should Peek only ever pause at a block
   boundary — which would make it approximate rather than immediate?

---

## 11. First hardware test

In this order, on scrap, with the spindle **off** for the first three:

1. Peek with no program running → refused with a reason.
2. Peek mid-air-cut, park, resume → confirm position returns to within a step, confirm `$G` before
   and after match.
3. Interrupt during a rapid, and again mid-arc — the arc is the one that exercises "re-cut from the
   start of the line".
4. Peek with a WCS rotation live, then check the return did **not** move X/Y unexpectedly (trap 7.2).
5. Only then, spindle on, in wood.
