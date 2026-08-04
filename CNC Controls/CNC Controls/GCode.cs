/*
 * GCode.cs - part of CNC Controls library for Grbl
 *
 * v0.47 / 2026-02-11 / Io Engineering (Terje Io)
 *
 * The desktop client's G-code program. Everything about a loaded program that does not depend on a
 * WPF desktop lives in the base class, CNC.Core.GCodeProgram - the GCodeJob model, the load pipeline,
 * the completion wiring that keeps GrblViewModel in step. What is left here is what talks to the
 * operator's machine rather than to the CNC one:
 *
 *   - the File singleton. A desktop client really does have exactly one loaded program, so it is right
 *     here and wrong in Core, where a server holds one program per session.
 *   - Open()/Save() - Microsoft.Win32 file dialogs, the remembered folder, the wait cursor.
 *   - Drag()/Drop() - WPF DragEventArgs.
 *   - the converter/transformer plug-in registry. IGCodeConverter/IGCodeTransformer and
 *     CNC.Converters.dll are client assemblies, and AddTransformer literally builds WPF MenuItems.
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
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public class GCode : GCodeProgram
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

        private List<GCodeConverter> Converters = new List<GCodeConverter>();
        private List<GCodeTransformer> Transformers = new List<GCodeTransformer>();

        private static readonly Lazy<GCode> file = new Lazy<GCode>(() => new GCode());

        private GCode()
        {
        }

        // Create a standalone, TRANSIENT program for a tool's generated run - see GCodeProgram's ctor.
        public GCode(GrblViewModel model) : base(model)
        {
        }

        public static GCode File { get { return file.Value; } }

        // The two host settings GCodeProgram's load pipeline asks for. App.config is client-side (Core has
        // no dependency on AppConfig at all), so this is where they are read - live on each call, so a
        // change on the Settings tab applies to the next load without any re-registration.
        protected override bool PushSimulatorHeaderEnabled { get { return AppConfig.Settings.Base.StartSimulator; } }
        protected override bool NumberLoadedLines { get { return AppConfig.Settings.Base.AddLineNumbers; } }

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

        // Hand the file to a registered converter when its extension matches one - see GCodeProgram.Load,
        // which calls this after its own re-entrancy guard so a converted load is gated exactly as a plain
        // one is. The converter does the loading itself (through this same instance) and Load stops there.
        protected override bool LoadViaConverter(string filename)
        {
            foreach (var converter in Converters)
            {
                var filetypes = converter.FileExtensions.Split(',');

                foreach (var filetype in filetypes) {
                    if (filename.EndsWith(filetype))
                    {
                        var loader = (IGCodeConverter)Activator.CreateInstance(converter.Type);
                        loader.LoadFile(this, filename);
                        return true;
                    }
                }
            }

            return false;
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

        public void Open()
        {
            string filename = string.Empty;
            // Explicit InitialDirectory, not left blank: with no directory set at all, this dialog falls
            // back to Windows' shared "last folder used in this process" - which Work Order's Load/Save
            // dialogs (WorkOrderView) explicitly pin to the Work Orders folder every time. Without its own
            // remembered folder, Load File would silently start opening in the Work Orders folder right
            // after a Load/Save Work Order (net462's Microsoft.Win32.OpenFileDialog has no ClientGuid to
            // give dialogs independent identities the way later frameworks do - this is the available fix).
            string lastFolder = AppConfig.Settings.Base.LastGCodeFolder;
            OpenFileDialog file = new OpenFileDialog
            {
                InitialDirectory = !string.IsNullOrEmpty(lastFolder) && Directory.Exists(lastFolder) ? lastFolder : string.Empty
            };

            string conversionFilter = string.Empty; //conversionTypes == string.Empty ? string.Empty : string.Format("Other files ({0})|{0}|", FileUtils.ExtensionsToFilter(conversionTypes));

            foreach (var converter in Converters)
                conversionFilter += string.Format("{0} ({1})|{1}|", converter.FileType, FileUtils.ExtensionsToFilter(converter.FileExtensions));

            file.Filter = string.Format("GCode files ({0})|{0}|{1}Text files (*.txt)|*.txt|All files (*.*)|*.*", FileUtils.ExtensionsToFilter(FileTypes), conversionFilter);

            if (file.ShowDialog() == true)
            {
                filename = file.FileName;
            }

            if (filename != string.Empty)
            {
                Load(filename);
                AppConfig.Settings.Base.LastGCodeFolder = System.IO.Path.GetDirectoryName(filename);
                AppConfig.Settings.Save();
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
                            foreach (var line in Data)
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
