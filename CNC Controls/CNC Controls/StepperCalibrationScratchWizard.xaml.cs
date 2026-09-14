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
    public partial class StepperCalibrationScratchWizard : ConfigPanel<ScratchParams>, IGrblConfigTab
    {
        private GrblViewModel model = null;
        private GrblSettingDetails setting = null;
        private string program = string.Empty;   // last generated program (previewed in the bottom Program View)

        public StepperCalibrationScratchWizard()
        {
            InitializeComponent();

            model = DataContext as GrblViewModel;
            Results = new ObservableCollection<CalibrationResult>();
        }

        #region Methods required by GrblConfigTab interface

        public GrblConfigType GrblConfigType { get { return GrblConfigType.StepperCalibrationScratch; } }

        // This tool's own program view (ProgramView refactor): created lazily, titled, connected to the streamer
        // stack so the overlay hosts it and the run marks it - independent of the Job-tab view.
        private ProgramView programView;
        private void EnsureProgramView()
        {
            if (programView == null)
                programView = new ProgramView { Title = "Stepper Calibration" };
        }

        public void Activate(bool activate)
        {
            isActiveTab = activate;
            // Re-resolve the view model here as well as in the constructor/OnConfigReady (2026-09-13): this
            // wizard's original home was a dockable Tools-tab component, and it now lives inside Machine
            // Setup's Calibration step, where the content is realized on first sub-tab selection. The sibling
            // probe wizard does exactly this for the same reason - a null model turns Generate and Run into
            // silent no-ops (both open with "if (model == null) return;"), which compiles perfectly.
            if (model == null)
                model = DataContext as GrblViewModel;

            if (activate)
            {
                // The origin readout has to FOLLOW the machine, not snapshot it. Without this the panel shows
                // whatever the offset was when the tab opened and keeps showing it after the operator touches
                // off - which is the failure mode where the display says one thing and the run does another.
                if (!subscribed && model != null)
                {
                    model.PropertyChanged += Model_PropertyChanged;
                    subscribed = true;
                }

                // Default to the first in-plane (X/Y) axis if not yet selected.
                if (CalAxes.Count > 0 && CalAxes.FirstOrDefault(a => a.Index == Axis) == null)
                    Axis = CalAxes[0].Index;
                getAxisDetails(Axis);
                if (!string.IsNullOrEmpty(program))
                {
                    EnsureProgramView();
                    programView.SetProgramText(program);
                    programView.Connect();     // this tool's own view shows in the overlay
                }
                MacroProcessor.ActiveRun = Run;                                             // Run runs it

                // Generate-mode registration (see MacroProcessor's own comments / StartJobView for the
                // reference implementation): the shared Run bar reads "Generate" until this tab has built its
                // program, then "Run" - no standalone Generate button of this tab's own any more.
                MacroProcessor.SupportsGenerateMode = true;
                MacroProcessor.ActiveGenerate = Generate;
                MacroProcessor.DiscardGenerated = DiscardProgram;
                MacroProcessor.IsProgramGenerated = !string.IsNullOrEmpty(program);
                RefreshGenerateReady();
            }
            else
            {
                MacroProcessor.ActiveRun = null;
                MacroProcessor.SupportsGenerateMode = false;
                MacroProcessor.ActiveGenerate = null;
                MacroProcessor.DiscardGenerated = null;
                // Discard the generated program on tab-leave too (not just after a run finishes - see
                // DiscardProgram's own comment) - so the tab is always back at "Generate" next time it's
                // focused. Not routed through DiscardProgram() itself: its isActiveTab guard would block the
                // MacroProcessor.IsProgramGenerated write here, since isActiveTab was already set false at
                // the top of this same Activate() call - but this IS the moment that write belongs.
                program = string.Empty;
                programView?.Disconnect();                     // active program follows the focused tab
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
            var w = (StepperCalibrationScratchWizard)d;
            w.getAxisDetails((int)e.NewValue);
            // Axis is NOT one of the PersistedProperties, so it does not reach OnPersistedPropertyChanged and
            // needs its own discard. It is also the worst one to get stale: the axis letter is baked into
            // every line of the program, so a program generated for X and run after switching to Y would cut
            // the X pattern while the panel said Y.
            w.DiscardProgram();
            w.RefreshGenerateReady();
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

        // Minimum stock the pattern needs: span + 2 x margin along the axis, and
        // (points-1) x row spacing + line length + 2 x margin across it.
        // The live work origin this run will use, and whether the pattern fits from there - shown in the
        // editor in place of the prompt that used to ask the operator to set one mid-run.
        /// <summary>
        /// Give the V-bit already in the spindle a tool length offset by touching the puck, before cutting.
        /// </summary>
        /// <remarks>
        /// This program does not change tools, so nothing else would. On this machine a work Z0 is only
        /// meaningful together with the loaded tool's G43.1 - tc.macro applies one on every M6, computed
        /// against the machine-wide baseline - so a bit fitted by hand has no offset and Z0 means whatever
        /// the PREVIOUS tool made it mean. StartJobView's own comment records what that costs: a spoilboard
        /// cut on 2026-08-06 when a second run with the same endmill already fitted emitted no M6, so
        /// nothing re-applied the offset and the job rapided to a work Z0 15.432mm inside the material.
        ///
        /// Uses Setup's sequence, not a copy of it - MacroProcessor.EmitTloBaseline/Reference/Restore were
        /// lifted out of StartJobView for exactly this.
        /// </remarks>
        /// <summary>
        /// Depth of the SECOND mark of each pair - the one a full span away from the first.
        /// </summary>
        /// <remarks>
        /// Two depths rather than one because the reference surface is not flat. A spoilboard that dips
        /// more than the cut depth across a 500mm span marks one end and misses the other entirely, and a
        /// pair with one mark missing cannot be measured at all. Depth does not affect the result - the
        /// measurement is the spacing between the two line CENTRES, and a V-bit's centre sits under the
        /// spindle axis whatever the depth - so the two may differ freely.
        /// </remarks>
        public static readonly DependencyProperty ScratchDepth2Property = DependencyProperty.Register(nameof(ScratchDepth2), typeof(double), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(0.5d));
        public double ScratchDepth2
        {
            get { return (double)GetValue(ScratchDepth2Property); }
            set { SetValue(ScratchDepth2Property, value); }
        }

        public static readonly DependencyProperty ReferenceTloProperty = DependencyProperty.Register(nameof(ReferenceTlo), typeof(bool), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(true));
        public bool ReferenceTlo
        {
            get { return (bool)GetValue(ReferenceTloProperty); }
            set { SetValue(ReferenceTloProperty, value); }
        }

        public static readonly DependencyProperty OriginTextProperty = DependencyProperty.Register(nameof(OriginText), typeof(string), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(""));
        public string OriginText
        {
            get { return (string)GetValue(OriginTextProperty); }
            set { SetValue(OriginTextProperty, value); }
        }

        public static readonly DependencyProperty FitTextProperty = DependencyProperty.Register(nameof(FitText), typeof(string), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(""));
        public string FitText
        {
            get { return (string)GetValue(FitTextProperty); }
            set { SetValue(FitTextProperty, value); }
        }

        public static readonly DependencyProperty MinStockTextProperty = DependencyProperty.Register(nameof(MinStockText), typeof(string), typeof(StepperCalibrationScratchWizard), new PropertyMetadata(""));
        public string MinStockText
        {
            get { return (string)GetValue(MinStockTextProperty); }
            set { SetValue(MinStockTextProperty, value); }
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

            RebuildResults();   // new axis -> new current steps/mm -> refresh prefilled candidates
            UpdateMinStock();
            RefreshGenerateReady();
        }

        // True only between Activate(true)/Activate(false) - guards writes to MacroProcessor's Generate-mode
        // statics (shared across all Generate-first tabs) so a stale event firing after this tab was left
        // can't stomp whichever OTHER tab is now focused. See StartJobView.isActiveTab's own comment.
        private bool isActiveTab = false;
        private bool subscribed = false;

        // The coarse live-readiness gate for the shared Run bar's "Generate" button - mirrors Generate()'s
        // own first precondition check.
        private void RefreshGenerateReady()
        {
            // BRACED, deliberately. This was a braceless "if (isActiveTab)" guarding only the line directly
            // under it, so when a blocked reason was added beside the gate it landed OUTSIDE the guard and
            // wrote MacroProcessor's SHARED statics from a tab that was not active - the exact stomp
            // isActiveTab exists to prevent (see its own comment above).
            if (!isActiveTab)
                return;

            string why = CurrentResolution > 0d ? CheckOrigin() : LibStrings.FindResource("ScNoAxisResolution");
            MacroProcessor.IsGenerateReady = why == null;
            // After the gate, never before: setting it ready clears the reason (see MacroProcessor).
            if (why != null)
                MacroProcessor.GenerateBlockedReason = why;
        }

        // Re-read the origin whenever the machine's own offset or homed state moves under us. WorkPosition
        // fires on every status poll, so filter to the properties that actually change the answer.
        private void Model_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!isActiveTab)
                return;
            if (e.PropertyName == nameof(GrblViewModel.WorkPositionOffset)
             || e.PropertyName == nameof(GrblViewModel.WorkCoordinateSystem)
             || e.PropertyName == nameof(GrblViewModel.HomedState))
                RefreshGenerateReady();
        }

        // Drop the generated program; also registered as MacroProcessor.DiscardGenerated (see Activate) -
        // called right after a clean run finishes so the Run bar reverts to "Generate" for the next job.
        private void DiscardProgram()
        {
            program = string.Empty;
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = false;
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
            double span = Span, l = LineLength, rs = RowSpacing, m = EdgeMargin;
            int tool = (int)Math.Round(ToolNumber);

            RebuildResults();   // (re)populate the candidate rows the g-code below mirrors

            lines.Add(string.Format("(ioSender stepper calibration - {0} axis - V-bit scratch lines)", axis));
            lines.Add(string.Format("(span {0} mm, current steps/mm {1}, measure spacing between the two lines of each pair)", F(span), FR(s0)));

            // Runs against the WORK ORIGIN THAT IS ALREADY SET - after Setup that is the stock corner with Z0
            // on its top, so there is nothing to do but press Run. The prompt that used to live here asked the
            // operator to jog to a corner and Zero All mid-run; it was the slowest part of the tool and it
            // carried a trap, because it fired BEFORE the "G54" line below it. Zero All zeroes whichever WCS
            // is ACTIVE, so an operator working in G55 zeroed G55 and then watched the program switch to G54
            // and run against whatever that held. The G54 line is gone with it: this program now inherits the
            // active WCS and never changes it. What replaced the promise is a real check - the editor shows
            // the live origin and refuses to generate unless the whole pattern fits the machine envelope.
            // "homed" is here because of the G30 park below, and ONLY because of it. Nothing else in this
            // program uses machine coordinates - the pattern itself is pure work-coordinate motion, which is
            // why this tool used to run on an unhomed machine. EmitGotoG30 is three G53 moves, and G53 on an
            // unhomed machine addresses coordinates that mean nothing.
            lines.Add("(PREREQ, connected, homed, noalarm)");

            // Park at G30 before asking for the bit, same as the probe wizard. Without it the prompt appears
            // with the spindle wherever the last operation left it - possibly down in the work - and the
            // operator is invited to reach in and change a tool there.
            lines.Add("(park at G30 - fit the V-bit)");
            MacroProcessor.EmitGotoG30(line => lines.Add(line));
            lines.Add("(WAITIDLE)");

            // Factual, not a task: the operator has already set the origin, so this only confirms what is
            // about to be cut and from where. The "fit the V-bit" lead-in is dropped when a tool change is
            // being emitted below - M6 prompts for the tool itself, and two prompts for one bit is one too
            // many. Nothing moves until OK.
            lines.Add(string.Format("(MBOX, OKCANCEL, {0}About to scratch {1} pairs of lines over {2}mm, starting {3}mm from the CURRENT work origin - {4}mm deep. Click OK to start, Cancel to abort.)",
                tool > 0 ? string.Empty : "Fit the V-bit. ", Results.Count, F(span), F(m),
                F(ScratchDepth) + "/" + F(ScratchDepth2)));
            lines.Add("(WAITIDLE)");

            lines.Add("G90 G94 G17 G21");

            // NO G49 HERE. It was added on 2026-09-14 reasoning that a stale tool length offset would wreck
            // a sub-millimetre scratch - which is backwards on this machine. The offset is not incidental,
            // it is what makes one work Z0 mean the same thing for every tool: tc.macro applies a G43.1 on
            // every M6, computed against the machine-wide baseline. Cancelling it leaves Z0 referenced to
            // whatever tool last had an offset. StartJobView records what that costs - a spoilboard cut on
            // 2026-08-06, "the offset was never stale, just discarded".
            //
            // Instead, when asked, give the loaded bit its OWN offset by touching the puck. No tool change,
            // so no prompt and no swap - which is the whole point of running with Tool = Loaded.
            // Gated on a toolsetter being DEFINED, not on its feeds: the puck probe's own feeds live in
            // tlo.macro now, where the puck is.
            bool referenceTlo = ReferenceTlo && ProbeDefinitions.Items.Any(d => d.ProbeType == ProbeType.ToolSetter);
            if (referenceTlo)
            {
                MacroProcessor.EmitTloBaseline(lines.Add, model != null && model.IsTloReferenceSet,
                                               AppConfig.Settings.Base.TloRefBaseline);
                // The V-bit is a rigid cutting tool, so any id but 8 - tlo.macro reads that as "pushes the
                // puck's own switch, use the toolsetter input". Pass the configured tool number when there
                // is one so the macro's own PRINT names the right tool.
                // The WCS to come back to is whichever one is ACTIVE - this program runs against the origin
                // already set and must not be dumped into the puck's G59.3 on the way out. Falls back to G54
                // only when the model cannot say, which is the same frame the rest of the program assumes.
                string wcs = model != null && !string.IsNullOrEmpty(model.WorkCoordinateSystem)
                           ? model.WorkCoordinateSystem : "G54";
                MacroProcessor.EmitTloReference(lines.Add, tool > 0 && tool != 8 ? tool : 1, wcs);
            }

            if (tool > 0)
                lines.Add("M6 T" + tool.ToString(CultureInfo.InvariantCulture));
            if (SpindleRPM > 0d)
                lines.Add("S" + ((int)Math.Round(SpindleRPM)).ToString(CultureInfo.InvariantCulture) + " M3");

            // AFTER the tool change, not before it. This used to sit above the M6, where it was pure noise -
            // the tool change retracts and parks on its own, discarding whatever height this had just set.
            // It is not redundant here though: the loop below opens with an absolute "G1 Z-depth" at PLUNGE
            // feed, so without first coming down to the safe height that plunge starts from wherever the tool
            // change left the spindle - machine top - and crawls the entire way down at 100 mm/min.
            lines.Add("G0 Z" + F(SafeZ));

            for (int i = 0; i < Results.Count; i++)
            {
                var r = Results[i];
                double commanded = r.Commanded;
                double a0 = m;                          // first mark, edge margin in from the corner
                double a1 = m + commanded;              // second mark, one commanded span further
                double rowStart = m + i * rs;           // each pair offset along the perpendicular axis
                double rowEnd = rowStart + l;

                lines.Add(string.Format("(P{0} {1}  steps/mm {2}  commanded {3})", i + 1, r.Label, FR(r.Steps), F(commanded)));
                // mark 1 - at the edge margin
                lines.Add(string.Format("G0 {0}{1} {2}{3}", axis, F(a0), perp, F(rowStart)));
                lines.Add(string.Format("G1 Z-{0} F{1}", F(ScratchDepth), F(PlungeFeed)));
                lines.Add(string.Format("G1 {0}{1} F{2}", perp, F(rowEnd), F(ScratchFeed)));
                lines.Add("G0 Z" + F(SafeZ));
                // mark 2 - one commanded span away, at its OWN depth: a span's worth of spoilboard is
                // rarely flat enough for one number to mark both ends crisply.
                lines.Add(string.Format("G0 {0}{1} {2}{3}", axis, F(a1), perp, F(rowStart)));
                lines.Add(string.Format("G1 Z-{0} F{1}", F(ScratchDepth2), F(PlungeFeed)));
                lines.Add(string.Format("G1 {0}{1} F{2}", perp, F(rowEnd), F(ScratchFeed)));
                lines.Add("G0 Z" + F(SafeZ));
            }

            if (SpindleRPM > 0d)
                lines.Add("M5");
            lines.Add("G0 Z" + F(SafeZ));
            // Bookkeeping BEFORE the park, so the park is the last thing that moves - see below.
            if (referenceTlo)
                MacroProcessor.EmitTloRestore(lines.Add);

            // Park at G30 rather than finishing at safe Z over the last scratch, where the spindle sits in
            // the middle of the work with nothing to tell the operator it is done.
            //
            // It also fixes the Run bar staying on "Run" after a clean finish. MacroProcessor's run watcher
            // ends at StreamingState Idle, but the JobFinished it needs to DISCARD the program comes from
            // OnProgramEnd, which the controller's own "[MSG:Pgm End]" drives. This program's last lines
            // were non-motion (G43.1, a PRINT, a parameter assignment, M30), so the machine was already
            // Idle and the watcher unsubscribed 49ms BEFORE Pgm End arrived - measured, 2026-09-14
            // 11:15:43.251 vs .300 - and nothing was listening when JobFinished finally came. Ending in
            // motion restores the ordering every other Generate-first tool already relies on.
            MacroProcessor.EmitGotoG30(lines.Add);
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

        // Prefill the results grid from the current parameters so the candidate steps/mm + commanded
        // distances are visible before running; only the Measured column is left for the operator to fill in.
        private void RebuildResults()
        {
            foreach (var r in Results)
                r.PropertyChanged -= Result_PropertyChanged;
            Results.Clear();

            double s0 = CurrentResolution;
            if (s0 <= 0d)
                return;

            double span = Span, delta = Delta;
            int n = Math.Max(1, (int)Math.Round(Points));
            for (int i = 0; i < n; i++)
            {
                double k = i - (n - 1) / 2.0;           // symmetric offset around the nominal value
                double candidate = s0 + k * delta;
                double commanded = span * candidate / s0;
                string label = k == 0d ? "nominal" : (k > 0d ? "+" : "") + FR(k * delta);

                var result = new CalibrationResult(i + 1, label, candidate, commanded, s0);
                result.PropertyChanged += Result_PropertyChanged;
                Results.Add(result);
            }
        }

        /// <summary>
        /// The pattern's extent in WORK coordinates from the origin: how far it reaches along the axis being
        /// calibrated, and across it. Deliberately the LARGEST candidate's commanded distance, not the
        /// nominal span - a candidate above the current steps/mm is commanded FURTHER than the span asked
        /// for, and that longest pair is the one that runs off the end of the stock (or the table).
        /// </summary>
        private void PatternExtent(out double along, out double across)
        {
            int n = Math.Max(1, (int)Math.Round(Points));
            double maxCommanded = Span;
            foreach (var r in Results)
                if (r.Commanded > maxCommanded)
                    maxCommanded = r.Commanded;

            along = EdgeMargin + maxCommanded;
            across = EdgeMargin + (n - 1) * RowSpacing + LineLength;
        }

        /// <summary>
        /// What this run will do to the machine, from the origin that is set RIGHT NOW - and whether it
        /// fits. Sets <see cref="OriginText"/>/<see cref="FitText"/> for the editor and returns the reason
        /// it cannot run, or null when it can.
        /// </summary>
        /// <remarks>
        /// This replaced a prompt that asked the operator to promise they had set a zero. A promise is not a
        /// signal; the work offset is. Note what is deliberately NOT tested: "is G54 defined". There is no
        /// such state - an unset WCS simply holds 0,0,0, which is also a perfectly legitimate origin for
        /// someone who zeroed at machine origin on purpose, so treating zero as "unset" would refuse a valid
        /// setup and still not catch a wrong one. The consequences are testable and the intent is not, so
        /// this checks the consequences: does the whole pattern stay inside the machine envelope, and is the
        /// work Z0 somewhere a stock top could actually be.
        /// </remarks>
        private string CheckOrigin()
        {
            OriginText = FitText = string.Empty;

            if (model == null)
                return "Not connected.";

            var wco = model.WorkPositionOffset;
            int axisIndex = Axis;
            int perpIndex = axisIndex == 0 ? 1 : 0;

            OriginText = string.Format(CultureInfo.InvariantCulture, "{0}  X{1:0.000}  Y{2:0.000}  Z{3:0.000}",
                string.IsNullOrEmpty(model.WorkCoordinateSystem) ? "current" : model.WorkCoordinateSystem,
                wco.X, wco.Y, wco.Z);

            double along, across;
            PatternExtent(out along, out across);

            // Z0 at the machine's Z home means the "stock top" is the top of Z travel - nothing can be
            // sitting there, so the V-bit would trace 0.3mm below the machine's ceiling and cut air. This is
            // the one case where a zero offset IS conclusive, because the geometry rules it out.
            if (wco.Z == 0d)
                return "Work Z0 is at the machine's Z home, so there is no stock top to scratch. Touch off on the stock first.";

            // A refusal now, not a warning. It used to be the latter because the program was pure
            // work-coordinate motion; it parks at G30 first as of 2026-09-14, which is G53, so its own
            // (PREREQ) demands homed too. Better to say so here than to generate a program that refuses
            // itself at the moment the operator presses Run.
            if (model.HomedState != HomedState.Homed)
                return "The machine is not homed. This program parks at G30 before asking for the V-bit, and the pattern cannot be checked against the machine envelope until the machine knows where it is.";

            // MPos = WPos + WCO, so the machine coordinate each end of the pattern reaches is the work
            // extent plus the offset. Checked per axis against the SHARED envelope (GrblInfo.ReachableLimit -
            // travel minus the homing pull-off, the formula a hand-rolled copy got wrong on 2026-09-14).
            string bad = EnvelopeFault(axisIndex, wco.Values[axisIndex], along)
                      ?? EnvelopeFault(perpIndex, wco.Values[perpIndex], across)
                      ?? EnvelopeFault(2, wco.Z, SafeZ, -ScratchDepth);
            if (bad != null)
                return bad;

            FitText = string.Format(CultureInfo.InvariantCulture, "pattern {0:0} x {1:0} mm from the origin - fits", along, across);
            return null;
        }

        private string EnvelopeFault(int axis, double offset, params double[] workReaches)
        {
            double lo = GrblInfo.ReachableLimit(axis, true), hi = GrblInfo.ReachableLimit(axis, false);
            if (double.IsNaN(lo) || double.IsNaN(hi))
            {
                FitText = "machine travel or pull-off unknown - cannot check the envelope";
                return null;
            }
            double min = Math.Min(lo, hi), max = Math.Max(lo, hi);

            foreach (double reach in workReaches)
            {
                double target = offset + reach;
                if (target < min || target > max)
                    return string.Format(CultureInfo.InvariantCulture,
                        "The pattern runs off the table: it reaches {0}{1:0.0} in machine coordinates, outside {2:0.0}..{3:0.0}. Move the origin, or reduce the span.",
                        GrblInfo.AxisIndexToLetter(axis), target, min, max);
            }
            return null;
        }

        // Minimum stock the pattern needs: span + 2 x margin along the axis; (points-1) x row spacing +
        // line length + 2 x margin across it.
        private void UpdateMinStock()
        {
            int n = Math.Max(1, (int)Math.Round(Points));
            double along = Span + 2d * EdgeMargin;
            double across = (n - 1) * RowSpacing + LineLength + 2d * EdgeMargin;
            MinStockText = string.Format(CultureInfo.InvariantCulture, "{0:0} x {1:0} mm", along, across);
        }

        // A watched parameter changed: persist it, refresh the prefilled results grid and minimum-stock
        // figure - and THROW AWAY the generated program.
        //
        // That last part was missing, and it is the dangerous half. The program is built once and held; the
        // Run bar reads "Run" while a program is held. So changing the span, the depth, the feeds or the tool
        // left the bar saying Run with the PREVIOUS program still loaded, and pressing it cut the old
        // pattern while the panel described the new one. The sibling probe wizard has always done this from
        // its own OnCalInputChanged; this one and AutoSquareWizard never did.
        protected override void OnPersistedPropertyChanged()
        {
            Persist();
            RebuildResults();
            UpdateMinStock();
            DiscardProgram();
            RefreshGenerateReady();   // extents changed, so the envelope check has to be re-run
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            switch ((string)((Button)sender).Tag)
            {
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
            if (string.IsNullOrWhiteSpace(program) || Results.Count == 0)
                Generate();
            if (string.IsNullOrWhiteSpace(program))
                return;

            MacroProcessor.Run(model, "Stepper calibration " + GrblInfo.AxisIndexToLetter(Axis), program, true);
        }

        private void Generate()
        {
            if (model == null)
                return;

            if (CurrentResolution <= 0d)
            {
                AppDialogs.Show(LibStrings.FindResource("ScNoAxisResolution"), "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            string originWhy = CheckOrigin();
            if (originWhy != null)
            {
                AppDialogs.Show(originWhy, "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            NewResolution = 0d;

            // Build the program and preview it in the bottom Program View (pops it open); Run streams it.
            program = string.Join("\r\n", BuildProgram());
            MacroProcessor.PublishGenerated("Stepper calibration " + GrblInfo.AxisIndexToLetter(Axis), program, EnsureProgramView, () => programView);
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = true;   // flips the shared Run bar from "Generate" to "Run"
        }

        private void Save()
        {
            if (setting == null || NewResolution <= 0d)
                return;

            setting.Value = NewResolution.ToInvariantString();
            if (GrblSettings.Save())
            {
                CurrentResolution = NewResolution;
                AppDialogs.Show(string.Format("{0} steps/mm updated to {1}.", GrblInfo.AxisIndexToLetter(Axis), FR(NewResolution)),
                                "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void cbxTool_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Loaded (index 0) = no tool change; Prompt (index 1) = issue M6 to load the bit (ToolNumber > 0).
            ToolNumber = cbxTool.SelectedIndex == 1 ? 1d : 0d;
        }

        // Persisted as the "StepperCalScratch" section of App.config (folded in from StepperCalScratch.xml).
        // ConfigPanel restores/saves this via the overrides below; AppConfig reads it for the section.
        public static ScratchParams SectionConfig;

        #region ConfigPanel<ScratchParams> overrides

        protected override ScratchParams Config { get { return SectionConfig; } set { SectionConfig = value; } }

        protected override DependencyProperty[] PersistedProperties => new[] {
            SpanProperty, DeltaProperty, PointsProperty, ScratchDepthProperty, PlungeFeedProperty,
            ScratchFeedProperty, SafeZProperty, LineLengthProperty, RowSpacingProperty,
            EdgeMarginProperty, SpindleRPMProperty, ToolNumberProperty, ReferenceTloProperty,
            ScratchDepth2Property };

        protected override void ApplyConfig(ScratchParams p)
        {
            Span = p.Span; Delta = p.Delta; Points = p.Points; ScratchDepth = p.ScratchDepth;
            // Fall back to the single depth this profile already had - see ScratchParams.ScratchDepth2.
            ScratchDepth2 = p.ScratchDepth2 > 0d ? p.ScratchDepth2 : p.ScratchDepth;
            PlungeFeed = p.PlungeFeed; ScratchFeed = p.ScratchFeed; SafeZ = p.SafeZ;
            LineLength = p.LineLength; RowSpacing = p.RowSpacing; EdgeMargin = p.EdgeMargin;
            SpindleRPM = p.SpindleRPM; ToolNumber = p.ToolNumber; ReferenceTlo = p.ReferenceTlo;
        }

        protected override ScratchParams CaptureConfig()
        {
            return new ScratchParams {
                Span = Span, Delta = Delta, Points = Points, ScratchDepth = ScratchDepth, ScratchDepth2 = ScratchDepth2,
                PlungeFeed = PlungeFeed, ScratchFeed = ScratchFeed, SafeZ = SafeZ, LineLength = LineLength,
                RowSpacing = RowSpacing, EdgeMargin = EdgeMargin, SpindleRPM = SpindleRPM, ToolNumber = ToolNumber,
                ReferenceTlo = ReferenceTlo
            };
        }

        // Runs after the base restores the saved params (first load) and each time the tab is re-shown.
        protected override void OnConfigReady()
        {
            if (model == null)
                model = DataContext as GrblViewModel;

            cbxTool.SelectedIndex = ToolNumber > 0d ? 1 : 0;   // Loaded / Prompt

            if (model != null && (CalAxes == null || CalAxes.Count == 0))
            {
                CalAxes = model.Axes.Where(a => a.Letter == "X" || a.Letter == "Y").ToList();
                if (CalAxes.Count > 0)
                    Axis = CalAxes[0].Index;
                getAxisDetails(Axis);
            }
        }

        #endregion
    }

    // Persisted stepper-calibration (scratch) parameters. Public for XmlSerializer.
    public class ScratchParams
    {
        // 0.5, not the original 0.3 (2026-09-14): 0.3 assumes a reference surface flatter than an unsurfaced
        // spoilboard actually is, and a pair with one mark missing cannot be measured at all. Depth does not
        // affect the result - see the field's own tooltip - so erring deep costs nothing. Only affects NEW
        // profiles; an existing one keeps whatever it has saved.
        // Reference the LOADED bit at the puck before cutting - see the wizard's ReferenceTlo property.
        public bool ReferenceTlo = true;
        // Depth of the SECOND mark of each pair. 0 = never set by this profile, in which case ApplyConfig
        // falls back to ScratchDepth so an existing setup keeps cutting both marks at the depth it had.
        // 0 is safe as the "unset" sentinel here in a way it usually is not: a zero-depth mark is not a
        // mark, so nobody sets it deliberately.
        public double ScratchDepth2 = 0d;
        public double Span = 400d, Delta = 0.010d, Points = 3d, ScratchDepth = 0.5d, PlungeFeed = 100d,
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
