/*
 * FeedsAndSpeedsView.xaml.cs - part of CNC Controls library
 *
 * "Feeds & Speeds" tab: reads the JSON the ioSenderV2 Fusion add-in's Feeds and Speeds ->
 * Export writes (~/Downloads/ioSenderV2/<docName>.json), runs FeedsSpeedsAdvisor (a ported
 * material chip-load/RPM table cross-checked against the CONNECTED controller's actual
 * grblHAL limits) over every operation, and writes a companion <docName>-apply.json the
 * Fusion add-in's Apply action reads back. An optional "Ask AI to review" pass (only shown
 * when an AI review key is set - Settings > App) shows a second opinion in the status box
 * before anything is written - it never silently overrides the table.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class FeedsAndSpeedsView : UserControl, ICNCView
    {
        // One display row per (operation, parameter) combination. Recompute() creates these up front with
        // table-only data; a later "Ask AI to review" fills in AiRecommended/AiRecommendedValue/Notes on
        // the SAME row objects, and PreferAi can be toggled by the user (or Select All) afterward - all
        // three need INotifyPropertyChanged so the already-rendered DataGrid cells actually refresh instead
        // of silently going stale.
        public class Row : INotifyPropertyChanged
        {
            public string Operation { get; set; }
            public string Parameter { get; set; }
            public string Current { get; set; }
            public string Recommended { get; set; }
            public string MachineLimit { get; set; }
            public string Verdict { get; set; }

            private string _notes;
            public string Notes { get { return _notes; } set { _notes = value; OnChanged(); } }

            private string _aiRecommended;
            public string AiRecommended { get { return _aiRecommended; } set { _aiRecommended = value; OnChanged(); } }

            private bool _preferAi;
            public bool PreferAi { get { return _preferAi; } set { _preferAi = value; OnChanged(); } }

            // Not shown - carried for the apply-file writer.
            internal string OpId;
            internal string Key;
            internal double? CurrentValue;
            internal double? ApplyValue;
            internal double? AiRecommendedValue;
            internal FeedsSpeedsVerdict RawVerdict;
            // The table-only notes AddRow first computed, kept separate from the live Notes property so a
            // fresh "Ask AI to review" run (or a cancel) can rebuild Notes = TableNotes + AI comment
            // without needing to "un-append" a previous AI comment string.
            internal string TableNotes;

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // Sentinel dropdown value: derive each SETUP's material from its own name instead of using one
        // material for the whole document. Mirrors SRWCommands' DERIVED_MATERIAL sentinel.
        private const string DerivedMaterial = "Derived";

        private static string DownloadsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ioSenderV2");

        private string _exportPath;
        private string _archiveStamp;   // shared timestamp pairing this load's archived export with any later apply-file archive
        private FeedsSpeedsExport _export;
        private readonly Dictionary<string, OperationRecommendation> _recommendations = new Dictionary<string, OperationRecommendation>();
        private readonly Dictionary<string, string> _opMaterial = new Dictionary<string, string>();   // op.Id -> resolved material (Derived mode) or null (unresolved)
        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();

        public FeedsAndSpeedsView()
        {
            InitializeComponent();
            dgrResults.ItemsSource = _rows;

            // "Derived" (first item, default) resolves each SETUP's own material from its name (the
            // first '_'-terminated token, e.g. "MDF_BottomSetup" -> MDF) - matches the naming convention
            // SRWCommands' Fusion side now uses. Picking a specific material instead overrides every
            // operation to that one material regardless of setup name.
            cbxMaterial.Items.Add(DerivedMaterial);
            foreach (var material in FeedsSpeedsAdvisor.MaterialRefs.Keys.OrderBy(m => m))
                cbxMaterial.Items.Add(material);
            cbxMaterial.SelectedIndex = 0;

            var aiVisibility = FeedsSpeedsAiReview.IsAvailable
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            btnAskAi.Visibility = aiVisibility;
            cbxAiModel.Visibility = aiVisibility;
            foreach (var (label, modelId) in FeedsSpeedsAiReview.AvailableModels)
                cbxAiModel.Items.Add(new ComboBoxItem { Content = label, Tag = modelId });
            cbxAiModel.SelectedIndex = 0;   // Sonnet 5, per AvailableModels' own ordering

            RefreshTabAvailability();

            // Intro tab only on the very first time this tab is ever opened for this user - after that,
            // default to Load / Import if there's actually a file to load, else Intro again (nothing else
            // to show). A real AppConfig field (persisted in App.config alongside every other setting),
            // not a loose marker file - one less stray file sitting in the config/install folder.
            bool firstTime = !AppConfig.Settings.Base.FeedsAndSpeedsIntroShown;
            tabFeedsSpeeds.SelectedIndex = !firstTime && tabLoad.Visibility == System.Windows.Visibility.Visible ? 1 : 0;
            if (firstTime)
            {
                AppConfig.Settings.Base.FeedsAndSpeedsIntroShown = true;
                AppConfig.Settings.Save();
            }

            // Activate() only re-checks when the user switches TO the Feeds & Speeds top-level tab from
            // another one INSIDE ioSender - it never fires from just alt-tabbing back from Fusion while
            // already sitting on this tab, which is the common case (run Export in Fusion, alt-tab back).
            // Hook the actual app window's Activated event so a fresh export file is noticed either way.
            Loaded += (s, e) =>
            {
                _hostWindow = System.Windows.Window.GetWindow(this);
                if (_hostWindow != null)
                    _hostWindow.Activated += HostWindow_Activated;
            };
            Unloaded += (s, e) =>
            {
                if (_hostWindow != null)
                {
                    _hostWindow.Activated -= HostWindow_Activated;
                    _hostWindow = null;
                }
            };
        }

        private System.Windows.Window _hostWindow;

        private void HostWindow_Activated(object sender, EventArgs e)
        {
            RefreshTabAvailability();
        }

        // Load / Import is only shown when there's actually an export file sitting in the downloads
        // folder to load; Results is only shown once something has actually been loaded (Intro stays
        // visible always - hidden tabs remain in TabControl.Items, just not rendered, so SelectedIndex by
        // position still works once a tab that was hidden becomes visible again).
        private void RefreshTabAvailability()
        {
            bool hasCandidate = Directory.Exists(DownloadsFolder) &&
                new DirectoryInfo(DownloadsFolder).GetFiles("*.json")
                    .Any(f => !f.Name.EndsWith("-apply.json", StringComparison.OrdinalIgnoreCase));
            tabLoad.Visibility = hasCandidate ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            tabResults.Visibility = _export != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.FeedsAndSpeeds; } }
        public bool CanEnable { get { return true; } }   // works offline, analyzing a file - no controller needed

        public void Activate(bool activate, ViewType chgMode)
        {
            // Re-check whenever the user switches to this top-level tab - e.g. they ran Fusion's Export
            // since this view was last shown, so Load / Import should now be available.
            if (activate)
                RefreshTabAvailability();
        }

        public void CloseFile()
        {
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
        }

        #endregion

        private void btnLoadLatest_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            LoadLatestExport();
        }

        private void cbxMaterial_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_export != null)
                Recompute();
        }

        private void LoadLatestExport()
        {
            try
            {
                if (!Directory.Exists(DownloadsFolder))
                {
                    txtLoadStatus.Text = "No export folder found:\n  " + DownloadsFolder +
                                         "\n\nRun Feeds and Speeds -> Export in Fusion first.";
                    return;
                }
                var candidate = new DirectoryInfo(DownloadsFolder)
                    .GetFiles("*.json")
                    .Where(f => !f.Name.EndsWith("-apply.json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (candidate == null)
                {
                    txtLoadStatus.Text = "No export .json found in:\n  " + DownloadsFolder +
                                         "\n\nRun Feeds and Speeds -> Export in Fusion first.";
                    return;
                }

                string json = File.ReadAllText(candidate.FullName);
                _export = JsonSerializer.Deserialize<FeedsSpeedsExport>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                _exportPath = candidate.FullName;
                txtExportPath.Text = candidate.Name;
                txtLoadStatus.Text = "Loaded " + candidate.Name + " - switched to the Results tab.";

                // Archive on every successful load, not just when something ends up flagged - Fusion's
                // Feeds and Speeds command deletes its own copy when the dialog closes, so this is the
                // only record that the check happened at all, even when it comes back clean. One shared
                // timestamp per load so a later apply-file archive (if any) pairs up with this one.
                _archiveStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                ArchiveToLogs(_exportPath, _archiveStamp);

                Recompute();
                RefreshTabAvailability();   // Results just became populated - make its tab visible

                // A successful load is the whole point of this tab - jump straight to Results (index 2)
                // rather than making the user click over manually.
                tabFeedsSpeeds.SelectedIndex = 2;
            }
            catch (Exception ex)
            {
                txtLoadStatus.Text = "Failed to load export: " + ex.Message;
            }
        }

        private void Recompute()
        {
            _rows.Clear();
            _recommendations.Clear();
            _opMaterial.Clear();
            if (_export == null || cbxMaterial.SelectedItem == null)
                return;

            string selected = (string)cbxMaterial.SelectedItem;
            bool derived = selected == DerivedMaterial;
            int changeCount = 0;
            int unresolvedSetups = 0;

            foreach (var setup in _export.Setups ?? new List<FeedsSpeedsSetup>())
            {
                // Derived: each setup resolves its OWN material from its name (the naming convention
                // SRWCommands' Fusion side now uses); a specific dropdown pick overrides every op to that
                // one material regardless of setup name.
                string material = derived ? FeedsSpeedsAdvisor.DeriveMaterialFromSetup(setup.Name) : selected;
                if (derived && material == null)
                    unresolvedSetups++;

                foreach (var op in setup.Operations ?? new List<FeedsSpeedsOperation>())
                {
                    if (op.Error != null)
                    {
                        _rows.Add(new Row { Operation = op.Name ?? op.Id, Parameter = "(error)", Notes = op.Error });
                        continue;
                    }

                    _opMaterial[op.Id] = material;
                    if (material == null)
                    {
                        _rows.Add(new Row
                        {
                            Operation = op.Name ?? op.Id,
                            Parameter = "(material)",
                            Notes = $"could not derive a material from setup \"{setup.Name}\" - " +
                                    "expected a prefix like \"MDF_...\"; pick a specific material instead to analyze it anyway",
                        });
                        continue;
                    }

                    var rec = FeedsSpeedsAdvisor.Evaluate(op, material);
                    FeedsSpeedsAdvisor.ApplyMachineLimits(rec);
                    _recommendations[op.Id] = rec;

                    AddRow(op, "RPM", "rpm", rec.Rpm);
                    AddRow(op, "Cutting feed (mm/min)", "cutting_feed", rec.CuttingFeed);
                    AddRow(op, "Plunge feed (mm/min)", "plunge_feed", rec.PlungeFeed);
                    AddRow(op, "Axial step (mm)", "axial_step", rec.AxialStep);
                    AddRow(op, "Radial step (mm)", "radial_step", rec.RadialStep);

                    changeCount += new[] { rec.Rpm, rec.CuttingFeed, rec.PlungeFeed, rec.AxialStep, rec.RadialStep }
                        .Count(pv => pv.Verdict == FeedsSpeedsVerdict.Change);
                }
            }

            string unresolvedMsg = unresolvedSetups > 0 ? $" ({unresolvedSetups} setup(s) had no derivable material)" : "";
            txtStatus.Text = $"{_rows.Count} row(s) across {_recommendations.Count} operation(s); " +
                             $"{changeCount} parameter(s) flagged Change.{unresolvedMsg}";
        }

        private void AddRow(FeedsSpeedsOperation op, string label, string key, ParameterVerdict pv)
        {
            if (pv.Verdict == FeedsSpeedsVerdict.None && pv.Current == null)
                return;   // parameter not exposed on this op at all - skip rather than clutter the grid

            string tableNotes = string.Join("; ", pv.Notes);
            _rows.Add(new Row
            {
                Operation = op.Name ?? op.Id,
                Parameter = label,
                Current = pv.Current?.ToString("F1") ?? "-",
                Recommended = pv.Recommended?.ToString("F1") ?? "-",
                MachineLimit = pv.MachineLimit?.ToString("F0") ?? "-",
                Verdict = pv.Verdict.ToString(),
                Notes = tableNotes,
                TableNotes = tableNotes,
                OpId = op.Id,
                Key = key,
                CurrentValue = pv.Current,
                ApplyValue = pv.Recommended,
                RawVerdict = pv.Verdict,
            });
        }

        // Set while a review is running so PreviewKeyDown (Esc / Ctrl+C) knows there's something to
        // cancel - also doubles as the "is a review in progress" flag so those keys are only hijacked
        // from their normal behavior (Ctrl+C = copy) during that narrow window.
        private System.Threading.CancellationTokenSource _aiCts;

        // Shared with PreviewKeyDown so a cancel keypress can log "Cancelling..." immediately - an in-
        // flight Anthropic call can take 20-30s, and without this line there was no visible sign the
        // keypress had even registered until it eventually finished.
        private StringBuilder _aiLog;

        // Tolerance for "is the AI's number basically the same as this other value" - a bit looser than
        // the grid's own F1 display rounding (0.05) since the AI's number arrives as raw JSON, not
        // rounded to our display precision.
        private static bool IsClose(double a, double b) => Math.Abs(a - b) <= Math.Max(0.05, 0.02 * Math.Abs(b));

        private void ClearAiColumns()
        {
            foreach (var row in _rows)
            {
                row.AiRecommended = null;
                row.AiRecommendedValue = null;
                row.PreferAi = false;
                row.Notes = row.TableNotes;   // drop any previously-appended "AI: ..." comment
            }
        }

        private async void btnAskAi_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_export == null || cbxMaterial.SelectedItem == null)
                return;

            // Reveal the AI/Prefer columns the moment the button is pressed, regardless of outcome - they
            // stay hidden until then since there's nothing in them before this.
            colAiRecommended.Visibility = System.Windows.Visibility.Visible;
            colPreferAi.Visibility = System.Windows.Visibility.Visible;

            // Every run starts clean - a re-run (different model, retry after a partial failure, etc.)
            // should never show stale AI opinions left over from a previous pass.
            ClearAiColumns();

            // Review EVERY operation the table evaluated, independent of its own Change/Nudge/Ok verdict -
            // the whole point of asking the AI is that it might catch something the table's arithmetic
            // didn't flag, so gating this on the table's own opinion would defeat that.
            var toReview = new List<(FeedsSpeedsOperation op, OperationRecommendation rec, string material)>();
            foreach (var setup in _export.Setups ?? new List<FeedsSpeedsSetup>())
                foreach (var op in setup.Operations ?? new List<FeedsSpeedsOperation>())
                {
                    if (op.Error != null || !_recommendations.TryGetValue(op.Id, out var rec))
                        continue;
                    // Per-op resolved material (Derived mode: each setup's own; otherwise the one dropdown
                    // pick) - same value Recompute() evaluated this op's recommendation against.
                    _opMaterial.TryGetValue(op.Id, out var material);
                    toReview.Add((op, rec, material));
                }

            if (toReview.Count == 0)
            {
                txtStatus.Text = "No operations available to review - load an export first.";
                return;
            }

            string model = (cbxAiModel.SelectedItem as ComboBoxItem)?.Tag as string ?? FeedsSpeedsAiReview.DefaultModel;

            // txtStatus becomes a running transcript of this whole run (not just the latest line) - built
            // up in this StringBuilder and re-rendered + scrolled to the bottom after every event.
            _aiLog = new StringBuilder();
            void Log(string line) { _aiLog.AppendLine(line); txtStatus.Text = _aiLog.ToString(); txtStatus.ScrollToEnd(); }

            Log($"Asking {model} to review {toReview.Count} operation(s) - Esc or Ctrl+C to cancel...");

            btnAskAi.IsEnabled = false;
            _aiCts = new System.Threading.CancellationTokenSource();
            var token = _aiCts.Token;
            int inputTokens = 0, outputTokens = 0;
            int failedCount = 0;
            try
            {
                for (int i = 0; i < toReview.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var (op, rec, material) = toReview[i];
                    Log($"[{i + 1}/{toReview.Count}] {op.Name ?? op.Id}...");

                    AiReviewResult result;
                    try
                    {
                        result = await Task.Run(() => FeedsSpeedsAiReview.Review(op, rec, material, model, token), token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;   // real cancellation - let the outer catch handle cleanup
                    }
                    catch (Exception ex)
                    {
                        // One operation's response failing (bad JSON, empty reply, etc.) shouldn't abort
                        // the whole batch - note it on that op's rows and move on to the next operation.
                        failedCount++;
                        Log($"  FAILED: {ex.Message}");
                        foreach (var row in _rows.Where(r => r.OpId == op.Id))
                            row.Notes = string.IsNullOrEmpty(row.TableNotes)
                                ? $"AI: review failed - {ex.Message}"
                                : $"{row.TableNotes}; AI: review failed - {ex.Message}";
                        continue;
                    }

                    inputTokens += result.InputTokens;
                    outputTokens += result.OutputTokens;
                    double runningCost = FeedsSpeedsAiReview.EstimateCostUsd(inputTokens, outputTokens, model);
                    Log($"  {result.InputTokens:N0} in / {result.OutputTokens:N0} out tokens " +
                        $"(running total: {inputTokens:N0}/{outputTokens:N0}, ~${runningCost:F3})");

                    // Fold each parameter's AI opinion onto the matching (already-rendered) grid row: a
                    // value in the AI Says column, and its comment appended to Notes - and also into this
                    // transcript, so the full reasoning is visible without hovering every row's tooltip.
                    foreach (var row in _rows.Where(r => r.OpId == op.Id))
                    {
                        if (!result.Parameters.TryGetValue(row.Key ?? "", out var ai))
                            continue;
                        if (ai.Recommend != null)
                        {
                            row.AiRecommendedValue = ai.Recommend;
                            double val = ai.Recommend.Value;
                            // The prompt asks for null when the AI agrees with the TABLE's recommended
                            // value - but a model can instead form its own opinion and express "leave it
                            // alone" by echoing back CURRENT (observed: table recommended 3.2, AI returned
                            // 1.5 = current, meaning "I disagree with the table, current is fine" - not a
                            // proposed new number). Label those two cases distinctly so a bare number in
                            // this column always means "here's a genuinely different value", never
                            // "here's current again" disguised as a change.
                            bool matchesTable = row.ApplyValue != null && IsClose(val, row.ApplyValue.Value);
                            bool matchesCurrent = row.CurrentValue != null && IsClose(val, row.CurrentValue.Value);
                            if (matchesTable && !matchesCurrent)
                                row.AiRecommended = $"{val:F1} (agrees w/ table)";
                            else if (matchesCurrent && !matchesTable)
                                row.AiRecommended = $"{val:F1} (keep as-is, disagrees w/ table)";
                            else
                                row.AiRecommended = val.ToString("F1");
                        }
                        else
                        {
                            row.AiRecommended = "(agrees)";
                        }
                        if (!string.IsNullOrWhiteSpace(ai.Comment))
                            row.Notes = string.IsNullOrEmpty(row.TableNotes) ? $"AI: {ai.Comment}" : $"{row.TableNotes}; AI: {ai.Comment}";
                        Log($"  {row.Parameter}: {row.AiRecommended}" + (string.IsNullOrWhiteSpace(ai.Comment) ? "" : $" - \"{ai.Comment}\""));
                    }
                }
                double cost = FeedsSpeedsAiReview.EstimateCostUsd(inputTokens, outputTokens, model);
                string failMsg = failedCount > 0 ? $" ({failedCount} operation(s) failed - see their Notes)" : "";
                Log($"\nReviewed {toReview.Count} operation(s) with {model} - {inputTokens:N0} input / " +
                    $"{outputTokens:N0} output tokens (~${cost:F3} estimated list price){failMsg}. Check " +
                    "\"Prefer AI\" on any row to use its number instead of the table's.");
            }
            catch (OperationCanceledException)
            {
                ClearAiColumns();
                Log($"\nAI review cancelled ({inputTokens:N0} input / {outputTokens:N0} output tokens spent " +
                    "before cancelling). No file is written by this step, so there's nothing left to clean up.");
            }
            catch (Exception ex)
            {
                Log("\nAI review failed: " + ex.Message);
            }
            finally
            {
                btnAskAi.IsEnabled = true;
                _aiCts?.Dispose();
                _aiCts = null;
            }
        }

        // Esc or Ctrl+C cancels an in-progress "Ask AI to review" - only hijacks those keys while
        // _aiCts is actually set (a review is running), so normal Escape/Ctrl+C (copy) behavior
        // elsewhere in this tab is completely unaffected otherwise.
        private void FeedsAndSpeedsView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_aiCts == null)
                return;
            bool isEscape = e.Key == System.Windows.Input.Key.Escape;
            bool isCtrlC = e.Key == System.Windows.Input.Key.C &&
                          (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;
            if (isEscape || isCtrlC)
            {
                if (!_aiCts.IsCancellationRequested && _aiLog != null)
                {
                    _aiLog.AppendLine("Cancelling...");
                    txtStatus.Text = _aiLog.ToString();
                    txtStatus.ScrollToEnd();
                }
                _aiCts.Cancel();
                e.Handled = true;
            }
        }

        private void btnSelectAllAi_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            foreach (var row in _rows.Where(r => r.AiRecommendedValue != null))
                row.PreferAi = true;
        }

        private void btnWriteApply_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_exportPath == null)
            {
                AppDialogs.Show("Load an export file first.", "Feeds and Speeds");
                return;
            }

            // Belt-and-suspenders: a DataGridCheckBoxColumn edit normally commits the instant the checkbox
            // is toggled, but if the cell/row is somehow still mid-edit when this click handler runs, the
            // row's bound PreferAi could still be reading the PRE-toggle value below. Force any pending
            // edit to commit first so we never silently apply the table's own value instead of a checked
            // "Prefer AI" override.
            dgrResults.CommitEdit(DataGridEditingUnit.Cell, true);
            dgrResults.CommitEdit(DataGridEditingUnit.Row, true);

            var byOp = new Dictionary<string, Dictionary<string, double>>();
            foreach (var row in _rows)
            {
                if (row.OpId == null)
                    continue;
                // "Prefer AI" is an explicit user override, so it wins regardless of the table's own
                // verdict; otherwise fall back to the table's Change-only gate as before.
                double? value = (row.PreferAi && row.AiRecommendedValue != null) ? row.AiRecommendedValue
                                : row.RawVerdict == FeedsSpeedsVerdict.Change ? row.ApplyValue : null;
                if (value == null)
                    continue;
                if (!byOp.TryGetValue(row.OpId, out var set))
                    byOp[row.OpId] = set = new Dictionary<string, double>();
                set[row.Key] = value.Value;
            }

            if (byOp.Count == 0)
            {
                AppDialogs.Show("Nothing flagged Change - no apply file written.", "Feeds and Speeds");
                return;
            }

            var applyFile = new FeedsSpeedsApplyFile
            {
                Ops = byOp.Select(kv => new FeedsSpeedsApplyOp { Id = kv.Key, Set = kv.Value }).ToList(),
            };

            string applyPath = Path.Combine(Path.GetDirectoryName(_exportPath),
                Path.GetFileNameWithoutExtension(_exportPath) + "-apply.json");
            try
            {
                // CamelCase policy is required here: Fusion's apply_from_file() reads lowercase "ops"/
                // "id"/"set" keys, case-sensitively (no PropertyNameCaseInsensitive on that side, unlike
                // our own export reader below) - without this the file serializes as "Ops"/"Id"/"Set"
                // (the C# property names) and Fusion silently sees an empty ops list ("apply file listed
                // no ops") despite the file having real content.
                File.WriteAllText(applyPath, JsonSerializer.Serialize(applyFile,
                    new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

                // Same stamp as this load's own export archive (set in LoadLatestExport), so the two
                // pair up under matching filenames; falls back to a fresh one if somehow unset.
                string archiveNote = ArchiveToLogs(applyPath, _archiveStamp ?? DateTime.Now.ToString("yyyyMMdd_HHmmss"));

                AppDialogs.Show($"Wrote {byOp.Count} operation(s) to:\n{applyPath}\n\n" +
                                "Switch back to Fusion and pick Action -> Apply." + archiveNote, "Feeds and Speeds");
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Failed to write apply file: " + ex.Message, "Feeds and Speeds");
            }
        }

        // Fusion's Feeds and Speeds command deletes both the export and apply files when it closes (a
        // deliberate "nothing lingers" cleanup), so this is the only remaining record of what was
        // imported and (if anything) what got asked for - archive into the app's logs folder
        // (timestamped, alongside the crash/debug/console logs CNC.Core.Resources.ResolveLogsDirectory()
        // already resolves) so a later "wait, what did we change?" has somewhere to look. Called once per
        // load (the export) and again per apply-file write, sharing one timestamp so the pair matches up.
        // Best-effort: never blocks the actual file write if it fails. Returns a note for the caller's
        // dialog (empty string if archiving itself failed).
        private static string ArchiveToLogs(string path, string stamp)
        {
            try
            {
                string logsDir = System.IO.Path.Combine(CNC.Core.Resources.ResolveLogsDirectory(), "FeedsAndSpeeds");
                Directory.CreateDirectory(logsDir);
                File.Copy(path, Path.Combine(logsDir, $"{stamp}_{Path.GetFileName(path)}"), true);
                return $"\n\nArchived a copy to:\n{logsDir}";
            }
            catch
            {
                return "";   // archiving is a nice-to-have, not worth failing the write over
            }
        }
    }
}
