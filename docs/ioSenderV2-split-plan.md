# ioSender V2 — full-split migration plan

Draft plan (written the night before). Goal: cleanly separate the **PR‑era archive** (the existing forks,
frozen at a cutoff, upstream‑submittable) from **ioSender V2** (new offshoot forks, an all‑in‑one product).
You forked each upstream **once**; a cutoff label marks where upstream compatibility ends; V2 branches off
there as a separate offshoot.

---

## 1. End state

| | **PR‑era archive** (existing forks, frozen) | **ioSender V2** (new forks) |
|---|---|---|
| Repos | `stevenrwood/ioSender`, `/Simulator`, `/core`, `/iMXRT1062`, plugin forks | `stevenrwood/ioSenderV2`, `/SimulatorV2`, … (names TBD — see §2) |
| Holds | `master` + `pr/*` single‑feature branches + `apply-prs`/`forks.json`/`gen-manifest.py` + `Overview.html` + `ProposedPRs.html` | one all‑in‑one build (seeded from today's `integration`) + `FeaturesAndFixes.html` + a superset `Overview.html` |
| Rooted at | the single upstream fork point | the cutoff state |
| Upstream | still theoretically submittable (that's its whole reason to exist) | severed — no pretense |
| Activity | frozen at the cutoff label; touched only for a back‑port | all ongoing V2 development |

Today's mixing (one repo whose `master`/`pr/*` is PR‑era but whose `integration` is already V2) is what the
split resolves.

---

## 2. Decisions to lock first (blocking)

1. **Cutoff definition.** The PR‑era archive = `master` + the `pr/*` branches as they stand; V2 root = the
   current `integration` tip. So the "cutoff" is conceptual, not a single divergence commit. Action: on each
   existing fork, tag the current `integration` tip `ioSenderV2-root` (so V2's origin is findable) and tag the
   PR‑era tip `pr-era-cutoff`. *(No content moves at tagging time — tags are just labels.)*
2. **V2 repo names.** Proposal: `ioSenderV2`, `SimulatorV2`, `iMXRT1062V2`, `coreV2`, `Plugin_*V2`. Alternative:
   keep the same names under a new GitHub org (e.g. `ioSenderV2/*`) so the code‑level names don't change. **Decide.**
3. **V2 default branch.** `master` vs `main` vs `integration`. Proposal: `master` (V2's own baseline == its
   product; no separate integration needed since there are no composable branches anymore).
4. **History.** Keep full history in V2 (shared back to the upstream fork point — preserves provenance and
   `git blame`) vs. a squashed clean root at cutoff. Proposal: **keep full history** — V2 *is* the continuation.
5. **`apply-prs` home.** It is PR‑era tooling → it belongs in the **archive**, not V2. So V2 drops
   `tools/apply-prs.py`, `forks.json`, `gen-manifest.py`. (Reverses tonight's "keep it in the tree" — that was
   right for one‑repo, wrong once the archive exists to hold it.)
6. **Firmware is special.** The firmware isn't one repo with an `integration` branch — it's a superproject
   (`iMXRT1062`, branch `srw/local-build-config`) + submodule forks (`core` @ `srw/combined`, `Plugin_networking`,
   `Plugin_SD_card`) + per‑PR branches for upstream. Its "PR‑era" = the per‑PR fork branches; its "V2 build" =
   `srw/local-build-config` + `srw/combined`. **Decide:** full V2 repos for firmware too, or leave firmware as
   one repo with a `pr-era-cutoff` tag (it's already effectively all‑or‑nothing). Leaning: **tag‑only for firmware**
   (lowest risk; the per‑PR branches already coexist cleanly with the build branch), full split for ioSender + Simulator.
7. **Doc naming (see §3).** Preference: keep **`Overview.html`** in V2 as a *superset* of the V1 Overview.

---

## 3. Docs & the lineage diagram

**Naming (recommended):**
- **Archive** keeps its `Overview.html` (V1 cross‑fork PR‑era map) + `ProposedPRs.html`, untouched — plus a one‑line
  "→ this became **ioSender V2**" pointer at the top.
- **V2** gets a **superset `Overview.html`** (the map: lineage diagram + the V2 cross‑fork runtime wiring) **and**
  `FeaturesAndFixes.html` (the changelog). `Overview.html` as the name is right: V2's Overview is a strict superset
  of V1's (it adds the V2 half + the runtime picture).

**The diagram** (lives in V2's `Overview.html`; a self‑contained inline SVG, build‑doc style):

```
    UPSTREAM (frozen origin)                        (forked ONCE)
    ┌───────────────────┐
    │ terjeio/ioSender  │────────┐
    │ grblHAL/Simulator │──────┐ │
    │ grblHAL/core+drv  │────┐ │ │        ═══ PR-era archive ═══            ┈┈┈ cutoff ┈┈┈
    └───────────────────┘    │ │ └──▶ stevenrwood/ioSender  (master + pr/*  ┊
             ▲               │ └────▶ stevenrwood/Simulator   + apply-prs +  ┊   ⇢ ┌──────────────┐
             │               └──────▶ stevenrwood/core…       ProposedPRs)   ┊     │ ioSender V2  │──▶ link:
             │                                                (submittable)  ┊     │  (offshoot)  │  FeaturesAndFixes.html
             └────────  ✗  no PRs back — upstream no longer submitted to  ───┘     └──────────────┘
                                                                            (dotted line = the cutoff / offshoot)
```

Says at a glance: **one** fork from each upstream → the PR‑era archive (up to the cutoff) → a **dotted line**
across the cutoff to the **ioSender V2** box, which is a **hyperlink to `FeaturesAndFixes.html`**. Upstream is a
frozen origin with the return path severed. *(Build the actual SVG when V2's `Overview.html` is created.)*

---

## 4. Migration steps — ioSender as the pilot

Do ioSender end‑to‑end first, verify, then repeat for Simulator; firmware per the §2.6 decision.

1. **Tag** the existing fork: `pr-era-cutoff` (archive tip) and `ioSenderV2-root` (current `integration` tip). Push tags.
2. **Create** `stevenrwood/ioSenderV2` on GitHub (empty, no auto‑init).
3. **Seed V2:** `git remote add v2 …/ioSenderV2.git` → `git push v2 integration:master` (+ `--tags` for V2‑relevant tags).
   V2's `master` == today's `integration`, full history.
4. **Set** V2 default branch = `master`; enable Actions.
5. **Clean V2 of PR‑era artifacts:** remove `tools/apply-prs.py`, `forks.json`, `gen-manifest.py`; add the superset
   `Overview.html` (with the diagram); `FeaturesAndFixes.html` already present. Update `readme.md`/`CLAUDE.md` sibling
   references to the V2 repos.
6. **Repoint cross‑repo code:** `SimulatorManager.SimulatorRepo` `stevenrwood/Simulator` → `stevenrwood/SimulatorV2`
   (and the workflow file / dispatch + release‑download URLs). Confirm the `GH_TOKEN` scope covers the new repo.
7. **Freeze the existing fork:** delete (or reset‑to‑cutoff) its `integration` branch — V2 lives in `ioSenderV2` now;
   keep `master` + `pr/*` + tooling + `Overview.html`/`ProposedPRs.html` as the frozen archive. Add the "→ ioSender V2"
   pointer to its readme.
8. **Local remotes:** repoint your working clone's `origin` to `ioSenderV2` for ongoing work (keep the archive as a
   second remote if you want to reach it).
9. **Verify:** clone `ioSenderV2` fresh → build Release (EXIT 0) → connect + smoke‑test → confirm the matched‑sim
   feature dispatches/downloads against `SimulatorV2`.

## 5. Simulator (repeat)

Archive keeps `master` + `pr/sim-*` + `apply-prs`. **`SimulatorV2`** seeded from `integration` — it carries the
patched sim + the **`build-matched-sim.yml`** CI. Move the workflow + recreate the `sim-<sig>` releases on
`SimulatorV2`; the sim‑matching feature in ioSenderV2 must point at `SimulatorV2`. (This is the CI/token‑bearing
piece — do it carefully and re‑verify the dispatch → per‑sig release → public download chain.)

## 6. Firmware (per §2.6 decision)

If **tag‑only:** tag `pr-era-cutoff`, keep everything; the per‑PR branches are the archive, `srw/combined` +
`srw/local-build-config` are the V2 build. If **full split:** V2 repos for `core`/`iMXRT1062`/plugins, repoint the
`.gitmodules` submodule URLs + superproject pins, move `firmware.yml`.

---

## 7. CI / cross‑reference checklist (things that break if missed)

- `CNC Controls/SimulatorManager.cs` — `SimulatorRepo`, `MatchedWorkflowRef`, release/dispatch URLs → `SimulatorV2`.
- `SimulatorV2` — the `sim-<sig>` release cache must exist there (re‑dispatch a build or copy releases).
- `GH_TOKEN` — `workflow` scope on the V2 repos.
- Firmware `.gitmodules` URLs + superproject submodule pins (if firmware is split).
- Hardcoded repo URLs in `readme.md`, `CLAUDE.md`, and the SRW helper docs.
- Local clone remotes; any saved CI secrets/tokens on the old repos.
- Memories that name `stevenrwood/Simulator`/`ioSender` (e.g. [[iosender-matched-simulator]]) — update to V2.

## 8. Rollback

Everything is additive until step 7 (freeze). If a V2 repo is wrong, delete it and re‑seed — the source
`integration` is untouched until you deliberately freeze it. Do the freeze **last**, only after V2 builds + runs.

---

## Open questions for the morning
- V2 repo names / new org? (§2.2)
- Firmware: full split or tag‑only? (§2.6)
- Confirm the diagram content + that `Overview.html` (superset) is the right home/name. (§3)
