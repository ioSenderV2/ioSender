# Publish the manual site

**When:** you've edited the online manual and want the live site updated.
**Script:** `docs/manual/publish-pages.ps1`.
**Live site:** https://iosenderv2.github.io/ioSender/
**Memory context:** `iosender-online-manual-plan.md`.

The script copies `docs/manual` into a throwaway git repo, commits an **orphan `gh-pages`** branch,
and force-pushes it to the `v2` remote (`ioSenderV2/ioSender`). It also copies `Overview.html` →
`overview.html` so `/overview.html` stays current. Only `index.html`, `README`, and `img/` are
published (NOT `screenshot-uploader.html`).

## Ready command

```powershell
powershell -ExecutionPolicy Bypass -File docs\manual\publish-pages.ps1
```

## Notes

- **Auth:** reads `$env:GH_TOKEN` from the registry User var — see
  [github_cli_token.md](github_cli_token.md) — and injects it into the push URL.
- gh-pages is an **orphan** branch whose root IS `docs/manual`, so the site root is the manual
  (`…/ioSender/`, not `…/manual/`).
- Re-run after every manual edit; the push is a force-push of the regenerated orphan branch.
- Edit source at `docs/manual/index.html` (single-file HTML, inline CSS/JS, linked `img/*.png`).
