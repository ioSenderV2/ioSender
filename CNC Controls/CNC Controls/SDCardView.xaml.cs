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

                CanUpload = GrblInfo.UploadProtocol != string.Empty && (DataContext as GrblViewModel).SDCardMountStatus != SDState.Undetected;
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

        private void DownloadRun_Click(object sender, RoutedEventArgs e)
        {
            if (currentFile != null && (int)currentFile["Size"] > 0 && !isMacro((string)currentFile["Name"]) && MessageBox.Show(string.Format((string)FindResource("DownloandRun"), (string)currentFile["Name"]), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            {
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
                Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_RUN + TargetName(currentFile));

                Rewind = false;
            }
        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            bool ok = false;
            string filename = string.Empty;
            OpenFileDialog file = new OpenFileDialog();

            file.Filter = string.Format("GCode files ({0})|{0}|GCode macros (*.macro)|*.macro|Text files (*.txt)|*.txt|All files (*.*)|*.*", FileUtils.ExtensionsToFilter(GCode.FileTypes));

            if (file.ShowDialog() == true)
            {
                filename = file.FileName;
            }

            if (filename != string.Empty)
            {
                GrblViewModel model = DataContext as GrblViewModel;

                model.Message = (string)FindResource("Uploading");

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
                                string path = string.Format("{0}{1}{2}", model.FsCwd, model.FsCwd.EndsWith("/") ? "" : "/", System.IO.Path.GetFileName(filename));

                                client.Credentials = new NetworkCredential("grblHAL", "grblHAL");
                                client.UploadFile(string.Format("ftp://{0}:{1}{2}", GrblInfo.IpAddress, port == -1 ? 21 : port, path), WebRequestMethods.Ftp.UploadFile, filename);
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
                    model.Message = (string)FindResource("Uploading");
                    YModem ymodem = new YModem();
                    ymodem.DataTransferred += Ymodem_DataTransferred;
                    ok = ymodem.Upload(filename);
                }

                if(!(GrblInfo.UploadProtocol == "FTP" && !ok))
                    model.Message = (string)FindResource(ok ? "TransferDone" : "TransferAborted");

                ReloadFiles();
            }
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
            if (MessageBox.Show(string.Format((string)FindResource("DeleteFile"), (string)currentFile["Name"]), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            {
                if(Grbl.WaitForResponse(GrblConstants.CMD_SDCARD_UNLINK + TargetName(currentFile)))
                    ReloadFiles();
            }
        }

        private void RunFile()
        {
            if (currentFile != null)
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

        public static void Load(GrblViewModel model, bool ViewAll)
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

            if (mounts.Count > 0)
            {
                FreeSpace = GrblFilesystems.FreeSpaceSummary(mounts);

                foreach (var mount in mounts)
                    ListMount(model, mount.Name, mount.Path, ViewAll);

                // Leave the controller's working directory back at the root.
                Grbl.WaitForResponse("$CWD=/");
            }
            else
                LegacyLoad(model, ViewAll);

            data.AcceptChanges();
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
                res = WaitFor.AckResponse<string>(
                    ct,
                    response => { var fs = GrblFilesystems.ParseMountLine(response); if (fs != null) mounts.Add(fs); },
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    1500, () => Comms.com.WriteCommand("$FI"));
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
                res = WaitFor.AckResponse<string>(
                    ct,
                    response => Process(response),
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    2000, () => Comms.com.WriteCommand(ViewAll ? GrblConstants.CMD_SDCARD_DIR_ALL : GrblConstants.CMD_SDCARD_DIR));
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
                GrblSDCard.data.Rows.Add(new object[] { id++, "", filename, filesize, invalid, curLocation, curPath });
            }
            else if (data == "error:62" || data == "error:64")
                grbl.SDCardMountStatus = SDState.Unmounted;
        }
    }
}
