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
    public class GCode
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

        private GCode()
        {
            Program.FileChanged += Program_FileChanged;
            Program.ToolChanged += Program_ToolChanged;
        }

        private bool Program_ToolChanged(int toolNumber)
        {
            return ToolChanged == null ? true : ToolChanged(toolNumber);
        }

        private void Program_FileChanged(string filename)
        {
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

            if(action == Core.Action.End)
                Model.Blocks = Blocks;
        }

        public void AddBlock(string block)
        {
            Program.AddBlock(block);
        }

        public void ClearStatus()
        {
            foreach (var row in Program.Blocks)
                if (row.Sent != string.Empty)
                    row.Sent = string.Empty;
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
                Model.IsFolderView = false;
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

        // Load a folder of per-toolpath .nc files (named <seq>_<name>_T<tool>.nc, as produced
        // by the SRWCommands Fusion add-in) and combine them, in memory, into one program shown
        // as an expandable per-toolpath outline. See FusionFolderLoader for the combine rules.
        public void OpenFolder()
        {
            // Modern folder picker (IFileOpenDialog with FOS_PICKFOLDERS) - same dialog family as
            // File > Load, in folder-select mode, and it remembers the last-used location.
            string folder = FolderPicker.Select("Select the folder of per-toolpath files (<seq>_<name>_T<tool>.nc)");

            if (string.IsNullOrEmpty(folder))
                return;

            bool restoreRapids = MessageBox.Show(
                "Restore rapid moves that Fusion Personal Use downgraded to feed moves (G1 → G0)?",
                "Load Folder", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;

            LoadFolder(folder, restoreRapids);
        }

        public void LoadFolder(string folder, bool restoreRapids)
        {
            var ops = FusionFolderLoader.MatchFolder(folder);

            if (ops.Count == 0)
            {
                MessageBox.Show("No per-toolpath files matching <seq>_<name>_T<tool>.nc were found in the selected folder.",
                                "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            // Combine can be a big (multi-MB / 100k+ line) program. Keep the list ungrouped while
            // bulk-adding so the bound DataGrid stays cheap, and pump messages periodically so the
            // STA thread doesn't trip a ContextSwitchDeadlock; group once at the end.
            if (Model != null)
                Model.IsFolderView = false;

            using (new UIUtils.WaitCursor())
            {
                // Use the folder's leaf name as the "filename" - not a real path, so the
                // sender won't treat it as a reloadable on-disk single file.
                Program.AddBlock(new DirectoryInfo(folder.TrimEnd('\\', '/')).Name, Core.Action.New);

                // Match a normal file load: only prepend N<line> numbers when the user has enabled
                // both controller line numbers and the "add line numbers" option.
                Program.AddLineNumbers = GrblInfo.UseLinenumbers && AppConfig.Settings.Base.AddLineNumbers;

                // File prolog (also re-sent before a toolpath when starting a run partway through).
                Program.BeginSection("Program start");
                foreach (var line in FusionFolderLoader.Prolog)
                    Program.AddBlock(line);

                // Tool table (e.g. 0_tooltable.nc): loaded first, verbatim, so its (TOOL ...) comments reach
                // the controller / simulator before the first tool change. Comments are preserved here (the
                // per-op files below still get their headers stripped).
                var toolTablePath = FusionFolderLoader.MatchToolTable(folder);
                if (toolTablePath != null)
                {
                    try
                    {
                        var ttLines = FusionFolderLoader.ReadPreservingComments(System.IO.File.ReadAllText(toolTablePath));
                        if (ttLines.Count > 0)
                        {
                            Program.BeginSection("Tool table");
                            foreach (var line in ttLines)
                                Program.AddBlock(line);
                        }
                    }
                    catch { }
                }

                int sincePump = 0;
                foreach (var op in ops)
                {
                    string content;
                    try
                    {
                        content = System.IO.File.ReadAllText(op.FilePath);
                    }
                    catch
                    {
                        continue;
                    }

                    if (restoreRapids)
                    {
                        int n;
                        content = FusionFolderLoader.RestoreRapids(content, out n);
                        op.RapidsRestored = n;
                    }

                    op.Body = FusionFolderLoader.StripWrappers(content);

                    Program.BeginSection(op.Section);
                    Program.AddBlock("G53 G0 Z0");          // machine-coord safe-Z retract before the tool change
                    // Only insert a tool change if the posted file doesn't already have one
                    // (e.g. grbl.cps omits M6, but an M6-emitting post includes it - avoid doubling).
                    if (!FusionFolderLoader.ContainsToolChange(op.Body))
                        Program.AddBlock("M6 T" + op.Tool); // M0 swap-pause is handled by the controller's tc.macro
                    foreach (var line in op.Body)
                    {
                        Program.AddBlock(line);
                        if (++sincePump >= 2000)
                        {
                            sincePump = 0;
                            EventUtils.DoEvents();
                        }
                    }
                }

                Program.BeginSection("Program end");
                Program.AddBlock("M5");
                Program.AddBlock("M30");

                Program.AddBlock("", Core.Action.End);
            }

            if (Model != null)
            {
                Model.IsFolderView = true;
                Model.Blocks = Blocks;
            }
        }

        public void Load(string filename)
        {
            if (Model != null)
                Model.IsFolderView = false;

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

            using (new UIUtils.WaitCursor())
            {
                Program.LoadFile(filename, GrblInfo.UseLinenumbers && AppConfig.Settings.Base.AddLineNumbers);
            }

            Model.Blocks = Blocks;
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
