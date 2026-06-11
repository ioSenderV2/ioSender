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

    // A starting-point preset for a known machine. Arrays are X,Y,Z; null = "leave the current value".
    // These SEED the wizard fields - the user still confirms every step. Sourced starting points, not
    // guarantees: steps/mm can vary with microstepping/belt upgrades, and the home corner is build-specific.
    public class MachinePreset
    {
        public string Name { get; set; }
        public double[] StepsPerMm { get; set; }   // $100-$102
        public double[] MaxRate { get; set; }      // $110-$112 (mm/min)
        public double[] Travel { get; set; }       // $130-$132 (stored, mm)
        public int HomingDirMask { get; set; } = -1;   // $23 suggestion; -1 = leave
        public bool? Homing { get; set; }              // $22 enable suggestion; null = leave
        public string Note { get; set; }
        public override string ToString() { return Name; }   // ComboBox display

        // Sourced shortlist (see research). First entry is the no-op "Custom" default.
        public static System.Collections.Generic.List<MachinePreset> List
        {
            get
            {
                return new System.Collections.Generic.List<MachinePreset>
                {
                    new MachinePreset { Name = "Custom / not listed (enter manually)" },
                    new MachinePreset { Name = "Sienci LongMill MK2 — 30×30",
                        StepsPerMm = new[]{ 200d, 200d, 200d }, MaxRate = new[]{ 4000d, 4000d, 3000d },
                        Travel = new[]{ 810d, 855d, 120d }, HomingDirMask = 3, Homing = false,
                        Note = "Sienci-published (original LongBoard; MK2.5/SuperLongBoard differ)." },
                    new MachinePreset { Name = "Carbide3D Shapeoko 3 — XXL",
                        StepsPerMm = new[]{ 40d, 40d, 320d }, Travel = new[]{ 838d, 838d, 95d },
                        Note = "Belt X/Y (~40 steps/mm); confirm Z steps/mm and exact travel for your unit." },
                    new MachinePreset { Name = "Inventables X-Carve — 1000mm (pre-2021)",
                        StepsPerMm = new[]{ 40d, 40d, 188.95d }, MaxRate = new[]{ 8000d, 8000d, 500d },
                        Travel = new[]{ 750d, 750d, 100d },
                        Note = "2021 upgrade kit (9mm belts / extended Z) changes $100/$101/$102/$132." },
                    new MachinePreset { Name = "BobsCNC Evolution 4",
                        StepsPerMm = new[]{ 80d, 80d, 400d }, Travel = new[]{ 610d, 610d, 85d }, Homing = false,
                        Note = "Ships without limit switches - no homing." },
                    new MachinePreset { Name = "Generic CNC 3018 / Genmitsu 3018-PROVer",
                        StepsPerMm = new[]{ 800d, 800d, 800d }, MaxRate = new[]{ 1000d, 1000d, 800d },
                        Travel = new[]{ 299d, 179d, 44d },
                        Note = "Steps/mm vary by board/microstepping (also 400 or 1600 seen) - verify." },
                };
            }
        }
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
        public System.Collections.Generic.List<MachinePreset> Presets { get; } = MachinePreset.List;

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

        private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPreset((sender as ComboBox)?.SelectedItem as MachinePreset);
        }

        // Seed the wizard fields from a machine preset (X/Y/Z only). Everything stays editable and the user
        // still confirms each step. The travel field is PHYSICAL travel, so add back the 2x pull-off the stored
        // $130-$132 reserves; the home corner is only a suggestion (most hobby machines home front-left).
        private void ApplyPreset(MachinePreset p)
        {
            if (p == null)
                return;
            PresetNote = p.Note ?? string.Empty;

            foreach (var axis in Setup.Axes)
            {
                if (axis.Index > 2)
                    continue;   // presets cover X/Y/Z
                int i = axis.Index;
                if (p.StepsPerMm != null && i < p.StepsPerMm.Length) axis.StepsPerMm = p.StepsPerMm[i];
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
