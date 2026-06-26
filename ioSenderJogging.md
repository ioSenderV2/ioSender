# ioSender — Jogging reference

A map of every way to jog, the modifier scheme, the two keyboard paths, and every
configuration variable that affects jogging (where it is changed and where it is used).

> This fork unifies the modifier behaviour across the keyboard and the on‑screen jog
> panel, and decouples the Continuous toggle from the distance presets. See **Modifier
> tiers** and **Config variables** below.

---

## 1. Ways to jog

| Source | How | Distance / feed used | Continuous? |
|---|---|---|---|
| **On‑screen jog panel** (X±/Y±/Z± buttons, arrow pad) | Click / press‑and‑hold | The on‑screen selected preset (or a modifier tier) | Hold‑to‑move when the selection is Continuous, or with Shift/Ctrl+Shift |
| **Keyboard — cursor keys** | `←→` = X, `↑↓` = Y, `PgUp/PgDn` = Z, `Home/End` = A | Selected preset / Fast / Slow / Step per modifier | Yes for Fast/Slow/Continuous; single move for Step |
| **Keyboard — letter keys** | `Ctrl+Shift` + `J/H` = X±, `K/L` = Y±, `I/M` = Z±, `U/N` = A± | Selected preset (via the same `JogCommand`) | Per selection |
| **Keyboard — step/feed cycling** | `[` / `]` (or NumPad `4`/`6`) = step −/+, NumPad `8`/`2` = feed +/− | n/a (changes the selection) | n/a |
| **Game controller (XInput)** — D‑pad / mapped buttons | One incremental jog per press | On‑screen selected distance & feed (`SelectedDistance`/`SelectedFeedrate`) | No (single G91 step) |
| **Game controller — bumpers** (LB/RB) | Cycle the on‑screen **feed** preset | n/a | n/a |
| **Game controller — left stick** | X/Y, **proportional** (push harder = faster) | Continuous; max feed = `FeedScale` × panel feed | Yes (cancel on release) |
| **Game controller — triggers** (LT/RT) | Z down/up, proportional | as above | Yes |
| **MDI** | Type `$J=…` | Whatever you type | As written |
| **3D view click‑to‑jog** (`ClickToJog`) | Ctrl+click a point in the toolpath view | Rapid to that X/Y at safe Z | n/a (go‑to move) |
| **Centre button / Go‑To panel** | Bullseye / G28/G30/G5x | Safe‑Z move to a position | n/a (not a jog — see `GotoBaseControl.SafeGoto`) |

The panel buttons, the UI‑mode cursor keys, and the `Ctrl+Shift` letter keys all funnel
through one method — `JogCommand` in `CNC Controls/CNC Controls/JogBaseControl.xaml.cs` —
so they behave identically. The controller D‑pad uses `ControllerMapper.JogStep`, which
mirrors the same on‑screen selection; the analog stick is a separate proportional path
(`ControllerMapper.OnPolled`).

---

## 2. Modifier tiers (keyboard arrows + panel buttons)

Applied identically by the **panel buttons** (`JogCommand`) and the **keyboard** continuous
path (`KeypressHandler.ProcessKeypress`):

| Modifier | Tier | Distance | Feed |
|---|---|---|---|
| *(none)* | Default | panel: `SelectedDistance`; keyboard: the `DefaultSpeedFast` default speed | `SelectedFeedrate` / keyboard default |
| **Shift** | **Fast** (continuous) | full travel (clamped) | `FastFeedrate` |
| **Ctrl** | **Step** (single increment) | `StepDistance` / `$`‑jog‑step | `StepFeedrate` |
| **Ctrl+Shift** | **Slow** (continuous) | full travel (clamped) | `SlowFeedrate` |

