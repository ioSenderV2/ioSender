# Re-import / redo a manual screenshot

**When:** a manual screenshot arrives (or is redone) and needs to go live.
**Memory context:** `iosender-online-manual-plan.md`.

Screenshots are linked `img/*.png` (NOT base64). Placeholders in the manual are
`<div class="shot-todo">…</div>` inside a `<figure>`.

## Steps (new shot)

1. The image lands in `C:\Users\steve\Downloads\<name>.png` (uploader download-fallback), or via the
   local `docs/manual/screenshot-uploader.html` tool.
2. Copy it into `docs/manual/img/<name>.png`.
3. In `docs/manual/index.html`, swap the placeholder for the image (keep the `<figcaption>`):
   ```html
   <img src="img/<name>.png" alt="…">
   ```
4. Verify no broken links, then publish → [publish_manual_site.md](publish_manual_site.md).

## Steps (redo an existing shot)

1. Newest copy lands in `C:\Users\steve\Downloads\<name>.png`.
2. Copy over `docs/manual/img/<name>.png`.
3. Publish → [publish_manual_site.md](publish_manual_site.md). (No HTML change needed — same filename.)

## Notes

- Do it incrementally as shots arrive.
- `screenshot-uploader.html` is local-only (not published) — it uses the File System Access API to
  write straight into `docs/manual/img`, or falls back to download-with-correct-name.
