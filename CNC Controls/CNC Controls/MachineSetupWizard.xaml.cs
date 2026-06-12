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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

    // One pending change shown on the review page.
    public class SettingChange
    {
        public string Setting { get; set; }
        public string Name { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
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

            model = DataContext as GrblViewModel;
            DataContextChanged += (s, e) => { if (DataContext is GrblViewModel) model = (GrblViewModel)DataContext; };
        }

        public GrblConfigType GrblConfigType { get { return GrblConfigType.MachineSetup; } }

        #region Dependency properties bound from XAML

        // Raised when the user presses Apply with a machine specified (settings written + remembered). The
        // shell uses this to leave the first-run "set up your machine" gate and return to the normal UI.
        public static event System.Action SetupApplied;

        public MachineSetupModel Setup { get; } = new MachineSetupModel();
        public List<MachineManufacturer> Manufacturers { get; } = MachineCatalog.Manufacturers;

        public static readonly DependencyProperty PresetNoteProperty = DependencyProperty.Register(nameof(PresetNote), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata(string.Empty));
        public string PresetNote { get { return (string)GetValue(PresetNoteProperty); } set { SetValue(PresetNoteProperty, value); } }

        public static readonly DependencyProperty HomeCornerTextProperty = DependencyProperty.Register(nameof(HomeCornerText), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata("No corner selected."));
        public string HomeCornerText { get { return (string)GetValue(HomeCornerTextProperty); } set { SetValue(HomeCornerTextProperty, value); } }

        public ObservableCollection<SettingChange> Changes { get; } = new ObservableCollection<SettingChange>();

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
                Setup.Axes.Add(new AxisSetup(axis.Letter, axis.Index));
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
                axis.MaxTravel = stored > 0d ? stored + 2d * Setup.HomingPulloff : 0d;
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
                if (p.Travel != null && i < p.Travel.Length) axis.MaxTravel = p.Travel[i] + 2d * Setup.HomingPulloff;
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

            // Stored max travel is the physical travel less the pull-off clearance reserved at BOTH ends.
            foreach (var axis in Setup.Axes)
            {
                double stored = Math.Max(0d, axis.MaxTravel - 2d * Setup.HomingPulloff);
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

            if (GrblInfo.HomingEnabled)
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

            targets[GrblSetting.SoftLimitsEnable] = Setup.SoftLimitsEnable ? "1" : "0";

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

                if (detail.Value != kv.Value)
                    Changes.Add(new SettingChange
                    {
                        Setting = "$" + (int)kv.Key,
                        Name = detail.Name ?? string.Empty,
                        OldValue = detail.Value,
                        NewValue = kv.Value
                    });
            }
        }

        private void Review_Expanded(object sender, RoutedEventArgs e)
        {
            BuildReview();
            txtStatus.Text = string.Format("{0} pending change(s).", Changes.Count);
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
        }

        // Mirror the controller's current settings into the bundled simulator's "My Machine" EEPROM, so a later
        // simulator connection boots with the same context as this real machine. Runs off the UI thread (it
        // briefly drives a headless simulator instance).
        private async void CopyToSim_Click(object sender, RoutedEventArgs e)
        {
            var cmds = GrblSettings.Settings.Select(s => "$" + s.Id + "=" + s.Value).ToList();
            if (cmds.Count == 0)
            {
                txtStatus.Text = "No settings to copy - read settings from the controller first.";
                return;
            }

            btnCopyToSim.IsEnabled = false;
            txtStatus.Text = "Copying settings to the simulator...";
            string err = null;
            bool ok = await System.Threading.Tasks.Task.Run(() => SimulatorManager.BuildMyMachineEeprom(cmds, out err));
            btnCopyToSim.IsEnabled = true;
            txtStatus.Text = ok ? "Copied to simulator (My Machine)." : ("Copy to simulator failed - " + err);
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

            // Keep the preview in sync so the user sees exactly what is being written.
            expReview.IsExpanded = true;

            foreach (var kv in BuildTargets())
            {
                var detail = GrblSettings.Get(kv.Key);
                if (detail != null)
                    detail.Value = kv.Value;
            }

            if (GrblSettings.Save())
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
                txtStatus.Text = "Failed to write settings.";
                if (model != null)
                    model.Message = "Machine setup: failed to write settings.";
            }
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
