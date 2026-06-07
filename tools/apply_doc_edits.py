#!/usr/bin/env python3
"""Apply the mechanical review-comment directives in the Mega-XL doc.

The doc (docs/Mega-XL-Upgrades.html) has an in-browser "Review" toolbar: you turn
on Comment mode, click a passage, and type an edit instruction. Clicking Save
downloads an annotated copy (with data-comment="..." attributes).

This script takes that annotated copy (default: the newest Mega-XL-Upgrades*.html
in your Downloads folder), applies the *structured* directives via headless
Edge/Chrome (which is the only thing that parses the page perfectly), writes the
result to docs/Mega-XL-Upgrades.html, re-renders the PDF, and reports what it did.

Structured directives it can apply automatically:
    replace with: <new text>            (or, with a Re:"phrase" prefix, just that phrase)
    replace "<old>" with "<new>"
    typo: <old> -> <new>     /     fix: <old> -> <new>
    delete                              (the passage, or the Re:"phrase")
    insert after: <new paragraph>
    append: <text>
Anything else (reword, "is this right?", etc.) needs judgement and is LEFT IN
PLACE as a comment for manual/LLM follow-up; the script lists those.

Usage:
    python tools/apply_doc_edits.py [annotated.html] [--commit] [--push] [--no-pdf] [--out PATH]

    annotated.html   path to the saved copy (default: newest in ~/Downloads)
    --commit         git add + commit docs/Mega-XL-Upgrades.{html,pdf} if changed
    --push           git push afterwards (implies --commit; only if a commit was made)
    --no-pdf         skip re-rendering the PDF
    --out PATH       write to PATH instead of docs/Mega-XL-Upgrades.html (for testing)
"""

import os
import sys
import glob
import html
import platform
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
DEFAULT_DOC = REPO / "docs" / "Mega-XL-Upgrades.html"


def find_browser():
    cands = []
    sysname = platform.system()
    if sysname == "Windows":
        roots = [os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
                 os.environ.get("ProgramFiles", r"C:\Program Files"),
                 os.environ.get("LocalAppData", "")]
        for r in roots:
            if not r:
                continue
            cands += [os.path.join(r, "Microsoft", "Edge", "Application", "msedge.exe"),
                      os.path.join(r, "Google", "Chrome", "Application", "chrome.exe")]
    elif sysname == "Darwin":
        cands += ["/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                  "/Applications/Chromium.app/Contents/MacOS/Chromium"]
    else:
        for name in ("microsoft-edge", "google-chrome", "chromium", "chromium-browser"):
            p = shutil.which(name)
            if p:
                cands.append(p)
    for c in cands:
        if c and os.path.exists(c):
            return c
    sys.exit("error: no Edge/Chrome found for headless rendering "
             "(looked for Edge then Chrome).")


def downloads_dir():
    d = Path.home() / "Downloads"
    return d if d.exists() else Path.home()


def newest_annotated(arg):
    if arg:
        p = Path(arg)
        if not p.exists():
            sys.exit(f"error: file not found: {p}")
        return p
    matches = glob.glob(str(downloads_dir() / "Mega-XL-Upgrades*.html"))
    if not matches:
        sys.exit("error: no Mega-XL-Upgrades*.html in Downloads — Save from the "
                 "review toolbar first, or pass the path explicitly.")
    return Path(max(matches, key=os.path.getmtime))


def file_uri(path, query=""):
    return Path(path).resolve().as_uri() + query


def run_browser(browser, args, capture=False, timeout=90):
    with tempfile.TemporaryDirectory(prefix="apply_doc_") as udd:
        cmd = [browser, "--headless=new", "--disable-gpu", "--no-first-run",
               "--no-default-browser-check", f"--user-data-dir={udd}"] + args
        return subprocess.run(cmd, capture_output=capture, timeout=timeout)


def count_comments(text):
    return len(re.findall(r'\sdata-comment="', text))


def left_directives(text):
    return [html.unescape(m) for m in re.findall(r'\sdata-comment="([^"]*)"', text)]


def main():
    argv = sys.argv[1:]
    do_push = "--push" in argv
    do_commit = "--commit" in argv or do_push   # pushing implies committing
    no_pdf = "--no-pdf" in argv
    out = DEFAULT_DOC
    if "--out" in argv:
        i = argv.index("--out")
        out = Path(argv[i + 1])
        del argv[i:i + 2]
    positional = [a for a in argv if not a.startswith("--")]
    src = newest_annotated(positional[0] if positional else None)
    pdf = out.with_suffix(".pdf")

    browser = find_browser()
    src_html = src.read_text(encoding="utf-8", errors="replace")
    total = count_comments(src_html)
    print(f"Source: {src}")
    if total == 0:
        print("No review comments found; nothing to apply.")
        return 0

    # Apply the structured directives in the browser DOM, then dump the result.
    res = run_browser(browser, ["--virtual-time-budget=5000", "--dump-dom",
                                file_uri(src, "?apply=1")], capture=True)
    applied_html = (res.stdout or b"").decode("utf-8", "replace")
    if "<html" not in applied_html.lower():
        sys.exit("error: headless apply produced no HTML (browser failed?).")
    if not applied_html.lstrip().lower().startswith("<!doctype"):
        applied_html = "<!DOCTYPE html>\n" + applied_html

    left = left_directives(applied_html)
    applied = total - len(left)

    if applied <= 0:
        print(f"Found {total} comment(s), but none are mechanical — all need review:")
        for d in left:
            print("  - " + d)
        print("Nothing written.")
        return 0

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(applied_html.encode("utf-8"))
    rel = out.relative_to(REPO) if str(out).startswith(str(REPO)) else out
    print(f"Applied {applied} edit(s) -> {rel}")
    if left:
        print(f"Left {len(left)} for review:")
        for d in left:
            print("  - " + d)

    if not no_pdf:
        run_browser(browser, ["--no-pdf-header-footer",
                              f"--print-to-pdf={pdf}", file_uri(out)])
        print(f"Rendered -> {pdf.relative_to(REPO) if str(pdf).startswith(str(REPO)) else pdf}")

    committed = False
    if do_commit:
        paths = [str(out)] + ([str(pdf)] if not no_pdf else [])
        subprocess.run(["git", "add"] + paths, cwd=REPO)
        staged = subprocess.run(["git", "diff", "--cached", "--quiet"], cwd=REPO)
        if staged.returncode != 0:
            msg = f"docs: apply {applied} review edit(s) to Mega-XL doc"
            if subprocess.run(["git", "commit", "-m", msg], cwd=REPO).returncode == 0:
                committed = True
                print(f"Committed: {msg}")
        else:
            print("Nothing changed on disk; no commit made.")

    if do_push:
        if committed:
            if subprocess.run(["git", "push"], cwd=REPO).returncode == 0:
                print("Pushed.")
            else:
                print("Push failed — if this branch has no upstream, set one once with "
                      "`git push -u <remote> <branch>`.")
        else:
            print("Nothing committed; nothing to push.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
