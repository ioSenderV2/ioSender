# Command-surface catalog: every direct machine write in the client code

*Generated 2026-08-08 by an exhaustive read-only survey (agent-assisted, spot-verified), as the
requirements list for growing the wire command surface (`CNC Contracts\CommandMessages.cs`:
`IMachineCommands` / `IMachineRealtimeChannel`). Context: the web-first pivot — the WPF views stay
as-is, but ANY client (web first) reaches the machine only through these channels, so every raw
write below is a command the wire must eventually carry. Scope: all projects except CNC Core /
CNC Contracts / CNC Common / CNC Client; obj/bin excluded. **~235 call sites across 40 files.***

## The ways a client reaches the machine today

| Helper | Where it ends | Sites |
|---|---|---|
| `Comms.com.WriteByte/WriteBytes` | raw realtime byte | 60 |
| `Comms.com.WriteCommand/WriteString` | raw line | 33 |
| `Comms.com.AwaitAck(cmd)` / `GetReply(cmd)` | writes **and** waits (`Comms.cs:177-179`) | easy to miss |
| `GrblViewModel.ExecuteCommand(string)` | `ApplyCommand` -> `MDI` prop -> `JobControl.OnDataContextPropertyChanged` -> `runner.SendCommand` -> wire | ~120 |
| `Grbl.Reset()` | `WriteByte(CMD_RESET)` + 20 ms sleep | 6 client sites |
| `Grbl.WaitForResponse/WaitForIdle(cmd)` | `ExecuteCommand` + block on ack | 5 client sites |
| `ProbingViewModel.WaitForResponse/WaitForIdle` | wraps `ExecuteCommand` + ack — a **second, parallel** ack-wait helper | 26 sites |
| `OffsetView.WriteCommandAndWait` (`:265`) | `WriteCommand` + ack — a **third** | |
| `GrblConfigControl.SetSetting` (`:269`) | `WriteCommand` + ack — a **fourth** | |
| `JobControl.runner.SendCommand` | JobRunner queue -> wire | 2 direct |
| `MacroProcessor.Run(...)` | load program -> `runner.Run(0,false)` — maps to `RunProgram` | 6 |

## A. Realtime bytes already covered by `IMachineRealtimeChannel` (45 sites)

- `CNC Controls\KeypressHandler.cs` — 17 override/toggle handlers (`:706-:814`), 1:1 enum matches.
  (The `:784`/`:790` spindle coarse pair was SWAPPED — fixed 2026-08-08, do not reintroduce.)
- `CNC Controls\JobControl.xaml.cs` — `:551/:557/:563/:569` feed overrides, `:599/:608` reset,
  `:644` `btnHold_Click` = **the run bar's Feed Hold**.
- `CNC Controls\ControllerMapper.cs` (gamepad) — `:306` CycleStart, `:309` FeedHold, `:312`
  SoftReset, `:315` SpindleStop, `:523` JogCancel.
- `CNC Controls\JogBaseControl.xaml.cs:240` JogCancel (spins on `Comms.com.OutCount` at `:239`).
- `CNC Controls\FixtureEditDialog.xaml.cs:297/:302` FeedHold + SoftReset (Cancel path).
- `CNC Controls\AppConfig.cs:2286` AutoReportingToggle, `:2476/:2541` SoftReset.
- `ioSender XL\MainWindow.xaml.cs:1289` AutoReportingToggle (Window_Closing).
- `Grbl.Reset()` -> SoftReset: `JobControl.xaml.cs:709/:716/:725` (leave Check mode),
  `StatusControl.xaml.cs:109/:162/:186`.
- ⚠ **Realtime bytes disguised as TEXT** (must become `IMachineRealtimeChannel.Send`, NOT `Mdi` —
  a naive ExecuteCommand->Mdi sweep reintroduces head-of-line blocking):
  `SpindleControl.xaml.cs:136` (SpindleStop as string), `CoolantControl.xaml.cs:56/:58`
  (`GrblCommand.Flood`/`.Mist` are single control chars), `:60` (Fan0 toggle).
- Byte-batch writes (generated override sequences -> N `Send` calls):
  `FeedControl.xaml.cs:68`, `SpindleControl.xaml.cs:146`.

## B. Realtime bytes with NO enum member yet (18 sites) — new `RealtimeCommand` members

- **StatusReport / StatusReportAll** (0x80/0x87) — 10 sites: `AppConfig.cs:2266/:2459`,
  `OffsetView.xaml.cs:366/:410`, `ToolView.xaml.cs:219/:254`, `ProbingView.xaml.cs:356/:488`,
  `ProbingViewModel.cs:345`, `Probing\Program.cs:275`. *Design note: each means "fresh snapshot
  NOW" — may become `RefreshState()` on the state stream instead of a realtime member.*
- **Stop** (0x19, grblHAL realtime Stop ≠ StopJob) — `AppConfig.cs:2434`,
  `Probing\Program.cs:132/:376`.
- **ProbeConnectedToggle** (0xA4) — `KeypressHandler.cs:802`, `ProbingView.xaml.cs:264`.
- **Fan0Toggle** (0x8A) — `KeypressHandler.cs:766`, `CoolantControl.xaml.cs:60`.
- **PidReport** (0xA2, via AwaitAck) — `PIDLogView.xaml.cs:211`.

## C. Free-text g-code -> `Mdi` (existing) — ~48 sites

