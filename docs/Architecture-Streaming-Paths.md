# How g-code reaches the controller — an audit

Written 2026-08-05, prompted by the question *"I wonder how many times the fact that work order
programs run through a different code path to stream their g-code is a problem."*

Read-only audit. No behaviour was changed by it. The point is to answer that question honestly and
to give step 4 (extracting `JobRunner` out of `JobControl.xaml.cs`) something to be designed against,
rather than mechanically preserving whatever is there.

## The short answer

**It is not two paths, and the split is not where it looks.**

There is **one streamer** — `StreamPump`, driven by `JobControl.Run` — and every in-sender program
reaches it. What differs between a "work order" run and a "loaded job" run is only *which object
`RunControl.Source` points at* and *who puts it back afterwards*. That part is in decent shape.

The real divergence is elsewhere, and it is threefold:

1. **Three routes bypass the sender's streamer entirely**, so no sender-side guard applies to them at
   all — SD-card jobs, controller-side `.macro` files invoked by O-word `CALL`, and MDI.
2. **`IsTransient` silently changes safety behaviour**, and it is set by *how the program was
   created*, not by what it contains.
3. **The macro route re-implements protections the streamer already has**, so the same rule exists
   twice with different wording and can drift.

## The routes

| # | Route | Source object | Reaches `StreamPump`? |
|---|---|---|---|
| 1 | Loaded job (Load File → Cycle Start) | `GCode.File` | yes |
| 2 | Macro / wizard (`MacroRunner` → `RunStreamedJobInPlace`) | transient `GCode(model)` | yes |
| 3 | Work Order (`preferJobView: true`) | **`GCode.File` itself** | yes |
| 4 | Wizard "Generate and Run" (`MacroProcessor.ActiveRun`) | resolves to 2 or 3 | yes |
| 5 | SD-card job (`$F=`, `IsSDCardJob`) | none — controller reads its own card | **no** |
| 6 | Controller-side macro (`O<pcorner> CALL`) | none — controller reads its own filesystem | **no** |
| 7 | MDI / realtime | none | **no** |

Routes 1–4 converge on `JobControl.Run` → `StreamPump`. Feed Hold, Stop, the streaming state
machine, per-line status marking and the ack flow control are therefore **common** to all of them —
including Work Order. That is the part the original worry was about, and it is genuinely shared.

## Where they actually diverge

| Protection | 1 loaded job | 2 macro transient | 3 Work Order | 5 SD card | 6 controller macro |
|---|---|---|---|---|---|
| Dry-run G92 Z clearance | ✔ | ✘ *by design* | ✔ | refused outright | ✘ **silently** |
| Per-line M3/M4/M7/M8 suppression | ✔ `StreamPump` | ✔ `MacroRunner.DryRunNeutralize` | ✔ `StreamPump` | ✘ | ✘ **silently** |
| M6 suppression under dry run | ✔ | ✔ | ✔ | ✘ | ✘ **silently** |
| Feed Hold / Stop | ✔ | ✔ | ✔ | ✔ realtime | ✔ realtime |
| Per-line Sent/status marking | ✔ | ✔ | ✔ | ✘ | ✘ |

The `✘ by design` is correct and deliberate: dry run is a Job-tab toggle, and letting a stray G92 Z
offset leak into a probing macro corrupted real positioning once already. `IsTransient` is what
excludes it.

## Findings, worst first

### 1. Dry run does not protect anything inside a controller-side macro, and does not say so

SD-card jobs are **explicitly refused** when dry run is armed, with a clear message, on the reasoning
that the sender never sees the lines so it cannot intercept them
(`JobControl.Run`, `model.IsSDCardJob` branch).

That exact reasoning applies to route 6 and nothing enforces it. `GCodeJob` deliberately does not set
`HasSpindleOrCoolantOn` / `HasToolChange` for O-word lines (`GCodeJob.cs:129` — "those are macro
control flow, not raw spindle commands"), which is right for the *line itself* — but the macro it
calls is opaque, may spindle up, and executes on the controller with the dry-run **G92 Z offset
active**.

A Work Order program is route **3**, which is *not* transient, so dry run fully applies to it — and
Start Job generates programs containing `O<pcorner> CALL`. So the corruption the `IsTransient`
exclusion exists to prevent is reachable by a different route, with no exclusion and no warning.

**Analysis, not observed.** It should be reproduced on the simulator before anything is changed.

### 2. The deferred Cycle Start is a known priority starvation with a workaround, not a fix

`RunStreamedJobInPlace` defers `RunControl.Run` to `DispatcherPriority.Background` and its own comment
records that it can be starved behind status-report handling until *after* the burst it belongs to has
finished — whereupon the next `(WAITIDLE)` walks into an untracked stream and aborts. The fix applied
was a staleness guard (`if (RunControl.Source == prog)`), which makes the symptom safe rather than
removing the race.

Note the interaction with `bbd5207`: comms replies moved from `Normal` (9) to `Input` (5) to stop them
starving Feed Hold. `Input` still outranks `Background` (4), so this particular starvation is changed
but not removed. Step 4 should give the run start a deterministic ordering instead of a priority race.

### 3. `preferJobView` carries an undocumented single-burst assumption

`RunStreamedJobInPlace` rebuilds directly into `GCode.File` with `Action.New`, which replaces its
content wholesale. That is safe only because Work Order's compiled program contains no `(MBOX)` or
`(WAITIDLE)`, so it always flushes as exactly one burst. The code says so. A second `preferJobView`
caller that streams more than one burst would have each burst silently wipe the previous one.

### 4. The same rule is written twice

Dry-run neutralisation exists in `StreamPump.SendNext` (per-block, from parser tokens) *and* in
`MacroRunner.DryRunNeutralize` (per-line, re-parsing). `MacroRunner` deliberately skips its copy when
`preferJobView` because it would be redundant — that conditional is exactly the kind of thing that
rots. One implementation, applied at the single point every route passes through, would be better.

## What this means for step 4

The `JobRunner` extraction should not preserve the current shape. Specifically:

- **Put every sender-side protection in the streamer**, at the one point routes 1–4 pass through,
  and delete the macro-route copies. `IsTransient` becomes an input to that one decision instead of a
  property some sources carry and others do not.
- **Make "the sender cannot see these lines" an explicit, first-class case** covering routes 5 *and*
  6, so the SD-card refusal and the controller-macro gap are the same code answering the same
  question.
- **Give run-start deterministic ordering.** No dispatcher priority races, no staleness guards
  compensating for them.
- Treat `Source` as a parameter of a run rather than shared mutable state that has to be saved and
  restored around every burst.

## Answer to the original question

Of the incidents this session — the Feed Hold starvation, the filesystem-listing collision, the
Validate bugs — **none were caused by the two-path split.** The Feed Hold bug was in the shared
marshalling layer and affected every route; the filesystem collision was a missing guard on an
unrelated background poll.

So the honest answer is: **the split has cost less than it looks, because the paths converge earlier
than the code suggests.** The cost is concentrated in one place — the protections that were
re-implemented on the macro side rather than shared, and the routes that bypass the streamer without
being explicitly accounted for. Finding 1 is the one worth acting on regardless of step 4.
