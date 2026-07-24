/*
 * GCodeJob.cs - part of CNC Controls library
 *
 * v0.47 / 2026-02-13 / Io Engineering (Terje Io)
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
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CNC.GCode;

namespace CNC.Core
{
    public enum Action
    {
        New,
        Add,
        End
    }

    public class GCodeBlock : ViewModelBase
    {
        private bool _break;
        private uint _lineNum;
        private long _explicitLineNum = -1;
        private string _data, _dataDisplay = string.Empty, _blockDisplay = string.Empty, _sent = string.Empty;

        private static readonly System.Text.RegularExpressions.Regex _nWord =
            new System.Text.RegularExpressions.Regex(@"^\s*[Nn](\d+)\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

        public GCodeBlock (uint lineNum, string block, int length, bool isComment, bool programEnd)
        {
            LineNum = lineNum;
            Data = block;
            Length = length;
            IsComment = isComment;
            ProgramEnd = programEnd;
        }

        public uint LineNum { get { return _lineNum; } set { _lineNum = value; RefreshDisplay(); } }
        public int Length { get; set; }
        public string Data { get { return _data; } set { _data = value; RefreshDisplay(); OnPropertyChanged(); } }

        // Program list display: the Block column shows the program line number and the Data column hides the N
        // word - while Data itself stays intact for streaming (the controller needs the N word for line-number
        // progress reporting). The Block sequence continues across unnumbered lines (previous + 1) and jumps to
        // an explicit N when present, so O<...> calls / comments still get an in-sequence number. BlockDisplay is
        // assigned by a collection pass (GCodeListControl.SetProgram) that has the ordering; the fallback here
        // keeps it non-blank if that pass has not run.
        public bool HasExplicitLineNum { get { return _explicitLineNum >= 0; } }
        public long ExplicitLineNum { get { return _explicitLineNum; } }
        public string DataDisplay { get { return _dataDisplay; } }
        public string BlockDisplay
        {
            get { return _blockDisplay; }
            set { if (_blockDisplay != value) { _blockDisplay = value; OnPropertyChanged(); } }
        }

        private void RefreshDisplay()
        {
            var m = _data != null ? _nWord.Match(_data) : System.Text.RegularExpressions.Match.Empty;
            if (m.Success && long.TryParse(m.Groups[1].Value, out _explicitLineNum))
                _dataDisplay = _data.Substring(m.Length);
            else {
                _explicitLineNum = -1;
                _dataDisplay = _data ?? string.Empty;
            }
            BlockDisplay = _explicitLineNum >= 0 ? _explicitLineNum.ToString() : _lineNum.ToString();
            OnPropertyChanged(nameof(DataDisplay));
        }
        public string Sent {
            get { return _sent; }
            set { _sent = BreakAt ? "BRK " + value : value; OnPropertyChanged(); }
        }
        public bool File { get; set; }
        public bool IsComment { get; set; }
        public bool BreakAt
        {
            get { return _break; }
            set {
                _break = value;
                Sent = _sent.Replace("BRK ", string.Empty);
                Length += _break ? 3 : -3;
            }
        }
        public bool ProgramEnd { get; set; }
        public bool Ok { get; set; }

        // Set at load time (see GCodeJob.ParseFileLines/AddBlock) from the real G-code parser's tokens for
        // this line - true iff it contains a spindle-ON (M3/M4) or coolant-ON (M7/M8) command. Consulted by
        // the streamers (StreamPump.SendNext, JobControl.SendNextLine) to neutralise the line when dry-run/
        // verify mode is active - see GrblViewModel.IsDryRunMode. NOT set for O-word/#-expression lines that
        // bypass the parser (GrblInfo.ExpressionsSupported passthrough) - those are macro control flow, not
        // raw spindle commands, in normal use.
        public bool HasSpindleOrCoolantOn { get; set; }

        // Same idea as HasSpindleOrCoolantOn, for M6 (tool change): consulted by the streamers to
        // neutralise the line when dry-run is active, so the loaded program's own tool changes never run
        // during a dry run. Dry run never cuts, so which physical tool is actually in the spindle doesn't
        // matter - and skipping the M6 entirely (rather than letting it run and just suppressing something
        // downstream) avoids tc.macro's own work-coordinate moves ever executing while dry-run's Z-offset
        // G92 is active, which corrupted a real tool-change macro's positioning (Alarm:2 + a hang-watchdog
        // reset) before this fix.
        public bool HasToolChange { get; set; }

        // Outline grouping: set when a program is assembled from a folder of
        // per-toolpath files (see GCode.LoadFolder). Null for ordinary single-
        // file loads (the Program list then renders flat, ungrouped).
        public string Section { get; set; }
        public bool IsSectionStart { get; set; }

        // The parser's own tokens for exactly this line, captured at parse time (see GCodeJob.ParseFileLines/
        // AddBlock, which slice Parser.Tokens[tokenStart..] right after parsing this block). Used by the
        // program list's hover-explain feature (GCodeListControl.BuildExplanation) instead of matching tokens
        // to a block via GCodeToken.LineNumber == LineNum - that match is unreliable whenever a file mixes
        // explicit N-words with unnumbered lines (common: a post-processor injects N only around tool
        // changes), because GCodeToken.LineNumber comes from the file's OWN (author-chosen, arbitrary)
        // N-word, last-seen and persisting across unnumbered lines, while LineNum is ioSender's unrelated
        // internal sequential count - the two numbering spaces can coincidentally collide anywhere in the
        // file, which read as "the tooltip is off by one" but wasn't actually about line adjacency. Empty for
        // blocks not built through GCodeJob (e.g. a wizard's own SetProgram(blocks)) - callers already treat
        // an empty/no token list as "explain from raw text instead", so this is a safe default.
        public List<GCodeToken> Tokens { get; set; } = new List<GCodeToken>();
    }

    public class GCodeJob
    {
        uint LineNumber = 1;

        // Section-marker comment the Fusion ioSenderBatchPost add-in emits between operations in its combined
        // output - "(--- 2: FinishBottom (T2) ---)" - captures the inner text verbatim as the section name
        // (matches FolderToolpath.Section's "{seq}: {name} (T{tool})" format exactly, since that's what the
        // add-in wraps). Recognized during a plain Load File parse, not just Load Folder's stitching.
        private static readonly Regex rxSectionMarker = new Regex(@"^\(---\s*(.+?)\s*---\)$");

        // Neutralise interior parens in a comment line so it survives as ONE well-formed comment regardless of
        // what's inside it (grblHAL-style parsers end a comment at the first ')'). Keeps the outer ( and last )
        // intact; only content strictly between them is affected. Same idiom as StartJobView's SanitizeParens,
        // applied here on the READ side (incoming files) rather than when generating NGC.
        private static string SanitizeCommentParens(string s)
        {
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
                return s;

            var sb = new System.Text.StringBuilder(s.Length);
            sb.Append(s, 0, open + 1);
            for (int i = open + 1; i < close; i++)
                sb.Append(s[i] == '(' ? '[' : s[i] == ')' ? ']' : s[i]);
            sb.Append(s, close, s.Length - close);
            return sb.ToString();
        }

        // Parser.Tokens is a CUMULATIVE, whole-file list (Parser.Reset() clears it once per file load, not
        // per ParseBlock() call - CarveView/Viewer/GCodeRotate/GCodeWrap/GCodeCompress/GCodeTransform/
        // ArcsToLines/StartJobView etc. all rely on it staying that way). The two helpers below must only
        // look at the tokens THIS call to ParseBlock just appended - NOT the whole list - or the first M3/M6
        // anywhere in a file would flag every line after it too (confirmed on real hardware: a dry run with
        // an early tool change silently turned the entire rest of the program into no-op comments - hundreds
        // of instant acks, zero motion). Callers pass the Tokens.Count captured BEFORE the ParseBlock call.

        // True iff the tokens just produced by Parser.ParseBlock for the current line (Parser.Tokens[tokenStart..])
        // include a spindle-ON (M3/M4) or coolant-ON (M7/M8) command - checked via the real parser's token
        // types, not a regex guess, so it can't be fooled by e.g. an M3 mentioned inside a comment. See
        // GCodeBlock.HasSpindleOrCoolantOn for why this exists (dry-run/verify mode spindle safety).
        private bool CurrentLineHasSpindleOrCoolantOn(int tokenStart)
        {
            for (int i = tokenStart; i < Parser.Tokens.Count; i++)
            {
                var t = Parser.Tokens[i];
                if (t is GCSpindleState && (t.Command == Commands.M3 || t.Command == Commands.M4))
                    return true;
                if (t is GCCoolantState && (t.Command == Commands.M7 || t.Command == Commands.M8))
                    return true;
            }
            return false;
        }

        // True iff the tokens just produced by Parser.ParseBlock for the current line (Parser.Tokens[tokenStart..])
        // include M6 (tool change). Same calling convention/reasoning as CurrentLineHasSpindleOrCoolantOn -
        // see GCodeBlock.HasToolChange.
        private bool CurrentLineHasToolChange(int tokenStart)
        {
            for (int i = tokenStart; i < Parser.Tokens.Count; i++)
                if (Parser.Tokens[i].Command == Commands.M6)
                    return true;
            return false;
        }

        private string filename = string.Empty;
        public ObservableCollection<GCodeBlock> blocks = new ObservableCollection<GCodeBlock>();

        public Queue<string> commands = new Queue<string>();

        public delegate bool ToolChangedHandler(int toolNumber);
        public event ToolChangedHandler ToolChanged = null;

        public delegate void FileChangedHandler(string filename);
        public event FileChangedHandler FileChanged = null;

        public GCodeJob()
        {
            Reset();

            Parser.ToolChanged += Parser_ToolChanged;
        }

        private bool Parser_ToolChanged(int toolNumber)
        {
            return ToolChanged == null ? true : ToolChanged(toolNumber);
        }

        public ObservableCollection<GCodeBlock> Blocks { get { return blocks; } }
        public bool Loaded { get { return blocks.Count > 0; } }
        public bool HeightMapApplied { get; set; }

        // Section currently being assembled (outline grouping). The next block
        // added after BeginSection() is flagged as that section's first block.
        public string CurrentSection { get; set; }
        private bool sectionStartPending = false;

        // True once BeginSection() has been called at least once since the last Reset() - i.e. this program
        // has an outline to show (GrblViewModel.HasOutline), regardless of whether it came from Load Folder's
        // stitching or Load File recognizing the Fusion add-in's (--- seq: name (Tn) ---) section markers.
        public bool HasSections { get; private set; }

        // Modal state (distance/feed mode, plane, units) to replay when starting mid-program from an
        // outline section, since a "Start from this toolpath" run skips whatever set that state earlier.
        public static readonly string[] DefaultProlog = { "G90 G94", "G17", "G21" };

        public void BeginSection(string name)
        {
            CurrentSection = name;
            sectionStartPending = true;
            HasSections = true;
        }

        // Whether AddBlock prepends N<line> numbers (when GrblInfo.UseLinenumbers is also set).
        // Default true preserves existing callers; LoadFile takes its own addLineNumber arg.
        // Reset to true by Reset().
        public bool AddLineNumbers { get; set; } = true;

        // Optional sink for parsed blocks. When set (a background load), AddStamped/ParseFileLines route parsed
        // blocks HERE instead of straight into the bound ObservableCollection - so the expensive parse runs off
        // the UI thread and the caller batches the collection adds onto the UI thread itself. Null = normal path.
        public System.Action<GCodeBlock> BlockConsumer;

        public List<GCodeToken> Tokens { get { return Parser.Tokens; } }
        public GcodeBoundingBox BoundingBox { get; private set; } = new GcodeBoundingBox();
        public GCodeParser Parser { get; private set; } = new GCodeParser();

        public double min_feed { get; private set; }
        public double max_feed { get; private set; }

        public bool LoadFile(string filename, bool addLineNumber = false)
        {
            AddBlock(filename, Action.New);

            bool ok = ParseFileLines(filename, addLineNumber);

            if (ok)
                AddBlock("", Action.End);
            else
                CloseFile();

            return ok;
        }

        // The per-line read/parse loop of a single file, WITHOUT the New/End bookends - so a background loader can
        // call AddBlock(Action.New) on the UI thread, run THIS on a worker thread (blocks routed through the
        // BlockConsumer sink), then finalize (ComputeLimits + RaiseFileChanged) once the buffered blocks are
        // flushed. The parse itself is unchanged from the old inline LoadFile; only blocks.Add became Emit.
        public bool ParseFileLines(string filename, bool addLineNumber = false)
        {
            bool ok = true, isComment;
            uint ln;

            FileInfo file = new FileInfo(filename);

            StreamReader sr = file.OpenText();

            string block = sr.ReadLine();

            while (block != null)
            {
                try
                {
                    block = block.Trim();

                    // A comment may itself contain parens (e.g. the Fusion add-in's malformed, pre-fix
                    // (--- seq: name (Tn) ---) section markers) - grblHAL-style parsers end a comment at the
                    // FIRST ')', so a nested one leaves garbage behind that fails as G-code. Neutralise interior
                    // parens ( -> [, ) -> ] ) so a single well-formed comment survives regardless of what's
                    // inside it; a no-op for ordinary comments (STOCK/TOOL lines have no interior parens).
                    if (block.Length > 1 && block[0] == '(')
                        block = SanitizeCommentParens(block);

                    int tokenStart = Parser.Tokens.Count;
                    if (Parser.ParseBlock(ref block, false, out ln, out isComment))
                    {
                        if (ln > 0)
                        {
                            LineNumber = ln;
                            addLineNumber = false;
                        }
                        else if (addLineNumber)
                        {
                            LineNumber += 10;
                            block = "N" + LineNumber.ToString() + block;
                        } else
                            LineNumber++;

                        // Recognize the Fusion add-in's (--- seq: name (Tn) ---) section-marker comments so a
                        // plain Load File builds the same outline Load Folder's stitching produces - AddStamped
                        // (not Emit) is what actually attaches Section/IsSectionStart to the block.
                        if (isComment)
                        {
                            var sm = rxSectionMarker.Match(block);
                            if (sm.Success)
                                BeginSection(sm.Groups[1].Value);
                        }

                        AddStamped(new GCodeBlock(LineNumber, block, block.Length + 1, isComment, Parser.ProgramEnd) { HasSpindleOrCoolantOn = CurrentLineHasSpindleOrCoolantOn(tokenStart), HasToolChange = CurrentLineHasToolChange(tokenStart), Tokens = Parser.Tokens.GetRange(tokenStart, Parser.Tokens.Count - tokenStart) });
                        while (commands.Count > 0)
                        {
                            block = commands.Dequeue();
                            LineNumber++;
                            if (addLineNumber)
                                block = "N" + (LineNumber).ToString() + block;
                            AddStamped(new GCodeBlock(LineNumber, block, block.Length + 1, false, false));
                        }
                    }
                    block = sr.ReadLine();
                }
                catch (Exception e)
                {
                    if ((ok = AppDialogs.Show(string.Format(LibStrings.FindResource("LoadError").Replace("\\n", "\r"), e.Message, LineNumber, block), "ioSender", MessageBoxButton.YesNo) == MessageBoxResult.Yes))
                        block = sr.ReadLine();
                    else
                        block = null;
                }
            }

            sr.Close();

            return ok;
        }

        public void AddBlock(string block, Action action)
        {
            if (action == Action.New)
            {
                if (Loaded)
                    blocks.Clear();

                Reset();
                commands.Clear();

                // Let the loader keep #-expression / O-word lines verbatim (instead of dropping them) when the
                // controller evaluates expressions, so a generated O-word program can be streamed (flow control)
                // rather than MDI'd.
                Parser.ExpressionsSupported = GrblInfo.ExpressionsSupported;

                filename = block;

            }
            else if (block != null && block.Trim().Length > 0) try
            {
                bool isComment = false;
                uint ln;

                block = block.Trim();

                // O-word flow (O<name> CALL/IF/WHILE/...) and #-expression lines are evaluated by the CONTROLLER,
                // not by this block parser. When the controller supports expressions keep them VERBATIM rather
                // than dropping them - and NEVER prefix an O-word OR #-parameter line with a line number (a
                // leading N breaks the controller's O-word routing, and - confirmed via a real Start Job failure,
                // error:2 "Bad number format" - ALSO breaks a #<name>=[expr] assignment: "N470#<name>=..." with
                // no separating space isn't recognised as a parameter assignment) - so generated O-word programs
                // (e.g. Load Stock's corner probe) and their #<_name>=value setup lines can be streamed with flow
                // control instead of being forced onto the MDI path.
                string ts = block.TrimStart();
                bool isOword = ts.Length > 1 && (ts[0] == 'o' || ts[0] == 'O') && ts[1] == '<';
                bool isParamLine = ts.Length > 0 && ts[0] == '#';
                bool passThrough = GrblInfo.ExpressionsSupported && (isOword || block.IndexOf('#') >= 0);

                int tokenStart = Parser.Tokens.Count;
                bool parsed;
                try { parsed = Parser.ParseBlock(ref block, false, out ln, out isComment); }
                catch { if (!passThrough) throw; parsed = false; }

                if (parsed || passThrough)
                {
                    // Don't add a line number to a block that already carries one (e.g. a generated program that
                    // numbered its own lines) - two N-words make a malformed block (the controller rejects it
                    // with error:25). Also never number an O-word or #-parameter line (see above).
                    string nt = block.TrimStart();
                    bool alreadyNumbered = nt.Length > 1 && (nt[0] == 'N' || nt[0] == 'n') && char.IsDigit(nt[1]);
                    if(GrblInfo.UseLinenumbers && AddLineNumbers && !isOword && !isParamLine && !alreadyNumbered)
                    {
                        LineNumber += 10;
                        block = "N" + LineNumber.ToString() + block;
                    } else
                        LineNumber++;

                    // parsed guards Tokens here too: a failed parse (O-word/#-expression passthrough, see
                    // `passThrough` above) leaves Parser.Tokens stale from whatever line last parsed
                    // successfully - only trust it right after ParseBlock itself returned true.
                    AddStamped(new GCodeBlock(LineNumber, block, block.Length + 1, isComment, parsed && Parser.ProgramEnd) { HasSpindleOrCoolantOn = parsed && CurrentLineHasSpindleOrCoolantOn(tokenStart), HasToolChange = parsed && CurrentLineHasToolChange(tokenStart), Tokens = parsed ? Parser.Tokens.GetRange(tokenStart, Parser.Tokens.Count - tokenStart) : new List<GCodeToken>() });
                    while (commands.Count > 0)
                    {
                        block = commands.Dequeue();
                        LineNumber++;
                        if (GrblInfo.UseLinenumbers && AddLineNumbers)
                            block = "N" + (LineNumber).ToString() + block;
                        AddStamped(new GCodeBlock(LineNumber, block, block.Length + 1, false, false));
                    }
                }
            }
            catch //(Exception e)
            {
                // 
            }

            if (action == Action.End)
            {
                ComputeLimits();
                FileChanged?.Invoke(filename);
            }
        }

        // Calculate program limits (bounding box) by emulating the parsed tokens. Touches only Tokens + BoundingBox
        // (no UI, no collection) so it is safe to run on a worker thread. Wrapped so an exotic program the emulator
        // can't fully evaluate (e.g. a controller-side NGC probe/macro program full of #-expressions and O-words,
        // like Load Stock) yields a partial/empty box rather than taking down the load/run with a parse fault.
        public void ComputeLimits()
        {
            BoundingBox.Reset();

            try
            {
                GCodeEmulator emu = new GCodeEmulator(true);

                foreach (var cmd in emu.Execute(Tokens))
                {
                    if (cmd.Token is GCArc)
                        BoundingBox.AddBoundingBox((cmd.Token as GCArc).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental));
                    else if (cmd.Token is GCCubicSpline)
                        BoundingBox.AddBoundingBox((cmd.Token as GCCubicSpline).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental));
                    else if (cmd.Token is GCQuadraticSpline)
                        BoundingBox.AddBoundingBox((cmd.Token as GCQuadraticSpline).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental));
                    else if (cmd.Token is GCAxisCommand9)
                    {
                        if (GrblInfo.LatheUVWModeEnabled)
                            BoundingBox.AddBoundingBox((cmd.Token as GCAxisCommand9).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental));
                        else
                            BoundingBox.AddPoint(cmd.End, (cmd.Token as GCAxisCommand9).AxisFlags);
                    }
                }
            }
            catch { /* unparseable expression program - leave whatever box was accumulated */ }

            BoundingBox.Conclude();
        }

        // Fire FileChanged on demand - used by the background loader to raise it on the UI thread, after the
        // buffered blocks have been flushed to the bound collection and the limits computed.
        public void RaiseFileChanged()
        {
            FileChanged?.Invoke(filename);
        }

        public void AddBlock(string block)
        {
            AddBlock(block, Action.Add);
        }

        private void AddStamped(GCodeBlock b)
        {
            b.Section = CurrentSection;
            if (sectionStartPending)
            {
                b.IsSectionStart = true;
                sectionStartPending = false;
            }
            Emit(b);
        }

        // Route a parsed block either to the background-load sink (worker thread) or straight to the bound
        // collection (normal synchronous path). See BlockConsumer.
        private void Emit(GCodeBlock b)
        {
            if (BlockConsumer != null)
                BlockConsumer(b);
            else
                blocks.Add(b);
        }

        public void CloseFile()
        {
            if (Loaded)
                blocks.Clear();

            commands.Clear();

            Reset();

            filename = "";

            FileChanged?.Invoke(filename);
        }

        private void Reset()
        {
            min_feed = double.MaxValue;
            max_feed = double.MinValue;
            BoundingBox.Reset();
            LineNumber = 0;
            HeightMapApplied = false;
            CurrentSection = null;
            sectionStartPending = false;
            HasSections = false;
            AddLineNumbers = true;
            Parser.Reset();
        }
    }

    public class ProgramLimits : ViewModelBase
    {
        public ProgramLimits()
        {
            init();
        }

        public ProgramLimits(ProgramLimits limits, double scaleFactor)
        {
            for (var i = 0; i < MinValues.Length; i++)
            {
                MinValues[i] = limits.MinValues[i] * scaleFactor;
                MaxValues[i] = limits.MaxValues[i] * scaleFactor;
            }

            MinValues.PropertyChanged += MinValues_PropertyChanged;
            MaxValues.PropertyChanged += MaxValues_PropertyChanged;
        }

        private void init()
        {
            Clear();

            MinValues.PropertyChanged += MinValues_PropertyChanged;
            MaxValues.PropertyChanged += MaxValues_PropertyChanged;
        }

        public void Clear()
        {
            for (var i = 0; i < MinValues.Length; i++)
            {
                MinValues[i] = double.NaN;
                MaxValues[i] = double.NaN;
            }
        }

        public void Scale(double factor)
        {
            for (var i = 0; i < MinValues.Length; i++)
            {
                MinValues[i] *= factor;
                MaxValues[i] *= factor;
            }
        }

        public bool SuspendNotifications
        {
            get { return MinValues.SuspendNotifications; }
            set { MinValues.SuspendNotifications = MaxValues.SuspendNotifications = value; }
        }

        private void MinValues_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged("Min" + e.PropertyName);
        }
        private void MaxValues_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged("Max" + e.PropertyName);
        }

        public CoordinateValues<double> MinValues { get; private set; } = new CoordinateValues<double>();
        public double MinX { get { return MinValues[0]; } set { MinValues[0] = value; } }
        public double MinY { get { return MinValues[1]; } set { MinValues[1] = value; } }
        public double MinZ { get { return MinValues[2]; } set { MinValues[2] = value; } }
        public double MinA { get { return MinValues[3]; } set { MinValues[3] = value; } }
        public double MinB { get { return MinValues[4]; } set { MinValues[4] = value; } }
        public double MinC { get { return MinValues[5]; } set { MinValues[5] = value; } }
        public double MinU { get { return MinValues[6]; } set { MinValues[6] = value; } }
        public double MinV { get { return MinValues[7]; } set { MinValues[7] = value; } }
        public double MinW { get { return MinValues[8]; } set { MinValues[8] = value; } }

        public CoordinateValues<double> MaxValues { get; private set; } = new CoordinateValues<double>();
        public double MaxX { get { return MaxValues[0]; } set { MaxValues[0] = value; } }
        public double MaxY { get { return MaxValues[1]; } set { MaxValues[1] = value; } }
        public double MaxZ { get { return MaxValues[2]; } set { MaxValues[2] = value; } }
        public double MaxA { get { return MaxValues[3]; } set { MaxValues[3] = value; } }
        public double MaxB { get { return MaxValues[4]; } set { MaxValues[4] = value; } }
        public double MaxC { get { return MaxValues[5]; } set { MaxValues[5] = value; } }
        public double MaxU { get { return MaxValues[6]; } set { MaxValues[6] = value; } }
        public double MaxV { get { return MaxValues[7]; } set { MaxValues[7] = value; } }
        public double MaxW { get { return MaxValues[8]; } set { MaxValues[8] = value; } }

        public double SizeX { get { return MaxX - MinX; } }
        public double SizeY { get { return MaxY - MinY; } }
        public double SizeZ { get { return MaxZ - MinZ; } }
        public double MaxSize { get { return Math.Max(Math.Max(SizeX, SizeY), SizeZ); } }
    }

    public class GcodeBoundingBox
    {
        public double[] Min = new double[9];
        public double[] Max = new double[9];
        public double[] Size = new double[9];

        public GcodeBoundingBox()
        {
            Reset();
        }

        public void Reset()
        {
            for (int i = 0; i < Min.Length; i++)
            {
                Min[i] = double.MaxValue;
                Max[i] = double.MinValue;
            }
        }

        public void Conclude()
        {
            for (int i = 0; i < Min.Length; i++)
            {
                if (Max[i] == double.MinValue)
                    Min[i] = Max[i] = 0.0;
                Size[i] = Math.Abs(Max[i] - Min[i]);
            }
        }

        private void AddPoint(double x, double y, double z)
        {
            Min[0] = Math.Min(Min[0], x);
            Max[0] = Math.Max(Max[0], x);

            Min[1] = Math.Min(Min[1], y);
            Max[1] = Math.Max(Max[1], y);

            Min[2] = Math.Min(Min[2], z);
            Max[2] = Math.Max(Max[2], z);
        }

        public void AddPoint(GCPlane plane, double x, double y, double z)
        {
            Min[plane.Axis0] = Math.Min(Min[plane.Axis0], x);
            Max[plane.Axis0] = Math.Max(Max[plane.Axis0], x);

            Min[plane.Axis1] = Math.Min(Min[plane.Axis1], y);
            Max[plane.Axis1] = Math.Max(Max[plane.Axis1], y);

            Min[plane.AxisLinear] = Math.Min(Min[plane.AxisLinear], z);
            Max[plane.AxisLinear] = Math.Max(Max[plane.AxisLinear], z);
        }
        public void AddPoint(GCPlane plane, Point3D point)
        {
            Min[plane.Axis0] = Math.Min(Min[plane.Axis0], point.X);
            Max[plane.Axis0] = Math.Max(Max[plane.Axis0], point.X);

            Min[plane.Axis1] = Math.Min(Min[plane.Axis1], point.Y);
            Max[plane.Axis1] = Math.Max(Max[plane.Axis1], point.Y);

            Min[plane.AxisLinear] = Math.Min(Min[plane.AxisLinear], point.Z);
            Max[plane.AxisLinear] = Math.Max(Max[plane.AxisLinear], point.Z);
        }

        public void AddPoint(Point3D point)
        {
            Min[0] = Math.Min(Min[0], point.X);
            Max[0] = Math.Max(Max[0], point.X);

            Min[1] = Math.Min(Min[1], point.Y);
            Max[1] = Math.Max(Max[1], point.Y);

            Min[2] = Math.Min(Min[2], point.Z);
            Max[2] = Math.Max(Max[2], point.Z);
        }
        public void AddPoint(Point3D point, AxisFlags axisflags)
        {
            if (axisflags.HasFlag(AxisFlags.X))
            {
                Min[0] = Math.Min(Min[0], point.X);
                Max[0] = Math.Max(Max[0], point.X);
            }

            if (axisflags.HasFlag(AxisFlags.Y))
            { 
                Min[1] = Math.Min(Min[1], point.Y);
                Max[1] = Math.Max(Max[1], point.Y);
            }

            if (axisflags.HasFlag(AxisFlags.Z))
            {
                Min[2] = Math.Min(Min[2], point.Z);
                Max[2] = Math.Max(Max[2], point.Z);
            }
        }

        public void AddBoundingBox(GcodeBoundingBox bbox)
        {
            AddPoint(bbox.Min[0], bbox.Min[1], bbox.Min[2]);
            AddPoint(bbox.Max[0], bbox.Max[1], bbox.Max[2]);
        }
    }
}
