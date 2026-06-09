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
    public partial class KeyMapEditor : Window
    {
        private readonly KeypressHandler keyboard;
        private readonly ObservableCollection<BindingRow> rows = new ObservableCollection<BindingRow>();
        private BindingRow capturing = null;

        public KeyMapEditor(GrblViewModel model)
        {
            InitializeComponent();

            keyboard = model.Keyboard;

            LoadRows();

            PreviewKeyDown += KeyMapEditor_PreviewKeyDown;
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
            var console = new KeypressHandler.KeyBinding { Method = "Console.Toggle", Context = "null" };
            ShortcutKey.TryParse(AppConfig.Settings.Base.ConsoleShortcut, out console.Key, out console.Modifiers);
            Add(new BindingRow(console, "Toggle console window") { IsConsole = true, Description = "Show or hide the console window." });

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
            var row = (sender as Button)?.Tag as BindingRow;
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

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            LoadRows();
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

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            keyboard.ApplyJogBindings(rows.Where(r => r.IsJog).Select(r => r.Model));
            keyboard.ApplyActionBindings(rows.Where(r => !r.IsJog && !r.IsConsole).Select(r => r.Model));

            var console = rows.FirstOrDefault(r => r.IsConsole);
            if (console != null)
                AppConfig.Settings.Base.ConsoleShortcut = console.Model.Key == Key.None
                    ? string.Empty
                    : ShortcutKey.ToStorageString(console.Model.Key, console.Model.Modifiers);

            DialogResult = true;
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
            bool v;
            return !(name != null && groupExpanded.TryGetValue(name, out v)) || v;
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

        // ---- categories (outline groups) ------------------------------------------------------

        private static void Categorize(BindingRow r)
        {
            if (r.IsJog) { r.Set("Jog", 0); return; }
            if (r.IsConsole) { r.Set("Program", 9); return; }

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

        public class BindingRow : INotifyPropertyChanged
        {
            public KeypressHandler.KeyBinding Model { get; }
            public string Label { get; }
            public string Description { get; set; }
            public bool IsConsole { get; set; }
            public bool IsJog { get { return Model.IsJog; } }

            public string Category { get; private set; }
            public int CategoryOrder { get; private set; }
            public void Set(string category, int order) { Category = category; CategoryOrder = order; }

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
            }

            public void Refresh()
            {
                Notify(nameof(DisplayText));
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

    /// <summary>Maps an outline group name to its remembered expanded/collapsed state.</summary>
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
