# Architecture spec — registration model + standalone controls

**Status:** design draft (not yet implemented)
**Goal:** Eliminate the structural collision points that make every feature branch fight over
the same files (`MainWindow.xaml(.cs)`, `AppConfig.cs`, `AppConfigView.Setup()`,
`GCode.File`, `GrblViewModel`), by moving to a model where components **self-register**
their config, their placement, and their data needs. MainWindow becomes a pure container;
the 3D view / Console / program view become standalone, **data-source-parameterized**
controls that can exist in more than one instance.

Scope decisions taken (2026-06-28):
- **Standalone = Full**: controls take their data source as input (break the `GCode.File`
  static) so multiple instances are possible (two 3D views; a program preview in a flyout
  while another streams).
- **This document is design-only** — no code changes yet.

---

## 1. Where we are today (the starting point)

The registration model is ~70% built but only half-wired:

| Piece | File | State |
|---|---|---|
| Panel/flyout registry (factory funcs) | `CNC Controls/MainPanelRegistry.cs` | **Real** — `AssignableItem` has `Create*Panel` factories; `JobView.BuildMainPanels()` composes from it. |
| Tab registry | `CNC Controls/TabRegistry.cs` | **Metadata only** — publishes `Name`/`Label` for reorder/hide. Tabs themselves are hardcoded XAML in `MainWindow.xaml:165-218`. No factory. |
| Layout persistence | `AppConfig.cs` (`MainPanelsCsv` etc.) | **Flat** — four parallel CSV lists (main / left / flyout / tabs). |
| App config | `AppConfig.cs` (`Config` class) | **Monolith** — one `[Serializable] Config`, `XmlSerializer(typeof(Config))`, ~140 props. |
| Settings UI | `AppConfigView.xaml.cs` `Setup()` | **Hardcoded** — each panel added by hand. |
| Program data | `CNC Controls/GCode.cs` (`GCode.File`) | **Static singleton** — `.Data`, `.Tokens`, `.Commands`, `.Drag/.Drop`. |
| Controller state | `CNC Core/GrblViewModel.cs` | **Shared VM** (correct — one machine), but features pile state onto it. |

### The collision points, precisely
- **Add a tab** → edit hardcoded XAML in `MainWindow.xaml` + the `ViewType` enum + the
  `getTab` / `ShowView` / `EnableView` plumbing in `MainWindow.xaml.cs`.
- **Add a setting** → edit the one `Config` class in `AppConfig.cs` **and** `AppConfigView.Setup()`.
- **Add a feature with controller state** → edit shared `GrblViewModel.cs`.
- A registry hides *host* coupling. It does **nothing** about *global-state* coupling
  (`GCode.File` static, `GrblViewModel`). Those are separate, deeper problems — section 4.

---

## 2. Config registration (`IConfigSection`)

Replaces the monolith + hardcoded settings UI. **This is the highest-value, lowest-risk change
and is fully independent of all UI work — it should land first.**

### Model
```csharp
public interface IConfigSection
{
    string SectionKey { get; }          // stable, namespaced: "Core", "Jog", "Surface", "GCodeViewer"
    void Read(XElement element);        // hydrate from this section's XML
    XElement Write();                   // serialize this section
}

// optional — a section that contributes a Settings:App panel
public interface ISettingsPanelProvider
{
    UserControl CreateSettingsPanel();  // its own UI; no edit to AppConfigView
    string PanelLabel { get; }          // localized (resource key)
}
```

### Store
```csharp
public static class ConfigStore
{
    public static void Register(IConfigSection section);
    public static T Get<T>() where T : IConfigSection;   // typed access, replaces AppConfig.Settings.Base.X
    public static void Load(string path);                // route each <section> to its owner
    public static void Save(string path);                // compose all sections into one document
}
```

### On-disk format — **section tree, with unknown-section preservation**
This is a deliberate requirement, not a nicety. Because builds are *composed* from PR subsets
(`tools/apply-prs.py`), a build that doesn't include feature X must **preserve** X's config
section rather than wipe it on save. So the store reads the file as a tree and re-emits
unknown sections verbatim:

```xml
<AppConfig version="2">
  <section key="Core">...</section>
  <section key="Jog">...</section>
  <section key="Surface">...</section>   <!-- preserved even if Surface isn't in this build -->
</AppConfig>
```

Each registered section deserializes only its own element (`XmlSerializer` over the section
type *or* manual `XElement` read). Unknown elements are held and rewritten untouched.

