# Architecture spec — Settings / Machine Setup navigation overhaul

**Status:** planned, not started. Decisions below were taken 2026-08-03.
**Related:** `Architecture-Registration-Refactor.md` (this delivers its parked Phase 2 for config panels).

## 1. The problem

`Settings` and `Machine Setup` are top-level tabs whose contents are themselves tab strips:

| Container | Nodes | Notes |
|---|---|---|
| `GrblConfigView` (Settings) | 8 | Grbl, App, Jogging, G Code, Keyboard & Controller, Macros, Main Page, Simulator |
| `MachineSetupWizard` (Machine Setup) | 12 | Overview + steps 1-9, and step 8 (Calibration) is itself a nested `TabControl` |

~20 nodes across two tab strips, one already nesting tabs inside tabs. Tabs don't scale: they compete for
horizontal space, give no room for grouping, can't be searched, and force a flat namespace. Every new
feature makes it worse, and the nesting is the visible symptom of the model breaking down.

Modern settings UIs solve this with a navigable index: a searchable tree on the left, the selected page on
the right.

## 2. We are generalizing an in-app pattern, not importing a new one

**`GrblConfigControl` (the Grbl tab) already is this design** and is the largest settings surface we have:

- `treeView.ItemsSource = GrblSettingGroups.Groups`, `HierarchicalDataTemplate` group -> settings
- a real search box: `$53` jump-to-setting, `$5?` wildcard group expansion, free-text match over name and
  value, an "n of m" readout, F3/F4 next/previous, auto-expand of matching groups
- modified-value styling, per-group "revert to value at startup" context menus

It is proven, localized, and users already use it. The overhaul promotes that pattern one level up so the
other ~19 nodes live in the same shell. This is the single biggest risk reducer in the plan.

The composition half is also already there: `model.ConfigControls` is a registry of feature-contributed
config panels (Basic, OddJobs, Camera, Probing, Lathe, GCodeViewer, StripGCode). The only missing piece is
that `GrblConfigView.TargetPanel()` decides placement with a hardcoded `switch`, including string-matching
`"CNC.Controls.Camera.ConfigControl"` because `CNC Controls` cannot reference that assembly.

## 3. Decisions taken (2026-08-03)

**D1 - Two top-level tabs, one shared shell.** `Settings` and `Machine Setup` both remain top-level tabs and
both host the same `SettingsNavShell`. Machine Setup keeps its numbered, ordered steps; Settings gets
categories. Rejected merging them into one tab: the wizard's step order is instructional, and demoting it to
a branch of a settings tree loses that. Each can also ship independently.

**D2 - Search indexes rendered text.** When a page is first realized, walk its visual tree and index the
actual rendered strings. No per-page keyword lists to maintain or translate, no drift, and because LocBaml
has already swapped the text at BAML load, **search works in all 7 locales for free**. See section 6 for the
realization problem this creates.

## 4. The shell

```
+-- search box -----------+---------------------------+
|  TreeView (nodes)       |  ContentPresenter         |
|                         |    = selected page        |
+-------------------------+---------------------------+
|  footer: [Reset to Default]        [Save] [Restart] |
+-----------------------------------------------------+
```

The footer is **moved, not rebuilt** - `GrblConfigView` already owns Save / Restart (pulsing when a
restart-only change is pending) / Reset-to-Default with per-tab visibility, plus the Grbl-only sub-footer
(Reload / Backup / Restore / Copy to simulator). Its per-tab visibility logic becomes per-node.

## 5. `SettingsPageDescriptor` + registry

Mirrors the existing `TabDescriptor` / `TabRegistry` deliberately - same shape, same reasoning, so there is
one registration idiom in the codebase rather than two.

```
Key                  stable id, persisted as the selected node (NOT an index)
Label                x:Uid'd, localized
Category / Parent    where it sits in the tree
Order                sort within parent
Create()             factory
IsAvailable()        capability gate (Trinamic drivers present, PID log, camera, ...)
IsResettable         opts the node into the footer's Reset button
IndexForSearch       default true; false for pages too expensive/side-effecting to realize (section 6)
```

This **retires `TargetPanel()`**: each feature assembly registers its own descriptor and declares its own
placement, so `CNC Controls` no longer needs to know the type names of panels in assemblies it cannot
reference.

