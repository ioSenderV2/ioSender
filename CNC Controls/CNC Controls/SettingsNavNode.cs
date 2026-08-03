/*
 * SettingsNavNode.cs - part of CNC Controls library
 *
 * One node in the Settings / Machine Setup navigation tree (see
 * docs/Architecture-Settings-Nav-Overhaul.md).
 *
 * A node is either a CATEGORY (no content, just children) or a PAGE (a single config panel shown in
 * the shell's right pane). One panel = one page = one node - deliberately: the old model crammed
 * several unrelated panels into three columns of one tab, which is exactly what stopped scaling.
 *
 * Labels are NOT a new localizable string set. Every config panel is a single GroupBox with an
 * x:Uid'd Header that LocBaml has already localized by the time the panel is constructed, so
 * LabelFrom() reads the label straight off the panel - correct in all 7 locales, no new CSV rows,
 * and it cannot drift out of sync with the panel it names.
 */

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public class SettingsNavNode : INotifyPropertyChanged
    {
        private bool isExpanded = true;
        private bool isSelected;
        private bool isVisible = true;
        private bool matchesSearch = true;

        public SettingsNavNode(string key, string label, FrameworkElement content = null)
        {
            Key = key;
            Label = label;
            Content = content;
            Children = new ObservableCollection<SettingsNavNode>();
        }

        public string Key { get; private set; }

        public string Label
        {
            get { return label; }
            set { if (label != value) { label = value; OnPropertyChanged(nameof(Label)); } }
        }
        private string label;

        // Null on a category node. Set lazily for pages whose content is built on first show
        // (the key-map / macros / main-page editors).
        public FrameworkElement Content
        {
            get { return content; }
            set { if (!ReferenceEquals(content, value)) { content = value; OnPropertyChanged(nameof(Content)); } }
        }
        private FrameworkElement content;

        public ObservableCollection<SettingsNavNode> Children { get; private set; }

        public bool IsCategory { get { return Children.Count > 0; } }

        // Capability gate: the host hides a page that does not apply (no camera fitted, the active
        // connection IS the simulator, ...).
        public bool IsVisible
        {
            get { return isVisible; }
            set { if (isVisible != value) { isVisible = value; OnPropertyChanged(nameof(IsVisible)); OnPropertyChanged(nameof(IsShown)); } }
        }

        // Search filter. Kept separate from IsVisible so clearing the search box restores the tree
        // without having to re-run every capability gate.
        public bool MatchesSearch
        {
            get { return matchesSearch; }
            set { if (matchesSearch != value) { matchesSearch = value; OnPropertyChanged(nameof(MatchesSearch)); OnPropertyChanged(nameof(IsShown)); } }
        }

        // What the tree actually binds to. A page shows when it is both applicable and matching; a
        // category shows when it is applicable and at least one child shows - so filtering out every
        // page under a heading takes the heading with it.
        public bool IsShown
        {
            get
            {
                if (!isVisible)
                    return false;
                if (Children.Count == 0)
                    return matchesSearch;
                foreach (var c in Children)
                    if (c.IsShown)
                        return true;
                return false;
            }
        }

        // IsShown is computed from children, and WPF has no way to know a child changed, so the host
        // re-announces it bottom-up after a search or a capability pass.
        public void RefreshShown()
        {
            foreach (var c in Children)
                c.RefreshShown();
            OnPropertyChanged(nameof(IsShown));
        }

        public bool IsExpanded
        {
            get { return isExpanded; }
            set { if (isExpanded != value) { isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); } }
        }

        public bool IsSelected
        {
            get { return isSelected; }
            set { if (isSelected != value) { isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        // Free-text keywords harvested from the page's rendered visual tree (phase 3). Empty for now:
        // Phase 0 searches labels only.
        public string SearchText { get; set; }

        public SettingsNavNode Add(SettingsNavNode child)
        {
            Children.Add(child);
            OnPropertyChanged(nameof(IsCategory));
            OnPropertyChanged(nameof(IsShown));
            return child;
        }

        public IEnumerable<SettingsNavNode> SelfAndDescendants()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.SelfAndDescendants())
                    yield return d;
        }

        // The label for a config panel: the Header of its outermost GroupBox, already localized.
        // Falls back to the supplied default when a panel isn't shaped that way.
        public static string LabelFrom(FrameworkElement panel, string fallback)
        {
            var gb = panel as GroupBox ?? (panel is UserControl uc ? uc.Content as GroupBox : null);
            var header = gb?.Header as string;
            return string.IsNullOrWhiteSpace(header) ? fallback : header;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
