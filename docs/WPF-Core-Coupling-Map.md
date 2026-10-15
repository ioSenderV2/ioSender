# WPF client ↔ CNC.Core coupling map (reference)

*Generated 2026-08-08 by an exhaustive read-only survey (agent-assisted), originally as the work
plan for migrating the WPF client to contracts-only. **That migration was abandoned the same day
(web-first pivot — see Command-Surface-Catalog.md):** the WPF views stay Core-coupled as the
serving host's built-in UI. Kept as reference: it documents exactly what machine access the web
client must replicate, and where the WPF app's couplings live if any ever needs surgery.*

## XAML gate (would have blocked any reference cut — 6 files total)

**Live** (markup compile binds a CNC.Core-assembly type):
- `ioSender XL\MainWindow.xaml` L7/L32 — `GrblViewModel` instantiated as the Window DataContext
  (the root DataContext of the whole app).
- `Grbl Config App\MainWindow.xaml` L7/L14 — same pattern.
- `CNC Controls\GrblConfigControl.xaml` L127/L137 — `GrblSettingGroup` +
  `GrblSettingDetails` as HierarchicalDataTemplate/DataTemplate DataTypes.
- `CNC Controls\GCodeListControl.xaml` L95 — `{x:Static core:GCodeRunTimeIndex.Instance}`.

**Dead** (xmlns declared, zero uses — safe cleanup anytime):
`JogBaseControl.xaml` L9, `MachinePositionFlyout.xaml` L8.

**Already-crossed pattern** (namespace mapped against migrated assemblies): DRO/Trinamic/Tool/
Spindle/Offset/SharedStyles → `assembly=CNC.Contracts` (CNC.GCode ns); THCMonitor/SignalsControl →
`assembly=CNC.Contracts` (CNC.Core ns).

## Headline numbers (CNC Controls: 88 of 177 .cs files coupled)

- **Already Core-clean, no work ever needed**: `UIViewModel.cs` (28-file fan-in!), `UIUtils.cs`,
  `ConfigPanel.cs`, and ~13 files whose `using CNC.Core` is satisfied by CNC.Common types alone.
- **The keystone**: `AppConfig.cs` — 86-file fan-in, touches 6 coupling families
  (ConfigStore 35, GrblInfo 21, Comms 19, GrblViewModel 11, Macro 8, all 4 stream types).
- **Densest single couplings**: `MachineSetupWizard.xaml.cs` GrblInfo ×66;
  `Widget.cs` GrblSettingDetails ×46; `GrblConfigControl.xaml.cs` GrblSettings ×28.
- Two cross-cutting families beyond the obvious ones: **ConfigStore** (settings persistence,
  8 files) and the **Macro model** (`Macro`/`MacroState`/`MacroStatusRow` — data, not engine,
  8 files).

## Wave structure (as it would have been; now = a map of what the web client replaces)

| Wave | Content | Files |
|---|---|---|
| 0 | XAML gate above | 4 live + 2 dead |
| 1 | Pure GrblViewModel reads (mechanical twin swap) | 11 |
| 2 | Statics: GrblInfo/GrblSettings/parser-state/alarm+error tables | 21 |
| 3 | Direct Comms writers (→ Command-Surface-Catalog.md) | 20 |
| 4 | GCode document model (hub: `GCode.cs`, `GCode.File` singleton, 15-file fan-in) | 13 |
| 5 | Server engines (JobRunner/MacroRunner/SDCard/AtcMacros/Validator) | 9 |
| 6 | Connection management (PortDialog/streams) | 4 |
| 7 | Core infra: ConfigStore/SecretStore/DebugLog/Macro model/FeedsSpeeds/StrokeFont | 22 |

**Hard integration files spanning ≥4 families** (single-owner territory): AppConfig.cs,
JobControl.xaml.cs (also 10-file fan-in), SDCardView.xaml.cs, MachineSetupWizard.xaml.cs,
JogBaseControl.xaml.cs, MacroProcessor.cs, KeypressHandler.cs, ControllerMapper.cs,
WorkOrderView.xaml.cs, GrblConfigControl.xaml.cs, TrinamicView.xaml.cs, GCodeWrap.cs.

**Sibling projects** still coupled (become relevant only if the migration is ever revived):
ioSender XL 9 files, Probing 8, Lathe 6, GCodeViewer 5, Grbl Config App 1, Converters 1.
`CNC Controls.csproj` never gained a CNC.Client reference (was step zero of the abandoned plan).
