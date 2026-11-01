/*
 * CarveViewConfigControl.xaml.cs - part of CNC GCodeViewer
 *
 * Settings > User Interface > 3D View - the settings-page home for the carve view's options. The options
 * themselves are CarveViewOptionsControl, which the view's own View options button hosts too.
 *
 * Reaches the settings host WITHOUT the host knowing this type exists: CNC Controls cannot reference this
 * assembly (the dependency runs the other way), so placement and discovery both travel with the panel -
 * ISettingsPanelCategory says where it belongs, ISettingsPanelProvider gets it found. See
 * SettingsPanelRegistry, which exists precisely to retire the hardcoded match-by-type-name the host used
 * to carry for panels in other assemblies.
 */

using System.Collections.Generic;
using System.Windows.Controls;

namespace CNC.Controls.Viewer
{
    public partial class CarveViewConfigControl : UserControl, ISettingsPanelCategory
    {
        public CarveViewConfigControl()
        {
            InitializeComponent();

            // The live config, so the page and the dialog edit the same object rather than two copies of
            // its values. Set here rather than inherited: a settings panel is placed into the host's tree,
            // whose DataContext is the profile, not this section's slice of it.
            DataContext = AppConfig.Settings.CarveView;
        }

        public string SettingsCategory { get { return SettingsCategories.UserInterface; } }

        // After the built-in UI sections (General/Sound/Remote/Jog are added first by the host) but with
        // room left either side, since this is the only registry-contributed panel in this category.
        public int SettingsOrder { get { return 500; } }
    }

    /// <summary>
    /// Auto-discovered by <see cref="SettingsPanelRegistry"/> from the loaded assemblies, so the panel
    /// appears with no startup hook to remember and no lifecycle dependency on when a view happens to be
    /// constructed. The Camera plugin's older pattern - adding to UIViewModel.ConfigControls from its own
    /// view - works too, but only once that view exists; the 3D view's settings should be reachable
    /// whether or not the operator has opened the Job tab yet.
    /// </summary>
    public class CarveViewSettingsPanels : ISettingsPanelProvider
    {
        public IEnumerable<SettingsPanelDescriptor> GetSettingsPanels()
        {
            yield return new SettingsPanelDescriptor("ui.carveview", () => new CarveViewConfigControl(), 500);
        }
    }
}
