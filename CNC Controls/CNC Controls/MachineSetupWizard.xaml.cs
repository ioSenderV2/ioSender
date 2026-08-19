/*
 * MachineSetupWizard.xaml.cs - part of CNC Controls library
 *
 * Machine Setup Wizard: a single-page configuration of the machine-description grbl settings
 * (work area / travel, home corner, per-axis steps/mm, max rate, direction & limit inversion,
 * homing and soft limits). Reads firmware capabilities (NEWOPT / NumAxes / force-set-origin) to
 * gate the questions, then writes the resulting $n settings via GrblSettings.Save(). Everything is
 * visible at once - pick a machine to seed the fields, click the home corner, fill the axis table,
 * preview the pending $ writes, then Apply.
 *
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    #region Wizard data model

    // One row per machine axis - holds the user's answers and the live limit-switch state.
    public class AxisSetup : ViewModelBase
    {
        private double _maxTravel, _maxRate, _stepsPerMm;
        private bool _homeAtMin, _invertDirection, _limitNormallyClosed, _limitActive;

        public AxisSetup(string letter, int index)
        {
            Letter = letter;
            Index = index;
        }

        public string Letter { get; private set; }
        public int Index { get; private set; }
        public int Bit { get { return 1 << Index; } }

        public double MaxTravel { get { return _maxTravel; } set { _maxTravel = value; OnPropertyChanged(); } }
        // Steps/mm ($100-$102) - deterministic from the drive; the value a machine preset is most useful for.
        public double StepsPerMm { get { return _stepsPerMm; } set { _stepsPerMm = value; OnPropertyChanged(); } }
        // Max feed rate ($110-$112), mm/min (deg/min for rotaries).
        public double MaxRate { get { return _maxRate; } set { _maxRate = value; OnPropertyChanged(); } }
        // Sensible starting rate for typical CNC steppers: slower vertical Z/W, faster horizontals, deg/min for rotaries.
        public double DefaultMaxRate { get { return (Letter == "A" || Letter == "B" || Letter == "C") ? 3600d : ((Letter == "Z" || Letter == "W") ? 500d : 2000d); } }
        // Home switch at the minimum (negative) end of travel -> machine travels positive ($23 bit set).
        public bool HomeAtMin { get { return _homeAtMin; } set { _homeAtMin = value; OnPropertyChanged(); } }
        // Z is fixed to home at the top of the gantry ($23 Z bit always clear), so its direction isn't user-editable.
        public bool HomingDirEditable { get { return Letter != "Z"; } }
        // Reverse this axis' motor direction ($3 direction-invert mask).
        public bool InvertDirection { get { return _invertDirection; } set { _invertDirection = value; OnPropertyChanged(); } }
        public bool LimitNormallyClosed { get { return _limitNormallyClosed; } set { _limitNormallyClosed = value; OnPropertyChanged(); } }
        // Live: this axis' limit input is currently asserted (updated from GrblViewModel.Signals).
        public bool LimitActive { get { return _limitActive; } set { _limitActive = value; OnPropertyChanged(); } }
    }

    public class MachineSetupModel : ViewModelBase
    {
        private bool _hasLimitSwitches = true, _hardLimits = true, _homingEnable = true, _softLimits = true;
        private bool _forceSetOrigin = true;
        private double _homingPulloff = 1d, _homingFeed = 25d, _homingSeek = 500d;
        private int _homingDebounce = 250;

        public ObservableCollection<AxisSetup> Axes { get; } = new ObservableCollection<AxisSetup>();

        public bool HasLimitSwitches { get { return _hasLimitSwitches; } set { _hasLimitSwitches = value; OnPropertyChanged(); } }
        public bool HardLimitsEnable { get { return _hardLimits; } set { _hardLimits = value; OnPropertyChanged(); } }
        public bool HomingEnable { get { return _homingEnable; } set { _homingEnable = value; OnPropertyChanged(); } }
        // $22 bit 3 (grblHAL): set machine origin to 0 at home, letting axes travel positive per $23 - required
        // for the home-corner choice to take effect and for the 3D view to orient to the real home corner.
        public bool ForceSetOrigin { get { return _forceSetOrigin; } set { _forceSetOrigin = value; OnPropertyChanged(); } }
        public bool SoftLimitsEnable { get { return _softLimits; } set { _softLimits = value; OnPropertyChanged(); } }

        public double HomingPulloff { get { return _homingPulloff; } set { _homingPulloff = value; OnPropertyChanged(); } }
        public double HomingFeed { get { return _homingFeed; } set { _homingFeed = value; OnPropertyChanged(); } }
        public double HomingSeek { get { return _homingSeek; } set { _homingSeek = value; OnPropertyChanged(); } }
        public int HomingDebounce { get { return _homingDebounce; } set { _homingDebounce = value; OnPropertyChanged(); } }
    }

    // Row status for a live-apply confirmation grid (PendingChangesDialog's optional apply mode) - a plain
    // Preview list (MachineSetupWizard's own use) leaves every row at Pending and never changes it.
    public enum SettingApplyStatus
    {
        Pending,
        NotSupported,
        Applied,
        Failed,
        RolledBack
    }

    // One pending change shown on the review page (or, in PendingChangesDialog's confirm+apply mode, one
    // row of a live restore - Status drives that row's colour as it's written).
    public class SettingChange : ViewModelBase
    {
        public string Setting { get; set; }
        public string Name { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }

        private SettingApplyStatus _status = SettingApplyStatus.Pending;
        public SettingApplyStatus Status { get { return _status; } set { _status = value; OnPropertyChanged(); } }
    }

    #endregion

    public partial class MachineSetupWizard : UserControl, IGrblConfigTab, ISettingsPageProvider
    {
        private GrblViewModel model = null;
        private bool _subscribed = false;
        private bool _restoringSelection = false;   // suppress persisting while we drive the dropdowns in code

        // True while LoadCurrentSettings/BuildAxes are driving the fields from the controller, so the
        // PropertyChanged storm they cause is not mistaken for the operator typing.
        private bool _loading = false;
        // True once the operator has actually changed something on this page. The refresh below refuses to
        // discard their work, and this is the only trustworthy test for that: Changes.Count is NOT, because a
        // stale table makes Changes non-empty all by itself - which is exactly the state we need to refresh
        // out of.
        private bool _userEdited = false;
        private bool _settingsHookAttached = false;
        private Window _fwInfoWindow = null;
        private FirmwareUpdateManager.ReleaseInfo _pendingFwRelease = null;
        private string _lastFirmwareKey = null;   // GrblInfo.Version+"|"+DriverSha as last shown - see Model_PropertyChanged

        public MachineSetupWizard()
        {
            InitializeComponent();

            // The step tabs used to be made bindable to a key here (TabKeyBinder.AttachTabBinding adds a
            // shortcut badge and a right-click bind menu to the tab header). The step tab strip no longer
            // renders - the steps are nodes in the navigation tree - so that UI was unreachable. Dropped
            // deliberately (user, 2026-08-03); AppConfig.ApplyOneTimeFixups strips shortcuts already
            // persisted against these ids. Top-level tabs, Probing and the Lathe wizards still have real
            // tab strips and keep theirs.

            model = DataContext as GrblViewModel;
            DataContextChanged += (s, e) => { if (DataContext is GrblViewModel) model = (GrblViewModel)DataContext; };

            // Recompute the pending-change set (and Apply's enabled state) whenever the model-level settings change.
            Setup.PropertyChanged += OnSetupChanged;

            // Step 5 hosts the probe library inline (live ObservableCollection - add/edit/delete in place).
            grdProbes.ItemsSource = ProbeDefinitions.Items;

            // Step 6 hosts the fixture library inline, same pattern as Probes. Starts empty (no prepopulation).
            grdFixtures.ItemsSource = Fixtures.Items;

            UpdateTloRefValueDisplay();

            // Colour the step tabs from the start - incomplete steps show red immediately, before any load.
            RefreshStepColors();
        }

        public GrblConfigType GrblConfigType { get { return GrblConfigType.MachineSetup; } }

        #region Dependency properties bound from XAML

        // Raised when the user presses Apply with a machine specified (settings written + remembered). The
        // shell uses this to leave the first-run "set up your machine" gate and return to the normal UI.
        public static event System.Action SetupApplied;

        // Lets code outside this class (e.g. a Restore from a backup file) prompt the same re-check the
        // wizard's own Apply button does, so an already-open Machine Setup reflects settings changed
        // behind its back instead of showing stale missing-parameter state.
        public static void NotifySettingsChangedExternally() => SetupApplied?.Invoke();

        public MachineSetupModel Setup { get; } = new MachineSetupModel();
        public List<MachineManufacturer> Manufacturers { get; } = MachineCatalog.Manufacturers;

        public static readonly DependencyProperty PresetNoteProperty = DependencyProperty.Register(nameof(PresetNote), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata(string.Empty));
        public string PresetNote { get { return (string)GetValue(PresetNoteProperty); } set { SetValue(PresetNoteProperty, value); } }

        public static readonly DependencyProperty HomeCornerTextProperty = DependencyProperty.Register(nameof(HomeCornerText), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata("No corner selected."));
        public string HomeCornerText { get { return (string)GetValue(HomeCornerTextProperty); } set { SetValue(HomeCornerTextProperty, value); } }

        public ObservableCollection<SettingChange> Changes { get; } = new ObservableCollection<SettingChange>();

        #endregion

        #region Startup setup gate

        // Per-step completeness for the startup setup gate. Returns the first step (1-6) not yet satisfied,
        // or 0 when fully set up. All checks read live controller/app state populated on connect ($$, $I).
        // hardGateOnly=true is the STARTUP gate's own check (MainWindow.ForceMachineSetupIfNeeded) - only
        // steps 1-4 (machine identity/homing/axis/limits) block the app from opening at all, since the
        // machine genuinely can't be jogged or run anything without them. Steps 5 (probe definitions, now
        // seeded with generic defaults on a fresh install - ProbeDefinitions.SetItems) and 7 (ATC macros)
        // are real requirements too, but only for probing/ATC-dependent work specifically - they're deferred
        // to a readiness check when Start Job or Odd Jobs Setup is actually opened (StartJobView's own
        // check), not forced on every startup. Every OTHER caller (tab coloring, IsSetupComplete, etc.) keeps
        // checking all steps via the false default.
        public static int FirstIncompleteStep(bool hardGateOnly = false)
        {
            // Can't judge the machine until the controller has reported version + settings ($I/$$). Returning
            // 0 (complete) here means a not-yet-ready / transient state never fires the setup gate.
            if (string.IsNullOrEmpty(GrblInfo.Version) || !GrblSettings.IsLoaded)
                return 0;

            // 1 - Machine: a machine identity has been picked (or Custom applied).
            if (string.IsNullOrEmpty(AppConfig.Settings.Base.LastMachine))
                return 1;

            // 2 - Home position: homing must be configured so a home corner is defined ($22/$23).
            if (!GrblInfo.HomingEnabled)
                return 2;

            // 3 - Axis: every axis needs steps/mm ($100-$102) and max travel ($130-$132).
            for (int i = 0; i < GrblInfo.NumAxes; i++)
                if (GrblSettings.GetDouble(GrblSetting.TravelResolutionBase + i) <= 0d ||
                    GrblSettings.GetDouble(GrblSetting.MaxTravelBase + i) <= 0d)
                    return 3;

            // 4 - Homing & limits: at least one of soft ($20) / hard ($21) limit protection enabled.
            if (GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) != 1 &&
                GrblSettings.GetInteger(GrblSetting.HardLimitsEnable) != 1)
                return 4;

            if (hardGateOnly)
                return 0;

            // 5 - Probe definitions: at least one defined (Load Stock / probing need it).
            if (ProbeDefinitions.Items.Count == 0)
                return 5;

            // Step 6 (Fixture definitions) is NOT gating - fixtures aren't required for basic machine operation.

            // 7 - Controller macros: on an ATC-capable controller every required macro must be present and
            // current. Query the filesystem (GetStatus) rather than trusting the ATC flag - the flag won't
            // notice a macro the user deleted or edited by hand.
            if (GrblInfo.HasFS && (GrblInfo.AtcMacrosRequired || GrblInfo.HasATC))
            {
                var macros = AtcMacros.GetStatus(Grbl.GrblViewModel);
                _macroStatus = macros;   // share with the tab-color cache - see the field's comment
                var bad = macros.Where(r => r.State != AtcMacros.MacroState.Installed).ToList();
                if (bad.Count > 0)
                {
                    // TEMP DIAGNOSTIC (2026-07-19) - "all green but the gate trips anyway" investigation.
                    // Reported intermittent/random - most likely the same right-after-reset filesystem-
                    // listing race ForceMachineSetupIfNeeded's own 1200ms re-check was added for (a synchronous
                    // GetStatus filesystem listing coming back empty/stale immediately post-reset), but this
                    // logs the OFFENDING macro name+state+size+FS every time this trips, from every caller
                    // (not just the wizard's own gate), to actually pin down which macro/state it is instead
                    // of guessing. Remove once resolved.
                    ConsoleLog.Write("[MachineSetupWizard] FirstIncompleteStep: step 7 tripped - " +
                        string.Join(", ", bad.Select(r => string.Format("{0}={1}(size={2},fs={3})", r.Name, r.State, r.Size, r.FS))));
                    return 7;
                }
            }

            return 0;
        }

        public static bool IsSetupComplete { get { return FirstIncompleteStep() == 0; } }

        private static string StepName(int step)
        {
            switch (step)
            {
                case 1: return "Machine - pick your machine";
                case 2: return "Home position - set up homing";
                case 3: return "Axis information - steps/mm and travel";
                case 4: return "Homing & limits - enable limit protection";
                case 5: return "Probe definitions - define a probe";
                case 7: return "Controller macros - install ATC macros";
                default: return string.Empty;
            }
        }

        // Select the given step's tab (1-5, 7 - step 6/Fixtures is not gated, so never targeted by the setup
        // gate) and note it in the status line. Used by the startup gate.
        // Tab order is Overview(0), Machine(1), Home(2), Axis(3), Homing(4), Probes(5), Fixtures(6), Macros(7).
        public void GoToStep(int step)
        {
            if (tabSteps != null && step >= 1 && step <= 7)
                tabSteps.SelectedIndex = step;

            if (txtStatus != null)
                txtStatus.Text = step <= 0 ? "Machine setup complete." : ("Next: step " + step + " - " + StepName(step));
        }

        // Drill into a setup step from a "Tab.MachineSetup.*" keyboard shortcut (via the host's ITabBindingHost).
        // Returns false (no change) when the step tab is not present.
        // ---- navigation pages (docs/Architecture-Settings-Nav-Overhaul.md) ----------------------
        // The wizard's step tabs (and the Calibration step's own two sub-tabs) are nodes in the Machine
        // Setup tree now. The wizard is NOT taken apart: it stays one control with every x:Name and every
        // selection hook intact, and ShowPage() just drives the underlying TabControls - so
        // Steps_SelectionChanged / Calibration_SelectionChanged keep firing exactly as before.

        // Must match the Calibration page's own Key exactly - the host looks the parent up by key, and a
        // near-miss fails silently by dropping the children at top level instead of under the heading.
        public const string CalibrationCategoryKey = "Tab.MachineSetup.Calibration";

        // The nav key of whatever step is selected right now, so the host can mirror a selection the
        // wizard made itself (GoToStep from the startup setup gate) back into the tree.
        public string SelectedStepKey()
        {
            var tab = tabSteps?.SelectedItem as TabItem;
            if (tab == null)
                return null;
            if (tab == tabStepOverview) return "Tab.MachineSetup.Overview";
            if (tab == tabStepMachine) return "Tab.MachineSetup.Machine";
            if (tab == tabStepHome) return "Tab.MachineSetup.Home";
            if (tab == tabStepAxis) return "Tab.MachineSetup.Axis";
            if (tab == tabStepHoming) return "Tab.MachineSetup.Homing";
            if (tab == tabStepProbes) return "Tab.MachineSetup.Probes";
            if (tab == tabStepFixtures) return "Tab.MachineSetup.Fixtures";
            if (tab == tabStepMacros) return "Tab.MachineSetup.Macros";
            if (tab == tabStepSimulator) return "Tab.MachineSetup.Simulator";
            if (tab == tabStepCalibration)
                return tabCalibration?.SelectedItem == tabCalSquareness
                     ? "Tab.MachineSetup.CalSquareness" : "Tab.MachineSetup.CalStepper";
            return null;
        }

        private static string Localized(string key, string fallback)
        {
            var s = LibStrings.FindResource(key);
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }

        // A step header is a plain string (Overview), the numbered colour-graded TextBlock, or - for every
        // step that is bindable to a key - a TabHeaderControl WRAPPING one of those, because
        // AttachTabBinding re-parents the original header into a wrapper carrying the shortcut badge and
        // the right-click bind menu. Casting to TextBlock/string therefore came back empty for exactly the
        // bound steps, which is why only the two calibration sub-tabs (not bindable) had labels.
        // TabHeaderControl.ToString() returns its label for precisely this case.
        private static string HeaderText(TabItem tab)
        {
            if (tab == null)
                return string.Empty;

            var tb = tab.Header as TextBlock;
            if (tb != null)
                return tb.Text;

            var str = tab.Header as string;
            if (!string.IsNullOrEmpty(str))
                return str;

            return tab.Header == null ? string.Empty : tab.Header.ToString();
        }

        public IEnumerable<SettingsSubPage> GetPages()
        {
            var pages = new List<SettingsSubPage>
            {
                new SettingsSubPage("Tab.MachineSetup.Overview", HeaderText(tabStepOverview), this) { IndexRoot = tabStepOverview.Content as FrameworkElement },
                new SettingsSubPage("Tab.MachineSetup.Machine", HeaderText(tabStepMachine), this) { IndexRoot = tabStepMachine.Content as FrameworkElement, Status = () => hdrMachine.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Home", HeaderText(tabStepHome), this) { IndexRoot = tabStepHome.Content as FrameworkElement, Status = () => hdrHome.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Axis", HeaderText(tabStepAxis), this) { IndexRoot = tabStepAxis.Content as FrameworkElement, Status = () => hdrAxis.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Homing", HeaderText(tabStepHoming), this) { IndexRoot = tabStepHoming.Content as FrameworkElement, Status = () => hdrHoming.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Probes", HeaderText(tabStepProbes), this) { IndexRoot = tabStepProbes.Content as FrameworkElement, Status = () => hdrProbes.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Fixtures", HeaderText(tabStepFixtures), this) { IndexRoot = tabStepFixtures.Content as FrameworkElement, Status = () => hdrFixtures.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Macros", HeaderText(tabStepMacros), this) { IndexRoot = tabStepMacros.Content as FrameworkElement, Status = () => hdrMacros.Foreground },
                new SettingsSubPage("Tab.MachineSetup.Calibration", HeaderText(tabStepCalibration), null) { Status = () => hdrCalibration.Foreground },
                new SettingsSubPage("Tab.MachineSetup.CalStepper", Localized("SettingsPageCalStepper", "Stepper"), this)
                    { IndexRoot = tabCalStepper.Content as FrameworkElement, Parent = CalibrationCategoryKey, IsAvailable = () => tabCalStepper.IsEnabled },
                new SettingsSubPage("Tab.MachineSetup.CalSquareness", HeaderText(tabCalSquareness), this)
                    { IndexRoot = tabCalSquareness.Content as FrameworkElement, Parent = CalibrationCategoryKey },
                new SettingsSubPage("Tab.MachineSetup.Simulator", HeaderText(tabStepSimulator), this)
                    { IndexRoot = tabStepSimulator.Content as FrameworkElement, Status = () => hdrSimulator.Foreground, IsAvailable = () => tabStepSimulator.Visibility == Visibility.Visible }
            };
            return pages;
        }

        public void ShowPage(string key)
        {
            // Calibration's children select the Calibration step AND the matching sub-tab. Order matters:
            // set the sub-tab first, so entering the step activates the right wizard rather than the
            // previously selected one and then immediately switching.
            if (key == "Tab.MachineSetup.CalStepper" || key == "Tab.MachineSetup.CalSquareness")
            {
                tabCalibration.SelectedItem = key == "Tab.MachineSetup.CalStepper" ? tabCalStepper : tabCalSquareness;
                tabSteps.SelectedItem = tabStepCalibration;
                return;
            }
            SelectSubTab(key);
        }

        // Raised whenever the per-step grading is recomputed, so the navigation tree can restate the
        // status dots it took over from the (no longer rendered) tab headers.
        public event EventHandler StepStatusChanged;

        public bool SelectSubTab(string id)
        {
            TabItem target;
            switch (id)
            {
                case "Tab.MachineSetup.Overview": target = tabStepOverview; break;
                case "Tab.MachineSetup.Machine": target = tabStepMachine; break;
                case "Tab.MachineSetup.Home": target = tabStepHome; break;
                case "Tab.MachineSetup.Axis": target = tabStepAxis; break;
                case "Tab.MachineSetup.Homing": target = tabStepHoming; break;
                case "Tab.MachineSetup.Probes": target = tabStepProbes; break;
                case "Tab.MachineSetup.Fixtures": target = tabStepFixtures; break;
                case "Tab.MachineSetup.Macros": target = tabStepMacros; break;
                case "Tab.MachineSetup.Calibration": target = tabStepCalibration; break;
                case "Tab.MachineSetup.Simulator": target = tabStepSimulator; break;
                default: target = null; break;
            }

            if (target == null || !tabSteps.Items.Contains(target))
                return false;

            tabSteps.SelectedItem = target;
            return true;
        }

        // Per-step status for tab colouring: green = complete, orange = needs attention, red = not started.
        private enum StepState { Complete, NeedsAttention, NotStarted }

        private static readonly Brush StepGreen = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly Brush StepOrange = new SolidColorBrush(Color.FromRgb(0xE6, 0x5A, 0x00));
        private static readonly Brush StepRed = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

        // Last macro-status query. STATIC/shared (not per-instance): FirstIncompleteStep() below already
        // calls AtcMacros.GetStatus() to decide whether to trip the setup gate - the exact same filesystem
        // listing + checksum read the step-7 tab color needs. Sharing the result means the color is correct
        // the moment the wizard opens (e.g. forced open by the gate), not only after the user has manually
        // clicked into the Controller Macros step once. Was per-instance before, which silently discarded
        // the gate's own query and left the header uncolored until independently re-queried by tab-visit.
        private static System.Collections.Generic.List<AtcMacros.MacroStatusRow> _macroStatus;

        // Colour every graded step tab from its current state. Fixtures (6) and Build simulator (8) are
        // optional/non-gating - they can never block setup completion - so they're always coloured green
        // ("passed its gate" because there IS no gate) rather than left uncoloured like Overview. Leaving
        // them blank read as an unexplained inconsistency (every OTHER step has a colour) rather than the
        // intended "this step doesn't block you" signal. Cheap (no filesystem query) - step 7 uses the
        // cached macro status, so this can be called freely (e.g. on every Setup edit).
        private void RefreshStepColors()
        {
            SetStepColor(hdrMachine, StepStatusOf(1));
            SetStepColor(hdrHome, StepStatusOf(2));
            SetStepColor(hdrAxis, StepStatusOf(3));
            SetStepColor(hdrHoming, StepStatusOf(4));
            SetStepColor(hdrProbes, StepStatusOf(5));
            SetStepColor(hdrFixtures, StepState.Complete);
            SetStepColor(hdrMacros, StepStatusOf(7));
            SetStepColor(hdrCalibration, StepState.Complete);
            SetStepColor(hdrSimulator, StepState.Complete);

            StepStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        // Colour only the tab's header text (not the tab body, which would make the descriptive text
        // unreadable - and would break dark mode).
        private static void SetStepColor(TextBlock hdr, StepState st)
        {
            if (hdr != null)
                hdr.Foreground = st == StepState.Complete ? StepGreen : (st == StepState.NeedsAttention ? StepOrange : StepRed);
        }

        private StepState StepStatusOf(int step)
        {
            // Runs at init too (before settings/AppConfig are loaded), so anything not ready yet falls through
            // to NotStarted (red) rather than throwing.
            try
            {
                switch (step)
                {
                    case 1: // Machine: a machine has been applied (LastMachine recorded)
                        return string.IsNullOrEmpty(AppConfig.Settings?.Base?.LastMachine) ? StepState.NotStarted : StepState.Complete;

                    case 2: // Home position: homing configured (the pending Setup model)
                        return Setup.HomingEnable ? StepState.Complete : StepState.NotStarted;

                    case 3: // Axis: steps/mm + travel on every axis (some-but-not-all = needs attention)
                    {
                        int set = 0, total = 0;
                        foreach (var a in Setup.Axes)
                        {
                            total++;
                            if (a.StepsPerMm > 0d && a.MaxTravel > 0d)
                                set++;
                        }
                        return set == 0 ? StepState.NotStarted : (set < total ? StepState.NeedsAttention : StepState.Complete);
                    }

                    case 4: // Homing & limits: some protection configured
                        return (Setup.SoftLimitsEnable || Setup.HasLimitSwitches || Setup.HomingEnable) ? StepState.Complete : StepState.NotStarted;

                    case 5: // Probes: at least one defined
                        return (ProbeDefinitions.Items?.Count ?? 0) > 0 ? StepState.Complete : StepState.NotStarted;

                    case 7: // Controller macros: all installed = green, some outdated = orange, any missing = red
                        if (_macroStatus != null && _macroStatus.Count > 0)
                        {
                            if (_macroStatus.All(r => r.State == AtcMacros.MacroState.Installed))
                                return StepState.Complete;
                            if (_macroStatus.Any(r => r.State == AtcMacros.MacroState.Missing))
                                return StepState.NotStarted;
                            return StepState.NeedsAttention;
                        }
                        return GrblInfo.AtcMacrosRequired ? StepState.NotStarted : StepState.Complete;

                    default:
                        return StepState.NotStarted;
                }
            }
            catch
            {
                return StepState.NotStarted;
            }
        }

        #endregion

        public void Activate(bool activate)
        {
            if (model == null)
                model = DataContext as GrblViewModel;

            if (activate)
            {
                // Belt-and-suspenders re-bind (Fixtures.SetItems now mutates its collection in place rather
                // than replacing it - see Fixture.cs - so this is no longer load-bearing, but costs nothing
                // and guards against the same "bound before the data existed" class of bug for any future
                // library that doesn't get the same treatment).
                grdProbes.ItemsSource = ProbeDefinitions.Items;
                grdFixtures.ItemsSource = Fixtures.Items;

                BuildAxes();
                LoadCurrentSettings();
                LoadWorkSurface();   // board extent - config, not controller settings (see WorkSurface.cs)

                // Machine choice is required input - restore the last machine the user picked (persisted across
                // runs), else default to a generic 3-axis CNC. Restoring only re-selects the dropdowns; it does
                // NOT re-seed catalog values - the fields keep the controller's actual settings (the machine's
                // real NVRAM), which LoadCurrentSettings just read. Leave an existing pick alone on re-entry.
                if (cbxManufacturer.SelectedItem == null)
                    RestoreOrDefaultMachine();

                // Same reason as Reload's: everything above drove the fields from the controller / the saved
                // machine pick, none of it is the operator editing. Clear it here so the page starts each
                // activation genuinely "unedited" and the refresh below stays armed.
                _userEdited = false;

                if (!_subscribed && model != null)
                {
                    model.PropertyChanged += Model_PropertyChanged;
                    _subscribed = true;
                }
                if (!_settingsHookAttached)
                {
                    GrblSettings.SettingsReloaded += OnSettingsReloaded;
                    _settingsHookAttached = true;
                }
                UpdateLimitState();
                UpdateApplyState();
                UpdateCalibrationStepAvailability();
                // Deferred (2026-07-19 - "Machine Setup tab permanently unresponsive" investigation): this
                // Activate(true) runs from MainWindow.TabMode_SelectionChanged, DURING the tab-switch's own
                // layout pass (the newly-selected tab's content is being measured/arranged right now).
                // AtcMacros.GetStatus pumps the dispatcher waiting for the controller's filesystem listing -
                // Steps_SelectionChanged (below) already defers the SAME call for exactly this reason ("throws
                // if run during the layout pass that generated this nested TabControl's items") - this call
                // site was calling it synchronously/undeferred instead, the one place that comment's warning
                // wasn't followed. A pumped-dispatcher wait colliding with an active layout pass here would
                // throw mid-tab-switch, after UIViewModel.CurrentView was already reassigned but before the
                // switch finished - explaining why the tab looked selected-but-frozen and never recovered on
                // its own (a mid-layout-pass exception doesn't reliably self-heal the visual tree).
                Dispatcher.BeginInvoke((System.Action)RefreshMacroStatus, System.Windows.Threading.DispatcherPriority.Background);

                // Ask the controller what it actually holds, rather than trusting the cached copy. Everything
                // else here only learns about a change ioSender itself saw go out; a setting altered by a
                // pendant, another sender, a controller-side macro, or any session not running this build
                // leaves the cache confidently wrong with nothing on the wire to say so. Opening this page is
                // the right moment to ask - it is the page whose numbers get written back to the machine, and
                // a $$ is one cheap round trip.
                //
                // Real case: $130/$131 diverged from 860/846 to 889/889 during a window with no ioSender
                // session at all, and this page went on offering 889 as a pending change afterwards.
                //
                // DEFERRED at Background priority for exactly the reason RefreshMacroStatus above is: this
                // runs during the tab-switch's own layout pass, and Load() pumps the dispatcher waiting for
                // the controller's reply - doing that mid-layout throws and leaves the tab frozen.
                Dispatcher.BeginInvoke((System.Action)ReloadSettingsFromController, System.Windows.Threading.DispatcherPriority.Background);

                UpdateSimulatorStepVisibility();
                RefreshFirmwareVersion();
            }
            else
            {
                if (_subscribed && model != null)
                {
                    model.PropertyChanged -= Model_PropertyChanged;
                    _subscribed = false;
                }
                if (_settingsHookAttached)
                {
                    // A static event holds a strong reference to this view - unsubscribing is what stops a
                    // menu-hosted instance being kept alive (and refreshed) after the operator has left it.
                    GrblSettings.SettingsReloaded -= OnSettingsReloaded;
                    _settingsHookAttached = false;
                }
                if (_fwInfoWindow != null)
                    _fwInfoWindow.Close();
            }
        }

        #region Capability detection / load

        private AxisSetup GetAxis(string letter)
        {
            return Setup.Axes.FirstOrDefault(a => a.Letter == letter);
        }

        private void BuildAxes()
        {
            Setup.Axes.Clear();
            foreach (var axis in model.Axes)
            {
                var a = new AxisSetup(axis.Letter, axis.Index);
                a.PropertyChanged += OnSetupChanged;   // per-axis edits also refresh the change set / Apply state
                Setup.Axes.Add(a);
            }
        }

        // Restore the machine persisted from a previous run, else fall back to the generic default.
        private void RestoreOrDefaultMachine()
        {
            string saved = AppConfig.Settings.Base != null ? AppConfig.Settings.Base.LastMachine : null;
            if (!string.IsNullOrEmpty(saved) && TrySelectMachine(saved))
                return;
            SelectDefaultMachine();
        }

        // Select Manufacturer/Product/Model by name ("mfr|product|model"); returns false if not found.
        private bool TrySelectMachine(string path)
        {
            var parts = path.Split('|');
            if (parts.Length != 3)
                return false;
            var mfr = Manufacturers.FirstOrDefault(m => m.Name == parts[0]);
            var prod = mfr != null ? mfr.Products.FirstOrDefault(p => p.Name == parts[1]) : null;
            var mdl = prod != null ? prod.Models.FirstOrDefault(m => m.Name == parts[2]) : null;
            if (mdl == null)
                return false;

            _restoringSelection = true;
            // Populate each child's ItemsSource explicitly (don't rely on the SelectionChanged cascade, whose
            // ItemsSource/SelectedItem timing can drop the selection) then select. We do NOT seed catalog values
            // here - restoring keeps the controller's actual settings (Model_Changed skips ApplyPreset while
            // _restoringSelection is set); the catalog only seeds on a fresh user pick.
            cbxManufacturer.SelectedItem = mfr;
            cbxProduct.ItemsSource = mfr.Products;
            cbxProduct.SelectedItem = prod;
            cbxModel.ItemsSource = prod.Models;
            cbxModel.SelectedItem = mdl;
            PresetNote = mdl.Note ?? string.Empty;   // show the machine's note without overwriting field values
            _restoringSelection = false;
            return true;
        }

        // Default the cascading selectors to "Generic / custom" -> "3-axis CNC" -> "With limit switches".
        // Setting each level in order drives the SelectionChanged handlers that populate the next.
        private void SelectDefaultMachine()
        {
            if (cbxManufacturer.Items.Count == 0)
                return;
            _restoringSelection = true;
            cbxManufacturer.SelectedIndex = 0;
            if (cbxProduct.Items.Count > 0)
                cbxProduct.SelectedIndex = 0;
            if (cbxModel.Items.Count > 0)
                cbxModel.SelectedIndex = 0;
            _restoringSelection = false;
        }

        // Persist the user's machine pick so it is restored next run (only for real user selections).
        // The three dropdowns as the "Manufacturer|Product|Model" identity stored in LastMachine, or null when
        // the picture is incomplete.
        private string SelectedMachineId()
        {
            var mfr = cbxManufacturer.SelectedItem as MachineManufacturer;
            var prod = cbxProduct.SelectedItem as MachineProduct;
            var mdl = cbxModel.SelectedItem as MachineModel;
            return (mfr == null || prod == null || mdl == null) ? null : mfr.Name + "|" + prod.Name + "|" + mdl.Name;
        }

        /// <summary>
        /// A machine is picked whose identity is not what LastMachine already holds - i.e. Apply still has
        /// something to commit even if no $ setting would change.
        ///
        /// This distinction is the whole of a first-run deadlock found 2026-08-03. Apply used to be gated
        /// purely on pending SETTINGS, and Apply_Click returned early on zero changes before it reached
        /// SaveSelectedMachine - which is the only place LastMachine is ever written. On a machine whose
        /// controller is already configured correctly, picking it produces no changes at all, so Apply stayed
        /// greyed, the identity was never recorded, and the startup setup gate (which asks for exactly that
        /// identity) re-armed on every launch with no way out.
        /// </summary>
        private bool MachineIdentityUnsaved()
        {
            string id = SelectedMachineId();
            return id != null && id != (AppConfig.Settings.Base?.LastMachine ?? string.Empty);
        }

        private void SaveSelectedMachine()
        {
            string id = SelectedMachineId();
            if (id == null || AppConfig.Settings.Base == null)
                return;
            AppConfig.Settings.Base.LastMachine = id;
            AppConfig.Settings.Save();
        }

        private void LoadCurrentSettings()
        {
            _loading = true;
            try { LoadCurrentSettingsCore(); }
            finally { _loading = false; _userEdited = false; }   // fields now mirror the controller again
        }

        // Re-read the controller's settings into the page when they change underneath it - the wizard used to
        // read them once per activation and then show that snapshot for as long as it stayed open, so a $130
        // written from the MDI (or by any other view) left a table claiming the old envelope. Cosmetic it is
        // not: Apply diffs the on-screen values against the LIVE settings, so the stale number comes back as a
        // pending change and gets written to the machine.
        //
        // Refuses to run over the operator's own edits - their typing is not something an event from the
        // controller may discard. In that case the page keeps what they typed and the pending-change list
        // (which compares against live values) still shows them the truth before anything is written.
        // Re-read $$ on page open (see the call site in Activate for why). Refuses in the three cases where
        // asking would cost more than the staleness it prevents:
        //
        //   - not connected: nothing to ask, and Load() would just fail.
        //   - a job is running: a $$ is ~100 lines of reply competing with the stream for the link.
        //   - unsaved setting edits exist anywhere (the grbl settings page shares this collection): a reload
        //     overwrites values from the controller, which would silently discard someone's typing on
        //     ANOTHER page. GrblConfigControl.ReloadSettings pairs Load() with ClearPendingEdits precisely
        //     because that is a deliberate, operator-initiated discard - this one is not.
        //
        // Load() raises SettingsReloaded on success, so the page refresh happens through the same path as
        // every other change; there is nothing to repopulate here.
        private void ReloadSettingsFromController()
        {
            if (!_settingsHookAttached)
                return;                                   // left the page during the deferral
            if (Comms.com == null || !Comms.com.IsOpen)
                return;
            if (model != null && model.IsJobRunning)
                return;
            if (GrblSettings.HasChanges())
                return;

            try { GrblSettings.Load(); }
            catch (Exception ex) { CNC.Core.DebugLog.Write("config", "Machine Setup: $$ refresh failed - " + ex.Message); }
        }

        private void OnSettingsReloaded(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((System.Action)(() => OnSettingsReloaded(sender, e)));
                return;
            }

            // _settingsHookAttached doubles as "the view is active" - it is subscribed on activate and
            // dropped on deactivate - and still matters after the CheckAccess hop, which can land after the
            // operator has left the page.
            if (!_settingsHookAttached || _userEdited)
                return;

            BuildAxes();
            LoadCurrentSettings();
            BuildReview();
            UpdateApplyState();
            RefreshStepColors();
        }

        private void LoadCurrentSettingsCore()
        {
            // The travel field shows physical travel, which is the stored soft-limit travel plus the pull-off
            // clearance reserved at each end (see BuildTargets). $22 is a bit-field on grblHAL (bit0 enable,
            // bit3 force-set-origin); test bits, don't compare to 1.
            int homingFlags = GrblSettings.GetInteger(GrblSetting.HomingEnable);
            if (homingFlags < 0) homingFlags = 0;
            Setup.HomingEnable = (homingFlags & 0x01) != 0;
            if (GrblInfo.IsGrblHAL)
                Setup.ForceSetOrigin = (homingFlags & 0x08) != 0;
            Setup.SoftLimitsEnable = GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) == 1;
            // Hard limits default ON for machines with switches (the wizard no longer asks - users tweak it in
            // Basic settings). Detect "has switches" from the controller's current homing/hard-limit state.
            Setup.HasLimitSwitches = GrblSettings.GetInteger(GrblSetting.HardLimitsEnable) == 1 || GrblInfo.HomingEnabled;
            Setup.HardLimitsEnable = true;

            double pulloff = GrblSettings.GetDouble(GrblSetting.HomingPulloff);
            if (pulloff > 0d) Setup.HomingPulloff = pulloff;
            double feed = GrblSettings.GetDouble(GrblSetting.HomingFeedRate);
            if (feed > 0d) Setup.HomingFeed = feed;
            double seek = GrblSettings.GetDouble(GrblSetting.HomingSeekRate);
            if (seek > 0d) Setup.HomingSeek = seek;
            int debounce = GrblSettings.GetInteger(GrblSetting.HomingDebounceDelay);
            if (debounce > 0) Setup.HomingDebounce = debounce;

            int dirMask = GrblSettings.GetInteger(GrblSetting.DirInvertMask);
            int limitMask = GrblSettings.GetInteger(GrblSetting.LimitPinsInvertMask);
            int homeMask = GrblSettings.GetInteger(GrblSetting.HomingDirMask);

            foreach (var axis in Setup.Axes)
            {
                double stored = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axis.Index);

                // Say so when the controller's travel does not come back. The page shows 0 in that case,
                // which looks exactly like a machine configured with no travel - and the operator cannot
                // tell the two apart from the table. Logged with the axis index and the setting id actually
                // asked for, because a wrong index would produce the same empty answer as a missing value.
                if (!(stored > 0d))
                    CNC.Core.DebugLog.Write("setup", string.Format(
                        "axis {0} (index {1}): ${2} read back as '{3}' - travel shown as 0 and NOT written by Apply",
                        axis.Letter, axis.Index, (int)(GrblSetting.MaxTravelBase + axis.Index), stored));

                axis.MaxTravel = stored > 0d ? stored : 0d;   // table value IS $13x (max travel); no pull-off fudge
                double rate = GrblSettings.GetDouble(GrblSetting.MaxFeedRateBase + axis.Index);
                axis.MaxRate = rate > 0d ? rate : axis.DefaultMaxRate;   // keep an existing rate, else a stepper-friendly default
                double steps = GrblSettings.GetDouble(GrblSetting.TravelResolutionBase + axis.Index);
                axis.StepsPerMm = steps > 0d ? steps : 0d;   // 0 = unknown; a preset (or the calibration tab) can fill it
                axis.InvertDirection = (dirMask & axis.Bit) != 0;
                axis.LimitNormallyClosed = (limitMask & axis.Bit) != 0;
                axis.HomeAtMin = (homeMask & axis.Bit) != 0;
            }

            // Z homes at the top of the gantry (max end -> travels negative -> $23 bit clear).
            var z = GetAxis("Z");
            if (z != null)
                z.HomeAtMin = false;

            UpdateHomeCornerText();
        }

        // Cascading Manufacturer -> Product -> Model selector. Picking a model seeds the fields.
        private void Manufacturer_Changed(object sender, SelectionChangedEventArgs e)
        {
            var m = cbxManufacturer.SelectedItem as MachineManufacturer;
            cbxProduct.ItemsSource = m?.Products;
            cbxProduct.SelectedItem = null;
            cbxModel.ItemsSource = null;
            cbxModel.SelectedItem = null;
            PresetNote = string.Empty;
        }

        private void Product_Changed(object sender, SelectionChangedEventArgs e)
        {
            var p = cbxProduct.SelectedItem as MachineProduct;
            cbxModel.ItemsSource = p?.Models;
            cbxModel.SelectedItem = null;
        }

        private void Model_Changed(object sender, SelectionChangedEventArgs e)
        {
            // A real user pick seeds catalog starting values; a restore only re-selects the machine and keeps
            // the controller's actual settings (its real NVRAM) loaded by LoadCurrentSettings. The pick is
            // remembered (LastMachine) only once the user commits it with Apply - see Apply_Click.
            if (_restoringSelection)
                return;
            ApplyPreset(cbxModel.SelectedItem as MachineModel);

            // Refresh Apply directly rather than relying on ApplyPreset's field edits to raise
            // PropertyChanged: when the preset matches the controller exactly, NOTHING changes and no
            // notification fires - which is precisely the case where picking the machine is the only thing
            // Apply has left to commit. Without this the button stays greyed on the very machine that needs it.
            UpdateApplyState();
        }

        // Seed the wizard fields from a catalog model (X/Y/Z only). Everything stays editable and the user
        // still confirms each value. The travel field is PHYSICAL travel, so add back the 2x pull-off the stored
        // $130-$132 reserves; the home corner is only a suggestion (most hobby machines home front-left).
        private void ApplyPreset(MachineModel p)
        {
            if (p == null)
                return;
            if (!p.Grbl)   // catalogued for reference but not a grbl controller - nothing to seed
            {
                PresetNote = p.Note ?? "Not a grbl controller - this wizard configures grbl settings only.";
                return;
            }
            PresetNote = p.Note ?? string.Empty;

            foreach (var axis in Setup.Axes)
            {
                if (axis.Index > 2)
                    continue;   // catalog covers X/Y/Z
                int i = axis.Index;
                if (p.StepsPerMm != null && i < p.StepsPerMm.Length && p.StepsPerMm[i] > 0d) axis.StepsPerMm = p.StepsPerMm[i];
                if (p.MaxRate != null && i < p.MaxRate.Length) axis.MaxRate = p.MaxRate[i];
                if (p.Travel != null && i < p.Travel.Length) axis.MaxTravel = p.Travel[i];
            }

            if (p.Homing.HasValue)
                Setup.HomingEnable = p.Homing.Value;

            // Catalog home corner ($23) and force-set-origin ($22 bit3) - most of these machines home to a
            // fixed corner the user won't change, so seed both. Force-set-origin makes the chosen corner the
            // machine zero (needed for it to take effect on grblHAL); Carbide-style machines leave it off.
            if (p.ForceSetOrigin.HasValue)
                Setup.ForceSetOrigin = p.ForceSetOrigin.Value;

            if (p.HomingDirMask >= 0)
            {
                foreach (var axis in Setup.Axes)
                    if (axis.Index <= 2)
                        axis.HomeAtMin = (p.HomingDirMask & axis.Bit) != 0;
                var z = GetAxis("Z");
                if (z != null)
                    z.HomeAtMin = false;   // Z homes at top
                UpdateHomeCornerText();
            }
        }

        #endregion

        #region Home corner picker

        // ---- work surface (spoilboard extent) ----
        //
        // Deliberately NOT expressed by shrinking $130/$131: the machine really can reach past the board to
        // the toolsetter, and must keep being allowed to or tc.macro can never drive there. See WorkSurface.cs.

        private bool loadingWorkSurface = false;

        private void LoadWorkSurface()
        {
            var ws = WorkSurface.Current;
            loadingWorkSurface = true;
            chkWorkSurfaceDefined.IsChecked = ws.Defined;
            txtWsMinX.Text = ws.MinX.ToString("0.###", CultureInfo.InvariantCulture);
            txtWsMaxX.Text = ws.MaxX.ToString("0.###", CultureInfo.InvariantCulture);
            txtWsMinY.Text = ws.MinY.ToString("0.###", CultureInfo.InvariantCulture);
            txtWsMaxY.Text = ws.MaxY.ToString("0.###", CultureInfo.InvariantCulture);
            loadingWorkSurface = false;
            ShowWorkSurfaceSummary();
        }

        private static double ParseOr(string text, double fallback)
        {
            double v;
            return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign,
                                   CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private void WorkSurface_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingWorkSurface)
                return;

            var ws = WorkSurface.Current;
            ws.Defined = chkWorkSurfaceDefined.IsChecked == true;
            ws.MinX = ParseOr(txtWsMinX.Text, ws.MinX);
            ws.MaxX = ParseOr(txtWsMaxX.Text, ws.MaxX);
            ws.MinY = ParseOr(txtWsMinY.Text, ws.MinY);
            ws.MaxY = ParseOr(txtWsMaxY.Text, ws.MaxY);
            AppConfig.Settings.Save();
            ShowWorkSurfaceSummary();
        }

        /// <summary>
        /// State what the numbers actually mean once clamped, rather than echoing them back. A board typed
        /// larger than the machine is silently held inside the travel limits (WorkSurface.UsableMin/Max), and
        /// an operator who cannot see that would be left believing an extent that will not be used.
        /// </summary>
        private void ShowWorkSurfaceSummary()
        {
            if (txtWorkSurfaceSummary == null)
                return;

            var ws = WorkSurface.Current;
            string text = ws.Summary;

            if (ws.Defined && (ws.UsableSpan(0) <= 0d || ws.UsableSpan(1) <= 0d))
                text = "These numbers do not describe a usable area - check that 'from' is less than 'to' on both axes.";

            txtWorkSurfaceSummary.Text = text;
        }

        /// <summary>Fill the near corner from the spindle's current machine position - jog there, then click.</summary>
        private void WorkSurfaceHere_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;

            txtWsMinX.Text = model.MachinePosition.X.ToString("0.###", CultureInfo.InvariantCulture);
            txtWsMinY.Text = model.MachinePosition.Y.ToString("0.###", CultureInfo.InvariantCulture);
            WorkSurface_Changed(sender, e);
        }

        private void Corner_Click(object sender, RoutedEventArgs e)
        {
            string corner = (string)((FrameworkElement)sender).Tag;   // FL / FR / BL / BR (front/back, left/right)

            var x = GetAxis("X");
            var y = GetAxis("Y");
            if (x != null) x.HomeAtMin = corner == "FL" || corner == "BL";   // left  = X min
            if (y != null) y.HomeAtMin = corner == "FL" || corner == "FR";   // front = Y min

            // Picking a home corner only takes visible effect with force-set-origin on (grblHAL): it puts
            // machine zero AT the chosen corner so the homing-direction ($23) choice and the 3D view match.
            // With it off, grblHAL keeps zero at the max corner regardless of $23 and the choice does nothing.
            if (GrblInfo.IsGrblHAL)
                Setup.ForceSetOrigin = true;

            HighlightCorner(corner);
            UpdateHomeCornerText();
        }

        private void HighlightCorner(string corner)
        {
            var dots = new Dictionary<string, System.Windows.Shapes.Ellipse>
            {
                { "FL", cornerFL }, { "FR", cornerFR }, { "BL", cornerBL }, { "BR", cornerBR }
            };
            foreach (var kv in dots)
                kv.Value.Fill = kv.Key == corner ? Brushes.LimeGreen : Brushes.White;
        }

        private void UpdateHomeCornerText()
        {
            var x = GetAxis("X");
            var y = GetAxis("Y");
            if (x == null || y == null)
            {
                HomeCornerText = string.Empty;
                return;
            }

            HomeCornerText = string.Format("Home: {0}, {1}, Z top.   X homes at {2}, Y homes at {3}.",
                y.HomeAtMin ? "front" : "back", x.HomeAtMin ? "left" : "right",
                x.HomeAtMin ? "min" : "max", y.HomeAtMin ? "min" : "max");

            HighlightCorner((y.HomeAtMin ? "F" : "B") + (x.HomeAtMin ? "L" : "R"));
        }

        #endregion

        #region Live limit indicators

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GrblViewModel.Signals))
            {
                UpdateLimitState();
                // Cheap self-heal for the firmware version label: it's only set from Activate(true), so a
                // reconnect to a different target (e.g. simulator -> real board) while this tab stays open
                // would otherwise leave it showing the previous connection's build stamp. Signals updates on
                // every status poll, so this notices within one poll after any reconnect - but only actually
                // refreshes (which also clears any in-progress/just-finished update check) when the reported
                // firmware identity has actually changed, so it doesn't clobber the check UI on every tick.
                string key = GrblInfo.Version + "|" + GrblInfo.DriverSha;
                if (key != _lastFirmwareKey)
                    RefreshFirmwareVersion();
            }
        }

        private void UpdateLimitState()
        {
            if (model == null)
                return;

            Signals s = model.Signals.Value;
            foreach (var axis in Setup.Axes)
            {
                Signals flag = (Signals)(1 << axis.Index);   // LimitX..LimitW share the low bits of Signals
                axis.LimitActive = (s & flag) != 0;
            }
        }

        #endregion

        #region Review + apply

        private int ApplyAxisBits(int current, Func<AxisSetup, bool> bitSet)
        {
            foreach (var axis in Setup.Axes)
            {
                if (bitSet(axis))
                    current |= axis.Bit;
                else
                    current &= ~axis.Bit;
            }
            return current;
        }

        // Collect (setting -> target value) for everything the wizard manages.
        private Dictionary<GrblSetting, string> BuildTargets()
        {
            var targets = new Dictionary<GrblSetting, string>();

            // Max travel ($13x) is written exactly as entered - no pull-off fudge. The old +/-2*pulloff
            // round-trip compounded across re-applies and silently shrank $13x (e.g. 120 -> 110 -> 100).
            foreach (var axis in Setup.Axes)
            {
                // Only write travel when it is KNOWN, exactly as steps/mm below already did. 0 is not a
                // travel, it is the absence of one: LoadCurrentSettingsCore stores 0 whenever $13x reads
                // back non-positive, so an unloaded/unavailable setting was indistinguishable from a real
                // value here and Apply would write $130=0 $131=0 $132=0 - a machine with soft limits enabled
                // and a zero envelope, where every move is out of bounds.
                //
                // Found 2026-08-19 with the Axis table displaying zeros while the controller held 860/840/135;
                // the guard was already three lines below for steps/mm and had simply never been extended up.
                if (axis.MaxTravel > 0d)
                    targets[GrblSetting.MaxTravelBase + axis.Index] = axis.MaxTravel.ToInvariantString();
                if (axis.MaxRate > 0d)      // same reasoning - a zero max rate is not a rate either
                    targets[GrblSetting.MaxFeedRateBase + axis.Index] = axis.MaxRate.ToInvariantString();
                if (axis.StepsPerMm > 0d)   // only write steps/mm when known (current value or a preset) - never clobber with 0
                    targets[GrblSetting.TravelResolutionBase + axis.Index] = axis.StepsPerMm.ToInvariantString();
            }

            targets[GrblSetting.DirInvertMask] =
                ApplyAxisBits(GrblSettings.GetInteger(GrblSetting.DirInvertMask), a => a.InvertDirection).ToString();
            targets[GrblSetting.HomingDirMask] =
                ApplyAxisBits(GrblSettings.GetInteger(GrblSetting.HomingDirMask), a => a.HomeAtMin).ToString();

            if (Setup.HasLimitSwitches)
            {
                targets[GrblSetting.LimitPinsInvertMask] =
                    ApplyAxisBits(GrblSettings.GetInteger(GrblSetting.LimitPinsInvertMask), a => a.LimitNormallyClosed).ToString();
                targets[GrblSetting.HardLimitsEnable] = Setup.HardLimitsEnable ? "1" : "0";
            }

            // Write the homing settings when the user wants homing (Setup.HomingEnable) OR it is already on -
            // NOT only when the controller currently has it enabled. On a fresh machine (the wizard's main use
            // case) homing starts off, so gating on the live state would refuse to ever turn it on.
            if (Setup.HomingEnable || GrblInfo.HomingEnabled)
            {
                // $22 is a bit-field on grblHAL (bit0 enable, bit3 force-set-origin, plus single-axis/init-lock/
                // ... bits) - read-modify-write only the two bits we own so the rest survive. Classic grbl: 0/1.
                if (GrblInfo.IsGrblHAL)
                {
                    int flags = GrblSettings.GetInteger(GrblSetting.HomingEnable);
                    if (flags < 0) flags = 0;
                    flags = (flags & ~0x09) | (Setup.HomingEnable ? 0x01 : 0) | (Setup.ForceSetOrigin ? 0x08 : 0);
                    targets[GrblSetting.HomingEnable] = flags.ToString();
                }
                else
                    targets[GrblSetting.HomingEnable] = Setup.HomingEnable ? "1" : "0";
                targets[GrblSetting.HomingPulloff] = Setup.HomingPulloff.ToInvariantString();
                targets[GrblSetting.HomingFeedRate] = Setup.HomingFeed.ToInvariantString();
                targets[GrblSetting.HomingSeekRate] = Setup.HomingSeek.ToInvariantString();
                targets[GrblSetting.HomingDebounceDelay] = Setup.HomingDebounce.ToString();
            }

            // grblHAL rejects soft limits ($20=1) unless homing is enabled (error:10), so only request them when
            // homing will be on. The two-pass write in Apply_Click also guarantees $22 is sent before $20.
            bool willHome = Setup.HomingEnable || GrblInfo.HomingEnabled;
            targets[GrblSetting.SoftLimitsEnable] = (Setup.SoftLimitsEnable && willHome) ? "1" : "0";

            return targets;
        }

        // Recompute the pending-change list (the diff between target values and what the controller holds now).
        private void BuildReview()
        {
            Changes.Clear();

            foreach (var kv in BuildTargets())
            {
                var detail = GrblSettings.Get(kv.Key);
                if (detail == null)
                    continue;   // setting not present on this firmware - skip silently

                if (TargetDiffers(detail, kv.Value))
                    Changes.Add(new SettingChange
                    {
                        Setting = "$" + (int)kv.Key,
                        Name = detail.Name ?? string.Empty,
                        OldValue = detail.Value,
                        NewValue = kv.Value
                    });
            }
        }

        // True when the target value would actually change the setting. INTEGER/FLOAT are compared numerically
        // so formatting-only differences (e.g. "1" vs "1.000") are not reported as changes. Internal: also used
        // by GrblConfigControl's Restore-from-file preview (same "what would actually change" comparison).
        internal static bool TargetDiffers(GrblSettingDetails detail, string target)
        {
            if (detail.DataType == GrblSettingDetails.DataTypes.FLOAT || detail.DataType == GrblSettingDetails.DataTypes.INTEGER)
            {
                if (double.TryParse(detail.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cur) &&
                    double.TryParse(target, NumberStyles.Any, CultureInfo.InvariantCulture, out double tgt))
                    return cur != tgt;
            }
            return detail.Value != target;
        }

        // Any setting changed (machine pick, field edit, reload) - refresh the pending-change set and enable
        // Apply only when there is something to write.
        private void OnSetupChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_loading)
                _userEdited = true;   // a real edit, not us filling the fields from the controller

            if (e.PropertyName == nameof(AxisSetup.HomeAtMin))
                UpdateHomeCornerText();   // keep the home-corner picture in sync when a checkbox is toggled
            UpdateApplyState();
            RefreshStepColors();
        }

        private void UpdateApplyState()
        {
            if (model == null || !GrblSettings.IsLoaded)
            {
                btnApply.IsEnabled = false;
                return;
            }
            BuildReview();
            // Preview is about pending WRITES, so it stays tied to the change set. Apply also commits the
            // machine identity, so it must stay live when that is all there is to commit - see
            // MachineIdentityUnsaved for the deadlock that came of conflating the two.
            btnPreview.IsEnabled = Changes.Count > 0;
            btnApply.IsEnabled = Changes.Count > 0 || MachineIdentityUnsaved();
        }

        // Preview the pending changes in a dialog (replaces the old inline expander).
        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            BuildReview();
            if (Changes.Count == 0)
            {
                txtStatus.Text = "No pending changes.";
                return;
            }
            txtStatus.Text = string.Format("{0} pending change(s).", Changes.Count);
            new PendingChangesDialog(Changes) { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        // Reload: discard any edits, re-read the controller's settings and return to the generic default.
        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            BuildAxes();
            LoadCurrentSettings();
            cbxManufacturer.SelectedIndex = -1;   // force the change events so the machine is re-applied
            RestoreOrDefaultMachine();
            Changes.Clear();
            txtStatus.Text = "Reloaded from controller.";
            // LAST, after the machine re-apply: re-selecting the dropdowns drives the fields and so trips the
            // edit flag. Leaving it set would quietly disable the settings-reloaded refresh for the rest of
            // the session - the page would go stale again and nothing would say so.
            _userEdited = false;
            UpdateApplyState();
        }

        // Forget the remembered machine so the first-run setup wizard reappears on the next launch. Does NOT
        // change any controller settings - it only clears the app's record of which machine was chosen.
        private void Forget_Click(object sender, RoutedEventArgs e)
        {
            if (AppConfig.Settings.Base != null)
            {
                AppConfig.Settings.Base.LastMachine = string.Empty;
                AppConfig.Settings.Save();
            }
            cbxManufacturer.SelectedIndex = -1;
            SelectDefaultMachine();
            txtStatus.Text = "Machine forgotten - the setup wizard will reappear next launch.";
        }

        // Probes are machine hardware, so the probe library is edited from here. Used by Load Stock and probing.
        // ---- Step 5: probe definitions (hosted inline) ----

        private void Probes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool sel = grdProbes.SelectedItem is ProbeDefinition;
            btnProbeEdit.IsEnabled = btnProbeDelete.IsEnabled = sel;
        }

        private void Probes_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (grdProbes.SelectedItem is ProbeDefinition)
                EditSelectedProbe();
        }

        private void ProbeAdd_Click(object sender, RoutedEventArgs e)
        {
            var def = new ProbeDefinition();
            var dlg = new ProbeDefinitionEditDialog(def) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                ProbeDefinitions.Items.Add(def);
                ProbeDefinitions.Renumber(ProbeDefinitions.Items);   // names derive from type + count
                ProbeDefinitions.Save();
                grdProbes.SelectedItem = def;
                RefreshStepColors();
                UpdateCalibrationStepAvailability();
            }
        }

        private void ProbeEdit_Click(object sender, RoutedEventArgs e)
        {
            EditSelectedProbe();
        }

        // Edit a clone and copy back on OK so Cancel reverts.
        private void EditSelectedProbe()
        {
            var sel = grdProbes.SelectedItem as ProbeDefinition;
            if (sel == null)
                return;

            var edit = sel.Clone();
            var dlg = new ProbeDefinitionEditDialog(edit) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                sel.CopyFrom(edit);
                ProbeDefinitions.Renumber(ProbeDefinitions.Items);   // type may have changed
                ProbeDefinitions.Save();
                grdProbes.Items.Refresh();
                UpdateCalibrationStepAvailability();
            }
        }

        private void ProbeDelete_Click(object sender, RoutedEventArgs e)
        {
            var sel = grdProbes.SelectedItem as ProbeDefinition;
            if (sel != null && AppDialogs.Show(string.Format("Delete probe \"{0}\"?", sel.Name), "Probe definitions",
                                               MessageBoxButton.YesNo, MessageBoxImage.Question, id: "probe.delete") == MessageBoxResult.Yes)
            {
                ProbeDefinitions.Items.Remove(sel);
                ProbeDefinitions.Renumber(ProbeDefinitions.Items);
                ProbeDefinitions.Save();
                RefreshStepColors();
                UpdateCalibrationStepAvailability();
            }
        }

        // The user's own named fixtures (Kind is a fixed, code-defined choice - see FixtureKind - not itself
        // user-addable). Not gated (optional) - no RefreshStepColors calls.
        // ---- Step 6: fixture definitions (hosted inline) ----

        private void Fixtures_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool sel = grdFixtures.SelectedItem is Fixture;
            btnFixtureEdit.IsEnabled = btnFixtureDelete.IsEnabled = sel;
        }

        private void Fixtures_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (grdFixtures.SelectedItem is Fixture)
                EditSelectedFixture();
        }

        private void FixtureAdd_Click(object sender, RoutedEventArgs e)
        {
            var def = new Fixture();
            var dlg = new FixtureEditDialog(def, model) { Owner = Window.GetWindow(this) };
            // Non-modal (Show, not ShowDialog) - Set/Test position needs the main window's jog pad and
            // keyboard jogging reachable while this is open, which a modal dialog blocks entirely. The
            // ShowDialog()-return-value idiom becomes a Closed handler instead.
            dlg.Closed += (s, ev) =>
            {
                if (dlg.Saved)
                {
                    Fixtures.Items.Add(def);
                    Fixtures.Save();
                    grdFixtures.SelectedItem = def;
                }
            };
            dlg.Show();
        }

        private void FixtureEdit_Click(object sender, RoutedEventArgs e)
        {
            EditSelectedFixture();
        }

        // Edit a clone and copy back on OK so Cancel reverts.
        private void EditSelectedFixture()
        {
            var sel = grdFixtures.SelectedItem as Fixture;
            if (sel == null)
                return;

            var edit = sel.Clone();
            var dlg = new FixtureEditDialog(edit, model) { Owner = Window.GetWindow(this) };
            // Non-modal - see FixtureAdd_Click's own comment.
            dlg.Closed += (s, ev) =>
            {
                if (dlg.Saved)
                {
                    sel.CopyFrom(edit);
                    Fixtures.Save();
                    grdFixtures.Items.Refresh();
                }
            };
            dlg.Show();
        }

        private void FixtureDelete_Click(object sender, RoutedEventArgs e)
        {
            var sel = grdFixtures.SelectedItem as Fixture;
            if (sel != null && AppDialogs.Show(string.Format("Delete fixture \"{0}\"?", sel.Name), "Fixture definitions",
                                               MessageBoxButton.YesNo, MessageBoxImage.Question, id: "fixture.delete") == MessageBoxResult.Yes)
            {
                Fixtures.Items.Remove(sel);
                Fixtures.Save();
            }
        }

        // ---- Step 7: controller macros status ----

        private void Steps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Refresh macro status when the macros step is shown (it queries the controller filesystem).
            // Deferred: GetStatus pumps the dispatcher, which throws if run during the layout pass that
            // generated this nested TabControl's items.
            if (e.OriginalSource == tabSteps && tabSteps.SelectedItem == tabStepMacros)
                Dispatcher.BeginInvoke((System.Action)RefreshMacroStatus, System.Windows.Threading.DispatcherPriority.Background);

            if (e.OriginalSource == tabSteps && tabSteps.SelectedItem == tabStepSimulator)
                Dispatcher.BeginInvoke((System.Action)RefreshSimulatorStep, System.Windows.Threading.DispatcherPriority.Background);

            // Calibration step: activate/deactivate whichever of its two sub-tabs (stepper cal / squareness)
            // is currently selected, mirroring how ToolsView used to Activate() its own sub-tabs. Deferred for
            // the same reason as the macro/simulator refreshes above - these wizards pump the dispatcher.
            if (e.OriginalSource == tabSteps)
            {
                if (e.RemovedItems.Count == 1 && e.RemovedItems[0] == tabStepCalibration)
                {
                    calibrationStepActive = false;
                    ActivateSelectedCalibrationChild(false);
                }
                if (tabSteps.SelectedItem == tabStepCalibration)
                {
                    calibrationStepActive = true;
                    UpdateCalibrationStepAvailability();
                    Dispatcher.BeginInvoke((System.Action)(() => ActivateSelectedCalibrationChild(true)), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        // True only while the Calibration step itself is the selected outer step - guards Calibration_
        // SelectionChanged from activating a sub-tab on startup/layout before the user has ever navigated here.
        private bool calibrationStepActive = false;

        private void ActivateSelectedCalibrationChild(bool activate)
        {
            var tab = tabCalibration?.SelectedItem as TabItem;
            if (tab == tabCalStepper)
                calStepperWizard.Activate(activate);
            else if (tab == tabCalSquareness)
                calSquarenessWizard.Activate(activate);
        }

        // Switching between Stepper calibration / Squareness within the Calibration step - deactivate the
        // outgoing sub-tab, activate the incoming one. Ignored while the Calibration step itself isn't the
        // active outer step (this event also bubbles up to Steps_SelectionChanged, which filters it out there
        // via e.OriginalSource, same pattern as the macros/simulator checks above).
        private void Calibration_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != tabCalibration || !calibrationStepActive)
                return;

            if (e.RemovedItems.Count == 1)
            {
                var removed = e.RemovedItems[0] as TabItem;
                if (removed == tabCalStepper)
                    calStepperWizard.Activate(false);
                else if (removed == tabCalSquareness)
                    calSquarenessWizard.Activate(false);
            }
            Dispatcher.BeginInvoke((System.Action)(() => ActivateSelectedCalibrationChild(true)), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Stepper calibration (probe) needs a real 3D probe to do anything useful - grey its sub-tab out
        // (not just its own Generate/Save buttons) when none is configured. Re-checked whenever the
        // Calibration step is shown and whenever a probe is added/edited/deleted, so it reflects changes
        // made on the Probe definitions step in the same session (mirrors ToolsView's old
        // UpdateStepperCalProbeAvailability).
        private void UpdateCalibrationStepAvailability()
        {
            if (tabCalStepper != null)
                tabCalStepper.IsEnabled = ProbeDefinitions.Items.Any(p => p.ProbeType == ProbeType.ThreeDProbe);
        }

        private void RefreshMacroStatus()
        {
            if (grdMacros == null)
                return;

            var rows = AtcMacros.GetStatus(model);
            _macroStatus = rows;
            grdMacros.ItemsSource = rows;
            btnInstallMacros.Visibility = rows.Any(r => r.State != AtcMacros.MacroState.Installed)
                ? Visibility.Visible : Visibility.Collapsed;
            RefreshStepColors();
        }

        private void RefreshMacros_Click(object sender, RoutedEventArgs e)
        {
            RefreshMacroStatus();
        }

        // Install/update the controller-side macros - delegates to the SD Card view's proven path, then refresh.
        private void InstallMacros_Click(object sender, RoutedEventArgs e)
        {
            // SDCardView.Instance is set in that view's CONSTRUCTOR. It used to be constructed at app startup
            // with every other tab, so it was always there; since the SD Card view moved off the tab bar into
            // a menu-hosted window (2026-08-03) it is not constructed until the operator actually opens that
            // window - so this refused with "The SD Card view is not available." purely because they had never
            // visited it. Same class as the getTab(ViewType.X)-returns-null trap the menu-hosting change
            // introduced elsewhere.
            // Constructing one here is enough and is safe: the ctor only does InitializeComponent, sets
            // ctxMenu.DataContext and assigns Instance - no comms, no event wiring. Provisioning explicitly
            // does not need the view REALIZED either; ProvisionAtcMacros says so itself and deliberately reads
            // Grbl.GrblViewModel rather than the view's own (still-null) DataContext.
            var sdCard = SDCardView.Instance ?? new SDCardView();
            sdCard.InstallAtcMacros(Window.GetWindow(this));
            RefreshMacroStatus();
        }

        // Picks up (PRINT, TLOREF_Z=..) below - same (PRINT, TAG=value) idiom StartJobView.rxResult already
        // uses for LS_X/LS_Y.
        private static readonly System.Text.RegularExpressions.Regex rxTloRefZ =
            new System.Text.RegularExpressions.Regex(@"TLOREF_Z\s*=\s*(-?\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private void UpdateTloRefValueDisplay()
        {
            double v = AppConfig.Settings.Base.TloRefBaseline;
            txtTloRefValue.Text = v == 0d ? "Never referenced" : string.Format("Baseline: {0:0.0##} mm", v);
        }

        // Machine-wide TLO baseline (see the XAML comment on this section) - probes the puck exactly like
        // tc.macro's own non-T8 (rigid tool, toolsetter input) branch, or its T8 (self-triggering 3D probe,
        // main input) branch when the checkbox says a 3D probe is what's actually mounted right now - then
        // stores the RAW machine-Z touch point as the baseline every job will load into #<_tlo_ref> at its own
        // start. Uses the "Tool setter" probe definition's own feeds, not tc.macro's hardcoded F500/F25,
        // matching every other probe move already threaded through a ProbeDefinition in this app.
        private void ReferenceTlo_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;

            var p = ProbeDefinitions.Items.FirstOrDefault(x => x.ProbeType == ProbeType.ToolSetter);
            if (p == null)
            {
                AppDialogs.Show(Window.GetWindow(this), "Define a Tool setter probe first (above).", "Reference TLO", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool probeInSpindle = chkTloRef3dProbe.IsChecked == true;
            string searchF = p.ProbeFeedRate.ToInvariantString("0.0##"), latchF = p.LatchFeedRate.ToInvariantString("0.0##");

            var b = new StringBuilder();
            b.AppendLine("(Machine Setup - reference TLO at the puck)");
            b.AppendLine("(PREREQ, connected, homed, noalarm, ATC=1, G30, G59.3)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            b.AppendLine("G53 G0 Z-5");
            b.AppendLine("G59.3");
            b.AppendLine("G0 X0 Y0");
            b.AppendLine("G0 Z0");
            // Main probe input (Q0) if a self-triggering 3D probe is actually in the spindle, else the
            // toolsetter input (Q1) for a rigid/cutting tool relying on continuity through the puck - same
            // convention tc.macro's own T8-vs-not branch uses.
            b.AppendLine(string.Format(GrblCommand.ProbeSelect, probeInSpindle ? 0 : 1));
            b.AppendLine("G91");
            b.AppendLine(string.Format("G38.2 Z-90 F{0}", searchF));
            b.AppendLine("G0 Z2");
            b.AppendLine(string.Format("G38.2 Z-5 F{0}", latchF));
            b.AppendLine("#<_probe_z> = #5063");
            b.AppendLine("G0 Z10");
            b.AppendLine("G90");
            b.AppendLine(string.Format(GrblCommand.ProbeSelect, 0));
            b.AppendLine("(PRINT, TLOREF_Z=#<_probe_z>)");
            b.AppendLine("G53 G0 Z-5");
            b.AppendLine("G53 G0 X#5181 Y#5182");
            b.AppendLine("G53 G0 Z#5183");

            double? captured = null;
            PropertyChangedEventHandler zHandler = (s, pe) =>
            {
                if (pe.PropertyName != nameof(GrblViewModel.Message) || string.IsNullOrEmpty(model.Message))
                    return;
                var m = rxTloRefZ.Match(model.Message);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    captured = v;
            };
            model.PropertyChanged += zHandler;

            bool started = false;
            PropertyChangedEventHandler doneHandler = null;
            doneHandler = (s, pe) =>
            {
                if (pe.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                if (!started)
                {
                    started = true;
                    return;
                }
                var st = model.StreamingState;
                if (st == StreamingState.Idle || st == StreamingState.NoFile || st == StreamingState.Stop)
                {
                    model.PropertyChanged -= doneHandler;
                    model.PropertyChanged -= zHandler;
                    bool alarmed = model.GrblState.State == GrblStates.Alarm;
                    if (!alarmed && captured.HasValue)
                    {
                        AppConfig.Settings.Base.TloRefBaseline = captured.Value;
                        AppConfig.Settings.Save();
                        UpdateTloRefValueDisplay();
                        model.Message = "TLO baseline referenced.";
                    }
                    else
                        model.Message = "Reference TLO failed or alarmed - baseline not changed.";
                }
            };
            model.PropertyChanged += doneHandler;

            if (!MacroProcessor.Run(model, "Reference TLO", b.ToString(), true))
            {
                model.PropertyChanged -= doneHandler;
                model.PropertyChanged -= zHandler;
            }
        }

        // ---- Step 8: build a simulator matching this machine ----

        // What Settings > Simulator would auto-detect from this controller right now (SimulatorConfigView.
        // SyncFromHardware derives the same fields the same way) - shared by the status readout below and
        // the Build click itself, so they can never disagree about what "matching this machine" means.
        private static SimulatorManager.ManualSimOptions CurrentMachineOptions()
        {
            return new SimulatorManager.ManualSimOptions
            {
                Axes = GrblInfo.NumAxes,
                Probe = GrblInfo.HasProbe,
                Toolsetter = GrblInfo.HasToolSetter,
                Rotation = GrblInfo.RotationSupported,
                LatheUvw = GrblInfo.LatheUVWModeEnabled,
                SafetyDoor = (GrblInfo.OptionalSignals & Signals.SafetyDoor) != 0,
                EStop = (GrblInfo.OptionalSignals & Signals.EStop) != 0
            };
        }

        // Same readout as Settings > Simulator's RefreshStatus: build id + whether it still matches this
        // machine's current options, or a plain "not built yet" - not just an enabled/disabled button.
        // Step 8 makes no sense while the active connection IS the simulator (there's no "connected
        // machine" left to build a matching one from) - hidden rather than merely disabled, and bumped off
        // if it happened to be the selected step when the sim connection was made.
        private void UpdateSimulatorStepVisibility()
        {
            if (tabStepSimulator == null)
                return;

            bool hide = SimulatorManager.IsSimulatorConnection();
            tabStepSimulator.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            if (hide && tabSteps.SelectedItem == tabStepSimulator)
                tabSteps.SelectedItem = tabStepOverview;
        }

        private void RefreshSimulatorStep()
        {
            if (btnBuildSimWizard == null)
                return;

            bool connected = SimulatorManager.IsRealControllerConnected();
            btnBuildSimWizard.IsEnabled = connected;

            if (!connected)
            {
                txtSimWizardStatus.Text = "Connect to the real machine first - this step builds from its detected options.";
                return;
            }

            if (!SimulatorManager.AppDataSimulatorPresent())
            {
                txtSimWizardStatus.Text = "No simulator built yet.";
                return;
            }

            string sig;
            SimulatorManager.BuildManualOptionSymbols(CurrentMachineOptions(), out sig);
            string activeSig = SimulatorManager.AppDataActiveSignature();
            txtSimWizardStatus.Text = sig == activeSig
                ? "Ready for Connect (build " + sig + ")."
                : "Installed (build " + activeSig + "), but this machine's options have changed since - rebuild recommended.";
        }

        // One button: derive options from the connected controller exactly like SimulatorConfigView's
        // SeedDefaults does (no picks to make here - the machine already specifies everything), ensure a
        // matching %AppData%\Simulator build exists, then copy this machine's live settings into it. Same
        // background-thread + Dispatcher.BeginInvoke pattern as SimulatorConfigView.btnBuild_Click - these
        // calls are blocking network/process I/O and must not run on the UI thread.
        private void BuildSimWizard_Click(object sender, RoutedEventArgs e)
        {
            if (!SimulatorManager.IsRealControllerConnected())
                return;

            btnBuildSimWizard.IsEnabled = false;
            var opts = CurrentMachineOptions();
            txtSimWizardStatus.Text = "Checking for a matching build...";

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sig, detail;
                    var r = SimulatorManager.EnsureAppDataSimulator(opts, out sig, out detail);
                    bool installed;
                    string exeStatus;
                    switch (r)
                    {
                        case SimulatorManager.MatchResult.AlreadyCurrent:
                            installed = true; exeStatus = "Already up to date (build " + sig + ")."; break;
                        case SimulatorManager.MatchResult.InstalledFromRelease:
                            installed = true; exeStatus = "Installed (build " + sig + ")."; break;
                        case SimulatorManager.MatchResult.BuildTriggered:
                            SetSimWizardStatus("Building (build " + sig + ") - this can take a few minutes...");
                            installed = SimulatorManager.PollAndInstallAppData(opts, sig);
                            exeStatus = installed
                                ? "Build ready and installed (build " + sig + ")."
                                : "Still building (build " + sig + ") - try again shortly.";
                            break;
                        default:
                            FinishSimWizard(detail ?? "Build failed.");
                            return;
                    }

                    if (!installed)
                    {
                        FinishSimWizard(exeStatus);
                        return;
                    }

                    SetSimWizardStatus(exeStatus + " Copying this machine's settings...");
                    var cmds = GrblSettings.Settings.Select(s => "$" + s.Id + "=" + s.Value).ToList();
                    string eepromErr = "no settings to copy.";
                    bool eepromOk = cmds.Count > 0 && SimulatorManager.BuildAppDataEeprom(cmds, out eepromErr);
                    FinishSimWizard(exeStatus + (eepromOk
                        ? " Machine settings copied to EEPROM.DAT."
                        : " Settings copy failed" + (string.IsNullOrEmpty(eepromErr) ? "." : (": " + eepromErr))));
                }
                catch (Exception ex) { FinishSimWizard(ex.Message); }
            });
        }

        private void SetSimWizardStatus(string text)
        {
            try { Dispatcher.BeginInvoke((System.Action)(() => txtSimWizardStatus.Text = text)); }
            catch { }
        }

        private void FinishSimWizard(string text)
        {
            try
            {
                Dispatcher.BeginInvoke((System.Action)(() =>
                {
                    txtSimWizardStatus.Text = text;
                    btnBuildSimWizard.IsEnabled = SimulatorManager.IsRealControllerConnected();
                }));
            }
            catch { }
        }

        // Populate the Apply tooltip on hover with the exact pending changes (old -> new), recomputed live
        // against the current selection and the controller's current values.
        private void btnApply_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            BuildReview();
            btnApply.ToolTip = Changes.Count == 0
                ? "No changes - the target settings already match the controller."
                : string.Format("Writes {0} setting{1} to the controller:\n{2}",
                    Changes.Count, Changes.Count == 1 ? "" : "s",
                    string.Join("\n", Changes.Select(c => string.Format("  {0} {1}: {2} → {3}", c.Setting, c.Name, c.OldValue, c.NewValue))));
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            BuildReview();

            if (Changes.Count == 0)
            {
                // Nothing to WRITE - but the machine identity itself may still be uncommitted, and recording
                // it is what satisfies the first-run setup gate. A controller that already holds exactly the
                // right settings is the normal case for anyone setting ioSender up against a machine that was
                // already working, so refusing outright here stranded them (see MachineIdentityUnsaved).
                if (MachineIdentityUnsaved())
                {
                    SaveSelectedMachine();
                    txtStatus.Text = "Machine recorded - the controller already had these settings.";
                    if (model != null)
                        model.Message = "Machine setup: machine recorded (settings already matched).";
                    UpdateApplyState();
                    // Explicitly, for the same reason Apply needed enabling by hand on this path: no setting
                    // changed, so no PropertyChanged fires, so nothing else would repaint step 1's indicator -
                    // it stayed red after being satisfied. RefreshStepColors raises StepStatusChanged, which
                    // is what the navigation tree's dots listen to.
                    RefreshStepColors();
                    SetupApplied?.Invoke();
                    return;
                }

                txtStatus.Text = "Nothing changed.";
                if (model != null)
                    model.Message = "Machine setup: nothing changed.";
                return;
            }

            var targets = BuildTargets();

            // grblHAL rejects $20=1 (soft limits) unless homing ($22) is already enabled, and GrblSettings.Save()
            // writes dirty settings in ascending id order ($20 before $22). So apply everything EXCEPT soft limits
            // first (which enables homing), then soft limits in a second pass once homing is on.
            string softLimits = null;
            foreach (var kv in targets)
            {
                if (kv.Key == GrblSetting.SoftLimitsEnable) { softLimits = kv.Value; continue; }
                var detail = GrblSettings.Get(kv.Key);
                if (detail != null)
                    detail.Value = kv.Value;
            }

            bool ok = GrblSettings.Save();

            if (softLimits != null)
            {
                var sd = GrblSettings.Get(GrblSetting.SoftLimitsEnable);
                if (sd != null)
                {
                    sd.Value = softLimits;
                    bool ok2 = GrblSettings.Save();   // separate pass: homing is enabled now, so $20=1 is accepted
                    ok = ok && ok2;
                }
            }

            // Collect any settings the controller rejected (Save records each error: reply on the setting), so we
            // can tell the user exactly which $ setting failed and why - not just "failed to write settings".
            var failures = new System.Collections.Generic.List<string>();
            foreach (var kv in targets)
            {
                var detail = GrblSettings.Get(kv.Key);
                if (detail == null || !detail.HasErrors)
                    continue;

                string reason = null;
                var errs = detail.GetErrors(string.Empty);
                if (errs != null)
                    foreach (var er in errs) { reason = er?.ToString(); break; }

                failures.Add("$" + (int)kv.Key + "=" + kv.Value
                    + (string.IsNullOrEmpty(detail.Name) ? string.Empty : "  (" + detail.Name + ")")
                    + (string.IsNullOrEmpty(reason) ? string.Empty : "  -> " + reason));
            }

            if (ok && failures.Count == 0)
            {
                int n = Changes.Count;
                txtStatus.Text = string.Format("Applied {0} setting(s).", n);
                if (model != null)
                    model.Message = string.Format("Machine setup: applied {0} setting(s).", n);
                BuildReview();   // should now be empty

                // The machine is now fully specified - remember it for next run and let the shell know setup is
                // done (first-run gating switches back to the normal UI on this event).
                SaveSelectedMachine();
                SetupApplied?.Invoke();
            }
            else
            {
                int failed = failures.Count;
                txtStatus.Text = failed > 0
                    ? string.Format("Failed to write {0} setting(s) - see details.", failed)
                    : "Failed to write settings.";
                if (model != null)
                    model.Message = "Machine setup: failed to write " + (failed > 0 ? failed + " setting(s)." : "settings.");

                string detail = failures.Count > 0
                    ? "The controller rejected these settings:\n\n" + string.Join("\n", failures)
                    : "The controller rejected the settings write (no specific error was reported).";
                AppDialogs.Show(detail, "Machine setup - settings rejected", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            UpdateApplyState();   // reflect the post-write state (cleared on success, still pending on failure)
        }

        #endregion

        #region Firmware update check + flash (grblHAL / SRW fork builds only)

        // Show what's connected and reset the update-check state - called on Activate and after a check.
        // Update checking only makes sense for a grblHAL board that emits the SRW fork's [BUILD:...] stamp
        // (GrblInfo.DriverSha) - stock grblHAL / classic grbl boards get an explanatory tooltip instead of
        // a permanently-visible text block, keeping the steady-state UI to just the two info lines + button.
        private void RefreshFirmwareVersion()
        {
            if (txtFwVersion == null)
                return;

            _lastFirmwareKey = GrblInfo.Version + "|" + GrblInfo.DriverSha;
            _pendingFwRelease = null;
            txtFwFlashStatus.Visibility = Visibility.Collapsed;
            txtFwFlashStatus.Text = string.Empty;

            if (string.IsNullOrEmpty(GrblInfo.Version))
            {
                txtFwVersion.Text = "Not connected.";
                btnCheckFwUpdate.IsEnabled = false;
                btnCheckFwUpdate.ToolTip = null;
                return;
            }

            txtFwVersion.Text = "Firmware: " + GrblInfo.Firmware + (GrblInfo.IsGrblHAL ? " (grblHAL)" : "")
                + (string.IsNullOrEmpty(GrblInfo.Version) ? "" : ", version " + GrblInfo.Version)
                + (GrblInfo.Build > 0 ? ", build " + GrblInfo.Build : "")
                + (string.IsNullOrEmpty(GrblInfo.DriverRef) ? "" : "\nBuild: " + GrblInfo.DriverRef);

            bool canCheck = GrblInfo.IsGrblHAL && !string.IsNullOrEmpty(GrblInfo.DriverSha);
            btnCheckFwUpdate.IsEnabled = canCheck;
            btnCheckFwUpdate.ToolTip = canCheck ? null : (GrblInfo.IsGrblHAL
                ? "This build doesn't report a driver build stamp - update checking isn't available."
                : "Update checking is only available for grblHAL.");
        }

        // Query the fw-latest release (background thread - network I/O) and compare its driver sha against
        // the connected board's own; report the result in a message box rather than inline text. An update
        // available folds the former separate confirm-before-flash step into the same box: Yes ("Flash
        // Firmware") goes straight into StartFlashFirmware, No/Cancel/closing leaves nothing pending.
        private void CheckFwUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!GrblInfo.IsGrblHAL || string.IsNullOrEmpty(GrblInfo.DriverSha))
                return;

            btnCheckFwUpdate.IsEnabled = false;
            string currentSha = GrblInfo.DriverSha;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string error;
                var release = FirmwareUpdateManager.GetLatestRelease(out error);

                Dispatcher.BeginInvoke((System.Action)(() =>
                {
                    btnCheckFwUpdate.IsEnabled = true;
                    var owner = Window.GetWindow(this);

                    if (release == null)
                    {
                        AppDialogs.Show(owner, "Could not check for updates: " + error, "Check for updates",
                            MessageBoxButton.OK, MessageBoxImage.Warning, id: "machinesetup.checkfwupdate.error");
                        return;
                    }

                    if (string.IsNullOrEmpty(release.DriverSha))
                    {
                        AppDialogs.Show(owner, "Latest release found, but its build stamp could not be read.",
                            "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning, id: "machinesetup.checkfwupdate.nostamp");
                        return;
                    }

                    if (string.Equals(release.DriverSha, currentSha, StringComparison.OrdinalIgnoreCase))
                    {
                        AppDialogs.Show(owner, "Up to date (build " + currentSha + ").", "Check for updates",
                            MessageBoxButton.OK, MessageBoxImage.Information, id: "machinesetup.checkfwupdate.uptodate");
                        return;
                    }

                    string header = string.Format("Update available: {0} (you have {1}).", release.DriverSha, currentSha);
                    string changelog = string.IsNullOrEmpty(release.Changelog) ? "" : "\n\n" + release.Changelog;

                    if (FirmwareUpdateManager.FindTeensyLoaderCli() == null)
                    {
                        AppDialogs.Show(owner, header + changelog + "\n\nteensy_loader_cli.exe not found - cannot flash automatically.",
                            "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning, id: "machinesetup.checkfwupdate.notoolcli");
                        return;
                    }

                    _pendingFwRelease = release;
                    string prompt = header + changelog + string.Format(
                        "\n\nFlashing disconnects ioSender. Windows can't auto-reboot the board into its " +
                        "programming mode, so once it says \"Waiting for Teensy device...\" you must press " +
                        "the RESET/PROGRAM button ON THE BOARD ITSELF within {0} seconds.", FlashWaitSeconds);
                    var choice = AppDialogs.Show(owner, prompt, "Firmware update available",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                        id: "machinesetup.flashfirmware", yesText: "Flash Firmware", noText: "Cancel");
                    if (choice == MessageBoxResult.Yes)
                        StartFlashFirmware(release);
                    else
                        _pendingFwRelease = null;
                }));
            });
        }

        // Wait for the bootloader after ioSender disconnects: teensy_loader_cli does NOT support an
        // automatic/soft reboot on Windows (confirmed via its own "Soft reboot is not implemented for
        // Win32" message), so the user must press the board's physical RESET/PROGRAM button - this needs
        // to be long enough to walk over and do that, not just cover upload time. Also shown in the
        // Flash Firmware confirmation prompt above.
        private const int FlashWaitSeconds = 180;

        // Download the release's hex and flash it via the bundled teensy_loader_cli, once the user has
        // already confirmed via the "Flash Firmware" choice in CheckFwUpdate_Click's dialog. Disconnects
        // ioSender's own connection first (frees the port, though flashing itself only needs the board's
        // HID bootloader interface) and does NOT reconnect afterward - the board reboots into the new
        // firmware, so the user reconnects once it's back.
        private void StartFlashFirmware(FirmwareUpdateManager.ReleaseInfo release)
        {
            string exe = FirmwareUpdateManager.FindTeensyLoaderCli();
            if (exe == null)
            {
                // Defensive only - CheckFwUpdate_Click already gates the Flash Firmware choice on this.
                AppDialogs.Show("teensy_loader_cli.exe was not found alongside ioSender - cannot flash automatically.",
                    "Update firmware", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnCheckFwUpdate.IsEnabled = false;
            SetFwFlashStatus("Downloading firmware...");

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string error;
                byte[] bytes = FirmwareUpdateManager.DownloadHex(release.HexAssetUrl, out error);
                if (bytes == null)
                {
                    SetFwFlashResult(false, "Download failed: " + error);
                    return;
                }

                string hexPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "iosender-fw-" + release.DriverSha + ".hex");
                try { System.IO.File.WriteAllBytes(hexPath, bytes); }
                catch (Exception ex) { SetFwFlashResult(false, "Could not save the download: " + ex.Message); return; }

                SetFwFlashStatus("Disconnecting...");
                try { Comms.com.Close(); } catch { }
                System.Threading.Thread.Sleep(500);   // let the port fully release before the loader opens it

                SetFwFlashStatus("Waiting for you to press RESET/PROGRAM on the board (build " + release.DriverSha + ")...");
                string log;
                bool ok = FirmwareUpdateManager.FlashHex(exe, hexPath, FlashWaitSeconds, out log, out error);
                SetFwFlashResult(ok, ok
                    ? "Flashed build " + release.DriverSha + ". Reconnect once the board finishes rebooting."
                    : "Flash failed: " + error + (string.IsNullOrEmpty(log) ? "" : "\n" + log));
            });
        }

        private void SetFwFlashStatus(string text)
        {
            try
            {
                Dispatcher.BeginInvoke((System.Action)(() =>
                {
                    txtFwFlashStatus.Visibility = Visibility.Visible;
                    txtFwFlashStatus.Text = text;
                }));
            }
            catch { }
        }

        private void SetFwFlashResult(bool ok, string text)
        {
            try
            {
                Dispatcher.BeginInvoke((System.Action)(() =>
                {
                    txtFwFlashStatus.Visibility = Visibility.Visible;
                    txtFwFlashStatus.Text = text;
                    btnCheckFwUpdate.IsEnabled = true;
                    if (ok)
                        _pendingFwRelease = null;
                }));
            }
            catch { }
        }

        #endregion

        #region Firmware info ($I)

        // Format the cached $I response (parsed into GrblInfo at connect) as a simple label: value list.
        private string BuildFirmwareInfo()
        {
            var sb = new System.Text.StringBuilder();
            string axes = string.Join("", Setup.Axes.Select(a => a.Letter));

            sb.AppendLine("Firmware:         " + GrblInfo.Firmware + (GrblInfo.IsGrblHAL ? " (grblHAL)" : ""));
            if (!string.IsNullOrEmpty(GrblInfo.Version)) sb.AppendLine("Version:          " + GrblInfo.Version);
            if (GrblInfo.Build > 0) sb.AppendLine("Build:            " + GrblInfo.Build);
            if (!string.IsNullOrEmpty(GrblInfo.Identity)) sb.AppendLine("Board / identity: " + GrblInfo.Identity);
            sb.AppendLine("Axes:             " + GrblInfo.NumAxes + (string.IsNullOrEmpty(axes) ? "" : " (" + axes + ")"));
            if (!string.IsNullOrEmpty(GrblInfo.Options)) sb.AppendLine("Options (OPT):    " + GrblInfo.Options);
            if (!string.IsNullOrEmpty(GrblInfo.NewOptions)) sb.AppendLine("Options (NEWOPT): " + GrblInfo.NewOptions);
            if (!string.IsNullOrEmpty(GrblInfo.TrinamicDrivers)) sb.AppendLine("Trinamic drivers: " + GrblInfo.TrinamicDrivers);
            sb.AppendLine("Serial RX buffer: " + GrblInfo.SerialBufferSize);
            sb.AppendLine("Planner buffer:   " + GrblInfo.PlanBufferSize);

            var caps = new List<string>();
            if (GrblInfo.HomingEnabled) caps.Add("homing");
            if (GrblInfo.ForceSetOrigin) caps.Add("force-set-origin");
            if (GrblInfo.HasSDCard) caps.Add("SD card");
            if (GrblInfo.HasProbe) caps.Add("probe");
            if (GrblInfo.HasATC) caps.Add("ATC");
            if (GrblInfo.HasFS) caps.Add("flash FS");
            if (GrblInfo.ExpressionsSupported) caps.Add("expressions");
            if (caps.Count > 0) sb.AppendLine("Capabilities:     " + string.Join(", ", caps));

            return sb.ToString().TrimEnd();
        }

        // Non-modal popup so the user can read $I while filling the form. Reuse the open window on repeat clicks.
        private void FirmwareInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_fwInfoWindow != null)
            {
                _fwInfoWindow.Activate();
                return;
            }

            var text = new TextBox
            {
                Text = BuildFirmwareInfo(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(10)
            };
            var close = new Button { Content = "Close", Width = 80, Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right };

            var panel = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            panel.Children.Add(close);
            panel.Children.Add(text);

            var win = new Window
            {
                Title = "Firmware information ($I)",
                Width = 540,
                Height = 360,
                Content = panel,
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            close.Click += (s, ev) => win.Close();
            win.Closed += (s, ev) => _fwInfoWindow = null;
            // Same non-modal + owned + ShowInTaskbar=false combination as FixtureEditDialog, so the same
            // owner-minimized-on-close symptom applies here - see UIUtils.ActivateOwnerOnClose.
            UIUtils.ActivateOwnerOnClose(win);

            _fwInfoWindow = win;
            win.Show();   // non-modal
        }

        #endregion
    }
}
