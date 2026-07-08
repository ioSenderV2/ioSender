# Playbooks — one-stop-shopping for repeatable procedures

Each file here is **one procedure**: what it's for, the ordered steps, and the ready-to-run
command or copy-paste fragment (after you substitute the parameters). This is the *executable*
counterpart to the auto-memory: memory holds the **why / history / state**, these hold the **recipe**.

When a procedure changes, edit the playbook — it's the single source of truth for the steps. The
matching memory file keeps its context and just points here.

## Index

| Playbook | What it does |
|---|---|
| [add_changelog_entry.md](add_changelog_entry.md) | Add a `#N` entry to the Overview.html changelog (the 4 places + HTML skeleton) |
| [regenerate_overview_pdf.md](regenerate_overview_pdf.md) | Rebuild Overview.pdf from Overview.html (headless Edge, fresh profile) |
| [end_of_session_wrapup.md](end_of_session_wrapup.md) | The full end-of-session sequence, in order |
| [capture_conversation_log.md](capture_conversation_log.md) | Export this session's conversation to styled HTML |
| [build_commit_test_loop.md](build_commit_test_loop.md) | The kill→debug+launch→release-verify→commit loop |
| [headless_build.md](headless_build.md) | Build (and optionally launch) with build.ps1, no VS GUI |
| [publish_manual_site.md](publish_manual_site.md) | Push docs/manual to the live gh-pages site |
| [wire_in_video.md](wire_in_video.md) | Attach a produced video to a manual topic + repoint Help |
| [reimport_manual_screenshot.md](reimport_manual_screenshot.md) | Swap a manual screenshot placeholder / redo a shot |
| [localization_pass.md](localization_pass.md) | Run the LocBaml/CSV localization catch-up pass |
| [github_cli_token.md](github_cli_token.md) | Run gh / the GitHub API on this box (portable gh + token) |
| [build_teensy_firmware.md](build_teensy_firmware.md) | Build the grblHAL Teensy 4.1 firmware with PlatformIO |
| [compose_demo_video.md](compose_demo_video.md) | Composite the CNC + app footage into a demo video |

## Conventions

- **Filenames** are `snake_case`, one procedure each, named for the verb.
- **Paths** are relative to the repo root `c:\github\ioSender` unless absolute.
- Replace `<...>` placeholders before running.
- Scripts referenced here (`build.ps1`, `tools/locadd.py`, `docs/manual/publish-pages.ps1`,
  `tools/effort/convo-logger.ps1`, `docs/demo-videos/README.md`) live in the repo and are the
  authoritative implementation — the playbook is the invocation guide + gotchas.
