/*
 * SDCardView.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.47 / 2026-03-10 / Io Engineering (Terje Io)
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

using System.Collections.Generic;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Net;
using Microsoft.Win32;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for SDCardView.xaml
    /// </summary>
    public partial class SDCardView : UserControl, ICNCView
    {
        public delegate void FileSelectedHandler(string filename, bool rewind);
        public event FileSelectedHandler FileSelected;

        private DataRow currentFile = null;

        public SDCardView()
        {
            InitializeComponent();
            ctxMenu.DataContext = this;
        }

        #region Methods and properties required by IRenderer interface

        public ViewType ViewType { get { return ViewType.SDCard; } }
        public bool CanEnable { get { return !(DataContext as GrblViewModel).IsGCLock; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                if (Comms.com == null || !Comms.com.IsOpen)
                {
                    GrblSDCard.Clear();
                    if (txtFreeSpace != null)
                        txtFreeSpace.Text = string.Empty;
                    (DataContext as GrblViewModel).Message = (string)FindResource("NoConnection");
                    return;
                }

                // Allow upload to any mounted filesystem: an SD card must be present (mounted), but a LittleFS-only
                // controller (HasFS without HasSDCard) has no SD mount status, so don't require one there.
                CanUpload = GrblInfo.UploadProtocol != string.Empty && GrblInfo.HasFS
                            && (!GrblInfo.HasSDCard || (DataContext as GrblViewModel).SDCardMountStatus != SDState.Undetected);
                CanDelete = GrblInfo.Build >= 20210421;
                CanViewAll = GrblInfo.Build >= 20230312;
                CanRewind = GrblInfo.IsGrblHAL;

                if (GrblInfo.HasSDCard && (DataContext as GrblViewModel).SDCardMountStatus == SDState.Undetected)
                {
                    GrblSDCard.Clear();
                    if (txtFreeSpace != null)
                        txtFreeSpace.Text = string.Empty;
                    (DataContext as GrblViewModel).Message = (string)FindResource("NoCard");
                } else
                    ReloadFiles();
            }
            else
                (DataContext as GrblViewModel).Message = string.Empty;
        }

        public void CloseFile()
        {
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
        }

        #endregion

        #region Dependency properties

        public static readonly DependencyProperty RewindProperty = DependencyProperty.Register(nameof(Rewind), typeof(bool), typeof(SDCardView), new PropertyMetadata(false));
        public bool Rewind
        {
            get { return (bool)GetValue(RewindProperty); }
            set { SetValue(RewindProperty, value); }
        }

        public static readonly DependencyProperty CanRewindProperty = DependencyProperty.Register(nameof(CanRewind), typeof(bool), typeof(SDCardView), new PropertyMetadata(false));
        public bool CanRewind
        {
            get { return (bool)GetValue(CanRewindProperty); }
            set { SetValue(CanRewindProperty, value); }
        }

        public static readonly DependencyProperty ViewAllProperty = DependencyProperty.Register(nameof(ViewAll), typeof(bool), typeof(SDCardView), new PropertyMetadata(false));
        public bool ViewAll
        {
            get { return (bool)GetValue(ViewAllProperty); }
            set { SetValue(ViewAllProperty, value); }
        }

        public static readonly DependencyProperty CanViewAllProperty = DependencyProperty.Register(nameof(CanViewAll), typeof(bool), typeof(SDCardView), new PropertyMetadata(false));
        public bool CanViewAll
        {
            get { return (bool)GetValue(CanViewAllProperty); }
            set { SetValue(CanViewAllProperty, value); }
        }

        public static readonly DependencyProperty CanUploadProperty = DependencyProperty.Register(nameof(CanUpload), typeof(bool), typeof(SDCardView), new PropertyMetadata(false));
        public bool CanUpload
        {
            get { return (bool)GetValue(CanUploadProperty); }
            set { SetValue(CanUploadProperty, value); }
        }

        public static readonly DependencyProperty CanDeleteProperty = DependencyProperty.Register(nameof(CanDelete), typeof(bool), typeof(SDCardView), new PropertyMetadata(false));
        public bool CanDelete
        {
            get { return (bool)GetValue(CanDeleteProperty); }
            set { SetValue(CanDeleteProperty, value); }
        }

        #endregion

        private void SDCardView_Loaded(object sender, RoutedEventArgs e)
        {
            dgrSDCard.DataContext = GrblSDCard.Files;
            //      dgrSDCard.SelectedIndex = 0;
        }

        void dgrSDCard_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            currentFile = e.AddedItems.Count == 1 ? ((DataRowView)e.AddedItems[0]).Row : null;
        }

        private void dgrSDCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            RunFile();
        }

        private void AddBlock(string data)
        {
            GCode.File.AddBlock(data);
        }

        private bool isMacro (string filename)
        {
            return filename.ToLower().EndsWith(".macro");
        }

        // Controller path for $F= / $FD= / $F<= on the selected row's filesystem (bare name on the
        // root volume - unchanged single-SD behaviour - or an absolute path on a sub-mount).
        private static string TargetName(DataRow row)
        {
            return GrblFilesystems.QualifiedName(row["Path"] as string, (string)row["Name"]);
        }

        // (Re)list the controller files and refresh the free-space banner.
        private void ReloadFiles()
        {
            GrblSDCard.Load(DataContext as GrblViewModel, ViewAll);
            if (txtFreeSpace != null)
                txtFreeSpace.Text = GrblSDCard.FreeSpace;
        }

        private void Load_Click(object sender, RoutedEventArgs e) { LoadFile(false); }
        private void LoadRun_Click(object sender, RoutedEventArgs e) { LoadFile(true); }

        // Download the selected file into ioSender's program view; when run, also start it on the controller
        // (after a confirm, since it moves the machine).
        private void LoadFile(bool run)
        {
            if (currentFile == null || (string)currentFile["Dir"] == GrblSDCard.EmptyMountMarker || (int)currentFile["Size"] <= 0)
                return;

            if (run && MessageBox.Show(string.Format((string)FindResource("DownloandRun"), (string)currentFile["Name"]), "ioSender",
                                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return;

            var model = DataContext as GrblViewModel;

            using (new UIUtils.WaitCursor())
            {
                bool? res = null;
                CancellationToken cancellationToken = new CancellationToken();

                Comms.com.PurgeQueue();

                model.SuspendProcessing = true;
                model.Message = string.Format((string)FindResource("Downloading"), (string)currentFile["Name"]);

                GCode.File.AddBlock((string)currentFile["Name"], CNC.Core.Action.New);

                new Thread(() =>
                {
                    res = WaitFor.AckResponse<string>(
                        cancellationToken,
                        response => AddBlock(response),
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        400, () => Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_DUMP + TargetName(currentFile)));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();

                model.SuspendProcessing = false;

                GCode.File.AddBlock(string.Empty, CNC.Core.Action.End);
            }

            model.Message = string.Empty;

            if (Rewind)
                Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_REWIND);

            FileSelected?.Invoke("SDCard:" + (string)currentFile["Name"], Rewind);
            if (run)
                Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_RUN + TargetName(currentFile));

            Rewind = false;
        }

        // ---- View / Edit / Create: hand the text-file app (Notepad) a copy of the content. ----------------

        private void View_Click(object sender, RoutedEventArgs e)
        {
            if (currentFile == null || (string)currentFile["Dir"] == GrblSDCard.EmptyMountMarker)
                return;
            OpenInEditor((string)currentFile["Name"], ReadFileContent(currentFile), false, null);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (currentFile == null || (string)currentFile["Dir"] == GrblSDCard.EmptyMountMarker)
                return;
            OpenInEditor((string)currentFile["Name"], ReadFileContent(currentFile), true, currentFile["Path"] as string);
        }

        private void CreateNew_Click(object sender, RoutedEventArgs e)
        {
            string name = PromptFileName("Create file", "new.nc");
            if (string.IsNullOrEmpty(name))
                return;
            // New file goes to the selected row's filesystem, else the root volume.
            string destPath = currentFile != null ? currentFile["Path"] as string : "/";
            OpenInEditor(name, string.Empty, true, destPath);
        }

        private void CreateFromFile_Click(object sender, RoutedEventArgs e)
        {
            Upload_Click(sender, e);   // pick a local file and upload it (to the selected row's filesystem)
        }

        // Read a controller file's full text content via $F<= (dump). Pumps events like the download path.
        private string ReadFileContent(DataRow row)
        {
            var model = DataContext as GrblViewModel;
            var sb = new System.Text.StringBuilder();
            bool? res = null;
            CancellationToken ct = new CancellationToken();

            using (new UIUtils.WaitCursor())
            {
                Comms.com.PurgeQueue();
                model.SuspendProcessing = true;
                model.Message = string.Format((string)FindResource("Downloading"), (string)row["Name"]);

                new Thread(() =>
                {
                    res = WaitFor.AckResponse<string>(
                        ct,
                        response => { if (response != "ok" && !response.StartsWith("error") && !response.StartsWith("[")) sb.AppendLine(response); },
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        400, () => Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_DUMP + TargetName(row)));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();

                model.SuspendProcessing = false;
            }

            model.Message = string.Empty;
            return sb.ToString();
        }

        // Write content to a temp file and open it in Notepad. When block is true (Edit/Create) wait off-thread
        // for Notepad to close, then upload the (edited) temp to destPath and reload; View just opens it.
        private void OpenInEditor(string fileName, string content, bool block, string destPath)
        {
            string baseName = System.IO.Path.GetFileName(fileName);   // controller names may carry a leading '/'
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ioSenderFiles");

            string temp;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                temp = System.IO.Path.Combine(dir, baseName);
                System.IO.File.WriteAllText(temp, content ?? string.Empty);
            }
            catch (System.Exception ex) { (DataContext as GrblViewModel).Message = ex.Message; return; }

            if (!block)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + temp + "\"") { UseShellExecute = false }); }
                catch (System.Exception ex) { (DataContext as GrblViewModel).Message = ex.Message; }
                return;
            }

            var model = DataContext as GrblViewModel;
            model.Message = string.Format("Editing {0} - close Notepad to save...", baseName);

            new Thread(() =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + temp + "\"") { UseShellExecute = false }).WaitForExit(); }
                catch { }

                Dispatcher.Invoke(() =>
                {
                    UploadLocalFile(temp, destPath);
                    try { System.IO.File.Delete(temp); } catch { }
                    ReloadFiles();
                });
            }).Start();
        }

        // Copy (or move) the selected file to another mounted filesystem: read it, upload to the destination,
        // and for a move delete the source. Same base name on the destination.
        private void CopyOrMove(DataRow row, FsMount dest, bool move)
        {
            if (row == null || dest == null)
                return;

            string content = ReadFileContent(row);
            string baseName = System.IO.Path.GetFileName((string)row["Name"]);
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ioSenderFiles");
            bool ok = false;

            try
            {
                System.IO.Directory.CreateDirectory(dir);
                string temp = System.IO.Path.Combine(dir, baseName);
                System.IO.File.WriteAllText(temp, content);
                ok = UploadLocalFile(temp, dest.Path);
                try { System.IO.File.Delete(temp); } catch { }
            }
            catch (System.Exception ex) { (DataContext as GrblViewModel).Message = ex.Message; }

            if (ok && move)
                Grbl.WaitForResponse(GrblConstants.CMD_SDCARD_UNLINK + TargetName(row));

            ReloadFiles();
        }

        // Minimal modal text prompt (no input dialog exists in the app).
        private string PromptFileName(string title, string initial)
        {
            var dlg = new Window {
                Title = title, Width = 320, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this)
            };
            var panel = new StackPanel { Margin = new Thickness(10) };
            var box = new TextBox { Text = initial ?? string.Empty, Margin = new Thickness(0, 0, 0, 8) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            string result = null;
            ok.Click += (s, e) => { result = box.Text.Trim(); dlg.DialogResult = true; };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            panel.Children.Add(box); panel.Children.Add(btns);
            dlg.Content = panel;
            box.Focus(); box.SelectAll();
            return dlg.ShowDialog() == true && !string.IsNullOrEmpty(result) ? result : null;
        }

        // Enable the file actions only for a real selected file (not an empty-mount placeholder), and rebuild
        // the Copy To / Move To submenus from the currently mounted filesystems.
        private void ctxMenu_Opened(object sender, RoutedEventArgs e)
        {
            bool isFile = currentFile != null && (string)currentFile["Dir"] != GrblSDCard.EmptyMountMarker;

            mnuView.IsEnabled = mnuEdit.IsEnabled = mnuLoad.IsEnabled = mnuLoadRun.IsEnabled = isFile;
            mnuCopyTo.IsEnabled = mnuMoveTo.IsEnabled = mnuDelete.IsEnabled = isFile;

            BuildCopyMoveSubmenu(mnuCopyTo, false);
            BuildCopyMoveSubmenu(mnuMoveTo, true);
        }

        private void BuildCopyMoveSubmenu(MenuItem parent, bool move)
        {
            parent.Items.Clear();

            string srcPath = currentFile != null ? currentFile["Path"] as string : null;
            DataRow row = currentFile;

            foreach (FsMount fs in GrblSDCard.Mounts)
            {
                if (fs.Path == srcPath)   // skip the file's own filesystem
                    continue;
                var item = new MenuItem { Header = fs.Name, Tag = fs };
                item.Click += (s, ev) => CopyOrMove(row, (FsMount)((MenuItem)s).Tag, move);
                parent.Items.Add(item);
            }

            if (parent.Items.Count == 0)
                parent.Items.Add(new MenuItem { Header = "(no other filesystem)", IsEnabled = false });
        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            string filename = string.Empty;
            OpenFileDialog file = new OpenFileDialog();

            file.Filter = string.Format("GCode files ({0})|{0}|GCode macros (*.macro)|*.macro|Text files (*.txt)|*.txt|All files (*.*)|*.*", FileUtils.ExtensionsToFilter(GCode.FileTypes));

            if (file.ShowDialog() == true)
            {
                filename = file.FileName;
            }

            if (filename != string.Empty)
            {
                UploadLocalFile(filename, currentFile != null ? currentFile["Path"] as string : null);
                ReloadFiles();
            }
        }

        // Upload a local file to the controller, targeting destPath's filesystem (root if null/"/"); the
        // controller file name is the local base name. Sets status messages and returns success; the caller
        // reloads. Reused by Upload / Edit-save / Create / Copy / Move.
        // Upload any missing ATC support macros to the controller filesystem, then refresh the listing so they
        // show. Reuses this tab's UploadLocalFile so the transfer (target filesystem, FTP/YModem) is the proven
        // path. Driven by the prompt in MainWindow.CheckAtcMacros after the user accepts.
        public AtcMacros.ProvisionResult ProvisionAtcMacros(System.Func<AtcMacros.UpdateReason, bool> confirmUpload)
        {
            // Use the authoritative shared view model, NOT this view's DataContext: when provisioning runs at
            // connect the SD Card tab may not have been realized yet, so DataContext is still null and
            // EnsureProvisioned(null, ...) returns Skipped - which is why the prompt only appeared after the tab
            // was opened. Grbl.GrblViewModel is always set once connected.
            GrblViewModel model = Grbl.GrblViewModel;
            try
            {
                var result = AtcMacros.EnsureProvisioned(model, UploadLocalFile, confirmUpload);
                ReloadFiles();
                return result;
            }
            catch (System.Exception ex)
            {
                // Never let a provisioning hiccup reach the app's global handler (its modal error box pumps the
                // dispatcher and can turn one fault into a cascade); surface it on the status bar instead.
                if (model != null)
                    model.Message = "ATC macro upload failed: " + ex.Message;
                return AtcMacros.ProvisionResult.Failed;
            }
        }

        private bool UploadLocalFile(string localPath, string destPath)
        {
            GrblViewModel model = Grbl.GrblViewModel ?? DataContext as GrblViewModel;   // provisioning can run before this view is realized
            bool ok = false;

            model.Message = (string)FindResource("Uploading");

            // $CWD makes both the FTP path (re-queried via PWD below) and the YModem write land on the chosen
            // filesystem; the caller's ReloadFiles restores the root afterwards.
            if (!string.IsNullOrEmpty(destPath) && destPath != "/")
                Grbl.WaitForResponse("$CWD=" + destPath.TrimEnd('/'));

            if (GrblInfo.UploadProtocol == "FTP")
            {
                if (GrblInfo.IpAddress == string.Empty)
                    model.Message = (string)FindResource("NoConnection");
                else using(new UIUtils.WaitCursor())
                {
                    model.Message = (string)FindResource("Uploading");

                    if (GrblInfo.Build > 20260308)
                    {
                        bool? res = null;
                        CancellationToken cancellationToken = new CancellationToken();

                        Comms.com.PurgeQueue();

                        new Thread(() =>
                        {
                            res = WaitFor.AckResponse<string>(
                                cancellationToken,
                                null,
                                a => model.OnResponseReceived += a,
                                a => model.OnResponseReceived -= a,
                                300, () => Comms.com.WriteCommand(GrblConstants.CMD_FS_PWD));
                        }).Start();

                        while (res == null)
                            EventUtils.DoEvents();
                    }

                    try
                    {
                        using (WebClient client = new WebClient())
                        {
                            int port = GrblSettings.GetInteger(grblHALSetting.FtpPort0);
                            if(port == -1)
                                port = GrblSettings.GetInteger(grblHALSetting.FtpPort1);
                            if (port == -1)
                                port = GrblSettings.GetInteger(grblHALSetting.FtpPort2);
                            // Build the FTP path from the explicit destination filesystem when known ($CWD was
                            // issued to it above), falling back to FsCwd. FsCwd is only refreshed by the PWD
                            // re-query above on newer builds, so relying on it alone uploaded to the stale root
                            // (e.g. the absent SD "/") on older firmware - "directory not found".
                            string dir = string.IsNullOrEmpty(destPath) ? model.FsCwd : destPath;
                            string path = string.Format("{0}{1}{2}", dir, dir.EndsWith("/") ? "" : "/", System.IO.Path.GetFileName(localPath));

                            client.Credentials = new NetworkCredential("grblHAL", "grblHAL");
                            client.UploadFile(string.Format("ftp://{0}:{1}{2}", GrblInfo.IpAddress, port == -1 ? 21 : port, path), WebRequestMethods.Ftp.UploadFile, localPath);
                            ok = true;
                        }
                    }
                    catch (WebException ex)
                    {
                        model.Message = ex.Message.ToString() + " " + ((FtpWebResponse)ex.Response).StatusDescription;
                    }
                    catch (System.Exception ex)
                    {
                        model.Message = ex.Message.ToString();
                    }
                }
            }
            else
            {
                YModem ymodem = new YModem();
                ymodem.DataTransferred += Ymodem_DataTransferred;
                ok = ymodem.Upload(localPath);
            }

            if(!(GrblInfo.UploadProtocol == "FTP" && !ok))
                model.Message = (string)FindResource(ok ? "TransferDone" : "TransferAborted");

            return ok;
        }

        private void Ymodem_DataTransferred(long size, long transferred)
        {
            GrblViewModel model = DataContext as GrblViewModel;
            model.Message = string.Format((string)FindResource("Transferring"), transferred, size);
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            RunFile();
        }

        private void ViewAll_Click(object sender, RoutedEventArgs e)
        {
            ReloadFiles();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (currentFile == null || (string)currentFile["Dir"] == GrblSDCard.EmptyMountMarker)
                return;

            if (MessageBox.Show(string.Format((string)FindResource("DeleteFile"), (string)currentFile["Name"]), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            {
                if(Grbl.WaitForResponse(GrblConstants.CMD_SDCARD_UNLINK + TargetName(currentFile)))
                    ReloadFiles();
            }
        }

        private void RunFile()
        {
            if (currentFile != null && (string)currentFile["Dir"] != GrblSDCard.EmptyMountMarker)
            {
                (DataContext as GrblViewModel).Message = string.Empty;

                if ((bool)currentFile["Invalid"])
                {
                    MessageBox.Show(string.Format(((string)FindResource("IllegalName")).Replace("\\n", "\r\r"), (string)currentFile["Name"]), "ioSender",
                                     MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if((int)currentFile["Size"] == -1) {
                    if(Grbl.WaitForResponse(GrblConstants.CMD_SDCARD_RUN + TargetName(currentFile)))
                        ReloadFiles();
                }
                else
                {
                    if (GrblInfo.ExpressionsSupported && isMacro((string)currentFile["Name"])) {
                        string filename = ((string)currentFile["Name"]).ToLower();
                        filename = filename.Substring(0, filename.LastIndexOf(".macro"));
                        int pos = filename.LastIndexOf("p");
                        if(pos >= 0)
                        {
                            int macro;
                            if(int.TryParse(filename.Substring(pos + 1), out macro) && macro >= 100)
                            {
                                if(MessageBox.Show(string.Format((string)FindResource("RunMacro"), macro), "ioSender",
                                                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                                {
                                    Comms.com.WriteCommand("G65P" + macro.ToString());
                                }

                            }
                        }
                        return;
                    }
                    if (Rewind)
                    {
                        Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_REWIND);
                    }
                    FileSelected?.Invoke("SDCard:" + (string)currentFile["Name"], Rewind);
                    Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_RUN + TargetName(currentFile));
                    Rewind = false;
                }
            }
        }
    }

    public static class GrblSDCard
    {
        private static DataTable data;
        private static int id = 0;
        private static GrblViewModel grbl;
        private static string curLocation = string.Empty, curPath = string.Empty;

        // One-line free-space banner across the mounted filesystems (set on each Load).
        public static string FreeSpace { get; private set; }

        // Dir-column marker for an empty-filesystem placeholder row (a mount with no files), so the browser
        // can show it and use it as an upload target while run/delete skip it.
        public const string EmptyMountMarker = "fs";

        // Filesystems reported by the last $FI (for the Copy To / Move To submenus). Empty on legacy single-FS.
        public static List<FsMount> Mounts { get; private set; } = new List<FsMount>();

        static GrblSDCard()
        {
            data = new DataTable("Filelist");

            data.Columns.Add("Id", typeof(int));
            data.Columns.Add("Dir", typeof(string));
            data.Columns.Add("Name", typeof(string));
            data.Columns.Add("Size", typeof(int));
            data.Columns.Add("Invalid", typeof(bool));
            data.Columns.Add("Location", typeof(string));  // which filesystem the file lives on (SD / littlefs)
            data.Columns.Add("Path", typeof(string));       // that filesystem's mount path, for $F=/$FD=/$F<=
            data.PrimaryKey = new DataColumn[] { data.Columns["Id"] };

            FreeSpace = string.Empty;
        }

        public static DataView Files { get { return data.DefaultView; } }
        public static bool Loaded { get { return data.Rows.Count > 0; } }

        public static void Clear()
        {
            data.Clear();
            FreeSpace = string.Empty;
        }

        private static bool _loading;

        public static void Load(GrblViewModel model, bool ViewAll)
        {
            // Load pumps the dispatcher (EventUtils.DoEvents) while waiting on the controller, so a refresh
            // queued meanwhile - tab activation, ATC provisioning - can re-enter and clear/rebuild the shared
            // DataTable mid-listing. That corrupted the parse state and cascaded into unhandled exceptions
            // (and a stack overflow via the modal error handler). Ignore re-entrant calls.
            if (_loading)
                return;
            _loading = true;

            try
            {
                grbl = model;

                data.Clear();
                FreeSpace = string.Empty;
                id = 0;

                // No point talking to the controller if the link is down or reconnecting - this
                // also avoids serial I/O exceptions when the SD tab is opened after a disconnect.
                // The user-facing message is set by the caller (SDCardView.Activate).
                if (Comms.com == null || !Comms.com.IsOpen)
                    return;

                // Prefer $FI: it enumerates every mounted filesystem (SD and/or LittleFS) so they can
                // be shown together with a Location column and per-FS free space. Builds that do not
                // implement $FI (or that report nothing mounted) return no [FS:...] lines; in that case
                // fall back to the original single-filesystem listing so behaviour is unchanged.
                var mounts = GetMounts(model);
                Mounts = mounts;

                if (mounts.Count > 0)
                {
                    FreeSpace = GrblFilesystems.FreeSpaceSummary(mounts);

                    foreach (var mount in mounts)
                    {
                        int before = data.Rows.Count;
                        ListMount(model, mount.Name, mount.Path, ViewAll);
                        if (data.Rows.Count == before)
                            // Empty filesystem: add a non-file placeholder so the mount stays visible and can be
                            // selected as an upload target. Marked via the (hidden, otherwise unused) Dir column so
                            // run/delete skip it (see SDCardView), and Upload targets its mount path.
                            data.Rows.Add(new object[] { id++, EmptyMountMarker, "(no files)", 0, false, mount.Name, mount.Path });
                    }

                    // Leave the working directory on a valid mount. Restoring to "/" errors (error:63 - Directory
                    // not found) when the root filesystem isn't mounted - e.g. SD enabled but no card inserted, with
                    // littlefs at /littlefs - so only use "/" when a mount actually lives there.
                    string cwd = mounts.Exists(m => m.Path == "/") ? "/" : mounts[0].Path;
                    Grbl.WaitForResponse("$CWD=" + (cwd.Length > 1 ? cwd.TrimEnd('/') : cwd));
                }
                else
                    LegacyLoad(model, ViewAll);

                data.AcceptChanges();
            }
            finally
            {
                _loading = false;
            }
        }

        // Enumerate mounted filesystems via $FI. Empty list => $FI unsupported or nothing mounted
        // (error:65), which steers Load() to the legacy single-filesystem path.
        private static List<FsMount> GetMounts(GrblViewModel model)
        {
            var mounts = new List<FsMount>();
            bool? res = null;
            var ct = new CancellationToken();

            Comms.com.PurgeQueue();
            model.Silent = true;

            new Thread(() =>
            {
                // A worker exception must still set res, or the res==null pump below spins forever.
                try { res = WaitFor.AckResponse<string>(
                    ct,
                    response => { var fs = GrblFilesystems.ParseMountLine(response); if (fs != null) mounts.Add(fs); },
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    1500, () => Comms.com.WriteCommand("$FI")); }
                catch { res = false; }
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            model.Silent = false;

            return mounts;
        }

        // List one filesystem by changing to its mount path ($CWD) and issuing $F / $F+. Each file
        // is tagged with its Location/Path so run/delete/download can target the right filesystem.
        private static void ListMount(GrblViewModel model, string location, string path, bool ViewAll)
        {
            Grbl.WaitForResponse("$CWD=" + (path.Length > 1 ? path.TrimEnd('/') : path));

            bool? res = null;
            var ct = new CancellationToken();

            Comms.com.PurgeQueue();
            curLocation = location;
            curPath = path;
            model.Silent = true;

            new Thread(() =>
            {
                // A worker exception must still set res, or the res==null pump below spins forever.
                try { res = WaitFor.AckResponse<string>(
                    ct,
                    response => Process(response),
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    2000, () => Comms.com.WriteCommand(ViewAll ? GrblConstants.CMD_SDCARD_DIR_ALL : GrblConstants.CMD_SDCARD_DIR)); }
                catch { res = false; }
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            model.Silent = false;
        }

        // Original single-filesystem listing: mount the SD card if needed, then $F the current FS.
        private static void LegacyLoad(GrblViewModel model, bool ViewAll)
        {
            bool? res = null;
            CancellationToken cancellationToken = new CancellationToken();

            curLocation = GrblInfo.HasSDCard ? "SD" : string.Empty;
            curPath = string.Empty;

            if (GrblInfo.HasSDCard && grbl.SDCardMountStatus == SDState.Unmounted)
            {
                Comms.com.PurgeQueue();

                new Thread(() =>
                {
                    res = WaitFor.AckResponse<string>(
                        cancellationToken,
                        response => CardCheck(response),
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        1500, () => Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_MOUNT));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();
            }

            if (!GrblInfo.HasSDCard || grbl.SDCardMountStatus == SDState.Mounted || grbl.SDCardMountStatus == SDState.Detected)
            {
                Comms.com.PurgeQueue();

                res = null;
                model.Silent = true;

                new Thread(() =>
                {
                res = WaitFor.AckResponse<string>(
                    cancellationToken,
                    response => Process(response),
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    2000, () => Comms.com.WriteCommand(ViewAll ? GrblConstants.CMD_SDCARD_DIR_ALL : GrblConstants.CMD_SDCARD_DIR));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();

                model.Silent = false;
            }
        }

        private static void CardCheck(string data)
        {
            if(data == "ok")
                grbl.SDCardMountStatus = SDState.Mounted;
        }

        private static void Process(string data)
        {
            string filename = "";
            int filesize = 0;
            bool invalid = false;

            if (data.StartsWith("[FILE:"))
            {
                string[] parameters = data.TrimEnd(']').Split('|');
                foreach (string parameter in parameters)
                {
                    string[] valuepair = parameter.Split(':');
                    switch (valuepair[0])
                    {
                        case "[FILE":
                            filename = valuepair[1];
                            break;

                        case "SIZE":
                            filesize = int.Parse(valuepair[1]);
                            break;

                        case "INVALID":
                            invalid = true;
                            break;
                    }
                }

                // $F at a root filesystem recurses into nested mounts, so a file can be reported while
                // listing an ancestor - e.g. on a board with the SD card at "/" and LittleFS at
                // "/littlefs", the SD listing returns the /littlefs/* files too. Attribute each file to
                // its deepest containing mount; skip it here otherwise, so it isn't duplicated under (and
                // mis-pathed on) the parent filesystem - the SD card should show "/..." paths, not
                // "/littlefs/...". Mounts is empty on the legacy single-FS path, so that is unaffected.
                string owner = curPath;
                foreach (var m in GrblSDCard.Mounts)
                {
                    if (m.Path.Length > owner.Length &&
                         filename.StartsWith(m.Path.TrimEnd('/') + "/", System.StringComparison.OrdinalIgnoreCase))
                        owner = m.Path;
                }
                if (owner != curPath)
                    return;

                GrblSDCard.data.Rows.Add(new object[] { id++, "", filename, filesize, invalid, curLocation, curPath });
            }
            else if (data == "error:62" || data == "error:64")
                grbl.SDCardMountStatus = SDState.Unmounted;
        }
    }
}
