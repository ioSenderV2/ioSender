# Unified streaming engine — design

Written 2026-08-08. **Design only — no code changed by this doc.** Grows out of
[Architecture-Streaming-Paths.md](Architecture-Streaming-Paths.md) (the 2026-08-05 audit) and the
user's confirmed direction that day: *"A single path for all programs/macros to run through. With
consistent behavior."*

## The ask, restated

Work Order's Generate button today takes **two presses**:
1. **Generate** — `MacroProcessor.PublishGenerated` builds directive-bearing text and shows it in a
   separate overlay `ProgramView`. The Job tab's real `GCode.File` is untouched.
2. **Run** — `MacroProcessor.Run` (→ `MacroRunner.Run`) walks the text itself, interpreting
   `(PREREQ)`/`(PROMPT)`/`(MBOX)`/`(WAITIDLE)` directives and flushing plain g-code between them as
   bursts through `RunStreamedJobInPlace`, which rebuilds `GCode.File` and fires a **deferred** Cycle
   Start (`DispatcherPriority.Background`).

The audit's Finding #2: that deferred Cycle Start is a documented, logged priority-starvation race
(`"skipped stale deferred CycleStart"`), not a missing feature. The "press Start twice" symptom the
user hit is very likely that race losing, not something to work around again.

**Confirmed direction:** don't patch the race — remove the second press by deleting the second engine.
`MacroRunner.Run`'s directive-interpretation loop moves INTO the streamer itself, so:
- Generate writes directive-bearing text **directly into `GCode.File`/the real `ProgramView`** — no
  overlay.
- The Job tab's own dispatch loop recognizes and acts on directives **inline**, the same way it already
  handles an ordinary line.
- Cycle Start always means "go." Loading a `.macro` via plain **Load File** and pressing Cycle Start
  does the right thing too — directive-awareness becomes a property of the streamer, not an opt-in mode
  Work Order happens to use.

## Today's actual shape (tighter than the audit's summary)

The audit found routes 1–4 all converge on **one streamer**. Reading the dispatch code directly for
this doc sharpens that: it isn't one streamer, it's **two independent per-line dispatch loops that
duplicate each other's dry-run suppression**, plus one outer interpreter layered on top of whichever
one a macro burst happens to use:

| Layer | What it is | Where |
|---|---|---|
| Outer: directive interpretation | `MacroRunner.Run`'s line scan — decides when to flush a burst, blocks (pumping `DoEvents`) at `WAITIDLE`/`MBOX`/`PROMPT` | `CNC Core/MacroRunner.cs` |
| Inner: ack-paced dispatch (background thread) | `StreamPump.SendNext` — grbl character-counting flow control, runs off the comms read thread | `CNC Core/StreamPump.cs:274` |
| Inner: ack-paced dispatch ("legacy", UI thread) | `JobRunner.SendNextLine` — same job, inline in `JobRunner`'s own response handling, still live per its own comments ("mirrors StreamPump.SendNext's buffered-path equivalent") | `CNC Core/JobRunner.cs:1751` |

`StreamPump.SendNext` (lines 296-319) and `JobRunner.SendNextLine` (lines 1772-1790) both independently
neutralise M3/M4/M7/M8/M6 under dry run from the SAME precomputed `HasSpindleOrCoolantOn`/
`HasToolChange` flags — identical logic, two places. `MacroRunner.DryRunNeutralize` (the audit's
Finding #4) is a **third**, older copy that re-parses each line instead of reading the flag, and is
already dead weight whenever `preferJobView` is true (Work Order already skips it, per its own
comment). **Three implementations of one rule, not two.**

**✅ Q1 answered (2026-08-08), by tracing the code, not guessing:** `StreamPump` is the ONE live
per-line dispatch loop. `JobRunner.Run` unconditionally creates and starts a `StreamPump` for every
real job (`JobRunner.cs:839-844`) — the comment right there says it outright: *"The send/ack flow
control ALWAYS runs on the dedicated background thread (StreamPump)... including Check mode ($C),
which USED TO fall back to the legacy UI-thread streamer... Check mode no longer needs a separate
streamer."* The moment the pump is active, `streamingHandler.Count` is explicitly set `false` "to
stop legacy line accounting so a late/trailing response can't re-enter it" (`OnPumpJobFinished`/
`OnPumpError`, `JobRunner.cs:899,907`) — i.e. `SendNextLine`'s own switch-driven re-entry is
deliberately neutered while the pump owns the run. `SendNextLine` isn't fully dead code, though: it's
still called directly from a couple of narrow resumption points (tool-change-line resume,
`JobRunner.cs:714`; a `StreamingState.Send` case explicitly commented "Only entered in legacy mode",
`JobRunner.cs:1255-1269`) that Step 2/3 below need to check are actually reachable before deciding
whether directive recognition needs adding there too, or whether they're dead in practice and safe to
delete alongside the rest of the legacy path. **Directive support goes into `StreamPump.SendNext`.**

