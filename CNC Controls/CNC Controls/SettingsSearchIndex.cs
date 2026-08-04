/*
 * SettingsSearchIndex.cs - part of CNC Controls library
 *
 * Builds the free-text index behind the settings/machine-setup search box (phase 3 of
 * docs/Architecture-Settings-Nav-Overhaul.md): the words actually written on a page, so searching
 * "backlash" finds the page that has a Backlash field rather than only pages whose NAME says backlash.
 *
 * It walks the LOGICAL tree, not the visual one. That matters twice over:
 *
 *  - No realization. Logical children exist from InitializeComponent; nothing is measured, arranged or
 *    Loaded to read them. The visual-tree approach the spec originally assumed would have needed every
 *    page realized to be indexed, and several of these pages do real work when shown (the Machine Setup
 *    steps query the controller's filesystem for macro status). Indexing must never talk to the machine.
 *  - Per-page text from shared controls. One control now backs several pages - the setup wizard backs
 *    twelve - so indexing "the page's control" would give all of them identical text. Each page hands in
 *    its own subtree instead (SettingsSubPage.IndexRoot).
 *
 * Only labelling text is collected - headers, labels, checkbox/button captions, tooltips, combo items.
 * Deliberately NOT TextBox/PasswordBox contents: those hold the user's own values (paths, IPs, an OBS
 * password), which are not search terms and have no business being copied into an in-memory index.
 */

using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace CNC.Controls
{
    public static class SettingsSearchIndex
    {
        private const int MaxNodes = 4000;     // guard against a pathological tree
        private const int MaxChars = 8000;

        // One tooltip, kept with the control that owns it. Tooltips are indexed per control rather than
        // concatenated because a tooltip hit has to be explained ("Matched tooltip: ..."), and an
        // explanation reads far better when it can name what the text belongs to - "Send comments" - than
        // when it can only quote a window of characters that starts mid-sentence.
        public sealed class Tip
        {
            public string Owner;    // null when no name is derivable; the caller then quotes text alone
            public string Text;
        }

        // Visible page text and tooltip text are kept apart so a match can say WHICH it hit. A tooltip
        // hit is the one worth explaining: the word is genuinely on the page but only appears on hover,
        // so without saying so it reads as a wrong result.
        public sealed class Harvested
        {
            public string Text = string.Empty;
            public List<Tip> Tooltips = new List<Tip>();
        }

        public static Harvested Harvest(object root)
        {
            var text = new StringBuilder();
            var tips = new List<Tip>();
            var seen = new HashSet<string>();
            int budget = MaxNodes;
            Walk(root as DependencyObject, text, tips, seen, ref budget);
            return new Harvested { Text = text.ToString(), Tooltips = tips };
        }

        private static void Walk(DependencyObject node, StringBuilder text, List<Tip> tips, HashSet<string> seen, ref int budget)
        {
            if (node == null || budget-- <= 0 || text.Length >= MaxChars)
                return;

            Collect(node, text, tips, seen);

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                var dep = child as DependencyObject;
                if (dep != null)
                    Walk(dep, text, tips, seen, ref budget);
                else
                    Add(child as string, text, seen);
            }
        }

        private static void Collect(DependencyObject node, StringBuilder sb, List<Tip> tips, HashSet<string> seen)
        {
            var tb = node as TextBlock;
            if (tb != null)
            {
                Add(tb.Text, sb, seen);
                return;
            }

            var run = node as Run;
            if (run != null)
            {
                Add(run.Text, sb, seen);
                return;
            }

            // A user's own values are not search terms - skip the editable controls entirely.
            if (node is TextBoxBase || node is PasswordBox)
                return;

            var headered = node as HeaderedContentControl;
            if (headered != null)
                Add(headered.Header as string, sb, seen);

            var headeredItems = node as HeaderedItemsControl;
            if (headeredItems != null)
                Add(headeredItems.Header as string, sb, seen);

            var content = node as ContentControl;
            if (content != null)
                Add(content.Content as string, sb, seen);

            var fe = node as FrameworkElement;
            if (fe != null)
            {
                AddTip(fe, tips, seen);

                // Items that are plain strings (combo/list choices) are legitimate search terms.
                var items = node as ItemsControl;
                if (items != null && items.ItemsSource == null)
                    foreach (var item in items.Items)
                        Add(item as string, sb, seen);
            }
        }

        private static void AddTip(FrameworkElement fe, List<Tip> tips, HashSet<string> seen)
        {
            var text = fe.ToolTip as string;
            if (string.IsNullOrWhiteSpace(text))
                return;

            text = text.Trim();
            if (text.Length > 200)
                text = text.Substring(0, 200);
            if (!seen.Add(text))
                return;

            tips.Add(new Tip { Owner = OwnerName(fe), Text = text });
        }

        // What to call the control a tooltip belongs to. Only names that the control genuinely carries -
        // there is deliberately no "nearest preceding TextBlock" guess, because a field/label pair has no
        // real link and a wrong name is worse than none. Returning null is a supported outcome: the caller
        // falls back to quoting the tooltip text on its own, exactly as before.
        private static string OwnerName(FrameworkElement fe)
        {
            // GroupBox/Expander/TabItem: the header names the group.
            var headered = fe as HeaderedContentControl;
            if (headered != null && headered.Header is string)
                return Clean(headered.Header as string);

            var headeredItems = fe as HeaderedItemsControl;
            if (headeredItems != null && headeredItems.Header is string)
                return Clean(headeredItems.Header as string);

            // CheckBox/Button/RadioButton: the caption IS the label, so this is the common good case.
            var content = fe as ContentControl;
            if (content != null && content.Content is string)
                return Clean(content.Content as string);

            // The field controls in this codebase (NumericField, CoordValueSetControl, DROBaseControl...)
            // carry their caption in a Label property rather than Content.
            var prop = fe.GetType().GetProperty("Label", typeof(string));
            if (prop != null && prop.CanRead)
                return Clean(prop.GetValue(fe, null) as string);

            return Clean(AutomationProperties.GetName(fe));
        }

        private static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            // Field captions are written with their trailing colon ("Reset delay:"); it reads as
            // punctuation noise once the name is followed by a dash and the quoted text.
            name = name.Trim().TrimEnd(':').Trim();
            return name.Length == 0 || name.Length > 60 ? null : name;
        }

        private static void Add(string text, StringBuilder sb, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(text) || sb.Length >= MaxChars)
                return;

            text = text.Trim();
            if (text.Length > 200)
                text = text.Substring(0, 200);
            if (!seen.Add(text))
                return;

            sb.Append(text).Append('\n');
        }
    }
}
