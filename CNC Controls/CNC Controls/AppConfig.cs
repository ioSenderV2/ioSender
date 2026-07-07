/*
 * AppConfig.cs - part of CNC Controls library
 *
 * v0.47 / 2026-02-11 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2026, Io Engineering (Terje Io)
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
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.Serialization;
using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Threading;
using System.Windows.Media.Media3D;
using CNC.Core;
using CNC.GCode;
using static CNC.GCode.GCodeParser;
using System.Collections.Generic;

namespace CNC.Controls
{
    public class LibStrings
    {
        static ResourceDictionary resource = new ResourceDictionary();

        public static string FindResource(string key)
        {
            if(resource.Source == null)
            try {
                resource.Source = new Uri("pack://application:,,,/CNC.Controls.WPF;Component/LibStrings.xaml", UriKind.Absolute);
            }
            catch
            {
            }

            return resource.Source == null || !resource.Contains(key) ? string.Empty : (string)resource[key];
        }
    }

    [Serializable]
    public class LatheConfig : ViewModelBase
    {
        private bool _isEnabled = false;
        private LatheMode _latheMode = LatheMode.Disabled;

        [XmlIgnore]
        public double ZDirFactor { get { return ZDirection == Direction.Negative ? -1d : 1d; } }

        [XmlIgnore]
        public LatheMode[] LatheModes { get { return (LatheMode[])Enum.GetValues(typeof(LatheMode)); } }

        [XmlIgnore]
        public Direction[] ZDirections { get { return (Direction[])Enum.GetValues(typeof(Direction)); } }

        [XmlIgnore]
        public bool IsEnabled { get { return _isEnabled; } set { _isEnabled = value; OnPropertyChanged(); } }

        // Simple on/off facade over XMode for the App > Main "Lathe mode" checkbox. Enabling defaults to
        // Radius mode (the common case); disabling clears it. Persisted via XMode in the "Lathe" section.
        [XmlIgnore]
        public bool LatheEnabled
        {
            get { return _latheMode != LatheMode.Disabled; }
            set { XMode = value ? LatheMode.Radius : LatheMode.Disabled; OnPropertyChanged(); }
        }

        public LatheMode XMode { get { return _latheMode; } set { _latheMode = value; IsEnabled = value != LatheMode.Disabled; } }
        public Direction ZDirection { get; set; } = Direction.Negative;
        public double PassDepthLast { get; set; } = 0.02d;
        public double FeedRate { get; set; } = 300d;
    }

    [Serializable]
    public class ProbeConfig : ViewModelBase
    {
        private bool _CheckProbeStatus = true;
        private bool _ValidateProbeConnected = false;

        public bool CheckProbeStatus { get { return _CheckProbeStatus; } set { _CheckProbeStatus = value; OnPropertyChanged(); } }
        public bool ValidateProbeConnected { get { return _ValidateProbeConnected; } set { _ValidateProbeConnected = value; OnPropertyChanged(); } }
    }

    [Serializable]
    public class CameraConfig : ViewModelBase
    {
        private string _camera = string.Empty;
        private double _xoffset = 0d, _yoffset = 0d, _crossHairX = -1d, _crossHairY = -1d;
        private int _guideScale = 10;
        private bool _moveToSpindle = false, _confirmMove = false;
        private CameraMoveMode _moveMode = CameraMoveMode.BothAxes;

        [XmlIgnore]
        internal bool IsDirty { get; set; } = false;

        [XmlIgnore]
        public CameraMoveMode[] MoveModes { get { return (CameraMoveMode[])Enum.GetValues(typeof(CameraMoveMode)); } }

        public string SelectedCamera { get { return _camera; } set { _camera = value; IsDirty = true; OnPropertyChanged(); } }
        public double XOffset { get { return _xoffset; } set { _xoffset = value; OnPropertyChanged(); } }
        public double YOffset { get { return _yoffset; } set { _yoffset = value; OnPropertyChanged(); } }
        public double CrosshairPosX { get { return _crossHairX; } set { _crossHairX = value; IsDirty = true; OnPropertyChanged(); } }
        public double CrosshairPosY { get { return _crossHairY; } set { _crossHairY = value; IsDirty = true; OnPropertyChanged(); } }
        public int GuideScale { get { return _guideScale; } set { _guideScale = value; IsDirty = true; OnPropertyChanged(); } }
        public bool InitialMoveToSpindle { get { return _moveToSpindle; } set { _moveToSpindle = value; IsDirty = true; OnPropertyChanged(); } }
        public bool ConfirmMove { get { return _confirmMove; } set { _confirmMove = value; IsDirty = true; OnPropertyChanged(); } }
        public CameraMoveMode MoveMode { get { return _moveMode; } set { _moveMode = value; OnPropertyChanged(); } }
    }

    [Serializable]
    public class GCodeViewerConfig : ViewModelBase
    {
        private bool _isEnabled = true;
        private int _arcResolution = 10;
        private double _minDistance = 0.05d, _toolDiameter = 3d;
        private bool _showGrid = true, _showAxes = true, _showBoundingBox = false, _showViewCube = true, _showCoordSystem = false, _showWorkEnvelope = false;
        private bool _showTextOverlay = false, _renderExecuted = false, _blackBackground = false, _scaleTool = true;
        private bool _clickToJog = true;
        Color _cutMotion = Colors.Black, _rapidMotion = Colors.LightPink, _retractMotion = Colors.Green, _toolOrigin = Colors.Green, _grid = Colors.Gray, _highlight = Colors.Crimson;

        [XmlIgnore]
        public bool IsHomingEnabled { get { return _isEnabled && GrblInfo.HomingEnabled; } set { OnPropertyChanged(); } }

        public bool IsEnabled { get { return _isEnabled; } set { _isEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsHomingEnabled)); } }
        public int ArcResolution { get { return _arcResolution; } set { _arcResolution = value; OnPropertyChanged(); } }
        public double MinDistance { get { return _minDistance; } set { _minDistance = value; OnPropertyChanged(); } }
        public bool ToolAutoScale { get { return _scaleTool; } set { _scaleTool = value; OnPropertyChanged(); } }
        public double ToolDiameter { get { return _toolDiameter; } set { _toolDiameter = value; OnPropertyChanged(); } }
        public bool ShowGrid { get { return _showGrid; } set { _showGrid = value; OnPropertyChanged(); } }
        public bool ShowAxes { get { return _showAxes; } set { _showAxes = value; OnPropertyChanged(); } }
        public bool ShowBoundingBox { get { return _showBoundingBox; } set { _showBoundingBox = value; OnPropertyChanged(); } }
        public bool ShowWorkEnvelope { get { return _showWorkEnvelope && GrblInfo.HomingEnabled; } set { _showWorkEnvelope = value; OnPropertyChanged(); } }
        public bool ShowViewCube { get { return _showViewCube; } set { _showViewCube = value; OnPropertyChanged(); } }
        public bool ShowTextOverlay { get { return _showTextOverlay; } set { _showTextOverlay = value; OnPropertyChanged(); } }
        public bool ShowCoordinateSystem { get { return _showCoordSystem; } set { _showCoordSystem = value; OnPropertyChanged(); } }
        public bool RenderExecuted { get { return _renderExecuted; } set { _renderExecuted = value; OnPropertyChanged(); } }
        public bool ClickToJog { get { return _clickToJog; } set { _clickToJog = value; OnPropertyChanged(); } }
        public bool BlackBackground { get { return _blackBackground; } set { _blackBackground = value; OnPropertyChanged(); } }
        public Color CutMotionColor { get { return _cutMotion; } set { _cutMotion = value; OnPropertyChanged(); } }
        public Color RapidMotionColor { get { return _rapidMotion; } set { _rapidMotion = value; OnPropertyChanged(); } }
        public Color RetractMotionColor { get { return _retractMotion; } set { _retractMotion = value; OnPropertyChanged(); } }
        public Color ToolOriginColor { get { return _toolOrigin; } set { _toolOrigin = value; OnPropertyChanged(); } }
        public Color GridColor { get { return _grid; } set { _grid = value; OnPropertyChanged(); } }
        public Color HighlightColor { get { return _highlight; } set { _highlight = value; OnPropertyChanged(); } }
        public int ViewMode { get; set; } = -1;
        public int ToolVisualizer { get; set; } = 1;
        public Point3D CameraPosition { get; set; }
        public Vector3D CameraLookDirection { get; set; }
        public Vector3D CameraUpDirection { get; set; }
    }

    [Serializable]
    public class JogUIConfig : ViewModelBase
    {
        private int[] _feedrate = new int[4];
        private double[] _distance = new double[4];

        public JogUIConfig()
        {
        }

        public JogUIConfig(int[] feedrate, double[] distance)
        {
            for(int i = 0; i < feedrate.Length; i++)
            {
                _feedrate[i] = feedrate[i];
                _distance[i] = distance[i];
            }
        }

        [XmlIgnore]
        public int[] Feedrate { get { return _feedrate; } }
        public int Feedrate0 { get { return _feedrate[0]; } set { _feedrate[0] = value; OnPropertyChanged(); } }
        public int Feedrate1 { get { return _feedrate[1]; } set { _feedrate[1] = value; OnPropertyChanged(); } }
        public int Feedrate2 { get { return _feedrate[2]; } set { _feedrate[2] = value; OnPropertyChanged(); } }
        public int Feedrate3 { get { return _feedrate[3]; } set { _feedrate[3] = value; OnPropertyChanged(); } }

        [XmlIgnore]
        public double[] Distance { get { return _distance; } }
        public double Distance0 { get { return _distance[0]; } set { _distance[0] = value; OnPropertyChanged(); } }
        public double Distance1 { get { return _distance[1]; } set { _distance[1] = value; OnPropertyChanged(); } }
        public double Distance2 { get { return _distance[2]; } set { _distance[2] = value; OnPropertyChanged(); } }
        public double Distance3 { get { return _distance[3]; } set { _distance[3] = value; OnPropertyChanged(); } }
    }

    [Serializable]
    public class JogConfig : ViewModelBase
    {
        private bool _kbEnable = true, _defaultSpeedFast = false, _keepUiJogSelection = false;

        private double _fastFeedrate = 500d, _slowFeedrate = 200d, _stepFeedrate = 100d;
        private double _fastDistance = 500d, _slowDistance = 500d, _stepDistance = 0.05d;

        // Master switch for keyboard jogging (default on; users who dislike it can turn it off). Keyboard,
        // on-screen UI, and controller jogging are all independent input methods that are always live - there
        // is no "jog mode" to select; the input you use is the mode.
        public bool KeyboardEnable { get { return _kbEnable; } set { _kbEnable = value; OnPropertyChanged(); } }
        // Restore the on-screen jog distance/speed selection from the previous session on startup.
        public bool KeepUiJogSelection { get { return _keepUiJogSelection; } set { _keepUiJogSelection = value; OnPropertyChanged(); } }
        // Default keyboard continuous-jog speed: false = Slow (Shift -> Fast), true = Fast (Shift -> Slow).
        public bool DefaultSpeedFast { get { return _defaultSpeedFast; } set { _defaultSpeedFast = value; OnPropertyChanged(); } }
        public double FastFeedrate { get { return _fastFeedrate; } set { _fastFeedrate = value; OnPropertyChanged(); } }
        public double SlowFeedrate { get { return _slowFeedrate; } set { _slowFeedrate = value; OnPropertyChanged(); } }
        public double StepFeedrate { get { return _stepFeedrate; } set { _stepFeedrate = value; OnPropertyChanged(); } }
        public double FastDistance { get { return _fastDistance; } set { _fastDistance = value; OnPropertyChanged(); } }
        public double SlowDistance { get { return _slowDistance; } set { _slowDistance = value; OnPropertyChanged(); } }
        public double StepDistance { get { return _stepDistance; } set { _stepDistance = value; OnPropertyChanged(); } }
    }

    [Serializable]
    public class Macros : ViewModelBase
    {
        public ObservableCollection<CNC.GCode.Macro> Macro { get; private set; } = new ObservableCollection<CNC.GCode.Macro>();
    }

    [Serializable]
    public class Config : ViewModelBase
    {
        private int _pollInterval = 200, /* ms*/  _maxBufferSize = 300, _resetDelay = 2000;
        private bool _useBuffering = false, _keepMdiFocus = true, _filterOkResponse = false, _saveWindowSize = false, _autoCompress = false, _send_comments = false, _addLinenumbers = false;
        private bool _preferNetwork = false;
        private bool _autoSaveSettings = false, _promptOnSave = false, _safeGotoZ = true, _restoreFusionRapids = false;
        private bool _autoSaveGrblSettings = false, _promptOnGrblSave = false;
        private CommandIgnoreState _ignoreM6 = CommandIgnoreState.No, _ignoreM7 = CommandIgnoreState.No, _ignoreM8 = CommandIgnoreState.No, _ignoreG61G64 = CommandIgnoreState.Strip;
        private string _theme = "default";

        [XmlIgnore]
        public Dictionary<string, string> Themes { get; private set; } = new Dictionary<string, string>();

        public string Theme
        {
            get { return _theme; }
            set {
                _theme = value; //.Substring(0, 1).ToUpper() + value.Substring(1);
                Properties.Settings.Default.ColorMode = value; // value.Substring(0, 1).ToUpper() + value.Substring(1);
                Properties.Settings.Default.Save();
                OnPropertyChanged();
            }
        }
        public int PollInterval { get { return _pollInterval < 100 ? 100 : _pollInterval; } set { _pollInterval = value; OnPropertyChanged(); } }
        // Go-To safety: lift Z to machine top, traverse X/Y, then descend Z - so a Go To (WCS origin, G28, G30)
        // clears any fixtures/stock standing up in Z instead of cutting a diagonal. Needs homing + soft limits;
        // falls back to a single move otherwise.
        public bool SafeGotoZ { get { return _safeGotoZ; } set { _safeGotoZ = value; OnPropertyChanged(); } }
        // Load Folder: always restore the rapid moves Fusion 360 Personal Edition downgrades to feed moves
        // (G1 -> G0), without prompting each time. Also used for a --LoadFolder startup load.
        public bool RestoreFusionRapids { get { return _restoreFusionRapids; } set { _restoreFusionRapids = value; OnPropertyChanged(); } }
        public string PortParams { get; set; } = "COMn:115200,N,8,1";
        // Default host for the Connect dialog's network tab: the most recent IP successfully connected to.
        // Empty until first set - seeded from the controller's $I-reported IP on a serial connection, then
        // updated to the IP used on each successful network connection. Empty falls back to the mDNS name.
        public string NetworkHost { get; set; } = string.Empty;
        // When set, after a serial/USB connection whose $I reports an IP, ioSender probes <ip>:23 and, if it
        // answers, automatically switches the connection to the network (status line: "Connection migrated...").
        public bool PreferNetwork { get { return _preferNetwork; } set { if (_preferNetwork != value) { _preferNetwork = value; OnPropertyChanged(); } } }
        public int ResetDelay { get { return _resetDelay; } set { _resetDelay = value; OnPropertyChanged(); } }
        // Remember when the saved target is the bundled simulator so startup auto-reconnect can launch
        // it first (a 127.0.0.1:port target is otherwise indistinguishable from a real network controller).
        public bool StartSimulator { get; set; } = false;
        public string SimulatorExe { get; set; } = "grblHAL_sim.exe";
        public string SimulatorArgs { get; set; } = string.Empty;
        // grblHAL_sim "-t" speedup: how fast simulated time runs vs real time. 1 = real time (machine speed),
        // 2/4/... = that many times faster, 0 = as fast as the host can (motion finishes near-instantly).
        // Edit in App.config; not exposed in Settings:App yet.
        public double SimulatorSpeedup { get; set; } = 1.0;
        public bool UseBuffering { get { return _useBuffering; } set { _useBuffering = value; OnPropertyChanged(); } }
        public bool KeepWindowSize { get { return _saveWindowSize; } set { if (_saveWindowSize != value) { _saveWindowSize = value; OnPropertyChanged(); } } }
        public double WindowWidth { get; set; } = 925;
        public double WindowHeight { get; set; } = 660;
        public double WindowLeft { get; set; } = double.NaN;   // NaN = never saved -> use the default placement
        public double WindowTop { get; set; } = double.NaN;
        public int OutlineFeedRate { get; set; } = 500;
        public int MaxBufferSize { get { return _maxBufferSize < 300 ? 300 : _maxBufferSize; } set { _maxBufferSize = value; OnPropertyChanged(); } }
        public string Editor { get; set; } = "notepad.exe";
        public bool KeepMdiFocus { get { return _keepMdiFocus; } set { _keepMdiFocus = value; OnPropertyChanged(); } }
        public bool FilterOkResponse { get { return _filterOkResponse; } set { _filterOkResponse = value; OnPropertyChanged(); } }
        public bool AutoCompress { get { return _autoCompress; } set { _autoCompress = value; OnPropertyChanged(); } }
        public bool SendComments { get { return _send_comments; } set { _send_comments = value; OnPropertyChanged(); } }
        public bool AddLineNumbers { get { return _addLinenumbers; } set { _addLinenumbers = value; OnPropertyChanged(); } }
        public bool ConsoleVerbose { get; set; } = false;
        public bool ConsoleFilterRT { get; set; } = false;
        public bool ConsoleShowRTAll { get; set; } = false;
        public bool ConsoleWindowOpen { get; set; } = false;
        public string ConsoleShortcut { get; set; } = "Esc";
        // Point size for the console scrollback + command prompt. Notifies so the console updates live.
        private double _consoleFontSize = 10d;
        public double ConsoleFontSize { get { return _consoleFontSize; } set { if (_consoleFontSize != value) { _consoleFontSize = Math.Max(6d, Math.Min(32d, value)); OnPropertyChanged(); } } }
        // Last machine picked in the Machine Setup Wizard ("Manufacturer|Product|Model"), restored next run.
        public string LastMachine { get; set; } = string.Empty;
        public double ConsoleWindowLeft { get; set; } = double.NaN;
        public double ConsoleWindowTop { get; set; } = double.NaN;
        public double ConsoleWindowWidth { get; set; } = double.NaN;
        public double ConsoleWindowHeight { get; set; } = double.NaN;

        [XmlIgnore]
        public CommandIgnoreState[] CommandIgnoreStates { get { return (CommandIgnoreState[])Enum.GetValues(typeof(CommandIgnoreState)); } }
        public CommandIgnoreState IgnoreM6 { get { return _ignoreM6; } set { _ignoreM6 = value; OnPropertyChanged(); } }
        public CommandIgnoreState IgnoreM7 { get { return _ignoreM7; } set { _ignoreM7 = value; OnPropertyChanged(); } }
        public CommandIgnoreState IgnoreM8 { get { return _ignoreM8; } set { _ignoreM8 = value; OnPropertyChanged(); } }
        public CommandIgnoreState IgnoreG61G64 { get { return _ignoreG61G64; } set { _ignoreG61G64 = value; OnPropertyChanged(); } }
        public ObservableCollection<CNC.GCode.Macro> Macros { get; set; } = new ObservableCollection<CNC.GCode.Macro>();
        public int LastMacroId { get; set; } = -1;   // most recently run macro (any entry point); UI defaults to it
        public JogConfig Jog { get; set; } = new JogConfig();
        public JogUIConfig JogUiMetric { get; set; } = new JogUIConfig(new int[4] { 5, 100, 500, 1000 }, new double[4] { .01d, .1d, 1d, 10d });
        public JogUIConfig JogUiImperial { get; set; } = new JogUIConfig(new int[4] { 5, 10, 50, 100 }, new double[4] { .001d, .01d, .1d, 1d });

        // Configurable main page (ioSender XL), edited via the "Edit Main Page" dialog.
        // MainPanels: ordered panel names filling the six main-page slots (col-major, [0,1,2]=left, [3,4,5]=right).
        // FlyoutItems: ordered names (panels / offset codes / specials) shown as sidebar flyouts.
        // Anything in neither list is unassigned (shown nowhere). Applied on restart.
        // NOTE: serialized as CSV strings (MainPanelsCsv / FlyoutItemsCsv), NOT as List<string>.
        // XmlSerializer APPENDS to a pre-initialized List<T> on load (defaults + saved), which silently
        // discarded the user's edits; a string property is replaced cleanly, and a missing element still
        // falls back to the initializer defaults for configs that predate the feature.
        private List<string> _mainPanels = new List<string> { "Outline", "Spindle", "Coolant", "WorkParameters", "Feed", "Goto" };
        private List<string> _flyoutItems = new List<string> { "Macros", "MachinePosition" };
        // LeftPanels: ordered panel names filling the area left of the 3D view (default = the original DRO +
        // program-limits stack). Signals/Status stay fixed below it.
        private List<string> _leftPanels = new List<string> { "DRO", "ProgramLimits" };
        // Tabs: ordered ViewType names of the main TabControl tabs that should be shown, in display order.
        // Empty (the default) means "show all available tabs in their built-in order" - no reordering/hiding.
        private List<string> _tabs = new List<string>();

        [XmlIgnore]
        public List<string> MainPanels { get { return _mainPanels; } set { _mainPanels = value ?? new List<string>(); } }
        [XmlIgnore]
        public List<string> Tabs { get { return _tabs; } set { _tabs = value ?? new List<string>(); } }
        [XmlIgnore]
        public List<string> FlyoutItems { get { return _flyoutItems; } set { _flyoutItems = value ?? new List<string>(); } }
        [XmlIgnore]
        public List<string> LeftPanels { get { return _leftPanels; } set { _leftPanels = value ?? new List<string>(); } }

        public string MainPanelsCsv
        {
            get { return string.Join(",", _mainPanels); }
            set { _mainPanels = string.IsNullOrEmpty(value) ? new List<string>() : new List<string>(value.Split(',')); }
        }
        public string FlyoutItemsCsv
        {
            get { return string.Join(",", _flyoutItems); }
            set { _flyoutItems = string.IsNullOrEmpty(value) ? new List<string>() : new List<string>(value.Split(',')); }
        }
        public string LeftPanelsCsv
        {
            get { return string.Join(",", _leftPanels); }
            set { _leftPanels = string.IsNullOrEmpty(value) ? new List<string>() : new List<string>(value.Split(',')); }
        }
        public string TabsCsv
        {
            get { return string.Join(",", _tabs); }
            set { _tabs = string.IsNullOrEmpty(value) ? new List<string>() : new List<string>(value.Split(',')); }
        }

        // One-shot: the Height Map tab was introduced after tab layouts started persisting, so an existing
        // saved layout/tab-order won't include it and would filter it out. Injected once on load (then the
        // user can hide it like any tab). See AppConfig load.
        public bool HeightMapTabMigrated { get; set; } = false;

        // One-shot: Load Stock was renamed "Start Job" and made the first tab (before Job). An existing saved
        // layout/tab-order pins its old position, so move it to the front once on load (the user can reorder
        // afterwards like any tab). See AppConfig load.
        public bool StartJobTabFirstMigrated { get; set; } = false;

        // Names of flyouts the user has pinned; reopened (pinned) on next launch. (Empty default -> append is harmless.)
        public List<string> PinnedFlyouts { get; set; } = new List<string>();
        // Settings:App autosave on tab-leave / close (opt-in); PromptOnSave shows a confirm/discard list of changes.
        public bool AutoSaveSettings { get { return _autoSaveSettings; } set { _autoSaveSettings = value; OnPropertyChanged(); } }
        public bool PromptOnSave { get { return _promptOnSave; } set { _promptOnSave = value; OnPropertyChanged(); } }
        // Grbl (controller $) settings autosave on leaving the Grbl tab (opt-in, default off - $ writes go to
        // hardware). When off, leaving the tab with unsaved changes still prompts "save now?" (legacy behavior).
        public bool AutoSaveGrblSettings { get { return _autoSaveGrblSettings; } set { _autoSaveGrblSettings = value; OnPropertyChanged(); } }
        public bool PromptOnGrblSave { get { return _promptOnGrblSave; } set { _promptOnGrblSave = value; OnPropertyChanged(); } }

        public LatheConfig Lathe { get; set; } = new LatheConfig();
        public CameraConfig Camera { get; set; } = new CameraConfig();
        public GCodeViewerConfig GCodeViewer { get; set; } = new GCodeViewerConfig();
        public ProbeConfig Probing { get; set; } = new ProbeConfig();
    }

    public class AppConfig : ViewModelBase
    {
        private string configfile = null;
        private bool? MPGactive = null;
        private Config _base = null;

        // Startup connection parameters parsed from the command line by LoadConfig(), consumed later
        // by OpenConnection() (the two are split so config loads synchronously before controls load,
        // while the connection is deferred so the window paints first).
        private bool _selectPort = false;
        private string _startupPort = string.Empty, _startupBaud = string.Empty;

        public string FileName { get; private set; }
        // Folder of per-toolpath .nc files to preload on startup (--LoadFolder). Transient (CLI only).
        public string FolderName { get; private set; }

        private static readonly Lazy<AppConfig> settings = new Lazy<AppConfig>(() => new AppConfig());

        // Set when a load migrated the on-disk format (legacy <Config> v1 -> sectioned <AppConfig> v2)
        // or imported a legacy standalone file, so LoadConfig persists the converted form immediately.
        private bool _migratedFormat = false;

        // Full Config serializer for reading the legacy v1 (<Config>) file - nested objects included.
        private static readonly XmlSerializer legacySerializer = new XmlSerializer(typeof(Config));

        // Core-section serializer: the same Config type but with the carved-out nested objects omitted
        // (they are written/read as their own <section>s). Built once - constructing an XmlSerializer with
        // overrides generates a dynamic assembly each time, so it must be cached.
        private static readonly XmlSerializer coreSerializer = BuildCoreSerializer();

        private static XmlSerializer BuildCoreSerializer()
        {
            var ov = new XmlAttributeOverrides();
            var ignore = new XmlAttributes { XmlIgnore = true };
            foreach (var member in new[] { "Jog", "JogUiMetric", "JogUiImperial", "Lathe", "Camera", "GCodeViewer", "Probing" })
                ov.Add(typeof(Config), member, ignore);
            return new XmlSerializer(typeof(Config), ov);
        }

        private bool _sectionsRegistered = false;

        // Register the built-in config sections (Core first, so it rebuilds Base before the nested
        // sections assign into it). The nested sections delegate to Base.<X> so the AppConfig.Base.<X>
        // facade keeps returning the same instances after the carve-out.
        private void RegisterSections()
        {
            if (_sectionsRegistered)
                return;
            _sectionsRegistered = true;

            ConfigStore.Register(new XmlObjectSection<Config>("Core", () => Base, v => Base = v, coreSerializer));
            ConfigStore.Register(new XmlObjectSection<JogConfig>("Jog", () => Base.Jog, v => Base.Jog = v));
            ConfigStore.Register(new XmlObjectSection<JogUIConfig>("JogUiMetric", () => Base.JogUiMetric, v => Base.JogUiMetric = v));
            ConfigStore.Register(new XmlObjectSection<JogUIConfig>("JogUiImperial", () => Base.JogUiImperial, v => Base.JogUiImperial = v));
            ConfigStore.Register(new XmlObjectSection<LatheConfig>("Lathe", () => Base.Lathe, v => Base.Lathe = v));
            ConfigStore.Register(new XmlObjectSection<CameraConfig>("Camera", () => Base.Camera, v => Base.Camera = v));
            ConfigStore.Register(new XmlObjectSection<GCodeViewerConfig>("GCodeViewer", () => Base.GCodeViewer, v => Base.GCodeViewer = v));
            ConfigStore.Register(new XmlObjectSection<ProbeConfig>("Probing", () => Base.Probing, v => Base.Probing = v));

            // Feature data formerly kept in standalone .xml files next to App.config, folded in as sections via
            // RegisterFolded (registers the section + one-time legacy importer + tracks the file for deletion after
            // the migrated save). See DeleteFoldedInFiles / ConfigStore for the mechanism.
            RegisterFolded<ProbeDefinitionList>("Probes",
                () => ProbeDefinitions.Export(), v => ProbeDefinitions.SetItems(v), "ProbeDefinitions.xml");

            // Game-controller button map (was ControllerMap.xml). CNC.Core can't see the config store, so it keeps
            // ControllerMapper.SectionConfig in sync and calls PersistHook to save; we wire that hook here.
            RegisterFolded<ControllerMapFile>("Controller",
                () => ControllerMapper.SectionConfig, v => ControllerMapper.SectionConfig = v, "ControllerMap.xml");
            ControllerMapper.PersistHook = () => Save();

            // Keyboard key mappings (was KeyMap0.xml) - legacy file uses a custom XML root, so a custom importer.
            RegisterFolded<List<KeypressHandler.KeypressHandlerFn>>("KeyMap",
                () => KeypressHandler.SectionConfig, v => KeypressHandler.SectionConfig = v, "KeyMap0.xml", () => KeypressHandler.ReadLegacyFile());
            KeypressHandler.PersistHook = () => Save();

            // Tool/wizard parameter blobs (were their own .xml files). These live in CNC.Controls so they save
            // via AppConfig directly; each keeps a static holder the section reads.
            RegisterFolded<SurfaceParams>("SurfaceSpoilboard",
                () => SurfaceSpoilboardWizard.SectionConfig, v => SurfaceSpoilboardWizard.SectionConfig = v, "SurfaceSpoilboard.xml");
            RegisterFolded<AutoSquareParams>("AutoSquare",
                () => AutoSquareWizard.SectionConfig, v => AutoSquareWizard.SectionConfig = v, "AutoSquare.xml");
            RegisterFolded<ScratchParams>("StepperCalScratch",
                () => StepperCalibrationScratchWizard.SectionConfig, v => StepperCalibrationScratchWizard.SectionConfig = v, "StepperCalScratch.xml");
            RegisterFolded<LoadStockSettings>("LoadStock",
                () => LoadStockConfig.Section, v => LoadStockConfig.Section = v, "LoadStock.xml");

            // Hierarchical layout tree (Phase 2b). Registered after Core so its migration importer can
            // read Base.Tabs when the section is absent (first run on a build that has it).
            layoutSection = new LayoutSection(() => LayoutMigration.FromFlat(Base?.Tabs));
            ConfigStore.Register(layoutSection);
        }

        // The current main-window layout tree (Phase 2b). Always EnsureEssentials-repaired (safe to consume).
        private LayoutSection layoutSection;
        public LayoutNode Layout { get { return layoutSection?.Root; } }

        // Show/hide a top-level tab component in the persisted layout, so a config toggle (e.g. the App > Main
        // "Lathe mode" checkbox) can add/remove a tab that only appears after a restart. Mirrors the HeightMap
        // injection below: updates BOTH the layout tree (the post-restart authority BuildTabs reads) AND the
        // legacy flat Config.Tabs list when the user has a customised order. When adding, insert after
        // afterComponent (or append). Safe to call repeatedly (idempotent).
        public void SetTabPresent(string component, bool present, string afterComponent = null)
        {
            var tabsSlot = layoutSection?.Root?.Slot(LayoutKeys.SlotTabs);

            if (present)
            {
                if (tabsSlot != null && tabsSlot.Items.FindIndex(n => n.Component == component) < 0)
                {
                    int after = afterComponent == null ? -1 : tabsSlot.Items.FindIndex(n => n.Component == afterComponent);
                    tabsSlot.Items.Insert(after >= 0 ? after + 1 : tabsSlot.Items.Count, new LayoutNode(component));
                }
                if (Base != null && Base.Tabs.Count > 0 && !Base.Tabs.Contains(component))
                {
                    int after = afterComponent == null ? -1 : Base.Tabs.FindIndex(t => t == afterComponent);
                    Base.Tabs.Insert(after >= 0 ? after + 1 : Base.Tabs.Count, component);
                }
            }
            else
            {
                tabsSlot?.Items.RemoveAll(n => n.Component == component);
                if (Base != null && Base.Tabs.Count > 0)
                    Base.Tabs.Remove(component);
            }

            Save(CNC.Core.Resources.IniFile);
        }

        // Standalone config files now folded into App.config as sections (populated below via RegisterFolded).
        // After a migrated load persists the merged config, these redundant files are deleted so only App.config
        // remains. Kept in registration order.
        private static readonly List<string> FoldedInFiles = new List<string>();

        // Register a section backed by a serializable DTO T, folded in from a legacy standalone file: get/set
        // expose the feature's holder for T, legacyFileName is the old file (imported once when the section is
        // absent, then deleted). A feature with a non-default XML root passes its own importer. This is the shared
        // "save/restore a config fragment" plumbing - each feature supplies only its DTO and how to read/write it.
        private void RegisterFolded<T>(string key, Func<T> get, Action<T> set, string legacyFileName, Func<T> importer = null) where T : class
        {
            ConfigStore.Register(new XmlObjectSection<T>(key, get, set, null, importer ?? (() => ReadLegacyXml<T>(legacyFileName))));
            if (!FoldedInFiles.Contains(legacyFileName))
                FoldedInFiles.Add(legacyFileName);
        }

        // Generic one-time importer: deserialize a legacy standalone XML file (default serializer / root) into T.
        private static T ReadLegacyXml<T>(string fileName) where T : class
        {
            try
            {
                string path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, fileName);
                if (!File.Exists(path))
                    return null;
                var xs = new XmlSerializer(typeof(T));
                using (var fs = File.OpenRead(path))
                    return (T)xs.Deserialize(fs);
            }
            catch { return null; }
        }

        private static void DeleteFoldedInFiles()
        {
            foreach (var name in FoldedInFiles)
            {
                try
                {
                    string p = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, name);
                    if (File.Exists(p))
                        File.Delete(p);
                }
                catch { /* best-effort; a leftover file is harmless (the section supersedes it) */ }
            }
        }

        private AppConfig()
        {
            RegisterSections();
            Properties.Settings.Default.PropertyChanged += Default_PropertyChanged;
        }

        private void Default_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ColorMode));   
        }

        public static AppConfig Settings { get { return settings.Value; } }

        // Raised when the console toggle shortcut is changed in the Key Mappings editor so the
        // main window(s) can re-register it without a restart.
        public static event System.Action ConsoleShortcutChanged;
        public static void NotifyConsoleShortcutChanged() { ConsoleShortcutChanged?.Invoke(); }

        public static string ColorMode { get { return Properties.Settings.Default.ColorMode; } }

        public Config Base
        {
            get { return _base; }
            private set
            {
                _base = value;
            }
        }

        public ObservableCollection<CNC.GCode.Macro> Macros { get { return Base == null ? null : Base.Macros; } }
        public int LastMacroId { get { return Base == null ? -1 : Base.LastMacroId; } }
        public JogConfig Jog { get { return Base == null ? null : Base.Jog; } }
        public JogUIConfig JogUiMetric { get { return Base == null ? null : Base.JogUiMetric; } }
        public JogUIConfig JogUiImperial { get { return Base == null ? null : Base.JogUiImperial; } }

        public CameraConfig Camera { get { return Base == null ? null : Base.Camera; } }
        public LatheConfig Lathe { get { return Base == null ? null : Base.Lathe; } }
        public GCodeViewerConfig GCodeViewer { get { return Base == null ? null : Base.GCodeViewer; } }
        public ProbeConfig Probing { get { return Base == null ? null : Base.Probing; } }

        public bool Save(string filename)
        {
            bool ok = false;

            if (Base == null)
                Base = new Config();

            // Compose Core + the carved-out sections (plus any sections from features not in this
            // build, preserved verbatim) into the single sectioned App.config document.
            XDocument doc = ConfigStore.WriteDocument();
            string tmp = filename + ".tmp";

            try
            {
                // Write atomically: serialize to a sibling temp file, then swap it into place. Writing
                // straight to the live file with FileMode.Create truncates it to zero the instant it
                // opens, so an interrupted serialize, a killed process, or a OneDrive sync lock mid-write
                // leaves a 0-byte config - which is exactly how the real config got destroyed. With this,
                // the live file is only ever replaced by a fully-written one.
                using (FileStream fsout = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    doc.Save(fsout);   // any failure throws here, before the swap - live file untouched

                if (File.Exists(filename))
                {
                    // Swap in the new file and keep the previous good copy as <name>.bak. That rolling
                    // backup is what Load() recovers from if the live file is ever found empty or unreadable.
                    string bak = filename + ".bak";
                    try { File.Replace(tmp, filename, bak); }   // atomic: live -> .bak, tmp -> live
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Fallback where Replace is unsupported (some sync placeholders): preserve the
                        // current file as the backup, then overwrite from the fully-written temp file.
                        try { File.Copy(filename, bak, true); } catch { /* best effort */ }
                        File.Copy(tmp, filename, true);
                        File.Delete(tmp);
                    }
                }
                else
                    File.Move(tmp, filename);    // first save - nothing to replace

                configfile = filename;
                ok = true;
            }
            catch (Exception e)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave the temp for inspection */ }
                MessageBox.Show(e.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }

            return ok;
        }

        public bool Save()
        {
            Camera.IsDirty = false;
            return configfile != null && Save(configfile);
        }

        // Record the most recently run macro (from any entry point) and persist it, so the UI can
        // default to it. No-op (and no save) if unchanged.
        public void RecordMacroRun(int id)
        {
            if (Base != null && Base.LastMacroId != id)
            {
                Base.LastMacroId = id;
                Save();
            }
        }

        public bool Load(string filename)
        {
            // Try the live config first. If it is missing, 0 bytes, or otherwise unreadable - an
            // interrupted save or a sync truncation can leave it empty - fall back to the rolling backup
            // Save() keeps. Recovering the last good copy avoids losing the user's settings AND the
            // destructive "create new?" prompt that a false "invalid" used to trigger.
            _migratedFormat = false;

            if (TryLoad(filename))
                return true;

            string bak = filename + ".bak";
            if (TryLoad(bak))
            {
                configfile = filename;                                  // keep writing to the real path
                try { File.Copy(bak, filename, true); } catch { /* leave .bak as the source of truth */ }
                return true;
            }

            return false;
        }

        // Deserialize one config file into Base, tolerating a transient unreadable / 0-length state by
        // retrying briefly. Returns false (Base untouched) if the file is absent or never becomes parseable.
        private bool TryLoad(string filename)
        {
            if (!File.Exists(filename))
                return false;

            bool ok = false;

            // The file can be momentarily unreadable at startup - build output still flushing, sync/AV
            // holding it open, a previous instance closing, or a save still in flight - so retry a few
            // times before giving up rather than declaring a good config invalid on the first hiccup.
            for (int attempt = 0; attempt < 6 && !ok; attempt++)
            {
                try
                {
                    // A 0-length file is not a config, it is a save that was interrupted or not yet
                    // flushed. Don't hand it to the deserializer (it would just throw a vague error) -
                    // treat it as a transient and let Load() fall back to the backup if it persists.
                    if (new FileInfo(filename).Length == 0L)
                        throw new IOException("config file is empty");

                    XDocument doc;
                    using (StreamReader reader = new StreamReader(filename))
                        doc = XDocument.Load(reader);

                    if (ConfigStore.IsLegacy(doc))
                    {
                        // Legacy v1 (<Config>): deserialize the whole graph (nested objects included)
                        // into Base, then flag a migrate-save so it is rewritten as sectioned v2.
                        using (var r = doc.Root.CreateReader())
                            Base = (Config)legacySerializer.Deserialize(r);
                        // ReadDocument is bypassed on this path, so run section legacy-importers
                        // explicitly (e.g. build the Layout tree from the flat Config.Tabs).
                        ConfigStore.ImportLegacyForAbsentSections();
                        _migratedFormat = true;
                    }
                    else
                    {
                        // v2 (<AppConfig>): the Core section rebuilds Base, the nested sections fill it
                        // in, unowned sections are preserved, and absent sections may import a legacy file.
                        ConfigStore.ReadDocument(doc);
                        if (Base == null)
                            Base = new Config();
                        if (ConfigStore.MigratedOnLoad)
                            _migratedFormat = true;
                    }
                    configfile = filename;

                    // Drop any session-only macros that leaked into the saved file. Iterate a COPY:
                    // removing from the live collection while enumerating it throws, which would have
                    // made a valid config look invalid (the very false-negative this guards against).
                    foreach (var macro in new List<CNC.GCode.Macro>(Base.Macros))
                        if (macro.IsSession)
                            Base.Macros.Remove(macro);

                    // Migrate legacy macros (saved before the FKey element existed) to an explicit
                    // F-key: a macro with Id n used to be run by Fn (see JobControl.FnKeyHandler).
                    foreach (var macro in Base.Macros)
                        if (macro.FKey == 0 && macro.Id >= 1 && macro.Id <= 12)
                            macro.FKey = macro.Id;

                    ok = true;
                }
                catch
                {
                    // Back off and retry; a genuinely malformed file fails every attempt and returns false.
                    if (attempt + 1 < 6)
                        Thread.Sleep(150);
                }
            }

            return ok;
        }

        public void Shutdown()
        {
            if (Camera.IsDirty)
                Save();
        }

        private bool isComPort(string port)
        {
            return !(port.ToLower().StartsWith("ws://") || char.IsDigit(port[0]));
        }

        private void setPort(string port, string baud)
        {
            if (isComPort(port) && port.IndexOf(':') == -1)
            {
                string prop = string.Format(":{0},N,8,1", string.IsNullOrEmpty(baud) ? "115200" : baud);
                string[] values = port.Split('!');
                if (isComPort(Base.PortParams))
                {
                    var props = Base.PortParams.Substring(Base.PortParams.IndexOf(':')).Split(',');
                    if(props.Length >= 4)
                        prop = string.Format(":{0},{1},{2},{3}", (string.IsNullOrEmpty(baud) ? props[0] : baud), props[1], props[2], props[3]);
                }
                port = values[0] + prop + (values.Length > 1 ? ",," + values[1] : "");
            }
            Base.PortParams = port;
        }

        // Combined load + open, kept for callers that connect synchronously at startup.
        public int SetupAndOpen(string appname, GrblViewModel model, System.Windows.Threading.Dispatcher dispatcher)
        {
            int status = LoadConfig(appname);
            return status == 0 ? OpenConnection(appname, model, dispatcher) : status;
        }

        // Name of the read-only factory-default template shipped next to the exe (app folder).
        public const string DefaultTemplateName = "Default-App.config";

        // The factory-default Config, read once from the read-only Default-App.config template in the app folder
        // (Resources.Path). The settings panels' "Reset to Default" reads its values from here, so the shipped
        // template defines the defaults. Falls back to code defaults (new Config()) if it's missing/unreadable.
        private static Config _factoryDefaults;
        public static Config GetFactoryDefaults()
        {
            if (_factoryDefaults != null)
                return _factoryDefaults;

            Config tpl = null;
            try
            {
                string dir = string.IsNullOrEmpty(CNC.Core.Resources.Path) ? AppDomain.CurrentDomain.BaseDirectory : CNC.Core.Resources.Path;
                string path = Path.Combine(dir, DefaultTemplateName);

                if (File.Exists(path))
                {
                    var doc = XDocument.Load(path);
                    if (ConfigStore.IsLegacy(doc))
                    {
                        using (var r = doc.Root.CreateReader())
                            tpl = (Config)legacySerializer.Deserialize(r);
                    }
                    else
                    {
                        // v2 sectioned: Core carries the scalar Config props; Jog / JogUiMetric are their own sections.
                        tpl = (Config)DeserializeSection(doc, "Core", coreSerializer) ?? new Config();
                        if (DeserializeSection(doc, "Jog", new XmlSerializer(typeof(JogConfig))) is JogConfig jog)
                            tpl.Jog = jog;
                        if (DeserializeSection(doc, "JogUiMetric", new XmlSerializer(typeof(JogUIConfig))) is JogUIConfig jum)
                            tpl.JogUiMetric = jum;
                    }
                }
            }
            catch { tpl = null; }

            _factoryDefaults = tpl ?? new Config();
            return _factoryDefaults;
        }

        // Deserialize one <section key="..."> payload from a v2 config document.
        private static object DeserializeSection(XDocument doc, string key, XmlSerializer ser)
        {
            try
            {
                var payload = doc.Root.Elements("section")
                                      .FirstOrDefault(sec => (string)sec.Attribute("key") == key)
                                     ?.Elements().FirstOrDefault();
                if (payload == null)
                    return null;
                using (var r = payload.CreateReader())
                    return ser.Deserialize(r);
            }
            catch { return null; }
        }

        // The per-user config directory (%AppData%\ioSender\). Created on first use; on the first run after this
        // relocation (no App.config there yet) the app folder's existing user files are migrated in (moved) so
        // nothing is lost, or seeded from the template on a fresh install. Falls back to the app folder if AppData
        // can't be resolved/created.
        private static string ResolveUserConfigDir()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ioSender");
                Directory.CreateDirectory(userDir);
                if (!userDir.EndsWith("\\"))
                    userDir += "\\";

                if (!File.Exists(Path.Combine(userDir, "App.config")))
                    SeedUserConfigDir(appDir, userDir);

                return userDir;
            }
            catch
            {
                return appDir;   // AppData unavailable - keep the legacy app-folder behaviour
            }
        }

        // First-run migration: MOVE the user-written files that exist in the app folder into the per-user folder,
        // so upgrading keeps the current settings/keymaps/probe defs/backups AND leaves the app folder holding
        // only the read-only Default-App.config template (no stale App.config to later override the defaults).
        // If there was no existing App.config to migrate (fresh install), seed it by COPYING the template.
        // Best-effort: any move/copy failure is skipped, never fatal.
        private static void SeedUserConfigDir(string appDir, string userDir)
        {
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "App.config", "App.config.bak", "KeyMap0.xml", "ControllerMap.xml",
                    "settings.txt", "offsets.nc", "ProbeDefinitions.xml",
                    "LoadStock.xml", "AutoSquare.xml", "StepperCalScratch.xml", "SurfaceSpoilboard.xml"
                };
                try
                {
                    foreach (var f in Directory.GetFiles(appDir, "*Profiles.xml")) names.Add(Path.GetFileName(f));
                    foreach (var f in Directory.GetFiles(appDir, "*.macro")) names.Add(Path.GetFileName(f));
                }
                catch { /* directory listing best-effort */ }

                foreach (var name in names)
                {
                    string src = Path.Combine(appDir, name), dst = Path.Combine(userDir, name);
                    if (File.Exists(src) && !File.Exists(dst))
                        try { File.Move(src, dst); } catch { }
                }

                // grbl settings restore points (a subfolder): move each file across.
                string sbSrc = Path.Combine(appDir, "settings-backups"), sbDst = Path.Combine(userDir, "settings-backups");
                if (Directory.Exists(sbSrc))
                {
                    try { Directory.CreateDirectory(sbDst); } catch { }
                    foreach (var f in Directory.GetFiles(sbSrc))
                    {
                        string dst = Path.Combine(sbDst, Path.GetFileName(f));
                        if (!File.Exists(dst))
                            try { File.Move(f, dst); } catch { }
                    }
                }

                // Fresh install (no existing App.config was migrated): seed the per-user App.config by copying the
                // shipped read-only template so first-run starts from the curated defaults.
                string userCfg = Path.Combine(userDir, "App.config");
                string template = Path.Combine(appDir, DefaultTemplateName);
                if (!File.Exists(userCfg) && File.Exists(template))
                    try { File.Copy(template, userCfg); } catch { }
            }
            catch { /* migration is best-effort; a missing file just re-initialises to defaults */ }
        }

        // Parse command-line args and load the config file (populates Base). Must run BEFORE any
        // control Loaded handlers that read AppConfig.Settings.Base (e.g. JogControl), so call it
        // synchronously at construction. Returns 1 on config error (caller should exit), else 0.
        public int LoadConfig(string appname)
        {
            int status = 0;
            _selectPort = false;
            _startupPort = _startupBaud = string.Empty;
            string port = string.Empty, baud = string.Empty;

            // Read-only shipped resources (CSVs, images, the App.config template) are read from the app folder;
            // all user-written state lives in a per-user folder (%AppData%\ioSender), seeded from the app folder
            // on first run so an existing install's settings/keymaps/backups carry over. A -configpath arg below
            // still overrides ConfigPath (portable use).
            CNC.Core.Resources.Path = AppDomain.CurrentDomain.BaseDirectory;
            CNC.Core.Resources.ConfigPath = ResolveUserConfigDir();

            string[] args = Environment.GetCommandLineArgs();

            int p = 0;
            while (p < args.GetLength(0)) switch (args[p++].ToLowerInvariant())
                {
                    case "-inifile":
                        CNC.Core.Resources.IniName = GetArg(args, p++);
                        break;

                    case "-debugfile":
                        CNC.Core.Resources.DebugFile = GetArg(args, p++);
                        break;

                    case "-configpath":
                        var path = GetArg(args, p++);
                        if(Path.IsPathRooted(path) && Directory.Exists(path))
                            Resources.ConfigPath = path + (path.EndsWith("\\") ? string.Empty : "\\");
                        else
                            MessageBox.Show("Invalid -configpath argument", "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                        break;

                    case "-locale":
                    case "-language": // deprecated
                        CNC.Core.Resources.Locale = GetArg(args, p++);
                        break;

                    case "-port":
                        port = GetArg(args, p++);
                        break;

                    case "-baud":
                        baud = GetArg(args, p++);
                        break;

                    case "-selectport":
                        _selectPort = true;
                        break;

                    case "-islegacy":
                        CNC.Core.Resources.IsLegacyController = true;
                        break;

                    // Preload a program on startup: --LoadFile <path> opens a single g-code file,
                    // --LoadFolder <path> combines a folder of per-toolpath .nc files (as Load Folder does).
                    case "--loadfile":
                        {
                            var f = GetArg(args, p++);
                            if (!string.IsNullOrEmpty(f) && File.Exists(f))
                                FileName = f;
                            break;
                        }

                    case "--loadfolder":
                        {
                            var d = GetArg(args, p++);
                            if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                                FolderName = d;
                            break;
                        }

                    default:
                        if (!args[p - 1].EndsWith(".exe") && File.Exists(args[p - 1]))
                            FileName = args[p - 1];
                        break;
                }

            if (!Load(CNC.Core.Resources.IniFile))
            {
                if (MessageBox.Show(LibStrings.FindResource("CreateConfig"), appname, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    // Never silently destroy an existing non-empty config: if Load failed but a file is
                    // present (a transient lock that outlasted the retries, or genuine corruption), copy
                    // it aside before overwriting so the user's settings are recoverable.
                    try
                    {
                        string ini = CNC.Core.Resources.IniFile;
                        if (File.Exists(ini) && new FileInfo(ini).Length > 0)
                            File.Copy(ini, ini + ".bak", true);
                    }
                    catch { /* best effort - do not block startup on a backup failure */ }

                    if (!Save(CNC.Core.Resources.IniFile))
                    {
                        MessageBox.Show(LibStrings.FindResource("CreateConfigFail"), appname);
                        status = 1;
                    }
                }
                else
                    return 1;
            }

            if (Base == null)   // Load failed and no usable config was created - cannot continue safely
                return 1;

            Base.Themes.Add("Standard", LibStrings.FindResource("ThemeDefault"));
            Base.Themes.Add("Black", LibStrings.FindResource("ThemeBlack"));
            Base.Themes.Add("Dark", LibStrings.FindResource("ThemeDark"));
            Base.Themes.Add("Light", LibStrings.FindResource("ThemeLight"));
            Base.Themes.Add("White", LibStrings.FindResource("ThemeWhite"));

            // One-shot: a tab introduced after layouts/tab-orders started persisting won't be in a saved tree
            // or Config.Tabs, so it would be filtered out and never built (hence not even listed in the editor
            // to re-add). Inject it once - into the layout tree (after Probing) and the legacy Config.Tabs if the
            // user has a customised list - then flag it so the user can hide it afterwards like any tab.
            if (!Base.HeightMapTabMigrated)
            {
                Base.HeightMapTabMigrated = true;

                var tabsSlot = layoutSection?.Root?.Slot(LayoutKeys.SlotTabs);
                if (tabsSlot != null && tabsSlot.Items.FindIndex(n => n.Component == LayoutKeys.HeightMap) < 0)
                {
                    int after = tabsSlot.Items.FindIndex(n => n.Component == LayoutKeys.Probing);
                    tabsSlot.Items.Insert(after >= 0 ? after + 1 : tabsSlot.Items.Count, new LayoutNode(LayoutKeys.HeightMap));
                }
                if (Base.Tabs.Count > 0 && !Base.Tabs.Contains(LayoutKeys.HeightMap))
                {
                    int after = Base.Tabs.FindIndex(t => t == LayoutKeys.Probing);
                    Base.Tabs.Insert(after >= 0 ? after + 1 : Base.Tabs.Count, LayoutKeys.HeightMap);
                }
                _migratedFormat = true;   // persist the injected layout/flag via the save below
            }

            // One-shot: move Load Stock (now "Start Job") to the front of the tab order for existing saved
            // layouts that pin its old position. Mirrors the HeightMap injection above - guarded by a flag so a
            // later manual reorder is respected.
            if (!Base.StartJobTabFirstMigrated)
            {
                Base.StartJobTabFirstMigrated = true;

                var tabsSlot = layoutSection?.Root?.Slot(LayoutKeys.SlotTabs);
                if (tabsSlot != null)
                {
                    int idx = tabsSlot.Items.FindIndex(n => n.Component == LayoutKeys.LoadStock);
                    if (idx > 0)
                    {
                        var node = tabsSlot.Items[idx];
                        tabsSlot.Items.RemoveAt(idx);
                        tabsSlot.Items.Insert(0, node);
                    }
                }
                if (Base.Tabs.Count > 0)
                {
                    Base.Tabs.Remove(LayoutKeys.LoadStock);
                    Base.Tabs.Insert(0, LayoutKeys.LoadStock);
                }

                // Also drop the F-key from an already-seeded "Start Job" macro: it used to auto-bind the first
                // free F-key (F1); Start Job is driven from its tab now, so clear the default assignment once.
                var startJob = Base.Macros?.FirstOrDefault(m => string.Equals(m.Name, "Start Job", StringComparison.OrdinalIgnoreCase));
                if (startJob != null)
                    startJob.FKey = 0;

                _migratedFormat = true;   // persist the reordered layout/flag via the save below
            }

            // Keep the layout tree's top-level tab order in sync with the legacy Config.Tabs (still the
            // editor's source until the layout editor edits the tree). Transitional - tree drives the build.
            if (layoutSection != null)
                TabOrder.Apply(layoutSection.Root, Base.Tabs);

            // The load migrated the on-disk format (legacy v1 -> sectioned v2) or imported a legacy
            // standalone file: persist the converted form now so the stored config is canonical. The
            // previous file is preserved as .bak by the atomic Save (recoverable on a downgrade).
            if (_migratedFormat)
            {
                Save(CNC.Core.Resources.IniFile);
                _migratedFormat = false;
                DeleteFoldedInFiles();   // their data is now in App.config; drop the redundant standalone files
            }

            _startupPort = port;
            _startupBaud = baud;

            return status;
        }

        // Open the startup connection (the saved target, or via PortDialog when -selectport / no saved
        // port), then run the controller handshake. Safe to defer to ApplicationIdle so the window
        // paints first; LoadConfig() must already have populated Base.
        public int OpenConnection(string appname, GrblViewModel model, System.Windows.Threading.Dispatcher dispatcher)
        {
            int status = 0;
            string port = _startupPort;

            if (!string.IsNullOrEmpty(port))
                _selectPort = false;

            if (!_selectPort)
            {
                if (!string.IsNullOrEmpty(port))
                    setPort(port, _startupBaud);
                OpenStreamFor(model, dispatcher);
            }

            if ((Comms.com == null || !Comms.com.IsOpen) && string.IsNullOrEmpty(port))
            {
                PortDialog portsel = new PortDialog();

                port = portsel.ShowDialog(Base.PortParams);
                if (string.IsNullOrEmpty(port))
                    status = 2;

                else
                {
                    PersistSimulatorChoice(portsel);
                    setPort(port, string.Empty);
                    OpenStreamFor(model, dispatcher);
                    Save(CNC.Core.Resources.IniFile);
                }
            }

            return InitConnectedController(appname, model, status);
        }

        // Open the comms stream for the current Base.PortParams, picking the transport from the
        // target string (ws:// / COMx / host:port). Shared by startup and the Connect menu item.
        private void OpenStreamFor(GrblViewModel model, System.Windows.Threading.Dispatcher dispatcher)
        {
            EnsureSimulatorRunning();   // launch the bundled simulator first if the saved target is it
#if USEWEBSOCKET
            if (Base.PortParams.ToLower().StartsWith("ws://"))
                new WebsocketStream(Base.PortParams, dispatcher);
            else
#endif
            if (Base.PortParams.ToLower().StartsWith("com"))
                new SerialStream(Base.PortParams, Base.ResetDelay, dispatcher);
            else if (Base.PortParams.Contains(":")) // host:port (IP or hostname)
                new TelnetStream(Base.PortParams, dispatcher);
            else
#if USEELTIMA
                new EltimaStream(Config.PortParams, Config.ResetDelay, dispatcher);
#else
                new SerialStream(Base.PortParams, Base.ResetDelay, dispatcher);
#endif
            // Report the target only once the link is actually open (drives the green/red target box).
            model.ConnectionTarget = (Comms.com != null && Comms.com.IsOpen) ? Base.PortParams : null;
        }

        // Launch is decoupled: the simulator is now run standalone by the user (it opens its own 3D view +
        // config dialog and listens on its port), and ioSender simply connects to 127.0.0.1:<port> as a
        // network target. ioSender no longer launches or manages the simulator process, so this is a no-op.
        private void EnsureSimulatorRunning()
        {
        }

        // Remember an IP address to default the Connect dialog's network tab to next time. Call once a
        // connection is fully up (GrblInfo loaded, so $I/GrblInfo.IpAddress is available). On a network
        // connection store the IP just used (most recent wins); on a serial connection seed it from the
        // controller's reported IP only while still unset. The bundled simulator (127.0.0.1) is excluded.
        public void CaptureConnectedIp()
        {
            string target = Base.PortParams ?? string.Empty, lower = target.ToLower();
            bool isNetwork = !Base.StartSimulator && !lower.StartsWith("com") && target.Contains(":");

            string ip = null;
            if (isNetwork)
            {
                string host = lower.StartsWith("ws://") ? target.Substring(5) : target;   // [ws://]host:port
                int c = host.IndexOf(':');
                ip = c > 0 ? host.Substring(0, c) : host;
            }
            else if (string.IsNullOrEmpty(Base.NetworkHost) && !string.IsNullOrWhiteSpace(GrblInfo.IpAddress))
                ip = GrblInfo.IpAddress;   // serial: seed from the controller's $I-reported IP

            if (!string.IsNullOrWhiteSpace(ip) && ip != Base.NetworkHost)
            {
                Base.NetworkHost = ip;
                Save(CNC.Core.Resources.IniFile);
            }
        }

        // Persist whether the chosen connection is the bundled simulator (and how to launch it) so a
        // later startup auto-reconnect to the same target can bring the simulator up first.
        private void PersistSimulatorChoice(PortDialog portsel)
        {
            Base.StartSimulator = portsel.IsSimulatorConnection;
            if (portsel.IsSimulatorConnection)
            {
                Base.SimulatorExe = portsel.SelectedSimulatorExe;
                Base.SimulatorArgs = portsel.SelectedSimulatorArgs;
            }
        }

        // Show the connection dialog and connect to the chosen target without reloading config.
        // Used by the Connect menu item when the app is running but not connected.
        public int Connect(string appname, GrblViewModel model, System.Windows.Threading.Dispatcher dispatcher)
        {
            PortDialog portsel = new PortDialog();

            string port = portsel.ShowDialog(Base.PortParams);
            if (string.IsNullOrEmpty(port))
                return 2;

            PersistSimulatorChoice(portsel);
            setPort(port, string.Empty);
            OpenStreamFor(model, dispatcher);
            Save(CNC.Core.Resources.IniFile);

            return InitConnectedController(appname, model, 0);
        }

        // Connect to a specific target without showing the dialog. Used by the "prefer network" auto-migrate
        // (serial -> network) flow; a migrated network target is always a real controller, never the simulator.
        public int ConnectTo(string appname, GrblViewModel model, System.Windows.Threading.Dispatcher dispatcher, string target)
        {
            Base.StartSimulator = false;
            setPort(target, string.Empty);
            OpenStreamFor(model, dispatcher);
            Save(CNC.Core.Resources.IniFile);

            return InitConnectedController(appname, model, 0);
        }

        // Run the controller handshake (MPG detection, status reporting) once the stream is open.
        private int InitConnectedController(string appname, GrblViewModel model, int status)
        {
            if (Comms.com != null && Comms.com.IsOpen)
            {
                Comms.com.DataReceived += model.DataReceived;

                // Auto-reconnect: let the view model react when the link drops / is restored.
                model.PollInterval = Base.PollInterval;
                Comms.com.ConnectionLost += model.OnConnectionLost;
                Comms.com.Reconnected += model.OnReconnected;

                CancellationToken cancellationToken = new CancellationToken();

                // Start MPG detection from a clean slate. A reconnect or transport switch (e.g. network -> serial)
                // delivers a one-off burst - a controller reset/startup banner, stale data from the previous link,
                // or an auto-report backlog - and ioSender never enables auto-reporting itself, so any such traffic
                // arriving in the detection window below is misread as a pendant polling Grbl -> a false "pendant
                // active" prompt. A single purge doesn't cover it: the burst can land DURING the 500 ms listen. So
                // drain repeatedly over a short settle window first; a genuine pendant (or real continuous auto-
                // reporting) keeps arriving and is still caught by the detection below.
                for (int drain = 0; drain < 6; drain++)
                {
                    Comms.com.PurgeQueue();
                    Thread.Sleep(40);
                }

                // Wait 400ms to see if a MPG is polling Grbl...

                new Thread(() =>
                {
                    MPGactive = WaitFor.SingleEvent<string>(
                    cancellationToken,
                    null,
                    a => model.OnRealtimeStatusProcessed += a,
                    a => model.OnRealtimeStatusProcessed -= a,
                    500);
                }).Start();

                while (MPGactive == null)
                    EventUtils.DoEvents();

                if (MPGactive == true)
                {
                    MPGactive = null;

                    new Thread(() =>
                    {
                        MPGactive = WaitFor.SingleEvent<string>(
                        cancellationToken,
                        null,
                        a => model.OnRealtimeStatusProcessed += a,
                        a => model.OnRealtimeStatusProcessed -= a,
                        500, () => Comms.com.WriteByte(GrblConstants.CMD_STATUS_REPORT_ALL));
                    }).Start();

                    while (MPGactive == null)
                        EventUtils.DoEvents();

                    if (MPGactive == true)
                    {
                        // Window 1 only saw *some* unsolicited traffic; window 2 just pulled a fresh full status
                        // report, so model.IsMPGActive now reflects the controller's authoritative MPG state. If no
                        // pendant actually has control, this was a reconnect / auto-report / burst false positive -
                        // proceed and do NOT raise the dialog (which would otherwise wait forever for a "pendant
                        // released" event that can never come, since there is no pendant). Turn off auto-reporting
                        // if that was the source so it stops tripping detection.
                        if (model.IsMPGActive != true)
                        {
                            MPGactive = false;
                            if (model.AutoReportingEnabled && model.AutoReportInterval > 0)
                            {
                                model.AutoReportingEnabled = false;
                                Comms.com.WriteByte(GrblConstants.CMD_AUTO_REPORTING_TOGGLE);
                            }
                        }
                    }
                }

                // ...if so show dialog for wait for it to stop polling and relinquish control.
                if (MPGactive == true)
                {
                    MPGPending await = new MPGPending(model) { Owner = Application.Current.MainWindow };
                    await.ShowDialog();
                    if (await.Cancelled)
                    {
                        Comms.com.Close(); //!!
                        status = 2;
                    }
                }

                model.IsReady = true;
            }
            else if (status != 2)
            {
                MessageBox.Show(string.Format(LibStrings.FindResource("ConnectFailed"), Base.PortParams), appname, MessageBoxButton.OK, MessageBoxImage.Error);
                status = 2;
            }

            return status;
        }

        private string GetArg(string[] args, int i)
        {
            return i < args.GetLength(0) ? args[i] : null;
        }
    }

    public class Controller
    {
        GrblViewModel model;

        public enum RestartResult
        {
            Ok = 0,
            NoResponse,
            Close,
            Exit
        }

        public Controller (GrblViewModel model)
        {
            this.model = model;
        }

        public bool ResetPending { get; private set; } = false;
        public string Message { get; private set; }

        public RestartResult Restart ()
        {
            Message = model.Message;
            model.Message = string.Format(LibStrings.FindResource("MsgWaiting"), AppConfig.Settings.Base.PortParams);

            string response = GrblInfo.Startup(model);

            if (response.StartsWith("<"))
            {
                if (model.GrblState.State != GrblStates.Unknown)
                {
                    switch (model.GrblState.State)
                    {
                        case GrblStates.Alarm:

                            model.Poller.SetState(AppConfig.Settings.Base.PollInterval);

                            if (!model.SysCommandsAlwaysAvailable) switch(model.GrblState.Substate)
                            {
                                case 1: // Hard limits
                                    if (!GrblInfo.IsLoaded)
                                    {
                                        if (model.LimitTriggered)
                                        {
                                            MessageBox.Show(string.Format(LibStrings.FindResource("MsgNoCommAlarm"), model.GrblState.Substate.ToString()), "ioSender");
                                            if (AttemptReset())
                                                model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                                            else
                                            {
                                                MessageBox.Show(LibStrings.FindResource("MsgResetFailed"), "ioSender");
                                                return RestartResult.Close;
                                            }
                                        }
                                        else if (AttemptReset())
                                            model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                                    }
                                    else
                                        response = string.Empty;
                                    break;

                                case 2: // Soft limits
                                    if (!GrblInfo.IsLoaded)
                                    {
                                        MessageBox.Show(string.Format(LibStrings.FindResource("MsgNoCommAlarm"), model.GrblState.Substate.ToString()), "ioSender");
                                        if (AttemptReset())
                                            model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                                        else
                                        {
                                            MessageBox.Show(LibStrings.FindResource("MsgResetFailed"), "ioSender");
                                            return RestartResult.Close;
                                        }
                                    }
                                    else
                                        response = string.Empty;
                                    break;

                                case 10: // EStop
                                    if (GrblInfo.IsGrblHAL && model.Signals.Value.HasFlag(Signals.EStop))
                                    {
                                        MessageBox.Show(LibStrings.FindResource("MsgEStop"), "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                                        while (!AttemptReset() && model.GrblState.State == GrblStates.Alarm)
                                        {
                                            if (MessageBox.Show(LibStrings.FindResource("MsgEStopExit"), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                                                return RestartResult.Close;
                                        }
                                    }
                                    else
                                        AttemptReset();
                                    if (!GrblInfo.IsLoaded)
                                        model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                                    break;

                                case 11: // Homing required
                                    if (GrblInfo.IsLoaded)
                                        response = string.Empty;
                                    else
                                        Message = LibStrings.FindResource("MsgHome");
                                    break;

                                case 17: // Motor fault
                                        if (!(GrblInfo.IsGrblHAL && model.Signals.Value.HasFlag(Signals.MotorFault)))
                                        {
                                            AttemptReset();
                                            if (!GrblInfo.IsLoaded)
                                                model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                                        }
                                    break;


                            }
                            break;

                        case GrblStates.Tool:
                            Comms.com.WriteByte(GrblConstants.CMD_STOP);
                            break;

                        case GrblStates.Door:
                            if (!GrblInfo.IsLoaded)
                            {
                                if (MessageBox.Show(LibStrings.FindResource("MsgDoorOpen"), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                                    return RestartResult.Close;
                                else
                                {
                                    bool exit = false;
                                    do
                                    {
                                        Comms.com.PurgeQueue();

                                        bool? res = null;
                                        CancellationToken cancellationToken = new CancellationToken();

                                        new Thread(() =>
                                        {
                                            res = WaitFor.SingleEvent<string>(
                                                cancellationToken,
                                                s => TrapReset(s),
                                                a => model.OnGrblReset += a,
                                                a => model.OnGrblReset -= a,
                                                200, () => Comms.com.WriteByte(GrblConstants.CMD_STATUS_REPORT));
                                        }).Start();

                                        while (res == null)
                                            EventUtils.DoEvents();

                                        if (!(exit = !model.Signals.Value.HasFlag(Signals.SafetyDoor)))
                                        {
                                            if (MessageBox.Show(LibStrings.FindResource("MsgDoorExit"), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                                            {
                                                exit = true;
                                                return RestartResult.Close;
                                            }
                                        }
                                    } while (!exit);
                                }
                                if(model.GrblState.State == GrblStates.Door && model.GrblState.Substate == 0)
                                    Comms.com.WriteByte(GrblConstants.CMD_RESET);
                            }
                            else
                            {
                                MessageBox.Show(LibStrings.FindResource("MsgDoorPersist"), "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                                response = string.Empty;
                            }
                            break;

                        case GrblStates.Hold:
                        case GrblStates.Sleep:
                            if (MessageBox.Show(string.Format(LibStrings.FindResource("MsgNoComm"), model.GrblState.State.ToString()),
                                                    "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                                return RestartResult.Close;
                            else if (!AttemptReset())
                            {
                                MessageBox.Show(LibStrings.FindResource("MsgResetExit"), "ioSender");
                                return RestartResult.Close;
                            }
                            break;

                        case GrblStates.Idle:
                            if (response.Contains("|SD:Pending"))
                                AttemptReset();
                            break;
                    }
                }
            }
            else
            {
                // No (or unparseable) response from the controller. Don't force-quit: offer a choice so the
                // operator can keep ioSender open to inspect the console, save the -debugfile log, or retry a
                // reconnect. Yes = exit (the old behaviour); No = stay open (RestartResult.NoResponse).
                string detail = response == string.Empty
                                    ? LibStrings.FindResource("MsgNoResponseExit")
                                    : string.Format(LibStrings.FindResource("MsgBadResponseExit"), response);
                return MessageBox.Show(detail + "\r\n\r\nExit ioSender now? Choose No to keep it open so you can inspect the console, save the log, or reconnect.",
                                       "ioSender - no controller response", MessageBoxButton.YesNo, MessageBoxImage.Stop) == MessageBoxResult.Yes
                    ? RestartResult.Exit
                    : RestartResult.NoResponse;
            }

            return response == string.Empty ? RestartResult.NoResponse : RestartResult.Ok;
        }

        private void TrapReset(string rws)
        {
            ResetPending = false;
        }

        private bool AttemptReset()
        {
            ResetPending = true;
            Comms.com.PurgeQueue();

            bool? res = null;
            CancellationToken cancellationToken = new CancellationToken();

            new Thread(() =>
            {
                res = WaitFor.SingleEvent<string>(
                    cancellationToken,
                    s => TrapReset(s),
                    a => model.OnGrblReset += a,
                    a => model.OnGrblReset -= a,
                    AppConfig.Settings.Base.ResetDelay, () => Comms.com.WriteByte(GrblConstants.CMD_RESET));
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            return !ResetPending;
        }
    }
}
