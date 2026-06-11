/*
 * MachineSetupWizard.xaml.cs - part of CNC Controls library
 *
 * Machine Setup Wizard: a guided first-run configuration of the machine-description grbl settings
 * (travel, home corner, limit sensors, homing, soft limits). Reads firmware capabilities
 * (NEWOPT / NumAxes / force-set-origin) to gate the questions, then writes the resulting $n
 * settings via GrblSettings.Save().
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
        private bool _homeAtMin, _limitNormallyClosed, _limitActive;

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
        private Grid[] steps;
        private int _currentStep = 0;
        private bool _subscribed = false;

        public MachineSetupWizard()
        {
            InitializeComponent();

            model = DataContext as GrblViewModel;
            DataContextChanged += (s, e) => { if (DataContext is GrblViewModel) model = (GrblViewModel)DataContext; };
        }

        public GrblConfigType GrblConfigType { get { return GrblConfigType.MachineSetup; } }

        #region Dependency properties bound from XAML

        public MachineSetupModel Setup { get; } = new MachineSetupModel();
        public System.Collections.Generic.List<MachineManufacturer> Manufacturers { get; } = MachineCatalog.Manufacturers;

        public static readonly DependencyProperty PresetNoteProperty = DependencyProperty.Register(nameof(PresetNote), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata(string.Empty));
        public string PresetNote { get { return (string)GetValue(PresetNoteProperty); } set { SetValue(PresetNoteProperty, value); } }

        public static readonly DependencyProperty StepTitleProperty = DependencyProperty.Register(nameof(StepTitle), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata(string.Empty));
        public string StepTitle { get { return (string)GetValue(StepTitleProperty); } set { SetValue(StepTitleProperty, value); } }

        public static readonly DependencyProperty StepNumberProperty = DependencyProperty.Register(nameof(StepNumber), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata(string.Empty));
        public string StepNumber { get { return (string)GetValue(StepNumberProperty); } set { SetValue(StepNumberProperty, value); } }

        public static readonly DependencyProperty CapabilitiesProperty = DependencyProperty.Register(nameof(Capabilities), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata(string.Empty));
        public string Capabilities { get { return (string)GetValue(CapabilitiesProperty); } set { SetValue(CapabilitiesProperty, value); } }

        public static readonly DependencyProperty HomeCornerTextProperty = DependencyProperty.Register(nameof(HomeCornerText), typeof(string), typeof(MachineSetupWizard), new PropertyMetadata("No corner selected."));
        public string HomeCornerText { get { return (string)GetValue(HomeCornerTextProperty); } set { SetValue(HomeCornerTextProperty, value); } }

        public static readonly DependencyProperty CanBackProperty = DependencyProperty.Register(nameof(CanBack), typeof(bool), typeof(MachineSetupWizard), new PropertyMetadata(false));
        public bool CanBack { get { return (bool)GetValue(CanBackProperty); } set { SetValue(CanBackProperty, value); } }

        public static readonly DependencyProperty CanNextProperty = DependencyProperty.Register(nameof(CanNext), typeof(bool), typeof(MachineSetupWizard), new PropertyMetadata(true));
        public bool CanNext { get { return (bool)GetValue(CanNextProperty); } set { SetValue(CanNextProperty, value); } }

        public static readonly DependencyProperty IsReviewProperty = DependencyProperty.Register(nameof(IsReview), typeof(bool), typeof(MachineSetupWizard), new PropertyMetadata(false));
        public bool IsReview { get { return (bool)GetValue(IsReviewProperty); } set { SetValue(IsReviewProperty, value); } }

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
                if (steps == null)
                    steps = new Grid[] { stepWelcome, stepTravel, stepMaxRate, stepHome, stepLimits, stepHoming, stepSoftLimits, stepReview };
                GoToStep(0);

                if (!_subscribed && model != null)
                {
                    model.PropertyChanged += Model_PropertyChanged;
                    _subscribed = true;
                }
                UpdateLimitState();
            }
            else if (_subscribed && model != null)
            {
                model.PropertyChanged -= Model_PropertyChanged;
                _subscribed = false;
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

            var caps = new List<string>();
            caps.Add(GrblInfo.IsGrblHAL ? "grblHAL" : "Grbl");
            caps.Add(string.Format("{0} axes ({1})", GrblInfo.NumAxes, string.Join("", Setup.Axes.Select(a => a.Letter))));
            caps.Add(GrblInfo.HomingEnabled ? "homing supported" : "homing NOT in firmware");
            if (GrblInfo.ForceSetOrigin)
                caps.Add("force-set-origin");
            Capabilities = string.Join("  -  ", caps);
        }

        private void LoadCurrentSettings()
        {
            // Load homing parameters first - the travel field shows physical travel, which is the stored
            // soft-limit travel plus the pull-off clearance reserved at each end (see BuildTargets).
            // $22 is a bit-field on grblHAL (bit0 enable, bit3 force-set-origin); test bits, don't compare to 1.
            int homingFlags = GrblSettings.GetInteger(GrblSetting.HomingEnable);
            if (homingFlags < 0) homingFlags = 0;
            Setup.HomingEnable = (homingFlags & 0x01) != 0;
            if (GrblInfo.IsGrblHAL)
                Setup.ForceSetOrigin = (homingFlags & 0x08) != 0;
            Setup.SoftLimitsEnable = GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) == 1;
            Setup.HardLimitsEnable = GrblSettings.GetInteger(GrblSetting.HardLimitsEnable) == 1;
            Setup.HasLimitSwitches = Setup.HardLimitsEnable || GrblInfo.HomingEnabled;

            double pulloff = GrblSettings.GetDouble(GrblSetting.HomingPulloff);
            if (pulloff > 0d) Setup.HomingPulloff = pulloff;
            double feed = GrblSettings.GetDouble(GrblSetting.HomingFeedRate);
            if (feed > 0d) Setup.HomingFeed = feed;
            double seek = GrblSettings.GetDouble(GrblSetting.HomingSeekRate);
            if (seek > 0d) Setup.HomingSeek = seek;
            int debounce = GrblSettings.GetInteger(GrblSetting.HomingDebounceDelay);
            if (debounce > 0) Setup.HomingDebounce = debounce;

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
            ApplyPreset(cbxModel.SelectedItem as MachineModel);
        }

        // Seed the wizard fields from a catalog model (X/Y/Z only). Everything stays editable and the user
        // still confirms each step. The travel field is PHYSICAL travel, so add back the 2x pull-off the stored
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

            string xs = x.HomeAtMin ? "left (X min)" : "right (X max)";
            string ys = y.HomeAtMin ? "front (Y min)" : "back (Y max)";
            HomeCornerText = string.Format("Home: {0}, {1}, Z top.   X homes at {2}, Y homes at {3}.",
                y.HomeAtMin ? "front" : "back", x.HomeAtMin ? "left" : "right",
                x.HomeAtMin ? "min" : "max", y.HomeAtMin ? "min" : "max");

            HighlightCorner((y.HomeAtMin ? "F" : "B") + (x.HomeAtMin ? "L" : "R"));
        }

        #endregion

        #region Step navigation

        private void GoToStep(int step)
        {
            _currentStep = Math.Max(0, Math.Min(steps.Length - 1, step));

            for (int i = 0; i < steps.Length; i++)
                steps[i].Visibility = i == _currentStep ? Visibility.Visible : Visibility.Collapsed;

            string[] titles = {
                "Welcome",
                "Travel limits",
                "Steps/mm && max rate",
                "Home corner",
                "Limit sensors",
                "Homing",
                "Soft limits",
                "Review && apply"
            };

            StepTitle = titles[_currentStep];
            StepNumber = string.Format("Step {0} of {1}", _currentStep + 1, steps.Length);
            CanBack = _currentStep > 0;
            IsReview = _currentStep == steps.Length - 1;
            CanNext = true;

            btnNext.Visibility = IsReview ? Visibility.Collapsed : Visibility.Visible;
            btnApply.Visibility = IsReview ? Visibility.Visible : Visibility.Collapsed;

            if (IsReview)
                BuildReview();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            GoToStep(_currentStep - 1);
        }

        // Start over: discard any answers, re-read the controller's current settings and return to step 1.
        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            BuildAxes();
            LoadCurrentSettings();
            GoToStep(0);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            GoToStep(_currentStep + 1);
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

            CanNext = false;   // review is the last step
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (Changes.Count == 0)
            {
                model.Message = "Machine setup: nothing changed.";
                return;
            }

            foreach (var kv in BuildTargets())
            {
                var detail = GrblSettings.Get(kv.Key);
                if (detail != null)
                    detail.Value = kv.Value;
            }

            if (GrblSettings.Save())
            {
                model.Message = string.Format("Machine setup: applied {0} setting(s).", Changes.Count);
                BuildReview();
            }
            else
                model.Message = "Machine setup: failed to write settings.";
        }

        #endregion
    }
}
