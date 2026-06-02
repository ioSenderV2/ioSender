/*
 * KeyMapEditor.xaml.cs - part of CNC Controls library for Grbl
 *
 * Modal editor for keyboard mappings: jog keys and action shortcuts (including the
 * console-toggle shortcut). Edits the live KeypressHandler; the caller persists.
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
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public partial class KeyMapEditor : Window
    {
        private readonly KeypressHandler keyboard;
        private readonly ObservableCollection<BindingRow> jogRows = new ObservableCollection<BindingRow>();
        private readonly ObservableCollection<BindingRow> actionRows = new ObservableCollection<BindingRow>();
        private BindingRow capturing = null;

        public KeyMapEditor(GrblViewModel model)
        {
            InitializeComponent();

            keyboard = model.Keyboard;

            LoadRows();

            gridJog.ItemsSource = jogRows;
            gridActions.ItemsSource = actionRows;

            PreviewKeyDown += KeyMapEditor_PreviewKeyDown;
        }

        private void LoadRows()
        {
            capturing = null;
            jogRows.Clear();
            actionRows.Clear();

            foreach (var b in keyboard.GetJogBindings())
                jogRows.Add(new BindingRow(b, "Jog " + b.AxisLabel));

            foreach (var b in keyboard.GetActionBindings().OrderBy(x => Label(x.Method), StringComparer.OrdinalIgnoreCase))
                actionRows.Add(new BindingRow(b, Label(b.Method)));

            // The console toggle is just another shortcut - surface it alongside the rest.
            var console = new KeypressHandler.KeyBinding { Method = "Console.Toggle", Context = "null" };
            ShortcutKey.TryParse(AppConfig.Settings.Base.ConsoleShortcut, out console.Key, out console.Modifiers);
            actionRows.Add(new BindingRow(console, "Toggle console window") { IsConsole = true });

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
            keyboard.ApplyJogBindings(jogRows.Select(r => r.Model));
            keyboard.ApplyActionBindings(actionRows.Where(r => !r.IsConsole).Select(r => r.Model));

            var console = actionRows.FirstOrDefault(r => r.IsConsole);
            if (console != null)
                AppConfig.Settings.Base.ConsoleShortcut = console.Model.Key == Key.None
                    ? string.Empty
                    : ShortcutKey.ToStorageString(console.Model.Key, console.Model.Modifiers);

            DialogResult = true;
        }

        private void UpdateConflicts()
        {
            var dups = actionRows.Concat(jogRows)
                .Where(r => r.Model.Key != Key.None)
                .GroupBy(r => ShortcutKey.ToDisplayString(r.Model.Key, r.Model.Modifiers))
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            lblConflict.Text = dups.Count == 0 ? string.Empty : "Duplicate: " + string.Join(", ", dups);
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
            { "RenderControl.ResetView", "3D view: reset" },
            { "RenderControl.RestoreView", "3D view: restore" },
            { "RenderControl.ToggleGrid", "3D view: toggle grid" },
            { "RenderControl.ToggleJobEnvelope", "3D view: toggle job envelope" },
            { "RenderControl.ToggleWorkEnvelope", "3D view: toggle work envelope" },
            { "ProbingView.StartProbe", "Probing: start" },
            { "ProbingView.StopProbe", "Probing: stop" },
            { "ProbingView.ProbeConnectedToggle", "Probing: toggle probe connected" },
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
