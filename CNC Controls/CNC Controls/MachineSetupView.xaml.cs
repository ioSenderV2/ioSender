/*
 * MachineSetupView.xaml.cs - part of CNC Controls library
 *
 * Top-level "Machine Setup" tab: a thin ICNCView host around the tabbed MachineSetupWizard (Overview +
 * one tab per setup step). The startup setup gate uses GoToStep() to land on the first incomplete step.
 */

using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MachineSetupView : UserControl, ICNCView, ITabBindingHost
    {
        public MachineSetupView()
        {
            InitializeComponent();
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.MachineSetup; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            machineSetupWizard?.Activate(activate);
        }

        public void CloseFile() { }

        public void Setup(UIViewModel model, AppConfig profile) { }

        #endregion

        // Land on the given setup step (1-6), used by the startup setup gate.
        public void GoToStep(int step)
        {
            machineSetupWizard?.GoToStep(step);
        }

        // Drill into a setup step from a "Tab.MachineSetup.*" keyboard shortcut (ITabBindingHost).
        public bool SelectSubTab(string id)
        {
            return machineSetupWizard?.SelectSubTab(id) ?? false;
        }
    }
}
