/*
 * CalibrationView.xaml.cs - part of CNC Controls library
 *
 * The three machine-calibration wizards as a top-level view: stepper calibration by probing a
 * reference block, stepper calibration by scratching V-bit lines, and gantry squareness.
 *
 * These were Machine Setup's step 8 until 2026-09-13. The move is not cosmetic. All three are
 * Generate-first tools - since a4baf61f none owns a Generate button, they register
 * MacroProcessor.ActiveGenerate/ActiveRun while active and the SHARED Run bar (JobControl, docked in
 * MainWindow) is the only button that drives them. Machine Setup defaults to a File-menu entry, and a
 * menu-hosted view opens in a ViewHostWindow - a separate top-level window - so the wizard sat in one
 * window and its only Generate button in another, behind it, where clicking it deactivated the popup
 * being worked in. As its own view this can live in the tab bar with the Run bar directly beneath it,
 * and TabDescriptor.RequiresRunStrip makes the Tools-menu entry open it as a tab rather than a window.
 *
 */

using System.Windows;
using System.Windows.Controls;
using System.Linq;
using CNC.Core;

namespace CNC.Controls
{
    public partial class CalibrationView : UserControl, ICNCView
    {
        // True only between Activate(true)/Activate(false). Guards Calibration_SelectionChanged from
        // activating a child during the first layout pass, before this view has ever been shown -
        // SelectionChanged fires while the TabControl is still being laid out, ahead of Loaded, and a child
        // activating then would register itself with MacroProcessor's shared Generate-mode statics while a
        // DIFFERENT view still owns them. Same guard MachineSetupWizard used for the nested version.
        private bool viewActive = false;

        public CalibrationView()
        {
            InitializeComponent();
        }

        #region Methods required by ICNCView

        public ViewType ViewType { get { return ViewType.Calibration; } }

        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            viewActive = activate;

            if (activate)
            {
                // Re-checked on every entry rather than kept in sync from the probe editor. The nested
                // version was refreshed from ProbeAdd/Edit/Delete in Machine Setup and got the ordering
                // wrong twice; asking the question at the moment the answer is needed cannot go stale.
                UpdateProbeAvailability();
                ActivateSelectedChild(true);
            }
            else
                ActivateSelectedChild(false);
        }

        public void CloseFile() { }

        public void Setup(UIViewModel model, AppConfig profile) { }

        #endregion

        // Stepper calibration (probe) needs a 3D probe OR a touch plate. It was 3D-only until 2026-09-13, on
        // my reasoning that a touch plate cannot feel an unwired block - which is wrong, and was disproven on
        // real hardware through Setup's own Measure option: the plate is handheld, so the program simply
        // pauses at safe Z to let it be moved to each corner. A tool setter still does not qualify (Z only,
        // fixed position, nothing to bring to a corner). The scratch wizard beside it needs no probe at all,
        // which is why it exists - so there is always a way to calibrate steps/mm.
        private void UpdateProbeAvailability()
        {
            bool canProbe = ProbeDefinitions.Items.Any(p => p.ProbeType == ProbeType.ThreeDProbe
                                                         || p.ProbeType == ProbeType.TouchPlate);
            tabCalStepper.IsEnabled = canProbe;

            // Never leave the selection sitting on a tab that has just been disabled - a disabled TabItem
            // keeps its selection and shows its (dead) content, which reads as the app having hung.
            if (!canProbe && tabCalibration.SelectedItem == tabCalStepper)
                tabCalibration.SelectedItem = tabCalScratch;
        }

        private void ActivateSelectedChild(bool activate)
        {
            var tab = tabCalibration?.SelectedItem as TabItem;
            if (tab == tabCalStepper)
                calStepperWizard.Activate(activate);
            else if (tab == tabCalScratch)
                calScratchWizard.Activate(activate);
            else if (tab == tabCalSquareness)
                calSquarenessWizard.Activate(activate);
        }

        // Switching wizards: deactivate the outgoing one, activate the incoming one. Activating is deferred
        // to Background priority so the incoming tab's content is realized (and its ConfigPanel Loaded hook
        // has run) before it is asked to register itself - the order the nested version settled on.
        private void Calibration_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != tabCalibration || !viewActive)
                return;

            if (e.RemovedItems.Count == 1)
            {
                var removed = e.RemovedItems[0] as TabItem;
                if (removed == tabCalStepper)
                    calStepperWizard.Activate(false);
                else if (removed == tabCalScratch)
                    calScratchWizard.Activate(false);
                else if (removed == tabCalSquareness)
                    calSquarenessWizard.Activate(false);
            }

            Dispatcher.BeginInvoke((System.Action)(() => ActivateSelectedChild(true)),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
