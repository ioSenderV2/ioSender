/*
 * StepperCalibrationScratchWizard.xaml.cs - part of CNC Controls library
 *
 * v0.47 / 2026-06-01 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for StepperCalibrationScratchWizard.xaml
    /// </summary>
    public partial class StepperCalibrationScratchWizard : UserControl, IGrblConfigTab
    {
        private GrblViewModel model = null;
        private GrblSettingDetails setting = null;

        public StepperCalibrationScratchWizard()
        {
            InitializeComponent();

            model = DataContext as GrblViewModel;
            Results = new ObservableCollection<CalibrationResult>();
        }

        #region Methods required by GrblConfigTab interface

        public GrblConfigType GrblConfigType { get { return GrblConfigType.StepperCalibrationScratch; } }

        public void Activate(bool activate)
        {
            if (activate)
            {
                // Default to the first in-plane (X/Y) axis if not yet selected.
                if (CalAxes.Count > 0 && CalAxes.FirstOrDefault(a => a.Index == Axis) == null)
                    Axis = CalAxes[0].Index;
                getAxisDetails(Axis);
            }

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        #endregion

        #region Dependency properties

        // The grbl axis index ($100 = X = 0, $101 = Y = 1) of the axis being calibrated.
        public static readonly DependencyProperty AxisProperty = DependencyProperty.Register(nameof(Axis), typeof(int), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(0, new PropertyChangedCallback(OnAxisChanged)));
        public int Axis
        {
            get { return (int)GetValue(AxisProperty); }
            set { SetValue(AxisProperty, value); }
        }
        private static void OnAxisChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((StepperCalibrationScratchWizard)d).getAxisDetails((int)e.NewValue);
        }

        public static readonly DependencyProperty CurrentResolutionProperty = DependencyProperty.Register(nameof(CurrentResolution), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(0d));
        public double CurrentResolution
        {
            get { return (double)GetValue(CurrentResolutionProperty); }
            set { SetValue(CurrentResolutionProperty, value); }
        }

        public static readonly DependencyProperty SpanProperty = DependencyProperty.Register(nameof(Span), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(400d));
        public double Span
        {
            get { return (double)GetValue(SpanProperty); }
            set { SetValue(SpanProperty, value); }
        }

        public static readonly DependencyProperty DeltaProperty = DependencyProperty.Register(nameof(Delta), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(0.010d));
        public double Delta
        {
            get { return (double)GetValue(DeltaProperty); }
            set { SetValue(DeltaProperty, value); }
        }

        public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(3d));
        public double Points
        {
            get { return (double)GetValue(PointsProperty); }
            set { SetValue(PointsProperty, value); }
        }

        public static readonly DependencyProperty ScratchDepthProperty = DependencyProperty.Register(nameof(ScratchDepth), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(0.3d));
        public double ScratchDepth
        {
            get { return (double)GetValue(ScratchDepthProperty); }
            set { SetValue(ScratchDepthProperty, value); }
        }

        public static readonly DependencyProperty PlungeFeedProperty = DependencyProperty.Register(nameof(PlungeFeed), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(100d));
        public double PlungeFeed
        {
            get { return (double)GetValue(PlungeFeedProperty); }
            set { SetValue(PlungeFeedProperty, value); }
        }

        public static readonly DependencyProperty ScratchFeedProperty = DependencyProperty.Register(nameof(ScratchFeed), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(500d));
        public double ScratchFeed
        {
            get { return (double)GetValue(ScratchFeedProperty); }
            set { SetValue(ScratchFeedProperty, value); }
        }

        public static readonly DependencyProperty SafeZProperty = DependencyProperty.Register(nameof(SafeZ), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(5d));
        public double SafeZ
        {
            get { return (double)GetValue(SafeZProperty); }
            set { SetValue(SafeZProperty, value); }
        }

        public static readonly DependencyProperty LineLengthProperty = DependencyProperty.Register(nameof(LineLength), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(5d));
        public double LineLength
        {
            get { return (double)GetValue(LineLengthProperty); }
            set { SetValue(LineLengthProperty, value); }
        }

        public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(15d));
        public double RowSpacing
        {
            get { return (double)GetValue(RowSpacingProperty); }
            set { SetValue(RowSpacingProperty, value); }
        }

        public static readonly DependencyProperty EdgeMarginProperty = DependencyProperty.Register(nameof(EdgeMargin), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(10d));
        public double EdgeMargin
        {
            get { return (double)GetValue(EdgeMarginProperty); }
            set { SetValue(EdgeMarginProperty, value); }
        }

        public static readonly DependencyProperty SpindleRPMProperty = DependencyProperty.Register(nameof(SpindleRPM), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(18000d));
        public double SpindleRPM
        {
            get { return (double)GetValue(SpindleRPMProperty); }
            set { SetValue(SpindleRPMProperty, value); }
        }

        public static readonly DependencyProperty ToolNumberProperty = DependencyProperty.Register(nameof(ToolNumber), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(1d));
        public double ToolNumber
        {
            get { return (double)GetValue(ToolNumberProperty); }
            set { SetValue(ToolNumberProperty, value); }
        }

        public static readonly DependencyProperty NewResolutionProperty = DependencyProperty.Register(nameof(NewResolution), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(0d, new PropertyChangedCallback(OnNewResolutionChanged)));
        public double NewResolution
        {
            get { return (double)GetValue(NewResolutionProperty); }
            set { SetValue(NewResolutionProperty, value); }
        }
        private static void OnNewResolutionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var instance = (StepperCalibrationScratchWizard)d;
            instance.CanUpdate = instance.setting != null && (double)e.NewValue > 0d;
        }

        public static readonly DependencyProperty CanUpdateProperty = DependencyProperty.Register(nameof(CanUpdate), typeof(bool), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(false));
        public bool CanUpdate
        {
            get { return (bool)GetValue(CanUpdateProperty); }
            set { SetValue(CanUpdateProperty, value); }
        }

        // X/Y axes only - hole/line spacing can only measure travel in the work plane.
        public static readonly DependencyProperty CalAxesProperty = DependencyProperty.Register(nameof(CalAxes), typeof(List<Axis>), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(null));
        public List<Axis> CalAxes
        {
            get { return (List<Axis>)GetValue(CalAxesProperty); }
            set { SetValue(CalAxesProperty, value); }
        }

        public ObservableCollection<CalibrationResult> Results { get; private set; }

        #endregion

        private void getAxisDetails(int axisIndex)
        {
            if (model == null)
                return;

            setting = GrblSettings.Get(GrblSetting.TravelResolutionBase + axisIndex);
            if (setting != null)
                CurrentResolution = dbl.Parse(setting.Value);
        }

        private static string F(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string FR(double value)
        {
            return value.ToString("0.000###", CultureInfo.InvariantCulture);
        }

        // Build the calibration program. Steps/mm cannot be changed mid-program, so candidate
        // settings are simulated by commanding distance C = span * candidate / current - this
        // produces the physically identical motion to setting steps/mm = candidate.
        private List<string> BuildProgram()
        {
            var lines = new List<string>();

            int axisIndex = Axis;
            string axis = GrblInfo.AxisIndexToLetter(axisIndex);
            string perp = axis == "X" ? "Y" : "X";
            double s0 = CurrentResolution;
            double span = Span, delta = Delta, l = LineLength, rs = RowSpacing, m = EdgeMargin;
            int n = Math.Max(1, (int)Math.Round(Points));
            int tool = (int)Math.Round(ToolNumber);

            Results.Clear();

            lines.Add(string.Format("(ioSender stepper calibration - {0} axis - V-bit scratch lines)", axis));
            lines.Add(string.Format("(span {0} mm, current steps/mm {1}, measure spacing between the two lines of each pair)", F(span), FR(s0)));

            // Prereq + fit/position prompt (mirrors the other generated programs): confirm the link, then hold while
            // the operator fits the V-bit, jogs to a stock corner and sets work zero. Nothing moves until OK.
            lines.Add("(PREREQ, connected, noalarm)");
            lines.Add("(MBOX, OKCANCEL, Fit the V-bit, jog to a stock corner, then set work zero here - on the DRO click Zero All [Z0 = stock top]. Click OK to start, Cancel to abort.)");
            lines.Add("(WAITIDLE)");

            // Work-coordinate prolog (the operator zeroed at the corner) - no machine-coord G53 moves, so homing is
            // not required. Lift to safe Z first (off the stock top), then change tool / start the spindle.
            lines.Add("G90 G94 G17 G21");
            lines.Add("G54");
            lines.Add("G0 Z" + F(SafeZ));
            if (tool > 0)
                lines.Add("M6 T" + tool.ToString(CultureInfo.InvariantCulture));
            if (SpindleRPM > 0d)
                lines.Add("S" + ((int)Math.Round(SpindleRPM)).ToString(CultureInfo.InvariantCulture) + " M3");

            for (int i = 0; i < n; i++)
            {
                double k = i - (n - 1) / 2.0;          // symmetric offset around the nominal value
                double candidate = s0 + k * delta;
                double commanded = s0 == 0d ? span : span * candidate / s0;
                double a0 = m;                          // first mark, edge margin in from the corner
                double a1 = m + commanded;              // second mark, one commanded span further
                double rowStart = m + i * rs;           // each pair offset along the perpendicular axis
                double rowEnd = rowStart + l;
                string label = k == 0d ? "nominal" : (k > 0d ? "+" : "") + FR(k * delta);

                lines.Add(string.Format("(P{0} {1}  steps/mm {2}  commanded {3})", i + 1, label, FR(candidate), F(commanded)));
                // mark 1 - at the edge margin
                lines.Add(string.Format("G0 {0}{1} {2}{3}", axis, F(a0), perp, F(rowStart)));
                lines.Add(string.Format("G1 Z-{0} F{1}", F(ScratchDepth), F(PlungeFeed)));
                lines.Add(string.Format("G1 {0}{1} F{2}", perp, F(rowEnd), F(ScratchFeed)));
                lines.Add("G0 Z" + F(SafeZ));
                // mark 2 - one commanded span away
                lines.Add(string.Format("G0 {0}{1} {2}{3}", axis, F(a1), perp, F(rowStart)));
                lines.Add(string.Format("G1 Z-{0} F{1}", F(ScratchDepth), F(PlungeFeed)));
                lines.Add(string.Format("G1 {0}{1} F{2}", perp, F(rowEnd), F(ScratchFeed)));
                lines.Add("G0 Z" + F(SafeZ));

                var result = new CalibrationResult(i + 1, label, candidate, commanded, s0);
                result.PropertyChanged += Result_PropertyChanged;
                Results.Add(result);
            }

            if (SpindleRPM > 0d)
                lines.Add("M5");
            lines.Add("G0 Z" + F(SafeZ));
            lines.Add("M30");

            return lines;
        }

        private void Result_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CalibrationResult.Measured))
                Recompute();
        }

        // Highlight the pair whose measured spacing is closest to the span (the best candidate
        // setting) and set the new steps/mm to the average of all measured pairs' implied values.
        private void Recompute()
        {
            var measured = Results.Where(r => r.HasMeasurement).ToList();

            double bestErr = double.MaxValue;
            CalibrationResult closest = null;
            foreach (var r in measured)
            {
                double err = Math.Abs(r.Measured.Value - Span);
                if (err < bestErr)
                {
                    bestErr = err;
                    closest = r;
                }
            }

            foreach (var r in Results)
                r.IsClosest = r == closest;

            if (measured.Count > 0)
                NewResolution = Math.Round(measured.Average(r => r.Estimate), GrblInfo.IsGrblHAL ? 6 : 3);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            switch ((string)((Button)sender).Tag)
            {
                case "generate":
                    Generate();
                    break;

                case "run":
                    Run();
                    break;

                case "save":
                    Save();
                    break;
            }
        }

        // Run the buffered program via the macro path (like Load Stock / Surface spoilboard): the run-control
        // panel floats, then the program streams - its (PREREQ)/(MBOX)/(WAITIDLE) confirm state and prompt the
        // operator to fit the bit and set work zero before any motion.
        private void Run()
        {
            if (model == null)
                return;
            if (string.IsNullOrWhiteSpace(txtProgram.Text) || Results.Count == 0)
                Generate();
            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                return;

            MacroProcessor.RunControlPanel?.Invoke(model);
            MacroProcessor.Run(model, "Stepper calibration", txtProgram.Text, true);
        }

        private void Generate()
        {
            if (model == null)
                return;

            if (CurrentResolution <= 0d)
            {
                MessageBox.Show(LibStrings.FindResource("ScNoAxisResolution"), "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            NewResolution = 0d;

            // Show the generated program in the preview buffer; Run streams it via the macro path (like Load Stock).
            txtProgram.Text = string.Join("\r\n", BuildProgram());
            btnRun.IsEnabled = true;
        }

        private void Save()
        {
            if (setting == null || NewResolution <= 0d)
                return;

            setting.Value = NewResolution.ToInvariantString();
            if (GrblSettings.Save())
            {
                CurrentResolution = NewResolution;
                MessageBox.Show(string.Format("{0} steps/mm updated to {1}.", GrblInfo.AxisIndexToLetter(Axis), FR(NewResolution)),
                                "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (model == null)
                model = DataContext as GrblViewModel;

            if (!_paramsLoaded)   // restore the saved parameters once, then persist on every change
            {
                _paramsLoaded = true;
                LoadScratchParams();
                cbxTool.SelectedIndex = ToolNumber > 0d ? 1 : 0;   // Loaded / Prompt
                foreach (var dp in new[] {
                    SpanProperty, DeltaProperty, PointsProperty, ScratchDepthProperty, PlungeFeedProperty,
                    ScratchFeedProperty, SafeZProperty, LineLengthProperty, RowSpacingProperty,
                    EdgeMarginProperty, SpindleRPMProperty, ToolNumberProperty })
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, typeof(StepperCalibrationScratchWizard))
                        .AddValueChanged(this, (s, ev) => SaveScratchParams());
            }

            if (model != null && (CalAxes == null || CalAxes.Count == 0))
            {
                CalAxes = model.Axes.Where(a => a.Letter == "X" || a.Letter == "Y").ToList();
                if (CalAxes.Count > 0)
                    Axis = CalAxes[0].Index;
                getAxisDetails(Axis);
            }

            txtWarnings.Text = "Stock must fit the full span + margin along the axis and (margin + (points-1) x row spacing + line length) across it. A V-bit gives sharp, easy-to-measure scratch lines.";
            txtProgram.Text =
                "1. Select the axis, confirm the current steps/mm, set span / delta / test points / margin / RPM, choose Tool (Loaded or Prompt), then press Generate (the program appears here).\n" +
                "2. Press Run - a confirmation and the floating run-control panel appear.\n" +
                "3. When prompted, fit the V-bit, jog to a stock corner and set work zero here (DRO > Zero All; Z0 = stock top), then click OK. The first mark is made the Edge margin in from that corner on both axes; marks stay inside the stock.\n" +
                "4. It scratches a pair of lines (perpendicular to the axis) for each candidate steps/mm.\n" +
                "5. Measure the spacing between the two lines of each pair with calipers and enter it in the Measured column.\n" +
                "6. The pair closest to the span is highlighted; New steps/mm is the average implied value. Press Save steps/mm to update the setting.\n\n" +
                "Notes: Tool \"Loaded\" skips the tool change; \"Prompt\" issues M6 to load the bit. RPM 0 omits spindle commands. Z cannot be calibrated this way - use the jog-based Stepper calibration tab for Z.";
        }

        private bool _paramsLoaded = false;

        private void cbxTool_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Loaded (index 0) = no tool change; Prompt (index 1) = issue M6 to load the bit (ToolNumber > 0).
            ToolNumber = cbxTool.SelectedIndex == 1 ? 1d : 0d;
        }

        private static string ScratchParamsFile {
            get { return System.IO.Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, "StepperCalScratch.xml"); }
        }

        // Restore the parameters saved by SaveScratchParams (best effort - falls back to the property defaults).
        private void LoadScratchParams()
        {
            try
            {
                if (!System.IO.File.Exists(ScratchParamsFile))
                    return;
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(ScratchParams));
                using (var fs = System.IO.File.OpenRead(ScratchParamsFile))
                {
                    var p = (ScratchParams)xs.Deserialize(fs);
                    Span = p.Span; Delta = p.Delta; Points = p.Points; ScratchDepth = p.ScratchDepth;
                    PlungeFeed = p.PlungeFeed; ScratchFeed = p.ScratchFeed; SafeZ = p.SafeZ;
                    LineLength = p.LineLength; RowSpacing = p.RowSpacing; EdgeMargin = p.EdgeMargin;
                    SpindleRPM = p.SpindleRPM; ToolNumber = p.ToolNumber;
                }
            }
            catch { /* ignore - use the defaults */ }
        }

        // Persist the current parameters (called on every parameter change).
        private void SaveScratchParams()
        {
            try
            {
                var p = new ScratchParams {
                    Span = Span, Delta = Delta, Points = Points, ScratchDepth = ScratchDepth,
                    PlungeFeed = PlungeFeed, ScratchFeed = ScratchFeed, SafeZ = SafeZ, LineLength = LineLength,
                    RowSpacing = RowSpacing, EdgeMargin = EdgeMargin, SpindleRPM = SpindleRPM, ToolNumber = ToolNumber
                };
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(ScratchParams));
                using (var fs = System.IO.File.Create(ScratchParamsFile))
                    xs.Serialize(fs, p);
            }
            catch { }
        }
    }

    // Persisted stepper-calibration (scratch) parameters. Public for XmlSerializer.
    public class ScratchParams
    {
        public double Span = 400d, Delta = 0.010d, Points = 3d, ScratchDepth = 0.3d, PlungeFeed = 100d,
                      ScratchFeed = 500d, SafeZ = 5d, LineLength = 5d, RowSpacing = 15d, EdgeMargin = 10d,
                      SpindleRPM = 18000d, ToolNumber = 1d;
    }

    public class CalibrationResult : ViewModelBase
    {
        private double? _measured = null;
        private bool _isClosest = false;
        private readonly double _baseResolution;

        public CalibrationResult(int index, string label, double steps, double commanded, double baseResolution)
        {
            Index = index;
            Label = label;
            Steps = steps;
            Commanded = commanded;
            _baseResolution = baseResolution;
        }

        public int Index { get; private set; }
        public string Label { get; private set; }
        public double Steps { get; private set; }
        public double Commanded { get; private set; }

        public double? Measured
        {
            get { return _measured; }
            set
            {
                _measured = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Estimate));
            }
        }

        public bool HasMeasurement { get { return _measured.HasValue && _measured.Value > 0d; } }

        // Implied true steps/mm from this pair: measured = commanded * base / true  =>  true = commanded * base / measured.
        public double Estimate
        {
            get { return HasMeasurement ? Commanded * _baseResolution / _measured.Value : double.NaN; }
        }

        public bool IsClosest
        {
            get { return _isClosest; }
            set { _isClosest = value; OnPropertyChanged(); }
        }
    }
}
