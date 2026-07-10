# Publish a folder/tool as its own public GitHub repo

**For:** taking a self-contained tool that lives in a subfolder (or its own local dir) and
publishing it as a new **public** repo under `stevenrwood`, with an agent-facing guide so
anyone's Claude can pick it up. Used to publish `agent-cockpit` and `WpfUiTestServer`.

**Prereqs:** the GitHub CLI + token dance from [github_cli_token.md](github_cli_token.md).

## Steps

1. **Make it stand alone.** If it's a subfolder of another repo, copy it to its own dir
   (`c:\github\<name>`). Preserve history only if you need it (`git subtree split --prefix=<dir>`);
   a fresh `git init` is fine otherwise (the source repo keeps the history).

2. **Add the repo hygiene files:**
   - `.gitattributes` → `* text=auto eol=lf` (repo standard is LF).
   - `.gitignore` for the stack (`node_modules/`, `bin/`, `obj/`, …).
   - `LICENSE` — default **BSD-3-Clause** (matches ioSender's origin) unless told otherwise.
   - `README.md` — human interface/spec.
   - **`CLAUDE.md`** — the agent-facing operating guide (another person's Claude Code auto-loads
     it): what it is, prereqs, deploy/run, the use loop, code map, gotchas, self-verify recipe.

3. **Commit locally**, on `master`:
   ```sh
   cd /c/github/<name> && git init -q && git add -A && git commit -m "…" && git branch -M master
   ```

4. **Create the public repo + push** (token injected per the gh playbook):
   ```powershell
   $env:GH_TOKEN = [Environment]::GetEnvironmentVariable('GH_TOKEN','User')
   $gh = "C:\Users\steve\tools\gh\bin\gh.exe"
   Set-Location C:\github\<name>
   & $gh repo create stevenrwood/<name> --public --source=. --remote=origin --push --description "<one-liner>"
   ```
   > PowerShell flags git's normal stderr progress ("To https://…", "[new branch]") as an error —
   > that is **not** a failure. Confirm with `gh repo view stevenrwood/<name> --json visibility,url`.

5. **Verify:** `git remote -v` shows `origin`, and `gh repo view … --jq '.visibility'` is `PUBLIC`.

## Notes
- `stevenrwood` is the personal namespace ("srw"); the only org on the account is `ioSenderV2`.
- If the tool is a library others consume, follow with
  [nuget_trusted_publishing.md](nuget_trusted_publishing.md) (or the package flow for its stack).
