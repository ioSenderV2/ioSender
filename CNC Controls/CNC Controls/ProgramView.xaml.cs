/*
 * ProgramView.xaml.cs - a standalone, streamer-connectable program view.
 *
 * Part of the ProgramView refactor (docs/Architecture-ProgramView-Refactor.md): replaces the single shared
 * program overlay with a reusable object. Each Load File / wizard Generate creates its own
 * instance; instances exist independently. The streamer is allocated to a view by an explicit Connect/Disconnect
 * push/pop stack - the connected view (stack top) is what Cycle Start runs.
 *
 * STEP 1 (this file): the object + the connect stack only. Nothing is wired to it yet - the streamer routing,
 * the main-window overlay migration, and the per-tool conversions come in later steps.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public partial class ProgramView : UserControl
    {
        public ProgramView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => HookModel(e.NewValue as GrblViewModel);
            UpdateTitleHint();
            titleBar.ToolTip = DefaultTitleTooltip;
        }

        // The program this view owns and renders. Set by the producer (Load/Generate); the same block objects
        // are what the streamer runs when this view is connected, so per-line markers are live (never a copy).
        public ObservableCollection<GCodeBlock> Blocks { get; private set; }

        // The ProgramView representing the loaded job (set by MainWindow when it creates jobProgramView) - the
        // ONLY instance Stock/DeclaredStock are valid on. Every other ProgramView (wizard/Start Job output, a
        // macro run's transient view) is generated content, not "the loaded job" - there is no such thing as
        // "the stock" for a probe/wizard program, so those throw rather than silently returning a value nobody
        // asked for. Not a cached singleton itself - just an instance pointer; the values below are still
        // computed fresh on every access, from this instance's own Blocks (or GCode.File.Data when Blocks is
        // null - the loaded-job convention, SetProgram(null)), never cached.
        public static ProgramView LoadedJob { get; set; }
        public bool IsLoadedJob { get; set; }

        private IEnumerable<string> ProgramLines()
        {
            return Blocks != null
                ? Blocks.Select(b => b.Data)
                : (GCode.File?.Data?.Select(b => b.Data) ?? Enumerable.Empty<string>());
        }

        // This program's declared stock size, from its own (STOCK X=.. Y=.. Z=..) comment (the Fusion
        // ioSenderBatchPost add-in's format - see GCodeProgramComments) - null if it has none.
        public GCodeStockInfo? DeclaredStock
        {
            get
            {
                if (!IsLoadedJob)
                    throw new InvalidOperationException("DeclaredStock is only defined for the loaded job's ProgramView.");
                return GCodeProgramComments.ParseStock(ProgramLines());
            }
        }

        // This program's declared per-tool geometry (diameter/shape/angle/length), from its own
        // (TOOL T=n D=d TYPE=.. [A=..] [L=..]) comments (the Fusion ioSenderBatchPost add-in's format - see
        // GCodeProgramComments) - empty when the program has none. Unlike DeclaredStock this is available on
        // ANY ProgramView instance, not just the loaded job - a wizard/generated program's own tool comments
        // (if it has any) are just as meaningful per-instance as the loaded job's.
        public IReadOnlyDictionary<int, GCodeToolInfo> DeclaredTools
        {
            get { return GCodeProgramComments.ParseTools(ProgramLines()); }
        }

        // This program's EFFECTIVE stock size: DeclaredStock if the program declares one, else the machine's
        // full work envelope (GrblInfo.MaxTravel) as a conservative default/sanity bound - always defined
        // (never null) on the loaded-job instance.
        public GCodeStockInfo Stock
        {
            get
            {
                var declared = DeclaredStock;   // throws here if !IsLoadedJob
                if (declared.HasValue)
                    return declared.Value;
                var travel = GrblInfo.MaxTravel;
                return new GCodeStockInfo { X = travel.X, Y = travel.Y, Z = travel.Z };
            }
        }

        public string Title
        {
            get { return txtTitle.Text; }
            set
            {
                txtTitle.Text = value ?? string.Empty;
                titleBar.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        // When this view Connect()s, whether the host overlay should auto-pop into view. Wizards leave it true
        // (Generate = "show me what I just made"); the loaded job sets it false - loading a file shouldn't fling
        // the overlay open over the work area (the persistent Job-tab list already shows it).
        public bool AutoShow { get; set; } = true;

        public void SetProgram(ObservableCollection<GCodeBlock> blocks)
        {
            Blocks = blocks;
            gcodeList.SetProgram(blocks);
            Compact = false;   // a freshly generated/loaded program opens in full; Cycle Start shrinks it
            txtRunStatus.Visibility = Visibility.Collapsed;   // stale from whatever the last connected run was
            UpdateTitleTooltip(-1);
        }

        // --- Compact (3-line) run view --------------------------------------------------------------------
        // Collapses the view to the executing line plus the one before and after it, so a running program takes
        // little space. Auto-enabled when a run starts on this view; toggled by clicking the title bar. The host
        // (MainWindow overlay) watches CompactChanged to size the popup to content while compact.
        public static event System.Action CompactChanged;

        private bool _compact;
        public bool Compact
        {
            get { return _compact; }
            set
            {
                if (_compact == value)
                    return;
                _compact = value;
                gcodeList.SetCompactRows(value ? 3 : 0);
                UpdateTitleHint();
                CompactChanged?.Invoke();
            }
        }

        private void UpdateTitleHint()
        {
            txtTitleHint.Text = _compact ? "click to expand ▸" : "click to shrink to run view ▾";
        }

        private void TitleBar_Click(object sender, MouseButtonEventArgs e)
        {
            Compact = !Compact;
            e.Handled = true;
        }

        private GrblViewModel _model;
        private void HookModel(GrblViewModel model)
        {
            if (_model == model)
                return;
            if (_model != null)
                _model.PropertyChanged -= Model_PropertyChanged;
            _model = model;
            if (_model != null)
                _model.PropertyChanged += Model_PropertyChanged;
        }

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Cycle Start on this (connected) view -> auto-shrink to the 3-line run view.
            if (e.PropertyName == nameof(GrblViewModel.IsJobRunning)
                 && (sender as GrblViewModel)?.IsJobRunning == true && IsConnected)
            {
                Compact = true;
                UpdateRunStatus();
            }

            if (e.PropertyName == nameof(GrblViewModel.BlockExecuting) && IsConnected)
                UpdateRunStatus();
        }

        // "What's running right now" sticky status - the nearest (TOOL T=..) and (TOOLPATH ..) comments AT OR
        // BEFORE the currently executing line, so an operator glancing at a running job (Pocket, Counterbore,
        // ...) can tell which physical tool number is asked-for and which named operation ("Bottom finishing
        // pass - ball end") is currently cutting, without scrolling the (possibly huge) full program to find
        // the nearest header comment - those scroll out of the 3-line compact view within a few lines of any
        // section starting. (TOOL ...) reuses the SAME format the Fusion ioSenderBatchPost post-processor
        // emits (see GCodeProgramComments) so it plugs into the same parser; (TOOLPATH ...) is this app's own
        // convention for a plain-English per-operation header, emitted by the Odd Jobs job wizards.
        private static readonly Regex rxToolComment =
            new Regex(@"\(\s*TOOL\s+T=(\d+)\s+D=([0-9.]+)\s+TYPE=(\w+)[^)]*\)", RegexOptions.IgnoreCase);
        private static readonly Regex rxToolpathComment =
            new Regex(@"\(\s*TOOLPATH\s+(.+?)\)\s*$", RegexOptions.IgnoreCase);

        private void UpdateRunStatus()
        {
            int exec = _model?.BlockExecuting ?? -1;
            if (exec < 0 || Blocks == null || Blocks.Count == 0)
            {
                txtRunStatus.Visibility = Visibility.Collapsed;
                UpdateTitleTooltip(-1);   // nothing running yet - the tooltip lists the whole program
                return;
            }

            string tool = null, toolpath = null;
            int upTo = Math.Min(exec, Blocks.Count - 1);
            for (int i = 0; i <= upTo; i++)
            {
                string line = Blocks[i]?.Data;
                if (string.IsNullOrEmpty(line))
                    continue;
                var mt = rxToolComment.Match(line);
                if (mt.Success)
                    tool = string.Format("T{0} - {1}mm {2}", mt.Groups[1].Value, mt.Groups[2].Value, mt.Groups[3].Value);
                var mp = rxToolpathComment.Match(line);
                if (mp.Success)
                    toolpath = mp.Groups[1].Value.Trim();
            }

            if (tool == null && toolpath == null)
            {
                txtRunStatus.Visibility = Visibility.Collapsed;
                UpdateTitleTooltip(upTo);
                return;
            }

            txtRunStatus.Text = tool != null && toolpath != null ? tool + "  |  " + toolpath : (tool ?? toolpath);
            txtRunStatus.Visibility = Visibility.Visible;
            UpdateTitleTooltip(upTo);
        }

        // Title-bar tooltip: what's left to run. The (TOOLPATH ..) section currently executing heads the list
        // (marked, since "remaining" only makes sense relative to something), followed by the ones still to
        // come. The sticky status line above only ever shows the CURRENT section, so without this there was no
        // way to see what a long work order still has in store - the headers themselves scroll out of the
        // 3-line compact view immediately.
        private void UpdateTitleTooltip(int executingIndex)
        {
            if (Blocks == null || Blocks.Count == 0)
            {
                titleBar.ToolTip = DefaultTitleTooltip;
                return;
            }

            var upcoming = new List<string>();
            string current = null;
            for (int i = 0; i < Blocks.Count; i++)
            {
                var m = rxToolpathComment.Match(Blocks[i]?.Data ?? string.Empty);
                if (!m.Success)
                    continue;

                string name = m.Groups[1].Value.Trim();
                // Drop the "- N lines" tail AppendSection adds; it's noise in a to-do list.
                int dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0 && name.EndsWith("lines)", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, dash);

                if (i <= executingIndex)
                    current = name;         // keep overwriting: the last one at-or-before is the live section
                else
                    upcoming.Add(name);
            }

            if (current == null && upcoming.Count == 0)
            {
                titleBar.ToolTip = DefaultTitleTooltip;
                return;
            }

            var sb = new System.Text.StringBuilder();
            if (current != null)
                sb.Append("► ").Append(current).Append("   (running)");
            foreach (var name in upcoming)
                sb.Append(sb.Length > 0 ? "\n" : string.Empty).Append("    ").Append(name);
            if (upcoming.Count == 0 && current != null)
                sb.Append("\n    (last toolpath)");

            sb.Append("\n\n").Append(DefaultTitleTooltip);
            titleBar.ToolTip = sb.ToString();
        }

        private const string DefaultTitleTooltip = "Click to collapse to a 3-line run view / expand.";

        // Build a program from raw NGC text (one block per line; a line starting with '(' is a comment). The
        // Block-column line numbers are assigned for display by GCodeListControl.SetProgram.
        public void SetProgramText(string ngc)
        {
            var blocks = new ObservableCollection<GCodeBlock>();
            if (!string.IsNullOrEmpty(ngc))
            {
                uint n = 0;
                foreach (var raw in ngc.Replace("\r", string.Empty).Split('\n'))
                    blocks.Add(new GCodeBlock(++n, raw, raw.Length, raw.TrimStart().StartsWith("("), false));
            }
            SetProgram(blocks);
        }

        public void Clear()
        {
            SetProgram(new ObservableCollection<GCodeBlock>());
        }

        // Show/hide toggle. A host (overlay) drives visibility off this; here it maps straight to Visibility.
        public bool IsOpen
        {
            get { return Visibility == Visibility.Visible; }
            set { Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        }

        // --- Connect/Disconnect stack -------------------------------------------------------------------
        // The streamer is allocated to the TOP of this stack. Connect() = push (this view becomes active,
        // the previous one is remembered beneath); Disconnect() = pop (restore whatever was under it). The
        // stack starts empty - nothing is instantiated until a producer creates and connects a view.

        private static readonly List<ProgramView> _stack = new List<ProgramView>();

        // The connected (active) view - the one the streamer runs. Null when the stack is empty.
        public static ProgramView Active { get { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; } }

        // Fires when the active (top-of-stack) view changes. Step 2 hooks this to route the streamer and to
        // refresh Cycle Start enable / the mint source highlight.
        public static event System.Action ActiveChanged;

        public bool IsConnected { get { return Active == this; } }

        // Push: allocate the streamer to this view. Re-connecting an already-stacked view moves it to the top.
        public void Connect()
        {
            _stack.Remove(this);
            _stack.Add(this);
            ActiveChanged?.Invoke();
        }

        // Pop: release this view; the one beneath (if any) becomes active again. Safe if not on the stack.
        public void Disconnect()
        {
            if (_stack.Remove(this))
                ActiveChanged?.Invoke();
        }
    }
}