Notes
- The tiers are **absolute** — Shift is always Fast, Ctrl+Shift always Slow — for both the
  panel buttons and the keyboard. (`DefaultSpeedFast` only chooses the keyboard's *no‑modifier*
  default speed; the panel's no‑modifier uses the on‑screen selection.)
- Fast/Slow are **continuous** (jog while held, stop on release). A `jogIsContinuous` flag
  drives the stop‑on‑release for both button‑up and key‑up.
- Ctrl = **Step** is a single finite increment per press.
- The **controller has no Shift/Ctrl** — it expresses speed via the bumpers (cycle the
  feed preset) and the analog stick (proportional). The D‑pad does not take modifier tiers.

---

## 3. Three independent inputs — no "jog mode"

There is **no jog-mode setting**. Keyboard, on-screen UI, and controller are independent input
methods that are **always live at the same time**; the input you use *is* the mode. Each keeps its
own configuration:

- **Keyboard** — arrows/letters run the **continuous-jog path** in `KeypressHandler.ProcessKeypress`,
  using the keyboard's own Slow/Fast/Step distance & feed sets, **independent of the on-screen
  selection**. Gated only by the master switch **`KeyboardEnable`** (`IsJoggingEnabled`, default on).
- **On-screen UI** — buttons/2×4 grid call **`JogCommand`** with the on-screen selected distance/feed.
- **Controller** — D-pad mirrors the on-screen selection (`ControllerMapper.JogStep`); analog stick is
  its own proportional path.

All three apply the same modifier tiers (Shift=Fast, Ctrl=Step, Ctrl+Shift=Slow). `IsContinuousJoggingEnabled`
(set from grblHAL capability in `Grbl.cs`) gates the keyboard's Fast/Slow continuous tiers.

Soft limits: when `$20` soft limits are on **and** grblHAL firmware jog-limiting is *off*, jogs are
emitted as `G53` absolute moves clamped to the work envelope (with `$27` pull-off as the margin);
otherwise as `G91` incremental moves. (`softLimits` in `JogBaseControl`.)

---

## 4. Config variables — where changed, where used

> **"Where changed" format:** `container:panel`. The two jog **settings** panels both live on
> **Settings → App**: the "Keyboard jogging" group (`JogConfigControl.xaml`) and the "UI jogging"
> group (`JogUiConfigControl.xaml`). The two **live** panels are placed on the **Grbl** page (or a
> flyout): "UI Jogging" (`UIJoggingControl`/`UIJogGridControl`) and "Kbd Jogging"
> (`KeyboardJoggingControl`/`KbdJogGridControl`). The Kbd Jogging panel is an *assignable* panel —
> add it to your layout to use it; it is auto-hidden only when `KeyboardEnable` is off.

### 4a. App jog settings — `AppConfig.Settings.Jog` (`JogConfig`, persisted in the XML app config)

| Variable | Default | Where changed | Where used / effect |
|---|---|---|---|
| `KeyboardEnable` | **true** | `Settings:App:Keyboard Jogging` (Enable checkbox) | Master switch for keyboard jogging → `IsJoggingEnabled`. Off → keyboard jogging disabled and the live Kbd Jogging panel hidden. (`Mode` and `LinkStepJogToUI` were removed — inputs are always independent.) |
| `KeepUiJogSelection` | false | `Settings:App:UI Jogging` | Persist/restore the on‑screen selection across restarts (`RestoreUiSelection`/`PersistSelection`) |
| `DefaultSpeedFast` | false | `Grbl:Kbd Jogging` *(not in the default layout — add the panel to edit)* | The keyboard's **no‑modifier** default speed (Slow/Fast) |
| `FastFeedrate` | 500 | `Settings:App:Keyboard Jogging` | **Shift** tier feed; keyboard `JogFeedrates[Fast]` |
| `SlowFeedrate` | 200 | `Settings:App:Keyboard Jogging` | **Ctrl+Shift** tier feed; keyboard `JogFeedrates[Slow]` |
| `StepFeedrate` | 100 | `Settings:App:Keyboard Jogging` | **Ctrl** tier feed; keyboard `JogFeedrates[Step]` |
| `FastDistance` | 500 | `Settings:App:Keyboard Jogging` | Keyboard `JogDistances[Fast]` (continuous send length) |
| `SlowDistance` | 500 | `Settings:App:Keyboard Jogging` | Keyboard `JogDistances[Slow]` |
| `StepDistance` | 0.05 | `Settings:App:Keyboard Jogging` | **Ctrl** tier distance; keyboard `JogDistances[Step]` / `grbl.JogStep` |

### 4b. On‑screen jog presets & live selection

The four distance/feed **presets** are edited in `Settings:App:UI Jogging`
(`JogUiMetric`/`JogUiImperial`); the active metric‑vs‑imperial set is chosen by `$13` and loaded
into `JogData` by `JogViewModel.SetMetric`. The **runtime selection** (which preset is active, and
the Continuous toggle) is made on the live `Grbl:UI Jogging` panel.

| Variable | Where changed | Where used |
|---|---|---|
| `Distance[0..3]` (metric .01/.1/1/10, imperial .001/.01/.1/1) | `Settings:App:UI Jogging` | `JogData._distance[]` → `SelectedDistance` |
| `Feedrate[0..3]` (metric 5/100/500/1000, imperial 5/10/50/100) | `Settings:App:UI Jogging` | `JogData._feedRate[]` → `SelectedFeedrate` |
| active distance / feed preset | `Grbl:UI Jogging` (slider / 2×4 grid) | `SelectedDistance` / `SelectedFeedrate` |
| Continuous toggle | `Grbl:UI Jogging` (checkbox) | `JogData.StepSize == Continuous` |

### 4c. Per‑user persisted selection — `Properties.Settings.Default` (user.config)

Written automatically when `KeepUiJogSelection` is on; restored once per run.

| Variable | Meaning | Where used |
|---|---|---|
| `UiJogStep` | last distance‑preset index (0–3) | `RestoreUiSelection` → `_lastStep` |
| `UiJogFeed` | last feed‑preset index (0–3) | `RestoreUiSelection` → `Feed` |
| `UiJogContinuous` | was Continuous checked | `RestoreUiSelection` → `StepSize` |

### 4d. Controller jog settings — `ControllerMap.xml` (changed in `Key Mappings:Controller`; used by `ControllerMapper`)

| Variable | Default | Effect |
|---|---|---|
| `AnalogJogEnabled` | true | Enable left‑stick / trigger proportional jogging |
| `FeedScale` | 2.0 | Analog max feed = `FeedScale` × the on‑screen panel feed |
| `DeadzonePercent` | 21 | Left‑stick deadzone |
| `InvertX/Y/Z` | false | Invert each analog axis |
| Button map | — | D‑pad/buttons → `JogXPlus…`; bumpers → cycle feed preset |

### 4e. Controller ($) settings that affect jogging — live grbl settings

Read from the controller; changed in the **`Settings`** view under the relevant grbl `$`‑settings
group (or by sending `$n=` from the MDI). Container:panel varies by group (e.g. `Settings:Limits`
for `$20`, `Settings:Homing` for `$27`).

| Setting | ioSender name | Effect on jogging |
|---|---|---|
| `$13` | `ReportInches` | Inch/mm — selects the metric vs imperial UI preset set and the `G20/G21` word |
| `$20` | `SoftLimitsEnable` | When on, jogs are envelope‑clamped (`G53` absolute) unless firmware jog‑limiting is on |
| `$27` | `HomingPulloff` | Clamp margin (`limitSwitchesClearance`) kept off the soft‑limit edge |
| `$110–$112` | `MaxFeedRate` | Rapid feed used by the safe‑Z Go‑To / Centre moves (not regular jog) |
| `$130–$132` | `MaxTravel` | Jog clamp envelope and continuous‑jog travel length |
| (grblHAL) | `SoftLimitJogging` | If the firmware limits jog commands itself, ioSender skips client‑side clamping |

---

## 5. Code map

| Concern | File · symbol |
|---|---|
| Shared jog emit + modifier tiers | `CNC Controls/CNC Controls/JogBaseControl.xaml.cs` · `JogCommand` |
| Letter‑key jogs (`Ctrl+Shift`+J/H/K/L…) | same file · `KeyJog*`, `KeyJogCancel` |
| Press‑and‑hold buttons | same file · `JogButton_JogStart` / `_JogEnd` (+ `jogIsContinuous`) |
| Distance/Continuous model | same file · `JogViewModel` (`StepSize`, `DistanceIndex`, `Continuous`, `_lastStep`) |
| Selection persistence | same file · `RestoreUiSelection`, `PersistSelection` |
| Keyboard dispatch + continuous path | `CNC Core/CNC Core/KeypressHandler.cs` · `ProcessKeypress` |
| Jog key table / modes | same file · `jogKeys[]`, `JogMode`, `IsJoggingEnabled`, `IsContinuousJoggingEnabled` |
| Controller D‑pad jog | `CNC Core/CNC Core/ControllerMapper.cs` · `JogStep` |
| Controller analog jog | same file · `OnPolled` |
| Wiring config → handler | `ioSender XL/ioSender XL/JobView.xaml.cs` (≈ lines 348–359) |
| Jog settings UI | `JogConfigControl.xaml`, `JogUiConfigControl.xaml`, `UIJogGridControl.xaml` |
