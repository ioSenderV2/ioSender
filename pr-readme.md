# Start Job "Dynamic" mode - fold Probing tabs + Height Map in

## Goal

Start Job's fixture workflow (clamp stock at a known fixture, probe once, get an exact origin) already
covers most jobs. The standalone **Probing** tab (Tool length offset / Edge finder external / Edge
finder internal / Center finder) and the **Height Map** tab predate that workflow and are largely a
holdover from before fixtures existed. This PR is a discussion prototype for
[issue #10](https://github.com/ioSenderV2/ioSender/issues/10): fold everything those tabs do into Start
Job's existing "no saved fixture, probe now" entry, so Start Job can do a corner-fence job **or** a
one-off manual probe **or** a height map, all from one tab - without deciding up front whether Probing
and Height Map should be hidden or removed.

Nothing is deleted. Probing and Height Map are dropped from the **default** tab layout only, still
fully registered, and can be shown again from Settings > Main Page > Tabs any time.

## Design

- **"Dynamic" fixture** - the synthetic "G28 (loose probe)" Start Job fixture entry (no saved position,
  pure jog-then-probe) is renamed **Dynamic** and given a **Geometry** panel (under Setup, before Stock):
  - **External / Internal** radio (External default) + **Is Circle** checkbox.
  - External/Internal (not Circle) embeds the *actual* clickable 3x3 picker grid from the Edge Finder
    External/Internal Probing tabs - same images, same click behavior, driven by a lightweight
    `ProbingViewModel` instance just for its `ProbeEdge` selection.
  - Is Circle shows the matching Center Finder reference image (solid boss for External, hole for
    Internal) plus a **Passes** field - repeats the center probe, re-centering each pass, same idea as
    the old Center Finder tab.
  - Picking the default (front-left, outside corner) behaves exactly as Start Job always has - the
    4-corner measure/rotate/TLO flow is untouched. Any other pick runs a new one-shot probe
    (`BuildDynamicProbeProgram`) through extended `pcorner.macro` (new inside/outside + single-face
    flags) or a new `pcenter.macro` (solid-boss/hole center finding).
- **"Set origin or offset"** - the WCS dropdown (G54-G59) gains a **G92** option for a temporary offset
  instead of a persistent WCS origin; Rotate and Verify skew correctly disable under G92 (neither
  applies to a temporary offset).
- **"Probe height map"** checkbox under Measure - after the run completes, probes a grid over the
  measured stock area and applies it to the loaded job, reusing the Height Map tab's own probing engine
  (`HeightMapView.RunHeightMapAndApply`) rather than re-deriving it.
- **Tabs editor bugfix** (found while removing Probing/Height Map from the default layout): a tab hidden
  via Settings > Main Page > Tabs could never be shown again, because the editor's "available" list
  only ever reflected tabs actually built that session, not the full registry. Added
  `TabRegistry.AllTabs` as the correct, permanent source for that list.

## Status

UI/simulator-verified only - **not yet run on real hardware**. Known rough edges, called out rather
than papered over:
- Non-round holes are stubbed/disabled in the center-finder geometry picker.
- Center-probe Z-depth discovery is a simple straight-down probe - fine for a solid boss, unreliable
  centered over an open hole (documented in `pcenter.macro`'s own header).
- Inside-corner/inside-edge picks need something to probe against (a pocket or step) to actually test.

## Try it

Apply the `beta` label to this PR to get a beta build published as a GitHub prerelease
(`beta-pr<N>` - see `.github/workflows/beta-release.yml`), updated on every push and removed
automatically when the PR closes.
