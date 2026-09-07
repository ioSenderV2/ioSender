# SVG import — turning artwork into a toolpath

**Status: PROPOSAL, not decided.** Written 2026-08-15 after measuring a real vendor logo against the
existing text-engraving pipeline. Nothing here is implemented; the two test files it refers to are.

---

## The question this answers

Text engraving already turns vector outlines into v-carved or engraved g-code. Could an `.svg` file
feed the same machinery, so a logo cuts the way a word does?

**Yes, and the seam already exists** — it was built deliberately. What follows is what is free and
what has to be written.

**Destination decided 2026-08-15: option (a)** — an SVG toolpath geometry inside a Work Order, with
the file chosen per toolpath. See the table further down for what that rules out.

## Where the seam is

`TrueTypeOutlines.Render()` (CNC Controls) turns a string into `List<OutlineContour>` — closed
polygons in mm, Y-up, each carrying its signed area. It uses WPF's `FormattedText.BuildGeometry()`
for the real glyph outlines and flattens curves to polylines **once**. Everything after that is
polygon arithmetic.

The engine's entry point is:

```csharp
VCarve.Build(IList<IList<Point2D>> contours, double halfAngleRad, ...)
```

Bare polygon lists. It has never known what a glyph is, and that is stated as the design intent in
`TrueTypeOutlines.cs`'s own header:

> *"the carve engine works on polygons and knows nothing about fonts, so everything font-shaped
> stops here… A headless server would supply outlines some other way — from a client, or from its
> own rasteriser — without the engine changing."*