`MDIControl.xaml.cs:114/:115/:133` · `ConsoleControl.xaml.cs:164/:170` ·
`GCodeListControl.xaml.cs:631/:641` · `OutlineBaseControl.xaml.cs:86` ·
`SpindleControl.xaml.cs:127/:140/:154/:157/:159` · `WorkParametersControl.xaml.cs:95/:103/:110` ·
`DROControl.xaml.cs:259/:262` · `JobControl.xaml.cs:456` (**the funnel every ExecuteCommand flows
through — the natural Mdi seam**) · `HeightMapView.xaml.cs:269` ·
`ioSender XL\JobView.xaml.cs:672-:695` (**Camera_MoveOffset: 7 raw WriteString + 1 ExecuteCommand
mixed in one method; the raw ones skip ParseBlock**) · `Probing\Program.cs:258/:390/:436/:485` ·
`ProbingViewModel.cs:184/:211` · `ProbingView.xaml.cs:367/:409/:412/:414` ·
`CsSelectControl.xaml.cs:58` · `ToolLengthControl.xaml.cs:133/:163/:180/:198` ·
`Probing HeightMapControl.xaml.cs:203/:206`.

## D. Jog -> `Jog` (existing) — 10 sites

`JogBaseControl.xaml.cs:610/:726/:727/:779/:780` · `ControllerMapper.cs:393` ·
`ControllerMapper.cs:506` (**deliberately raw WriteCommand for thread-affinity, comment at `:408` —
the wire Jog must satisfy the 20 Hz gamepad tick off the UI thread**) ·
`GCodeViewer\Renderer.xaml.cs:572/:573` · `ProbingViewModel.cs:326`.

## E. Run control -> `RunProgram`/`StopJob` (existing) — 10 sites

`JobControl.xaml.cs:965` (run bar) / `:976` (RunMacro) / `:403` Stop / `:650` Abort / `:411`
CycleStart · MacroProcessor.Run clients: `MacroExecuteControl.xaml.cs:121`,
`JobControl.xaml.cs:630`, `AutoSquareWizard.xaml.cs:685`,
`FixtureEditDialog.xaml.cs:386/:687/:764`, `MachineSetupWizard.xaml.cs:1409`.

## Proposed NEW `IMachineCommands` members, ranked by sites absorbed

| Rank | Command | Sites | Notes |
|---|---|---|---|
| 1 | `SetCoordinateOffset` | 25 | G92/G10 L1/L2/L20/G28.1/G30.1 — **ack matters** ("accepted" ≠ enough); OffsetView/OffsetFlyout/ToolView/DRO + 6 probing controls + FixtureEditDialog:587 |
| 2 | `Unlock` ($X) | 14 | AppConfig Restart ×5, StatusControl ×3, JobControl ×2, ControllerMapper, FixtureEditDialog, waitidle-probe ×2 |
| 3 | FileSystem group | 12 | all SDCardView: `ReadRemoteFile` ($F<=), `RunRemoteFile` ($F=), `DeleteRemoteFile` ($FD=), `SetWorkingDirectory` ($CWD=), `GetWorkingDirectory` ($PWD), `Reboot` ($REBOOT); $FR = existing `RewindJob`; YModem upload -> `FileUpload` (a stream, own channel shape) |
| 4 | `QueryParserState`/`QueryParameters` ($G/$#) | 11 | read-back after every offset write; 8 probing controls + ToolLength ×4 |
| 5 | `Probe` (3-phase G38.2 fast/latch/slow) | 9 | Edge/EdgeInt/StartJob ×3 each — a protocol, not 3 MDI lines |
| 6 | `GotoPosition` (Safe-Z ordered G53 G0) | 9 | GotoBaseControl SafeGoto/SafeGotoMachine — ordering must stay atomic |
| 7 | `SetSetting` ($n=v) + `ResetSettings` ($RST=$) | 8 | GrblConfigControl ×4, TrinamicView, JobView WriteFirmwareJog, Grbl Config App, waitidle-probe |
| 8 | `Home` ($H) | 7 | StatusControl ×4, JobControl, ControllerMapper, AutoSquareWizard |
| 9 | `TrinamicDiagnostics` (M122/M914) | 6 | all TrinamicView, raw `\r` literals |
| 10 | `WaitForIdle` | 4+ | a *protocol* needing a home: ProbingViewModel ×2, CenterFinder, HeightMap ×2, Program:510 |
| 11 | `MacroCall` (G65P{n}) | 1(+3) | SDCardView:760; ProbeSelect (G65P5Q{n}) ×3 if unified |
| 12 | `SetRtc` / `Reboot` / `PidReport` | 1 each | JobControl:528 / SDCardView:180 / PIDLogView:211 |

## Things that will bite during migration

1. **Four separate ack-wait helpers** all reimplement "write, pump DoEvents, wait for ok".
   `IMachineCommands` returns `Task<CommandResult>` where Success = *accepted* — but 40+ sites
   need *completed*. **The accepted-vs-completed gap is THE design decision** blocking the
   offset/settings/probe groups.
2. **Realtime bytes disguised as text** (Coolant/Spindle controls) — route to the realtime
   channel, never Mdi.
3. **`ControllerMapper.cs:506` thread affinity** — wire Jog must work at 20 Hz off the UI thread.
4. **`JobView.Camera_MoveOffset` mixed raw/parsed paths** — behaviour differs subtly (ParseBlock).
5. The `KeypressHandler` spindle-coarse byte swap (fixed 2026-08-08) — don't resurrect it from an
   old diff.

## Non-WPF tools (separate decision)

`tools/waitidle-probe` ($X, $n=v, ? poller) and `tools/net8-smoke` (?, $I) hit the raw API as
harnesses; `tools/websocket-probe` exercises the transport itself (raw access is the point —
leave); `tools/delta-probe` uses a fake Comms (safe).

## Clean projects (nothing to migrate)

CNC Controls Camera, Dragknife, Lathe, Converters, Fusion Addin.
