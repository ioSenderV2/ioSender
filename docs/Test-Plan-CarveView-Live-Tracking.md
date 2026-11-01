# Test plan — can the carve view track during a job?

**Status:** planned, not yet implemented. Nothing in the app has changed.
**Written:** 2026-10-30.

## The question

The 3D carve view freezes completely while a job streams. Can it track instead — and if the full
carve is too expensive, can the tool cone track on its own?

**This is not about whether the guard's comment is stale.** That is already settled: the comment in
`CarveView.ScheduleBuild`/`UpdateTool` says acks arrive via `Dispatcher.BeginInvoke` so
`ResponseReceived -> SendNextLine` runs on the dispatcher. That was true when the guard was written
(965b9db8, 2026-06-29). `StreamPump` then moved the send/ack flow control onto its own thread, and
`SerialStream` now raises `ReplyClassified` **inline on the read thread**, explicitly "bypassing the
UI dispatcher so flow control never waits on a busy UI". The old `Dispatcher.BeginInvoke` line is
commented out at `SerialStream.cs:517`.

So the mechanism the guard cites no longer applies **by that route**. What is NOT established is
whether the freeze is still needed for other reasons. That is what this run decides.

## What still runs on the UI thread during a job

Worth having in front of you, because these are what live tracking would compete with:

- every reply's `DataReceived` handling, marshalled by `Comms.PostTo` -> `context.Post` at Normal
  priority (~5/s at a 200 ms poll, plus messages)
- the whole handler fan-out off those replies: view-model properties and every binding on them — DRO,
  state, overrides, run strip
- `StreamPump`'s display marshals (grid "Sent" marks, `BlockExecuting`, `ScrollPosition`), coalesced
  at **Background** priority; the pump never waits on them draining
- the `JobControl` state machine
- console/message rendering when visible

**Not** on the UI thread: the serial read loop, the `ReplyClassified` tap, `StreamPump`'s pacer
thread, and the poller's own `?` write (`System.Timers.Timer`, so a thread-pool thread).

## The three arms

`CarveView.UpdateTool` already separates the two costs and labels them:

```
toolCone.Origin = p;         // track the live cutter (cheap) when idle
...
CarveTo(p);                  // live machine motion carves the stock too
PushMesh();                  // status-poll rate is low, so push each update
```

with "Live material removal (CarveTo mutates the stock mesh, PushMesh re-publishes it) is the
expensive part". So a binary frozen/unfrozen test would miss the interesting answer.

| Arm | Behaviour | Why it is in the run |
|---|---|---|
| **A** | frozen (today) | the baseline every other number is read against |
| **B** | cone tracks, no carve | the cheap half — live tool tracking, which is what an operator notices missing |
| **C** | cone + carve | the full thing |

**Run A, then C. If C passes, B is unnecessary** — B only earns a run if C fails.

## Implementation (done)

1. ~~A launch flag `-carvelive=off|cone|full`~~ — **superseded 2026-10-30 by two persisted settings**
   on the carve view's own **View options**, which is where an operator's own choice belongs rather
   than in a flag they must remember every launch:

   | setting | arm | cost |
   |---|---|---|
   | *Update tool position in 3D view during a carve* | B | one Origin assignment per status report - "cheap" |
   | *Update material removal in 3D view during a carve* | C | `CarveTo` + `PushMesh` - "the expensive part" |

   Both default **off**, so the shipped behaviour is unchanged. The second is gated on the first in
   the dialog **and** in its getter - a stored true with tracking off would otherwise spring back to
   life the moment tracking was re-enabled, which is not what unticking the parent asked for.

   The arms are now selected by ticking boxes rather than by relaunching, which also removes the
   original worry: arms no longer need separate launches at all, so a paired A/C/A/C comparison is
   just four short runs with two clicks between them.
2. **Feed Hold latency instrumentation.** PollDiag measures replies *arriving*; it cannot see
   operator input being *serviced*. A log line on entry to the Feed Hold handler, read against the
   wire log's timestamp for the hold byte going out, gives the interval. This is the strongest
   remaining argument for the guard — the button handler is on the UI thread, and Feed Hold has been
   starved for a whole job before — so a clean marshal number alone does not clear it.

## Build

🔴 **Release, with `-debuglog=poll`. Not Debug.** `DebugLog.Init` has a `#if DEBUG` block that
force-enables tracing with a **null category filter** — every category, no flag needed. A Debug run
would measure the instrument rather than the app, and flood the file. Release stays opt-in, so
`-debuglog=poll` gives exactly this one instrument.

## What to record