### Migration
- v1 file = today's flat `<Config>`. On first load detect version, map the flat blob into a
  `Core` section + the known nested ones (`Jog`, `Camera`, `GCodeViewer`, `Lathe`…), write v2.
- **Do not big-bang all ~140 properties.** Keep a `Core` section that *is* today's `Config`
  minus the nested feature objects, then carve features out one at a time. `AppConfig.Settings.Base.X`
  can remain as a thin facade over `ConfigStore.Get<CoreConfig>()` during the transition.

### Result
Adding a setting touches **only the feature's own file** (its `IConfigSection` +
optional settings panel). Zero edits to `AppConfig.cs` or `AppConfigView`.

---

## 3. Component registration + hierarchical composition

### 3a. Unify the two registries into one descriptor
Fold `TabRegistry` into the `MainPanelRegistry` concept; give it a factory and capability flags.

```csharp
[Flags] public enum Placement { Tab=1, MainPanel=2, LeftPanel=4, Flyout=8, ContainerChild=16 }

public sealed class ComponentDescriptor
{
    public string Key { get; }                      // stable id, also config-namespace + layout ref
    public string LabelResourceKey { get; }         // localized via LibStrings (NOT a literal)
    public Placement Capabilities { get; }
    public bool AllowMultiple { get; }              // REQUIRED for Full scope (two 3D views, etc.)
    public Placement DefaultPlacement { get; }
    public string ConfigSectionKey { get; }         // optional link to its IConfigSection
    public Func<ComponentContext, UserControl> Create { get; }  // context carries data bindings — §4
    // setup-gate / ordering metadata (see §6)
}

public static class ComponentRegistry
{
    public static void Register(ComponentDescriptor d);
    public static IEnumerable<ComponentDescriptor> ByCapability(Placement p);
    public static ComponentDescriptor ByKey(string key);
}
```

MainWindow, JobControl, and any container build their children by filtering the registry by
capability and resolving the layout tree — exactly how `JobView.BuildMainPanels()` works today,
generalized.

### 3b. Composition becomes a tree, not four flat lists
The moment JobControl is a container with an *optional* program view, and a tab can host
program/3D/console sub-views, "a tab" is itself a container with its own sub-composition.
So the persisted layout moves from CSV-per-slot to a **slot tree**:

```csharp
public sealed class LayoutNode
{
    public string ComponentKey { get; set; }     // -> ComponentRegistry.ByKey
    public string InstanceId { get; set; }        // stable per placement; required when AllowMultiple
    public string Slot { get; set; }              // named region of the parent container
    public List<LayoutNode> Children { get; set; }// for container components
}
```

- A **container component** declares named **slots** (e.g. JobControl: `run-controls`, `center`,
  `panels-left`, `panels-right`, `flyouts`). The layout tree assigns components into slots
  recursively.
- The root is MainWindow with one slot `tabs`. Each tab may be a container (JobControl) whose
  `center` slot holds a tab-group of program/3D/console instances.
- Persist as XML/JSON tree under the `Layout` config section (§2). Migration: read the old
  four CSVs once → synthesize the default tree.

### 3c. Editor + non-XL
- `MainPageEditor` shuttle UI generalizes to per-container editing (or a tree editor). **This is
  a real UI change — flag for its own sub-task.**
- Non-XL ioSender (hardcoded `JobView`) keeps a fixed default tree with `LayoutEnabled=false`;
  the registration path must degrade to that without the editor.

---

## 4. Data-source parameterization (Full scope — the centerpiece)

Two distinct data axes are tangled today; the refactor separates them:

1. **Program data** (the loaded file) — currently the `GCode.File` **static**. Must become an
   **instance** so multiple controls can show *different* programs.
2. **Controller state** (live position, alarms, MDI, response log) — `GrblViewModel`. Stays a
   shared singleton (there is exactly one machine), but controls take it **explicitly via the
   context**, not via statics like `GrblSettings`/`GrblInfo` where avoidable. Many views over
   one machine is correct.

### 4a. `IProgramSource`
Encapsulate everything `GCode.File` is today:
```csharp
public interface IProgramSource
{
    ObservableCollection<GCodeData> Data { get; }   // program view grid
    List<GCodeToken> Tokens { get; }                // 3D render
    BlockingCollection<...> Commands { get; }        // stream queue
    ProgramMetadata Meta { get; }                    // path, bounds, tool list, units
    void Drag(...); void Drop(...);                  // editor ops
    event ... ProgramChanged;
}
```

