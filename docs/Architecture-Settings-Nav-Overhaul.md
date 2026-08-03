# Architecture spec — Settings / Machine Setup navigation overhaul

**Status: DELIVERED (2026-08-03).** All four phases shipped. This document was written as a plan and has
been corrected in place to describe what was actually built — where the plan turned out to be wrong, the
plan is stated and then the correction, because the reasons are the useful part.
**Related:** `Architecture-Registration-Refactor.md` (this delivered its parked Phase 2 for config panels).

## 1. The problem

`Settings` and `Machine Setup` were top-level tabs whose contents were themselves tab strips:

| Container | Nodes | Notes |
|---|---|---|
| `GrblConfigView` (Settings) | 8 | Grbl, App, Jogging, G Code, Keyboard & Controller, Macros, Main Page, Simulator |
| `MachineSetupWizard` (Machine Setup) | 12 | Overview + steps 1-9, and step 8 (Calibration) was itself a nested `TabControl` |

~20 nodes across two tab strips, one already nesting tabs inside tabs. Tabs don't scale: they compete for
horizontal space, give no room for grouping, can't be searched, and force a flat namespace.

## 2. We generalized an in-app pattern, not an imported one

**`GrblConfigControl` (the Grbl page) already was this design** — `treeView.ItemsSource =
GrblSettingGroups.Groups`, a real search box with `$53` jump-to, `$5?` wildcard expansion, free-text match,
an "n of m" readout and F3/F4 next/previous. Proven, localized, already in daily use. That was the single
biggest risk reducer available and the overhaul was shaped around reusing it rather than inventing.

## 3. Decisions

**D1 — Two top-level tabs, one shared shell.** Both remain top-level tabs; both host `SettingsNavShell`.
Rejected merging them: the wizard's step order is instructional, and demoting it to a branch of a settings
tree loses that.

**D2 — Every config panel is its own node** (user direction). Not "one node per old tab": the App, Jogging
and G Code tabs each crammed unrelated panels into 2-3 columns, and that cramming is what stopped scaling.
Camera later split further, into `Camera` + `Demo recording (OBS)`.

**D3 — The Grbl page stays self-contained.** Its group tree is *data* — live controller values with
modified-highlighting and revert-to-startup, fetched on connect — not navigation. Lifting those groups into
the nav tree would have made the tree connection-dependent (~30 nodes appearing on connect, gone on
disconnect) and broken on classic grbl, which reports no groups at all. Cost of the decision: two search
boxes on screen when you are on the Grbl page. Accepted.

**D4 — Labels are read from the panels, not authored.** Every config panel is a single `GroupBox` with an
`x:Uid`'d Header that LocBaml has already localized by the time the panel is constructed, so a node reads
its label off the panel it names. Correct in all 7 locales, no new CSV rows, and it cannot drift. Only the
5 category headings and the pages contributed by editors needed real resources.

**D5 — Sub-tab key bindings dropped** (user approved). The shortcut badge and right-click "Bind to Key"
attach to a tab header; with no tab strip they were unreachable. `ApplyOneTimeFixups` strips shortcuts
persisted against `Tab.Settings.*` / `Tab.MachineSetup.*`. **Top-level tab shortcuts are untouched** — they
are wired separately in `MainWindow`, and the trailing dot in those prefixes is what protects them.

## 4. The shell

Search box + category tree left, selected page right, the pre-existing Save / Restart / Reset footer
beneath — *moved*, not rebuilt. `SettingsNavShell` owns navigation only; hosts wire behaviour through
`SelectedNodeChanged`, exactly as they used to react to `SelectionChanged`.

Tree width is **measured**, not hardcoded (`AutoSizeNav`): `FormattedText` over every visible label in the
tree's own typeface, plus indent depth, plus the status dot where present, plus expander/padding/scrollbar.
Labels come from localized panel headers, so the longest one differs per locale — any fixed number that
fits English truncates silently elsewhere.

## 5. Placement is declared by the panel

`ISettingsPanelCategory` (category + sort order) is implemented on the panel itself, so placement travels
with it however it reaches the host. This retired **both** halves of the old hardcoded placement —
`TargetPanel()` and `TabFor()` — including the full type-name string matching (`"CNC.Controls.Camera.
ConfigControl"`) that `CNC Controls` needed for panels in assemblies it cannot reference. Nodes are
inserted by declared order: feature panels register when their own view is first built, which is not a
stable order, so appending made the tree depend on which features happened to load first.

## 6. Editors and wizards keep their controls whole

