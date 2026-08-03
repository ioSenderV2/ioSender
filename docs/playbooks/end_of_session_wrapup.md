# End-of-session wrap-up

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
3.55. **Pull the changelog stamp the CI just pushed** — `git fetch v2 && git merge --ff-only v2/master`.
   **This step is not optional and its absence used to break the next push every single time a release
   actually published.** `release.yml`'s final step is
   `git commit -m "chore: stamp vN changelog entries [skip release]"` + `git push origin HEAD:master`,
   so the moment 3.5 reports success your local branch is exactly one commit behind `v2/master`. Make the
   3.6 commit without pulling that first and `push-all` is rejected on `v2/master` ("fetch first") - which
   is how `a975fa8` (v2.36) and the v2.38 release both ended up needing a recovery merge.
   `--ff-only` is deliberate: the stamp should be the only thing there, so if it refuses, something else
   pushed and you want to look rather than auto-merge. In that case merge explicitly and say so in the
   message (precedent: `a975fa8`) - do NOT rebase, since by this point step 3 has already pushed your
   commits to `origin/integration`.

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
   One command, ~1 s: it writes this session's HTML, appends its record to `sessions.json`, and
   re-renders `ClaudeConv\index.html` (elapsed/kbd time/turns/tokens/TOC#/release per session, linking
   to each saved conversation). **Running it is what defines the session boundary** — everything since
   the last capture is this session — so don't skip it, and don't run it twice (use `-Amend` if you
   captured early and kept working). No separate `build-session-index.ps1` step any more.

## Ordering that matters (steps 5 → 6): put the summary BEFORE the capture, in the SAME message

The capture reads the session transcript from disk. Claude Code flushes the assistant message's **text**
to the transcript **before** it runs a tool call in that same message — so any text written *earlier in the
message than the capture call* is already on disk and gets captured. Therefore:

- **Write the full end-of-session summary as prose first, then make the capture the LAST action of the
  same message.** The summary lands in *this* session's log, not the next run's. (Verified 2026-07-08 with a
  marker-phrase test.)
- The old flow ran the capture and *then* wrote the summary as trailing text — which pushed the summary to
  the following run. Don't do that.

## Ready command (step 3.5)

```powershell
powershell -ExecutionPolicy Bypass -File tools\wait-for-release.ps1
```

## Ready command (step 6)

```powershell
powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1
```

## Notes

- This is a one-shot at end-of-session, **not** a per-commit routine and **not** a git hook.
- **A recovery merge does not spam the releases page.** `release.yml` runs on it, but its first step
  computes the changelog delta and everything after is conditional on `hasChanges` - with the entries
  already stamped it skips the build, the publish and the stamp commit. Verified on the v2.38 merge:
  run succeeded, `Publish release v2.39` skipped, still exactly one release. So don't "fix" such a run,
  and don't force `[skip release]` into a merge message to prevent it.
- **The version bump commit is the LAST push of the session.** After 3.55 + 3.6, `push-all` again. That
  second push is expected and is not a sign anything went wrong.
