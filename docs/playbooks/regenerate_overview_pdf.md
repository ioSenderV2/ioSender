# Regenerate Overview.pdf

**When:** after editing `Overview.html` (e.g. a new changelog entry).
**Memory context:** `iosender-proposed-prs-howto.md`.

Uses headless Edge. **You MUST pass a fresh `--user-data-dir`** — without it Edge attaches to the
already-running instance and the print **silently no-ops** (size/timestamp unchanged).

## Ready command

```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --no-pdf-header-footer `
  --user-data-dir="$env:TEMP\edgepdf_<guid>" --print-to-pdf="C:\github\ioSender\Overview.pdf" `
  "file:///C:/github/ioSender/Overview.html"
```

## Notes

- Substitute a unique `<guid>` in the profile path each run (or a timestamp).
- Wait ~4 s, then confirm `Overview.pdf` LastWriteTime/size actually changed.
- It spawns a browser process, so run with the sandbox disabled.
