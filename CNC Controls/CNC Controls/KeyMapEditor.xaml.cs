/*
 * KeyMapEditor.xaml.cs - part of CNC Controls library for Grbl
 *
 * Modal editor for keyboard mappings: jog keys and action shortcuts (including the
 * console-toggle shortcut), presented as one list grouped (outline) by function.
 * Edits the live KeypressHandler; the caller persists.
 *
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    public partial class KeyMapEditor : UserControl, ISettingsEditorTab, ISettingsResettable
    {
        private readonly KeypressHandler keyboard;
        private readonly GrblViewModel model;
        private readonly ObservableCollection<BindingRow> rows = new ObservableCollection<BindingRow>();
        private readonly ObservableCollection<ControllerRow> controllerRows = new ObservableCollection<ControllerRow>();
        private readonly Dictionary<string, GroupRowState> groupStates = new Dictionary<string, GroupRowState>();
        private KeyMapGroupStateConverter groupStateConverter;
        private BindingRow _capturing = null;
        private BindingRow capturing { get { return _capturing; } set { _capturing = value; IsCapturing = value != null; } }

        // True while any row is waiting to capture a keypress. The main-window tab-switch dispatcher checks this
        // so an already-bound tab hotkey doesn't switch tabs (stealing the key) while you are rebinding it here.
        public static bool IsCapturing { get; private set; }
        private bool _controllerHooked = false;   // controller dispatch paused + live events hooked (while tab visible)

        /// <summary>Action choices for the Controller tab dropdowns (bound from XAML).</summary>
        public List<ActionItem> ActionItems { get; private set; }

        public KeyMapEditor(GrblViewModel model)
        {
            InitializeComponent();

            this.model = model;
            keyboard = model.Keyboard;
            DataContext = this;
            groupStateConverter = Resources["GroupState"] as KeyMapGroupStateConverter;

            LoadRows();
            LoadController();   // builds the row data only; the live dispatch-pause + controller event hooks
                                // happen in KeyMapEditor_Loaded/_Unloaded so they apply only while this tab is shown.

            PreviewKeyDown += KeyMapEditor_PreviewKeyDown;
            Loaded += KeyMapEditor_Loaded;
            Unloaded += KeyMapEditor_Unloaded;
        }

        // Save-on-leave: apply the edited bindings to the live keyboard handler + controller map, persist the
        // key mappings and app settings, and re-register the console hotkey. This is the former Ok_Click plus the
        // work the AppConfigView caller used to do after the modal returned OK. Called by the host on tab-leave.
        public void Commit()
        {
            keyboard.ApplyJogBindings(rows.Where(r => r.IsJog).Select(r => r.Model));
            keyboard.ApplyActionBindings(rows.Where(r => !r.IsJog && !r.IsConsole).Select(r => r.Model));

            var console = rows.FirstOrDefault(r => r.IsConsole);
            if (console != null)
                AppConfig.Settings.Base.ConsoleShortcut = console.Model.Key == Key.None
                    ? string.Empty
                    : ShortcutKey.ToStorageString(console.Model.Key, console.Model.Modifiers);

            // Rebuild the saved tab-switch list from the bound rows only (unbound tabs are simply absent).
            AppConfig.Settings.Base.TabShortcuts = rows
                .Where(r => r.IsTabSwitch && r.Model.Key != Key.None)
                .Select(r => new TabShortcut { Id = r.Model.Method, Key = ShortcutKey.ToStorageString(r.Model.Key, r.Model.Modifiers) })
                .ToList();

            if (model.ControllerMapper != null)
            {
                var m = model.ControllerMapper;
                foreach (var r in controllerRows)
                    m.SetAction(r.Button, r.Action);

                m.AnalogJogEnabled = chkAnalogEnabled.IsChecked == true;
                int dz;
                if (int.TryParse(txtDeadzone.Text, out dz))
                    m.DeadzonePercent = Math.Max(0, Math.Min(95, dz));
                double fs;
                if (double.TryParse(txtFeedScale.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out fs))
                    m.FeedScale = Math.Max(0.1d, Math.Min(10d, fs));
                m.InvertX = chkInvertX.IsChecked == true;
                m.InvertY = chkInvertY.IsChecked == true;
                m.InvertZ = chkInvertZ.IsChecked == true;

                m.SaveMap();
            }

            keyboard.SaveMappings();   // persists into the App.config "KeyMap" section
            AppConfig.Settings.Save();
            AppConfig.NotifyConsoleShortcutChanged();
            AppConfig.NotifyTabShortcutsChanged();
        }

        private void LoadRows()
        {
            capturing = null;
            rows.Clear();

            foreach (var b in keyboard.GetJogBindings())
                Add(new BindingRow(b, "Jog " + b.AxisLabel) { Description = JogDescription(b.AxisLabel) });

            foreach (var b in keyboard.GetActionBindings())
            {
                if (IsHidden(b.Method))
                    continue; // internal jog plumbing - jog axis keys are remapped in the Jog group
                Add(new BindingRow(b, Label(b.Method)) { Description = Description(b.Method) });
            }

            // The console toggle is just another program-level toggle - surface it alongside the rest.
            var console = new KeypressHandler.KeyBinding { Method = "Console.Toggle", Context = "null", DefaultKey = Key.Escape };
            ShortcutKey.TryParse(AppConfig.Settings.Base.ConsoleShortcut, out console.Key, out console.Modifiers);
            Add(new BindingRow(console, "Toggle console window") { IsConsole = true, Description = "Show or hide the console window." });

            // Tab-switch shortcuts. Unbound by default (DefaultKey = None); persisted in Base.TabShortcuts and
            // dispatched at the main-window level like the console toggle, so they fire regardless of focus.
            var saved = AppConfig.Settings.Base.TabShortcuts;
            foreach (var t in TabTargets)
            {
                var b = new KeypressHandler.KeyBinding { Method = t.Id, Context = "null", DefaultKey = Key.None };
                var s = saved?.FirstOrDefault(x => x.Id == t.Id);
                if (s != null)
                    ShortcutKey.TryParse(s.Key, out b.Key, out b.Modifiers);
                Add(new BindingRow(b, t.Label) { IsTabSwitch = true, Description = t.Description });
            }

            BuildGroupStates();

            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BindingRow.Category)));
            view.SortDescriptions.Add(new SortDescription(nameof(BindingRow.CategoryOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(BindingRow.Label), ListSortDirection.Ascending));
            grid.ItemsSource = view;

            UpdateJogPresetRadios();
            UpdateConflicts();
        }

        private void Add(BindingRow row)
        {
            Categorize(row);
            rows.Add(row);
        }

        // Pull each tab-switch row's key back from Config.TabShortcuts (the store the tab right-click menu writes)
        // and refresh any that changed, so the editor stays in step with out-of-editor binds. Cheap; no rebuild.
        private void SyncTabRows()
        {
            var saved = AppConfig.Settings.Base.TabShortcuts;
            foreach (var row in rows.Where(r => r.IsTabSwitch))
            {
                Key k = Key.None;
                ModifierKeys m = ModifierKeys.None;
                var s = saved?.FirstOrDefault(x => x.Id == row.Model.Method);
                if (s != null)
                    ShortcutKey.TryParse(s.Key, out k, out m);

                if (row.Model.Key != k || row.Model.Modifiers != m)
                {
                    row.Model.Key = k;
                    row.Model.Modifiers = m;
                    row.Refresh();
                }
            }
            UpdateConflicts();
        }

        private static readonly HashSet<Key> modifierKeys = new HashSet<Key>
        {
            Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift,
            Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin, Key.System, Key.None
        };

        private void KeyMapEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (capturing == null)
                return;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (modifierKeys.Contains(key))
                return; // wait for a non-modifier key

            e.Handled = true; // consume so IsDefault/IsCancel buttons don't fire while capturing

            capturing.Model.Key = key;
            capturing.Model.Modifiers = capturing.IsJog ? ModifierKeys.None : Keyboard.Modifiers;
            capturing.Capturing = false;
            capturing.Refresh();   // update changed-state + group indicator
            capturing = null;

            UpdateConflicts();
        }

        private void Binding_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as BindingRow;
            if (row == null)
                return;

            if (row.Capturing)
            {
                row.Capturing = false;
                capturing = null;
                return;
            }

            if (capturing != null)
                capturing.Capturing = false;

            capturing = row;
            row.Capturing = true;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var row = (fe?.Tag as BindingRow) ?? (fe?.DataContext as BindingRow);   // button Tag or menu-item DataContext
            if (row == null)
                return;

            if (capturing == row)
                capturing = null;

            row.Capturing = false;
            row.Model.Key = Key.None;
            row.Model.Modifiers = ModifierKeys.None;
            row.Refresh();

            UpdateConflicts();
        }

        // Reset every keyboard binding (and controller button) to its factory default. Invoked by the shared
        // Settings footer's "Reset to Default" (this tab has no in-tab reset button of its own).
        public void ResetToDefaults()
        {
            foreach (var row in rows)
                row.ResetToDefault();

            if (model.ControllerMapper != null)
                foreach (var r in controllerRows)
                    r.Action = ControllerMapper.DefaultAction(r.Button);

            UpdateJogPresetRadios();
            UpdateConflicts();
        }

        private void BuildGroupStates()
        {
            groupStates.Clear();
            foreach (var row in rows)
            {
                GroupRowState gs;
                if (!groupStates.TryGetValue(row.Category, out gs))
                {
                    gs = new GroupRowState(row.Category, GroupDescription(row.Category));
                    groupStates[row.Category] = gs;
                }
                gs.Rows.Add(row);
                row.Group = gs;
            }
            foreach (var gs in groupStates.Values)
                gs.Recompute();

            if (groupStateConverter != null)
                groupStateConverter.Lookup = groupStates;
        }

        // Reset a single binding to its factory default (row context menu).
        private void ResetRow_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as BindingRow;
            if (row == null)
                return;
            row.ResetToDefault();
            UpdateConflicts();
        }

        // Reset every binding in a group to its factory default (group-header context menu).
        private void ResetGroup_Click(object sender, RoutedEventArgs e)
        {
            var gs = (sender as FrameworkElement)?.DataContext as GroupRowState;
            if (gs == null)
                return;
            foreach (var row in gs.Rows)
                row.ResetToDefault();
            UpdateConflicts();
        }

        private bool suppressPreset = false;

        // Reflect the current jog-preset bindings in the radio buttons without re-applying them.
        private void UpdateJogPresetRadios()
        {
            if (rbNumpad == null || rbTopRow == null)
                return;

            var row = rows.FirstOrDefault(r => r.Model.Method == "JogBaseControl.JogStep0");
            Key k = row == null ? Key.None : row.Model.Key;

            suppressPreset = true;
            rbNumpad.IsChecked = k >= Key.NumPad0 && k <= Key.NumPad9;
            rbTopRow.IsChecked = k >= Key.D1 && k <= Key.D9;
            suppressPreset = false;
        }

        private void JogPreset_Checked(object sender, RoutedEventArgs e)
        {
            if (suppressPreset)
                return;

            SetJogPreset(rbTopRow.IsChecked == true);
            UpdateConflicts();
        }

        private void SetJogPreset(bool topRow)
        {
            foreach (var row in rows)
            {
                Key key;
                ModifierKeys mods;
                if (JogPresetKey(row.Model.Method, topRow, out key, out mods))
                {
                    row.Model.Key = key;
                    row.Model.Modifiers = mods;
                    row.Refresh();
                }
            }
        }

        // The NumPad-bound jog actions and their top-row equivalents (for keyboards without a keypad).
        private static bool JogPresetKey(string method, bool topRow, out Key key, out ModifierKeys mods)
        {
            mods = ModifierKeys.None;
            key = Key.None;

            switch (method)
            {
                case "JogBaseControl.JogStep0": key = topRow ? Key.D1 : Key.NumPad0; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogStep1": key = topRow ? Key.D2 : Key.NumPad1; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogStep2": key = topRow ? Key.D3 : Key.NumPad2; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogStep3": key = topRow ? Key.D4 : Key.NumPad3; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogFeed0": key = topRow ? Key.D5 : Key.NumPad4; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogFeed1": key = topRow ? Key.D6 : Key.NumPad5; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogFeed2": key = topRow ? Key.D7 : Key.NumPad6; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.JogFeed3": key = topRow ? Key.D8 : Key.NumPad7; mods = ModifierKeys.Control; return true;
                case "JogBaseControl.StepDec": key = topRow ? Key.OemOpenBrackets : Key.NumPad4; return true;
                case "JogBaseControl.StepInc": key = topRow ? Key.OemCloseBrackets : Key.NumPad6; return true;
                case "JogBaseControl.FeedDec": key = topRow ? Key.OemMinus : Key.NumPad2; return true;
                case "JogBaseControl.FeedInc": key = topRow ? Key.OemPlus : Key.NumPad8; return true;
                default: return false;
            }
        }

        // ---- Controller tab ------------------------------------------------------------------

        // Build the controller-tab row data. The live parts (pausing machine dispatch, hooking connect/poll
        // events) are deferred to KeyMapEditor_Loaded so they engage only while this tab is actually visible -
        // otherwise merely having the settings view open would freeze controller dispatch.
        private void LoadController()
        {
            ActionItems = BuildActionItems();

            if (model.Controller == null || model.ControllerMapper == null)
            {
                lblController.Text = "Controller support is not available.";
                return;
            }

            model.ControllerMapper.EnsureLoaded();   // show the saved map, not just defaults

            foreach (var b in ControllerMapper.MappableButtons)
            {
                var def = ControllerMapper.DefaultAction(b);
                var choices = ActionItems.Select(a => a.Action == def ? new ActionItem(a.Action, "* " + a.Label, a.Description) : a).ToList();
                controllerRows.Add(new ControllerRow(b, ButtonName(b), model.ControllerMapper.GetAction(b), choices));
            }

            gridController.ItemsSource = controllerRows;

            foreach (var r in controllerRows)
                r.PropertyChanged += ControllerRow_Changed;
            UpdateRestoreDefaultsButton();

            // Analog jog settings
            var m = model.ControllerMapper;
            chkAnalogEnabled.IsChecked = m.AnalogJogEnabled;
            txtDeadzone.Text = m.DeadzonePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            txtFeedScale.Text = m.FeedScale.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            chkInvertX.IsChecked = m.InvertX;
            chkInvertY.IsChecked = m.InvertY;
            chkInvertZ.IsChecked = m.InvertZ;

            UpdateControllerStatus();
        }

        // Tab shown: pause machine dispatch (so testing controller buttons here can't move the machine) and hook
        // live controller status/poll events. Paired with _Unloaded; guarded so re-entry doesn't double-hook.
        private void KeyMapEditor_Loaded(object sender, RoutedEventArgs e)
        {
            // Re-sync every time the tab is shown so bindings made via a tab's right-click "Bind to Key" (which
            // write straight to Config.TabShortcuts) are reflected here, even though this editor is built once.
            SyncTabRows();

            if (_controllerHooked || model.Controller == null || model.ControllerMapper == null)
                return;

            _controllerHooked = true;
            model.ControllerMapper.Enabled = false;
            UpdateControllerStatus();
            model.Controller.Connected += Controller_StatusChanged;
            model.Controller.Disconnected += Controller_StatusChanged;
            model.Controller.Polled += Controller_Polled;
        }

        // Tab hidden / view left: unhook and resume machine dispatch.
        private void KeyMapEditor_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_controllerHooked)
                return;

            _controllerHooked = false;
            if (model.Controller != null)
            {
                model.Controller.Connected -= Controller_StatusChanged;
                model.Controller.Disconnected -= Controller_StatusChanged;
                model.Controller.Polled -= Controller_Polled;
            }
            if (model.ControllerMapper != null)
                model.ControllerMapper.Enabled = true;   // resume machine dispatch
        }

        private void UpdateControllerStatus()
        {
            lblController.Text = model.Controller != null && model.Controller.IsConnected
                ? "Controller connected (slot " + model.Controller.ControllerIndex + ")."
                : "No controller detected.";
        }

        private void Controller_StatusChanged(object sender, EventArgs e)
        {
            UpdateControllerStatus();
        }

        // Drive the press indicators straight from the raw XInput state every poll - the definitive
        // check of what the controller actually reports (D-pad lights here = input is getting through).
        private void Controller_Polled(object sender, EventArgs e)
        {
            ushort buttons = model.Controller.State.wButtons;
            foreach (var r in controllerRows)
                r.Pressed = (buttons & (ushort)r.Button) != 0;
        }

        private void ControllerDefaults_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in controllerRows)
                r.Action = ControllerMapper.DefaultAction(r.Button);
        }

        private void ControllerRow_Changed(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ControllerRow.Action))
                UpdateRestoreDefaultsButton();
        }

        private void UpdateRestoreDefaultsButton()
        {
            if (btnRestoreDefaults != null)
                btnRestoreDefaults.IsEnabled = controllerRows.Any(r => r.Action != ControllerMapper.DefaultAction(r.Button));
        }

        private static List<ActionItem> BuildActionItems()
        {
            const string jogNote = " by the distance and feed rate currently selected in the Jog panel.";

            // Use the conventional mill directions (X+ right, Y+ away, Z+ up) - same as the on-screen jog
            // arrows. In lathe mode the convention differs, so fall back to plain axis +/-.
            bool lathe = GrblInfo.LatheModeEnabled;
            string xp = lathe ? "" : " (right)", xm = lathe ? "" : " (left)";
            string yp = lathe ? "" : " (away)", ym = lathe ? "" : " (toward you)";
            string zp = lathe ? "" : " (up)", zm = lathe ? "" : " (down)";

            return new List<ActionItem>
            {
                new ActionItem(ControllerAction.None, "(none)", "No action assigned to this button."),
                new ActionItem(ControllerAction.CycleStart, "Cycle Start / Resume", "Start the loaded program, or resume after a feed hold."),
                new ActionItem(ControllerAction.FeedHold, "Feed Hold", "Pause motion (feed hold)."),
                new ActionItem(ControllerAction.Reset, "Reset (soft-reset)", "Soft-reset the controller (Ctrl-X)."),
                new ActionItem(ControllerAction.Unlock, "Unlock", "Clear an alarm / unlock the controller ($X)."),
                new ActionItem(ControllerAction.Home, "Home", "Run the homing cycle ($H)."),
                new ActionItem(ControllerAction.SpindleStop, "Spindle stop", "Stop the spindle (during a feed hold)."),
                new ActionItem(ControllerAction.JogXPlus, "Jog X +", "Jog the X axis +" + xp + jogNote),
                new ActionItem(ControllerAction.JogXMinus, "Jog X −", "Jog the X axis −" + xm + jogNote),
                new ActionItem(ControllerAction.JogYPlus, "Jog Y +", "Jog the Y axis +" + yp + jogNote),
                new ActionItem(ControllerAction.JogYMinus, "Jog Y −", "Jog the Y axis −" + ym + jogNote),
                new ActionItem(ControllerAction.JogZPlus, "Jog Z +", "Jog the Z axis +" + zp + jogNote),
                new ActionItem(ControllerAction.JogZMinus, "Jog Z −", "Jog the Z axis −" + zm + jogNote),
                new ActionItem(ControllerAction.JogStepIncrease, "Jog speed +", "Select the next-faster UI jog speed preset (2×4 grid)."),
                new ActionItem(ControllerAction.JogStepDecrease, "Jog speed −", "Select the next-slower UI jog speed preset (2×4 grid)."),
                new ActionItem(ControllerAction.JogDistanceIncrease, "Jog distance +", "Select the next-larger UI jog distance preset (2×4 grid)."),
                new ActionItem(ControllerAction.JogDistanceDecrease, "Jog distance −", "Select the next-smaller UI jog distance preset (2×4 grid).")
            };
        }

        private static string ButtonName(XInputButton b)
        {
            switch (b)
            {
                case XInputButton.DPadUp: return "D-pad ↑";
                case XInputButton.DPadDown: return "D-pad ↓";
                case XInputButton.DPadLeft: return "D-pad ←";
                case XInputButton.DPadRight: return "D-pad →";
                case XInputButton.LeftShoulder: return "Left bumper (LB)";
                case XInputButton.RightShoulder: return "Right bumper (RB)";
                case XInputButton.Back: return "Back / View";
                case XInputButton.Start: return "Start / Menu";
                case XInputButton.LeftThumb: return "Left stick click (L3)";
                case XInputButton.RightThumb: return "Right stick click (R3)";
                default: return b.ToString();
            }
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetExpanded(true);
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetExpanded(false);
        }

        private void SetExpanded(bool expanded)
        {
            foreach (var ex in FindVisualChildren<Expander>(grid))
                ex.IsExpanded = expanded;
        }

        // Remembers which outline groups are collapsed, so the dialog looks the same next time
        // it is opened within this session. Default (unseen group) is expanded.
        private static readonly Dictionary<string, bool> groupExpanded = new Dictionary<string, bool>();

        public static bool IsGroupExpanded(string name)
        {
            // Default collapsed (like the Load Folder outline); remembered once toggled this session.
            bool v;
            return name != null && groupExpanded.TryGetValue(name, out v) && v;
        }

        private void Group_Expanded(object sender, RoutedEventArgs e)
        {
            RecordGroup(sender, true);
        }

        private void Group_Collapsed(object sender, RoutedEventArgs e)
        {
            RecordGroup(sender, false);
        }

        private static void RecordGroup(object sender, bool expanded)
        {
            var name = ((sender as FrameworkElement)?.DataContext as CollectionViewGroup)?.Name as string;
            if (name != null)
                groupExpanded[name] = expanded;
        }

        private static bool IsHidden(string method)
        {
            if (string.IsNullOrEmpty(method))
                return false;
            // Directional jog handlers and jog cancel are internal plumbing for the Jog group keys.
            return method.Contains("CursorJog") || method.Contains("KeyJog") || method.Contains("EndJog");
        }

        private void UpdateConflicts()
        {
            // Same key+modifiers only conflicts within the same control context - actions bound to
            // the same key in different contexts (e.g. Start job vs Start probe on Alt+R) are fine.
            var dups = rows
                .Where(r => r.Model.Key != Key.None)
                .GroupBy(r => ShortcutKey.ToDisplayString(r.Model.Key, r.Model.Modifiers) + "\0" + (r.Model.Context ?? "null"))
                .Where(g => g.Count() > 1)
                .Select(g => ShortcutKey.ToDisplayString(g.First().Model.Key, g.First().Model.Modifiers))
                .Distinct()
                .ToList();

            lblConflict.Text = dups.Count == 0 ? string.Empty : "Duplicate: " + string.Join(", ", dups);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                yield break;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t)
                    yield return t;
                foreach (var d in FindVisualChildren<T>(child))
                    yield return d;
            }
        }

        // ---- tab-switch targets ---------------------------------------------------------------

        /// <summary>A main-page tab or Settings sub-tab that can be bound to a key.</summary>
        public class TabTarget
        {
            public readonly string Id;          // stable stored/dispatch identity - NEVER rename (saved bindings key on it)
            public readonly string Label;
            public readonly string Description;
            public TabTarget(string id, string label, string description) { Id = id; Label = label; Description = description; }
        }

        // The bindable tabs, in display order within their group. Id is "Tab.<Name>" for a main-page tab and
        // "Tab.Settings.<Name>" for a Settings sub-tab. This is the source of truth for the editor rows; the
        // matching id -> tab dispatch lives in MainWindow.RegisterTabShortcuts / MainWindow_PreviewKeyDown.
        public static readonly TabTarget[] TabTargets = new[]
        {
            new TabTarget("Tab.Settings",     "Settings tab",      "Switch to the Settings tab."),
            new TabTarget("Tab.StartJob",     "Start Job tab",     "Switch to the Start Job tab."),
            new TabTarget("Tab.Job",          "Job tab",           "Switch to the Job tab."),
            new TabTarget("Tab.Offsets",      "Offsets tab",       "Switch to the Offsets tab."),
            new TabTarget("Tab.SDCard",       "SD Card tab",       "Switch to the SD Card tab."),
            new TabTarget("Tab.Probing",      "Probing tab",       "Switch to the Probing tab."),
            new TabTarget("Tab.Tools",        "Tools tab",         "Switch to the Tools tab."),
            new TabTarget("Tab.MachineSetup", "Machine Setup tab", "Switch to the Machine Setup tab."),
            new TabTarget("Tab.HeightMap",    "Height Map tab",    "Switch to the Height Map tab."),
            new TabTarget("Tab.LatheWizard",  "Lathe Tools tab",   "Switch to the Lathe Tools tab."),

            new TabTarget("Tab.Settings.Grbl",     "Settings → Grbl",                  "Switch to Settings and show the Grbl sub-tab."),
            new TabTarget("Tab.Settings.App",      "Settings → App",                   "Switch to Settings and show the App sub-tab."),
            new TabTarget("Tab.Settings.Jogging",  "Settings → Jogging",               "Switch to Settings and show the Jogging sub-tab."),
            new TabTarget("Tab.Settings.GCode",    "Settings → G Code",                "Switch to Settings and show the G Code sub-tab."),
            new TabTarget("Tab.Settings.Keyboard", "Settings → Keyboard & Controller", "Switch to Settings and show the Keyboard & Controller sub-tab."),
            new TabTarget("Tab.Settings.Macros",   "Settings → Macros",                "Switch to Settings and show the Macros sub-tab."),
            new TabTarget("Tab.Settings.MainPage", "Settings → Main Page",             "Switch to Settings and show the Main Page sub-tab."),

            new TabTarget("Tab.MachineSetup.Overview", "Machine Setup → Overview",           "Switch to Machine Setup and show the Overview step."),
            new TabTarget("Tab.MachineSetup.Machine",  "Machine Setup → Machine",            "Switch to Machine Setup and show the Machine step."),
            new TabTarget("Tab.MachineSetup.Home",     "Machine Setup → Home position",      "Switch to Machine Setup and show the Home position step."),
            new TabTarget("Tab.MachineSetup.Axis",     "Machine Setup → Axis information",   "Switch to Machine Setup and show the Axis information step."),
            new TabTarget("Tab.MachineSetup.Homing",   "Machine Setup → Homing & limits",    "Switch to Machine Setup and show the Homing & limits step."),
            new TabTarget("Tab.MachineSetup.Probes",   "Machine Setup → Probe definitions",  "Switch to Machine Setup and show the Probe definitions step."),
            new TabTarget("Tab.MachineSetup.Macros",   "Machine Setup → Controller macros",  "Switch to Machine Setup and show the Controller macros step."),

            new TabTarget("Tab.Probing.ToolOffset",   "Probing → Tool length offset",    "Switch to Probing and show the Tool length offset tab."),
            new TabTarget("Tab.Probing.EdgeExternal", "Probing → Edge finder, external", "Switch to Probing and show the external Edge finder tab."),
            new TabTarget("Tab.Probing.EdgeInternal", "Probing → Edge finder, internal", "Switch to Probing and show the internal Edge finder tab."),
            new TabTarget("Tab.Probing.Center",       "Probing → Center finder",         "Switch to Probing and show the Center finder tab."),

            new TabTarget("Tab.Tools.ToolTable",         "Tools → Tool table",                   "Switch to Tools and show the Tool table tab."),
            new TabTarget("Tab.Tools.StepperCal",        "Tools → Stepper calibration",          "Switch to Tools and show the Stepper calibration tab."),
            new TabTarget("Tab.Tools.StepperScratch",    "Tools → Stepper calibration (scratch)", "Switch to Tools and show the scratch Stepper calibration tab."),
            new TabTarget("Tab.Tools.SurfaceSpoilboard", "Tools → Surface spoilboard",           "Switch to Tools and show the Surface spoilboard tab."),
            new TabTarget("Tab.Tools.Squareness",        "Tools → Squareness",                   "Switch to Tools and show the Squareness tab."),
            new TabTarget("Tab.Tools.Trinamic",          "Tools → Trinamic tuner",               "Switch to Tools and show the Trinamic tuner tab."),
            new TabTarget("Tab.Tools.PID",               "Tools → PID Tuner",                    "Switch to Tools and show the PID Tuner tab."),

            new TabTarget("Tab.LatheWizard.Turning",   "Lathe Tools → Turning",   "Switch to Lathe Tools and show the Turning tab."),
            new TabTarget("Tab.LatheWizard.Parting",   "Lathe Tools → Parting",   "Switch to Lathe Tools and show the Parting tab."),
            new TabTarget("Tab.LatheWizard.Facing",    "Lathe Tools → Facing",    "Switch to Lathe Tools and show the Facing tab."),
            new TabTarget("Tab.LatheWizard.Threading", "Lathe Tools → Threading", "Switch to Lathe Tools and show the Threading tab."),
        };

        // ---- categories (outline groups) ------------------------------------------------------

        private static void Categorize(BindingRow r)
        {
            if (r.IsJog) { r.Set("Jog", 0); return; }
            if (r.IsConsole) { r.Set("Program", 9); return; }
            if (r.IsTabSwitch)
            {
                // "Tab.<Name>" is a main-page tab; "Tab.<Parent>.<Sub>" is a second-level tab grouped by parent.
                string[] parts = (r.Model.Method ?? string.Empty).Split('.');
                if (parts.Length < 3) { r.Set("Main Page tabs", 13); return; }
                switch (parts[1])
                {
                    case "Settings": r.Set("Settings tabs", 14); break;
                    case "MachineSetup": r.Set("Machine Setup tabs", 15); break;
                    case "Probing": r.Set("Probing tabs", 16); break;
                    case "Tools": r.Set("Tools tabs", 17); break;
                    case "LatheWizard": r.Set("Lathe Tools tabs", 18); break;
                    default: r.Set("Settings tabs", 14); break;
                }
                return;
            }

            string m = r.Model.Method ?? string.Empty;

            if (m.StartsWith("DROControl.Zero")) r.Set("Zeroing", 8);
            else if (m.StartsWith("RenderControl.")) r.Set("3D view", 11);
            else if (m.StartsWith("ProbingView.")) r.Set("Probing", 10);
            else if (m.Contains("JogStep") || m.Contains("JogFeed") ||
                     m.EndsWith("FeedInc") || m.EndsWith("FeedDec") ||
                     m.EndsWith("StepInc") || m.EndsWith("StepDec")) r.Set("Jog speed & step", 1);
            else if (m.Contains("Rapids")) r.Set("Rapids override", 5);
            else if (m.Contains("FeedOverride")) r.Set("Feed override", 4);
            else if (m.Contains("SpindleOverride")) r.Set("Spindle override", 6);
            else if (m.Contains("Flood") || m.Contains("Mist") || m.Contains("Fan")) r.Set("Coolant & aux", 7);
            else if (m.Contains("FeedRate")) r.Set("Feed rate", 3);
            else if (m.Contains("StartJob") || m.Contains("StopJob") || m.Contains("Home") ||
                     m.Contains("Unlock") || m.Contains("Reset") || m.Contains("FeedHold")) r.Set("Job control", 2);
            else if (m.Contains("OptionalStop") || m.Contains("SingleBlock") || m.Contains("ProbeConnected")) r.Set("Program", 9);
            else r.Set("Other", 12);
        }

        // ---- descriptions (tooltips) ----------------------------------------------------------

        private static readonly Dictionary<string, string> groupDescriptions = new Dictionary<string, string>
        {
            { "Jog", "Keys that jog each axis. Hold Ctrl for a single step, Shift for fast jog." },
            { "Jog speed & step", "Select or nudge the jog step size and jog feed rate." },
            { "Job control", "Run, stop and recover the current job." },
            { "Feed rate", "Nudge the programmed feed rate up or down." },
            { "Feed override", "Real-time feed rate override, as a percentage of the programmed feed." },
            { "Rapids override", "Real-time override of rapid (G0) traverse speed." },
            { "Spindle override", "Real-time override of spindle speed." },
            { "Coolant & aux", "Toggle coolant outputs and the auxiliary fan." },
            { "Zeroing", "Set the work-coordinate zero for an axis (or all axes)." },
            { "Program", "Program-level toggles (optional stop, single block, probe state) and the console window." },
            { "Probing", "Start or stop probing and toggle the probe-connected state." },
            { "3D view", "Control the 3D tool-path viewer." },
            { "Main Page tabs", "Jump straight to a main-page tab from anywhere in the app." },
            { "Settings tabs", "Jump to the Settings tab and show a specific sub-tab." },
            { "Machine Setup tabs", "Jump to Machine Setup and show a specific step." },
            { "Probing tabs", "Jump to Probing and show a specific probing tab." },
            { "Tools tabs", "Jump to Tools and show a specific tool tab." },
            { "Lathe Tools tabs", "Jump to Lathe Tools and show a specific wizard tab." },
            { "Other", "Additional actions." }
        };

        public static string GroupDescription(string name)
        {
            string d;
            return name != null && groupDescriptions.TryGetValue(name, out d) ? d : null;
        }

        private static readonly Dictionary<string, string> descriptions = new Dictionary<string, string>
        {
            { "JobControl.StartJob", "Start the loaded job, or resume after a feed hold." },
            { "JobControl.StopJob", "Stop the running job." },
            { "JobControl.Home", "Run the homing cycle." },
            { "JobControl.Unlock", "Clear an alarm / unlock the controller ($X)." },
            { "JobControl.Reset", "Soft-reset the controller (Ctrl-X)." },
            { "JobControl.ResetAndUnlock", "Soft-reset the controller, then clear the alarm ($X) once it restarts." },
            { "JobControl.FeedHold", "Pause motion (feed hold)." },
            { "JobControl.FeedRateUp", "Increase the programmed feed rate." },
            { "JobControl.FeedRateDown", "Decrease the programmed feed rate." },
            { "JobControl.FeedRateUpFine", "Increase the feed rate in a small step." },
            { "JobControl.FeedRateDownFine", "Decrease the feed rate in a small step." },
            { "DROControl.ZeroX", "Set the X work position to zero." },
            { "DROControl.ZeroY", "Set the Y work position to zero." },
            { "DROControl.ZeroZ", "Set the Z work position to zero." },
            { "DROControl.ZeroA", "Set the A work position to zero." },
            { "DROControl.ZeroB", "Set the B work position to zero." },
            { "DROControl.ZeroC", "Set the C work position to zero." },
            { "DROControl.ZeroU", "Set the U work position to zero." },
            { "DROControl.ZeroV", "Set the V work position to zero." },
            { "DROControl.ZeroW", "Set the W work position to zero." },
            { "DROControl.ZeroAxes", "Set all work positions to zero." },
            { "JogBaseControl.JogStep0", "Select jog step size preset 1." },
            { "JogBaseControl.JogStep1", "Select jog step size preset 2." },
            { "JogBaseControl.JogStep2", "Select jog step size preset 3." },
            { "JogBaseControl.JogStep3", "Select jog step size preset 4." },
            { "JogBaseControl.JogFeed0", "Select jog feed rate preset 1." },
            { "JogBaseControl.JogFeed1", "Select jog feed rate preset 2." },
            { "JogBaseControl.JogFeed2", "Select jog feed rate preset 3." },
            { "JogBaseControl.JogFeed3", "Select jog feed rate preset 4." },
            { "JogBaseControl.FeedInc", "Increase the jog feed rate." },
            { "JogBaseControl.FeedDec", "Decrease the jog feed rate." },
            { "JogBaseControl.StepInc", "Increase the jog step size." },
            { "JogBaseControl.StepDec", "Decrease the jog step size." },
            { "RenderControl.ResetView", "Reset the 3D view to the default orientation." },
            { "RenderControl.RestoreView", "Restore the last saved 3D view." },
            { "RenderControl.ToggleGrid", "Show or hide the grid in the 3D view." },
            { "RenderControl.ToggleJobEnvelope", "Show or hide the job bounding box." },
            { "RenderControl.ToggleWorkEnvelope", "Show or hide the work envelope." },
            { "ProbingView.StartProbe", "Start the selected probing routine." },
            { "ProbingView.StopProbe", "Stop the running probing routine." },
            { "ProbingView.ProbeConnectedToggle", "Toggle the simulated probe-connected state." },
            { "KeypressHandler.FeedOverrideFinePlus", "Increase feed override by 1%." },
            { "KeypressHandler.FeedOverrideFineMinus", "Decrease feed override by 1%." },
            { "KeypressHandler.FeedOverrideCoarsePlus", "Increase feed override by 10%." },
            { "KeypressHandler.FeedOverrideCoarseMinus", "Decrease feed override by 10%." },
            { "KeypressHandler.FeedOverrideReset", "Reset feed override to 100%." },
            { "KeypressHandler.FeedOverrideRapidsMedium", "Set rapid override to the medium step." },
            { "KeypressHandler.FeedOverrideRapidsLow", "Set rapid override to the low step." },
            { "KeypressHandler.FeedOverrideRapidsReset", "Reset rapid override to 100%." },
            { "KeypressHandler.FloodOverrideToggle", "Toggle flood coolant (M7/M9)." },
            { "KeypressHandler.MistOverrideToggle", "Toggle mist coolant (M8/M9)." },
            { "KeypressHandler.Fan0Toggle", "Toggle the auxiliary fan output." },
            { "KeypressHandler.SpindleOverrideFinePlus", "Increase spindle override by 1%." },
            { "KeypressHandler.SpindleOverrideFineMinus", "Decrease spindle override by 1%." },
            { "KeypressHandler.SpindleOverrideCoarsePlus", "Increase spindle override by 10%." },
            { "KeypressHandler.SpindleOverrideCoarseMinus", "Decrease spindle override by 10%." },
            { "KeypressHandler.SpindleOverrideStop", "Stop the spindle while in feed hold." },
            { "KeypressHandler.ProbeConnectedToggle", "Toggle the simulated probe-connected state." },
            { "KeypressHandler.OptionalStopToggle", "Toggle optional stop (M1) handling." },
            { "KeypressHandler.SingleBlockToggle", "Toggle single-block (step one line at a time) mode." }
        };

        private static string Description(string method)
        {
            string d;
            if (method != null && descriptions.TryGetValue(method, out d))
                return d;
            return null;
        }

        private static string JogDescription(string axisLabel)
        {
            if (string.IsNullOrEmpty(axisLabel))
                return null;

            string letter = axisLabel.Substring(0, 1);
            string dir = axisLabel.Contains("+") ? "positive" : "negative";
            return string.Format("Jog the {0} axis in the {1} direction while the key is held.", letter, dir);
        }

        // ---- friendly labels ------------------------------------------------------------------

        private static readonly Dictionary<string, string> labels = new Dictionary<string, string>
        {
            { "JobControl.StartJob", "Start / resume job" },
            { "JobControl.StopJob", "Stop job" },
            { "JobControl.Home", "Home" },
            { "JobControl.Unlock", "Unlock (clear alarm)" },
            { "JobControl.Reset", "Reset (soft-reset)" },
            { "JobControl.ResetAndUnlock", "Reset and unlock" },
            { "JobControl.FeedHold", "Feed hold" },
            { "JobControl.FeedRateUp", "Feed rate +" },
            { "JobControl.FeedRateDown", "Feed rate −" },
            { "JobControl.FeedRateUpFine", "Feed rate + (fine)" },
            { "JobControl.FeedRateDownFine", "Feed rate − (fine)" },
            { "DROControl.ZeroX", "Zero X" },
            { "DROControl.ZeroY", "Zero Y" },
            { "DROControl.ZeroZ", "Zero Z" },
            { "DROControl.ZeroA", "Zero A" },
            { "DROControl.ZeroB", "Zero B" },
            { "DROControl.ZeroC", "Zero C" },
            { "DROControl.ZeroU", "Zero U" },
            { "DROControl.ZeroV", "Zero V" },
            { "DROControl.ZeroW", "Zero W" },
            { "DROControl.ZeroAxes", "Zero all axes" },
            { "JogBaseControl.JogStep0", "Jog step size 1" },
            { "JogBaseControl.JogStep1", "Jog step size 2" },
            { "JogBaseControl.JogStep2", "Jog step size 3" },
            { "JogBaseControl.JogStep3", "Jog step size 4" },
            { "JogBaseControl.JogFeed0", "Jog feed rate 1" },
            { "JogBaseControl.JogFeed1", "Jog feed rate 2" },
            { "JogBaseControl.JogFeed2", "Jog feed rate 3" },
            { "JogBaseControl.JogFeed3", "Jog feed rate 4" },
            { "JogBaseControl.FeedInc", "Jog feed +" },
            { "JogBaseControl.FeedDec", "Jog feed −" },
            { "JogBaseControl.StepInc", "Jog step +" },
            { "JogBaseControl.StepDec", "Jog step −" },
            { "RenderControl.ResetView", "Reset view" },
            { "RenderControl.RestoreView", "Restore view" },
            { "RenderControl.ToggleGrid", "Toggle grid" },
            { "RenderControl.ToggleJobEnvelope", "Toggle job envelope" },
            { "RenderControl.ToggleWorkEnvelope", "Toggle work envelope" },
            { "ProbingView.StartProbe", "Start probe" },
            { "ProbingView.StopProbe", "Stop probe" },
            { "ProbingView.ProbeConnectedToggle", "Toggle probe connected" },
            { "KeypressHandler.FeedOverrideFinePlus", "Feed override +1%" },
            { "KeypressHandler.FeedOverrideFineMinus", "Feed override −1%" },
            { "KeypressHandler.FeedOverrideCoarsePlus", "Feed override +10%" },
            { "KeypressHandler.FeedOverrideCoarseMinus", "Feed override −10%" },
            { "KeypressHandler.FeedOverrideReset", "Feed override reset" },
            { "KeypressHandler.FeedOverrideRapidsMedium", "Rapids override: medium" },
            { "KeypressHandler.FeedOverrideRapidsLow", "Rapids override: low" },
            { "KeypressHandler.FeedOverrideRapidsReset", "Rapids override: reset" },
            { "KeypressHandler.FloodOverrideToggle", "Toggle flood coolant" },
            { "KeypressHandler.MistOverrideToggle", "Toggle mist coolant" },
            { "KeypressHandler.Fan0Toggle", "Toggle fan" },
            { "KeypressHandler.SpindleOverrideFinePlus", "Spindle override +1%" },
            { "KeypressHandler.SpindleOverrideFineMinus", "Spindle override −1%" },
            { "KeypressHandler.SpindleOverrideCoarsePlus", "Spindle override +10%" },
            { "KeypressHandler.SpindleOverrideCoarseMinus", "Spindle override −10%" },
            { "KeypressHandler.SpindleOverrideStop", "Spindle stop (during hold)" },
            { "KeypressHandler.ProbeConnectedToggle", "Toggle probe connected" },
            { "KeypressHandler.OptionalStopToggle", "Toggle optional stop (M1)" },
            { "KeypressHandler.SingleBlockToggle", "Toggle single block" }
        };

        private static string Label(string method)
        {
            string l;
            if (method != null && labels.TryGetValue(method, out l))
                return l;
            return Prettify(method);
        }

        private static string Prettify(string method)
        {
            if (string.IsNullOrEmpty(method))
                return "(action)";

            int dot = method.IndexOf('.');
            string name = dot >= 0 ? method.Substring(dot + 1) : method;

            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                    sb.Append(' ');
                sb.Append(c);
            }

            return sb.ToString();
        }

        public class ActionItem
        {
            public ControllerAction Action { get; }
            public string Label { get; }
            public string Description { get; }
            public ActionItem(ControllerAction action, string label, string description = null)
            {
                Action = action;
                Label = label;
                Description = description;
            }
        }

        public class ControllerRow : INotifyPropertyChanged
        {
            public XInputButton Button { get; }
            public string ButtonName { get; }
            public List<ActionItem> Choices { get; }   // per-button: the default action is marked with '*'

            private ControllerAction action;
            public ControllerAction Action
            {
                get { return action; }
                set { action = value; Notify(nameof(Action)); Notify(nameof(Description)); }
            }

            /// <summary>Description of the currently-selected action (for the row tooltip).</summary>
            public string Description
            {
                get
                {
                    var c = Choices == null ? null : Choices.FirstOrDefault(x => x.Action == action);
                    return c == null ? null : c.Description;
                }
            }

            private bool pressed;
            public bool Pressed
            {
                get { return pressed; }
                set { if (pressed != value) { pressed = value; Notify(nameof(Pressed)); } }
            }

            public ControllerRow(XInputButton button, string buttonName, ControllerAction action, List<ActionItem> choices)
            {
                Button = button;
                ButtonName = buttonName;
                this.action = action;
                Choices = choices;
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Notify(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public class BindingRow : INotifyPropertyChanged
        {
            public KeypressHandler.KeyBinding Model { get; }
            public string Label { get; }
            public string Description { get; set; }
            public bool IsConsole { get; set; }
            public bool IsTabSwitch { get; set; }
            public bool IsJog { get { return Model.IsJog; } }

            public string Category { get; private set; }
            public int CategoryOrder { get; private set; }
            public void Set(string category, int order) { Category = category; CategoryOrder = order; }

            public GroupRowState Group;   // owning outline group (for header change indication)

            // Value when the editor was first opened this session (the persisted / app-startup binding). Used
            // for the "modified" highlight, so a saved custom binding doesn't read as modified on every launch.
            public readonly Key StartupKey;
            public readonly ModifierKeys StartupModifiers;

            /// <summary>True when the binding differs from its factory default (drives Reset-to-default).</summary>
            public bool IsChanged
            {
                get { return Model.Key != Model.DefaultKey || Model.Modifiers != Model.DefaultModifiers; }
            }

            /// <summary>True when the binding differs from its value at first open (drives the highlight).</summary>
            public bool IsModified
            {
                // Tab-switch rows have no factory key (default = unbound), so any binding is a customization worth
                // flagging - and it must highlight the same whether it was set here or via right-click on the tab
                // (where StartupKey already captured the bound value, so the session-baseline test would miss it).
                // Keyed actions keep the session-baseline test so a saved custom binding doesn't light up on open.
                get { return IsTabSwitch ? IsChanged : (Model.Key != StartupKey || Model.Modifiers != StartupModifiers); }
            }

            /// <summary>True when a key is assigned (so Clear is meaningful).</summary>
            public bool IsBound { get { return Model.Key != Key.None; } }

            public void ResetToDefault()
            {
                Model.Key = Model.DefaultKey;
                Model.Modifiers = Model.DefaultModifiers;
                Refresh();
            }

            private bool capturing;
            public bool Capturing
            {
                get { return capturing; }
                set { capturing = value; Notify(nameof(DisplayText)); }
            }

            public string DisplayText
            {
                get
                {
                    if (capturing)
                        return "Press keys…";
                    return Model.Key == Key.None ? "—" : ShortcutKey.ToDisplayString(Model.Key, Model.Modifiers);
                }
            }

            public BindingRow(KeypressHandler.KeyBinding model, string label)
            {
                Model = model;
                Label = label;
                StartupKey = model.Key;
                StartupModifiers = model.Modifiers;
            }

            public void Refresh()
            {
                Notify(nameof(DisplayText));
                Notify(nameof(IsChanged));
                Notify(nameof(IsModified));
                Notify(nameof(IsBound));
                if (Group != null)
                    Group.Recompute();
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Notify(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        /// <summary>Per-outline-group state so the group header can flag (and reset) modified bindings.</summary>
        public class GroupRowState : INotifyPropertyChanged
        {
            public string Name { get; }
            public string Description { get; }
            public int Count { get { return Rows.Count; } }
            public List<BindingRow> Rows { get; } = new List<BindingRow>();

            private bool modified;
            // Any row differs from its factory default (enables "Reset group to defaults").
            public bool Modified
            {
                get { return modified; }
                private set { if (modified != value) { modified = value; Notify(nameof(Modified)); } }
            }

            private bool hasModified;
            // Any row differs from its value at first open (drives the group-header highlight).
            public bool HasModified
            {
                get { return hasModified; }
                private set { if (hasModified != value) { hasModified = value; Notify(nameof(HasModified)); } }
            }

            public GroupRowState(string name, string description)
            {
                Name = name;
                Description = description;
            }

            public void Recompute()
            {
                Modified = Rows.Any(r => r.IsChanged);
                HasModified = Rows.Any(r => r.IsModified);
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Notify(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    /// <summary>Maps an outline group name to its description for the group-header tooltip.</summary>
    public class KeyMapGroupTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return KeyMapEditor.GroupDescription(value as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Maps an outline group name to its live GroupRowState (for header change-indication / reset).</summary>
    public class KeyMapGroupStateConverter : IValueConverter
    {
        public Dictionary<string, KeyMapEditor.GroupRowState> Lookup;

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            KeyMapEditor.GroupRowState gs;
            return Lookup != null && value is string && Lookup.TryGetValue((string)value, out gs) ? gs : null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class KeyMapGroupExpandedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return KeyMapEditor.IsGroupExpanded(value as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