PollDiag writes one line a minute to the logs folder (`%AppData%\ioSender\logs`, or `logs` under the
config dir), with **`latest_debug.log`** hard-linked at the top level.

| Field | What it decides |
|---|---|
| **marshal: n / mean / max** | **the decisive one.** Stamped before `context.Post`, read inside the callback — precisely how long a reply waited for the UI thread. Its own header: "rises with UI-thread saturation and nothing else." |
| send: n / mean / max | the poller firing and writing. Should stay near `PollInterval` (200 ms). If this moves, the poller is affected. |
| write: mean / max | send-path contention on the timer thread. Expect unchanged. |
| rx: n / mean / max | reply arrival gaps. |
| process: mean / max | cost of the handler fan-out once it runs. |
| **subs** | subscriber counts on the view model's hot events. **Must be flat.** A climbing count means handlers accumulating on tab activation, which would confound the whole comparison. |

**The inline outlier lines matter more than the means.** PollDiag warns immediately on marshal
> 250 ms and rx gap > 1000 ms (capped at 20 per window, with the suppressed count carried into the
periodic line). It exists because a single 7.6 s stall got averaged into "max=259ms" by the first
version of the instrument. Read the warnings before the averages.

Also record, per arm: the Feed Hold interval from (2) above, and the operator's own observation of
whether motion stuttered.

## Controls — get these wrong and it is three anecdotes, not a comparison

- **Same program, stock, tool and feeds** across arms. Prefer dense short moves: worst case for both
  the planner buffer and the carve mesh.
- 🔴 **Restart ioSender before each arm, and run each arm for the same wall-clock time.** PollDiag
  exists because marshal latency degrades with **uptime**, not workload — measured drifting from
  0.264 s to 0.758 s over one ~11 hour session against a 200 ms poll. Unequal uptime would swamp the
  signal entirely.
- **At least 3 minutes per arm**, so there are three report windows; the numbers are per-minute
  aggregates.
- 🔴 **DISCARD THE FIRST REPORT WINDOW, and start the job only after it has passed.** Measured
  2026-10-30 on the very first launch carrying the probe: within six seconds of startup it logged UI
  lateness of 476 ms and 189 ms and marshal stalls of 253 ms and 347 ms - all of it connect handshake,
  settings load and view construction, none of it a job. Those four lines would otherwise land in
  arm A's first window and set a baseline that no arm could fail against.
- **The 3D view must be on screen** for arm C — `UpdateTool` returns early on `!IsVisible` before the
  carve, so an off-screen view silently measures arm B.

## Acceptance criteria — decided before the run, not after

🔴 **Judge the SIMULATOR phase on the A-to-C DELTA, and the HARDWARE phase on the absolute
thresholds.** See "What the simulator changes" below for why: the sim inflates the absolute numbers,
but it inflates them identically in both arms, so the delta survives and the absolutes do not.

**Phase 1 (simulator) — an arm passes if:**

- marshal mean and max no worse than arm A's by more than a small margin
- send gap mean no worse than arm A's
- subscriber counts flat across the arm's windows
- no NEW marshal outlier warnings that arm A did not also produce

**Phase 2 (hardware) — an arm passes if, additionally:**

- no marshal outlier warnings at all (none over 250 ms)
- send gap mean within ~10% of `PollInterval`
- Feed Hold interval not materially worse than arm A's
- no stutter the operator can see or hear

A phase-2 fail means the guard stays. A phase-1 fail does **not** on its own — see below.

## Phases

**Phase 1 — simulator.** Arms A and C against the option-matched grblHAL simulator. No cutter, no
scrap, no risk.

🔴 **A pass here is strong evidence; a FAIL here is not conclusive and must not end the
investigation.** Everything the simulator changes biases the measurement toward looking *worse* than
hardware (see below), so a sim failure may be the simulator's own cost rather than the carve view's.
A phase-1 fail means "confirm on hardware before the guard stays on this account", not "done".

**Phase 2 — hardware, in scrap.** Only if phase 1 is clean. This is the only thing that can answer
"does motion actually stutter" and give a real Feed Hold interval, neither of which the simulator
can produce. Scrap material, because arm C is the arm that could spoil a part.

## What the simulator changes

Checked rather than assumed, because the plan's validity rests on it.

**The architecture does NOT change.** `SimulatorManager` starts the sim as a local process and
connects a `TcpClient` to `127.0.0.1`, so the live link is `TelnetStream` — and `TelnetStream` raises
`ReplyClassified` with the same "bypassing the UI dispatcher" comment as `SerialStream`. All four
stream classes carry the tap. Same `Comms.PostTo` marshal to the UI thread, same read-thread tap,
same `StreamPump` pacer, same poller timer. What is being measured has the same shape.