### 4b. The context object handed to factories
```csharp
public sealed class ComponentContext
{
    public GrblViewModel Machine { get; }    // shared controller state
    public IProgramSource Program { get; }   // injected; defaults to the active job program
    public IConfigSection Config { get; }    // the component's own section
}
```
Controls bind to `ctx.Program` / `ctx.Machine` instead of reaching for `GCode.File` /
`AppConfig.Settings.*` statics. Default wiring passes the **active job program**, so existing
behavior is preserved; passing a *different* `IProgramSource` is what unlocks multi-instance.

### 4c. "Active program" vs "a program"
Multi-instance forces an explicit concept that's implicit today:
- The **active job program** is the one a primary `JobControl` streams.
- Secondary program/3D views are **read-only previews** over other `IProgramSource` instances.
- `JobControl` owns a reference to the program it streams. There is exactly one *streaming*
  program at a time; there can be many *viewed* programs.

### 4d. Migration strategy (incremental, facade-first)
`GCode.File` static is referenced widely (`GCodeListControl`, `Renderer`, `JobControl`,
`FusionFolderLoader`, `GCodeJob`, `GrblViewModel`). Do **not** convert all at once:
1. Introduce `GCodeProgram : IProgramSource` (the instance type).
2. Repoint the `GCode.File` static to *be* one `GCodeProgram` instance (the active one) — a
   **facade**. Nothing breaks; every existing `GCode.File.X` now reads the active instance.
3. Convert call sites from `GCode.File.X` → injected `ctx.Program.X` one control at a time.
4. When the last consumer is converted, the static becomes just "the active program" pointer.

### 4e. Per-control reality (from the coupling map)
| Control | Today | Work for Full scope |
|---|---|---|
| **Console** (`ConsoleControl`) | 75% standalone; needs `GrblViewModel` + MDI-history. | No program source at all — only `Machine`. Easiest; convert first as the pattern proof. |
| **3D view** (`Renderer`/`RenderControl`) | 30%; binds `GrblViewModel` via DataContext, renders `GCode.File.Tokens`, reads `AppConfig.GCodeViewer` + `GrblSettings`/`GrblInfo` statics. | Take `ctx.Program.Tokens` + `ctx.Config`; geometry from `Machine`/injected settings. Medium. |
| **Program view** (`GCodeListControl`) | 20%; **welded** to `GCode.File` static (`DataContext = GCode.File.Data`, `.Drag/.Drop/.Commands`). | The boss fight. Bind to `ctx.Program`. Only viable *because* §4d makes `GCode.File` an instance behind a facade first. |

---

