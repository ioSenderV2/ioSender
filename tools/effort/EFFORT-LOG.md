# Effort log

Tracks effort on this fork: **your hours** (active time at the computer) and **my tokens** (Claude),
with a focus on tokens spent **autonomously while you're away**.

## Your hours
Auto-captured by [`effort-tracker.ps1`](effort-tracker.ps1) into `sessions.csv` — one row per active
stretch, where a session **ends after >5 min of keyboard/mouse inactivity** and the next input starts a new
one. Read the CSV, or paste a roll-up snapshot here when you want one (I can add a `summarize.ps1`).

## My tokens (Claude)
I can't read my own token counts mid-conversation, so the **exact** figure comes from Claude Code's **`/cost`**.
What I log here is each **autonomous stint** (work done while you were away) so the session cost can be
attributed to "while away" vs "together".

### Autonomous stints
| Date (local) | Work done while you were away | Exact tokens |
|---|---|---|
| 2026-06-25 night | Probe definitions: type-driven editor (3D probe / touch plate / tool setter / edge finder) → corner-touch-plate **lip width** + plain-language relabel of every field → **"Edit motion params…"** sub-dialog (essentials-only main dialog, bit diameter for touch plate). Machine Setup Wizard footer rework + "5 · Probe definitions" box + Preview dialog. **This effort-tracking tooling.** | run `/cost` |
| 2026-06-26 | **Main-tab restructure** (commit `12ff1e5`): Settings = Grbl + App sub-tabs; new **Machine Setup** top-level tab (Overview + 6 color-coded step tabs + startup gate); new **Tools** container (tool table, stepper cal/scratch, surface spoilboard, Trinamic, PID); inline probe + macro-status grids. Load Stock pcorner blocker root-caused + fixed (N-prefix on `O<…> CALL` broke O-word routing) — shipped + hardware-verified (`0b6d2cb`). | run `/cost` |
| 2026-06-27 | **Tools streamer routing + Auto Square** (`69b3cbd`; firmware `80c8b83`): MacroProcessor routes big generated programs through the real job streamer (fixes surfacing hang/UI-freeze); machine-referenced Surface Spoilboard; new **Auto Square** tool (Phil Barrett offset, ganged-axis `$170+axis`). `AtcMacros.GetStatus` hardened. | run `/cost` |
| 2026-06-28 (mostly together) | Hardware-fix session: `$13` jog-units fix, content-based **streaming** so Feed Hold/Stop always work, probe-stream throttle, Surface Spoilboard (Reuse Z0 / Finish pass / abbreviated dry-run), Squareness gauge, pcorner feeds-from-probe-definition, Fusion add-in measured-stock→(STOCK)/ceil + log fix, pinned-flyout reopen. Commit/push/route. **ProposedPRs catalog** (PRs 28–31 + reframe + PDF regen, pushed). **Registration-architecture design spec** ([`docs/Architecture-Registration-Refactor.md`](../../docs/Architecture-Registration-Refactor.md)) + 5 design decisions; began Phase 0 (config sections). | run `/cost` |

> Add the `/cost` number per stint (or tell me to estimate from tool-call / build counts).
> **Hours not auto-captured** for these stints — `effort-tracker.ps1` wasn't running, so there's no
> `sessions.csv`. Start it (see [README](README.md)) to capture kbd hours going forward; for the sessions
> above I can only estimate from wall-clock if you tell me the rough start/stop times.
