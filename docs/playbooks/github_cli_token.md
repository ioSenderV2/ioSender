# Run gh / the GitHub API on this box

**When:** any GitHub CLI or API operation (fork, push, enable Pages, dispatch CI).
**Memory context:** `gh-cli-setup.md`.

gh is installed **portable** (no PATH entry): `C:\Users\steve\tools\gh\bin\gh.exe` (call by full path).

**Token gotcha:** the PAT lives in `GH_TOKEN` at **User scope in the registry**, but harness shells
DON'T inherit it (env was cached before the var was set). So `$env:GH_TOKEN` reads empty. Fix: read the
persisted value from the registry and inject it for the command — no restart needed.

## Ready command

```powershell
tools\gh.ps1 auth status                 # stevenrwood, scopes: gist read:org repo workflow
tools\gh.ps1 repo fork OWNER/REPO --clone=false
```

`tools\gh.ps1` injects `GH_TOKEN` from the registry and calls the portable `gh.exe`, passing all
args straight through. The raw snippet it wraps:

```powershell
$env:GH_TOKEN = [Environment]::GetEnvironmentVariable('GH_TOKEN','User')
$gh = "C:\Users\steve\tools\gh\bin\gh.exe"
& $gh <args>
```

## Notes

- Same pattern for raw API via curl/PowerShell: `Authorization: Bearer $env:GH_TOKEN`.
- **Never echo the token value.**
- `gh repo fork` rejects `--remote=...` when a repo arg is given — create the fork, then add the git
  remote + push manually (cached GCM creds for github.com/stevenrwood already work for `git push`).
