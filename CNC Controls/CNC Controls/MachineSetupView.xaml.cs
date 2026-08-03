/*
 * MachineSetupView.xaml.cs - part of CNC Controls library
 *
 * Top-level "Machine Setup" tab. Phase 2 of docs/Architecture-Settings-Nav-Overhaul.md: the wizard's
 * step tab strip (and the tab strip nested inside its Calibration step) is replaced by the same
 * searchable navigation tree the Settings tab uses.
 *
 * The wizard is NOT dismantled to do this - it stays one control, is the content of every page, and
 * keeps all of its x:Names and selection hooks. This view builds the tree from the wizard's own
 * GetPages() and drives it via ShowPage(); the wizard's Steps_SelectionChanged /
 * Calibration_SelectionChanged still fire underneath exactly as before.
 */

using System.Linq;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MachineSetupView : UserControl, ICNCView, ITabBindingHost
    {
        private readonly MachineSetupWizard wizard = new MachineSetupWizard();
        private bool built;

        public MachineSetupView()
        {
            InitializeComponent();
            nav.SelectedNodeChanged += (s, e) => wizard.ShowPage(e.To?.Key);

            // The wizard is created here rather than declared in XAML, so it no longer inherits this
            // view's DataContext through the visual tree until it is parented by the selected page.
            // Bind it instead of assigning once, so a later DataContext change still reaches it.
            wizard.SetBinding(DataContextProperty, new System.Windows.Data.Binding("DataContext") { Source = this });
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.MachineSetup; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                BuildTree();
            }

            // Activate the wizard BEFORE refreshing the tree: it recomputes step grading and the
            // simulator step's visibility, which is what the nodes' status dots and capability gates read.
            wizard.Activate(activate);

            if (activate)
            {
                RefreshNodes();
                nav.EnsureSelection();
                wizard.ShowPage(nav.SelectedNode?.Key);
            }
        }

        public void CloseFile() { }

        public void Setup(UIViewModel model, AppConfig profile) { }

        #endregion

        private void BuildTree()
        {
            if (built)
                return;
            built = true;

            foreach (var page in wizard.GetPages())
            {
                var node = new SettingsNavNode(page.Key, page.Label, page.Content)
                {
                    Owner = wizard,
                    AvailabilityCheck = page.IsAvailable,
                    StatusCheck = page.Status
                };

                if (string.IsNullOrEmpty(page.Parent))
                    nav.Nodes.Add(node);
                else
                {
                    // A page that declares a parent goes under it - Calibration's two sub-wizards. The
                    // parent stops being selectable in its own right (a category is a heading), which is
                    // fine here: the Calibration step's whole body WAS that nested tab strip.
                    var parent = nav.FindByKey(page.Parent);
                    if (parent != null)
                        parent.Add(node);
                    else
                        nav.Nodes.Add(node);
                }
            }

            // The wizard regrades its steps on every edit; restate the dots when it does.
            wizard.StepStatusChanged += (s, e) => RefreshNodes();
        }

        private void RefreshNodes()
        {
            foreach (var n in nav.Nodes)
                n.RefreshFromProvider();
            nav.RefreshVisibility();
        }

        // Drill into a setup step from a "Tab.MachineSetup.*" keyboard shortcut (ITabBindingHost).
        public bool SelectSubTab(string id)
        {
            return nav.SelectByKey(id);
        }

        // Land on the given setup step (1-6), used by the startup setup gate.
        public void GoToStep(int step)
        {
            BuildTree();
            wizard.GoToStep(step);
            // Mirror the wizard's own selection into the tree so the two never disagree.
            var key = wizard.SelectedStepKey();
            if (!string.IsNullOrEmpty(key))
                nav.SelectByKey(key);
        }
    }
}