## 6. Search design, and its one real hazard

Two tiers, unified in one result list:

1. **Model tier** - grbl settings, already searchable via `GrblSettingDetails` (id / name / value).
2. **Harvest tier** - per page, a walk of the realized visual tree collecting `TextBlock.Text`,
   `CheckBox`/`RadioButton.Content`, `GroupBox.Header`, `Label.Content` and tooltips into a keyword set
   attributed to that node.

**The hazard: harvesting requires realization, and realization can have side effects.** Some config panels
do real work when they load or activate - the simulator config, the camera panel, anything that reads from
the controller on `Activate(true)`. Blindly constructing every page at startup to build a search index could
trigger controller traffic or hardware init that the user never asked for. That would be a self-inflicted
version of exactly the class of bug that has bitten this app before.

Mitigation, in order:

- Index a page when it is **first shown**, always. Free, no side effects beyond what the user triggered.
- For a full-coverage index, run a background pass at idle that **constructs pages without activating them**
  (`Create()` + measure/arrange in a detached host, never `Activate(true)`).
- Any page that still cannot tolerate that sets `IndexForSearch = false` and falls back to its label plus an
  optional short keyword string. Expected to be a handful (Simulator, Camera).
- Re-harvest on locale change.

## 7. Taxonomy (starting point - it is data, revise freely)

Settings:

- **Controller** -> Grbl, Simulator
- **Interface** -> Main Page, Jogging, Keyboard & Controller, Macros
- **Job & G-code** -> G Code, Viewer, Probing, Camera
- **Application** -> general app settings

Machine Setup: unchanged order, numbering preserved, Overview first, and step 8's nested `TabControl`
flattens into two child nodes (Stepper calibration, Squareness).

**Open question for Phase 1 - does the grbl group tree lift into the nav tree?** Today the Grbl page owns its
own internal tree, so adopting the shell naively gives a tree inside a tree - the same nesting smell we are
removing, one level down. The better end state is lifting `GrblSettingGroups.Groups` in as child nodes under
Controller, with the right panel showing one group's settings. That makes the nav tree ~40+ nodes, which is
precisely what the search box is for. Confirm before Phase 1; it does not block Phase 0.

## 8. Phasing (each independently shippable to `integration`)

**Phase 0 - the shell, proving itself.** Build `SettingsNavShell`, the descriptor and the registry. Host the
existing 8 Settings tabs as pages with their content untouched. Small, reviewable diff; if the shell feels
wrong it costs a day, not the project.

**Phase 1 - registration + taxonomy.** Retire `TargetPanel()`, features self-register, apply the taxonomy,
resolve the grbl-tree question above.

**Phase 2 - Machine Setup onto the shell.** 12 nodes, numbering kept, Calibration flattened. Largest visible
win; deliberately after the shell is proven.

**Phase 3 - search.** Model tier + harvest tier + the realization strategy in section 6.

## 9. Cross-cutting

**Localization.** Every node gets an `x:Uid` and a row in all 7 `Locale/<loc>/csv/*.csv` via
`tools/locadd.py`, in the same change that adds the node - English fallback means skipping it fails
silently and accumulates.

**Persistence / migration - this now matters.** `StretchTabControl PersistKey="Settings"` persists a selected
tab *index*; the shell persists a node *key*. There is a real second user (Phil) with a saved profile, so
this needs a one-time fixup in `AppConfig.ApplyOneTimeFixups` mapping old index -> new key, not a silent
break onto a blank panel. Top-level `Config.Tabs` layout entries are unaffected - both tabs survive, which
is a further point in favour of D1.

**Keyboard.** Ctrl+F focuses search; F3/F4 keep their existing next/previous-match meaning; the tree is
arrow-navigable; the selected node survives a controller reconnect.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Realizing pages for the index causes controller/hardware side effects | Section 6: lazy-first, construct-without-activate, per-page opt-out |
| Machine Setup loses its "do these in order" teaching | Numbering and Overview preserved; wizard semantics are why D1 kept it separate |
| One giant unreviewable diff | Four phases, each shippable; Phase 0 deliberately changes no page content |
| Saved profiles open on a blank panel | One-time fixup, tested against a real profile |
| Search finds nothing useful on non-grbl pages | Harvest tier (D2) rather than hand-maintained keywords |
