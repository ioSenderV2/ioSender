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

    public partial class MachineSetupWizard : UserControl, IGrblConfigTab
    {
        private GrblViewModel model = null;
        private bool _subscribed = false;
        private bool _restoringSelection = false;   // suppress persisting while we drive the dropdowns in code
        private Window _fwInfoWindow = null;

        public MachineSetupWizard()
        {
            InitializeComponent();

            // Make each step tab bindable to a key (badge + right-click menu). AttachTabBinding re-parents the
            // existing header - for the six steps that is the named, colour-coded TextBlock (hdrMachine ...),
            // which keeps working because RefreshStepColors holds it by field reference, not via TabItem.Header.
            TabKeyBinder.AttachTabBinding(tabStepOverview, "Tab.MachineSetup.Overview");
            TabKeyBinder.AttachTabBinding(tabStepMachine, "Tab.MachineSetup.Machine");
            TabKeyBinder.AttachTabBinding(tabStepHome, "Tab.MachineSetup.Home");
            TabKeyBinder.AttachTabBinding(tabStepAxis, "Tab.MachineSetup.Axis");
            TabKeyBinder.AttachTabBinding(tabStepHoming, "Tab.MachineSetup.Homing");
            TabKeyBinder.AttachTabBinding(tabStepProbes, "Tab.MachineSetup.Probes");
            TabKeyBinder.AttachTabBinding(tabStepFixtures, "Tab.MachineSetup.Fixtures");
            TabKeyBinder.AttachTabBinding(tabStepMacros, "Tab.MachineSetup.Macros");
            TabKeyBinder.AttachTabBinding(tabStepSimulator, "Tab.MachineSetup.Simulator");

            model = DataContext as GrblViewModel;
            DataContextChanged += (s, e) => { if (DataContext is GrblViewModel) model = (GrblViewModel)DataContext; };

            // Recompute the pending-change set (and Apply's enabled state) whenever the model-level settings change.
            Setup.PropertyChanged += OnSetupChanged;

            // Step 5 hosts the probe library inline (live ObservableCollection - add/edit/delete in place).
            grdProbes.ItemsSource = ProbeDefinitions.Items;

            // Step 6 hosts the fixture library inline, same pattern as Probes. Starts empty (no prepopulation).
            grdFixtures.ItemsSource = Fixtures.Items;

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
        public static int FirstIncompleteStep()
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
                if (macros.Any(r => r.State != AtcMacros.MacroState.Installed))
                    return 7;
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

        // Last macro-status query (cached so tab colouring doesn't re-hit the controller filesystem).
        private System.Collections.Generic.List<AtcMacros.MacroStatusRow> _macroStatus;

        // Colour the six graded step tabs from their current state (step 6/Fixtures is optional, not graded,
        // so its header stays uncoloured like Overview). Cheap (no filesystem query) - step 7 uses the
        // cached macro status, so it can be called freely (e.g. on every Setup edit).
        private void RefreshStepColors()
        {
            SetStepColor(hdrMachine, StepStatusOf(1));
            SetStepColor(hdrHome, StepStatusOf(2));
            SetStepColor(hdrAxis, StepStatusOf(3));
            SetStepColor(hdrHoming, StepStatusOf(4));
            SetStepColor(hdrProbes, StepStatusOf(5));
            SetStepColor(hdrMacros, StepStatusOf(7));
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
                BuildAxes();
                LoadCurrentSettings();

                // Machine choice is required input - restore the last machine the user picked (persisted across
                // runs), else default to a generic 3-axis CNC. Restoring only re-selects the dropdowns; it does
                // NOT re-seed catalog values - the fields keep the controller's actual settings (the machine's
                // real NVRAM), which LoadCurrentSettings just read. Leave an existing pick alone on re-entry.
                if (cbxManufacturer.SelectedItem == null)
                    RestoreOrDefaultMachine();

                if (!_subscribed && model != null)
                {
                    model.PropertyChanged += Model_PropertyChanged;
                    _subscribed = true;
                }
                UpdateLimitState();
                UpdateApplyState();
                RefreshMacroStatus();   // queries the filesystem once so step 7's colour is right on open
            }
            else
            {
                if (_subscribed && model != null)
                {
                    model.PropertyChanged -= Model_PropertyChanged;
                    _subscribed = false;
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
        private void SaveSelectedMachine()
        {
            var mfr = cbxManufacturer.SelectedItem as MachineManufacturer;
            var prod = cbxProduct.SelectedItem as MachineProduct;
            var mdl = cbxModel.SelectedItem as MachineModel;
            if (mfr == null || prod == null || mdl == null || AppConfig.Settings.Base == null)
                return;
            AppConfig.Settings.Base.LastMachine = mfr.Name + "|" + prod.Name + "|" + mdl.Name;
            AppConfig.Settings.Save();
        }

        private void LoadCurrentSettings()
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
                UpdateLimitState();
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
                double stored = Math.Max(0d, axis.MaxTravel);
                targets[GrblSetting.MaxTravelBase + axis.Index] = stored.ToInvariantString();
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
            btnApply.IsEnabled = btnPreview.IsEnabled = Changes.Count > 0;
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
            }
        }

        // The user's own named fixtures (Kind is a fixed, code-defined choice - see FixtureKind - not itself
        // user-addable). Not gated (optional) - no RefreshStepColors calls.
        // ---- Step 6: fixture definitions (hosted inline) ----

        private void Fixtures_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool sel = grdFixtures.SelectedItem is Fixture;
            btnFixtureEdit.IsEnabled = btnFixtureDelete.IsEnabled = btnFixtureSetPosition.IsEnabled = sel;
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
            if (dlg.ShowDialog() == true)
            {
                Fixtures.Items.Add(def);
                Fixtures.Save();
                grdFixtures.SelectedItem = def;
            }
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
            if (dlg.ShowDialog() == true)
            {
                sel.CopyFrom(edit);
                Fixtures.Save();
                grdFixtures.Items.Refresh();
            }
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

        // Captures the CURRENT machine position as the selected fixture's reference start point (the point
        // the corner probe reads from - NOT a firmware G28 write, kept only in this fixture's own Coords).
        // Also available inside the Add/Edit dialog itself, and from Start Job.
        private void FixtureSetPosition_Click(object sender, RoutedEventArgs e)
        {
            var sel = grdFixtures.SelectedItem as Fixture;
            if (sel == null)
                return;

            string coords = Fixtures.CurrentCoordsCsv(model);
            if (coords == null)
            {
                if (model != null)
                    model.Message = "Machine position unknown - home first to save a fixture position.";
                return;
            }

            sel.Coords = coords;
            Fixtures.Save();
            grdFixtures.Items.Refresh();
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
            if (SDCardView.Instance != null)
            {
                SDCardView.Instance.InstallAtcMacros(Window.GetWindow(this));
                RefreshMacroStatus();
            }
            else
                AppDialogs.Show(Window.GetWindow(this), "The SD Card view is not available.", "Controller macros", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---- Step 8: build a simulator matching this machine ----

        private void RefreshSimulatorStep()
        {
            if (btnBuildSimWizard == null)
                return;

            bool connected = SimulatorManager.IsRealControllerConnected();
            btnBuildSimWizard.IsEnabled = connected;
            txtSimWizardStatus.Text = connected
                ? string.Empty
                : "Connect to the real machine first - this step builds from its detected options.";
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
            var opts = new SimulatorManager.ManualSimOptions
            {
                Axes = GrblInfo.NumAxes,
                Probe = GrblInfo.HasProbe,
                Rotation = GrblInfo.RotationSupported,
                LatheUvw = GrblInfo.LatheUVWModeEnabled,
                SafetyDoor = (GrblInfo.OptionalSignals & Signals.SafetyDoor) != 0,
                EStop = (GrblInfo.OptionalSignals & Signals.EStop) != 0
            };
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

            _fwInfoWindow = win;
            win.Show();   // non-modal
        }

        #endregion
    }
}
