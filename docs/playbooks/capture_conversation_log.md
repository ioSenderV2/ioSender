# Capture the conversation log

> **Extends: `claude-hub/playbooks/capture_conversation_log.md`** — read that for the whole
> procedure (what gets captured, how the session boundary works, the modes, the ordering rule).
> Only the ioSender-specific bits are below.

**When:** the final step of end-of-session wrap-up ([end_of_session_wrapup.md](end_of_session_wrapup.md)),
before the user `/clear`s.
**Memory context:** `iosender-end-of-session-convolog.md`.

## Ready command

```powershell
cd c:\github\ioSender
pwsh -ExecutionPolicy Bypass -File c:\github\claude-hub\tools\convo-sessions.ps1 -Project ioSender
```

**Both halves of that are load-bearing, and both were wrong here until 2026-10-29.**

**`-Project ioSender` is not optional.** This page used to say "`-Project` defaults to `ioSender`,
so there is nothing else to pass". It does not. It defaults from the **current directory**
(`Resolve-ProjectFromCwd` in `claude-hub/tools/convo-sessions.ps1`), not from the playbook that
names it. On 2026-10-28 a wrap-up that ran hub commands first left the shell in
`c:\github\claude-hub`, so the bare command captured a **claude-hub** session and amended an
unrelated hub record while this project's 58 turns stayed unfiled — and it printed `OK` and
committed, exactly as a correct run does. The `cd` is the belt to that suspenders.

**`pwsh`, never a bare `powershell`.** A bare `powershell` on Windows is **5.1**, whose
`ConvertTo-Json` formats with four spaces and doubled colons. A capture run that way rewrites
`sessions.json` wholesale — ~45% of the bytes — which turns `archive-check.ps1` red and makes two
machines on different interpreters fight over the file on every session. That is exactly what
happened in hub commit `f101d74` (traced 2026-10-27, repaired in `7af7a9f`/`bcab3c3`, and
`claude-hub/tools/manifest-reformat.ps1` exists to repair it again). **This line was the cause** —
the shared playbook said `pwsh` in all four places; this replacing copy said `powershell`.

It writes into `claude-hub\projects\ioSender\conversations\`, re-renders that project's index plus
the cross-project roll-up, and commits itself into `claude-hub` (no push).

*(This page named the pre-reorganisation location — conversations\ioSender directly under the hub
root — in three places until 2026-10-29, all stale since the hub moved to a `projects/<name>/`
layout. The shared copy had it right. Nothing compared the content of a "replaces" copy against the
file it replaces, which is how a shadow drifts with nothing going red; `tools\playbook-lint.ps1` in
the hub now checks it, which is also why the dead path above is spelled out in prose rather than
quoted — the lint would flag it again.)*

## Step 0 — verify committed + pushed (gate)

Run this FIRST, in **this** repo. Capturing is the last thing before `/clear`, so don't do it on
top of uncommitted or unpushed work. Read-only.

```powershell
cd c:\github\ioSender
pwsh -ExecutionPolicy Bypass -File tools\verify-pushed.ps1
```

Mirrors `push-all.ps1`'s remote model (origin/integration + v2/master) and exits **1** listing
anything dirty/unpushed. If it fails: commit, run `tools\push-all.ps1`, re-run the gate. Only
proceed once it prints `OK  clean + pushed`.

**It cannot pass on a `pr/*` branch, and that is the gate's fault, not yours.** Two structural
failures, both measured 2026-10-28 at the end of a `pr/portable-core` beta session: it compares
`v2/master..<branch>`, which reads 838 commits "not pushed" for a branch fully in sync with
`v2/pr/portable-core`; and it uses `git status --porcelain`, which counts **untracked** files, so
this repo can never be clean while `web/` exists — and `web/` must never be committed. On a
PR/beta session, do by hand what the gate exists to prove and say so on the record:

```powershell
git rev-parse HEAD; git rev-parse v2/pr/portable-core      # must match
git status --porcelain --untracked-files=no                # must be empty
```

## What moved (2026-08-03)

The tooling used to live at `tools/effort/` in this repo and write to
`%USERPROFILE%\Downloads\ClaudeConv`. Both are gone:

- Tools → `claude-hub/tools/`. There is no copy here; there is deliberately only one.
- Conversations → `claude-hub/projects/ioSender/conversations/`, now versioned — the session HTMLs too,
  not just the manifest, since an aged-out transcript makes the HTML equally irreplaceable.
- The in-repo manifest **mirror** (`tools/effort/sessions.json`) is gone along with the mechanism
  that needed it. Nothing to commit *here* on capture any more.
- Effort-tracker data (`sessions.csv`) → `claude-hub/effort/`.

Per-project settings now live in `claude-hub/projects/ioSender/conversations/sessions.json` under `config`
— that one file carries the config, the checkpoint and every session record.
