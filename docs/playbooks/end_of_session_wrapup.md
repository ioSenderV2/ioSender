# End-of-session wrap-up

> **Shadows: `claude-hub/playbooks/end_of_session_wrapup.md`** — that file holds the shared
> shape (the ordering, and why the conversation-log step is last). This one replaces it for
> ioSender because every step below names this project's branches, remotes and release tooling.

**When:** work for the session is done and the user is about to `/clear`.
**Memory context:** `iosender-end-of-session-convolog.md`.

Run these **in order**. The conversation-log step is LAST because the user's next prompt is almost
always `/clear`.

## The sequence

1. **Everything committed** on `integration`.
2. **Changelog updated** — new `#N` entry in `Overview.html` + `Overview.pdf` regen.
   → [add_changelog_entry.md](add_changelog_entry.md), [regenerate_overview_pdf.md](regenerate_overview_pdf.md).
3. **Pushed all the way to remote** — `origin/integration` **and** `v2/master`. → `tools\push-all.ps1`
   (checks ahead/behind, pushes both, verifies both refs land; `-DryRun` to preview).
3.5. **Wait for the rolling-release CI build to finish** — the push to `v2/master` triggers
   `.github/workflows/release.yml` (`Rolling release`) on `ioSenderV2/ioSender`. It can fail for
   reasons nothing local catches (clean-runner build vs. a locally cached `-restore`). Run
   `tools\wait-for-release.ps1` and **wait for it to exit** before moving on - it polls until the run
   for this push's commit completes and exits 0/1 on success/failure. **If it fails, stop and
   surface the failure** (link + a look at the log) instead of writing the summary as if everything
   shipped clean - don't silently proceed to steps 5/6 on a red build.
   On success it also **fast-forwards onto the changelog stamp the release just pushed** - `release.yml`
   ends by committing the version stamps and pushing them to master, so without this the next push (3.6)
   is rejected with "fetch first". Nothing to do by hand; if it reports that master has *diverged* (more
   than the stamp), stop and look rather than merging blind.
3.6. **Bump the local dev-build version display** — once 3.5 succeeds, update `legacyVersion` in
   `ioSender XL\ioSender XL\MainWindow.xaml.cs` (~L66) to the version that was JUST published + 1, so
   local/dev builds show the upcoming version rather than a stale one. This constant is the fallback used
   whenever `BuildInfo.Version == "dev"` (i.e. every local build - only CI's `release.yml` stamps
   `BuildInfo.cs` with a real version). Compute the next number the same way
   `tools\cut-release.ps1` does: latest GitHub release tag `major.minor`, minor+1 (e.g. just shipped 2.22 →
   set `legacyVersion = "2.23"`). Commit standalone with `[skip release]` (same convention as the TOC-stamp
   commits) so this metadata-only change doesn't itself trigger a release.
4. **New docs published to gh-pages** (only if the manual changed).
   → [publish_manual_site.md](publish_manual_site.md).
5. **Write the end-of-session summary to chat** — the recap of what shipped (the message the user reads).
   Include the CI result from step 3.5.
6. **THEN capture the conversation log** — → [capture_conversation_log.md](capture_conversation_log.md).
   One command, ~1 s: it writes this session's HTML into `claude-hub\conversations\ioSender\`,
   appends its record to that project's `sessions.json`, re-renders that index plus the
   cross-project roll-up, and commits itself into `claude-hub` (no push).
   **Running it is what defines the session boundary** — everything since
   the last capture is this session — so don't skip it, and don't run it twice (use `-Amend` if you
   captured early and kept working). No separate `build-session-index.ps1` step any more.

## Ordering that matters (steps 5 → 6): the wrap-up is TWO turns

> ⚠️ **CORRECTED 2026-08-08 — this supersedes the same-message flow described below, which lost the
> summary TWICE.** Mid-turn prose (text emitted *between* tool calls in one turn) is **not reliably
> persisted** to the transcript `.jsonl`. Two separate summary attempts written mid-turn — prose, then
> the capture call in the same turn — both vanished; the transcript kept the surrounding `tool_use`
> entries but no text block, verified by parsing the raw `.jsonl` both times. Short mid-turn status
> lines sometimes survive; long prose did not. **Only the TURN-FINAL message is guaranteed captured.**

So:

1. **Turn A** ends with the session summary as its **final message — no tool calls after it.** Tell the
   user plainly that the capture still has to run, and that anything they send will trigger it.
2. **Turn B** (the user says anything) runs the capture as the **LAST ACTION OF THE TURN, and writes
   nothing after it.**

> 🔴 **Why nothing after it (user, 2026-08-12) — this is the orphan bug's actual cause.** The stray
> 1-2 turn fragments the capture keeps having to fold backwards are **just the messages written after
> the capture ran**: a verification report, a sign-off, a "captured N turns" note. They land between
> the end of one session and the start of the next and belong to neither.
> *"When you do the capture don't do anything else, don't send any more output to the messages. I will
> see when it's done and do the `/clear` at that point."*
> Say everything beforehand — the summary in turn A, any commentary before the tool call. The user
> reads the tool result themselves.
>
> **Consequence for the verify step:** "grep the HTML before reporting success" cannot be done as prose
> afterwards without recreating the bug. Never report "captured" from an exit code alone either — that
> chained two false claims once, including wrongly "correcting" the user, who had it right. The way out
> is to **build the check into `convo-sessions.ps1`** (does the final turn's text appear in the HTML?)
> so the script itself answers it. Until then: verify in the *next* session, not after the run.
>
> Use **`-Amend`** only when a capture already ran for THIS sitting and turns need folding into it;
> `-Amend` extends the most recent session, so on a first capture it would wrongly extend the previous
> one.

The original reasoning still holds and is why `-Amend` works at all: the capture reads the transcript from
disk, and Claude Code flushes an assistant message's **text** before running a tool call in that same
message — so text written earlier *in the message* than the capture call is already on disk. What the
2026-08-08 incident showed is that this holds for the message's **final** text block, not for prose
sandwiched between tool calls. Never run the capture first and write the summary as trailing text — that
pushed the summary into the following run, which is the original bug this ordering fixed (2026-07-08).

## Ready command (step 3.5)

```powershell
powershell -ExecutionPolicy Bypass -File tools\wait-for-release.ps1
```

## Ready command (step 6)

```powershell
powershell -ExecutionPolicy Bypass -File c:\github\claude-hub\tools\convo-sessions.ps1
```

## Notes

- This is a one-shot at end-of-session, **not** a per-commit routine and **not** a git hook.
- **The version bump commit is the LAST push of the session.** After 3.6, `push-all` again. That second
  push is expected and is not a sign anything went wrong.
- **A push that carries no unstamped changelog entries does not spam the releases page.** `release.yml`
  runs on it, but its first step computes the changelog delta and every later step is conditional on
  `hasChanges` - so it skips the build, the publish and the stamp. Verified on 2026-08-03: the run for
  the v2.38 recovery merge succeeded with `Publish release v2.39` **skipped**, leaving exactly one
  release with its tag and asset untouched. Don't try to "fix" such a run.