## 5. GrblViewModel (secondary collision point)
`GrblViewModel.cs` is shared core that features keep extending (it's modified in the working
tree right now). Keep it the single controller VM, but:
- Controls receive it via `ComponentContext.Machine`, not statics.
- Feature-specific state should live in the **feature's own view-model that composes/observes**
  `GrblViewModel`, registered alongside the component — rather than new properties bolted onto
  the shared class. This curbs (doesn't eliminate) the pile-on.
- Genuine core machine state still belongs on `GrblViewModel`; use judgement.

---

## 6. Cross-cutting concerns to carry through
- **Setup gate** — `MachineSetupView` / `FirstIncompleteStep` currently knows specific
  tabs/steps. With tab registration it becomes ordering + precondition **metadata on the
  descriptor** (a component declares "I am setup step N / I gate on condition C"). Don't leave
  it hardcoded against tab identities.
- **Localization** — descriptor labels are **resource keys**, resolved via LibStrings; every
  registered component needs its `x:Uid` + 7-locale CSV rows (the standing loc debt applies here
  too).
- **Pinned flyouts / restore** — the pin state + reopen-on-launch logic moves to the layout
  tree (a flyout node with a `pinned` attribute) instead of `PinnedFlyouts` CSV.
- **`ICNCView` / `ViewType`** — the existing activation contract (`Activate()`) is good; keep it
  as the tab lifecycle interface, but resolve tabs through the registry instead of `ViewType`
  enum + `getTab`.

---

## 7. Phasing (each phase independently shippable to `integration`)
0. **Config sections** (§2) — `IConfigSection` + `ConfigStore` + section-tree file w/ unknown-
   section preservation + v1→v2 migration + facade. New settings stop touching shared files.
   *No UI risk; do first.*
1. **Tab factory** (§3a) — promote registry to factories + capabilities; MainWindow builds tabs
   from registrations; `ViewType`/`getTab` move behind the registry. **MainWindow = container.**
2. **Hierarchical layout** (§3b/3c) — slot-tree model + migration from CSVs + generalized editor;
   JobControl becomes a container with an optional program view.
3. **Data-source parameterization** (§4) — `IProgramSource` + facade + per-control conversion
   (Console → 3D → program view). This is where Full scope is actually realized.

Phases 0 and 1 deliver most of the collision relief on their own. Phase 3 is the largest and
riskiest and benefits from 0–2 being done first.

---

## 8a. Decisions (2026-06-28)
- **Streaming invariant:** exactly one *streaming* program at a time (one machine); many
  *viewable* programs allowed. Adopted as a hard invariant — simplifies JobControl ownership.
- **Editor UX:** *Defer.* Ship registration + sensible default layouts now; no new editor UI
  until Phase 2. Phases 0–1 need no editor; existing `MainPageEditor` keeps doing what it does.
- **Config access:** *Keep the facade.* `AppConfig.Settings.Base.X` stays as a thin delegate
  over `ConfigStore.Get<T>()`; convert call sites opportunistically, no deadline.
- **Instance identity:** *Auto-mint + optional rename.* System generates `InstanceId`; user may
  label. Single-instance components use their `Key` as the `Id` (no overhead).
- **Config carve-out:** *Extract the already-nested classes* (`Jog`, `JogUI`, `Camera`, `Lathe`,
  `GCodeViewer`, `Probe`, `Macros`) into sections; flat top-level props stay in one `Core`
  section. Mechanical, low-risk.
- **One config surface (decision B):** *Converge* today's standalone persistence files into
  `App.config` sections over time — single config surface, not per-panel files. Each migration
  ships with a **one-time legacy-file importer** (see §2c). Phase 0 builds the mechanism and
  migrates the in-`AppConfig` nested classes only; standalone-file conversions are per-feature
  follow-ons.

### 2c. Legacy standalone-file convergence (decision B)
End state = one `App.config` of sections. Today's separate files migrate in, each via a one-time
importer: on load, if a section's element is **absent**, the section runs its optional
`ImportLegacy()`; on success the store flags a **post-load save** so the imported data is written
into `App.config` (and the old file may be renamed `*.migrated`). In-scope files:

| Legacy type | Source | Category |
|---|---|---|
| `LoadStockSettings` | `ioSender XL/LoadStockView.xaml.cs` | config |
| Surface Spoilboard params | `CNC Controls/SurfaceSpoilboardWizard.xaml.cs` | config |
| Stepper cal/scratch params | `CNC Controls/StepperCalibrationScratchWizard.xaml.cs` | config |
| Auto Square params | `CNC Controls/AutoSquareWizard.xaml.cs` | config |
| `JobParametersViewModel` | `CNC Converters/JobParametersDialog.xaml.cs` | config |
| `ProbeDefinitionList` | `CNC Controls/ProbeDefinition.cs` | list/shareable |
| `ProbingProfile` / `ProbingMacro` | `CNC Controls Probing/` | list/shareable |
| Lathe `ProfileData` | `CNC Controls Lathe/WizardConfig.cs` | list/shareable |
| `KeyMappings` (`List<KeypressHandlerFn>`) | `CNC Core/KeypressHandler.cs` | list/shareable |
| `ControllerMapFile` | `CNC Core/ControllerMapper.cs` | list/shareable |

*list/shareable* sections hold data a user may hand-edit or copy between machines — convert them
last and keep an export/import affordance so that workflow survives the move into `App.config`.

## 8b. Open questions still owed
- **Editor UX** for the slot tree — generalize the shuttle per-container, or a single tree
  editor? (Affects Phase 2 size.)
- **Config access during transition** — keep the `AppConfig.Settings.Base.X` facade indefinitely,
  or hard-cut consumers to `ConfigStore.Get<T>()`?
- **Instance identity** — how `InstanceId` is minted/displayed when a user adds a second 3D view
  (auto vs named).
- **What counts as "core" vs a feature section** for the initial §2 carve-out.
- **Streaming ownership** when multiple JobControls could exist — spec says one streams; confirm
  we never want two concurrent streams (we don't — one machine — but state it as an invariant).