## The directive model

Each directive keyword needs a different shape of handling. None of this is "run it as a normal line":

### `(PREREQ, ...)` — up-front gate, before any motion
Scan the whole loaded program once, before Cycle Start fires (not per-line during dispatch) — same
timing as `MacroRunner.Run`'s step 1 today, just relocated to fire from wherever Cycle Start is
requested (`JobControl.Run` / `JobRunner.Run`) instead of from `MacroRunner.Run`. Unmet condition
refuses the run with the existing message box. Applies uniformly whether the program came from
Generate or a plain Load File.

### `(PROMPT, ...)` — up-front collection, per-line substitution
Same up-front pass as PREREQ: collect every `(PROMPT param, default[, label])` into one combined
dialog shown before Cycle Start (bare `(PROMPT)` with no fields = a plain run-confirmation, same as
today). Values are **substituted at send time** (`#<_name>` regex replace, reusing
`MacroRunner.ApplySubstitutions` verbatim) rather than rewriting the stored rows — keeps `GCode.File`'s
text stable for inspection/re-run, matches how dry-run suppression already rewrites the wire line
without touching the stored block.

### `(WAITIDLE)` — a dispatch barrier, not a blocking wait
This is the one genuine behavior change, not a relocation. `MacroRunner.WaitForIdle` blocks
synchronously (pumping `EventUtils.DoEvents`) — safe there because it owns its own call stack. It
CANNOT be ported as-is into `SendNextLine`/`SendNext`, because those are themselves invoked FROM the
response/ack handlers that would need to keep running underneath a blocking wait.

