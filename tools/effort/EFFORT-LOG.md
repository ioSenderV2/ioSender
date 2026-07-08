# Effort log

Effort on this fork — **your hours** (active time at the computer) and **my load** (Claude: commits driven,
tokens, context windows) — from the **first commit** to now.

> **Last updated:** 2026-07-07 (through the settings-tree / `-debuglog` / `-forgetNetwork` session).
> **Covers:** 2026-05-30 (first commit) → 2026-07-07.

## How the numbers are derived — read before trusting them

- **Hours are estimated from git, not measured.** `effort-tracker.ps1` didn't run during this window, so
  hours come from **commit-timestamp session-clustering**: consecutive commits within a **90-min gap** = one
  session; each session = its span **+ a 30-min ramp** for the un-committed work before its first commit.
  Both tables below use the **same 90/30 method** so they're apples-to-apples.
- **It's a band, not a number.** It **under**counts un-committed work (research, reading code, hardware
  testing, long conversations with no commit) and **over**counts bursty AI-paced sessions (many commits in
  one window while you review). Treat totals as ±15%.
- **Exact tokens are gone.** `/cost` only reads the *current* Claude Code session; past sessions can't be
  queried after the fact. Token/window figures here are estimates at **~0.25 M tokens per commit** (reads +
  edits + builds + test iteration + docs, cache included). Re-scale linearly if you ever measure a rate.
- **Scope note:** the first table (through 06-23) counts **all repos** (ioSender + Simulator + firmware);
  the second (from 06-24) is **ioSender only** — recent Simulator/firmware activity was negligible.

---

## Part 1 — 2026-05-30 → 2026-06-23  (all repos)

First commit: `2026-05-30 10:27 "Fix build: add missing RP.Math references…"` (PR 1).

| Date | Day | Commits | ~Hrs | Top themes |
|---|---|--:|--:|---|
| 05-30 | Sat | 4 | 0.5 | build/fix, connect/sim |
| 05-31 | Sun | 6 | 1.0 | atc/macro, ui/jog |
| 06-01 | Mon | 6 | 0.7 | atc/macro, ui/jog |
| 06-02 | Tue | 42 | **9.6** | ui/jog (20), build/fix |
| 06-03 | Wed | 14 | 2.4 | ui/jog, loc |
| 06-04 | Thu | 19 | 3.6 | atc/macro, ui/jog |
| 06-05 | Fri | 10 | 4.2 | atc/macro (7) |
| 06-06 | Sat | 14 | 6.2 | tracker/docs (13) |
| 06-07 | Sun | 45 | 7.5 | tracker/docs (23), connect/sim (13) |
| 06-08 | Mon | 20 | 3.4 | connect/sim, settings |
| 06-09 | Tue | 36 | 3.4 | ui/jog (18), tracker/docs |
| 06-10 | Wed | 8 | 2.7 | settings/setup |
| 06-11 | Thu | 46 | **14.0** | settings (17), connect/sim (11) |
| 06-12 | Fri | 10 | 2.1 | settings, connect/sim |
| 06-13 | Sat | 5 | 0.6 | tracker/docs |
| 06-14 | Sun | 5 | 2.7 | atc/macro |
| 06-15 | Mon | 25 | 7.8 | atc/macro (7), loc (6) |
| 06-16 | Tue | 5 | 0.5 | atc/macro |
| 06-17 | Wed | 2 | 0.9 | loc, docs |
| 06-18 | Thu | 38 | 7.5 | tracker/docs (9), loc (8), atc (8) |
| 06-19 | Fri | 36 | **9.4** | connect/sim (11), offsets/probe |
| 06-20 | Sat | 27 | 7.3 | connect/sim (7), tracker/docs (7) |
| 06-21 | Sun | 18 | 4.5 | settings, connect/sim |
| 06-22 | Mon | 21 | 1.8 | ui/jog (7), tracker/docs |
| 06-23 | Tue | 146 | 8.8 | **loc (56)**, tracker/docs (21), connect/sim (19) |
| **Subtotal** | | **608** | **~113 h** | 25 active days |

**By week:** wk22 ~1.5 h · wk23 ~34 h · wk24 ~29 h · wk25 ~38 h · wk26 (through 06-23) ~11 h
**By repo:** ioSender **520** · Simulator **58** · firmware (iMXRT1062 + core + plugins) **~30**
**By theme:** tracker/docs 116 · localization 92 · connect/sim 86 · UI/jog 83 · settings/setup 59 · ATC/macro 51 · tooling 15 · sdcard/fs 14 · offsets/probe 12 · build/fix 19
**Shape:** clear evening peak (19:00–21:00) but real overnight activity — a steady grind, not 9-to-5. Five days carried ~half the load: 06-11 (14 h), 06-02 (9.6), 06-19 (9.4), 06-23 (8.8), 06-15 (7.8).

