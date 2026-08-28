/*
 * WorkOrderView.xaml.cs - part of CNC Controls library
 *
 * Odd Jobs "Work Order": the single composer tab that replaced the five fixed job wizards (Surface Stock,
 * Drill/Bore, Counterbore, Pocket, Contour/Slot). The operator adds a TOOLPATH - a named piece of geometry
 * picked from the loops this tab can handle - then adds the OPERATIONS that cut it, in whatever order suits
 * the job. Run compiles every toolpath into ONE program (WorkOrderCompiler) rather than one standalone
 * program per tab.
 *
 * A counterbore needs no special case: it's ONE Circle toolpath carrying a shallow Bore for the recess, a
 * through Drill at the clearance diameter, and a Chamfer - all on the same centerline, in that order.
 *
 * Each toolpath and operation carries an Enabled flag (the tree's checkboxes) so a subset can be generated on
 * its own - re-running just the finishing passes without recutting the pocket.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNC.Core;

namespace CNC.Controls
{
    public partial class WorkOrderView : ConfigPanel<WorkOrder>, IGrblConfigTab, ICNCView
    {
        private GrblViewModel model = null;
        private string program = string.Empty;
        private WorkOrder workOrder = new WorkOrder();
        private WorkOrderToolpath selectedToolpath;
        private WorkOrderOperation selectedOp;
        // Guards the field->model write-back while the fields are being populated FROM the model on selection.
        private bool loadingFields = false;
        // Which toolpath's operations the user has manually collapsed - RebuildTree throws every TreeViewItem
        // away and remakes them on every edit, so without this a real collapse (as opposed to the old
        // permanently-forced IsExpanded="true") would spring back open the moment you changed anything.
        private readonly HashSet<WorkOrderToolpath> collapsedToolpaths = new HashSet<WorkOrderToolpath>();
        // Group headers collapse by NAME, not by object - a group has no object, and the name is what
        // survives a RebuildTree.
        private readonly HashSet<string> collapsedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The .workorder file this came from/was last saved to - null until one of those happens. Drives the
        // title bar (name, no path or extension) and Save's suggested filename.
        private string currentFilePath = null;
        // Set by New's name prompt, cleared by the first successful Save - lets the title bar and Save's
        // suggested filename reflect what the operator typed before there's an actual file on disk yet.
        private string pendingName = null;

        public WorkOrderView()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;

            foreach (var kind in WorkOrderRules.AllGeometries)
                cbxGeometry.Items.Add(new ComboBoxItem { Content = WorkOrderRules.GeometryLabel(kind), Tag = kind });
            foreach (var kind in WorkOrderRules.AllPatterns)
                cbxPattern.Items.Add(new ComboBoxItem { Content = WorkOrderRules.PatternLabel(kind), Tag = kind });
            foreach (var a in WorkOrderRules.AllAnchors)
                cbxAnchor.Items.Add(new ComboBoxItem { Content = WorkOrderRules.AnchorLabel(a), Tag = a });
            foreach (var m in WorkOrderRules.AllOffsetModes)
                cbxOffsetMode.Items.Add(new ComboBoxItem { Content = WorkOrderRules.OffsetModeLabel(m), Tag = m });

            // Same select-on-focus behavior every NumericField already has - txtName is a plain TextBox
            // (free-text, not numeric), so it doesn't get that for free.
            UIUtils.SelectAllOnFocus(txtName);

            // First entry is the built-in stroke font (= FontFamily "", the engraving mode); everything
            // after it is an installed family and means V-carve. One combo carries both the mode and the
            // font, because they are not independent choices - see WorkOrderToolpath.FontFamily.
            cbxFont.Items.Add(StrokeFontLabel);
            foreach (var family in TrueTypeOutlines.InstalledFamilies())
                cbxFont.Items.Add(family);
            cbxFont.SelectionChanged += (s, e) => { UpdateFontStyleEnabled(); CaptureFields(); };
            chkFontBold.Click += (s, e) => CaptureFields();
            chkFontItalic.Click += (s, e) => CaptureFields();

            // Typed text used to reach the model only when some OTHER field's change ran CaptureFields -
            // type into the box and Generate straight away, and the previous text was what got cut. Same
            // commit-on-change the name box has (CaptureFields itself no-ops while fields are loading).
            txtEngraveText.TextChanged += (s, e) => CaptureFields();

            // Order matches the enums - the selected index IS the value.
            foreach (var label in new[] { "Left", "Center", "Right" })
                cbxTextHAlign.Items.Add(label);
            foreach (var label in new[] { "Top", "Center", "Bottom" })
                cbxTextVAlign.Items.Add(label);
            cbxTextHAlign.SelectionChanged += (s, e) => CaptureFields();
            cbxTextVAlign.SelectionChanged += (s, e) => CaptureFields();

            // Same list, same source, as the Setup tab's own Material dropdown - there is one material
            // table (FeedsSpeedsAdvisor) and this is a second editor of the one shared value, not a copy.
            foreach (var material in CNC.Core.FeedsSpeedsAdvisor.MaterialRefs.Keys.OrderBy(m => m))
                cbxMaterial.Items.Add(material);

            foreach (var f in AllFields())
                System.ComponentModel.DependencyPropertyDescriptor
                    .FromProperty(NumericField.ValueProperty, typeof(NumericField))
                    .AddValueChanged(f, (s, e) => CaptureFields());

            canvasDiagram.MouseLeftButtonDown += (s, e) => { placing = true; PlaceFromMouse(e.GetPosition(canvasDiagram)); canvasDiagram.CaptureMouse(); };
            canvasDiagram.MouseMove += (s, e) => { if (placing) PlaceFromMouse(e.GetPosition(canvasDiagram)); };
            canvasDiagram.MouseLeftButtonUp += (s, e) => { placing = false; canvasDiagram.ReleaseMouseCapture(); };
        }

        private const string StrokeFontLabel = "(single stroke - engrave)";

        // Bold/italic only mean something for a real font family - the stroke font has one weight.
        private void UpdateFontStyleEnabled()
        {
            pnlFontStyleRow.IsEnabled = cbxFont.SelectedIndex > 0;
        }

        private NumericField[] AllFields()
        {
            // Every NumericField the editor owns MUST be here: this list is the only thing wiring
            // ValueChanged -> CaptureFields, so one left out is a field that silently does nothing.
            // fldSvgWidth was, and editing the artwork width changed neither the model nor the preview.
            return new[] { fldX, fldY, fldLength, fldAngle, fldDiameter, fldSize, fldWidth, fldDepthY, fldCapHeight, fldEngraveWidth,
                           fldCarveMaxDepth, fldSvgWidth,
                           fldColumns, fldColumnSpacing, fldRows, fldRowSpacing,
                           fldPatternCount, fldPatternRadius, fldPatternStartAngle, fldPatternArcSpan,
                           fldHoleDiameter, fldTotalDepth, fldDepthOfCut, fldPeckDepth, fldBoreStepDown, fldStepover,
                           fldNumTabs, fldTabWidth, fldTabHeight,
                           fldWallStockToLeave, fldFloorStockToLeave, fldChamferDepth, fldCountersinkDiameter };
        }

        private bool placing = false;

        // The transform of the drawing ON SCREEN - what a click on canvasDiagram is measured against.
        // Kept separate from drawTransform below because "Save Drawing" redraws everything into an
        // off-screen canvas at the PAPER's aspect ratio, and that must not move where a click lands.
        private OddJobsStockCanvas.Transform stockTransform;

        // Where the current DrawInto pass is drawing, and at what scale. Set for the duration of one
        // DrawInto call and read by the Add*/DrawEnvelope helpers, so the whole shape-drawing body is
        // written once and serves both the screen and the exported sheet - the drawing that gets taken
        // to the machine cannot describe a different arrangement from the one on screen.
        private Canvas drawTarget;
        private OddJobsStockCanvas.Transform drawTransform;

        // Click/drag on the stock drawing to place the selected toolpath's geometry - works whether the
        // toolpath itself or one of its operations is selected, since the geometry belongs to the toolpath.
        private void PlaceFromMouse(Point p)
        {
            if (selectedToolpath == null)
                return;

            var work = OddJobsStockCanvas.ToWork(stockTransform, p);
            // Clamped against the stock this work order is drawn on, not Setup's - the drag has to land
            // inside the rectangle the operator can actually see.
            var clamped = OddJobsStockCanvas.ClampToKeepOut(work.X, work.Y, workOrder.StockWidth, workOrder.StockDepth);
            selectedToolpath.X = Math.Round(clamped.X, 1);
            selectedToolpath.Y = Math.Round(clamped.Y, 1);
            if (selectedOp == null)
            {
                loadingFields = true;
                fldX.Value = selectedToolpath.X;
                fldY.Value = selectedToolpath.Y;
                loadingFields = false;
            }
            OnWorkOrderChanged();
        }

        #region Composition (add / remove / reorder)

        // Reached by clicking the tree's own "<Add Toolpath>" placeholder row: pick the geometry first, since
        // that's what decides which operations are even possible on it. Mirrors OpenOperationPicker below.
        private void OpenAddToolpathPicker(UIElement anchor)
        {
            // Deferred for the same reason as OpenOperationPicker - see its comment.
            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var kind in WorkOrderRules.AllGeometries)
                {
                    var item = new MenuItem { Header = WorkOrderRules.GeometryLabel(kind), Tag = kind };
                    item.Click += (s, ev) => AddToolpath((WorkOrderGeometryKind)((MenuItem)s).Tag);
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void AddToolpath(WorkOrderGeometryKind kind)
        {
            var s = StartJobConfig.Section;
            var tp = new WorkOrderToolpath
            {
                Geometry = kind,
                Name = NextToolpathName(kind),
                // Centered on the stock - roughly where most jobs want it, then click/drag or type to adjust.
                X = s != null ? Math.Round(s.Width / 2d, 1) : 0d,
                Y = s != null ? Math.Round(s.Height / 2d, 1) : 0d
            };
            if (kind == WorkOrderGeometryKind.Indirect)
            {
                tp.IndirectSource = workOrder.Toolpaths.FirstOrDefault(t => !t.IsIndirect)?.Name;
                UpdateIndirectName(tp);
            }
            EnsureEngraveOperation(tp);
            workOrder.Toolpaths.Add(tp);
            RebuildTree(tp);
            OnWorkOrderChanged();
        }

        // A Text toolpath supports exactly one operation - Engrave - and does nothing at all without it
        // (WorkOrderRules.AvailableOperations returns Engrave and yields nothing else for this geometry).
        // Making the operator open the picker to choose the only thing it can offer is pure ceremony, so
        // create it up front. NewOperation is what picks the V-bit and its feeds, so this stays one
        // definition of what an Engrave starts life as.
        private static void EnsureEngraveOperation(WorkOrderToolpath tp)
        {
            // Text and Svg both land on a geometry whose ONLY available operation is Engrave
            // (WorkOrderRules.AvailableOperations), so making the operator open a picker to choose the
            // one thing it can offer is ceremony either way.
            if (tp == null || (tp.Geometry != WorkOrderGeometryKind.Text && tp.Geometry != WorkOrderGeometryKind.Svg))
                return;
            if (tp.Operations.Any(o => o.Kind == WorkOrderOpKind.Engrave))
                return;     // switched away and back, or loaded from a saved work order

            tp.Operations.Add(NewOperation(WorkOrderOpKind.Engrave, tp));
        }

        // Default name is the geometry plus a running count, e.g. "Circle 1" - editable in the Name field.
        private string NextToolpathName(WorkOrderGeometryKind kind)
        {
            string prefix = kind.ToString();
            int n = 1;
            while (workOrder.Toolpaths.Any(t => string.Equals(t.Name, prefix + " " + n, StringComparison.OrdinalIgnoreCase)))
                n++;
            return prefix + " " + n;
        }

        // Reached by clicking a toolpath's "<add operation>" placeholder row. Opening is deferred to Background
        // priority so the click that triggered it is fully done first - a menu opened synchronously inside the
        // click gets closed again by that same click's remaining input events.
        private void OpenOperationPicker(WorkOrderToolpath tp, UIElement anchor)
        {
            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                var kinds = WorkOrderRules.OfferableOperations(tp).ToArray();
                if (kinds.Length == 0)
                {
                    AppDialogs.Show("This toolpath already has every operation its geometry supports.", "Work Order", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var kind in kinds)
                {
                    var item = new MenuItem { Header = WorkOrderRules.OpLabel(kind), Tag = kind };
                    item.Click += (s, ev) =>
                    {
                        var op = NewOperation((WorkOrderOpKind)((MenuItem)s).Tag, tp);
                        tp.Operations.Add(op);
                        RebuildTree(op);
                        OnWorkOrderChanged();
                    };
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Sensible starting values per operation kind, including which tool the Feeds and Speeds dialog should
        // open on - a finishing pass wants a ball end, a chamfer a V-bit.
        private static WorkOrderOperation NewOperation(WorkOrderOpKind kind, WorkOrderToolpath tp)
        {
            string material = StartJobConfig.Section?.Material ?? string.Empty;
            var op = new WorkOrderOperation { Kind = kind };

            // A hole starts out matching the geometry it's centered on - the common case is one hole at the
            // circle's own size, and the counterbore case just means editing it afterward.
            if (tp != null && tp.Geometry == WorkOrderGeometryKind.Circle)
                op.HoleDiameter = tp.Diameter;

            switch (kind)
            {
                case WorkOrderOpKind.Pocket:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("roughing", material);
                    break;
                case WorkOrderOpKind.Contour:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("roughing", material);
                    break;
                case WorkOrderOpKind.Drill:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("drilling", material);
                    break;
                case WorkOrderOpKind.Bore:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("roughing", material);
                    break;
                case WorkOrderOpKind.SideFinish:
                case WorkOrderOpKind.BottomFinish:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("finishing", material);
                    break;
                case WorkOrderOpKind.Chamfer:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("chamfer", material);
                    op.Feed = 500d;
                    break;
                case WorkOrderOpKind.Engrave:
                    // Same tool class as a chamfer - both cut with the point of a V-bit - so it reuses that
                    // suggestion rather than inventing a second lookup that would drift from it. Without
                    // this the operation kept the generic 2-flute end mill, and then the depth readout was
                    // computed against a tool that has no included angle at all.
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("chamfer", material);
                    op.Feed = 500d;
                    break;
                case WorkOrderOpKind.Countersink:
                    // op.CountersinkDiameter already holds its own default (12.5mm - see WorkOrderModel) at
                    // this point, so the tool is picked from that instead of a generic "smallest" guess.
                    op.Tool = OddJobsFeedsSpeedsDialog.SmallestCountersinkBitFor(op.CountersinkDiameter);
                    break;
                case WorkOrderOpKind.Surface:
                    op.Tool = OddJobsFeedsSpeedsDialog.SuggestTool("facing", material);
                    break;
            }

            // The operation's diameter follows whatever tool was just chosen - the definition is the
            // source of truth. This used to be special-cased for Surface only (its 25mm bit was the one
            // whose mismatch against the generic 6.35 default was glaring), leaving every other kind with
            // the stale default until Feeds and Speeds was confirmed once - and a drill is exempt as ever,
            // its diameter IS the hole.
            var chosen = CustomTools.Find(op.Tool);
            if (op.Kind != WorkOrderOpKind.Drill && chosen != null && chosen.DiameterMm > 0d)
                op.BitDiameter = chosen.DiameterMm;

            // Recall whatever this tool/material was last dialed in to, so a new operation starts from the
            // operator's own proven numbers rather than the chart default (see OddJobsToolMemory).
            var remembered = OddJobsToolMemory.Find(op.Tool, op.BitDiameter, material);
            if (remembered != null)
            {
                if (remembered.Rpm > 0d) op.SpindleRPM = remembered.Rpm;
                if (remembered.Feed > 0d) op.Feed = remembered.Feed;
                if (remembered.PlungeFeed > 0d) op.PlungeFeed = remembered.PlungeFeed;
                if (remembered.DepthOfCut > 0d) op.DepthOfCut = remembered.DepthOfCut;
            }
            return op;
        }

        // Reached from a tree row's own context menu (Remove) and the Delete key - see AttachRowContextMenu
        // and TreeToolpaths_PreviewKeyDown. No longer a standing button: the old Up/Down/Remove row sat below
        // the whole tree, disconnected from the row it acted on - a right-click context menu on the row itself
        // is the standard place for per-item actions like this.
        private void RemoveSelected()
        {
            if (selectedOp != null && selectedToolpath != null)
                selectedToolpath.Operations.Remove(selectedOp);
            else if (selectedToolpath != null)
                workOrder.Toolpaths.Remove(selectedToolpath);
            else
                return;

            RebuildTree(null);
            OnWorkOrderChanged();
        }

        // Independent copy of a toolpath and every operation on it, appending "(n)" to the name for
        // uniqueness. Unlike an Indirect toolpath (which stays a live reference to its source), this diverges
        // immediately - editing either copy afterward doesn't touch the other.
        private void DuplicateToolpath(WorkOrderToolpath tp)
        {
            var copy = WorkOrderRules.CopyFields(tp, new WorkOrderToolpath());

            // Name and Operations are the only two fields marked [NoClone] (see WorkOrderModel, where each
            // carries its own reason) - CopyFields skipped them, so supply them here: a unique name, and a
            // list of operations of its own. copy.Operations is already an empty list from its initialiser.
            copy.Name = NextDuplicateName(tp.Name);
            foreach (var op in tp.Operations)
                copy.Operations.Add(CloneOperation(op));
            // Same contents, its own list - a duplicate of an Indirect toolpath holds back what the original
            // held back, and changing either afterwards leaves the other alone.
            copy.HeldBack = new List<string>(tp.HeldBack);

            workOrder.Toolpaths.Insert(workOrder.Toolpaths.IndexOf(tp) + 1, copy);
            RebuildTree(copy);
            OnWorkOrderChanged();
        }

        // Every field on WorkOrderOperation is a value type or a string, so a flat field copy IS a deep copy -
        // there is no reference here for the two copies to share.
        private static WorkOrderOperation CloneOperation(WorkOrderOperation op)
        {
            return WorkOrderRules.CopyFields(op, new WorkOrderOperation());
        }

        // Strips a trailing " (n)" before re-deriving the next free number, so duplicating a duplicate lands
        // on "Foo (3)" instead of chaining into "Foo (2) (2)".
        private string NextDuplicateName(string baseName)
        {
            string root = System.Text.RegularExpressions.Regex.Replace(baseName, @"\s\(\d+\)$", string.Empty);
            int n = 2;
            while (workOrder.Toolpaths.Any(t => string.Equals(t.Name, root + " (" + n + ")", StringComparison.OrdinalIgnoreCase)))
                n++;
            return root + " (" + n + ")";
        }

        // Reorders within the selection's own level: an operation moves inside its toolpath, a toolpath moves
        // among the other toolpaths.
        private void Move(int delta)
        {
            if (selectedOp != null && selectedToolpath != null)
            {
                var ops = selectedToolpath.Operations;
                int i = ops.IndexOf(selectedOp), j = i + delta;
                if (i < 0 || j < 0 || j >= ops.Count)
                    return;
                ops.RemoveAt(i);
                ops.Insert(j, selectedOp);
                RebuildTree(selectedOp);
            }
            else if (selectedToolpath != null)
            {
                int i = workOrder.Toolpaths.IndexOf(selectedToolpath), j = i + delta;
                if (i < 0 || j < 0 || j >= workOrder.Toolpaths.Count)
                    return;
                workOrder.Toolpaths.RemoveAt(i);
                workOrder.Toolpaths.Insert(j, selectedToolpath);
                RebuildTree(selectedToolpath);
            }
            else
                return;

            OnWorkOrderChanged();
        }

        #endregion

        #region Tree

        // Marks the "<add operation>" placeholder row - a Tag type that's neither a toolpath nor an operation,
        // so selection handling can tell it apart and act on a click instead of showing a parameter panel.
        private class AddOperationPlaceholder
        {
            public WorkOrderToolpath Toolpath;
        }

        // Marks a group header row. Also neither a toolpath nor an operation, so selecting one shows no
        // parameter panel at all (LoadFields hides both) - a group has nothing of its own to edit. It is a
        // label over a run of toolpaths, and its checkbox is the only thing it does.
        private class GroupRow
        {
            public string Name;
        }

        // Marks one borrowed-toolpath row under an Indirect toolpath. Nothing here is editable either - the
        // parameters belong to the SOURCE and are edited there - so its checkbox is likewise the only thing
        // it does, holding that member back in THIS copy only.
        private class BorrowedRow
        {
            public WorkOrderToolpath Indirect;
            public WorkOrderToolpath Member;
        }

        // Header for a toolpath/operation row: an enable checkbox plus the summary text. The text is a separate
        // TextBlock rather than the CheckBox's own Content on purpose - as Content, clicking the row's label to
        // SELECT it would toggle the checkbox as a side effect.
        // invalidTool: red text (same IndianRed as the indirect-toolpath "(source not found)" placeholder row)
        // when this operation's Tool doesn't resolve to a real tool (see ParameterWarnings) - lets the operator
        // spot which row a "pick a different tool" warning refers to at a glance instead of cross-referencing
        // the text list. Always false for a toolpath row (toolpaths don't carry a tool of their own).
        private FrameworkElement MakeCheckHeader(string text, bool enabled, Action<bool> onToggle, bool invalidTool = false)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var check = new CheckBox
            {
                IsChecked = enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = "Include this in Generate. Unchecked rows are held back."
            };
            // Handled so the toggle doesn't also bubble up as a row selection change.
            check.Click += (s, ev) => { ev.Handled = true; onToggle(((CheckBox)s).IsChecked == true); };
            panel.Children.Add(check);
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            if (invalidTool)
                label.Foreground = Brushes.IndianRed;
            panel.Children.Add(label);
            return panel;
        }

        // Updates an existing check-header in place: text, checked state, the dimming that shows a row is
        // held back, and the invalid-tool highlight (see MakeCheckHeader's own comment). Cheaper than
        // rebuilding the tree, and it keeps selection and expansion state.
        private static void SetCheckHeader(TreeViewItem item, string text, bool enabled, bool effective, bool invalidTool = false)
        {
            var panel = item.Header as StackPanel;
            if (panel == null || panel.Children.Count < 2)
                return;
            ((CheckBox)panel.Children[0]).IsChecked = enabled;
            var label = (TextBlock)panel.Children[1];
            label.Text = text;
            // Dimmed when this row won't run - either unchecked itself, or under an unchecked toolpath.
            label.Opacity = effective ? 1.0 : 0.45;
            // ClearValue (not a hardcoded "back to black") so a row that's since been fixed reverts to
            // whatever the tree's normal inherited/themed text color actually is.
            if (invalidTool)
                label.Foreground = Brushes.IndianRed;
            else
                label.ClearValue(TextBlock.ForegroundProperty);
        }

        // A group's header row. Its checkbox reads ON only when every member is on, and toggling it drives
        // all of them - which is the entire reason the row exists. Indirect members are included here (they
        // are toolpaths in the group like any other); only EXPANSION skips them, see WorkOrderRules.Expand.
        private TreeViewItem MakeGroupItem(string name)
        {
            var members = WorkOrderRules.GroupMembers(workOrder, name).ToList();
            // "Is anything under me still on?", exactly as a toolpath's own tick reads it (ToggleEnabled).
            // Not "is everything on" - unticking one member of four would otherwise clear this header while
            // the other three still ran, which is the same misreading the cascade exists to prevent.
            bool anyOn = members.Any(m => m.Enabled);
            string captured = name;

            var item = new TreeViewItem
            {
                Header = MakeCheckHeader(GroupHeaderText(name, members.Count), anyOn, on => ToggleGroup(captured, on)),
                Tag = new GroupRow { Name = name },
                IsExpanded = !collapsedGroups.Contains(name)
            };
            item.Expanded += (s, ev) => collapsedGroups.Remove(captured);
            item.Collapsed += (s, ev) => collapsedGroups.Add(captured);
            return item;
        }

        // One borrowed row's text: which toolpath it is, and what that toolpath will cut here. Always named,
        // even when only one is borrowed - the row is about a MEMBER, and a conditional prefix is one more
        // thing for the build path and the refresh path to disagree about.
        private static string BorrowedRowText(WorkOrderToolpath member)
        {
            return member.Name + " - " + string.Join(", ", member.Operations.Select(o => WorkOrderRules.Summarize(o)));
        }

        // Holds one borrowed toolpath back in THIS copy, or lets it run again. Writes only to the Indirect
        // toolpath's own HeldBack - the source keeps its own ticks, which is the entire point of the copy
        // having switches of its own.
        private void ToggleBorrowed(WorkOrderToolpath indirect, WorkOrderToolpath member, bool on)
        {
            WorkOrderRules.SetHeldBack(indirect, member, !on);
            OnWorkOrderChanged();
        }

        // One definition, because the header is written in two places - built by MakeGroupItem and rewritten
        // in place by RefreshTreeHeaders - and two copies of a format string is how the same row starts
        // reading differently depending on which path last touched it.
        private static string GroupHeaderText(string name, int members)
        {
            return string.Format("{0}  ({1} toolpath{2})", name, members, members == 1 ? string.Empty : "s");
        }

        // Enables or disables every toolpath in a group at once, cascading to their operations - the same
        // rule a toolpath applies to its own operations, one level up. See ToggleEnabled's comment: a tick
        // and everything under it move together, so there is never an unticked row hiding ticked ones.
        //
        // This originally set only member.Enabled and left the operations alone, reasoning that a held-back
        // operation shouldn't be re-enabled by switching a group back on. That produced exactly the state
        // the rule exists to forbid - four unticked toolpaths with every operation under them still ticked -
        // and it was reported as looking wrong the first time anyone tried it. The concern was real but it
        // is already the accepted trade-off for a toolpath's own tick; making the group behave differently
        // bought nothing and cost the invariant.
        private void ToggleGroup(string name, bool on)
        {
            foreach (var member in WorkOrderRules.GroupMembers(workOrder, name))
                SetToolpathEnabled(member, on);

            // No RebuildTree: OnWorkOrderChanged refreshes every row's header in place - group headers
            // included, now - which repaints the cascade without disturbing the selection.
            OnWorkOrderChanged();
        }

        private void RebuildTree(object toSelect)
        {
            treeToolpaths.Items.Clear();

            // A contiguous run of toolpaths sharing a group nests under one header. Membership is KEPT
            // contiguous when a group is assigned (see cbxGroup_Changed), so a run is the whole group.
            // A hand-edited file could still interleave them, and that shows up honestly as a second header
            // of the same name rather than being silently re-sorted behind your back - re-sorting would
            // change the program order, since Schedule's default IS this order.
            TreeViewItem groupItem = null;
            string groupName = null;

            foreach (var tp in workOrder.Toolpaths)
            {
                if (string.IsNullOrEmpty(tp.Group))
                {
                    groupItem = null;
                    groupName = null;
                }
                else if (!string.Equals(tp.Group, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    groupName = tp.Group;
                    groupItem = MakeGroupItem(groupName);
                    treeToolpaths.Items.Add(groupItem);
                }

                var owner = tp;
                var tpItem = new TreeViewItem
                {
                    Header = MakeCheckHeader(WorkOrderRules.Summarize(workOrder, tp), tp.Enabled, on => ToggleEnabled(owner, null, on)),
                    Tag = tp,
                    // Real collapse now (see collapsedToolpaths' own comment) - not a hardcoded "true" that
                    // made this a flat checklist wearing tree chrome rather than an actual disclosure tree.
                    IsExpanded = !collapsedToolpaths.Contains(tp)
                };
                tpItem.Expanded += (s, ev) => collapsedToolpaths.Remove(owner);
                tpItem.Collapsed += (s, ev) => collapsedToolpaths.Add(owner);
                AttachRowContextMenu(tpItem, isToolpath: true);

                if (tp.IsIndirect)
                {
                    // No operations of its own to list, and none can be added here - see WorkOrderRules
                    // .AvailableOperations. Read-only rows instead, listing what the source actually
                    // contributes, so the tree still shows what this toolpath will cut without implying it
                    // can be edited from here.
                    //
                    // BorrowedBy, not Expand: Expand is the filtered view of what actually cuts, and a
                    // held-back member still needs a row here to tick back on. Both resolve a source the
                    // same way, so a group reference lists its members rather than reading as missing.
                    var borrowed = WorkOrderRules.BorrowedBy(workOrder, tp);
                    if (borrowed.Count == 0)
                    {
                        tpItem.Items.Add(new TreeViewItem { Header = "(source not found)", Foreground = Brushes.IndianRed, FontStyle = FontStyles.Italic });
                    }
                    else if (borrowed.Sum(b => b.Operations.Count) == 0)
                    {
                        tpItem.Items.Add(new TreeViewItem { Header = "(source has no operations yet)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
                    }
                    else
                    {
                        // One TICKABLE row per borrowed toolpath. The copy has a fate of its own - it doesn't
                        // inherit the source's switches - so it needs switches of its own, and this is where
                        // they live. Ticking here writes to this Indirect toolpath's HeldBack and touches
                        // nothing on the source.
                        //
                        // A row per borrowed TOOLPATH rather than per operation: held-back is recorded by
                        // member name, and an operation has no name to record. The operations it contributes
                        // are named in the row's text so the row still says what it will cut.
                        foreach (var b in borrowed)
                        {
                            var member = b;
                            var indirect = tp;
                            tpItem.Items.Add(new TreeViewItem
                            {
                                Header = MakeCheckHeader(BorrowedRowText(b), !WorkOrderRules.IsHeldBack(tp, b),
                                                         on => ToggleBorrowed(indirect, member, on)),
                                Tag = new BorrowedRow { Indirect = tp, Member = b }
                            });
                        }
                    }
                }
                else
                {
                    foreach (var op in tp.Operations)
                    {
                        var ownerOp = op;
                        var opItem = new TreeViewItem
                        {
                            Header = MakeCheckHeader(WorkOrderRules.Summarize(op), op.Enabled, on => ToggleEnabled(owner, ownerOp, on),
                                invalidTool: CustomTools.Find(op.Tool) == null),
                            Tag = op,
                            ToolTip = FeedsSummaryText(owner, op)
                        };
                        AttachRowContextMenu(opItem, isToolpath: false);
                        tpItem.Items.Add(opItem);
                    }

                    // Always last: the placeholder that adds the next operation. A toolpath with no operations
                    // yet shows nothing but this, which is the prompt to add one.
                    var addItem = new TreeViewItem
                    {
                        Header = "<add operation>",
                        Tag = new AddOperationPlaceholder { Toolpath = tp },
                        Foreground = Brushes.SteelBlue,
                        FontStyle = FontStyles.Italic
                    };
                    // Driven by the click itself rather than by selection: opening the picker from
                    // SelectedItemChanged meant the click's own mouse-up landed after the menu appeared and
                    // dismissed it instantly, and re-clicking an already-selected placeholder raised no
                    // selection change at all, so nothing happened.
                    var owningToolpath = tp;
                    addItem.PreviewMouseLeftButtonUp += (s, ev) =>
                    {
                        ev.Handled = true;
                        OpenOperationPicker(owningToolpath, (UIElement)s);
                    };
                    tpItem.Items.Add(addItem);
                }
                ((ItemsControl)groupItem ?? treeToolpaths).Items.Add(tpItem);
            }

            // Root-level placeholder that adds a whole new toolpath - replaces the old "+" button in the
            // title bar, so the tree itself is the one place composition happens, same idiom as
            // "<add operation>" above.
            var addToolpathItem = new TreeViewItem
            {
                Header = "<Add Toolpath>",
                Foreground = Brushes.SteelBlue,
                FontStyle = FontStyles.Italic
            };
            addToolpathItem.PreviewMouseLeftButtonUp += (s, ev) =>
            {
                ev.Handled = true;
                OpenAddToolpathPicker((UIElement)s);
            };
            treeToolpaths.Items.Add(addToolpathItem);

            if (toSelect != null)
                SelectInTree(toSelect);
        }

        // Right-click actions for a toolpath or operation row - Move Up/Down/Remove - replacing the old
        // standing Up/Down/Remove buttons below the whole tree. Standard placement for per-item list actions
        // is a context menu on the item itself, not buttons disconnected from the row they act on; Delete and
        // Ctrl+Up/Ctrl+Down (TreeToolpaths_PreviewKeyDown) cover the keyboard-only case.
        private void AttachRowContextMenu(TreeViewItem item, bool isToolpath)
        {
            var up = new MenuItem { Header = "Move Up" };
            var down = new MenuItem { Header = "Move Down" };
            MenuItem duplicate = null;
            if (isToolpath)
            {
                duplicate = new MenuItem { Header = "Duplicate" };
                duplicate.Click += (s, ev) => DuplicateToolpath((WorkOrderToolpath)item.Tag);
            }
            var remove = new MenuItem { Header = isToolpath ? "Remove Toolpath" : "Remove Operation" };
            up.Click += (s, ev) => { item.IsSelected = true; Move(-1); };
            down.Click += (s, ev) => { item.IsSelected = true; Move(1); };
            remove.Click += (s, ev) => { item.IsSelected = true; RemoveSelected(); };

            var menu = new ContextMenu();
            menu.Items.Add(up);
            menu.Items.Add(down);
            if (duplicate != null)
                menu.Items.Add(duplicate);
            menu.Items.Add(remove);
            // Recomputed each time it opens rather than once at build time - RebuildTree runs often enough
            // (every edit) that a stale enabled/disabled state would rarely be visibly wrong, but there's no
            // reason to risk it when the index is cheap to look up again.
            menu.Opened += (s, ev) =>
            {
                int i, count;
                if (isToolpath)
                {
                    var tp = (WorkOrderToolpath)item.Tag;
                    i = workOrder.Toolpaths.IndexOf(tp);
                    count = workOrder.Toolpaths.Count;
                }
                else
                {
                    var op = (WorkOrderOperation)item.Tag;
                    var owner = workOrder.Toolpaths.FirstOrDefault(t => t.Operations.Contains(op));
                    i = owner?.Operations.IndexOf(op) ?? -1;
                    count = owner?.Operations.Count ?? 0;
                }
                up.IsEnabled = i > 0;
                down.IsEnabled = i >= 0 && i < count - 1;
            };
            item.ContextMenu = menu;

            // WPF's own ContextMenuService opens on right-button UP by default, which something else in this
            // row (or the TreeViewItem's own input handling) swallows before it gets there - matches the same
            // class of bug TabKeyBinder.AttachBindMenu already works around, same fix: open it ourselves on
            // right-button DOWN and swallow both the down and the following up so the framework's own
            // up-triggered open doesn't also fire and immediately re-toggle it.
            item.PreviewMouseRightButtonDown += (s, ev) =>
            {
                // PreviewMouseRightButtonDown TUNNELS (root -> leaf), and an operation row's TreeViewItem
                // sits INSIDE its parent toolpath row's TreeViewItem visually - so without this check, the
                // toolpath's own handler (registered the same way) fires FIRST, on every right-click anywhere
                // in its subtree, sets Handled=true, and the operation row's own handler below it in the
                // tree never runs at all - right-clicking an operation always opened the toolpath's menu
                // instead. Only actually handle it here if THIS item is the innermost TreeViewItem under the
                // click (i.e. the click's real target), not an ancestor of it.
                if (UIUtils.TryFindParent<TreeViewItem>(ev.OriginalSource as DependencyObject) != item)
                    return;

                ev.Handled = true;
                item.IsSelected = true;
                item.Focus();
                // Deferred, same reason as OpenOperationPicker/OpenAddToolpathPicker: selecting a DIFFERENT
                // row a click above just triggered a synchronous LoadFields()/DrawDiagram() (new parameter
                // panel, redrawn diagram) - opening the menu against item's bounds in that same tick placement-
                // computes off whatever hadn't finished re-laying-out yet, which is what put it at the screen's
                // top-left corner on anything but the row that was already selected. MousePoint placement (not
                // the default Bottom-of-item) also means it opens under the cursor even once the layout settles.
                Dispatcher.BeginInvoke((System.Action)(() =>
                {
                    menu.PlacementTarget = item;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    menu.IsOpen = true;
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            item.PreviewMouseRightButtonUp += (s, ev) => ev.Handled = true;
        }

        // Delete removes the selected row; Ctrl+Up/Ctrl+Down reorders it - keyboard equivalents of the
        // context menu above, so composing a work order doesn't require the mouse.
        private void TreeToolpaths_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
            else if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
                     (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down))
            {
                Move(e.Key == System.Windows.Input.Key.Up ? -1 : 1);
                e.Handled = true;
            }
        }

        // Re-selects a toolpath/operation after RebuildTree threw every TreeViewItem away and remade them -
        // e.g. right after Move() reorders one via Ctrl+Up/Down. IsSelected alone leaves keyboard FOCUS on
        // the old (now-destroyed) item, which WPF's default TreeViewItem style reads as "selected but not the
        // active selection" and paints with the dimmed/inactive highlight instead of the normal one - looks
        // like the selection went stale even though the data model already points at the right row. Focus()
        // fixes the highlight and means Ctrl+Up immediately after Ctrl+Down keeps working on the same item.
        // Deferred to DispatcherPriority.Loaded - the item was just added this call and has no layout yet, so
        // an immediate Focus() would silently no-op.
        // Searches at any depth (see AllRows). The two-level walk this used to do stopped finding an
        // OPERATION once its toolpath sat under a group header, because that put it three levels down -
        // the selection silently didn't move rather than failing visibly.
        private void SelectInTree(object tag)
        {
            foreach (var item in AllRows(treeToolpaths))
                if (ReferenceEquals(item.Tag, tag))
                {
                    item.IsSelected = true;
                    item.Dispatcher.BeginInvoke((System.Action)(() => item.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }
        }

        private void treeToolpaths_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var item = e.NewValue as TreeViewItem;

            // The placeholder isn't a thing to inspect - the picker is opened by its own click handler (see
            // RebuildTree); landing here just shows the toolpath it belongs to.
            if (item?.Tag is AddOperationPlaceholder placeholder)
            {
                selectedToolpath = placeholder.Toolpath;
                selectedOp = null;
                LoadFields();
                DrawDiagram();
                return;
            }

            // A borrowed row owns nothing editable - its parameters live on the SOURCE toolpath and are
            // edited there. Selecting one shows the Indirect toolpath it belongs to, so clicking a row to
            // tick it doesn't blank the panel out from under you.
            if (item?.Tag is BorrowedRow borrowed)
            {
                selectedToolpath = borrowed.Indirect;
                selectedOp = null;
                LoadFields();
                DrawDiagram();
                return;
            }

            selectedOp = item?.Tag as WorkOrderOperation;
            selectedToolpath = item?.Tag as WorkOrderToolpath
                ?? (selectedOp != null ? workOrder.Toolpaths.FirstOrDefault(t => t.Operations.Contains(selectedOp)) : null);

            LoadFields();
            DrawDiagram();
        }

        #endregion

        #region Parameter fields

        private static void Show(UIElement el, bool visible) { el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

        private void LoadFields()
        {
            loadingFields = true;

            Show(pnlToolpath, selectedToolpath != null && selectedOp == null);
            Show(pnlOperation, selectedOp != null);

            if (selectedOp != null)
                LoadOperationFields();
            else if (selectedToolpath != null)
                LoadToolpathFields();
            else
                txtPanelHeader.Text = "Add a toolpath to get started";

            LoadMaterial();

            loadingFields = false;
            UpdateFeedsSummary();
        }

        // The shared Setup material, shown on the toolpath panel. Re-read rather than remembered: Setup can
        // change it while this view is alive, and the value belongs to the job setup, not to any toolpath.
        private void LoadMaterial()
        {
            string material = StartJobConfig.Section?.Material ?? string.Empty;
            cbxMaterial.SelectedItem = cbxMaterial.Items.Cast<string>().FirstOrDefault(m => m == material);
        }

        private void cbxMaterial_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields)
                return;

            var s = StartJobConfig.Section;
            if (s == null)
                return;

            string material = cbxMaterial.SelectedItem as string ?? string.Empty;
            if (s.Material == material)
                return;

            s.Material = material;
            AppConfig.Settings.Save();

            // The material feeds every operation's suggested speeds, so the summary under the panel is
            // stale the moment it changes.
            UpdateFeedsSummary();
        }

        private void LoadToolpathFields()
        {
            var tp = selectedToolpath;
            txtPanelHeader.Text = "Toolpath geometry";

            // Indirect's name is generated, not typed - see UpdateIndirectName - so the box is shown but
            // disabled, same idiom as a drill's hole diameter field being driven by the geometry instead of
            // editable (LoadOperationFields).
            // Only LEAF toolpaths can join a group. An Indirect one is a reference, and letting a reference
            // into a group is what would make a group able to contain itself - see WorkOrderRules
            // .GroupMembers. Refusing it here, at the one place membership is set, is why nothing downstream
            // needs cycle detection.
            Show(pnlGroupRow, !tp.IsIndirect);

            // Rebuilt every load so a group created on another toolpath a moment ago is offerable here. The
            // box is editable, so a name that isn't in the list yet is how a NEW group gets made.
            cbxGroup.Items.Clear();
            foreach (var g in WorkOrderRules.GroupNames(workOrder))
                cbxGroup.Items.Add(g);
            cbxGroup.Text = tp.Group ?? string.Empty;

            txtName.Text = tp.Name;
            txtName.IsEnabled = !tp.IsIndirect;
            txtName.ToolTip = tp.IsIndirect ? "Generated from the source toolpath and X/Y - change those instead." : null;

            // Once a toolpath IS Indirect, there's nothing left to decide here - the combo still being
            // interactive (rather than genuinely greyed out) while everything around it disappears read as
            // broken, not disabled. Switching INTO Indirect is still done through this same combo on a normal
            // toolpath (see cbxGeometry_SelectionChanged) - it only disappears once you're already there.
            // Getting back out is Remove + Add a new toolpath of the geometry you actually want.
            Show(pnlGeometryRow, !tp.IsIndirect);
            cbxGeometry.SelectedIndex = Array.IndexOf(WorkOrderRules.AllGeometries, tp.Geometry);

            // Exactly one of these two rows is ever up. An Indirect toolpath has no shape of its own for an
            // anchor to name a corner of - the row was inert for it, and worse than inert, since a live
            // dropdown that changes nothing reads as a setting that didn't take. What it wants to say instead
            // is what X/Y are measured FROM, so that takes the same slot.
            Show(pnlAnchorRow, !tp.IsIndirect);
            Show(pnlOffsetModeRow, tp.IsIndirect);

            cbxAnchor.SelectedIndex = Array.IndexOf(WorkOrderRules.AllAnchors, tp.Anchor);
            cbxOffsetMode.SelectedIndex = Array.IndexOf(WorkOrderRules.AllOffsetModes, tp.OffsetMode);
            fldX.Value = tp.X; fldY.Value = tp.Y;
            fldLength.Value = tp.Length; fldAngle.Value = tp.Angle;
            fldDiameter.Value = tp.Diameter; fldSize.Value = tp.Size;
            fldWidth.Value = tp.Width; fldDepthY.Value = tp.Depth;
            txtEngraveText.Text = tp.Text ?? string.Empty; fldCapHeight.Value = tp.CapHeight;
            txtSvgFile.Text = tp.SvgFile ?? string.Empty; fldSvgWidth.Value = tp.SvgWidth;
            // A family saved on another machine may not be installed here. Adding it to the list rather
            // than falling back to index 0 keeps the choice intact - silently reverting to the stroke font
            // would change the MODE of the cut just by opening the file. (WPF itself falls back to Arial
            // for rendering an unknown family, which is visible and recoverable; a lost field is neither.)
            if (tp.IsCarved && !cbxFont.Items.Contains(tp.FontFamily))
                cbxFont.Items.Add(tp.FontFamily);
            cbxFont.SelectedIndex = tp.IsCarved ? cbxFont.Items.IndexOf(tp.FontFamily) : 0;
            chkFontBold.IsChecked = tp.FontBold; chkFontItalic.IsChecked = tp.FontItalic;
            UpdateFontStyleEnabled();
            chkEntireSpoilboard.IsChecked = tp.EntireSpoilboard;
            chkSpoilExistingOrigin.IsChecked = tp.UseExistingOrigin;

            // Only the dimensions this geometry actually has. Indirect has none of its own - it borrows
            // whatever the source toolpath has.
            bool isLine = tp.Geometry == WorkOrderGeometryKind.Line;
            bool isCircle = tp.Geometry == WorkOrderGeometryKind.Circle;
            bool isSurface = tp.Geometry == WorkOrderGeometryKind.Surface;
            bool isWD = tp.Geometry == WorkOrderGeometryKind.Oval || tp.Geometry == WorkOrderGeometryKind.Rect || isSurface;
            // Entire spoilboard covers the whole machine travel, so X/Y/Width/Depth have nothing left to say.
            bool entireSpoilboard = isSurface && tp.EntireSpoilboard;
            Show(fldX, !entireSpoilboard);
            Show(fldY, !entireSpoilboard);
            bool isText = tp.Geometry == WorkOrderGeometryKind.Text;
            // Shape text: the same text fields serve the Text kind and a shape with its Text box ticked
            // (see the XAML comment). Alignment is shape-text-only - the Text kind places by anchor.
            bool canShapeText = WorkOrderRules.SupportsShapeText(tp.Geometry);
            bool showText = isText || (canShapeText && tp.HasText);
            Show(fldLength, isLine);
            // The baseline angle is the same field a Line uses - degrees from +X - so Text just shows it too.
            // Angle rotates artwork about its anchor exactly as it rotates a text baseline - BuildVCarve
            // already applies it (BuildEngrave passes tp.Angle straight through), so this is the whole
            // of "rotate the logo 90 degrees".
            Show(fldAngle, isLine || isText || tp.Geometry == WorkOrderGeometryKind.Svg);
            // Corner reliefs need corners: Square/Rect only. A circle or oval has none, a Line and Text
            // have no interior at all.
            bool canRelieveCorners = tp.Geometry == WorkOrderGeometryKind.Square || tp.Geometry == WorkOrderGeometryKind.Rect;
            Show(pnlCornerReliefsRow, canRelieveCorners);
            chkCornerReliefs.IsChecked = tp.CornerReliefs;
            Show(pnlHasTextRow, canShapeText);
            chkHasText.IsChecked = tp.HasText;
            Show(pnlTextRow, showText);
            Show(fldCapHeight, showText);
            Show(pnlFontRow, showText);
            Show(pnlFontStyleRow, showText);
            // Artwork rows. Deliberately NOT folded into showText: an SVG toolpath has no text, no cap
            // height and no font, and reusing those rows would have offered a font for a logo.
            bool isSvg = tp.Geometry == WorkOrderGeometryKind.Svg;
            Show(pnlSvgFileRow, isSvg);
            Show(fldSvgWidth, isSvg);
            Show(pnlSvgInfoRow, isSvg);
            if (isSvg)
                UpdateSvgInfo();
            Show(pnlTextHAlignRow, showText && !isText);
            Show(pnlTextVAlignRow, showText && !isText);
            // Sliding a rectangle around inside a curve voids the inscribed-fit guarantee - see
            // WorkOrderTextFit - so circles and ovals stay centered.
            bool centerOnly = tp.Geometry == WorkOrderGeometryKind.Circle || tp.Geometry == WorkOrderGeometryKind.Oval;
            cbxTextHAlign.IsEnabled = cbxTextVAlign.IsEnabled = !centerOnly;
            cbxTextHAlign.SelectedIndex = centerOnly ? (int)WorkOrderTextHAlign.Center : (int)tp.TextHAlign;
            cbxTextVAlign.SelectedIndex = centerOnly ? (int)WorkOrderTextVAlign.Center : (int)tp.TextVAlign;
            Show(fldDiameter, isCircle);
            Show(fldSize, tp.Geometry == WorkOrderGeometryKind.Square);
            Show(fldWidth, isWD && !entireSpoilboard);
            Show(fldDepthY, isWD && !entireSpoilboard);
            Show(pnlEntireSpoilboard, isSurface);

            Show(pnlIndirectSource, tp.IsIndirect);
            if (tp.IsIndirect)
            {
                // Every OTHER non-Indirect toolpath is a legal source - excluding Indirect ones keeps this to a
                // single hop rather than a chain WorkOrderCompiler would have to resolve recursively.
                cbxIndirectSource.Items.Clear();
                foreach (var candidate in workOrder.Toolpaths.Where(t => !ReferenceEquals(t, tp) && !t.IsIndirect))
                    cbxIndirectSource.Items.Add(candidate.Name);
                // Groups are offered alongside single toolpaths - a group reference copies every member at
                // once, so adding a toolpath to the group later adds it to every copy without going and
                // finding them. That is the whole reason to point at a group rather than at each member.
                // Listed after the toolpaths and marked, since the two share one namespace here and
                // resolution tries toolpaths first (see WorkOrderRules.Expand).
                foreach (var g in WorkOrderRules.GroupNames(workOrder).Where(g => !ReferenceEquals(tp.Group, g)))
                    cbxIndirectSource.Items.Add(g);
                cbxIndirectSource.SelectedItem = tp.IndirectSource;
            }

            // Indirect already IS a single repeat of the source at a different X/Y - a pattern of its OWN on
            // top of that would be a repeat of a repeat, and everything else about the cut lives on the
            // source anyway (see pnlPatternSection's own comment), so the whole section is hidden rather than
            // just left blank.
            //
            // Which is NOT the same as an Indirect toolpath never patterning: it inherits the SOURCE's
            // pattern, so pointing one at a 3x2 grid cuts six instances at the new position (see
            // WorkOrderCompiler.ResolveIndirect, and the preview, which draws them). What is being refused
            // here is a second pattern layered over that one - the count still shows up in the tree row,
            // via WorkOrderRules.Summarize.
            Show(pnlPatternSection, !tp.IsIndirect);
            if (!tp.IsIndirect)
            {
                cbxPattern.SelectedIndex = Array.IndexOf(WorkOrderRules.AllPatterns, tp.Pattern);
                fldColumns.Value = tp.Columns; fldColumnSpacing.Value = tp.ColumnSpacing;
                fldRows.Value = tp.Rows; fldRowSpacing.Value = tp.RowSpacing;
                fldPatternCount.Value = tp.PatternCount; fldPatternRadius.Value = tp.PatternRadius;
                fldPatternStartAngle.Value = tp.PatternStartAngle; fldPatternArcSpan.Value = tp.PatternArcSpan;

                Show(pnlGrid, tp.Pattern == WorkOrderPatternKind.Grid);
                Show(pnlCircular, tp.Pattern == WorkOrderPatternKind.Circular);

                int instances = tp.InstanceCount;
                txtPatternSummary.Text = instances > 1
                    ? string.Format("{0} instances - every operation on this toolpath is cut at each one.", instances)
                    : string.Empty;
            }
        }

        private void LoadOperationFields()
        {
            var op = selectedOp;
            txtPanelHeader.Text = WorkOrderRules.OpLabel(op.Kind);

            fldHoleDiameter.Value = op.HoleDiameter;
            fldTotalDepth.Value = op.TotalDepth;
            fldDepthOfCut.Value = op.DepthOfCut;
            fldPeckDepth.Value = op.PeckDepth;
            fldBoreStepDown.Value = op.BoreStepDown;
            fldStepover.Value = op.Stepover;
            fldNumTabs.Value = op.NumTabs; fldTabWidth.Value = op.TabWidth; fldTabHeight.Value = op.TabHeight;
            fldWallStockToLeave.Value = op.WallStockToLeave;
            fldFloorStockToLeave.Value = op.FloorStockToLeave;
            fldChamferDepth.Value = op.ChamferDepth;
            fldEngraveWidth.Value = op.EngraveWidth;
            fldCarveMaxDepth.Value = op.CarveMaxDepth;
            fldCountersinkDiameter.Value = op.CountersinkDiameter;
            chkThrough.IsChecked = op.Through;

            bool supportsThrough = WorkOrderRules.SupportsThrough(op.Kind);
            // Finishing passes and the chamfer follow the roughing operation's depth rather than setting their
            // own, so they show no depth field at all.
            bool ownsDepth = op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour
                          || op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore
                          || op.Kind == WorkOrderOpKind.Surface;

            bool isHole = op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore;

            Show(pnlThrough, supportsThrough);
            Show(fldHoleDiameter, isHole);
            // A through cut takes its depth from the stock thickness, so Total depth has nothing left to say.
            Show(fldTotalDepth, ownsDepth && !(supportsThrough && op.Through));
            // Surface is a single skim pass, not stepped roughing - no depth-of-cut to set.
            // Engrave included: for a V-carve this is the depth STEP between iso-contour levels, which was
            // a hardcoded 0.5 mm until it turned out the shallower levels are geometrically redundant
            // wherever a region reaches full depth - a deeper pass's flank runs through the tip of the one
            // above, so the deepest pass alone recreates the whole V. See WorkOrderCompiler.CarveStep.
            Show(fldDepthOfCut, op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour
                             || op.Kind == WorkOrderOpKind.Engrave);
            Show(fldPeckDepth, op.Kind == WorkOrderOpKind.Drill);
            Show(fldBoreStepDown, op.Kind == WorkOrderOpKind.Bore);

            // Say whether this hole size is something a drill actually comes in, and for a bore whether the
            // bit is big enough to reach the middle.
            Show(txtDrillMatch, isHole);
            if (op.Kind == WorkOrderOpKind.Drill)
                txtDrillMatch.Text = WorkOrderRules.TryMatchDrill(op.HoleDiameter, out string drill)
                    ? string.Format("Standard {0} drill.", drill)
                    : "Not a standard drill size - use a Bore operation instead.";
            else if (op.Kind == WorkOrderOpKind.Bore)
                txtDrillMatch.Text = WorkOrderRules.NeedsSteppedBore(op.HoleDiameter, op.BitDiameter)
                    ? string.Format("Bored with the Ã˜{0:0.##} mm bit in stepped helical passes (the bit alone can't reach the middle of a Ã˜{1:0.##} mm hole).", op.BitDiameter, op.HoleDiameter)
                    : string.Format("Bored in one continuous helix with the Ã˜{0:0.##} mm bit.", op.BitDiameter);
            // Stepover only matters where an enclosed area gets cleared - a pocket, a floor lap, or a bore
            // wide enough to need more than one helix.
            Show(fldStepover, op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.BottomFinish || op.Kind == WorkOrderOpKind.Surface
                           || (op.Kind == WorkOrderOpKind.Bore && WorkOrderRules.NeedsSteppedBore(op.HoleDiameter, op.BitDiameter)));
            Show(fldWallStockToLeave, op.Kind == WorkOrderOpKind.SideFinish);
            Show(fldFloorStockToLeave, op.Kind == WorkOrderOpKind.BottomFinish);
            Show(fldChamferDepth, op.Kind == WorkOrderOpKind.Chamfer);

            // Engraving asks for a stroke WIDTH, but what gets cut is a depth - so show the depth the
            // current V-bit will actually plunge to. It is not a detail: the same 0.8 mm line is 0.40 mm
            // deep on a 90-degree bit and 0.69 on a 60, and that difference decides whether a shallow
            // engraving goes through a veneer.
            bool isEngrave = op.Kind == WorkOrderOpKind.Engrave;
            // A V-carve has no stroke width to ask for - depth follows the shape's own local width - so
            // the width field gives way and the note explains where depth comes from instead.
            // CarvesOutlines, NOT IsCarved: the latter is a question about the FONT and is false for an
            // SVG, so this used to offer artwork a Stroke width field the compiler ignores and quote it a
            // stroke plunge depth. It is the same property BuildEngrave routes on, so the editor and the
            // cut cannot disagree about what this toolpath is.
            bool isCarve = isEngrave && selectedToolpath != null && selectedToolpath.CarvesOutlines;
            Show(fldEngraveWidth, isEngrave && !isCarve);
            // The mirror image of the width field: a carve has no stroke width to ask for, but it is the
            // only thing that HAS a depth worth capping (a stroke engrave's depth already follows from
            // the width above it).
            Show(fldCarveMaxDepth, isEngrave && isCarve);
            Show(txtEngraveDepth, isEngrave);
            if (isEngrave)
            {
                var vtool = CustomTools.Find(op.Tool);
                double half = vtool != null ? vtool.HalfAngleRad : Math.PI / 4d;
                double deg = half * 360d / Math.PI;
                // Same helper the compiler uses, so what this says is what will be cut - clamp included.
                var cut = vtool != null ? vtool.EngraveCutFor(op.EngraveWidth)
                                        : new EngraveCut { Width = Math.Max(0.01d, op.EngraveWidth),
                                                           Depth = Math.Max(0.01d, op.EngraveWidth) / 2d };

                string note = vtool == null ? "  (no tool selected - assuming 90°)"
                            : vtool.Kind != CustomToolKind.VBitOrChamfer && vtool.Kind != CustomToolKind.Countersink
                                  ? "  Pick a V-bit for this operation."
                                  : string.Empty;

                if (isCarve)
                {
                    // Same helper the compiler uses, so what this says is what will be cut - the cap and
                    // the bit-limit clamp included. Deriving it separately here is how the note and the
                    // cut drift apart (the reason EngraveCutFor exists, applied to the carve's ceiling).
                    var carve = vtool != null ? vtool.CarveDepthFor(op.CarveMaxDepth)
                                              : new CarveDepth { Depth = op.CarveMaxDepth > 0d ? op.CarveMaxDepth : 3d,
                                                                 BitLimit = 3d, Requested = op.CarveMaxDepth > 0d };

                    // Say plainly when a requested cap could not be honoured, rather than quietly cutting
                    // shallower than asked - the same courtesy the stroke branch pays a clamped width.
                    if (carve.Clamped)
                        note = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "  Capped at {0:0.###} mm - the deepest this bit can carve.", carve.BitLimit) + note;

                    txtEngraveDepth.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Depth follows the letter shapes - up to {0:0.###} mm with the {1:0.#}° bit{2}. Wider strokes bottom out flat and are cleared.{3}",
                        carve.Depth, deg, carve.Requested && !carve.Clamped ? " (your cap)" : string.Empty, note);
                }
                else
                {
                    // Say it plainly rather than quietly cutting something narrower than was asked for: past its
                    // own diameter the bit's cone has run out and the shank would be doing the cutting.
                    if (cut.Clamped)
                        note = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "  Limited to {0:0.###} mm - the widest this bit can cut.", cut.Width) + note;

                    txtEngraveDepth.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0:0.###} mm deep with the {1:0.#}° bit.{2}", cut.Depth, deg, note);
                }
            }
            Show(fldCountersinkDiameter, op.Kind == WorkOrderOpKind.Countersink);
            Show(pnlTabs, selectedToolpath != null && WorkOrderRules.SupportsTabs(selectedToolpath, op));

            // A drill's diameter IS the hole - it comes from the geometry, so there's no bit to choose here.
            btnFeedsSpeeds.IsEnabled = true;
        }

        // Write the visible fields back into whatever is selected. No-op while LoadFields is populating them.
        private void CaptureFields()
        {
            if (loadingFields)
                return;

            if (selectedOp != null)
            {
                var op = selectedOp;
                op.HoleDiameter = fldHoleDiameter.Value;
                op.TotalDepth = fldTotalDepth.Value;
                op.DepthOfCut = fldDepthOfCut.Value;
                op.PeckDepth = fldPeckDepth.Value;
                op.BoreStepDown = fldBoreStepDown.Value;
                op.Stepover = fldStepover.Value;
                op.NumTabs = fldNumTabs.Value; op.TabWidth = fldTabWidth.Value; op.TabHeight = fldTabHeight.Value;
                op.WallStockToLeave = fldWallStockToLeave.Value;
                op.FloorStockToLeave = fldFloorStockToLeave.Value;
                op.ChamferDepth = fldChamferDepth.Value;
                op.EngraveWidth = fldEngraveWidth.Value;
                op.CarveMaxDepth = fldCarveMaxDepth.Value;
                op.CountersinkDiameter = fldCountersinkDiameter.Value;
                // The target diameter drives the bit choice, not the other way around - confirmed on real
                // hardware 2026-07-30 (operator: a 19.5mm target should pick the 21mm bit automatically).
                // Only for a Countersink op - CountersinkDiameter is still captured unconditionally above
                // (harmless for any other kind, same as every other hidden field here), but re-picking op.Tool
                // from it would silently clobber an unrelated operation's own tool choice.
                if (op.Kind == WorkOrderOpKind.Countersink)
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SmallestCountersinkBitFor(op.CountersinkDiameter);
            }
            else if (selectedToolpath != null)
            {
                var tp = selectedToolpath;
                tp.X = fldX.Value; tp.Y = fldY.Value;
                if (cbxAnchor.SelectedIndex >= 0)
                    tp.Anchor = WorkOrderRules.AllAnchors[cbxAnchor.SelectedIndex];

                if (tp.IsIndirect)
                {
                    if (cbxOffsetMode.SelectedIndex >= 0)
                        tp.OffsetMode = WorkOrderRules.AllOffsetModes[cbxOffsetMode.SelectedIndex];
                    UpdateIndirectName(tp);
                }
                else
                {
                    tp.Length = fldLength.Value; tp.Angle = fldAngle.Value;
                    tp.Text = txtEngraveText.Text; tp.CapHeight = fldCapHeight.Value;
                    tp.SvgFile = txtSvgFile.Text; tp.SvgWidth = fldSvgWidth.Value;
                    tp.FontFamily = cbxFont.SelectedIndex > 0 ? (string)cbxFont.SelectedItem : string.Empty;
                    tp.FontBold = chkFontBold.IsChecked == true; tp.FontItalic = chkFontItalic.IsChecked == true;
                    // HasText itself is toggled in chkHasText_Click (it adds/removes the Engrave op);
                    // only the alignment choices are captured here, and only where they're editable.
                    if (WorkOrderRules.SupportsShapeText(tp.Geometry) && cbxTextHAlign.IsEnabled)
                    {
                        if (cbxTextHAlign.SelectedIndex >= 0) tp.TextHAlign = (WorkOrderTextHAlign)cbxTextHAlign.SelectedIndex;
                        if (cbxTextVAlign.SelectedIndex >= 0) tp.TextVAlign = (WorkOrderTextVAlign)cbxTextVAlign.SelectedIndex;
                    }
                    tp.Diameter = fldDiameter.Value; tp.Size = fldSize.Value;
                    tp.Width = fldWidth.Value; tp.Depth = fldDepthY.Value;
                    tp.Columns = fldColumns.Value; tp.ColumnSpacing = fldColumnSpacing.Value;
                    tp.Rows = fldRows.Value; tp.RowSpacing = fldRowSpacing.Value;
                    tp.PatternCount = fldPatternCount.Value; tp.PatternRadius = fldPatternRadius.Value;
                    tp.PatternStartAngle = fldPatternStartAngle.Value; tp.PatternArcSpan = fldPatternArcSpan.Value;

                    int n = tp.InstanceCount;
                    txtPatternSummary.Text = n > 1
                        ? string.Format("{0} instances - every operation on this toolpath is cut at each one.", n)
                        : string.Empty;
                }
            }
            else
                return;

            // The artwork readout is derived from SvgWidth, so it has to be re-derived whenever a field
            // changes - not only when a file is picked, which is all the browse handler covered.
            if (selectedToolpath != null && selectedToolpath.Geometry == WorkOrderGeometryKind.Svg)
                UpdateSvgInfo();

            OnWorkOrderChanged();
        }

        private void txtName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null)
                return;
            selectedToolpath.Name = txtName.Text;
            OnWorkOrderChanged();
        }

        // Shape text on/off. Ticking adds the Engrave operation that will cut the text (with the V-bit
        // suggestion NewOperation already applies); unticking removes it - leaving it would leave an
        // Engrave op that no longer has anything to engrave, flagged by Validate but better not created.
        // The artwork's real extent at the chosen width, plus anything the import cannot read. Shown
        // in the editor rather than saved for Generate: "will this fit the stave, and can we even cut
        // it" are questions the operator is asking WHILE choosing the file, and finding out at Generate
        // - or worse, from a comment buried in 30,000 lines of g-code - is too late to be useful.
        private void UpdateSvgInfo()
        {
            string path = txtSvgFile.Text;
            if (string.IsNullOrWhiteSpace(path))
            {
                txtSvgInfo.Text = "No file chosen.";
                return;
            }
            if (!System.IO.File.Exists(path))
            {
                txtSvgInfo.Text = "File not found.";
                return;
            }

            var r = SvgOutlines.Load(path, fldSvgWidth.Value);
            if (r.Error != null)
                txtSvgInfo.Text = r.Error;
            else if (!r.IsComplete)
                txtSvgInfo.Text = string.Format("{0:0.#} x {1:0.#} mm - CANNOT CUT: this build does not import {2}.",
                                                r.WidthMm, r.HeightMm, r.Describe());
            else
                // Spelled out as a consequence - "at N mm wide it cuts WxH" - because the width is the
                // OPERATOR'S choice, not something read from the file. An SVG carrying only a viewBox has
                // no honest natural size in mm, so there is nothing to default it to and the field starts
                // at 100; saying so here is cheaper than an operator wondering where 100 came from.
                txtSvgInfo.Text = string.Format("Artwork is {0:0.00}:1. At {1:0.#} mm wide it cuts {1:0.#} x {2:0.#} mm, {3} outline{4}.",
                                                r.HeightMm > 0d ? r.WidthMm / r.HeightMm : 0d,
                                                r.WidthMm, r.HeightMm, r.Contours.Count,
                                                r.Contours.Count == 1 ? string.Empty : "s");
        }

        private void btnSvgBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog {
                Title = "Choose SVG artwork",
                Filter = "SVG artwork (*.svg)|*.svg|All files (*.*)|*.*",
                CheckFileExists = true
            };
            // Reopen where the last one came from - artwork lives together, and re-picking from the
            // same folder is the common case (a second logo, or a re-export of this one).
            try
            {
                string dir = System.IO.Path.GetDirectoryName(txtSvgFile.Text);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }
            catch { /* a malformed path is not a reason to refuse the dialog */ }

            if (dlg.ShowDialog() != true)
                return;

            txtSvgFile.Text = dlg.FileName;
            UpdateSvgInfo();
            CaptureFields();
        }

        private void chkHasText_Click(object sender, RoutedEventArgs e)
        {
            var tp = selectedToolpath;
            if (loadingFields || tp == null || !WorkOrderRules.SupportsShapeText(tp.Geometry))
                return;

            tp.HasText = chkHasText.IsChecked == true;
            if (tp.HasText && !tp.Operations.Any(o => o.Kind == WorkOrderOpKind.Engrave))
                tp.Operations.Add(NewOperation(WorkOrderOpKind.Engrave, tp));
            else if (!tp.HasText)
                tp.Operations.RemoveAll(o => o.Kind == WorkOrderOpKind.Engrave);

            // The op list changed shape, and the text fields' visibility follows the checkbox.
            RebuildTree(tp);
            OnWorkOrderChanged();
        }

        // Corner reliefs on/off. Unlike shape text this adds no operation - it only changes the wall path
        // the existing Pocket/Side finish already cut, so committing the flag and redrawing is all of it.
        private void chkCornerReliefs_Click(object sender, RoutedEventArgs e)
        {
            var tp = selectedToolpath;
            if (loadingFields || tp == null)
                return;

            tp.CornerReliefs = chkCornerReliefs.IsChecked == true;
            OnWorkOrderChanged();
        }

        // The anchor only reinterprets X/Y - the numbers are left exactly as typed - so all this has to do is
        // commit the choice and redraw. The shape moving is the point, not a side effect.
        private void cbxAnchor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null || cbxAnchor.SelectedIndex < 0)
                return;
            selectedToolpath.Anchor = WorkOrderRules.AllAnchors[cbxAnchor.SelectedIndex];
            OnWorkOrderChanged();
        }

        // Like the anchor above, this RE-INTERPRETS the X/Y already typed rather than recomputing them - the
        // geometry moves, the numbers stay as they are. Switching an Indirect toolpath sitting at (50,0) to
        // Relative leaves it reading (50,0), now meaning 50 mm past its source instead of 50 mm from the WCS
        // origin. Rewriting the numbers to hold the shape still would be the other choice, and it is the
        // wrong one here: the offset you want from a source is nearly always a round number you'd type, not
        // whatever the difference happens to be.
        //
        // The generated name carries the mode (see UpdateIndirectName), so which one is in force stays
        // visible in the tree rather than only on the selected toolpath's own panel.
        private void cbxOffsetMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null || cbxOffsetMode.SelectedIndex < 0)
                return;
            selectedToolpath.OffsetMode = WorkOrderRules.AllOffsetModes[cbxOffsetMode.SelectedIndex];
            UpdateIndirectName(selectedToolpath);
            OnWorkOrderChanged();
        }

        // Joining a group MOVES the toolpath to sit with the rest of that group, rather than only relabelling
        // it. Schedule's default program order is tree order, so a header drawn around toolpaths that are
        // scattered through the run would show a grouping the machine doesn't cut - and for an Indirect
        // pointing at the group, the members' offsets are taken from the FIRST one, which needs to be a
        // stable, visible thing rather than whichever happened to be added first.
        //
        // Leaving a group doesn't move anything back: there is nowhere to move it to, and the position it
        // has is as good as any. Only the label is cleared.
        private void cbxGroup_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null)
                return;

            string name = (cbxGroup.Text ?? string.Empty).Trim();
            if (string.Equals(name, selectedToolpath.Group ?? string.Empty, StringComparison.Ordinal))
                return;

            // An Indirect toolpath names its source in one namespace shared by toolpaths and groups, and
            // resolution tries toolpaths first (WorkOrderRules.Expand). A group sharing a toolpath's name
            // would therefore be permanently unreachable - referencing it would silently get the toolpath
            // instead. Refused at the point of naming, where it can still be corrected, rather than left to
            // be discovered as a copy of the wrong thing.
            if (!string.IsNullOrEmpty(name)
                && workOrder.Toolpaths.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                AppDialogs.Show(string.Format("\"{0}\" is already a toolpath name. A group needs a name of its own - "
                                            + "an Indirect toolpath picks its source from one list, and the toolpath would "
                                            + "always win.", name),
                                "Work Order", MessageBoxButton.OK, MessageBoxImage.Information);
                loadingFields = true;
                cbxGroup.Text = selectedToolpath.Group ?? string.Empty;
                loadingFields = false;
                return;
            }

            var tp = selectedToolpath;
            tp.Group = name;

            if (!string.IsNullOrEmpty(name))
            {
                // Pull it out FIRST, then look for the run - so the index found is already an index into the
                // list being inserted into, and there is no off-by-one to reason about.
                int wasAt = workOrder.Toolpaths.IndexOf(tp);
                workOrder.Toolpaths.Remove(tp);

                // After the last existing member, so a group grows in the order you add to it. A brand new
                // group has no run to join, so the toolpath stays exactly where it was.
                int last = workOrder.Toolpaths.FindLastIndex(t => string.Equals(t.Group, name, StringComparison.OrdinalIgnoreCase));
                workOrder.Toolpaths.Insert(last >= 0 ? last + 1 : wasAt, tp);
            }

            RebuildTree(tp);
            OnWorkOrderChanged();
        }

        private void cbxGeometry_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null || cbxGeometry.SelectedIndex < 0)
                return;

            var kind = WorkOrderRules.AllGeometries[cbxGeometry.SelectedIndex];
            if (kind == selectedToolpath.Geometry)
                return;
            selectedToolpath.Geometry = kind;

            // Changing between open and closed can invalidate operations already on this toolpath (Pocket and
            // Bottom finish need an enclosed area) - drop those rather than leave the work order in a state
            // Generate would just reject. Switching TO Indirect drops every operation the same way (it's
            // never in AvailableOperations - see WorkOrderRules) and also clears any Pattern, since Indirect
            // already IS a single repeat of the source elsewhere - patterning it too would be a repeat of a
            // repeat (see pnlPatternSection's own comment).
            var allowed = WorkOrderRules.AvailableOperations(selectedToolpath).ToList();
            int dropped = selectedToolpath.Operations.RemoveAll(o => !allowed.Contains(o.Kind));
            if (dropped > 0)
                AppDialogs.Show(string.Format("{0} operation{1} removed - not possible on {2}.", dropped, dropped == 1 ? "" : "s",
                    kind == WorkOrderGeometryKind.Indirect ? "an Indirect toolpath" : "an open geometry"),
                    "Work Order", MessageBoxButton.OK, MessageBoxImage.Information);

            if (kind == WorkOrderGeometryKind.Indirect)
            {
                selectedToolpath.Pattern = WorkOrderPatternKind.None;
                // Groups hold leaf toolpaths only - a reference in a group is what would let a group contain
                // itself. Dropped here rather than refused, matching how the Pattern above and the operations
                // are dropped when a toolpath becomes Indirect.
                selectedToolpath.Group = string.Empty;
                if (string.IsNullOrEmpty(selectedToolpath.IndirectSource))
                    selectedToolpath.IndirectSource = workOrder.Toolpaths.FirstOrDefault(t => !ReferenceEquals(t, selectedToolpath) && !t.IsIndirect)?.Name;
                UpdateIndirectName(selectedToolpath);
            }

            // Switching TO Text lands on a geometry with exactly one possible operation - give it the same
            // head start a freshly added Text toolpath gets. The drop above has already removed whatever the
            // previous geometry's operations were, so this cannot stack on top of them.
            EnsureEngraveOperation(selectedToolpath);

            RebuildTree(selectedToolpath);
            LoadFields();
            OnWorkOrderChanged();
        }

        private void cbxIndirectSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null)
                return;
            selectedToolpath.IndirectSource = cbxIndirectSource.SelectedItem as string;
            UpdateIndirectName(selectedToolpath);
            RebuildTree(selectedToolpath);
            OnWorkOrderChanged();
        }

        // Indirect's name is generated from what it actually does - "@source(x,y)" - rather than typed, since
        // there's nothing else about it left to name: no dimensions, no operations of its own, just a source
        // and a position. Keeps the tree/diagram label honest without the operator having to keep it in sync
        // by hand every time the source or position changes.
        private void UpdateIndirectName(WorkOrderToolpath tp)
        {
            string source = string.IsNullOrEmpty(tp.IndirectSource) ? "?" : tp.IndirectSource;
            // Relative offsets are signed (@Src(+50,+0)) so the tree distinguishes "50 mm past the source"
            // from "at X=50" without having to select the toolpath to find out which.
            string fmt = tp.OffsetMode == WorkOrderOffsetMode.Relative ? "@{0}({1:+0.###;-0.###;+0},{2:+0.###;-0.###;+0})"
                                                                      : "@{0}({1:0.###},{2:0.###})";
            tp.Name = string.Format(fmt, source, tp.X, tp.Y);
            if (ReferenceEquals(selectedToolpath, tp))
            {
                loadingFields = true;
                txtName.Text = tp.Name;
                loadingFields = false;
            }
        }

        private void chkGroupByTool_Click(object sender, RoutedEventArgs e)
        {
            if (loadingFields)
                return;
            workOrder.GroupByTool = chkGroupByTool.IsChecked == true;
            OnWorkOrderChanged();
        }

        private void chkSkipFirstToolChange_Click(object sender, RoutedEventArgs e)
        {
            if (loadingFields)
                return;
            workOrder.SkipFirstToolChange = chkSkipFirstToolChange.IsChecked == true;
            OnWorkOrderChanged();
        }

        private void cbxWcs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields || cbxWcs.SelectedIndex < 0)
                return;
            // Index 0 = "Follow Setup" = WorkOrder.Wcs 0; indices 1-6 = pinned G54-G59 = WorkOrder.Wcs 1-6 -
            // same numbering, no offset.
            workOrder.Wcs = cbxWcs.SelectedIndex;
            OnWorkOrderChanged();
        }

        // Names the tool the program will start on, so the claim being made ("it's already loaded") is about a
        // specific tool rather than an abstract one - and grouping can change which tool that is.
        private void UpdateSkipFirstToolChangeSummary()
        {
            if (txtSkipFirstToolChangeSummary == null)
                return;

            int first = WorkOrderCompiler.FirstToolNumber(workOrder);
            if (first == int.MinValue)
                txtSkipFirstToolChangeSummary.Text = string.Empty;
            else if (workOrder.SkipFirstToolChange)
                txtSkipFirstToolChangeSummary.Text = string.Format("No M6 for T{0} - its tool length offset is assumed valid.", first);
            else
                txtSkipFirstToolChangeSummary.Text = string.Format("Program starts by asking for T{0}.", first);
        }

        // Says what the setting actually buys on THIS work order - the count of tool changes either way, since
        // grouping can only combine tools where each toolpath's own operation order allows it.
        private void UpdateGroupByToolSummary()
        {
            if (txtGroupByToolSummary == null)
                return;

            int asComposed = WorkOrderCompiler.ToolChangeCount(workOrder, false);
            int grouped = WorkOrderCompiler.ToolChangeCount(workOrder, true);

            if (asComposed == 0)
                txtGroupByToolSummary.Text = string.Empty;
            else if (grouped >= asComposed)
                txtGroupByToolSummary.Text = string.Format("{0} tool change{1} either way - nothing to gain here.",
                    asComposed, asComposed == 1 ? "" : "s");
            else
                txtGroupByToolSummary.Text = string.Format("{0} tool changes as composed, {1} grouped.", asComposed, grouped);
        }

        private void cbxPattern_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null || cbxPattern.SelectedIndex < 0)
                return;
            selectedToolpath.Pattern = WorkOrderRules.AllPatterns[cbxPattern.SelectedIndex];
            LoadFields();
            OnWorkOrderChanged();
        }

        private void chkThrough_Click(object sender, RoutedEventArgs e)
        {
            if (loadingFields || selectedOp == null)
                return;
            selectedOp.Through = chkThrough.IsChecked == true;
            LoadFields();
            OnWorkOrderChanged();
        }

        private void chkSpoilExistingOrigin_Click(object sender, RoutedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null)
                return;
            selectedToolpath.UseExistingOrigin = chkSpoilExistingOrigin.IsChecked == true;
            OnWorkOrderChanged();
        }

        private void chkEntireSpoilboard_Click(object sender, RoutedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null)
                return;
            selectedToolpath.EntireSpoilboard = chkEntireSpoilboard.IsChecked == true;
            LoadFields();
            OnWorkOrderChanged();
        }

        private void btnFeedsSpeeds_Click(object sender, RoutedEventArgs e)
        {
            if (selectedOp == null)
                return;

            var op = selectedOp;
            var tp = selectedToolpath;
            string material = StartJobConfig.Section?.Material ?? string.Empty;
            bool isDrill = op.Kind == WorkOrderOpKind.Drill;
            bool isBore = op.Kind == WorkOrderOpKind.Bore;
            bool isCountersink = op.Kind == WorkOrderOpKind.Countersink;
            bool showDoc = op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour
                        || op.Kind == WorkOrderOpKind.Chamfer || isDrill || isBore || isCountersink;

            string docLabel = op.Kind == WorkOrderOpKind.Chamfer ? "Chamfer depth:"
                            : isCountersink ? "Countersink diameter:"
                            : isDrill ? "Peck depth:"
                            : isBore ? "Step down (per rev):"
                            : "Depth of cut:";
            double doc = op.Kind == WorkOrderOpKind.Chamfer ? op.ChamferDepth
                       : isCountersink ? op.CountersinkDiameter
                       : isDrill ? op.PeckDepth
                       : isBore ? op.BoreStepDown
                       : op.DepthOfCut;

            // Drill/Countersink are a straight on-center plunge - no path to reverse, so no direction choice.
            bool showDirection = !isDrill && !isCountersink;

            var dlg = new OddJobsFeedsSpeedsDialog(op.Tool, docLabel: docLabel, showDoc: showDoc, showDirection: showDirection)
            {
                Owner = Window.GetWindow(this),
                // Flutes deliberately NOT set - the dialog's tool dropdown sets the right count for the
                // selected tool (CustomTool.Flutes). The old wizards all overrode it with a hardcoded 2,
                // so the 3-flute roughing bit computed its chip load as if it were 2-flute.
                // A drill's diameter is the hole itself, so it comes from the geometry, not from a bit field.
                // Everything else seeds from the TOOL DEFINITION, not the operation's stored copy: the
                // dialog's constructor selects the tool (which sets the right diameter), but an object
                // initializer runs AFTER the constructor, so seeding op.BitDiameter here stomped that with
                // the operation's stale 6.35 default - edit the bit to 12.5 mm in its definition and every
                // dialog still opened saying 6.35, forever, because OK wrote the stale value back. The
                // field stays editable for a one-off override, but each open follows the definition.
                BitDiameter = isDrill ? op.HoleDiameter
                            : CustomTools.Find(op.Tool)?.DiameterMm > 0d ? CustomTools.Find(op.Tool).DiameterMm
                            : op.BitDiameter,
                SpindleRPM = op.SpindleRPM, Feed = op.Feed, PlungeFeed = op.PlungeFeed,
                DepthOfCut = doc,
                Material = material,
                IsHssDrill = op.DrillHss,
                CutDirection = op.Direction,
                // A ball engaging near its TIP (a bottom-finish pass skimming the floor) behaves like a much
                // smaller cutter than nominal - the advisor needs the engagement depth to say anything useful.
                EngagementDepthMm = op.Kind == WorkOrderOpKind.BottomFinish ? op.FloorStockToLeave : (double?)null
            };
            dlg.RestrictToolsFor(op.Kind);

            if (dlg.ShowDialog() != true)
                return;

            op.Tool = dlg.SelectedToolValue;
            // A drill's size is dictated by the hole, so whatever the dialog shows there isn't the operator's
            // to change - everything else takes the dialog's diameter.
            if (!isDrill)
                op.BitDiameter = dlg.BitDiameter;
            op.SpindleRPM = dlg.SpindleRPM; op.Feed = dlg.Feed; op.PlungeFeed = dlg.PlungeFeed;
            if (showDirection)
                op.Direction = dlg.CutDirection;
            if (op.Kind == WorkOrderOpKind.Chamfer)
                op.ChamferDepth = dlg.DepthOfCut;
            else if (isCountersink)
                op.CountersinkDiameter = dlg.DepthOfCut;
            else if (isDrill)
            {
                op.PeckDepth = dlg.DepthOfCut;
                op.DrillHss = dlg.IsHssDrill;
            }
            else if (isBore)
                op.BoreStepDown = dlg.DepthOfCut;
            else if (showDoc)
                op.DepthOfCut = dlg.DepthOfCut;

            OddJobsToolMemory.Remember(dlg.SelectedToolValue, dlg.BitDiameter, material, dlg.SpindleRPM, dlg.Feed, dlg.PlungeFeed, dlg.DepthOfCut);

            LoadFields();
            OnWorkOrderChanged();
        }

        private void UpdateFeedsSummary()
        {
            if (txtFeedsSummary == null)
                return;
            txtFeedsSummary.Text = selectedOp == null ? string.Empty : FeedsSummaryText(selectedToolpath, selectedOp);
        }

        // Shared by the "Feeds and speeds..." button's own small-text summary (UpdateFeedsSummary, above) and
        // each operation row's ToolTip in the tree (BuildTree) - one op's worth of "what will actually run"
        // (tool/diameter/rpm/feed/plunge/step down/stepover), without having to open the Feeds and speeds
        // dialog or select the op to see it. Step down/stepover applicability mirrors LoadFields' own Show()
        // gating for fldDepthOfCut/fldBoreStepDown/fldStepover - only shown where that op kind actually uses it.
        private static string FeedsSummaryText(WorkOrderToolpath tp, WorkOrderOperation op)
        {
            double rpm = op.SpindleRPM > 0d ? op.SpindleRPM : 0.70d * op.BitMaxRPM;
            double dia = tp != null ? WorkOrderCompiler.EffectiveBitDiameter(tp, op) : op.BitDiameter;
            var s = string.Format("T{0} {1} - Ø{2:0.0##} mm - {3:0} rpm - {4:0}/{5:0} mm/min feed/plunge",
                WorkOrderCompiler.ToolNumberFor(op), CustomTools.Find(op.Tool)?.Name ?? ("tool " + op.Tool), dia, rpm, op.Feed, op.PlungeFeed);

            if (op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour)
                s += string.Format(" - {0:0.0##} mm step down", op.DepthOfCut);
            else if (op.Kind == WorkOrderOpKind.Bore)
                s += string.Format(" - {0:0.0##} mm step down/rev", op.BoreStepDown);

            if (op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.BottomFinish
                || (op.Kind == WorkOrderOpKind.Bore && WorkOrderRules.NeedsSteppedBore(op.HoleDiameter, op.BitDiameter)))
                s += string.Format(" - {0:0}% stepover", op.Stepover);

            if (op.Kind == WorkOrderOpKind.Drill)
                s += string.Format(" - {0} - {1:0.0##} mm peck", op.DrillHss ? "HSS" : "brad point", op.PeckDepth);
            else if (op.Kind == WorkOrderOpKind.Chamfer)
                s += string.Format(" - {0:0.0##} mm chamfer depth", op.ChamferDepth);
            else if (op.Kind == WorkOrderOpKind.Countersink)
                s += string.Format(" - Ø{0:0.##} mm target", op.CountersinkDiameter);
            else if (op.Kind == WorkOrderOpKind.SideFinish)
                s += string.Format(" - {0:0.0##} mm wall stock to leave", op.WallStockToLeave);
            else if (op.Kind == WorkOrderOpKind.BottomFinish)
                s += string.Format(" - {0:0.0##} mm floor stock to leave", op.FloorStockToLeave);

            return s;
        }

        #endregion

        // Any composition or parameter change invalidates a previously generated program - otherwise the Run
        // bar (and the overlay's ProgramView) keep offering g-code built from values that have since changed.
        private void OnWorkOrderChanged()
        {
            RefreshTreeHeaders();
            UpdateFeedsSummary();
            UpdateGroupByToolSummary();
            UpdateSkipFirstToolChangeSummary();
            UpdateValidation();
            DrawDiagram();
            DiscardProgram();
            // Diverged from LastWorkOrderFilePath on disk (if any) - Persist() below saves this alongside the
            // content itself in the same App.config write. See AppConfig.Base.WorkOrderDirty's own comment and
            // Activate(false), which prompts to save when leaving with this set.
            AppConfig.Settings.Base.WorkOrderDirty = true;
            UpdateTitleBar();
            Persist();
        }

        // Cheaper than a full RebuildTree (which would fight the current selection) - the structure hasn't
        // changed, only the summaries a parameter edit affects. The trailing placeholder row is left alone.
        //
        // Walks by TAG, at whatever depth a row sits. This used to pair treeToolpaths.Items[t] with
        // workOrder.Toolpaths[t] BY INDEX, which was fine while every top-level row was a toolpath and became
        // wrong the moment group headers joined that level: index N stopped meaning toolpath N, so the header
        // was given the FIRST toolpath's summary, the toolpaths nested under it were given that toolpath's
        // OPERATION summaries, and everything after shifted by one. It rendered as a group header wearing a
        // toolpath's name over rows wearing operations' names - reported from a real work order, with the
        // model underneath perfectly correct the whole time.
        private void RefreshTreeHeaders()
        {
            foreach (var item in AllRows(treeToolpaths))
            {
                var tp = item.Tag as WorkOrderToolpath;
                if (tp != null)
                {
                    SetCheckHeader(item, WorkOrderRules.Summarize(workOrder, tp), tp.Enabled, tp.Enabled);
                    // An Indirect toolpath's child rows are the BORROWED operations, not its own (it has
                    // none), so this loop simply doesn't run for one - tp.Operations is empty.
                    for (int i = 0; i < tp.Operations.Count && i < item.Items.Count; i++)
                    {
                        var op = tp.Operations[i];
                        // An operation under an unchecked toolpath keeps its own tick but is dimmed too - it
                        // isn't going to run, and showing it bright would be a lie about what Generate emits.
                        SetCheckHeader((TreeViewItem)item.Items[i], WorkOrderRules.Summarize(op), op.Enabled, op.Enabled && tp.Enabled,
                            invalidTool: CustomTools.Find(op.Tool) == null);
                    }
                    continue;
                }

                // A group header's own tick follows its members, so toggling one member updates it without a
                // full rebuild - the same reason this method exists for toolpaths.
                var group = item.Tag as GroupRow;
                if (group != null)
                {
                    var members = WorkOrderRules.GroupMembers(workOrder, group.Name).ToList();
                    bool anyOn = members.Any(m => m.Enabled);   // same rule as MakeGroupItem - see there
                    SetCheckHeader(item, GroupHeaderText(group.Name, members.Count), anyOn, anyOn);
                    continue;
                }

                // A borrowed row describes a toolpath it does NOT own, so editing that source elsewhere has
                // to repaint it here - the operations it lists are the source's, and they change without
                // this row's own Indirect toolpath being touched at all.
                var borrowedRow = item.Tag as BorrowedRow;
                if (borrowedRow != null)
                {
                    bool runs = !WorkOrderRules.IsHeldBack(borrowedRow.Indirect, borrowedRow.Member);
                    SetCheckHeader(item, BorrowedRowText(borrowedRow.Member), runs,
                                   runs && borrowedRow.Indirect.Enabled);
                }
            }
        }

        // Every row in the tree, at any depth.
        //
        // Groups made the tree three levels deep in places (header -> toolpath -> operation) where it had
        // always been two, and both walkers over it assumed two. Recursing by Tag rather than indexing by
        // position is immune to how deeply a row happens to be nested, which is the property that was
        // missing when a new kind of row was added.
        private static IEnumerable<TreeViewItem> AllRows(ItemsControl root)
        {
            foreach (var item in root.Items.OfType<TreeViewItem>())
            {
                yield return item;
                foreach (var child in AllRows(item))
                    yield return child;
            }
        }

        // The toolpath tick and its operations' ticks are kept in agreement rather than being independent
        // gates: toggling the toolpath cascades to every operation under it, and toggling operations pulls the
        // toolpath's own tick to "is anything under me still on?". So the tree always reads as exactly what
        // will run - there's no state where a ticked toolpath contains nothing, or an unticked one hides
        // ticked operations.
        private void ToggleEnabled(WorkOrderToolpath tp, WorkOrderOperation op, bool on)
        {
            if (op != null)
            {
                op.Enabled = on;
                tp.Enabled = tp.Operations.Any(o => o.Enabled);
            }
            else
                SetToolpathEnabled(tp, on);

            // OnWorkOrderChanged refreshes every row's header, which is what repaints the cascaded ticks.
            OnWorkOrderChanged();
        }

        // The one definition of what enabling or disabling a whole toolpath means - its own tick and every
        // operation under it move together. Extracted so the group header drives toolpaths exactly the way
        // a toolpath drives its operations, rather than through a second, subtly different rule.
        private static void SetToolpathEnabled(WorkOrderToolpath tp, bool on)
        {
            tp.Enabled = on;
            foreach (var each in tp.Operations)
                each.Enabled = on;
        }

        private void UpdateValidation()
        {
            List<string> advisories;
            var warnings = WorkOrderRules.Validate(workOrder, out advisories);
            warnings.AddRange(ParameterWarnings());

            // Both shown, only the blocking ones gate. An advisory is marked so it reads as something to
            // consider rather than something to fix - it is in the list precisely BECAUSE this build cannot
            // be certain it is right and the operator can.
            var shown = new List<string>(warnings);
            foreach (var a in advisories)
                shown.Add("Note: " + a);
            txtWarnings.Text = string.Join("\n", shown);
            if (isActiveTab)
            {
                MacroProcessor.IsGenerateReady = warnings.Count == 0 && workOrder.Toolpaths.Count > 0;
                // The reason travels with the gate, so the greyed-out Run bar can say what it wants instead
                // of the operator having to find this panel and read it. First warning plus a count: the
                // whole list would not fit a tooltip, and the first is the one to go and fix.
                MacroProcessor.GenerateBlockedReason =
                    workOrder.Toolpaths.Count == 0 ? "This work order has no toolpaths yet."
                    : warnings.Count == 0 ? string.Empty
                    : warnings.Count == 1 ? warnings[0]
                    : string.Format("{0}\n\n(and {1} more - see the list under the parameters panel)",
                                    warnings[0], warnings.Count - 1);
            }

            // What will actually be EMITTED, so an Indirect toolpath counts for what it copies rather than
            // for the nothing it owns. This read "4 toolpaths, 4 operations" beside five toolpaths cutting
            // 21 instances, because a copy contributed zero to both numbers.
            int ops = workOrder.GeneratedOperationCount;
            int tps = workOrder.Toolpaths.Count(t => workOrder.ContributedOperationCount(t) > 0);
            string summary = workOrder.Toolpaths.Count == 0
                ? "Add a toolpath to get started."
                : string.Format("{0} toolpath{1}, {2} operation{3} - runs as one program in the order listed.",
                    tps, tps == 1 ? "" : "s", ops, ops == 1 ? "" : "s");

            // Says so plainly rather than leaving the operator to notice the tree is partly unticked - the
            // program that comes out will be missing operations they authored.
            if (workOrder.AnyHeldBack)
                summary += string.Format(" {0} of {1} held back (unchecked).",
                    workOrder.TotalOperationCount - ops, workOrder.TotalOperationCount);

            txtSummary.Text = summary;
        }

        // Per-toolpath numeric sanity, carried over from the old wizards' own UpdateSummary checks.
        private List<string> ParameterWarnings()
        {
            var warnings = new List<string>();
            double thickness = StartJobConfig.Section?.Thickness ?? 0d;

            foreach (var tp in workOrder.Toolpaths)
            {
                string label = tp.Name + ": ";
                double minSpan = tp.MinSpan;
                double wallLeave = tp.Operations.FirstOrDefault(o => o.Kind == WorkOrderOpKind.SideFinish)?.WallStockToLeave ?? 0d;
                var rough = WorkOrderRules.RoughingOp(tp);

                foreach (var op in tp.Operations)
                {
                    string opLabel = label + WorkOrderRules.OpLabel(op.Kind) + " - ";

                    // Any tool (Settings:App > Work Order) - factory-default or operator-added - can be
                    // deleted out from under an operation that still references it - flag it explicitly
                    // rather than let the generic "bit diameter must be > 0" check below fire on whatever
                    // stale op.BitDiameter was last saved.
                    if (CustomTools.Find(op.Tool) == null)
                    {
                        warnings.Add(opLabel + "this operation's tool has since been deleted - pick a different tool.");
                        continue;
                    }

                    bool ownsDepth = op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour
                                  || op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore
                                  || op.Kind == WorkOrderOpKind.Surface;

                    if (ownsDepth)
                    {
                        if (op.Through && thickness <= 0d)
                            warnings.Add(opLabel + "set the stock thickness on the Setup tab for a through cut.");
                        if (!op.Through && op.TotalDepth <= 0d)
                            warnings.Add(opLabel + "set a total depth (or tick Through with a stock thickness).");
                    }

                    // A Drill/Bore's own HoleDiameter is independent of the toolpath's nominal Diameter (it
                    // only seeds the INITIAL default - see NewOperation) - nothing constrains it afterward, so
                    // it can end up wider than what the toolpath nominally represents with no warning at all.
                    // Flagged rather than clipped: silently resizing would override a value the operator
                    // explicitly typed, and legitimately wanting a wider counterbore than the toolpath's own
                    // label - though unusual - isn't impossible. Confirmed as a real gap 2026-07-30 (an 18.5mm
                    // bore on an 18mm toolpath compiled and cut exactly as entered, wider than the diagram
                    // shows and wider than nearby feature spacing may have assumed).
                    if ((op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore)
                        && tp.Geometry == WorkOrderGeometryKind.Circle && op.HoleDiameter > tp.Diameter + 1e-6)
                        warnings.Add(opLabel + string.Format("hole Ø{0:0.##} mm is wider than the toolpath's own Ø{1:0.##} mm - resize the toolpath to match, or this cuts wider than the diagram shows.", op.HoleDiameter, tp.Diameter));

                    // A drill's diameter comes from the geometry, so there's no bit size of its own to check.
                    if (op.Kind != WorkOrderOpKind.Drill && op.BitDiameter <= 0d)
                    {
                        warnings.Add(opLabel + "bit diameter must be greater than 0.");
                        continue;
                    }

                    if (op.Kind == WorkOrderOpKind.Pocket && minSpan <= op.BitDiameter + 2d * wallLeave)
                        warnings.Add(opLabel + "too small to pocket with this bit" + (wallLeave > 0d ? " plus the wall stock to leave." : "."));

                    if (op.Kind == WorkOrderOpKind.Bore && op.BitDiameter >= op.HoleDiameter)
                        warnings.Add(opLabel + "bit must be smaller than the hole to bore it - use a Drill if the bit IS the hole size.");

                    if (op.Kind == WorkOrderOpKind.Countersink)
                    {
                        if (tp.Geometry != WorkOrderGeometryKind.Circle)
                            warnings.Add(opLabel + "a countersink bit only plunges a round hole - pick a Chamfer operation for this shape.");
                        else if (!CustomTools.IsCountersink(op.Tool))
                            warnings.Add(opLabel + "pick one of the countersink bits for this operation.");
                        else
                        {
                            // Same 45-deg-per-side geometry as the V-bit trace - the cone can't reach a
                            // diameter wider than the bit's own, so the deepest usable target is the bit's
                            // own diameter.
                            if (op.BitDiameter < op.CountersinkDiameter)
                                warnings.Add(opLabel + string.Format("bit too small to reach this target diameter - a {0:0.0##} mm bit can't cut wider than {0:0.0##} mm across.", op.BitDiameter));
                            if (op.BitDiameter <= tp.Diameter)
                                warnings.Add(opLabel + "bit must be larger than the hole to chamfer its rim, not just re-cut the hole itself.");
                        }
                    }

                    if (op.Kind == WorkOrderOpKind.BottomFinish && rough != null)
                    {
                        double trueDepth = rough.Through ? thickness + 0.5d : rough.TotalDepth;
                        if (op.FloorStockToLeave >= trueDepth)
                            warnings.Add(opLabel + "floor stock to leave must be less than the roughing depth.");
                    }
                }
            }
            return warnings;
        }

        #region Diagram

        private void canvasDiagram_SizeChanged(object sender, SizeChangedEventArgs e) { DrawDiagram(); }

        // How far OUTSIDE its nominal outline a toolpath actually removes material. Pocket, Contour (on closed
        // geometry) and the finishing passes all run with the tool center inset by its radius, so they cut only
        // up to the nominal line - nothing beyond it. A chamfer's V-bit spreads outward by roughly its depth. A
        // countersink's target diameter can likewise extend past the hole's own nominal circle.
        private static double OutsideReachMm(WorkOrderToolpath tp)
        {
            double extra = 0d;
            foreach (var op in tp.Operations)
            {
                if (op.Kind == WorkOrderOpKind.Chamfer)
                    extra = Math.Max(extra, op.ChamferDepth);
                else if (op.Kind == WorkOrderOpKind.Countersink)
                    extra = Math.Max(extra, (op.CountersinkDiameter - tp.Diameter) / 2d);
            }
            return extra;
        }

        // Half-width of the swath a LINE toolpath cuts - the tool center rides the line itself, so it removes a
        // slot a full bit diameter wide. Invisible if only the nominal line is drawn.
        private static double LineHalfWidthMm(WorkOrderToolpath tp)
        {
            double half = 0d;
            foreach (var op in tp.Operations)
                half = Math.Max(half, op.BitDiameter / 2d + (op.Kind == WorkOrderOpKind.Chamfer ? op.ChamferDepth : 0d));
            return half;
        }

        // How wide the engraved stroke this toolpath cuts actually is - the width the V-bit's point opens up
        // at depth, which is what the operator sets and what the envelope should show. Falls back to the
        // bit's own width for a text toolpath carrying some other operation, which is not a combination the
        // editor offers but is cheaper to answer than to assume away.
        private static double EngraveWidthMm(WorkOrderToolpath tp)
        {
            double w = 0d;
            foreach (var op in tp.Operations)
                if (op.Kind == WorkOrderOpKind.Engrave)
                    w = Math.Max(w, op.EngraveWidth);
            return w > 0d ? w : LineHalfWidthMm(tp) * 2d;
        }

        // The widest hole any Drill/Bore on this toolpath makes. These carry their own diameter, so a hole can
        // reach FURTHER out than the circle it's centered on - exactly the case worth seeing before it eats
        // into a neighbour.
        private static double HoleRadiusMm(WorkOrderToolpath tp)
        {
            double r = 0d;
            foreach (var op in tp.Operations)
                if (op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore)
                    r = Math.Max(r, op.HoleDiameter / 2d);
            return r;
        }

        // Every toolpath at once - the selected one solid steel blue, the rest dimmed - so the whole work order
        // is visible in place on the stock rather than one shape at a time. Each also gets a translucent
        // "material removed" envelope drawn behind it, because that, not the nominal outline, is what actually
        // collides with a neighbouring toolpath.
        private void DrawDiagram()
        {
            if (canvasDiagram == null || canvasDiagram.ActualWidth <= 0 || canvasDiagram.ActualHeight <= 0)
                return;

            stockTransform = DrawInto(canvasDiagram);
            UpdateStockBanner();
        }

        /// <summary>
        /// The whole stock diagram, drawn into <paramref name="target"/> at whatever size that canvas
        /// is. This is the only place the diagram is built; DrawDiagram() points it at the on-screen
        /// canvas and "Save Drawing" points it at an off-screen one sized to the paper.
        ///
        /// Each toolpath draws in its own colour (WorkOrderPalette) and is named by a lettered balloon
        /// rather than by its name written across the drawing - the name of a toolpath is many times
        /// wider than the toolpath, so printing it was what made a busy stock unreadable. The letter is
        /// the identifier and the colour only reinforces it, which is what keeps the saved drawing
        /// working in mono; the table on the sheet, and the tree on screen, carry the names.
        /// </summary>
        private OddJobsStockCanvas.Transform DrawInto(Canvas target)
        {
            drawTarget = target;
            balloons.Clear();

            // No stock size in the file: say so and draw NOTHING. Falling back to Setup's size would put
            // the right toolpaths on the wrong blank, and a layout drawn against stock this work order was
            // never authored for is worse than no layout - it looks like an answer. The banner above
            // carries the one-click fix.
            if (!workOrder.HasStock)
            {
                target.Children.Clear();
                drawTransform = new OddJobsStockCanvas.Transform { Scale = 1d, OriginX = 0d, OriginY = 0d };
                AddNoStockNotice(target);
                return drawTransform;
            }

            drawTransform = OddJobsStockCanvas.DrawStock(target, workOrder.StockWidth, workOrder.StockDepth);
            double scale = drawTransform.Scale;

            // Envelopes first, so the nominal outlines stay legible on top of them.
            // A held-back toolpath gets no envelope: the envelope shows where material WILL be removed, and
            // this one isn't going to remove any. An Indirect toolpath's "own operations" are borrowed from
            // its source (see WillRun, and WorkOrderRules.Expand) - it has none of its own to check here.
            for (int i = 0; i < workOrder.Toolpaths.Count; i++)
            {
                var tp = workOrder.Toolpaths[i];
                if (!WillRun(tp))
                    continue;
                foreach (var placement in WorkOrderRules.Expand(workOrder, tp))
                    foreach (var pos in placement.Geometry.PatternPositions(placement.X, placement.Y))
                        DrawEnvelope(placement.Geometry, pos[0], pos[1], scale, i);
            }

            for (int index = 0; index < workOrder.Toolpaths.Count; index++)
            {
                var tp = workOrder.Toolpaths[index];
                bool isSelected = ReferenceEquals(tp, selectedToolpath);
                // Still drawn when held back - it's geometry you authored and want to see for fit against the
                // rest - but greyed, so what's actually going to be cut reads at a glance. Grey wins over the
                // feature's own colour here: "this one isn't cutting" matters more than which one it is.
                bool willRun = WillRun(tp);
                var stroke = willRun ? WorkOrderPalette.BrushFor(index) : WorkOrderPalette.BrushFor(WorkOrderPalette.HeldBack);
                double thickness = isSelected ? 2d : 1d;

                // Everything this toolpath puts on the stock: one placement normally, one per member when it
                // is an Indirect pointing at a GROUP. Each placement brings its own geometry AND its own
                // pattern, which is why the pattern is laid out per placement rather than once out here.
                // Same WorkOrderRules.Expand the compiler builds its shadow toolpaths from, so the drawing
                // and the cut cannot describe different arrangements.
                var placements = WorkOrderRules.Expand(workOrder, tp);
                int drawn = 0;

                foreach (var placement in placements)
                {
                    var geom = placement.Geometry;

                    // Every pattern instance is drawn: a pattern that only showed its anchor would hide
                    // exactly the overlap this drawing exists to catch.
                    foreach (var pos in geom.PatternPositions(placement.X, placement.Y))
                    {
                        var center = OddJobsStockCanvas.ToPixel(drawTransform, pos[0], pos[1]);
                        switch (geom.Geometry)
                        {
                            case WorkOrderGeometryKind.Line:
                                AddLine(center, geom, scale, stroke, thickness);
                                break;
                            case WorkOrderGeometryKind.Circle:
                                AddEllipse(center, geom.Diameter / 2d * scale, geom.Diameter / 2d * scale, stroke, thickness, null);
                                break;
                            case WorkOrderGeometryKind.Oval:
                                AddEllipse(center, geom.Width / 2d * scale, geom.Depth / 2d * scale, stroke, thickness, null);
                                break;
                            case WorkOrderGeometryKind.Square:
                                AddRect(center, geom.Size / 2d * scale, geom.Size / 2d * scale, stroke, thickness, null);
                                break;
                            case WorkOrderGeometryKind.Text:
                                AddTextStrokes(center, geom, scale, stroke, thickness);
                                break;
                            case WorkOrderGeometryKind.Svg:
                                AddSvgOutline(center, geom, scale, stroke);
                                break;
                            default:
                                AddRect(center, geom.Width / 2d * scale, geom.Depth / 2d * scale, stroke, thickness, null);
                                break;
                        }

                        // Shape text draws INSIDE the outline just drawn, at the fit's own size and
                        // placement - so the preview answers "does it fit, and where" as you type.
                        if (geom.HasText && geom.Geometry != WorkOrderGeometryKind.Text && WorkOrderRules.SupportsShapeText(geom.Geometry))
                            AddTextStrokes(center, geom, scale, stroke, 1d);

                        var dot = new Ellipse { Width = 5, Height = 5, Fill = stroke };
                        Canvas.SetLeft(dot, center.X - 2.5); Canvas.SetTop(dot, center.Y - 2.5);
                        drawTarget.Children.Add(dot);
                        drawn++;
                    }
                }

                // Balloon once, on the anchor instance - one per instance would just be clutter, and for a
                // group that would be one per member on top of that. The count rides in the balloon's own
                // suffix rather than a second piece of text.
                // Sized by the FIRST placement's shape - for a group that is its anchor member, which is
                // what the resolved center refers to as well, so the balloon points at the thing it names.
                var at = WorkOrderRules.ResolvedCenter(workOrder, tp);
                var anchor = OddJobsStockCanvas.ToPixel(drawTransform, at[0], at[1]);
                var shape = placements.Count > 0 ? placements[0].Geometry : tp;
                // The balloon keeps the feature's OWN colour even when the shape is greyed out. The balloon
                // is the key that ties the drawing to the table, and a key that changes colour depending on
                // whether a toolpath happens to be held back is not a key - the saved sheet had feature G
                // grey on the stock and magenta in the table, naming the same thing twice differently.
                // "Won't cut" is carried by the greyed geometry and the dimmed table row instead.
                AddBalloon(anchor, WorkOrderPalette.Id(index) + (drawn > 1 ? "×" + drawn : string.Empty),
                           WorkOrderPalette.BrushFor(index), ShapeHalfWidthPx(shape, scale),
                           ShapeHalfHeightPx(shape, scale), isSelected);
            }

            return drawTransform;
        }

        // Centred on the empty diagram in place of a stock rectangle nobody has told us the size of.
        private static void AddNoStockNotice(Canvas target)
        {
            var text = new TextBlock
            {
                Text = "No stock size recorded for this work order.\nSet it above to see the layout.",
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x00)),
                Width = Math.Max(80d, target.ActualWidth > 0d ? target.ActualWidth - 24d : target.Width - 24d)
            };
            text.Measure(new Size(text.Width, double.PositiveInfinity));
            double h = target.ActualHeight > 0d ? target.ActualHeight : target.Height;
            Canvas.SetLeft(text, 12d);
            Canvas.SetTop(text, Math.Max(8d, (h - text.DesiredSize.Height) / 2d));
            target.Children.Add(text);
        }

        // Where the balloons already placed in this pass ended up, so the next one can avoid them. Bounds
        // only - a balloon is a circle, but overlapping bounding boxes are close enough to overlapping
        // balloons that treating them as the same thing costs nothing and keeps this readable.
        private readonly List<Rect> balloons = new List<Rect>();

        private const double BalloonR = 9d, BalloonGap = 5d;

        /// <summary>
        /// A filled circle carrying the feature's letter, placed clear of the shape it names and clear of
        /// every balloon already placed, with a leader line back to the shape's edge.
        ///
        /// This replaced writing the toolpath's NAME on the drawing. A name is many times wider than the
        /// feature it names, so on a busy stock the names collided with each other and buried the geometry
        /// - the drawing became unreadable exactly when there was most to read. A balloon is a fixed
        /// ~18 px wide whatever the toolpath is called, which is what makes the collision avoidance below
        /// able to succeed at all.
        /// </summary>
        private void AddBalloon(Point anchor, string id, Brush color, double halfW, double halfH, bool isSelected)
        {
            // Preferred first (directly above, where the label used to sit), then around the shape, then
            // further out. Screen Y grows downward, so "above" is the negative direction.
            double rx = Math.Max(halfW, 2d) + BalloonR + BalloonGap;
            double ry = Math.Max(halfH, 2d) + BalloonR + BalloonGap;
            double[] angles = { -90d, 0d, 180d, 90d, -45d, -135d, 45d, 135d };
            double[] rings = { 1d, 1.5d, 2.1d, 3d };

            Point at = new Point(anchor.X, anchor.Y - ry);
            bool found = false;
            foreach (double ring in rings)
            {
                foreach (double a in angles)
                {
                    double rad = a * Math.PI / 180d;
                    var p = new Point(anchor.X + Math.Cos(rad) * rx * ring, anchor.Y + Math.Sin(rad) * ry * ring);
                    var box = new Rect(p.X - BalloonR, p.Y - BalloonR, BalloonR * 2d, BalloonR * 2d);
                    if (balloons.Any(b => b.IntersectsWith(box)))
                        continue;
                    at = p;
                    found = true;
                    break;
                }
                if (found)
                    break;
            }
            // Every ring was full: take the preferred spot and overlap. A balloon drawn on top of another
            // is still readable; silently dropping one would lose a feature off the drawing entirely.
            balloons.Add(new Rect(at.X - BalloonR, at.Y - BalloonR, BalloonR * 2d, BalloonR * 2d));

            // Leader first, so the balloon's own fill covers the end of it. Drawn from the shape's edge in
            // the balloon's direction rather than from its centre, so a leader onto a large pocket doesn't
            // strike a line across the whole thing.
            double dx = at.X - anchor.X, dy = at.Y - anchor.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0.5d)
            {
                double ux = dx / len, uy = dy / len;
                // Radius of the shape's bounding ellipse in this direction.
                double ex = Math.Max(halfW, 1d), ey = Math.Max(halfH, 1d);
                double edge = 1d / Math.Sqrt(ux * ux / (ex * ex) + uy * uy / (ey * ey));
                if (edge < len)
                    drawTarget.Children.Add(new Line
                    {
                        X1 = anchor.X + ux * edge, Y1 = anchor.Y + uy * edge,
                        X2 = at.X, Y2 = at.Y,
                        Stroke = color, StrokeThickness = 1d
                    });
            }

            var text = new TextBlock
            {
                Text = id,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White   // every palette colour is dark enough to carry white
            };
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Widened for a two-character id (AA, AB...) or an instance count, so the letter never spills
            // out of its own balloon.
            double half = Math.Max(BalloonR, text.DesiredSize.Width / 2d + 4d);
            var disc = new Ellipse
            {
                Width = half * 2d, Height = BalloonR * 2d,
                Fill = color,
                Stroke = Brushes.White,
                StrokeThickness = isSelected ? 2.5d : 1.25d
            };
            Canvas.SetLeft(disc, at.X - half); Canvas.SetTop(disc, at.Y - BalloonR);
            drawTarget.Children.Add(disc);

            Canvas.SetLeft(text, at.X - text.DesiredSize.Width / 2d);
            Canvas.SetTop(text, at.Y - text.DesiredSize.Height / 2d);
            drawTarget.Children.Add(text);
        }

        // ---- The stock this work order expects --------------------------------------------------------
        //
        // A work order is a recipe for a known blank, so the size lives in the file and the diagram is
        // drawn against it. Setup's Width/Height/Thickness are a different thing entirely: the operator's
        // own numbers for the material currently clamped to the table, feeding probing and the keep-out
        // area. So nothing here writes Setup without a click - StartJobView's own CheckStockAgainstProgram
        // records that silently applying a loaded job's stock to those fields was tried and confirmed
        // unwanted, and this is the same situation with a different file format.

        private const double StockMatchToleranceMm = 0.05d;

        private static bool SameStock(double a, double b) { return Math.Abs(a - b) < StockMatchToleranceMm; }

        private static string StockText(double w, double d, double t)
        {
            return t > 0d
                 ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} × {1:0.#} × {2:0.#} mm", w, d, t)
                 : string.Format(CultureInfo.InvariantCulture, "{0:0.#} × {1:0.#} mm", w, d);
        }

        private void UpdateStockBanner()
        {
            if (pnlStockBanner == null)
                return;

            var s = StartJobConfig.Section;
            double sw = s != null ? s.Width : 0d, sd = s != null ? s.Height : 0d, st = s != null ? s.Thickness : 0d;
            bool setupHasStock = sw > 0d && sd > 0d;

            if (!workOrder.HasStock)
            {
                txtStockBanner.Text = setupHasStock
                    ? "This work order has no stock size recorded, so its layout can't be drawn. Setup currently has "
                      + StockText(sw, sd, st) + " - take that as the blank this work order expects, or set the size on Setup first."
                    : "This work order has no stock size recorded, and neither has Setup. Set the stock size on the Setup tab, then take it from here.";
                btnAdoptSetupStock.IsEnabled = setupHasStock;
                btnApplyStockToSetup.Visibility = Visibility.Collapsed;
                btnAdoptSetupStock.Visibility = Visibility.Visible;
                pnlStockBanner.Visibility = Visibility.Visible;
                return;
            }

            // Recorded and Setup agrees: nothing to say. The banner is for a discrepancy, not a status line.
            if (setupHasStock && SameStock(sw, workOrder.StockWidth) && SameStock(sd, workOrder.StockDepth)
                && (workOrder.StockThickness <= 0d || SameStock(st, workOrder.StockThickness)))
            {
                pnlStockBanner.Visibility = Visibility.Collapsed;
                return;
            }

            txtStockBanner.Text = "This work order was authored for "
                + StockText(workOrder.StockWidth, workOrder.StockDepth, workOrder.StockThickness)
                + " - the drawing shows that blank. Setup has "
                + (setupHasStock ? StockText(sw, sd, st) : "no stock size")
                + ", which is what probing and the keep-out area will use.";
            btnApplyStockToSetup.Visibility = Visibility.Visible;
            btnAdoptSetupStock.Visibility = setupHasStock ? Visibility.Visible : Visibility.Collapsed;
            btnAdoptSetupStock.IsEnabled = setupHasStock;
            pnlStockBanner.Visibility = Visibility.Visible;
        }

        // File -> Setup. Setup reads these back on its next activation (StartJobView.Activate), the same
        // way it already re-reads Material, because its own LoadInputs runs once per session and SaveInputs
        // rebuilds the section wholesale from its controls on the way out.
        private void btnApplyStockToSetup_Click(object sender, RoutedEventArgs e)
        {
            var s = StartJobConfig.Section;
            if (s == null || !workOrder.HasStock)
                return;

            s.Width = workOrder.StockWidth;
            s.Height = workOrder.StockDepth;
            if (workOrder.StockThickness > 0d)
                s.Thickness = workOrder.StockThickness;
            AppConfig.Settings.Save();

            UpdateStockBanner();
            DrawDiagram();
            if (model != null)
                model.Message = "Setup stock size set from this work order: " + StockText(s.Width, s.Height, s.Thickness);
        }

        // Setup -> file. Also the way to CHANGE a work order's stock: set it on Setup, then take it here.
        private void btnAdoptSetupStock_Click(object sender, RoutedEventArgs e)
        {
            var s = StartJobConfig.Section;
            if (s == null || s.Width <= 0d || s.Height <= 0d)
                return;

            workOrder.StockWidth = s.Width;
            workOrder.StockDepth = s.Height;
            workOrder.StockThickness = s.Thickness;
            OnWorkOrderChanged();   // marks dirty, persists, and redraws against the size just adopted
            UpdateStockBanner();
        }

        // ---- "Save Drawing": the diagram as a dimensioned PDF sheet -----------------------------------

        private void DiagramMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Nothing to draw with no toolpaths. Resolved off the menu instance rather than the generated
            // field for the same reason WorkOrderNameMenu_Opened does - a ContextMenu lives outside the
            // visual tree and its name scope is the least reliable thing about it.
            // Also needs a stock size: the sheet dimensions the blank, and a drawing that dimensioned a
            // placeholder would be worse than no drawing at all.
            foreach (var item in ((ContextMenu)sender).Items)
                if (item is MenuItem mi && mi.Name == "miSaveDrawing")
                    mi.IsEnabled = workOrder != null && workOrder.Toolpaths.Count > 0 && workOrder.HasStock;
        }

        private void MenuSaveDrawing_Click(object sender, RoutedEventArgs e)
        {
            if (workOrder == null || workOrder.Toolpaths.Count == 0 || !workOrder.HasStock)
                return;

            string name = currentFilePath != null
                        ? System.IO.Path.GetFileNameWithoutExtension(currentFilePath)
                        : "Untitled work order";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Drawing",
                Filter = "PDF drawing (*.pdf)|*.pdf",
                AddExtension = true,
                DefaultExt = ".pdf",
                InitialDirectory = currentFilePath != null ? System.IO.Path.GetDirectoryName(currentFilePath) : WorkOrdersFolder(),
                FileName = name + ".pdf",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true)
                return;

            // The sheet is a drawing of the work order, not of the current editing session: the selection
            // highlight is UI state and would print as one arbitrarily bolder feature. Restored straight
            // after - DrawInto is synchronous, so nothing else can observe the gap.
            var wasSelected = selectedToolpath;
            selectedToolpath = null;
            try
            {
                WorkOrderDrawing.Save(dlg.FileName, workOrder, name, DrawInto);
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not write the drawing:\n\n" + ex.Message, "Save Drawing",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                selectedToolpath = wasSelected;
                DrawDiagram();   // the off-screen pass left drawTarget/drawTransform pointing at the sheet
            }

            if (AppDialogs.Show("Drawing saved to\n\n" + dlg.FileName + "\n\nOpen it now?", "Save Drawing",
                                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(dlg.FileName); }
                catch (Exception ex)
                {
                    AppDialogs.Show("The drawing was saved, but Windows would not open it:\n\n" + ex.Message,
                                    "Save Drawing", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        // Whether this toolpath's cut will actually show up in Generate.
        //
        // Mirrors WorkOrderCompiler.ResolveIndirect exactly, and the two halves differ on purpose:
        //
        //   ordinary  its own tick, and at least one operation still ticked under it.
        //   Indirect  its own tick, and a source that DEFINES something - what the source has ticked is
        //             not consulted, because the copy doesn't inherit those ticks either.
        //
        // Reading the source's ticks here would grey out a copy that is about to cut, which is what this
        // did while the compiler shared the source's operation objects. A group counts if any member
        // contributes; Expand is what knows what a group expands to.
        private bool WillRun(WorkOrderToolpath tp)
        {
            if (!tp.Enabled)
                return false;

            return tp.IsIndirect
                ? WorkOrderRules.Expand(workOrder, tp).Any(p => p.Geometry.Operations.Count > 0)
                : tp.Operations.Any(o => o.Enabled);
        }

        // Vertical half-extent in pixels, so a label can sit clear of the shape it names.
        private static double ShapeHalfHeightPx(WorkOrderToolpath tp, double scale)
        {
            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Line:
                    return Math.Abs(Math.Sin(tp.Angle * Math.PI / 180d)) * tp.Length / 2d * scale + LineHalfWidthMm(tp) * scale;
                case WorkOrderGeometryKind.Circle:
                    return Math.Max(tp.Diameter / 2d, HoleRadiusMm(tp)) * scale;
                case WorkOrderGeometryKind.Square:
                    return tp.Size / 2d * scale;
                default:
                    return tp.Depth / 2d * scale;
            }
        }

        // Horizontal half-extent in pixels - the sibling of ShapeHalfHeightPx, so a balloon placed to the
        // side of a shape clears it by as much as one placed above does.
        private static double ShapeHalfWidthPx(WorkOrderToolpath tp, double scale)
        {
            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Line:
                    return Math.Abs(Math.Cos(tp.Angle * Math.PI / 180d)) * tp.Length / 2d * scale + LineHalfWidthMm(tp) * scale;
                case WorkOrderGeometryKind.Circle:
                    return Math.Max(tp.Diameter / 2d, HoleRadiusMm(tp)) * scale;
                case WorkOrderGeometryKind.Square:
                    return tp.Size / 2d * scale;
                default:
                    return tp.Width / 2d * scale;
            }
        }

        // Whether every operation on this toolpath cuts only its PERIMETER, leaving the interior untouched.
        //
        // It matters because the envelope is meant to be the material actually removed, and a contour
        // removes a band a bit wide along the outline - not the area inside it. Drawing it filled said a
        // through-contour around a part had cleared the whole part, which on a work order with an outline
        // toolpath washed the entire stock in one colour and buried everything else on the drawing.
        //
        // Enabled is deliberately not consulted, matching OutsideReachMm/LineHalfWidthMm/HoleRadiusMm - the
        // reach helpers this sits beside all describe what the toolpath is, not what is ticked today.
        private static bool IsPerimeterOnly(WorkOrderToolpath tp)
        {
            if (tp.Operations.Count == 0)
                return false;

            foreach (var op in tp.Operations)
                switch (op.Kind)
                {
                    case WorkOrderOpKind.Contour:
                    case WorkOrderOpKind.SideFinish:
                    case WorkOrderOpKind.Chamfer:
                        break;
                    default:
                        // Pocket/Surface clear the area; Drill/Bore/Countersink make a hole; BottomFinish
                        // faces a floor; Engrave cuts strokes across the interior. None is a band.
                        return false;
                }
            return true;
        }

        // How far INSIDE its nominal outline a perimeter pass reaches - the band's inner edge.
        //
        // Straight off WorkOrderCompiler: BuildContour runs Outline() with the tool center inset by
        // BitDiameter/2 + WallLeave, so a contour's cut spans from the nominal line inward by one full bit
        // diameter (plus whatever a side-finish pass was told to leave for itself). A side finish then cuts
        // that leave away with its own bit. The envelope wants the union, so this takes the deepest.
        private static double InsideReachMm(WorkOrderToolpath tp)
        {
            double reach = 0d;
            double wallLeave = tp.Operations
                                 .Where(o => o.Kind == WorkOrderOpKind.SideFinish)
                                 .Select(o => o.WallStockToLeave)
                                 .DefaultIfEmpty(0d)
                                 .Max();

            foreach (var op in tp.Operations)
            {
                if (op.Kind == WorkOrderOpKind.Contour)
                    reach = Math.Max(reach, op.BitDiameter + wallLeave);
                else if (op.Kind == WorkOrderOpKind.SideFinish)
                    reach = Math.Max(reach, op.BitDiameter);
            }
            return reach;
        }

        // The footprint of material this toolpath removes at one instance position, in a pale wash of the
        // toolpath's OWN colour so a busy stock still says which envelope belongs to which feature.
        private void DrawEnvelope(WorkOrderToolpath tp, double atX, double atY, double scale, int index)
        {
            if (tp.Operations.Count == 0)
                return;

            var center = OddJobsStockCanvas.ToPixel(drawTransform, atX, atY);
            // Pale enough that a large envelope tints the drawing rather than burying it. The edges are
            // darker so the footprint still has a readable boundary.
            var fill = WorkOrderPalette.TintFor(index, 0.12d);
            var edge = WorkOrderPalette.TintFor(index, 0.45d);
            double outside = OutsideReachMm(tp);

            // A line has no interior to clear or spare: the bit rides the line and sweeps a slot a full
            // diameter wide, so its envelope was always a band and stays one.
            if (tp.Geometry == WorkOrderGeometryKind.Line)
            {
                AddLine(center, tp, scale, fill, Math.Max(1d, LineHalfWidthMm(tp) * 2d * scale));
                return;
            }

            // Text and artwork have no rectangle to grow: what they remove is the glyphs themselves. Both
            // fell through to the Rect default below, which drew a 40 x 25 mm box - the toolpath's UNUSED
            // Width/Depth defaults, nothing to do with the cut. That is the same fault DescribeGeometry
            // carried and had fixed ("rect 40x25" in the tree); the envelope was missed in that sweep.
            //
            // It matters more here than it did there, because it UNDERSTATES: a 150 mm logo claimed a 40 mm
            // footprint, so the one thing the envelope exists to show - that this toolpath is about to run
            // into its neighbour - read as clear.
            //
            // Drawn by the same helpers the outline pass uses, at the width the cut actually is: stroke text
            // as round-capped strokes a full engraving width wide, carved text and artwork as their filled
            // outlines. Geometry that cannot be resolved (a fit that fails, an SVG that will not import)
            // draws NOTHING, which is what those helpers already do and is the honest answer - a missing
            // envelope says "unknown", a rectangle says "this much", and only one of those is true.
            if (tp.Geometry == WorkOrderGeometryKind.Text)
            {
                AddTextStrokes(center, tp, scale, fill, Math.Max(1d, EngraveWidthMm(tp) * scale));
                return;
            }
            if (tp.Geometry == WorkOrderGeometryKind.Svg)
            {
                AddSvgOutline(center, tp, scale, fill);
                return;
            }

            double inside = IsPerimeterOnly(tp) ? InsideReachMm(tp) : 0d;

            // Once the band is as deep as the shape's narrowest half-span there is no interior left for it
            // to spare - a 8 mm circle contoured with a 6.35 mm bit really does remove the lot - so it goes
            // back to being drawn filled rather than as a ring turned inside out.
            if (inside > 0d && inside < tp.MinSpan / 2d)
            {
                // Drawn as a thick STROKE along the band's centreline rather than as a filled ring. Same
                // picture, and it survives the trip into the saved drawing: WorkOrderDrawing's canvas
                // walker turns Rectangle/Ellipse straight into PDF operators, where a true ring would need
                // an even-odd path it only builds for line-segment outlines.
                AddOffsetOutline(center, tp, scale, (outside - inside) / 2d, fill, (outside + inside) * scale, null);
                AddOffsetOutline(center, tp, scale, outside, edge, 1d, null);
                AddOffsetOutline(center, tp, scale, -inside, edge, 1d, null);
                return;
            }

            // A hole can reach further out than the circle it is centered on, and that is exactly the case
            // worth seeing before it eats into a neighbour - so the growth is whichever is larger.
            double grow = tp.Geometry == WorkOrderGeometryKind.Circle
                        ? Math.Max(outside, HoleRadiusMm(tp) - tp.Diameter / 2d)
                        : outside;
            AddOffsetOutline(center, tp, scale, grow, edge, 1d, fill);
        }

        // This toolpath's outline at one instance, grown outward by offsetMm - negative shrinks it. The one
        // place the envelope's shape-per-geometry switch lives, so the band's three passes (body, outer
        // edge, inner edge) and the filled case cannot drift into describing different shapes.
        private void AddOffsetOutline(Point center, WorkOrderToolpath tp, double scale, double offsetMm,
                                      Brush stroke, double thickness, Brush fill)
        {
            double o = offsetMm * scale;
            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Circle:
                {
                    double r = Math.Max(0.5d, tp.Diameter / 2d * scale + o);
                    AddEllipse(center, r, r, stroke, thickness, fill);
                    break;
                }
                case WorkOrderGeometryKind.Oval:
                    AddEllipse(center, Math.Max(0.5d, tp.Width / 2d * scale + o),
                                       Math.Max(0.5d, tp.Depth / 2d * scale + o), stroke, thickness, fill);
                    break;
                case WorkOrderGeometryKind.Square:
                    AddRect(center, Math.Max(0.5d, tp.Size / 2d * scale + o),
                                    Math.Max(0.5d, tp.Size / 2d * scale + o), stroke, thickness, fill);
                    break;
                default:
                    AddRect(center, Math.Max(0.5d, tp.Width / 2d * scale + o),
                                    Math.Max(0.5d, tp.Depth / 2d * scale + o), stroke, thickness, fill);
                    break;
            }
        }

        private void AddLine(Point center, WorkOrderToolpath tp, double scale, Brush stroke, double thickness)
        {
            double a = tp.Angle * Math.PI / 180d;
            double dx = Math.Cos(a) * tp.Length / 2d * scale, dy = Math.Sin(a) * tp.Length / 2d * scale;
            drawTarget.Children.Add(new Line
            {
                X1 = center.X - dx, Y1 = center.Y + dy,   // screen Y grows downward
                X2 = center.X + dx, Y2 = center.Y - dy,
                Stroke = stroke, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        // Draw the engraving exactly as it will be cut - the real glyph strokes, not a bounding box. The
        // canvas' default case draws a rectangle, which for text would have previewed a box where the words
        // are: enough to check it fits the stock, useless for checking it reads right or clears a feature.
        //
        // Mirrors BuildEngrave's own transform so preview and g-code cannot disagree: centre the unrotated
        // text on the anchor, then rotate about it. Screen Y grows DOWNWARD, hence the negated Y - the same
        // flip AddLine already makes.
        private void AddTextStrokes(Point center, WorkOrderToolpath tp, double scale, Brush stroke, double thickness)
        {
            // Same resolution BuildEngrave does: the Text KIND draws at its own size/angle/anchor
            // convention, shape text at whatever the fit resolver says - the preview shows the CUT,
            // not the fields. A fit that fails draws nothing; Validate is already saying why.
            double capHeight = tp.CapHeight, angleDeg = tp.Angle, dx = 0d, dy = 0d;
            bool shapeText = tp.HasText && tp.Geometry != WorkOrderGeometryKind.Text
                          && WorkOrderRules.SupportsShapeText(tp.Geometry);
            if (shapeText)
            {
                var fit = WorkOrderTextFit.Resolve(tp);
                if (!fit.Fits)
                    return;
                capHeight = fit.CapHeight; angleDeg = fit.Angle; dx = fit.OffsetX; dy = fit.OffsetY;
            }

            if (tp.IsCarved)
            {
                AddCarvedText(center, tp, scale, stroke, capHeight, angleDeg, dx, dy);
                return;
            }

            var strokes = CNC.Core.StrokeFont.Render(tp.Text, capHeight);
            if (strokes.Count == 0)
                return;

            var size = CNC.Core.StrokeFont.Measure(tp.Text, capHeight);
            double ox = -size.X / 2d + dx;
            double oy = shapeText ? -size.Y / 2d + dy : -size.Y / 2d + capHeight / 2d;
            double rad = angleDeg * Math.PI / 180d;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            foreach (var st in strokes)
            {
                var poly = new Polyline
                {
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                foreach (var pt in st)
                {
                    double lx = pt.X + ox, ly = pt.Y + oy;
                    poly.Points.Add(new Point(center.X + (lx * cos - ly * sin) * scale,
                                              center.Y - (lx * sin + ly * cos) * scale));
                }
                drawTarget.Children.Add(poly);
            }
        }

        // Carved text previews FILLED - the cut removes the whole glyph interior, and showing solid
        // letters is also what tells the operator at a glance they are in carve mode rather than stroke.
        // Mirrors BuildVCarve's transform (bounding-box centre on the anchor, then rotate about it) the
        // same way AddTextStrokes mirrors BuildEngrave's.
        // capHeight/angleDeg/dx/dy arrive resolved from AddTextStrokes, exactly as BuildVCarve's do
        // from BuildEngrave.
        private void AddCarvedText(Point center, WorkOrderToolpath tp, double scale, Brush brush,
                                   double capHeight, double angleDeg, double dx, double dy)
        {
            AddFilledOutline(center, TrueTypeOutlines.Render(tp.Text, tp.FontFamily, capHeight, tp.FontBold, tp.FontItalic),
                             scale, brush, angleDeg, dx, dy);
        }

        // Artwork preview. Same fill, same even-odd rule, same rotation as carved text - because it is
        // the same cut: SvgOutlines and TrueTypeOutlines produce the identical contour type, and this
        // draws whichever it is given. A file that cannot be imported draws NOTHING rather than a
        // partial logo; the editor's info line and Validate both say why.
        private void AddSvgOutline(Point center, WorkOrderToolpath tp, double scale, Brush brush)
        {
            var svg = SvgOutlines.Load(tp.SvgFile, tp.SvgWidth);
            if (svg.Error != null || !svg.IsComplete)
                return;
            // SvgOutlines puts the origin at the artwork's bottom-left; the shared drawer centres on the
            // outline's own bounds, so no extra offset is needed here.
            AddFilledOutline(center, svg.Contours, scale, brush, tp.Angle, 0d, 0d);
        }

        private void AddFilledOutline(Point center, List<OutlineContour> outline, double scale, Brush brush,
                                      double angleDeg, double dx, double dy)
        {
            if (outline == null || outline.Count == 0)
                return;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var c in outline)
                foreach (var p in c.Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }

            double ox = -(minX + maxX) / 2d + dx, oy = -(minY + maxY) / 2d + dy;
            double rad = angleDeg * Math.PI / 180d;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            // One geometry, even-odd fill: the counters (the hole in an O) punch out exactly as the
            // carve engine's own inside test treats them.
            var geo = new PathGeometry { FillRule = FillRule.EvenOdd };
            foreach (var c in outline)
            {
                var fig = new PathFigure { IsClosed = true, IsFilled = true };
                bool first = true;
                foreach (var p in c.Points)
                {
                    double lx = p.X + ox, ly = p.Y + oy;
                    var q = new Point(center.X + (lx * cos - ly * sin) * scale,
                                      center.Y - (lx * sin + ly * cos) * scale);   // screen Y grows downward
                    if (first) { fig.StartPoint = q; first = false; }
                    else fig.Segments.Add(new LineSegment(q, true));
                }
                geo.Figures.Add(fig);
            }
            drawTarget.Children.Add(new System.Windows.Shapes.Path { Data = geo, Fill = brush });
        }

        private void AddEllipse(Point center, double rx, double ry, Brush stroke, double thickness, Brush fill)
        {
            var el = new Ellipse { Width = rx * 2, Height = ry * 2, Stroke = stroke, StrokeThickness = thickness, Fill = fill };
            Canvas.SetLeft(el, center.X - rx); Canvas.SetTop(el, center.Y - ry);
            drawTarget.Children.Add(el);
        }

        private void AddRect(Point center, double hw, double hh, Brush stroke, double thickness, Brush fill)
        {
            var r = new Rectangle { Width = hw * 2, Height = hh * 2, Stroke = stroke, StrokeThickness = thickness, Fill = fill };
            Canvas.SetLeft(r, center.X - hw); Canvas.SetTop(r, center.Y - hh);
            drawTarget.Children.Add(r);
        }

        #endregion

        #region Methods required by ICNCView
        // Promoted from an Odd Jobs sub-tab to its own top-level tab (2026-07-31) - this needs BOTH interfaces:
        // IGrblConfigTab.Activate(bool) is the pre-existing internal lifecycle hook (kept below, unchanged);
        // ICNCView is what MainWindow's own top-level tab-switching (TabMode_SelectionChanged/getView) actually
        // looks for - a bare IGrblConfigTab-only tab (like the Trinamic tuner) never gets Activate called by
        // MainWindow at all, it just relies on being otherwise stateless. Overload resolution keeps both
        // Activate methods distinct (different parameter lists), so both interfaces coexist cleanly.

        public ViewType ViewType { get { return ViewType.WorkOrder; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }
        public void Activate(bool activate, ViewType chgMode) { Activate(activate); }
        public void CloseFile() { }
        public void Setup(UIViewModel model, AppConfig profile) { }

        // Entry points for the main menu's New/Load Work Order items (2026-07-31) - the tab's own toolbar
        // buttons (MenuNew_Click/btnLoad_Click below) stay as the in-context way to do the same thing; these
        // just let the operator jump straight into a new/existing work order from outside the tab, same as
        // Load File does for the Job tab. Neither handler reads its (sender, e) args, so null is safe here.
        public void New() { MenuNew_Click(this, null); }
        public void Load() { btnLoad_Click(this, null); }

        #endregion

        #region Methods required by IGrblConfigTab

        public GrblConfigType GrblConfigType { get { return GrblConfigType.WorkOrder; } }

        private bool isActiveTab = false;
        private ProgramView programView;

        private void EnsureProgramView()
        {
            // No EditTargetTab: this preview only ever shows while Generate is pressed FROM the Work Order
            // tab (ActiveGenerate is only wired up in Activate(true)), so an Edit-back-to-Work-Order button
            // would just switch to the tab it's already showing on - a no-op, removed 2026-08-01.
            if (programView == null)
                programView = new ProgramView { Title = "Work Order", Source = ProgramSource.Generated };
        }

        public void Activate(bool activate)
        {
            isActiveTab = activate;
            if (activate)
            {
                // Step 6: this tab's run-bar button is ALWAYS "Generate" - IsProgramGenerated is never
                // set, so the Generate->Run label flip (and the preview overlay it re-showed here) is
                // gone. Generate hands the program to the Job tab as the loaded job; the run half of
                // the bar belongs there now. ActiveRun stays null for the same reason - there is no
                // second engine to route a run through.
                MacroProcessor.SupportsGenerateMode = true;
                MacroProcessor.AllowRunModesWhenGenerated = true;
                MacroProcessor.ActiveGenerate = Generate;
                MacroProcessor.DiscardGenerated = DiscardProgram;
                MacroProcessor.IsProgramGenerated = false;
                // Setup may have changed the shared material while this tab was away.
                loadingFields = true;
                LoadMaterial();
                loadingFields = false;
                UpdateValidation();
                // ...and its stock size, which the banner compares against this work order's own. Redrawing
                // is what refreshes that comparison, and it also picks up the material just reloaded - the
                // stock colour is read at draw time, so without this both were stale until something else
                // happened to trigger a repaint.
                DrawDiagram();
            }
            else
            {
                // An untitled work order (no file association at all) is dirty by default, same as one that's
                // been edited since its last save - see AppConfig.Base.WorkOrderDirty's own comment. Guarded on
                // Toolpaths.Count so a genuinely empty work order (nothing entered, nothing to lose) never
                // prompts - only content actually worth saving does.
                bool needsSave = workOrder.Toolpaths.Count > 0
                    && (currentFilePath == null || AppConfig.Settings.Base.WorkOrderDirty);
                if (needsSave)
                {
                    // Says up front that Yes overwrites the existing file (when there is one) - so SaveToDisk's
                    // SaveFileDialog can turn off its own native "replace it?" prompt (OverwritePrompt=false)
                    // without silently skipping that warning, just moving it here instead of a second, redundant
                    // confirm right on top of this one.
                    string overwriteNote = currentFilePath != null
                        ? string.Format(" This overwrites {0}.", System.IO.Path.GetFileName(currentFilePath))
                        : string.Empty;

                    // Default (off): today's unconditional prompt, unchanged - the safety net for anyone who
                    // never touches the new Settings:App > Odd Jobs toggle. On: save without asking, unless
                    // PromptBeforeAutoSaveWorkOrder also wants a confirm first (same prompt, just opt-in).
                    bool doSave;
                    if (!AppConfig.Settings.Base.AutoSaveWorkOrderOnExit)
                        doSave = AppDialogs.Show("This work order has unsaved changes. Save before leaving?" + overwriteNote,
                            "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    else if (AppConfig.Settings.Base.PromptBeforeAutoSaveWorkOrder)
                        doSave = AppDialogs.Show("Save these work order changes now?" + overwriteNote,
                            "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    else
                        doSave = true;

                    if (doSave)
                        SaveToDisk();
                }

                MacroProcessor.ActiveRun = null;
                MacroProcessor.SupportsGenerateMode = false;
                MacroProcessor.AllowRunModesWhenGenerated = false;
                MacroProcessor.ActiveGenerate = null;
                MacroProcessor.DiscardGenerated = null;
                program = string.Empty;
                programView?.Disconnect();
            }

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        private void DiscardProgram()
        {
            program = string.Empty;
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = false;
            programView?.Disconnect();
        }

        #endregion

        // Switches to the Job tab FIRST, then streams - one click does both. The switch fires this tab's own
        // Activate(false) synchronously (WPF tab selection is not deferred), which is also where the existing
        // unsaved-changes prompt/auto-save-on-exit already lives (AppConfig.Base.AutoSaveWorkOrderOnExit) - so
        // "Run saves, moves to the Job tab, and starts" falls out of that existing mechanism with no new save
        // logic needed here. That same Activate(false) clears the `program` field, so it must be captured into
        // a local BEFORE switching, or the calls below would be handed an empty string and no-op.
        //
        // GCode.File.Push()/LoadText() make the generated program the actual loaded job - not a floating
        // overlay - so the Job tab's real docked list (ProgramPanel) and jobProgramView show it exactly like
        // any loaded file, since both are hard-wired to that same shared instance. Push remembers whatever
        // was loaded before (or that nothing was); WatchForRunEnd's Pop() restores it once this run reaches
        // its true terminal.
        //
        // (The two-press Run() that interpreted directives via the retired second engine was replaced by
        // Step 6 - Generate() above IS the whole handoff now, and the ordinary Cycle Start streams the
        // loaded program - GCode.File itself - through the unified engine, live per-line status landing
        // directly in the docked list. Step 7 then moved every OTHER macro caller onto the same path.)

        // Pop the loaded job back to whatever was there before, once this run reaches its TRUE terminal state
        // (Idle/NoFile - a clean finish or a Stop) - mirrors MainWindow.RestoreSourceOnEnd's own arm-on-
        // running/fire-on-terminal pattern (see its comment for why Idle/NoFile, not JobFinished). Left in
        // place through an Error/Halted (alarm) on purpose, same as every other Generate-first tool, so the
        // operator can still see what failed rather than having it silently vanish back to the previous file
        // mid-inspection.
        private void WatchForRunEnd()
        {
            WatchForRunEnd(model);
        }

        // One watcher, ever. Without this, Generate over a still-loaded Work Order program (the
        // boot-restored one, or a previous Generate that never ran) pushed AND armed a second time -
        // observed live 2026-08-08 15:04: "Push: depth now 2" + every watcher trace doubled. The
        // stacked pops happened to cancel out, but duplicate handlers and a growing snapshot stack
        // are exactly the state-drift class this tab has been burned by before.
        private static bool runEndWatcherArmed;

        // Static since the compile cache's boot-time auto-restore (2026-08-08): that path arms this
        // watcher before any WorkOrderView instance exists, and the body only ever needed the view
        // model + statics anyway. Behavior unchanged for the Generate path, which forwards above.
        private static void WatchForRunEnd(GrblViewModel model)
        {
            if (runEndWatcherArmed)
            {
                DebugLog.Write("workorder", "WatchForRunEnd: already armed - not arming a second watcher");
                return;
            }
            runEndWatcherArmed = true;
            bool started = false;
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = model.StreamingState;
                // Send ONLY - not SendMDI. Step 6 arms this watcher at GENERATE time, and Cycle Start
                // may be minutes away: a single jog in between reaches SendMDI then Idle, which under
                // the old SendMDI-arming would have popped the program before it ever ran.
                // AND only OUR program's Send: any other run in between - a Setup/macro run pushes the
                // work order aside and streams under its own name - must be ignored outright, not
                // latched. Without this gate, Setup-then-carve (2026-08-08, first boot-restored session)
                // latched started on SETUP's Send, hit Setup's terminal with the macro loaded, took the
                // not-ours disarm exit below, and the carve then finished with no watcher: no pop, no
                // switch back. The macro run's own watcher (MacroProcessor.Run) restores the work order
                // around it, so staying armed across a foreign run is exactly right.
                if (st == StreamingState.Send && model.FileName == "Work Order")
                    started = true;
                DebugLog.Write("workorder", string.Format("WatchForRunEnd: saw StreamingState={0}, started={1}{2}",
                    st, started, !started ? " - NOT ARMED, a terminal state here will be ignored" : string.Empty));
                if (!started || (st != StreamingState.Idle && st != StreamingState.NoFile))
                    return;
                model.PropertyChanged -= handler;
                runEndWatcherArmed = false;   // the one unsubscribe point - both exits below run through it
                // Self-disarm without popping when the loaded job is no longer OURS - the operator
                // generated, then loaded a different file instead of running: popping now would yank
                // THEIR file out from under THEIR run. (The pushed stack entry is left unconsumed in
                // that path - accepted, the alternative is worse.)
                if (model.FileName != "Work Order")
                {
                    DebugLog.Write("workorder", string.Format("WatchForRunEnd: terminal but loaded job is '{0}', not ours - disarming without pop", model.FileName));
                    return;
                }
                DebugLog.Write("workorder", "WatchForRunEnd: terminal - popping the borrowed program and switching back");
                GCode.File.Pop();
                MacroProcessor.SwitchToTab?.Invoke(ViewType.WorkOrder);
            };
            model.PropertyChanged += handler;

            // The state AT ARM TIME is the number that matters, and it is the one thing the handler above can
            // never tell us: this watcher only arms (started=true) by OBSERVING a Send/SendMDI transition, so
            // if the run already passed that point before we subscribed, no terminal state will ever pop the
            // program and it just sits there as "the job" forever. Reported 2026-08-06 - a work order finished,
            // parked at G30, and the program stayed loaded. That run had the UI roughly 2.5 minutes behind the
            // wire (the console reached the final Ln:36623 at 17:29:51; the machine got there at 17:27:17),
            // which is exactly the condition that makes arriving late plausible.
            // So record where we came in. "armed while already Send" or "armed while already Idle" identifies
            // the fault immediately; without it the two are indistinguishable after the fact.
            DebugLog.Write("workorder", string.Format("WatchForRunEnd: armed with StreamingState={0}", model.StreamingState));
        }

        // ---- Compiled-program cache (user request 2026-08-08: a script-font engraving compiles for
        // ~4 MINUTES, and since Step 6/7 the program pops off the Job tab after every run, so repeat
        // runs and restarts each paid that again). Generate is MEMOIZED: a fingerprint of everything
        // that shapes the output - the work order content plus the ambient state WorkOrderCompiler
        // reads (TLO baseline baked into #<_tlo_ref>, spindle capability, soft-limit travel/pulloff/
        // homing direction) plus the app version (the compiler evolves) - keys both an in-memory copy
        // and the stamped Generated\work_order.macro file (which doubles as the debugging copy, whose
        // refresh Step 6 had silently dropped). Any content or machine change misses and recompiles;
        // a fingerprint that CANNOT be computed (settings not loaded) fails closed to a recompile.
        // Known accepted blind spot: the hash covers the work order, not the font FILES - a system TTF
        // changing on disk would not invalidate (vanishingly rare for installed fonts).
        // Static: one Work Order tab, and the memo must survive view recreation.
        private static string cachedFp, cachedProgram, cachedStats;

        private const string CacheStampPrefix = "(WOCACHE ";

        // Everything the compiled text depends on, hashed. Null = can't know (controller settings not
        // loaded yet) - callers must treat that as a miss, never as a match.
        internal static string ComputeFingerprint(WorkOrder wo)
        {
            if (wo == null)
                return null;
            // The compiler reads these live (see WorkOrderCompiler: MaxTravel/pulloff/homing for the
            // envelope math, TloRefBaseline for #<_tlo_ref>, SpindleDirectionCapability) - if the
            // settings aren't in yet the output can't be predicted, so no fingerprint.
            if (string.IsNullOrEmpty(GrblSettings.GetString(GrblSetting.MaxTravelBase)))
                return null;

            var sb = new StringBuilder();
            using (var sw = new System.IO.StringWriter(sb))
                new System.Xml.Serialization.XmlSerializer(typeof(WorkOrder)).Serialize(sw, wo);
            sb.Append("|v=").Append(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            // ...and the BUILD, not just the version. Every dev build carries the same version number, so a
            // change to the compiler did not move this fingerprint: Generate matched the stamp on the
            // previous build's work_order.macro and handed it straight back. A fix would appear to do
            // nothing, and - worse, on 2026-08-18 - a line that had been REVERTED came back, because the
            // file predating the revert still matched.
            //
            // ModuleVersionId is a GUID the compiler stamps into an assembly on every compile. It is what
            // makes the binary's bytes differ build to build, so hashing the exe would be measuring this
            // indirectly, at the cost of reading and digesting a file on a path that runs on every input
            // change. Taken directly instead, and from the two assemblies that actually SHAPE the output:
            // CNC.Controls carries WorkOrderCompiler and WorkOrderModel, CNC Core carries VCarve and
            // MacroRunner. ioSender.exe carries none of them.
            //
            // Within one run of a released build these never change, so the cache still does its job - it
            // only ever invalidates across a rebuild, which is exactly when it must.
            sb.Append("|mvc=").Append(System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N"));
            sb.Append("|mvk=").Append(typeof(CNC.Core.VCarve).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
            // The Setup tab's stock feeds the compiler - Thickness has always driven TrueDepth, and the
            // stock size is now emitted as the program's own (STOCK ...) declaration - but none of it was
            // in the fingerprint, so editing stock in Setup silently reused the previous compile. Found
            // 2026-08-10 the hard way: a cached program with no STOCK line kept being restored after the
            // compiler had started emitting one, which read as "the fix did nothing".
            var stockSec = StartJobConfig.Section;
            sb.Append("|stk=").Append(stockSec == null ? "-" :
                stockSec.Width.ToInvariantString() + "x" + stockSec.Height.ToInvariantString() + "x" + stockSec.Thickness.ToInvariantString());
            sb.Append("|tlo=").Append(AppConfig.Settings.Base.TloRefBaseline.ToInvariantString());
            sb.Append("|spin=").Append(AppConfig.Settings.Base.SpindleDirectionCapability);
            sb.Append("|fso=").Append(GrblInfo.ForceSetOrigin);
            sb.Append("|hd=").Append(GrblInfo.HomingDirection);
            sb.Append("|po=").Append(GrblSettings.GetString(GrblSetting.HomingPulloff));
            for (int i = 0; i < GrblInfo.NumAxes; i++)
                sb.Append("|t").Append(i).Append('=').Append(GrblSettings.GetString(GrblSetting.MaxTravelBase + i));

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        // Read Generated\work_order.macro back IF its stamp matches this fingerprint. The stamp is the
        // file's first line - "(WOCACHE <sha1> stats=<line>)" - an ordinary g-code comment, so a stamped
        // file is still a valid, runnable/inspectable program. A file from before stamping existed (or a
        // hand-edited one) simply misses.
        /// <summary>
        /// What to call this work order in the log. The saved .workorder filename when there is one, the
        /// name typed at New before the first Save, otherwise an explicit "(unsaved work order)" - never a
        /// blank, which would read as though the line had failed to record anything.
        /// </summary>
        private string WorkOrderIdentity()
        {
            if (!string.IsNullOrEmpty(currentFilePath))
                return System.IO.Path.GetFileName(currentFilePath);

            return string.IsNullOrEmpty(pendingName) ? "(unsaved work order)" : pendingName;
        }

        private static bool TryReadCachedProgram(string fp, out string text, out string stats)
        {
            text = stats = null;
            try
            {
                string path = MacroRunner.GeneratedCopyPath("Work Order");
                if (fp == null || !System.IO.File.Exists(path))
                    return false;
                string all = System.IO.File.ReadAllText(path);
                int nl = all.IndexOf('\n');
                if (nl < 0)
                    return false;
                string stamp = all.Substring(0, nl).TrimEnd('\r').Trim();
                if (!stamp.StartsWith(CacheStampPrefix, StringComparison.Ordinal) || !stamp.EndsWith(")", StringComparison.Ordinal))
                    return false;
                string body = stamp.Substring(CacheStampPrefix.Length, stamp.Length - CacheStampPrefix.Length - 1);
                int sp = body.IndexOf(' ');
                string stampFp = sp < 0 ? body : body.Substring(0, sp);
                if (stampFp != fp)
                    return false;
                int st = body.IndexOf("stats=", StringComparison.Ordinal);
                stats = st >= 0 ? body.Substring(st + 6).Trim() : string.Empty;
                text = all.Substring(nl + 1);
                return text.Length > 0;
            }
            catch
            {
                text = stats = null;
                return false;   // unreadable cache = miss, never an error - Generate just recompiles
            }
        }

        // Write-through after a real compile: memo + the stamped file (which is ALSO the Generated-folder
        // debugging copy - one write serves both). No fingerprint (settings unavailable) still writes the
        // plain unstamped copy - the debugging aid must not vanish just because the cache can't key it.
        private static void StoreCachedProgram(string fp, string text, string stats)
        {
            cachedFp = fp;
            cachedProgram = text;
            cachedStats = stats;
            MacroRunner.SaveGeneratedCopy("Work Order", fp == null ? text
                : string.Format("(WOCACHE {0} stats={1})\r\n{2}", fp, stats ?? string.Empty, text));
        }

        // Startup auto-restore (user-chosen "both" 2026-08-08): once the controller is booted and its
        // settings are in (the fingerprint needs them - fp==null before that fails closed), reload the
        // cached program as the job if it still matches the persisted work order, so a restart lands
        // ready for Cycle Start without repaying a multi-minute compile. The section holder is static
        // and populated at config load, so this works without the tab ever having been opened. Never
        // fires over an already-loaded job (a file-open argument wins).
        public static bool TryAutoRestoreCachedProgram(GrblViewModel model)
        {
            try
            {
                var wo = SectionConfig;
                if (wo == null || wo.Toolpaths.Count == 0 || GCode.File.IsLoaded)
                    return false;
                string fp = ComputeFingerprint(wo);
                if (!TryReadCachedProgram(fp, out string text, out string stats))
                    return false;
                cachedFp = fp;          // warm the in-session memo too - Generate after this is instant
                cachedProgram = text;
                cachedStats = stats;
                // Same run-end contract as Generate (found missing on the first hardware test of this
                // feature: the restored run finished and did NOT pop/switch back to the Work Order tab):
                // push the (empty, at boot) slot and arm the same terminal watcher, so a finished or
                // stopped run evaporates the program and lands the operator on the Work Order tab,
                // exactly like a Generate-initiated run.
                GCode.File.Push();
                GCode.File.LoadText("Work Order", text);
                if (model != null)
                {
                    model.Message = string.Format("Restored the last Work Order program from cache ({0}) - press Cycle Start when ready.", stats);
                    WatchForRunEnd(model);
                }
                DebugLog.Write("workorder", string.Format("auto-restored cached program at boot (fp {0}, {1})", fp.Substring(0, 8), stats));
                return true;
            }
            catch
            {
                return false;   // restore is a convenience - any failure means "press Generate", never a fault
            }
        }

        // Shared by Generate and Run: validate + build program text into the
        // `program` field. False (nothing built, `program` untouched) on any validation failure or a "no" to
        // the WCS/tool-length confirm.
        private bool BuildProgram()
        {
            if (model == null)
                return false;

            if (workOrder.Toolpaths.Count == 0)
            {
                AppDialogs.Show("Add at least one toolpath first.", "Work Order", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            var warnings = WorkOrderRules.Validate(workOrder);
            warnings.AddRange(ParameterWarnings());
            if (warnings.Count > 0)
            {
                AppDialogs.Show("Fix these first:\n\n" + string.Join("\n", warnings), "Work Order", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            // The whole of the old Setup gate, reduced to one question at the one moment it matters. Everything
            // this tab emits is written in this work order's own WCS (workOrder.Wcs - see
            // WorkOrderCompiler.WorkOrderWcs) and relies on the tool length reference the toolsetter
            // established - neither of which the app can re-verify without making the operator run Setup
            // again. So it states what it's trusting and lets them answer.
            //
            // Exception: Entire Spoilboard is fully machine-referenced (G53) and touches off its own fresh Z0
            // at run time - it doesn't trust the cached WCS or the tool-length reference at all, so the
            // WCS/TLO warning above is actively misleading for it. WorkOrderRules.Validate already requires
            // it to be the work order's ONLY enabled operation when used, so "any enabled op is Entire
            // Spoilboard" is the same as "every enabled op is." The retired standalone SurfaceSpoilboardWizard
            // this replaced never showed a confirm at Generate either, for the same reason (nothing cached to
            // be stale) - its own MBOX jog-to-touch prompt at RUN time (BuildSurfaceEntireSpoilboard) is where
            // the operator actually confirms machine state, same as it always was.
            bool entireSpoilboardOnly = workOrder.Toolpaths.Any(t => t.EntireSpoilboard && workOrder.EnabledOperations(t).Any());
            string wcsCode = WorkOrderCompiler.WcsCode(workOrder);
            if (!entireSpoilboardOnly && AppDialogs.Show(
                    string.Format("This program will be built on the cached work origin ({0}) and the current tool length reference.\n\n", wcsCode) +
                    "If the machine has been re-homed, the origin has moved, or the tool length reference has been cleared since they were set, run Setup again first.\n\n" +
                    "Proceed?",
                    "Work Order", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return false;

            // Memoized Generate (see the cache block above BuildProgram): same fingerprint = same
            // output, so reuse the last compile - the in-session memo first, then the stamped
            // Generated copy (the restart case). Placed AFTER the validation + WCS/TLO confirm above
            // on purpose: those gates are about the MACHINE'S current trustworthiness, not the text,
            // and must keep firing even when the text is reused.
            string fp = ComputeFingerprint(workOrder);
            if (fp != null)
            {
                string hitText = null, hitStats = null;
                if (fp == cachedFp && cachedProgram != null)
                {
                    hitText = cachedProgram;
                    hitStats = cachedStats;
                }
                else if (TryReadCachedProgram(fp, out string fileText, out string fileStats))
                {
                    hitText = fileText;
                    hitStats = fileStats;
                    cachedFp = fp;
                    cachedProgram = fileText;
                    cachedStats = fileStats;
                }
                if (hitText != null)
                {
                    program = hitText;
                    // Named here too: a cache hit skips the compile entirely, so without this the log would
                    // show a run of a program nothing in the file ever recorded generating.
                    model.LogDetail("Generate for " + WorkOrderIdentity() + " - reused from cache");
                    MacroProcessor.ActiveProgramStats = string.IsNullOrEmpty(hitStats) ? "cached" : hitStats + " (cached)";
                    model.Message = string.Format("Work order program reused from cache - {0}.", MacroProcessor.ActiveProgramStats);
                    DebugLog.Write("workorder", string.Format("Generate: cache hit (fp {0})", fp.Substring(0, 8)));
                    return true;
                }
            }

            // The compile is synchronous on the UI thread and noticeably long for a V-carve, so say so -
            // and the render flush is not optional: without it the "Compiling..." message and the wait
            // cursor would only ever paint AFTER the work they announce is already done.
            // Name what is being generated. The "compiled - N lines in T s" message below already reaches
            // status.log (every Message assignment does), but it never said WHICH work order produced it -
            // so a log with several generates in it could not be matched to the parts they made.
            model.LogDetail("Generate for " + WorkOrderIdentity());

            model.Message = "Compiling work order...";
            Mouse.OverrideCursor = Cursors.Wait;
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var built = WorkOrderCompiler.BuildProgram(workOrder);
                sw.Stop();
                program = string.Join("\r\n", built);
                // Naive kinematic estimate (no acceleration model - see GCodeRunTime's header), so it
                // reads optimistic; still the difference between a 4-minute and a 40-minute engraving,
                // known before pressing Start.
                string estimate = GCodeRunTime.Format(GCodeRunTime.EstimateText(program));
                // Kept on MacroProcessor too so the "ready - press Cycle Start" prompt (which lands right
                // after this and used to overwrite it) can carry the result along - both the Generate
                // path (PublishGenerated re-sets it) and Run's hand-off to the Job tab read it there.
                MacroProcessor.ActiveProgramStats = string.Format("{0} lines in {1:0.0} s", built.Count, sw.Elapsed.TotalSeconds)
                    + (estimate.Length > 0 ? ", est. run " + estimate : string.Empty);
                model.Message = string.Format("Work order compiled - {0}.", MacroProcessor.ActiveProgramStats);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            // Write-through: warm the memo and write the stamped Generated copy in one go (the stamp
            // is what lets a restart reuse this compile; the file is also the debugging copy, whose
            // refresh Step 6 had dropped when it took PublishGenerated out of this path).
            StoreCachedProgram(fp, program, MacroProcessor.ActiveProgramStats);
            return true;
        }

        // ONE press (unified streaming engine Step 6, 2026-08-08): Generate compiles and makes the
        // directive-bearing program THE loaded job - no preview overlay, no second engine, no second
        // press. The ordinary Cycle Start on the Job tab then runs it through the unified pump: the
        // PREREQ gate and PROMPT dialog fire up front in JobRunner.Run, and WAITIDLE/MBOX hold
        // dispatch inline. MacroProcessor.Run / RunStreamedJobInPlace are no longer in Work Order's
        // path at all (the other wizard tabs still use them until Step 7 retires the old engine).
        // The mechanics preserved from the retired two-press Run(): capture the program to a local
        // BEFORE the tab switch (Activate(false) clears the field synchronously), and re-arm dry-run
        // around LoadText (Program_FileChanged clears it by design on every load).
        private void Generate()
        {
            if (!BuildProgram())
                return;
            // The .workorder file is the authoritative source the generated program is built from - keep it
            // current on every Generate (not just gated behind AutoSaveWorkOrderOnExit, and regardless of
            // PromptBeforeAutoSaveWorkOrder - Generate is already an explicit, deliberate action, not a
            // background/leaving-the-tab moment) so the file on disk always matches what was actually output.
            // Untitled (never named/saved at all) is left alone here - nothing to keep in sync with yet, and
            // forcing a Save As on every Generate click for a work order still being drafted would be a
            // bigger interruption than the problem this solves.
            if (currentFilePath != null)
                SaveToDisk();

            bool dryRunArmed = model != null && model.IsDryRunMode;
            string toLoad = program;
            string stats = MacroProcessor.ActiveProgramStats;

            MacroProcessor.SwitchToTab?.Invoke(ViewType.GRBL);   // the Job tab

            // Don't push a SECOND slot when the loaded job is already our own watched Work Order
            // program (boot-restored, or a previous Generate that never ran) - LoadText below replaces
            // it in place and the already-armed watcher keeps serving. Pushing again stacked snapshots
            // and doubled the watcher (observed live 2026-08-08). A foreign loaded file still gets
            // pushed (and thus restored at the end) exactly as before.
            if (!(runEndWatcherArmed && model?.FileName == "Work Order"))
                GCode.File.Push();
            GCode.File.LoadText("Work Order", toLoad);
            if (model != null)
            {
                model.IsDryRunMode = dryRunArmed;
                model.Message = string.Format("Work order loaded ({0}) - press Cycle Start when ready.", stats);
            }

            WatchForRunEnd();
        }

        public static WorkOrder SectionConfig;

        // App-exit safety net (MainWindow.Window_Closing) - separate from AutoSaveIfEnabled/Activate(false)
        // because those are INSTANCE methods on the tab, and MainWindow only calls Activate(false) on
        // whichever tab is CURRENTLY showing at shutdown. If the operator closes the app from a different
        // tab, Work Order's own Activate(false) never runs, so an AutoSaveWorkOrderOnExit operator would
        // still get no save-to-named-file even though they turned that setting on specifically to avoid
        // needing to think about it. The live content itself is never actually at risk either way (Persist()
        // already keeps it in the auto-persisted App.config section on every edit) - this only makes sure the
        // NAMED FILE catches up too. Static and driven off AppConfig.Base (LastWorkOrderFilePath/
        // WorkOrderDirty) rather than an instance's currentFilePath, since a WorkOrderView instance may not
        // even exist if the tab was never opened this session. Always silent (ignores
        // PromptBeforeAutoSaveWorkOrder) - a confirm dialog for a tab that isn't even on screen, on the way
        // out the door, would be a surprise rather than a courtesy.
        public static void AutoSaveOnAppExit()
        {
            if (!AppConfig.Settings.Base.AutoSaveWorkOrderOnExit || !AppConfig.Settings.Base.WorkOrderDirty)
                return;
            string path = AppConfig.Settings.Base.LastWorkOrderFilePath;
            var wo = SectionConfig;
            if (path == null || wo == null || wo.Toolpaths.Count == 0)
                return;
            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(WorkOrder));
                using (var writer = new System.IO.StreamWriter(path))
                    serializer.Serialize(writer, wo);
                AppConfig.Settings.Base.WorkOrderDirty = false;
                AppConfig.Settings.Save();
            }
            catch { /* best-effort on the way out - never block shutdown over a save failure */ }
        }

        #region ConfigPanel<WorkOrder> overrides

        protected override WorkOrder Config { get { return SectionConfig; } set { SectionConfig = value; } }

        // The work order is composed at runtime rather than being a fixed set of dependency properties, so
        // there's nothing for the base class to watch - OnWorkOrderChanged calls Persist() directly.
        protected override DependencyProperty[] PersistedProperties => new DependencyProperty[0];

        protected override void ApplyConfig(WorkOrder config)
        {
            workOrder = config ?? new WorkOrder();
            // The work order CONTENT is this fragment, auto-persisted by ConfigPanel<T> on every edit - but
            // which file it came from (or the New dialog's chosen name) was never part of it, just a private
            // field, so it reset to null every restart even though the content itself came back fine. See
            // AppConfig.Base.LastWorkOrderFilePath's own comment.
            currentFilePath = AppConfig.Settings.Base.LastWorkOrderFilePath;
            pendingName = AppConfig.Settings.Base.LastWorkOrderName;
        }

        protected override WorkOrder CaptureConfig() { return workOrder; }

        #region Save / Load

        // Named work-order files, separate from the single live work order App.config always keeps. The live one
        // answers "what was the tab showing last time"; these answer "the job I set up for that fixture" - and
        // being plain files, they can be handed to someone else.
        private const string WorkOrderFilter = "Work order (*.workorder)|*.workorder|All files (*.*)|*.*";

        private static string WorkOrdersFolder()
        {
            // Fully qualified: an unqualified "Resources" binds to FrameworkElement.Resources on this control.
            try { System.IO.Directory.CreateDirectory(CNC.Core.Resources.WorkOrdersFolder); }
            catch { /* the dialog just opens somewhere else - not worth failing the click over */ }
            return CNC.Core.Resources.WorkOrdersFolder;
        }

        // Actual save-to-disk. True "Save" semantics when a file is already associated (silent, reuses
        // currentFilePath - critical for the auto-save-on-exit path: AutoSaveWorkOrderOnExit + no prompt is
        // supposed to mean NO dialog at all, but Run's tab-switch hits this far more often than the old
        // occasional tab-leave did, so a picker popping every time defeats the point). Only an untitled work
        // order (currentFilePath == null - the true first save) shows the picker, same as Save As always has.
        private bool SaveToDisk()
        {
            string path = currentFilePath;

            if (path == null)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = WorkOrderFilter,
                    AddExtension = true,
                    DefaultExt = ".workorder",
                    InitialDirectory = WorkOrdersFolder(),
                    FileName = SuggestedFileName(),
                    // The needsSave prompt above already says "this overwrites <file>" up front when there's
                    // an existing association - the native replace-confirm here would just be a second,
                    // redundant "are you sure" on top of that one for the (rare) case of picking an existing
                    // filename on a true first save.
                    OverwritePrompt = false
                };
                if (dlg.ShowDialog() != true)
                    return false;
                path = dlg.FileName;
            }

            if (!WriteWorkOrderFile(path))
                return false;

            AdoptWorkOrderFile(path);
            if (model != null)
                model.Message = "Work order saved to " + path;
            return true;
        }

        // Serialize the LIVE work order to a file - the one write everybody shares (Save, Open Copy,
        // Rename). Errors are shown here so no caller can forget to.
        private bool WriteWorkOrderFile(string path)
        {
            // A work order being saved for the first time takes the blank it was composed against, so
            // authoring one records its stock without the operator having to think about it. Only when
            // NOTHING is recorded yet: once a work order names a blank, that is what it was authored for
            // and a later Setup change must not quietly rewrite it. "Use Setup's size" is the way to
            // change it, and it says so on the button.
            if (!workOrder.HasStock)
            {
                var s = StartJobConfig.Section;
                if (s != null && s.Width > 0d && s.Height > 0d)
                {
                    workOrder.StockWidth = s.Width;
                    workOrder.StockDepth = s.Height;
                    workOrder.StockThickness = s.Thickness;
                }
            }

            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(WorkOrder));
                using (var writer = new System.IO.StreamWriter(path))
                    serializer.Serialize(writer, workOrder);
                return true;
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not save the work order:\n" + ex.Message, "Work order", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // Make a just-written file THE work order file: association, boot-restore pointer, clean dirty
        // state, repainted title. The bookkeeping SaveToDisk always did, shared with Open Copy/Rename.
        private void AdoptWorkOrderFile(string path)
        {
            currentFilePath = path;
            pendingName = null;
            AppConfig.Settings.Base.LastWorkOrderFilePath = currentFilePath;
            AppConfig.Settings.Base.LastWorkOrderName = null;
            AppConfig.Settings.Base.WorkOrderDirty = false;
            AppConfig.Settings.Save();
            UpdateTitleBar();
        }

        // Prefers the name the operator already gave this work order - via New's prompt, or the file it was
        // loaded from/last saved to - over guessing from the toolpaths, so Save's suggestion matches what the
        // title bar is already showing instead of surprising them with something different.
        private string SuggestedFileName()
        {
            string name = !string.IsNullOrEmpty(pendingName) ? pendingName
                : currentFilePath != null ? System.IO.Path.GetFileNameWithoutExtension(currentFilePath)
                : null;
            if (string.IsNullOrEmpty(name))
            {
                name = workOrder.Toolpaths.Count > 0 ? workOrder.Toolpaths[0].Name : "work order";
                if (workOrder.Toolpaths.Count > 1)
                    name += string.Format(" +{0}", workOrder.Toolpaths.Count - 1);
            }
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // If AutoSaveWorkOrderOnExit is on, saves the current work order the same way LEAVING the tab
        // already would (Activate(false), above) - PromptBeforeAutoSaveWorkOrder still asks a plain Yes/No
        // first if that sub-option is also on. Called before New/Load's own "discard/replace" check so an
        // operator who's turned autosave on never sees that prompt for content that's about to be saved
        // anyway - the button is just a different trigger than a tab switch for the exact same situation.
        private void AutoSaveIfEnabled()
        {
            bool wouldLoseChanges = workOrder.Toolpaths.Count > 0
                && (currentFilePath == null || AppConfig.Settings.Base.WorkOrderDirty);
            if (!wouldLoseChanges || !AppConfig.Settings.Base.AutoSaveWorkOrderOnExit)
                return;

            bool doSave = !AppConfig.Settings.Base.PromptBeforeAutoSaveWorkOrder
                || AppDialogs.Show("Save these work order changes now?", "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (doSave)
                SaveToDisk();
        }

        // Clears the tab to a blank, untitled work order - the top-level command Load/Save were missing a
        // counterpart for. No name prompt: an untitled work order is dirty by default (currentFilePath == null
        // - see AppConfig.Base.WorkOrderDirty), so Activate(false) already asks for a save (and therefore a
        // name/file) once there's actually something in it worth naming, instead of demanding a name upfront
        // for what might stay empty.
        private void MenuNew_Click(object sender, RoutedEventArgs e)
        {
            AutoSaveIfEnabled();

            // Only worth confirming if there's something that would actually be LOST - a work order that's
            // already saved to disk (currentFilePath set, WorkOrderDirty false) has nothing on this tab that
            // isn't also sitting safely in that file, so starting fresh needs no gate. Same dirty check
            // Activate(false) already uses for its own "unsaved changes" prompt.
            bool wouldLoseChanges = workOrder.Toolpaths.Count > 0
                && (currentFilePath == null || AppConfig.Settings.Base.WorkOrderDirty);
            if (wouldLoseChanges &&
                AppDialogs.Show(string.Format("Discard the current work order ({0} toolpath{1}) and start a new one?",
                        workOrder.Toolpaths.Count, workOrder.Toolpaths.Count == 1 ? "" : "s"),
                    "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ResetToBlankWorkOrder();
        }

        // Clear the tab to a blank, untitled work order - New's reset, shared with the title menu's
        // Close and Delete (which do their own gating/confirming first).
        private void ResetToBlankWorkOrder()
        {
            // WorkOrder's own field default (Wcs = 0, "Follow Setup") is already the right starting point -
            // no explicit seeding needed here.
            workOrder = new WorkOrder();
            currentFilePath = null;
            pendingName = null;
            AppConfig.Settings.Base.LastWorkOrderFilePath = null;
            AppConfig.Settings.Base.LastWorkOrderName = null;
            AppConfig.Settings.Save();
            selectedToolpath = null;
            selectedOp = null;

            loadingFields = true;
            chkGroupByTool.IsChecked = workOrder.GroupByTool;
            chkSkipFirstToolChange.IsChecked = workOrder.SkipFirstToolChange;
            cbxWcs.SelectedIndex = Math.Min(Math.Max(workOrder.Wcs, 0), 6);
            loadingFields = false;

            RebuildTree(null);
            LoadFields();
            UpdateTitleBar();
            OnWorkOrderChanged();
            // Not diverged from a saved file since there's nothing in it yet (OnWorkOrderChanged above just set
            // this true, same as any other content change) - Activate(false)'s prompt still treats "untitled"
            // (currentFilePath == null) as needing a save on its own once real content exists, so this doesn't
            // mean New can never prompt, just that an empty new work order alone doesn't.
            AppConfig.Settings.Base.WorkOrderDirty = false;
            AppConfig.Settings.Save();
            // OnWorkOrderChanged above already painted the title with a "*" (it runs while Dirty is
            // momentarily true) - repaint now that it's back to false, or New would show a stale asterisk.
            UpdateTitleBar();
        }

        // The tree panel's own title bar: the work order's name with no path or extension - "Toolpaths" said
        // nothing this panel doesn't already show by being full of toolpaths, where the name of the actual
        // job being composed is worth surfacing. Full path lives in the tooltip instead of cluttering the row.
        // Trailing "*" mirrors the same AppConfig.Base.WorkOrderDirty flag the Load/New/leave-tab prompts
        // gate on, live (called from OnWorkOrderChanged, not just Load/New/Save) - so "does this need saving"
        // is visible at a glance instead of only discoverable by trying to Load/New and seeing whether it asks.
        private void UpdateTitleBar()
        {
            string suffix = AppConfig.Settings.Base.WorkOrderDirty ? " *" : string.Empty;
            if (currentFilePath != null)
            {
                txtWorkOrderName.Text = System.IO.Path.GetFileNameWithoutExtension(currentFilePath) + suffix;
                txtWorkOrderName.ToolTip = currentFilePath;
            }
            else if (!string.IsNullOrEmpty(pendingName))
            {
                txtWorkOrderName.Text = pendingName + suffix;
                txtWorkOrderName.ToolTip = "Not yet saved";
            }
            else
            {
                txtWorkOrderName.Text = "(untitled)" + suffix;
                txtWorkOrderName.ToolTip = "Not yet saved";
            }
        }

        // ---- Title right-click menu (user request 2026-08-08): Rename / Open Copy / Close / Delete ----
        // The file-level operations the tab never had - before this, renaming a work order meant closing
        // it and renaming the .workorder in Explorer. The full path already lives in the title's tooltip.

        private void WorkOrderNameMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Rename/Delete operate on the associated FILE, so they grey out for an untitled work order.
            // Resolved off the menu instance rather than generated fields - a ContextMenu lives outside
            // the visual tree and its name scope is the least reliable thing about it.
            bool hasFile = currentFilePath != null;
            foreach (var item in ((ContextMenu)sender).Items)
                if (item is MenuItem mi && (mi.Name == "miWoRename" || mi.Name == "miWoDelete"))
                    mi.IsEnabled = hasFile;
        }

        // Same picker both Rename and Open Copy use to ask for the target name: seeded next to the
        // current file (or the work-orders folder for an untitled one). The native replace-confirm is ON
        // here, unlike SaveToDisk's first-save picker - overwriting a DIFFERENT work order's file is
        // destructive and has no earlier "this overwrites X" prompt covering it.
        private string PickWorkOrderFileName(string title, string suggestedName)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = WorkOrderFilter,
                AddExtension = true,
                DefaultExt = ".workorder",
                InitialDirectory = currentFilePath != null ? System.IO.Path.GetDirectoryName(currentFilePath) : WorkOrdersFolder(),
                FileName = suggestedName,
                OverwritePrompt = true
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // Rename = write the LIVE content under the new name, adopt it, then remove the old file. Going
        // through a save rather than File.Move means unsaved edits ride along instead of being lost or
        // silently flushed into the OLD name first - and a failed write leaves the original untouched.
        private void MenuRenameWorkOrder_Click(object sender, RoutedEventArgs e)
        {
            if (currentFilePath == null)
                return;

            string newPath = PickWorkOrderFileName("Rename work order", System.IO.Path.GetFileNameWithoutExtension(currentFilePath));
            if (newPath == null || SamePath(newPath, currentFilePath))
                return;

            string oldPath = currentFilePath;
            if (!WriteWorkOrderFile(newPath))
                return;

            try
            {
                System.IO.File.Delete(oldPath);
            }
            catch (Exception ex)
            {
                // The rename itself succeeded - the new file exists and is adopted below - so tell the
                // operator the leftover exists rather than failing an operation that is already done.
                AppDialogs.Show(string.Format("Renamed, but the old file could not be removed:\n{0}\n\n{1}", oldPath, ex.Message),
                    "Work order", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            AdoptWorkOrderFile(newPath);
            if (model != null)
                model.Message = "Work order renamed to " + newPath;
        }

        // Open Copy = save the LIVE content under a new name and continue editing THAT file - the
        // original stays on disk as it last was. (With Rename above this is the "duplicate for the next
        // variant" half; it also works for an untitled work order, where it is simply the first save.)
        private void MenuOpenCopyWorkOrder_Click(object sender, RoutedEventArgs e)
        {
            string suggested = SuggestedFileName() + " copy";
            string newPath = PickWorkOrderFileName("Open a copy of this work order", suggested);
            if (newPath == null)
                return;
            if (currentFilePath != null && SamePath(newPath, currentFilePath))
            {
                AppDialogs.Show("That is the current file - pick a different name for the copy.",
                    "Work order", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!WriteWorkOrderFile(newPath))
                return;

            AdoptWorkOrderFile(newPath);
            if (model != null)
                model.Message = "Now editing " + newPath;
        }

        // Close = New's clear-to-blank with Close wording: same autosave courtesy, same only-ask-when-
        // something-would-be-lost gate (a saved, clean work order closes silently - its file has it all).
        private void MenuCloseWorkOrder_Click(object sender, RoutedEventArgs e)
        {
            AutoSaveIfEnabled();

            bool wouldLoseChanges = workOrder.Toolpaths.Count > 0
                && (currentFilePath == null || AppConfig.Settings.Base.WorkOrderDirty);
            if (wouldLoseChanges &&
                AppDialogs.Show(string.Format("Discard the current work order ({0} toolpath{1}) and close it?",
                        workOrder.Toolpaths.Count, workOrder.Toolpaths.Count == 1 ? "" : "s"),
                    "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ResetToBlankWorkOrder();
        }

        // Delete = remove the .workorder file from disk AND close it on the tab. One confirm covers both
        // (it names the file and says the tab closes too); no autosave first - saving content into a file
        // that is about to be deleted would be absurd, and the confirm is the operator accepting the loss.
        private void MenuDeleteWorkOrder_Click(object sender, RoutedEventArgs e)
        {
            if (currentFilePath == null)
                return;

            if (AppDialogs.Show(string.Format("Delete this work order file from disk?\n\n{0}\n\nThis cannot be undone, and the work order closes with it.", currentFilePath),
                    "Work order", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                System.IO.File.Delete(currentFilePath);
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not delete the work order file:\n" + ex.Message, "Work order", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ResetToBlankWorkOrder();
            if (model != null)
                model.Message = "Work order deleted.";
        }

        private void btnLoad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = WorkOrderFilter,
                InitialDirectory = WorkOrdersFolder()
            };
            if (dlg.ShowDialog() != true)
                return;

            WorkOrder loaded;
            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(WorkOrder));
                using (var reader = new System.IO.StreamReader(dlg.FileName))
                    loaded = (WorkOrder)serializer.Deserialize(reader);
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not read that work order:\n" + ex.Message, "Work order", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (loaded == null)
                return;

            AutoSaveIfEnabled();

            // Loading REPLACES what's on the tab, and the tab is auto-persisted, so an unsaved composition is
            // gone for good - worth a confirmation while there's something to lose. A work order that's
            // already saved (currentFilePath set, WorkOrderDirty false) has nothing here that isn't also
            // sitting safely in that file already, so skip the prompt - same dirty check Activate(false)
            // already uses for its own "unsaved changes" prompt.
            bool wouldLoseChanges = workOrder.Toolpaths.Count > 0
                && (currentFilePath == null || AppConfig.Settings.Base.WorkOrderDirty);
            if (wouldLoseChanges &&
                AppDialogs.Show(string.Format("Replace the current work order ({0} toolpath{1}) with the one in\n{2}?",
                        workOrder.Toolpaths.Count, workOrder.Toolpaths.Count == 1 ? "" : "s", System.IO.Path.GetFileName(dlg.FileName)),
                    "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            workOrder = loaded;
            currentFilePath = dlg.FileName;
            pendingName = null;
            AppConfig.Settings.Base.LastWorkOrderFilePath = currentFilePath;
            AppConfig.Settings.Base.LastWorkOrderName = null;
            AppConfig.Settings.Save();
            selectedToolpath = null;
            selectedOp = null;

            loadingFields = true;
            chkGroupByTool.IsChecked = workOrder.GroupByTool;
            chkSkipFirstToolChange.IsChecked = workOrder.SkipFirstToolChange;
            cbxWcs.SelectedIndex = Math.Min(Math.Max(workOrder.Wcs, 0), 6);
            loadingFields = false;

            RebuildTree(workOrder.Toolpaths.FirstOrDefault());
            LoadFields();
            UpdateTitleBar();
            OnWorkOrderChanged();
            // Matches what's on disk right after loading it (OnWorkOrderChanged above set this true, same as
            // any other content change - override it back to false here).
            AppConfig.Settings.Base.WorkOrderDirty = false;
            AppConfig.Settings.Save();
            // OnWorkOrderChanged above already painted the title with a "*" (it runs while Dirty is
            // momentarily true) - repaint now that it's back to false, or every Load would show a stale
            // asterisk despite matching the file it was just loaded from.
            UpdateTitleBar();
        }

        #endregion

        protected override void OnConfigReady()
        {
            if (model == null)
                model = DataContext as GrblViewModel;
            loadingFields = true;
            chkGroupByTool.IsChecked = workOrder.GroupByTool;
            chkSkipFirstToolChange.IsChecked = workOrder.SkipFirstToolChange;
            cbxWcs.SelectedIndex = Math.Min(Math.Max(workOrder.Wcs, 0), 6);
            loadingFields = false;
            RebuildTree(workOrder.Toolpaths.FirstOrDefault());
            LoadFields();
            UpdateTitleBar();
            UpdateGroupByToolSummary();
            UpdateSkipFirstToolChangeSummary();
            UpdateValidation();
            DrawDiagram();
        }

        #endregion
    }
}
