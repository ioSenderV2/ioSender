/*
 * MainPanelRegistry.cs - part of CNC Controls library for Grbl
 *
 * Registry of items that can be assigned to the main page or to a sidebar flyout
 * (ioSender XL), via the "Edit Main Page" dialog. Items are panels (Spindle, Coolant,
 * ... - main page or flyout), coordinate-system offsets (G54 ... - flyout only) and
 * special flyouts (Macros, Machine Position - flyout only).
 *
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;

namespace CNC.Controls
{
    // Implemented by flyouts that support a "pin" (stay open when another flyout is opened).
    public interface IPinnableFlyout
    {
        string PanelName { get; }
        bool Pinned { get; set; }
        event Action<IPinnableFlyout> PinnedChanged;    // raised when the user toggles the pin
    }

    public enum PanelKind { Panel, Offset, Special }

    public class AssignableItem
    {
        public string Name { get; }                  // stored in Config.MainPanels / FlyoutItems / PinnedFlyouts
        public string Label { get; }                 // shown in the editor and flyout tab
        public PanelKind Kind { get; }
        public Func<UserControl> CreateMainPanel { get; }   // main-page control factory (Panel kind only)
        public Func<UserControl> CreateFlyoutPanel { get; } // optional alternate control when hosted in a flyout

        public AssignableItem(string name, string label, PanelKind kind, Func<UserControl> createMainPanel = null, Func<UserControl> createFlyoutPanel = null)
        {
            Name = name;
            Label = label;
            Kind = kind;
            CreateMainPanel = createMainPanel;
            CreateFlyoutPanel = createFlyoutPanel;
        }

        public bool CanBeMainPanel { get { return Kind == PanelKind.Panel; } }

        // Control to host in a sidebar flyout - the flyout-specific variant if provided, else the main one.
        public UserControl CreateFlyout() { return (CreateFlyoutPanel ?? CreateMainPanel)?.Invoke(); }
    }

    public static class MainPanelRegistry
    {
        // Set true by the host that supports a configurable main page (ioSender XL).
        public static bool LayoutEnabled = false;

        // Panels: may occupy a main-page slot or a flyout.
        public static readonly List<AssignableItem> Panels = new List<AssignableItem>
        {
            new AssignableItem("Outline", "Outline", PanelKind.Panel, () => new OutlineControl()),
            new AssignableItem("Spindle", "Spindle", PanelKind.Panel, () => new SpindleControl()),
            new AssignableItem("Coolant", "Coolant", PanelKind.Panel, () => new CoolantControl()),
            new AssignableItem("WorkParameters", "Work Parameters", PanelKind.Panel, () => {
                var c = new WorkParametersControl();
                c.SetBinding(WorkParametersControl.IsToolChangingProperty, new Binding("IsToolChanging"));
                return c;
            }),
            new AssignableItem("Feed", "Feed rate", PanelKind.Panel, () => new FeedControl()),
            new AssignableItem("Goto", "Goto", PanelKind.Panel, () => new GotoControl()),
            new AssignableItem("UIJogging", "UI Jogging", PanelKind.Panel, () => new UIJogGridControl(), () => new UIJoggingControl()),
            new AssignableItem("KeyboardJogging", "Kbd Jogging", PanelKind.Panel, () => new KbdJogGridControl(), () => new KeyboardJoggingControl()),
            new AssignableItem("DRO", "DRO (work)", PanelKind.Panel, () => new DROControl()),
            new AssignableItem("ProgramLimits", "Program limits", PanelKind.Panel, () => new LimitsControl()),
            new AssignableItem("MachinePosition", "Machine Position", PanelKind.Panel, () => new MachinePositionControl())
        };

        // Special flyout-only items (flyout instances are supplied by the host window).
        public static readonly List<AssignableItem> Specials = new List<AssignableItem>
        {
            new AssignableItem("Macros", "Macros", PanelKind.Special)
        };

        // Coordinate-system offsets (flyout only); shown via OffsetFlyout(code).
        public static readonly string[] OffsetCodes =
            { "G28", "G30", "G54", "G55", "G56", "G57", "G58", "G59", "G59.1", "G59.2", "G59.3", "G92" };

        public static List<AssignableItem> Offsets
        {
            get { return OffsetCodes.Select(c => new AssignableItem(c, c, PanelKind.Offset)).ToList(); }
        }

        // All assignable items (panels, specials, offsets).
        public static List<AssignableItem> AllItems()
        {
            var list = new List<AssignableItem>();
            list.AddRange(Panels);
            list.AddRange(Specials);
            list.AddRange(Offsets);
            // Keyboard jogging is redundant when its distance/speed are linked to UI jogging - hide it so it
            // can't be assigned, and so a previously-placed one is dropped on next layout build.
            if (AppConfig.Settings.Base != null && AppConfig.Settings.Base.Jog.LinkStepJogToUI)
                list.RemoveAll(i => i.Name == "KeyboardJogging");
            return list;
        }

        public static AssignableItem ByName(string name)
        {
            return AllItems().FirstOrDefault(i => i.Name == name);
        }
    }
}
