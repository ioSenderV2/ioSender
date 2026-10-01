# Run gh / the GitHub API on this box

> **Shared playbook — the procedure is `claude-hub/playbooks/github_cli_token.md`.**
> Read it there. Nothing in it is specific to this project, so this file is a pointer rather than a
> copy: until now it was a byte-identical duplicate, and a duplicate has no way to know it has gone
> stale. If this project ever needs to change the procedure, add the delta here under an
> `> Extends:` header and state only what differs.

> **The wrapper script is the hub's too** — `c:\github\claude-hub\tools\gh.ps1`. This repo
> carried a copy until 2026-09-30; the hub version is a superset (portable-then-PATH lookup, does
> not clobber an existing GH_TOKEN, does not hard-fail when a keyring login would do), so the
> local copy was deleted rather than left to drift. `tools/wait-for-release.ps1` calls the hub path.
