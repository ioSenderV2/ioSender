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
                Add(new BindingRow(b, "Jog " + b.AxisLabel));

            foreach (var b in keyboard.GetActionBindings())
                Add(new BindingRow(b, Label(b.Method)));

            // The console toggle is just another shortcut - surface it alongside the rest.
            var console = new KeypressHandler.KeyBinding { Method = "Console.Toggle", Context = "null" };
            ShortcutKey.TryParse(AppConfig.Settings.Base.ConsoleShortcut, out console.Key, out console.Modifiers);
            Add(new BindingRow(console, "Toggle console window") { IsConsole = true });

            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BindingRow.Category)));
            view.SortDescriptions.Add(new SortDescription(nameof(BindingRow.CategoryOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(BindingRow.Label), ListSortDirection.Ascending));
            grid.ItemsSource = view;

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

        private void UpdateConflicts()
        {
            var dups = rows
                .Where(r => r.Model.Key != Key.None)
                .GroupBy(r => ShortcutKey.ToDisplayString(r.Model.Key, r.Model.Modifiers))
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
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
            if (r.IsConsole) { r.Set("Console", 11); return; }

            string m = r.Model.Method ?? string.Empty;

            if (m.StartsWith("DROControl.Zero")) r.Set("Zeroing", 7);
            else if (m.StartsWith("RenderControl.")) r.Set("3D view", 10);
            else if (m.StartsWith("ProbingView.")) r.Set("Probing", 9);
            else if (m.Contains("Rapids")) r.Set("Rapids override", 4);
            else if (m.Contains("FeedOverride")) r.Set("Feed override", 3);
            else if (m.Contains("SpindleOverride")) r.Set("Spindle override", 5);
            else if (m.Contains("Flood") || m.Contains("Mist") || m.Contains("Fan")) r.Set("Coolant & aux", 6);
            else if (m.Contains("FeedRate")) r.Set("Feed rate", 2);
            else if (m.Contains("StartJob") || m.Contains("StopJob") || m.Contains("Home") ||
                     m.Contains("Unlock") || m.Contains("Reset") || m.Contains("FeedHold")) r.Set("Job control", 1);
            else if (m.Contains("OptionalStop") || m.Contains("SingleBlock") || m.Contains("ProbeConnected")) r.Set("Program", 8);
            else r.Set("Other", 12);
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
            { "DROControl.ZeroAxes", "Zero all axes" },
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
}
