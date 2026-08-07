/*
 * GCodeProgram.cs - part of CNC Core library
 *
 * A loaded G-code program: the GCodeJob model (blocks, tokens, parser, bounding box) plus the load
 * pipeline and the completion wiring that keeps the machine model in step with it. Split out of
 * CNC.Controls.GCode, which now derives from this and keeps only the parts that talk to the operator's
 * desktop - the "one loaded program" singleton, file dialogs, drag/drop, and the converter/transformer
 * plug-in registry (see CNC.Controls.GCode's own header).
 *
 * There is deliberately NO static accessor here. A desktop client really does have exactly one loaded
 * program, so the singleton is correct - but it is correct *there*, not in Core: a server holds a program
 * per session. Construct one and hold it; nothing in Core reaches for "the" program.
 *
 */

/*

Copyright (c) 2018-2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CNC.GCode;

namespace CNC.Core
{
    public class GCodeProgram : IProgramSource
    {
        public const string FileTypes = "cnc,nc,ncc,ngc,gcode,tap";

        protected GCodeJob Program { get; } = new GCodeJob();

        public event GCodeJob.ToolChangedHandler ToolChanged = null;

        private readonly bool _transient;

        // True for a macro/tool-generated run (RunStreamedJobInPlace) - a standalone program built with
        // AddBlock that is streamed WITHOUT becoming the loaded job. Lets CycleStart tell "the operator's
        // loaded job" apart from "a probing/wizard macro's own g-code", so job-tab-only features (dry-run
        // mode's Z offset + spindle/coolant suppression) don't leak into macro runs that never armed them.
        public bool IsTransient { get { return _transient; } }

        public GCodeProgram()
        {
            Program.FileChanged += Program_FileChanged;
            Program.ToolChanged += Program_ToolChanged;
        }

        // Create a standalone, TRANSIENT program for a tool's generated run. It is streamed via JobControl.Source
        // WITHOUT becoming the loaded job, so it must never mutate the shared Model (FileName / limits / Blocks)
        // or push a header to the simulator. Model is set so the streamer can drive it; build it with AddBlock.
        public GCodeProgram(GrblViewModel model)
        {
            _transient = true;
            Model = model;
            Program.FileChanged += Program_FileChanged;
            Program.ToolChanged += Program_ToolChanged;
        }

        // --- Host configuration -------------------------------------------------------------------------
        // Two settings the load pipeline needs that Core does not own. ioSender keeps them in App.config,
        // which is client-side by decision (Core has no dependency on AppConfig at all - see
        // CNC.Controls.GCode for the overrides). A host that overrides neither gets no simulator header push
        // and no injected line numbers, which is the right default for a headless one: no simulator to push
        // to, and no operator preference to honour.

        protected virtual bool PushSimulatorHeaderEnabled { get { return false; } }
        protected virtual bool NumberLoadedLines { get { return false; } }

        private bool Program_ToolChanged(int toolNumber)
        {
            return ToolChanged == null ? true : ToolChanged(toolNumber);
        }

        private void Program_FileChanged(string filename)
        {
            if (_transient)
                return;   // a transient (tool-run) program never touches the shared Model or the simulator

            // Dry-run mode is a per-run, deliberately-armed toggle (see GrblViewModel.IsDryRunMode) - it must
            // never silently carry over onto a DIFFERENT program the operator just loaded. This is the single
            // point every load funnels through (see the comment below), so it can't be missed by loading via
            // a different route.
            if (Model != null)
                Model.IsDryRunMode = false;

            // Rebuild the shared (TOOL ...)/(STOCK ...) comment lookup once per completed Load File - this is
            // the single point every load funnels through (GCodeJob.FileChanged), so callers (e.g. touch-plate
            // probing's edge-radius compensation, CarveView's 3D carve simulation) never need to re-scan the
            // program themselves.
            GCodeProgramComments.Refresh(Data);

            if (Model != null)
            {
                if (filename == "")
                    Model.ProgramLimits.Clear();
                else foreach (int i in AxisFlags.All.ToIndices())
                {
                    Model.ProgramLimits.MinValues[i] = Model.ConvertMM2Current(Program.BoundingBox.Min[i]);
                    Model.ProgramLimits.MaxValues[i] = Model.ConvertMM2Current(Program.BoundingBox.Max[i]);
                }

                Model.FileName = filename;
                // A single file's FileName already IS its full path; generated programs have no path.
                Model.ProgramPath = filename;
            }

            if (filename != "")
                PushHeaderToSimulator();

            EstimateRunTime(filename);
        }

        // Run-time estimate for whatever was just loaded - file, drag-drop, converter, restored program, all
        // of which land here. Reuses the tokens the load ALREADY produced, so there is no second parse.
        //
        // Off the UI thread on purpose: the walk is proportional to program length, and this runs at the end
        // of a load that is already the slowest thing the app does on a big file. The estimate simply appears
        // a moment after the program does. The token list is SNAPSHOT first - the next load calls
        // Parser.Reset(), which clears that very list out from under a walk in progress - and a generation
        // counter makes sure a slow estimate for a file the operator has already replaced is discarded rather
        // than labelling the new one.
        private static int estimateGeneration;

        private void EstimateRunTime(string filename)
        {
            if (Model == null)
                return;

            int generation = System.Threading.Interlocked.Increment(ref estimateGeneration);
            var model = Model;

            if (filename == "" || !Program.Loaded)
            {
                model.EstimatedRunTime = string.Empty;
                return;
            }

            var snapshot = new List<GCodeToken>(Program.Parser.Tokens);
            System.Threading.Tasks.Task.Run(() =>
            {
                string text;
                try
                {
                    text = GCodeRunTime.Format(GCodeRunTime.Estimate(snapshot));
                }
                catch
                {
                    text = string.Empty;   // an estimate is a nicety; it must never surface as a failure
                }
                if (System.Threading.Volatile.Read(ref estimateGeneration) == generation)
                    model.EstimatedRunTime = text;
            });
        }

        // When connected to the simulator, send the program's leading comment lines (e.g. (STOCK X=..) and
        // (TOOL T=1 D=.. TYPE=..)) to it as soon as the program is loaded - so a start_job macro run *before*
        // the program already knows the stock size and tool table. Only the simulator consumes these comments;
        // a real controller ignores them. Stops at the first tool change / motion (the end of the header).
        private void PushHeaderToSimulator()
        {
            if (!PushSimulatorHeaderEnabled || Comms.com == null || !Comms.com.IsOpen)
                return;

            int scanned = 0;
            foreach (var block in Program.Blocks)
            {
                if (++scanned > 1000)
                    break;
                if (block.IsComment)
                {
                    Comms.com.WriteCommand(block.Data);
                    continue;
                }
                string u = block.Data.ToUpperInvariant();
                if (u.Contains("M6") || u.IndexOfAny(new[] { 'X', 'Y', 'Z' }) >= 0)
                    break;                                  // first tool change / axis move -> header done
            }
        }

        public bool IsLoaded { get { return Program.Loaded; } }
        public string FileName { get { return Model == null ? string.Empty : Model.FileName; } }
        public int ToolChanges { get { return Program.Parser.ToolChanges; } }
        public bool HasGoPredefinedPosition { get { return Program.Parser.HasGoPredefinedPosition; } }
        public int Decimals { get { return Program.Parser.Decimals; } }
        public bool HeightMapApplied { get { return Program.HeightMapApplied; } set { Program.HeightMapApplied = value; } }
        // Whether AddBlock prepends N<line> numbers. Set false for programs built in memory (e.g. the
        // calibration generator) so the gcode column doesn't duplicate the row's sequence number.
        public bool AddLineNumbers { get { return Program.AddLineNumbers; } set { Program.AddLineNumbers = value; } }

        public ObservableCollection<GCodeBlock> Data { get { return Program.Blocks; } }
        public int Blocks { get { return Program.Blocks.Count; } }
        public List<GCodeToken> Tokens { get { return Program.Tokens; } }
        public Queue<string> Commands { get { return Program.commands; } }
        public GCodeParser Parser { get { return Program.Parser; } }

        public GrblViewModel Model { get; set; }

        public void AddBlock(string block, Action action)
        {
            Program.AddBlock(block, action);

            if(action == Action.End && !_transient)
                Model.Blocks = Blocks;   // transient programs don't drive the job's block-count display
        }

        public void AddBlock(string block)
        {
            Program.AddBlock(block);
        }

        // Set by the streamer (CycleStart) when a run begins marking block Sent status; cleared here. Lets the
        // common case - re-entering the Job tab on an idle, never-/already-cleared program - skip the full
        // O(blocks) scan, which for a 300k+ line program was needless work on every tab activation.
        public bool StatusDirty { get; set; }

        public void ClearStatus()
        {
            if (!StatusDirty)
                return;

            foreach (var row in Program.Blocks)
                if (row.Sent != string.Empty)
                    row.Sent = string.Empty;

            StatusDirty = false;
        }

        public void Close()
        {
            Program.CloseFile();
            if (Model != null)
                Model.HasOutline = false;
            Model.Blocks = Blocks;
        }

        // --- Job-level push/pop -------------------------------------------------------------------------
        // Lets a producer (Work Order's Run) temporarily replace the loaded job with its own generated
        // program - showing in the Job tab's real docked list (ProgramPanel) and jobProgramView exactly
        // like any loaded file, since both ultimately read this SAME shared instance - then restore exactly
        // what was loaded before once it's done. Pop restores from an in-memory snapshot, NOT a re-Load()
        // from disk - a naive re-Load of a ~220k-line file took 30+ seconds on real hardware (2026-07-31);
        // see GCodeJob.TakeSnapshot's own comment for exactly what is/isn't captured. The restore itself is
        // one BulkObservableCollection.ReplaceAll call (a single Reset notification) rather than a per-block
        // Add loop, which froze the app entirely on a large file (2026-08-01, see PrepareRestore's comment).
        private readonly Stack<GCodeJob.Snapshot> _pushedSnapshots = new Stack<GCodeJob.Snapshot>();

        public void Push()
        {
            _pushedSnapshots.Push(Program.TakeSnapshot());
            DebugLog.Write("workorder", string.Format("GCode.File.Push: depth now {0}", _pushedSnapshots.Count));
        }

        public void Pop()
        {
            // An unbalanced Push is invisible from the outside - the generated program simply stays loaded as
            // "the job" with nothing left to restore it, which is what a work order that fails to evaporate at
            // the end of a run looks like (2026-08-06). Log both halves so the pairing can be read off the log
            // instead of inferred, and say so loudly when a Pop arrives with nothing to restore.
            if (_pushedSnapshots.Count == 0)
            {
                DebugLog.Write("workorder", "GCode.File.Pop: NOTHING PUSHED - ignored (the loaded program stays as-is)");
                return;
            }
            var snapshot = _pushedSnapshots.Pop();
            DebugLog.Write("workorder", string.Format("GCode.File.Pop: restoring '{0}', depth now {1}",
                snapshot.FileName ?? "(none)", _pushedSnapshots.Count));

            Program.PrepareRestore(snapshot);   // sets filename/BoundingBox/HasSections - no events, blocks untouched yet
            ((BulkObservableCollection<GCodeBlock>)Program.Blocks).ReplaceAll(snapshot.Blocks);

            // Set HasOutline before RaiseFileChanged, not after: FileChanged reconnects jobProgramView
            // (MainWindow.OnJobFileChanged -> SetProgram(null) -> ApplyGrouping) SYNCHRONOUSLY, which reads
            // Model.HasOutline at that exact moment - by the time RaiseFileChanged returns it's already too
            // late. Setting it any EARLIER than this (before ReplaceAll above) let GCodeListControl's own
            // HasOutline-changed handler call ApplyGrouping against still-stale (pre-restore) block data.
            if (Model != null)
                Model.HasOutline = Program.HasSections;

            Program.RaiseFileChanged();

            if (Model != null)
                Model.Blocks = Blocks;
        }

        // Load already-built G-code text (not from disk) as the job - the same completion pipeline as Load
        // File (FileChanged fires: Model.FileName/limits update, the simulator header push runs, the
        // docked list and jobProgramView both pick it up), just fed from a string instead of a file. `name`
        // is cosmetic - becomes the docked list's title; there is no actual file at that path.
        public void LoadText(string name, string program)
        {
            if (Model != null)
                Model.HasOutline = false;

            var lines = (program ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            AddBlock(name, Action.New);
            for (int i = 0; i < lines.Length - 1; i++)
                AddBlock(lines[i], Action.Add);
            AddBlock(lines[lines.Length - 1], Action.End);
        }

        // Read + parse a (potentially huge) program on a background thread so the rest of the UI stays
        // responsive - and so the LIVE, DataGrid-bound Blocks collection is never touched until parsing is
        // completely done. 'parse' runs on the worker thread and writes into a private, unbound buffer (no
        // ObservableCollection, no dispatcher hops, no per-item notifications - the program view has no
        // reason to be involved while a file is still being read off disk); it must finish with
        // Program.ComputeLimits(). 'onDone' runs on the UI thread after ONE bulk bind (raise FileChanged, set
        // Model.Blocks, ...). This used to flush in batches of 4000 via periodic dispatcher.Invoke calls, each
        // batch still hundreds/thousands of individual ObservableCollection.Add() notifications on the live
        // grid - confirmed as real, measurable load-time cost on a 220k-line file (2026-08-01); a single
        // BulkObservableCollection.ReplaceAll at the end fires exactly one Reset instead.
        // 'displayName' drives the status line: "Loading <name>..." while the worker reads the file, then
        // "Loaded <name> - N lines in T s" once it is bound. On a 220k-line file the load is long enough
        // that a silent wait cursor reads as a hang, so it says what it is doing and what it did.
        private async void BackgroundLoad(System.Action parse, System.Action onDone, string displayName = null)
        {
            var buffer = new List<GCodeBlock>(65536);

            if (Model != null)
                Model.IsLoading = true;

            // Whole-operation timing, distinct from the per-phase sw below: this is the number the operator
            // actually waited, so it must span read+parse, the UI bind AND onDone, not just one phase.
            var total = System.Diagnostics.Stopwatch.StartNew();

            if (Model != null && displayName != null)
                Model.Message = string.Format("Loading {0}...", displayName);

            // Worker-thread sink: just accumulate. No dispatcher marshalling at all until parsing is done.
            Program.BlockConsumer = b => buffer.Add(b);

            try
            {
                // Timing instrumentation (2026-08-01) - the "bind once" change unexpectedly measured SLOWER
                // (50s vs the original 30s) on a 220k-line file; splitting the phases pins down where the
                // time actually goes before changing anything else further blind.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await System.Threading.Tasks.Task.Run(parse);
                DebugLog.Write("load", string.Format("read+parse+ComputeLimits: {0} ms ({1} blocks)", sw.ElapsedMilliseconds, buffer.Count));

                // Resumed on the UI thread (the awaited Task.Run's continuation captures the calling
                // SynchronizationContext) - bind everything in one shot.
                sw.Restart();
                ((BulkObservableCollection<GCodeBlock>)Program.Blocks).ReplaceAll(buffer);
                DebugLog.Write("load", string.Format("ReplaceAll (UI bind): {0} ms", sw.ElapsedMilliseconds));

                sw.Restart();
                onDone?.Invoke();
                DebugLog.Write("load", string.Format("onDone (FileChanged/HasOutline/simulator push/...): {0} ms", sw.ElapsedMilliseconds));

                // Program.Loaded distinguishes a real load from one onDone aborted (a parse failure calls
                // Close()) - reporting "Loaded" for a discarded partial file would be a lie, so that case
                // just clears the "Loading..." text rather than replacing it.
                if (Model != null && displayName != null)
                    Model.Message = Program.Loaded
                        ? string.Format("Loaded {0} - {1:N0} lines in {2:N1} s", displayName, buffer.Count, total.Elapsed.TotalSeconds)
                        : string.Empty;
            }
            catch (Exception e)
            {
                UserPrompt.Show("Error loading program: " + e.Message, "ioSender", PromptButtons.OK, PromptIcon.Warning);
            }
            finally
            {
                Program.BlockConsumer = null;
                if (Model != null)
                    Model.IsLoading = false;
            }
        }

        // A file this program cannot read itself may still be loadable by a converter the host registered
        // (DXF/HPGL/Excellon ...). Those live client-side - IGCodeConverter and CNC.Converters.dll are WPF
        // assemblies, and which converters exist is the host's business - so Core only asks whether one
        // claimed the file. Return true if it did (loading is then that converter's job and Load stops here).
        // Called AFTER the re-entrancy guard and the HasOutline reset, so a converted load is subject to
        // both exactly as a plain one is.
        protected virtual bool LoadViaConverter(string filename)
        {
            return false;
        }

        public void Load(string filename)
        {
            if (Model != null && Model.IsLoading)
                return;   // a background load is already in progress - ignore a re-entrant request

            if (Model != null)
                Model.HasOutline = false;

            if (LoadViaConverter(filename))
                return;

            // Read + parse on a background thread (see BackgroundLoad) so a large single file doesn't freeze the
            // UI. Clear + reset on the UI thread first; the per-line parse loop runs on the worker thread.
            Program.AddBlock(filename, Action.New);
            bool addLineNumbers = GrblInfo.UseLinenumbers && NumberLoadedLines;
            bool[] ok = { true };

            BackgroundLoad(() =>
            {
                // Timed separately (2026-08-04): these two are very different jobs and the old single
                // "read+parse+ComputeLimits" number could not tell them apart. ParseFileLines lexes and
                // builds the token model; ComputeLimits then re-executes every token through a
                // GCodeEmulator - a SECOND full interpretation of the program - and takes the bounding box
                // of each arc by expanding it into 0.01 mm points. Which of the two owns the ~32 s on a
                // 220k-line file decides whether the fix is an analytic arc bounding box (contained) or the
                // per-line token allocation (invasive - the viewer and explainer read those tokens).
                var phase = System.Diagnostics.Stopwatch.StartNew();
                ok[0] = Program.ParseFileLines(filename, addLineNumbers);
                DebugLog.Write("load", string.Format("  read+parse: {0} ms ({1:N0} tokens)", phase.ElapsedMilliseconds, Program.Tokens.Count));

                if (ok[0])
                {
                    phase.Restart();
                    Program.ComputeLimits();
                    DebugLog.Write("load", string.Format("  ComputeLimits (emulator re-run + arc bounds): {0} ms", phase.ElapsedMilliseconds));
                }
            },
            () =>
            {
                if (ok[0])
                {
                    Program.RaiseFileChanged();
                    // Recognizes the Fusion add-in's (--- seq: name (Tn) ---) section markers
                    // (GCodeJob.ParseFileLines calls BeginSection on a match) - an ordinary file with no such
                    // markers leaves this false.
                    Model.HasOutline = Program.HasSections;
                    Model.Blocks = Blocks;
                }
                else
                    Close();   // aborted mid-parse: discard the partial load
            },
            System.IO.Path.GetFileName(filename));
        }
    }
}
