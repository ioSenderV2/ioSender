/*
 * ProbingView.xaml.cs - part of CNC Probing library
 *
 * v0.46 / 2025-06-05 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2025, Io Engineering (Terje Io)
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

using System.Windows;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Threading.Tasks;
using CNC.Core;
using CNC.Controls;
using CNC.GCode;

namespace CNC.Controls.Probing
{

    /// <summary>
    /// Interaction logic for ProbingView.xaml
    /// </summary>
    public partial class ProbingView : UserControl, ICNCView, ITabBindingHost, IAvailabilityGated
    {
        // Probing needs a probe AND probe-coordinate reporting ($10); without either it can do nothing, so the
        // whole tab is removed (Height Map stays - it can still load/apply a saved .map offline).
        public string UnavailableReason => !GrblInfo.HasProbe
            ? "No probe is configured."
            : !GrblSettings.ReportProbeCoordinates ? "Probe coordinate reporting is off ($10 - enable it to use probing)." : null;
        public bool HideWhenUnavailable => true;

        private static bool keyboardMappingsOk = false;

        private bool probeTriggered = false, probeDisconnected = false, cycleStartSignal = false, wasMetric = true;
        private ProbingViewModel model = null;
        private GrblViewModel grbl = null;
        private IInputElement focusedControl = null;
        private CollectionViewSource probeView = null;   // per-tab filtered probe-definition list

        public ProbingView()
        {
            InitializeComponent();

            TabKeyBinder.AttachTabBinding(tabToolOffset, "Tab.Probing.ToolOffset");
            TabKeyBinder.AttachTabBinding(tabEdgeExternal, "Tab.Probing.EdgeExternal");
            TabKeyBinder.AttachTabBinding(tabEdgeInternal, "Tab.Probing.EdgeInternal");
            TabKeyBinder.AttachTabBinding(tabCenter, "Tab.Probing.Center");
        }

        // Drill into a probing sub-tab from a "Tab.Probing.*" keyboard shortcut (ITabBindingHost). Returns false
        // (no change) when the sub-tab is not present.
        public bool SelectSubTab(string id)
        {
            TabItem target;
            switch (id)
            {
                case "Tab.Probing.ToolOffset": target = tabToolOffset; break;
                case "Tab.Probing.EdgeExternal": target = tabEdgeExternal; break;
                case "Tab.Probing.EdgeInternal": target = tabEdgeInternal; break;
                case "Tab.Probing.Center": target = tabCenter; break;
                default: target = null; break;
            }

            if (target == null || !tab.Items.Contains(target))
                return false;

            tab.SelectedItem = target;
            return true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureInitialized();
        }

        // Per-INSTANCE setup, split out of Loaded for two reasons, both of which crashed this view once it
        // became a menu-hosted window rather than a tab (2026-08-04, NullReferenceException at
        // tab_SelectionChanged):
        //
        //  - Timing: the inner TabControl selects its first tab while the host Window is being measured for
        //    the first time (Window.Show -> MeasureOverride -> GenerateChildren -> SelectionChanged), which
        //    happens BEFORE Loaded fires. So the handler ran with a null model. It now calls this first.
        //  - Lifetime: all of this used to sit behind the STATIC keyboardMappingsOk flag. As a tab there was
        //    only ever one instance, so nobody noticed; a menu entry builds a fresh view on every open, and
        //    the second one would have skipped model creation entirely and crashed the same way. Only the
        //    keyboard registration - which really is app-wide-once - stays behind that flag.
        //
        // Idempotent per instance (model != null is the guard) and a no-op until the DataContext is the
        // GrblViewModel it needs.
        private void EnsureInitialized()
        {
            if (model != null || !(DataContext is GrblViewModel))
                return;

            grbl = (DataContext as GrblViewModel);

            if (!keyboardMappingsOk)
            {
                KeypressHandler keyboard = grbl.Keyboard;

                keyboardMappingsOk = true;

                keyboard.AddHandler(Key.R, ModifierKeys.Alt, StartProbe, this);
                keyboard.AddHandler(Key.S, ModifierKeys.Alt, StopProbe, this);
                keyboard.AddHandler(Key.C, ModifierKeys.Alt, ProbeConnectedToggle, this);
            }

            // Probing parameters come from the shared probe library (Settings: App > Edit Probe
            // Definitions). Seed sensible defaults if it's empty so probing always has parameters.
            if (ProbeDefinitions.Items.Count == 0)
            {
                ProbeDefinitions.Items.Add(new ProbeDefinition { ProbeType = ProbeType.ThreeDProbe });
                ProbeDefinitions.Items.Add(new ProbeDefinition { ProbeType = ProbeType.ToolSetter });
                ProbeDefinitions.Renumber();
                ProbeDefinitions.Save();
            }

            DataContext = model = new ProbingViewModel(grbl);

            // The dropdown lists probe definitions, filtered per tab by type: the tool setter only on
            // Tool length; the workpiece-probing tabs (edge/centre/rotation) show 3D probe / edge finder
            // / touch plate. Refreshed on tab change.
            probeView = new CollectionViewSource { Source = model.ProbeDefs };
            probeView.Filter += ProbeView_Filter;
            cbxProbe.ItemsSource = probeView.View;

            grbl.OnCameraProbe += addCameraPosition;

            // The selection that fired before this ran got skipped, so apply it now that there IS a model -
            // otherwise the tab you are looking at is never told it is the active one.
            if (tab.SelectedItem is TabItem selected)
                ApplyTabSelection(selected, null);
        }

        private static bool ProbeValidForTab(ProbeDefinition p, ProbingType type)
        {
            // Tool length uses a tool setter; the workpiece-probing tabs use a 3D probe / edge finder / touch plate.
            return type == ProbingType.ToolLength ? p.ProbeType == ProbeType.ToolSetter : p.ProbeType != ProbeType.ToolSetter;
        }

        private void ProbeView_Filter(object sender, FilterEventArgs e)
        {
            e.Accepted = !(e.Item is ProbeDefinition p) || model == null || ProbeValidForTab(p, model.ProbingType);
        }

        // Tell the controller which physical probe input to use for the selected definition (tool setter -> 1).
        private void SelectControllerProbe(ProbeDefinition p)
        {
            if (model == null || p == null)
                return;
            int id = p.ProbeType == ProbeType.ToolSetter ? 1 : 0;
            if (model.Grbl.Probe != id)
                model.Grbl.ExecuteCommand(string.Format(GrblCommand.ProbeSelect, id));
        }

        // If the selected definition is not valid for the current tab, pick the first valid one.
        private void EnsureValidProbe()
        {
            if (model == null)
                return;

            if (model.SelectedProbe == null || !ProbeValidForTab(model.SelectedProbe, model.ProbingType))
                model.SelectedProbe = ProbeDefinitions.Items.FirstOrDefault(p => ProbeValidForTab(p, model.ProbingType));

            SelectControllerProbe(model.SelectedProbe);
        }

        private void addCameraPosition(Position position)
        {
            if (grbl.IsProbing)
            {
                if(model.CameraPositions == 0)
                {
                    model.PreviewText = string.Empty;
                    model.PreviewEnable = true;
                }

                model.Positions.Add(position);
                var positions = model.CameraPositions = model.Positions.Count;

                if(positions == model.CameraPositions) // model.CameraPositions may have been changed elsewhere!
                    model.PreviewText += (model.PreviewText == string.Empty ? string.Empty : "\n") + string.Format((string)FindResource("CameraPosition"), model.CameraPositions, position.X.ToInvariantString(), position.Y.ToInvariantString());
            }
        }

        private static IProbeTab getView(TabItem tab)
        {
            IProbeTab view = null;

            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(tab))
            {
                if (uc is IProbeTab)
                {
                    view = (IProbeTab)uc;
                    break;
                }
            }

            return view;
        }

        private bool StopProbe(Key key)
        {
            getView(tab.SelectedItem as TabItem)?.Stop();

            return true;
        }

        private bool StartProbe(Key key)
        {
            if (!grbl.IsJobRunning)
            {
                focusedControl = Keyboard.FocusedElement;
                getView(tab.SelectedItem as TabItem)?.Start(model.PreviewEnable);
            }

            return true;
        }

        private bool ProbeConnectedToggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_PROBE_CONNECTED_TOGGLE);
            return true;
        }

        private bool FnKeyHandler(Key key)
        {
            if (!model.Grbl.IsJobRunning)
            {
                int id = int.Parse(key.ToString().Substring(1));
                var macro = AppConfig.Settings.Macros.FirstOrDefault(o => o.Id == id);
                if (macro != null && AppDialogs.Show(string.Format((string)FindResource("RunMacro"), macro.Name), "Run macro", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    MacroProcessor.Run(model.Grbl, macro.Name, macro.Code);
                    return true;
                }
            }
            return false;
        }

        private void DisplayPosition(GrblViewModel grbl)
        {
            Position position = new Position(grbl.Position, grbl.UnitFactor);
            model.Position = string.Format("X:{0}  Y:{1}  Z:{2} {3} {4}",
                                            position.X.ToInvariantString(grbl.Format),
                                             position.Y.ToInvariantString(grbl.Format),
                                              position.Z.ToInvariantString(grbl.Format),
                                               probeTriggered ? "P" : "",
                                                probeDisconnected ? "D" : "");
        }

        private void Grbl_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var grbl = sender as GrblViewModel;

            switch (e.PropertyName) {

                case nameof(GrblViewModel.IsJobRunning):
                    foreach (TabItem tabitem in tab.Items)
                        tabitem.IsEnabled = !grbl.IsJobRunning || tabitem == tab.SelectedItem;
                    if (!grbl.IsJobRunning && focusedControl != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
                        {
                            focusedControl.Focus();
                            focusedControl = null;
                        }), DispatcherPriority.Render);
                    }
                    break;

                case nameof(GrblViewModel.Position):
                    DisplayPosition(grbl);
                    break;

                case nameof(GrblViewModel.Signals):
                    probeTriggered = grbl.Signals.Value.HasFlag(Signals.Probe);
                    probeDisconnected = grbl.Signals.Value.HasFlag(Signals.ProbeDisconnected);
                    DisplayPosition(grbl);
                    var signals = ((GrblViewModel)sender).Signals.Value;
                    if (!grbl.IsJobRunning && signals.HasFlag(Signals.CycleStart) && !signals.HasFlag(Signals.Hold) && !cycleStartSignal)
                        StartProbe(Key.R);
                    cycleStartSignal = signals.HasFlag(Signals.CycleStart);
                    break;
            }
        }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.Probing; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? !(DataContext as GrblViewModel).IsGCLock : !model.Grbl.IsGCLock; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if ((grbl.IsProbing = activate))
            {
                if (model.CoordinateSystems.Count == 0)
                {
                    //                   model.CoordinateSystems.Add(model.CoordinateSystem = new CoordinateSystem("Active", "0"));
                    foreach (var cs in GrblWorkParameters.CoordinateSystems)
                    {
                        if (cs.Id > 0 && cs.Id < 9)
                            model.CoordinateSystems.Add(new CoordinateSystem(cs.Code, "0"));

                        if (cs.Id == 9)
                            model.HasCoordinateSystem9 = true;
                    }
                    model.HasToolTable = GrblInfo.NumTools > 0;
                }

                if (GrblInfo.IsGrblHAL)
                    Comms.com.WriteByte(GrblConstants.CMD_STATUS_REPORT_ALL);

                if (GrblInfo.IsGrblHAL)
                {
                    GrblParserState.Get();
                    GrblWorkParameters.Get();
                }
                else
                    GrblParserState.Get(true);

                if (!(wasMetric = GrblParserState.IsMetric))
                    model.WaitForResponse("G21");

                model.ProbeVerified = !AppConfig.Settings.Probing.ValidateProbeConnected;
                model.DistanceMode = GrblParserState.DistanceMode;
                model.Tool = model.Grbl.Tool == GrblConstants.NO_TOOL ? "0" : model.Grbl.Tool;
                model.CanProbe = !model.Grbl.Signals.Value.HasFlag(Signals.Probe);
                model.HeightMapApplied = GCode.File.HeightMapApplied;
                int csid = GrblWorkParameters.GetCoordinateSystem(model.Grbl.WorkCoordinateSystem).Id;
                model.CoordinateSystem = csid == 0 || csid >= 9 ? 1 : csid;
                model.ReferenceToolOffset &= model.CanReferenceToolOffset;

                if (model.Grbl.IsTloReferenceSet && !double.IsNaN(model.Grbl.TloReference))
                {
                    model.TloReference = model.Grbl.TloReference;
                    model.ReferenceToolOffset = false;
                }

                Probing.Command = GrblInfo.ReportProbeResult ? "G38.3" : "G38.2";

                getView(tab.SelectedItem as TabItem)?.Activate(true);

                model.Grbl.PropertyChanged += Grbl_PropertyChanged;
                model.Grbl.IgnoreNextCycleStart = true;

                probeTriggered = model.Grbl.Signals.Value.HasFlag(Signals.Probe);
                probeDisconnected = model.Grbl.Signals.Value.HasFlag(Signals.ProbeDisconnected);
                cycleStartSignal = model.Grbl.Signals.Value.HasFlag(Signals.CycleStart);

                DisplayPosition(model.Grbl);
            }
            else
            {
                model.Grbl.PropertyChanged -= Grbl_PropertyChanged;
                getView(tab.SelectedItem as TabItem)?.Activate(false);

                if (!model.Grbl.IsGCLock)
                {
                    // If probing alarm active unlock
                    //if(model.Grbl.GrblState.State == GrblStates.Alarm && (model.Grbl.GrblState.Substate == 4 || model.Grbl.GrblState.Substate == 5))
                    //    model.WaitForResponse(GrblConstants.CMD_UNLOCK);
                    //else
                    if (model.Grbl.GrblError != 0)
                        model.WaitForResponse("");  // Clear error

                    if (!wasMetric)
                        model.WaitForResponse("G20");

                    model.WaitForResponse(model.DistanceMode == DistanceMode.Absolute ? "G90" : "G91");
                }
            }

            model.Message = string.Empty;
            model.Grbl.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        public void CloseFile()
        {
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
            if (!model.IsConfigControlInstantiated<ConfigControl>())
                model.ConfigControls.Add(new ConfigControl());
        }

        #endregion

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!(e.Handled = ProcessKeyPreview(e)))
                base.OnPreviewKeyDown(e);
        }
        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (!(e.Handled = ProcessKeyPreview(e)))
                base.OnPreviewKeyDown(e);
        }
        protected bool ProcessKeyPreview(KeyEventArgs e)
        {
            if (grbl.Keyboard == null)
                return false;

            // Keyboard jogging is always available here; ProcessKeypress only jogs when focus is not in a
            // text box (so typing a value never jogs) - so no "activate jogging" gate/button is needed.
            return grbl.Keyboard.ProcessKeypress(e, true, this);
        }

        private void tab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Equals(e.OriginalSource, sender))
            {
                // Fires during the host window's first measure pass, before Loaded - see EnsureInitialized.
                EnsureInitialized();

                if (e.AddedItems.Count == 1 && model != null)
                    ApplyTabSelection(e.AddedItems[0] as TabItem,
                                      e.RemovedItems.Count == 1 ? e.RemovedItems[0] as TabItem : null);
                e.Handled = true;
            }
        }

        // Point the view model and the probe list at the newly selected tab, and hand activation over.
        // Called from tab_SelectionChanged and, for the selection that landed before there was a model,
        // from EnsureInitialized. Requires model != null - both callers check.
        private void ApplyTabSelection(TabItem added, TabItem removed)
        {
            var view = getView(added);
            if (view == null)
                return;

            model.Positions.Clear();
            if (!model.AllowMeasure && model.CoordinateMode == ProbingViewModel.CoordMode.Measure)
                model.CoordinateMode = ProbingViewModel.CoordMode.G10;
            model.AllowMeasure = false;
            model.ProbingType = view.ProbingType;
            probeView?.View.Refresh();   // re-filter the probe list for the new tab
            EnsureValidProbe();
            model.Message = string.Empty;
            model.PreviewEnable = false;

            if (GrblInfo.IsGrblHAL)
                Comms.com.WriteByte(GrblConstants.CMD_STATUS_REPORT_ALL);

            if (removed != null)
                getView(removed)?.Activate(false);

            view.Activate(true);
        }

        private void cbxProbe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectedItem is bound to model.SelectedProbe (copies the definition's params into the VM);
            // here we just point the controller at the matching physical probe input.
            if (e.AddedItems.Count == 1 && e.AddedItems[0] is ProbeDefinition p)
                SelectControllerProbe(p);
        }

    }
}
