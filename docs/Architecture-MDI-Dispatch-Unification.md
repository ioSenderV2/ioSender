# MDI dispatch unification — spec

**Status:** proposed, not started. Written 2026-08-10 at the end of the split-screen/3D-view session,
for a session of its own.

**Goal:** retire `JobRunner`'s private `SendMDI` pacing so there is ONE mechanism that puts a line on
the wire and waits for its acknowledgement, rather than two that must each be taught the same
firmware quirks.

**Not the goal:** making a typed command into a job. See [Rejected: route MDI through the program
view](#rejected-route-mdi-through-the-program-view).

---

## Why

Two independent pacing mechanisms exist today, and every firmware quirk has to be discovered and
fixed in each of them separately. That is not a hypothetical cost — it is the direct cause of at
least three logged defects:

| Defect | Mechanism | Fixed in |
| --- | --- | --- |
| Jogging dead until app restart (`$J=` + `0x85` flushed, never acked, queue grew 1..5) | `SendMDI` waits forever for an ack the firmware will not send | `3eb08c6` — `ReleaseFlushedJogMdi`, a fix `JogGate` already had |
| A macro's 14 lines / 670 bytes flooded out in 6 ms with no ack pacing → `error:71` | `SendMDI` flipped to Idle the moment its LOCAL queue emptied | the `case StreamingState.SendMDI:` comment at `JobRunner.cs:1856` |
| `error:9` zeroing Z right after a jog, with `Idle` on screen | neither mechanism knew a jog was queued-but-unacked | `fac78f6b` — refuse at `ApplyCommand`, **prevention, not a fix** |

The pattern is explicit in the code. `JobRunner.cs:1141` says it outright: *"grblHAL documented
(JogGate.cs) to never ack a `$J=` sent while already in Jog state"* — a quirk `JogGate` had already
been taught and `SendMDI` had not. Same firmware, same wire, two places to learn it.

The unified streaming engine already retired the **macro** engine for exactly this reason. MDI is
the last path still carrying its own pacing.

---

## Current state

### The MDI path

```
caller → GrblViewModel.ExecuteCommand / ExecuteMDI
       → ApplyCommand            ← the only refusal seam (Tool state; jog-outstanding as of fac78f6b)
       → sets the MDI property
       → JobControl's PropertyChanged on nameof(GrblViewModel.MDI)
       → JobRunner.SendCommand                          (JobRunner.cs:1110)
       → Source.Commands.Enqueue + a synthetic ResponseReceived("go")
       → case StreamingState.SendMDI in ResponseReceived (JobRunner.cs:1856)  ← the ONLY place a
         queued command reaches the wire, and it only runs when a REAL ack arrives
```

Properties worth preserving:

- **One outstanding command at a time.** Deliberate: the flood that produced `error:71` is what
  happens without it.
- **`streamingState` doubles as the "busy" flag**, which is why the state must stay `SendMDI` while
  an ack is outstanding even if the local queue is empty.
- **Realtime bytes bypass everything** — `command.Length == 1` → `SendRTCommand` (`JobRunner.cs:1117`).
- **`Source.Commands` is never purged.** `3eb08c6` purges queued `$J` rows on release specifically
  because replaying a stale jog was a 152 mm surprise-Z hazard.

### The program path

`StreamPump` writes with planner-buffer-aware flow control, plus the whole job apparatus: the
`(WAITIDLE)` / `(MBOX)` / `(PROMPT)` / `(PREREQ)` directives, progress marking, dry-run Z lift,
Simulate, Feed Hold / Stop / Rewind, run-end watchers, parser-state restore.

**Only the first clause is wanted here.** Everything after "plus" is job semantics that a single
typed line must not trigger.

---

## Target

Extract the pacing primitive the pump already implements — *write one line, wait for its ack, honour
the firmware's ack quirks, never let a flushed command wedge the queue* — and have BOTH the pump and
MDI dispatch use it.

MDI keeps its own identity: command history, console echo, `_mdiQueryPending` for typed `$` replies,
immediacy, and the Tool-state allowances. What it loses is its *private* answer to "when may the
next line go out".

The jog-outstanding refusal added in `fac78f6b` should survive as a **guard**, not be replaced by
queueing — see the decision below.

---

## Migration steps

1. **Name the primitive.** Read `StreamPump`'s write/ack path and `JobRunner.cs:1856` side by side
   and write down the contract they should share, including: what counts as an ack, what happens to a
   command whose ack never arrives, and what is purged on release. Do this before moving any code —
   the two disagree today and the disagreement IS the bug class.
2. **Extract it** behind an interface, with the pump as the first caller. No behaviour change; the
   pump's hardware-verified behaviour is the reference.
3. **Re-point `JobRunner.SendCommand`** at the primitive. `streamingState == SendMDI` stops being the
   busy flag; the primitive owns that. Keep the `ReleaseFlushedJogMdi` semantics — they move into the
   primitive rather than being deleted, since the firmware quirk they answer is real.
4. **Delete the parallel path** — the `case StreamingState.SendMDI:` branch and the synthetic
   `ResponseReceived("go")` kick. Deleting is the point of the exercise; leaving it dormant recreates
   "two plausible mechanisms, one live" ([[dead-KeypressHandler lesson]], cited in `GrblViewModel.cs`).
5. **Re-check the guard.** With one queue, `JogGate.Pending` may become derivable from the primitive.
   Do NOT remove the refusal — see below.

## Decisions already made (do not re-open)

- **A jog-blocked g-code command is REFUSED, never deferred.** `G10 L20` means "make HERE read zero";
  by the time a jog finishes, here has moved — one millimetre in the 2026-08-10 report. A deferred
  zero sets the work origin somewhere the operator never chose, silently, and on Z that is a crash or
  a scrapped part. Same doctrine `JogGate` applies to jogs: the request referred to a position that
  no longer exists.
- **Only g-code is gated.** `$` commands and realtime bytes are not subject to the firmware's
  lockout, and gating them would break Feed Hold, soft reset and jog cancel — far worse than the
  error being prevented.

## Rejected: route MDI through the program view

Considered 2026-08-10. `GCode.File.Push()/Pop()` makes it mechanically possible (Work Order already
does it), but every typed line would then displace or interleave with the loaded program and drag the
job lifecycle with it: run-start/run-end transitions, the elapsed timer, the "ready — press Cycle
Start" prompt, run-end watchers that pop tabs. It also does not apply to `$` commands or realtime
bytes, which are not programs. Sharing the *pacing primitive* gets the benefit; sharing the *program
view* imports the costs.

---

## Risks

- **94 `ExecuteCommand` call sites** across ~30 files (`GotoBaseControl` 9, `ToolLengthControl` 9,
  `StatusControl` 7, `SpindleControl` 6, `JogBaseControl` 5, …). The signature should not change; if
  it must, that is the moment to stop and reconsider scope.
- **Probing is the highest-consequence caller.** `CNC Controls Probing` drives real motion off MDI
  commands and reads results back. Any change to ack timing shows up there first, and a probe that
  mis-sequences drives a probe into the work.
- **Timing changes are invisible until hardware.** Both prior defects here presented as "the app is
  dead" or "the controller threw errors it should never have seen", neither reproducible on a clean
  build.
- **`Source.Commands` purge semantics must survive the move.** Replaying a stale jog is a real
  hazard, not tidiness.

## Hardware verification plan

The unit that matters is the wire, so verify with `latest_wire.log` + `-debuglog=jobrunner,jog`, not
by reasoning about the code:

1. **Jog rate** — hold a continuous jog, then discrete taps. No `DROPPED`, no growing queue depth, no
   two-second cadence (that cadence is `AckTimeoutMs` and means the gate is doing the pacing).
2. **The wedge repro** — tap-release continuous jog after a Hold→Stop, then jog again. Expect
   `SendMDI RELEASED` (or its successor) and a live second jog.
3. **The macro flood** — a macro sending several lines in one C# loop. Expect one ack per line on the
   wire, not 14 lines in 6 ms, and no `error:71`.
4. **The `error:9` case** — jog, then immediately zero Z. Expect the refusal message, never `error:9`.
5. **A probing cycle end to end**, on the SIMULATOR first ([[iosender-testserver-real-hardware-safety]]).
6. **A full job**, to prove the pump's own behaviour did not regress when its internals were extracted.

## Reading list before starting

- `CNC Core/CNC Core/JogGate.cs` — the firmware quirk, and the doctrine on dropping stale requests.
- `CNC Core/CNC Core/JobRunner.cs:1110` (`SendCommand`) and `:1856` (`case StreamingState.SendMDI`) —
  both comment blocks record hardware evidence; read them, do not skim.
- `CNC Core/CNC Core/StreamPump.cs` — the reference implementation.
- `docs/Architecture-Streaming-Paths.md` and `docs/Architecture-Unified-Streaming-Engine.md` — the
  audit that concluded there is one streamer, and the engine that retired the macro path.