The existing `probePending` mechanism is the right shape to extend, not reinvent: dispatch already
knows how to "hold everything, resume on a later event" for G38 probes (`JobRunner.cs` — "Probe
barrier: hold all lines while a streamed probe is in flight"). A `WAITIDLE` row should set an
equivalent barrier flag that the dispatch loop checks and does not advance past, released by the SAME
event stream that already re-invokes dispatch (status reports / acks) once `ACKPending` has drained to
0 AND two consecutive Idle reports have been observed — mirroring `WaitForIdle`'s own success
condition, just event-driven instead of polled-and-blocking.

**⚠️ Gap found 2026-08-08, changes Step 3's real shape — not just "extend probePending":**
`probePending`'s release condition (`inflight.Count == 0`, `StreamPump.cs:388`) is pure ack-bookkeeping
— it never looks at `GrblState` at all. Checked directly: `StreamPump` taps `Comms.com.AckSink` and
**nothing else** — it has zero visibility into status reports, by design (the pump thread's own
threading contract is "every accounting field touched ONLY by the pump thread," deliberately decoupled
from the UI-thread `GrblViewModel`/`GrblState` machinery). "Two consecutive Idle reports" is a signal
that **does not reach the pump thread today** — there is no existing path to extend, unlike probe
completion. A `WAITIDLE` barrier gated on ack-draining alone (the signal that IS already there) would
be actively unsafe: acks for a controller-buffered move can land before the physical motion finishes,
which is exactly the race `WAITIDLE`/`MacroRunner.WaitForIdle` exists to close.

**What Step 3 actually needs, not yet designed:** a new, thread-safe way for a status report (observed
on the UI thread, same as everywhere else `GrblState` is read) to reach the pump thread and clear the
barrier — something in the shape of `Comms.com.AckSink`'s own callback wiring, but for status reports,
scoped so it only matters while a `WAITIDLE` barrier is actually set (no cost/risk to every other job).
**Left undesigned rather than guessed** — this is exactly the kind of decision that shouldn't be made
blind, and it changes Step 3 from "mirror an existing mechanism" to "design a new cross-thread signal,
then mirror the barrier shape." Answer this BEFORE Step 3 code, not during it.

**✅ Signal built 2026-08-08.** The gap above is closed at the transport level, not by adding a
narrow second `StatusSink` alongside `AckSink` — `AckSink` (a single-purpose `Action<string>`
*property*, one subscriber at a time) is **replaced** by `Comms.ReplyClassified`, a real multicast
event (`event Action<Comms.ReplyClass, string>`) raised from the SAME classification point in all
four stream classes (`Serial`/`Telnet`/`Websocket`/`Eltima`Stream) for **every** reply, not just
ack/nak — `ReplyClass` is `{ Ack, Nak, Status, Other }`, status recognized by `reply[0] == '<'`.
`StreamPump` migrated from assigning `AckSink` to subscribing `OnReplyClassified`; Ack/Nak handling
is bit-for-bit the same as the old closure (`if (!Suspended) acks.Add(reply)`), and Status is now
logged (`PumpLog.W("STATUS " + reply)`) so its presence is confirmable on real hardware — **nothing
consumes it yet**, the WAITIDLE barrier itself is still not built. One non-obvious risk caught and
guarded before it shipped: `JobRunner` REUSES the same `StreamPump` instance across jobs rather than
recreating it, and a real event *accumulates* subscribers with `+=` where the old property always
just replaced the previous value — so `Start()` now does `-=` before `+=` (a safe no-op if nothing
was subscribed) to guarantee exactly one active subscription regardless of what state a previous
run's cleanup left behind; without that guard, a `Start()` racing ahead of a previous `Abort()`
would silently double-process every ack.

Verified two ways before trusting it: `.\build.ps1 -Scratch` (compiles clean) AND
`dotnet run --project tools/websocket-probe` — a REAL loopback test against the actual production
`WebsocketStream` (not a mock) — all 22 checks passed, including "ack tapped via ReplyClassified"
confirming ack delivery through the new event is unchanged. Still needs a real hardware jog/job test
before the barrier logic itself gets built on top of it.

**Remaining for Step 3 proper (not done):** the actual `WAITIDLE` dispatch barrier that consumes
`Status` — recognizing two consecutive `<Idle|...>` reports, clearing the barrier, resuming dispatch.

### `(MBOX, ...)` — a dispatch barrier + a prompt
Same barrier pattern as `WAITIDLE`: hold dispatch, show the existing non-modal `HoldPrompt` (already
built not to steal focus, so the operator can jog while it's up — keep that property), resume dispatch
from the prompt's own callback. Cancel/No needs to abort the run the same way an operator-initiated
Stop already does — reuse that path, don't invent a second "aborted mid-directive" state.

### Recognizing a directive row without re-parsing text every time
Add a precomputed flag at load time (`GCodeJob.ParseFileLines`/`AddBlock`, alongside the existing
`HasSpindleOrCoolantOn`/`HasToolChange`) — e.g. `IsDirective` — so the dispatch loop branches on a flag
instead of running `MacroRunner.IsDirective`'s string check on every line. Mirrors the existing
pattern exactly.

## What's reused vs. what retires

**Reused (relocated, not rewritten)** — pure logic, zero dependency on the old bursty streamer:
`IsDirective`, `Body`, `EvalPrereq`, `ParsePromptField`, `ApplySubstitutions`, `RunMBox`,
`CoordinateSystemDefined`, `StoredPositionUnreachable`, `SanitizeComment`. These move to wherever the
new up-front scan + per-line dispatch lives.

**Retires once this lands:**
- `MacroProcessor`'s overlay `ProgramView` + the two-press Generate/Run flow.
- `RunStreamedJobInPlace` and its deferred-Cycle-Start dispatcher hop (Finding #2 — the whole class of
  bug disappears, it isn't fixed, because there's no longer a second `Run()` call to race).
- `MacroRunner.Run`'s own loop (`Flush`/`StreamProgram`) and `DryRunNeutralize`.

**Explicitly out of scope** — Finding #1 (dry run doesn't protect controller-side `.macro`/SD-card
routes, because those never reach the sender's streamer at all) is a different problem: it's about
routes 5/6 bypassing the streamer entirely, which this redesign doesn't touch. Stays open, tracked
separately.

## Staged plan (same discipline as JobRunner's own 4-step extraction — small, hardware-verified, machine free)

1. ✅ **Settle the `StreamPump` vs. `JobRunner.SendNextLine` duplication** — investigation only, no
   behavior change. Prerequisite: don't add directive support to a dead path. **Done 2026-08-08:
   `StreamPump` is live, see "Today's actual shape" above.**
2. ✅ **Add `IsDirective` at load time**, no dispatch behavior yet. Verify inert — a directive line looks
   like an ordinary comment today, so nothing should change until the next step reads the flag.
   **Drafted 2026-08-08 (`d330305`), compile-checked via -Scratch only, NOT hardware-verified — a
   carve was running, no `-Launch` was possible. Verify inert on a real launch before trusting it.**
3. ✅⏳ **The status-report-reaches-the-pump-thread signal is built** (`Comms.ReplyClassified`,
   2026-08-08 — see the gap note above) — compile-checked + verified via the real `websocket-probe`
   loopback test, NOT yet hardware-verified on a real controller. **Still to do:** the `WAITIDLE`
   barrier itself that consumes it — smallest functional slice, hardware-verify on a macro that uses
   only `WAITIDLE`.
4. **`MBOX` barrier + `PROMPT` up-front dialog/substitution.**
5. **`PREREQ` up-front gate.**
6. **Point Work Order's Generate button at the new path** — write into `GCode.File` directly, one
   Cycle Start. Retire the overlay.
7. **Delete the now-dead code** in one cleanup commit, separate from the functional slices above.

## Open questions — ✅ all three answered 2026-08-08

- **Q1 — which dispatch loop is live:** `StreamPump`. See the note under "Today's actual shape" above.
- **Q2 — where the up-front PREREQ/PROMPT dialog lives:** inside `JobRunner.Run` itself, via the SAME
  static seams `MacroRunner` already established (`CNC.Core.UserPrompt`/`FieldPrompt`/`HoldPrompt`) —
  not a new caller-side step in `JobControl`. `MacroRunner` and `JobRunner` are both already in
  `CNC.Core` and both need to stay WPF-free (the client/server split's own rule: "what talks to the
  machine stays [in Core]; what talks to the operator goes [through a seam, never direct UI]" —
  [[iosender-client-server-split-project]]). Reusing the existing delegates means no new seam to
  design or wire up — `JobRunner.Run` just calls the same statics `MacroRunner.Run` already calls.
- **Q3 — MBOX Cancel/No semantics:** the existing Stop path, not a new softer "abort mid-directive"
  state. By the time an MBOX prompt is showing, the dispatch barrier (same mechanism as `WAITIDLE`)
  has already halted line-sending — there's no in-flight motion to distinguish from a normal Stop, so
  a second abort concept would be pure duplication for no behavioral gain. Reusing Stop also means any
  future improvement to Stop's own handling applies here for free. Flagged for a quick hardware sanity
  check during Step 4 (does Stop behave sensibly when called with nothing actually in flight? almost
  certainly yes — Feed Hold/Stop already have to tolerate an idle controller — but confirm, don't
  assume, before shipping it).

Related: [[iosender-unified-streaming-engine-design]] (memory), [Architecture-Streaming-Paths.md](Architecture-Streaming-Paths.md),
[[iosender-jobcontrol-run-blocker]] (JobRunner's own extraction history/discipline to mirror).