**Three things do change, and all three bias the same way — toward looking WORSE than hardware:**

| | Effect | Direction |
|---|---|---|
| **CPU contention** | the sim is a process on the same box, competing for CPU with the very UI thread under measurement; a real controller is a separate MCU doing none of that | inflates marshal latency |
| **Reply density** | status reports are sender-driven at `PollInterval` so that rate is identical, but `ok` replies come back as fast as the sim can parse — faster than a real board at 115200 — and every reply still gets a `PostTo` to the UI thread | more UI marshals per second |
| **Write cost** | a localhost socket write is far cheaper than real serial | PollDiag's `write` figure is optimistic — do **not** compare it across phases |

**Why the run survives this:** both arms carry the identical simulator overhead, so the **A-to-C
delta is valid** even though the absolute numbers are inflated. Hence the split acceptance criteria
above — delta for phase 1, absolutes for phase 2.

**One unknown, not answerable from the code:** whether the sim honours feed rates in real time or
runs as fast as it can. If it runs faster, position changes faster per wall-clock second and the
carve does more work than hardware would — conservative again, but it means the carve load is not
representative. Check it during the run by comparing the program's wall-clock duration against what
its feeds predict.

## What would invalidate the run

- a Debug build (see above)
- unequal uptime between arms
- a different program between arms
- climbing subscriber counts
- arm C run with the 3D view off screen
- reading the means without reading the outlier warnings

## Results so far — arm A only, 2026-10-30

Two arm A runs on the simulator, Phil's 67,555-line program (250 depth passes at 0.1 mm on 25 mm
stock), G54 at X282 Y-570 Z-20.

| | 2.3 h run, 141 windows | 10 min run, 12 windows |
|---|---|---|
| send mean (median) | 211 ms | 210 ms |
| marshal mean (median) | 23.4 ms | **35.0 ms** |
| marshal max (median) | 248 ms | 274 ms |
| process mean (median) | 0.0 ms | 1.1 ms |
| uilate mean (median) | 20.9 ms | **26.8 ms** |
| uilate max (median) | 226 ms | 236 ms |
| uilate breaches / min | 3.1 | **4.3** |
| marshal stalls / min | 1.1 | **3.0** |
| Feed Hold | **locked out once** | **worked every time** |

**The simulator honours real feed rates** - 10 minutes covered under 5% of the program. That answers
the open question above.

**No uptime drift.** Over 2.4 h, marshal mean went 25.0 -> 24.0 and uilate mean 21.9 -> 21.1. The
degradation PollDiag was originally written for is not reproducing in these sessions.

🔴 **THE BASELINE IS NOT REPEATABLE ENOUGH FOR THE PLANNED COMPARISON.** Two arm A runs of the same
program differ by ~50% on marshal mean (23.4 vs 35.0) and ~28% on uilate mean. Any arm C effect has
to be bigger than that gap to be visible, and there is no reason to assume it will be. **Before
running arm C, decide how to handle this** - repeated alternating short runs (A, C, A, C) rather than
one of each, so the comparison is paired rather than between two single samples.

## Discovered on the way: Feed Hold disables itself while a hold is in flight

Not the carve view's doing, and worth its own fix. The gate log shows:

```
21:40:06.793  FEED HOLD pressed - UI input lateness was 1ms when this handler ran
21:40:06.861  feed hold gate -> disabled (armed=False, controller-disabled=False, state=Run)
21:40:12.758  feed hold gate -> ENABLED  (armed=True,  controller-disabled=False, state=Hold)
```

The press is serviced in 1 ms, then the button greys out for **six seconds** while the machine
decelerates from Run to Hold through a full planner buffer. A second press in that window does
nothing, which from the operator's chair is indistinguishable from the button not working - and on
2026-10-30 the operator read it exactly that way and reset the machine instead.

**UI-thread lateness did NOT cause it**, and the measurements say so plainly: the 10-minute run had
*worse* lateness on every measure and Feed Hold worked every time, while the long run's extreme
spikes (1112 ms, 1294 ms) landed at 19:25 and the lockout was at ~21:33. A theory that the dispatcher
was to blame survived two turns on nothing but plausibility; the gate log killed it in one run.

**The fix is a label, not a latency**: while a hold is in flight the control should say so
("Holding...", or similar) rather than simply disabling, so a press that WAS taken does not look like
one that was missed.

## Afterwards

Whatever the outcome, the stale comment in `CarveView` gets corrected — a comment that justifies a
restriction with an obsolete mechanism is worse than no comment, because it stops the next person
asking. Deliberately left until the dust settles so it can record what was measured rather than what
was expected.
