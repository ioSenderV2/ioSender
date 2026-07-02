/*
 * ProgramView.xaml.cs - a standalone, streamer-connectable program view.
 *
 * Part of the ProgramView refactor (docs/Architecture-ProgramView-Refactor.md): replaces the single shared
 * program overlay with a reusable object. Each Load File / Load Folder / wizard Generate creates its own
 * instance; instances exist independently. The streamer is allocated to a view by an explicit Connect/Disconnect
 * push/pop stack - the connected view (stack top) is what Cycle Start runs.
 *
 * STEP 1 (this file): the object + the connect stack only. Nothing is wired to it yet - the streamer routing,
 * the main-window overlay migration, and the per-tool conversions come in later steps.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class ProgramView : UserControl
    {
        public ProgramView()
        {
            InitializeComponent();
        }

        // The program this view owns and renders. Set by the producer (Load/Generate); the same block objects
        // are what the streamer runs when this view is connected, so per-line markers are live (never a copy).
        public ObservableCollection<GCodeBlock> Blocks { get; private set; }

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
        }

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