Several pages share one control: the key-map editor backs 2, Camera 2, the setup wizard 12. **The obvious
implementation — hand out each `TabItem`'s body as page content — is wrong and dangerously so.** Those
controls hook their behaviour on the control: `KeyMapEditor.PreviewKeyDown` drives shortcut capture, and its
`Loaded`/`Unloaded` pause controller dispatch so that testing a gamepad button cannot drive the machine.
Handing out the bodies leaves the control outside the visual tree and silently kills both — no compile
error, and the second one is a safety behaviour.

So each control stays whole and is the content of all its pages; its tab strip is templated down to a bare
`ContentPresenter`, and `ISettingsPageProvider.ShowPage()` switches sections. `SettingsNavNode.Owner`
records the owning control so save-on-leave and reset-to-defaults resolve to it, not to the page body.
Save-on-leave skips when moving *between* pages of the same editor, which is not leaving it.

For Machine Setup this also means every existing selection hook still fires underneath — the macro-status
refresh, the simulator refresh, the calibration sub-wizard activation — none of it reimplemented.

Two signals had to be carried across explicitly, because both lived on tab headers that no longer render:
the per-step **green/orange/red grading** (now a status dot, restated on `StepStatusChanged`) and the
**sub-tab gates** (stepper calibration needs a 3D probe; the simulator step hides while connected to the
simulator — now `IsAvailable` checks re-evaluated after `wizard.Activate()`).

## 7. Search — the plan was wrong twice

**Planned:** harvest each page's *visual* tree, with a background pass realizing every page to index it,
and an `IndexForSearch = false` opt-out for pages too expensive or side-effecting to realize.

**Both halves failed on contact:**

- **Shared controls.** Indexing "the page's control" gives all twelve wizard steps identical text, so every
  query matches all of them. Each page hands in its own subtree (`SettingsSubPage.IndexRoot`) instead.
- **Realization.** Realizing a Machine Setup step fires its selection hooks — including the macro-status
  query that reads the controller's filesystem. **Indexing must never talk to the machine.**

**As built:** walk the **logical** tree. Those objects exist from `InitializeComponent`, so nothing is
measured, arranged or `Loaded` to read them, pages never opened are still searchable, and there is no
background pass. The `IndexForSearch` opt-out was deleted rather than kept — it implied a hazard that no
longer exists.

Collected: headers, labels, checkbox/button captions, tooltips, plain-string combo items. **Not**
TextBox/PasswordBox contents — those are the user's own values (paths, IPs, the OBS password), not search
terms, and have no business in an index.

Visible text and tooltip text are indexed **separately**, because a tooltip hit is on the page but only on
hover: searching "strip" matched the `Main` page via a *Send comments* tooltip, which read as a wrong
result until the row could say `Matched tooltip: ...`. A name match beats a text match for Enter.

## 8. Phases (all delivered)

| Phase | What | Commit |
|---|---|---|
| 0 | Shell + per-panel nodes, Settings tab strip retired | `01774be` |
| 1 | `ISettingsPanelCategory`, placement retired from the host, localization | `7bf44aa`, `aed0d94` |
| 2 | Machine Setup onto the shell, status dots, sub-tab gates | `a9d71ee` |
| 3 | Logical-tree search, match count, match explanation | `e0a5d2c` |

## 9. Cross-cutting

**Localization.** Panel pages cost nothing (D4). The 5 category headings and 5 editor/step page labels are
real resources in `CNC.Core`'s `LibStrings.xaml` with rows in all 7 CSVs.

`tools/locadd.py` was **broken and silently so** — it still listed `SurfaceSpoilboardWizard.xaml`, deleted
in the Tools-tab retirement, and died on the missing file before reaching anything else, so every `x:Uid`
added since that deletion had gone un-backfilled. Fixed; a missing target now warns and skips.

**Migration.** `ApplyOneTimeFixups` strips the retired sub-tab shortcuts (D5). The old
`StretchTabControl PersistKey="Settings"` tab-order entry simply falls out of use — ordering is declared
now, not user-reordered.

**Tooltips (app-wide, prompted by this work).** Long tooltips wrap at 450px, and are placed at
`MousePoint` with a 32/28 offset: WPF's default `Mouse` placement offsets by an *assumed* standard cursor
size, so an "extra large" Windows pointer covered the first characters. The wrap template is selected by
content type, since a blanket `ContentTemplate` would render element-content tooltips as their type name.

## 10. What this cost

- Two search boxes on the Grbl page (D3).
- Sub-tab key binding, removed with approval (D5).
- `KeyMapEditor` still declares its own implicit `ToolTip` style, shadowing the app-wide one; left alone so
  a wrapping change would not silently alter its deliberate `MousePoint` placement.
