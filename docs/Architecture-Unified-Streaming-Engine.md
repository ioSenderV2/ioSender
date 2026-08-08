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

**Open question, blocks Step below:** which of `StreamPump`/`JobRunner.SendNextLine` is actually the
live path today (config-dependent on `useBuffering`?), and is the other vestigial. Don't know yet —
verify before adding directive support to either, so it isn't built twice again.

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

1. **Settle the `StreamPump` vs. `JobRunner.SendNextLine` duplication** — investigation only, no
   behavior change. Prerequisite: don't add directive support to a dead path.
2. **Add `IsDirective` at load time**, no dispatch behavior yet. Verify inert — a directive line looks
   like an ordinary comment today, so nothing should change until the next step reads the flag.
3. **`WAITIDLE` barrier only** — smallest slice, mechanically closest to the existing `probePending`
   pattern. Hardware-verify on a macro that uses only `WAITIDLE`.
4. **`MBOX` barrier + `PROMPT` up-front dialog/substitution.**
5. **`PREREQ` up-front gate.**
6. **Point Work Order's Generate button at the new path** — write into `GCode.File` directly, one
   Cycle Start. Retire the overlay.
7. **Delete the now-dead code** in one cleanup commit, separate from the functional slices above.

## Open questions (need an answer before Step 1, not before Step 6)

- **Q1:** Which of `StreamPump`/`JobRunner.SendNextLine` is the live dispatch path today — is the other
  dead, or does `useBuffering`/some config still route through both? Needs verifying in code, not
  guessed.
- **Q2:** Where should the up-front `PREREQ`/`PROMPT` scan + dialog live — inside `JobRunner.Run`
  itself, or a caller-side step in `JobControl` before Cycle Start is requested? Affects whether
  `JobRunner` needs its own UI-prompt seam or keeps taking an already-resolved program.
- **Q3:** `MBOX`'s Cancel/No today aborts just the macro (`return false`). In the unified engine that
  becomes "abort the job" — should it be exactly the existing Stop path, or does aborting mid-directive
  need softer handling (no rewind, leave the machine where it is)?

Related: [[iosender-unified-streaming-engine-design]] (memory), [Architecture-Streaming-Paths.md](Architecture-Streaming-Paths.md),
[[iosender-jobcontrol-run-blocker]] (JobRunner's own extraction history/discipline to mirror).