**So SVG support is one new producer of `List<OutlineContour>`.** Two call sites consume the font
one today (`WorkOrderCompiler.BuildVCarve` and `WorkOrderView`'s preview).

## What is free

- the v-carve engine (`VCarve.cs`, 754 lines) — depth field, passes, the lot
- the engrave/stroke path
- tool geometry, feeds, depth-of-cut, pass splitting
- stock placement, rotation, the preview canvas
- g-code emission, including the comment sanitising added 2026-08-15
- `VCarve.Contains(ring, px, py)` — a point-in-polygon test, which §"winding" below needs

## What has to be written

### 1. Path data → polygons — mostly solved for free

**WPF's `Geometry.Parse()` speaks the XAML path mini-language, which is very close to SVG's `d`
grammar** (`M L H V C S Q T A Z`, absolute and relative). Parse with it and the result flows into
the *same* `GetFlattenedPathGeometry(tolerance)` call `TrueTypeOutlines` already makes — identical
flattening, identical tolerance, identical downstream behaviour.

Not a perfect superset, so it needs checking against real files rather than assuming. But it
removes what would otherwise be the largest and most error-prone chunk, elliptical arcs included.

### 2. The document around the paths

`viewBox` + `width`/`height` → mm per user unit; `transform` on elements and nested groups; the
primitives (`rect`, `circle`, `ellipse`, `polygon`, `polyline`); `<use>`/`<defs>` instancing;
`style=` vs `class=` fills. This is the bulk of the work, and it is **breadth, not depth** — each
piece is small and independently testable.

### 3. Winding — real, but it costs cut ORDER, not the shape

> **⚠️ CORRECTED 2026-08-15, same day.** This section first claimed a mis-derived hole would be
> *carved solid* — that a 17 x 16 mm chunk would go missing from the artwork. **That was wrong, and
> it was wrong because I reasoned from `TrueTypeOutlines`' comment instead of reading the engine.**
>
> `VCarve.DistanceField.Inside()` is **even-odd ray casting across ALL contours together**, and
> even-odd *is* containment parity. The engine never looks at winding direction. Verified by
> transcribing its algorithm over `logo-nested-islands.svg`: a point in the depth-2 island reads
> SOLID, a point in the hole around it reads void, both correct.
>
> **The v-carve engine is already correct for SVG, whatever the winding.** The measurement below
> still stands and is still worth knowing — but its consequence is machining order, not geometry.
> The original claim is left visible rather than deleted, because "the doc said the risk was
> elsewhere" is exactly how the wrong thing gets built carefully.

`OutlineContour.IsOuter` is read in exactly **one** place — `WorkOrderCompiler` line 787 — and only
to group passes so each glyph is cut completely before the next, left to right. Nothing else reads
it, and nothing reads `SignedArea` outside `TrueTypeOutlines` itself.

So for SVG, `IsOuter` should mean *"bounds a solid region from outside"* = **even containment
depth**. Get it wrong and some regions' passes fall to the unclaimed fallback and are cut last,
out of the tidy left-to-right order. Worth doing properly; not a safety or correctness issue.

The measurement that prompted all this:

`TrueTypeOutlines` decides outer-vs-counter from the **sign of the signed area**, and says why that
is safe:

> *"a glyph's counters … are contours wound OPPOSITE to the outer boundary, and a carve engine that
> cannot tell them apart will happily carve the hole solid."*

TrueType guarantees that. **SVG does not.** SVG expresses holes through the `fill-rule` (`nonzero`
by default, or `evenodd`), and authors' paths are routinely wound inconsistently.

**Measured, not assumed.** A real vendor logo (2026-08-15, 23 KB, 29 `<path>` elements) flattened to
**42 subpaths**: 28 positive area, 14 negative, **12 true holes by containment nesting** — and the
sign rule **disagreed with containment on 2 of the 42**.

Both disagreements sat at **nesting depth 2** — islands inside a counter — wound the same way as the
holes around them:

```
subpath 0   area -16749   depth 2   containment: SOLID   sign rule: HOLE   (222 x 198 units)
subpath 1   area   -585   depth 2   containment: SOLID   sign rule: HOLE   ( 26 x  45 units)
```

They render correctly in any browser: under `nonzero` the winding numbers sum to `+1-1-1 = -1`,
which is non-zero, so they fill. The sign rule calls them holes.

**⇒ Derive `IsOuter` from containment nesting (point-in-polygon, depth parity), never from winding
sign.** `VCarve.Contains` is already there. That keeps pass grouping sensible; the cut geometry is
the engine's even-odd test either way.

### 4. Open paths make the two operations diverge

SVG carries unclosed paths and stroke-only shapes. Meaningful to **engrave** (follow the line);
meaningless to **v-carve**, which needs closed regions. Text never has this problem, so it is a new
case rather than a port — and the honest behaviour is to say so, not to silently drop them.

### 5. Sizing has no analogue

SVG has no cap height, so `WorkOrderTextFit` does not transfer. Size by bounding box — width, height,
or scale-to-fit within the shape. Simpler than the text fit, but new.

## Two possible destinations — decide this first

|  | Reuses | Gives you |
|---|---|---|
| **(a) an SVG toolpath geometry in a Work Order** | v-carve/engrave engines, fit, preview, stock placement | v-carve a logo with real tools, depths, tabs, positioned on stock |
| **(b) an `IGCodeConverter`** | the HPGL/Excellon plug-in pattern, Load File | a program from an SVG — no v-carve, no work order |

(b) already has a slot beside `HpglToGCode` and `Excellon2GCode` and is far shallower. **(a) is what
"like the text feature" means**, and is what the architecture is arranged for.

## The test files

Both live in `tests/svg/` and were **verified by measurement**, with the same flatten-and-nest
analysis run on the real logo — the properties below are results, not intentions.

### `logo-nested-islands.svg` — the acceptance test for v1

Matches the real logo's shape class: `<path>` only, no transforms, no arcs, no quadratics, every
subpath closed, fills by CSS class, `viewBox` with no `width`/`height`.

```
subpaths        : 9
nesting depths  : {0: 3, 1: 3, 2: 3}
solid regions   : 6      (3 outer + 3 islands)
holes           : 3
sign rule WRONG : 3 of 9  (all three islands)
```

It is the real file's failure mode concentrated: **an importer that cuts 6 solid regions here is
correct; one that cuts 3 has ported the font shortcut.**

### `logo-hostile.svg` — the backlog, made concrete

Everything v1 is *not* expected to handle, in one file:

```
elements   : rect 1, circle 1, ellipse 1, polygon 2, use 2, path 6, g 2, defs 1
transforms : 5  (including nested group transforms)
arcs       : 3
open paths : 1
evenodd    : 2
units      : width 80mm / height 40mm vs viewBox 400x200  ->  0.2 mm/unit, NOT 1:1
```

**A v1 importer should fail this file honestly, naming what it cannot do.** Silent partial import is
the dangerous outcome: a plausible preview, half the artwork missing, and the operator cuts it. Move
each capability's comment into the acceptance file as it starts working.

## Recommended first cut

Paths and the basic primitives, flattened transforms, scale-to-fit by bounding box,
containment-derived holes, v-carve + engrave. The engine half is already done and already
polygon-native, so the work is the front end.

Gate it on **real files from the toolchain that will actually produce them** — Inkscape, Illustrator
and Fusion differ in units, transforms and structure, and that long tail is where the effort
actually goes. Prove holes on a file with counters before trusting any preview.

## Note on test assets

The vendor logo that produced the measurements above is **not** in this repo, and should not be:
this is a public repository and that artwork is a third party's trademark. The synthetic files
replace it for testing and are deliberately nastier than it was.
