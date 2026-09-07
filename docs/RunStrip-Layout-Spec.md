# Run strip — target layout spec

User spec, 2026-08-09. **This is the design the web client gets held to.** The WPF implementation is
knowingly throwaway (the desktop client retires at phase H); the *layout* is the durable artifact.

Right half only so far — the left half is a later pass.

## Arrangement, left to right

```
[ button row (existing) ] [ jog pad ] [ JOGGING ] [ SIGNALS ] [ FEEDS AND SPEEDS ]
```

The jog pad is the existing `JogBaseControl ArrowsOnly`. The three headed columns to its right are a
**new control** — see "Why a new control" below.

## Column: FEEDS AND SPEEDS  (rightmost)

Three rows, each `label:  −  [value]  +`

| Row | Label | Value | Bind |
|---|---|---|---|
| 1 | Feed rate | override % | `FeedOverride`, `FeedOverrideDisabled` |
| 2 | Rapids | override %, **capped 100** | `RapidsOverride` |
| 3 | Spindle | override % | `RPMOverride`, `RPMOverrideDisabled` |

Override bytes (send via `Comms.com.WriteBytes`, the pattern in `FeedControl.override_CommandGenerated`):

- Feed: `CMD_FEED_OVR_RESET` / `..._FINE_MINUS` / `..._FINE_PLUS` / `..._COARSE_MINUS` / `..._COARSE_PLUS`
- Rapids: `CMD_RAPID_OVR_RESET` / `..._MEDIUM` / `..._LOW` — **three fixed steps, not continuous**
- Spindle: `CMD_SPINDLE_OVR_*`, same shape as feed

⚠ Rapids has no fine/coarse pair; its minus steps through 100 → 50 → 25. A `−/+` pair has to walk
that ladder rather than nudge by a percentage.

## Column: SIGNALS

Four rows (spec said "three" then listed four — four is what's wanted):

| Row | Label | Letters |
|---|---|---|
| 1 | Limit | X Y Z |
| 2 | Steppers | W F |
| 3 | Probes | H S P |
| 4 | TLO ref | *value* |

Letter key, from `GrblInfo.SignalLetters` = `"XYZABCUVWEPRDHSLTOMF"` positionally against the
`Signals` flags enum:

| Letter | Signal | | Letter | Signal |
|---|---|---|---|---|
| X Y Z A B C U V W | LimitX…LimitW | | H | **Hold input** |
| E | EStop | | S | **Cycle Start input** |
| P | Probe | | T | Optional Stop |
| R | Reset | | L | Block Delete |
| D | Safety Door | | O | Probe Disconnected |
| M | Motor Warning | | F | Motor Fault |

⚠ **W is the W-axis limit, not a stepper fault.** On a 3-axis machine it can never assert. If the
Steppers row wants two meaningful items, **M (motor warning) + F (motor fault)** is the real pair.
Flagged for the user; spec left as written until they say.

TLO ref binds `TloReference` / `IsTloReferenceSet` (null when unset — render absent, not 0).

## Column: JOGGING

| Row | Label | Control |
|---|---|---|
| 1 | Distance | `−  [value]  +` |
| 2 | Speed | `−  [value]  +` |
| 3 | Kbd Def | `200 | 2000` — reuse `KbdDefaultSpeedControl` |
| 4 | Continuous | checkbox |

Distance/Speed currently live in `JogPresetSelector`; Continuous binds
`Keyboard.IsContinuousJoggingEnabled` (shared with the jog tab and keyboard handler, so the two
surfaces cannot disagree).

## Why a new control, not reuse

`OverrideControl` — the obvious candidate for the feeds rows — is **slider**-based (`SliderValue`,
`Ticks`, `Minimum`/`Maximum`). The spec wants a compact `− [value] +` stepper. Same for signals:
`SignalsControl` renders one undifferentiated block with no grouping option.

So: a new `RunStripPanel` UserControl in `CNC Controls`, containing the three headed columns, built
from one small repeated row widget (`label − value +`). Reuse `KbdDefaultSpeedControl` for row 3 of
Jogging; everything else is new markup.

## State of play

Committed (`MainWindow.xaml`): the five-column skeleton and the taller strip, with jog config moved
off the State line and a Continuous checkbox under the pad. That was a *rearrangement* pass — it
proved the column layout but does not meet this spec.

**Next:** build `RunStripPanel` to the above and drop it into columns 2–4, replacing the moved
`JogPresetSelector`/`KbdDefaultSpeedControl`, `SignalsControl` and `FeedControl`.

Every new control needs an inline `x:Uid` as it is created, then `tools/locadd.py` for the 7 locale
CSVs.
