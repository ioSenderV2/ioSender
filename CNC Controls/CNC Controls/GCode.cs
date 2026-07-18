/*
 * GCode.cs - part of CNC Controls library for Grbl
 *
 * v0.47 / 2026-02-11 / Io Engineering (Terje Io)
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

using CNC.Core;
using CNC.GCode;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public class GCode : IProgramSource
    {
        private struct GCodeConverter
        {
            public Type Type;
            public string FileType;
            public string FileExtensions;
        }
        private struct GCodeTransformer
        {
            public Type Type;
            public string Name;
        }

        public const string FileTypes = "cnc,nc,ncc,ngc,gcode,tap";

        private GCodeJob Program { get; set; } = new GCodeJob();
        private List<GCodeConverter> Converters = new List<GCodeConverter>();
        private List<GCodeTransformer> Transformers = new List<GCodeTransformer>();

        private static readonly Lazy<GCode> file = new Lazy<GCode>(() => new GCode());

        public event GCodeJob.ToolChangedHandler ToolChanged = null;

        private readonly bool _transient;

        // True for a macro/tool-generated run (RunStreamedJobInPlace) - a standalone program built with
        // AddBlock that is streamed WITHOUT becoming the loaded job. Lets CycleStart tell "the operator's
        // loaded job" apart from "a probing/wizard macro's own g-code", so job-tab-only features (dry-run
        // mode's Z offset + spindle/coolant suppression) don't leak into macro runs that never armed them.
        public bool IsTransient { get { return _transient; } }

        private GCode()
        {
            Program.FileChanged += Program_FileChanged;
            Program.ToolChanged += Program_ToolChanged;
        }

        // Create a standalone, TRANSIENT program for a tool's generated run. It is streamed via JobControl.Source
        // WITHOUT becoming the loaded job, so it must never mutate the shared Model (FileName / limits / Blocks)
        // or push a header to the simulator. Model is set so the streamer can drive it; build it with AddBlock.
        public GCode(GrblViewModel model)
        {
            _transient = true;
            Model = model;
            Program.FileChanged += Program_FileChanged;
            Program.ToolChanged += Program_ToolChanged;
        }

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
            // point both Load File/Load Folder funnel through (see the comment below), so it can't be missed
            // by loading via a different route.
            if (Model != null)
                Model.IsDryRunMode = false;

            // Rebuild the shared (TOOL ...)/(STOCK ...) comment lookup once per completed Load File/Load
            // Folder - this is the single point both funnel through (GCodeJob.FileChanged), so callers (e.g.
            // touch-plate probing's edge-radius compensation, CarveView's 3D carve simulation) never need to
            // re-scan the program themselves.
            GCodeProgramComments.Refresh();

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
        }

        // When connected to the simulator, send the program's leading comment lines (e.g. (STOCK X=..) and
        // (TOOL T=1 D=.. TYPE=..)) to it as soon as the program is loaded - so a start_job macro run *before*
        // the program already knows the stock size and tool table. Only the simulator consumes these comments;
        // a real controller ignores them. Stops at the first tool change / motion (the end of the header).
        private void PushHeaderToSimulator()
        {
            if (!AppConfig.Settings.Base.StartSimulator || Comms.com == null || !Comms.com.IsOpen)
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

        public static GCode File { get { return file.Value; } }
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

        public bool AddConverter(Type converter, string filetype, string fileextensions)
        {
            bool ok = converter.GetInterface("CNC.Controls.IGCodeConverter") != null;
            if (ok)
                Converters.Add(new GCodeConverter { Type = converter, FileType = filetype, FileExtensions = fileextensions });

            return ok;
        }

        private string getConversionTypes ()
        {
            string types = string.Empty;
            foreach (var converter in Converters)
                types += (types == string.Empty ? "" : ",") + converter.FileExtensions;

            return types;
        }

        public bool AddTransformer(Type converter, string name, ObservableCollection<MenuItem> menu)
        {
            bool ok = converter.GetInterface("CNC.Controls.IGCodeTransformer") != null;
            if (ok)
            {
                Transformers.Add(new GCodeTransformer { Type = converter, Name = name });

                MenuItem item = new MenuItem()
                {
                    Header = name,
                    Tag = menu.Count
                };

                item.Click += TransformMenu_Click;

                menu.Add(item);
            }

            return ok;
        }

        public bool HasTransformer(Type converter)
        {
            return Transformers.Where(x => x.Type == converter).FirstOrDefault().Type == converter;
        }

        // Registered transformer display names in Transform(id) index order. Lets a right-click menu build
        // its own Transform items fresh (menu overhaul) instead of sharing the single UIViewModel MenuItem set.
        public System.Collections.Generic.List<string> TransformerNames
        {
            get { return Transformers.Select(x => x.Name).ToList(); }
        }

        private void TransformMenu_Click(object sender, RoutedEventArgs e)
        {
            Transform((int)(sender as MenuItem).Tag);
        }

        public void Transform(int id)
        {
            if (Transformers.Count > id)
            {
                var loader = (IGCodeTransformer)Activator.CreateInstance(Transformers[id].Type);
                loader.Apply();
            }
        }

        public void AddBlock(string block, Core.Action action)
        {
            Program.AddBlock(block, action);

            if(action == Core.Action.End && !_transient)
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

        public void Drag(object sender, DragEventArgs e)
        {
            bool allow = Model != null && GrblParserState.IsLoaded && (Model.StreamingState == StreamingState.Idle || Model.StreamingState == StreamingState.NoFile);

            if (allow && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop, false);
                allow = files.Count() == 1 && FileUtils.IsAllowedFile(files[0].ToLower(), FileTypes + (getConversionTypes() == string.Empty ? "" : "," + getConversionTypes()) + ",txt");
            }

            e.Handled = true;
            e.Effects = allow ? DragDropEffects.Copy : DragDropEffects.None;
        }

        public void Drop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop, false);

            if (files.Count() == 1)
            {
                Load(files[0]);
            }
        }

        public void Close()
        {
            Program.CloseFile();
            if (Model != null)
                Model.HasOutline = false;
            Model.Blocks = Blocks;
        }

        public void Open()
        {
            string filename = string.Empty;
            OpenFileDialog file = new OpenFileDialog();

            string conversionFilter = string.Empty; //conversionTypes == string.Empty ? string.Empty : string.Format("Other files ({0})|{0}|", FileUtils.ExtensionsToFilter(conversionTypes));

            foreach (var converter in Converters)
                conversionFilter += string.Format("{0} ({1})|{1}|", converter.FileType, FileUtils.ExtensionsToFilter(converter.FileExtensions));

            file.Filter = string.Format("GCode files ({0})|{0}|{1}Text files (*.txt)|*.txt|All files (*.*)|*.*", FileUtils.ExtensionsToFilter(FileTypes), conversionFilter);

            if (file.ShowDialog() == true)
            {
                filename = file.FileName;
            }

            if(filename != string.Empty)
                Load(filename);

            Model.Blocks = Blocks;
        }

        // Read + parse a (potentially huge) program on a background thread so the rest of the UI stays responsive,
        // flushing parsed blocks onto the UI thread in batches so rows appear as the files are read. Only the
        // program view(s) show a Wait cursor (Model.IsLoading) while it runs. 'parse' runs on the worker thread -
        // it must route blocks through Program.BlockConsumer (done automatically by AddBlock/ParseFileLines) and
        // finish with Program.ComputeLimits(); 'onDone' runs on the UI thread after the final flush (raise
        // FileChanged, set Model.Blocks, ...).
        private async void BackgroundLoad(System.Action parse, System.Action onDone)
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var buffer = new List<GCodeBlock>(8192);
            const int BatchSize = 4000;

            if (Model != null)
                Model.IsLoading = true;

            // Worker-thread sink: buffer parsed blocks, marshalling a batch to the UI thread whenever it fills.
            // dispatcher.Invoke (synchronous) gives natural backpressure so the buffer can't run away on a huge file.
            Program.BlockConsumer = b =>
            {
                buffer.Add(b);
                if (buffer.Count >= BatchSize)
                {
                    var batch = buffer.ToArray();
                    buffer.Clear();
                    dispatcher.Invoke((System.Action)(() => { foreach (var x in batch) Program.Blocks.Add(x); }));
                }
            };

            try
            {
                await System.Threading.Tasks.Task.Run(parse);

                // Final flush - we resume here on the UI thread, so add straight to the bound collection.
                foreach (var x in buffer)
                    Program.Blocks.Add(x);

                onDone?.Invoke();
            }
            catch (Exception e)
            {
                AppDialogs.Show("Error loading program: " + e.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
            finally
            {
                Program.BlockConsumer = null;
                if (Model != null)
                    Model.IsLoading = false;
            }
        }

        public void Load(string filename)
        {
            if (Model != null && Model.IsLoading)
                return;   // a background load is already in progress - ignore a re-entrant request

            if (Model != null)
                Model.HasOutline = false;

            foreach (var converter in Converters)
            {
                var filetypes = converter.FileExtensions.Split(',');

                foreach (var filetype in filetypes) {
                    if (filename.EndsWith(filetype))
                    {
                        var loader = (IGCodeConverter)Activator.CreateInstance(converter.Type);
                        loader.LoadFile(File, filename);
                        return;
                    }
                }
            }

            // Read + parse on a background thread (see BackgroundLoad) so a large single file doesn't freeze the
            // UI. Clear + reset on the UI thread first; the per-line parse loop runs on the worker thread.
            Program.AddBlock(filename, Core.Action.New);
            bool addLineNumbers = GrblInfo.UseLinenumbers && AppConfig.Settings.Base.AddLineNumbers;
            bool[] ok = { true };

            BackgroundLoad(() =>
            {
                ok[0] = Program.ParseFileLines(filename, addLineNumbers);
                if (ok[0])
                    Program.ComputeLimits();
            },
            () =>
            {
                if (ok[0])
                {
                    Program.RaiseFileChanged();
                    // Recognizes the Fusion add-in's (--- seq: name (Tn) ---) section markers the same way
                    // Load Folder's stitching does (GCodeJob.ParseFileLines calls BeginSection on a match) -
                    // an ordinary file with no such markers leaves this false, same as before.
                    Model.HasOutline = Program.HasSections;
                    Model.Blocks = Blocks;
                }
                else
                    Close();   // aborted mid-parse: discard the partial load
            });
        }

        public void Save()
        {
            SaveFileDialog saveDialog = new SaveFileDialog()
            {
                Filter = "GCode file (*.nc)|*.nc",
                AddExtension = true,
                DefaultExt = ".nc",
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    //using (new UIUtils.WaitCursor())
                    //{
                    //    GCodeParser.Save(saveDialog.FileName, GCodeParser.TokensToGCode(File.Tokens));
                    //}

                    using (StreamWriter stream = new StreamWriter(saveDialog.FileName))
                    {
                        using (new UIUtils.WaitCursor())
                        {
                            foreach (var line in Program.Blocks)
                                stream.WriteLine(line.Data);
                        }
                    }
                }
                catch (IOException)
                {
                }

                Model.FileName = saveDialog.FileName;
            }
        }
    }
}
