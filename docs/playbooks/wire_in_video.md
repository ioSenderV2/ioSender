# Wire a produced video into the manual

**When:** a demo video is produced and uploaded (YouTube, unlisted) and you want it live in the manual.
**Memory context:** `iosender-demo-videos.md`, `iosender-online-manual-plan.md`.

The manual has a lazy `data-video` embed hook. Five topics currently carry `data-video="pending"`:
**connect, start-job, machine-setup, probing, tools**.

## Steps

1. Upload the finished video to **YouTube (unlisted)**; note the `<yt-id>`.
2. In `docs/manual/index.html`, on that topic's `<section>`, set:
   ```html
   data-video="<yt-id>"
   ```
   (replace `data-video="pending"`).
3. Publish the site → [publish_manual_site.md](publish_manual_site.md).
4. (Once, when the first video ships) repoint **Help → Video tutorials** from terjeio's playlist to the
   V2 playlist — handler `videoTutorials_Click` in `MainWindow.xaml.cs` (~line 904).

## Notes

- The manual is served over real https, so YouTube iframes embed fine (no CSP issue).
- The embed lazy-loads on click, so pages stay fast.
- Extra task videos worth producing beyond the 5 slots: Surface Spoilboard, Auto Square, a tool change.
