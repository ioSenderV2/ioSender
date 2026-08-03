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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace CNC.Controls
{
    public static class SettingsSearchIndex
    {
        private const int MaxNodes = 4000;     // guard against a pathological tree
        private const int MaxChars = 8000;

        public static string Harvest(object root)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            int budget = MaxNodes;
            Walk(root as DependencyObject, sb, seen, ref budget);
            return sb.ToString();
        }

        private static void Walk(DependencyObject node, StringBuilder sb, HashSet<string> seen, ref int budget)
        {
            if (node == null || budget-- <= 0 || sb.Length >= MaxChars)
                return;

            Collect(node, sb, seen);

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                var dep = child as DependencyObject;
                if (dep != null)
                    Walk(dep, sb, seen, ref budget);
                else
                    Add(child as string, sb, seen);
            }
        }

        private static void Collect(DependencyObject node, StringBuilder sb, HashSet<string> seen)
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
                Add(fe.ToolTip as string, sb, seen);

                // Items that are plain strings (combo/list choices) are legitimate search terms.
                var items = node as ItemsControl;
                if (items != null && items.ItemsSource == null)
                    foreach (var item in items.Items)
                        Add(item as string, sb, seen);
            }
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
