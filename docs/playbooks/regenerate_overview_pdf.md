# Regenerate Overview.pdf

**When:** after editing `Overview.html` (e.g. a new changelog entry).
**Memory context:** `iosender-proposed-prs-howto.md`.

Uses headless Edge. **You MUST pass a fresh `--user-data-dir`** — without it Edge attaches to the
already-running instance and the print **silently no-ops** (size/timestamp unchanged).

## Ready command

```powershell
tools\regen-overview-pdf.ps1
```

The script bakes in the fresh-`--user-data-dir` GUID (the no-op trap), the ~4 s wait, and a
LastWriteTime verify that fails loudly if Edge silently no-op'd. It spawns a browser process, so
run with the sandbox disabled.

## The raw command it runs (for reference)

```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --no-pdf-header-footer `
  --user-data-dir="$env:TEMP\edgepdf_<guid>" --print-to-pdf="C:\github\ioSender\Overview.pdf" `
  "file:///C:/github/ioSender/Overview.html"
```