---

## Part 2 — 2026-06-24 → 2026-07-07  (ioSender only)

Same 90/30 method as Part 1.

| Date | Day | Commits | ~Hrs | Top themes |
|---|---|--:|--:|---|
| 06-24 | Wed | 6 | 1.7 | tracker/docs, ui/jog |
| 06-25 | Thu | 30 | 4.5 | probing (16), tools (5), startjob (3) |
| 06-26 | Fri | 5 | 2.1 | probing (3), tabs/layout, docs |
| 06-27 | Sat | 5 | 3.4 | streamer, startjob, ui/jog |
| 06-28 | Sun | 44 | **11.8** | 3d/carve (16), tabs/layout (12) |
| 06-29 | Mon | 41 | **15.0** | streamer (10), probing (10), loc (5) |
| 06-30 | Tue | 31 | 9.2 | probing (12), streamer (7), startjob (5) |
| 07-01 | Wed | 4 | 1.1 | streamer, probing |
| 07-02 | Thu | 10 | 3.3 | streamer, ui/jog |
| 07-03 | Fri | 20 | 5.4 | tracker/docs (12), ui/jog (6), connect/sim |
| 07-04 | Sat | 13 | 5.4 | tabs/layout (4), ui/jog (4), settings (4) |
| 07-06 | Mon | 15 | 4.6 | tracker/docs (5), tabs/layout (3), ui/jog (3) |
| 07-07 | Tue | 30 | 7.5 | tracker/docs (9), tabs/layout (6), connect/sim (3) |
| **Subtotal** | | **254** | **~74.9 h** | 13 active days |

**By week:** wk26 (from 06-24) ~23.4 h · wk27 ~39.4 h · wk28 (07-07) ~12.1 h
**By theme:** probing 48 · tracker/docs 34 · tabs/layout 30 · streamer 24 · UI/jog 20 · 3d/carve 18 · startjob 17 · settings 12 · loc 8 · tools 7 · connect/sim 7 · build/fix 3 · atc/macro 2 · lathe 1
**Shape:** two big days carried it — 06-29 (15 h) and 06-28 (11.8 h) — the registration-refactor + 3D-carve-view + streamer-pump push.

**What got built (highlights):** registration refactor + layout tree + one bottom run-control cluster · live 3D carve view (dexel material removal) · streamer pump as the sole cutting path · Height Map tab · probing redesign onto ProbeDefinition · Start Job workflow + Verify skew · option-matched simulator · adaptive alarm-recovery menu · Auto Square tool · settings flat tab strip + `$`-search · authoritative keyboard-jog config · high-DPI fixed-dim audit (~250 dims) · lathe wizards rebuilt · ioSenderV2 full-split docs · headless `build.ps1` + crash-log · online user manual + F1 help · localization sweep (+1545 rows / 7 locales) · Connect Network default + LAN discovery · drag-to-reorder tabs · settings empty-tree deep fix · `-debuglog` facility · `-forgetNetwork`.

---

## Grand total — first commit → now

| | Commits | ~Hours (90/30) | ~Tokens @0.25M/commit |
|---|--:|--:|--:|
| Part 1 (05-30→06-23, all repos) | 608 | ~113 h | ~152 M |
| Part 2 (06-24→07-07, ioSender)  | 254 | ~75 h  | ~64 M  |
| **Total** | **~862** | **~188 h** | **~215 M** |

- **~188 h** over ~38 active days (~5 h/active day) — honest band ~160–215 h given the ±15% and the
  under-counted hardware/testing time.
- **~215 M tokens** ≈ on the order of **~215 context-window-equivalents** (1 M each) of my working context.
  Re-scale everything if you ever pull a real per-commit rate from a live `/cost`.

---

## Provenance
Part 1 was reconstructed live in the 2026-06-26 session and existed **only as chat output** until now
(recovered from the session transcript `~/.claude/projects/c--github-ioSender/3d9c7015-…jsonl`). Part 2 was
computed from `git log` on 2026-07-07. Neither is measured time.

> **Hours are measured from here on:** `effort-tracker.ps1` now runs at login (10-min idle gap) and writes
> `sessions.csv`. Next roll-up can use real keyboard time for anything after 2026-07-07.
