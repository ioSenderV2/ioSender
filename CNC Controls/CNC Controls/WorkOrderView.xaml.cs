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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNC.Core;

namespace CNC.Controls
{
    public partial class WorkOrderView : ConfigPanel<WorkOrder>, IGrblConfigTab
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

            foreach (var f in AllFields())
                System.ComponentModel.DependencyPropertyDescriptor
                    .FromProperty(NumericField.ValueProperty, typeof(NumericField))
                    .AddValueChanged(f, (s, e) => CaptureFields());

            canvasDiagram.MouseLeftButtonDown += (s, e) => { placing = true; PlaceFromMouse(e.GetPosition(canvasDiagram)); canvasDiagram.CaptureMouse(); };
            canvasDiagram.MouseMove += (s, e) => { if (placing) PlaceFromMouse(e.GetPosition(canvasDiagram)); };
            canvasDiagram.MouseLeftButtonUp += (s, e) => { placing = false; canvasDiagram.ReleaseMouseCapture(); };
        }

        private NumericField[] AllFields()
        {
            return new[] { fldX, fldY, fldLength, fldAngle, fldDiameter, fldSize, fldWidth, fldDepthY,
                           fldColumns, fldColumnSpacing, fldRows, fldRowSpacing,
                           fldPatternCount, fldPatternRadius, fldPatternStartAngle, fldPatternArcSpan,
                           fldHoleDiameter, fldTotalDepth, fldDepthOfCut, fldPeckDepth, fldBoreStepDown, fldStepover,
                           fldNumTabs, fldTabWidth, fldTabHeight,
                           fldWallStockToLeave, fldFloorStockToLeave, fldChamferDepth, fldCountersinkDiameter };
        }

        private bool placing = false;
        private OddJobsStockCanvas.Transform stockTransform;

        // Click/drag on the stock drawing to place the selected toolpath's geometry - works whether the
        // toolpath itself or one of its operations is selected, since the geometry belongs to the toolpath.
        private void PlaceFromMouse(Point p)
        {
            if (selectedToolpath == null)
                return;

            var work = OddJobsStockCanvas.ToWork(stockTransform, p);
            var clamped = OddJobsStockCanvas.ClampToKeepOut(work.X, work.Y);
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
            workOrder.Toolpaths.Add(tp);
            RebuildTree(tp);
            OnWorkOrderChanged();
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
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SuggestTool("roughing", material);
                    break;
                case WorkOrderOpKind.Contour:
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SuggestTool("roughing", material);
                    break;
                case WorkOrderOpKind.Drill:
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SuggestTool("drilling", material);
                    break;
                case WorkOrderOpKind.Bore:
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SuggestTool("roughing", material);
                    break;
                case WorkOrderOpKind.SideFinish:
                case WorkOrderOpKind.BottomFinish:
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SuggestTool("finishing", material);
                    break;
                case WorkOrderOpKind.Chamfer:
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SuggestTool("chamfer", material);
                    op.Feed = 500d;
                    break;
                case WorkOrderOpKind.Countersink:
                    // op.CountersinkDiameter already holds its own default (12.5mm - see WorkOrderModel) at
                    // this point, so the tool is picked from that instead of a generic "smallest" guess.
                    op.Tool = (int)OddJobsFeedsSpeedsDialog.SmallestCountersinkBitFor(op.CountersinkDiameter);
                    break;
            }

            // Recall whatever this tool/material was last dialed in to, so a new operation starts from the
            // operator's own proven numbers rather than the chart default (see OddJobsToolMemory).
            var remembered = OddJobsToolMemory.Find((OddJobsTool)op.Tool, op.BitDiameter, material);
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
            var copy = new WorkOrderToolpath
            {
                Name = NextDuplicateName(tp.Name),
                Geometry = tp.Geometry,
                Enabled = tp.Enabled,
                X = tp.X, Y = tp.Y,
                Length = tp.Length, Angle = tp.Angle, Diameter = tp.Diameter,
                Width = tp.Width, Depth = tp.Depth, Size = tp.Size,
                Pattern = tp.Pattern,
                Columns = tp.Columns, RowSpacing = tp.RowSpacing, ColumnSpacing = tp.ColumnSpacing, Rows = tp.Rows,
                PatternCount = tp.PatternCount, PatternRadius = tp.PatternRadius,
                PatternStartAngle = tp.PatternStartAngle, PatternArcSpan = tp.PatternArcSpan,
                IndirectSource = tp.IndirectSource
            };
            foreach (var op in tp.Operations)
                copy.Operations.Add(CloneOperation(op));

            workOrder.Toolpaths.Insert(workOrder.Toolpaths.IndexOf(tp) + 1, copy);
            RebuildTree(copy);
            OnWorkOrderChanged();
        }

        private static WorkOrderOperation CloneOperation(WorkOrderOperation op)
        {
            return new WorkOrderOperation
            {
                Kind = op.Kind, Enabled = op.Enabled, Tool = op.Tool, BitDiameter = op.BitDiameter,
                HoleDiameter = op.HoleDiameter, TotalDepth = op.TotalDepth, Through = op.Through,
                NumTabs = op.NumTabs, TabWidth = op.TabWidth, TabHeight = op.TabHeight,
                DepthOfCut = op.DepthOfCut, Stepover = op.Stepover, PeckDepth = op.PeckDepth, DrillHss = op.DrillHss,
                BoreStepDown = op.BoreStepDown, WallStockToLeave = op.WallStockToLeave,
                FloorStockToLeave = op.FloorStockToLeave, ChamferDepth = op.ChamferDepth,
                CountersinkDiameter = op.CountersinkDiameter,
                Feed = op.Feed, PlungeFeed = op.PlungeFeed, SpindleRPM = op.SpindleRPM, BitMaxRPM = op.BitMaxRPM
            };
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

        // Header for a toolpath/operation row: an enable checkbox plus the summary text. The text is a separate
        // TextBlock rather than the CheckBox's own Content on purpose - as Content, clicking the row's label to
        // SELECT it would toggle the checkbox as a side effect.
        private FrameworkElement MakeCheckHeader(string text, bool enabled, Action<bool> onToggle)
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
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        // Updates an existing check-header in place: text, checked state, and the dimming that shows a row is
        // held back. Cheaper than rebuilding the tree, and it keeps selection and expansion state.
        private static void SetCheckHeader(TreeViewItem item, string text, bool enabled, bool effective)
        {
            var panel = item.Header as StackPanel;
            if (panel == null || panel.Children.Count < 2)
                return;
            ((CheckBox)panel.Children[0]).IsChecked = enabled;
            var label = (TextBlock)panel.Children[1];
            label.Text = text;
            // Dimmed when this row won't run - either unchecked itself, or under an unchecked toolpath.
            label.Opacity = effective ? 1.0 : 0.45;
        }

        private void RebuildTree(object toSelect)
        {
            treeToolpaths.Items.Clear();
            foreach (var tp in workOrder.Toolpaths)
            {
                var owner = tp;
                var tpItem = new TreeViewItem
                {
                    Header = MakeCheckHeader(WorkOrderRules.Summarize(tp), tp.Enabled, on => ToggleEnabled(owner, null, on)),
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
                    var source = workOrder.Toolpaths.FirstOrDefault(t => string.Equals(t.Name, tp.IndirectSource, StringComparison.OrdinalIgnoreCase));
                    if (source == null)
                    {
                        tpItem.Items.Add(new TreeViewItem { Header = "(source not found)", Foreground = Brushes.IndianRed, FontStyle = FontStyles.Italic });
                    }
                    else if (source.Operations.Count == 0)
                    {
                        tpItem.Items.Add(new TreeViewItem { Header = "(source has no operations yet)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
                    }
                    else
                    {
                        foreach (var op in source.Operations)
                            tpItem.Items.Add(new TreeViewItem { Header = WorkOrderRules.Summarize(op), Foreground = Brushes.Gray, IsEnabled = false });
                    }
                }
                else
                {
                    foreach (var op in tp.Operations)
                    {
                        var ownerOp = op;
                        var opItem = new TreeViewItem
                        {
                            Header = MakeCheckHeader(WorkOrderRules.Summarize(op), op.Enabled, on => ToggleEnabled(owner, ownerOp, on)),
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
                treeToolpaths.Items.Add(tpItem);
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
        private void SelectInTree(object tag)
        {
            foreach (TreeViewItem tpItem in treeToolpaths.Items)
            {
                if (ReferenceEquals(tpItem.Tag, tag))
                {
                    tpItem.IsSelected = true;
                    tpItem.Dispatcher.BeginInvoke((System.Action)(() => tpItem.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }
                foreach (TreeViewItem opItem in tpItem.Items)
                    if (ReferenceEquals(opItem.Tag, tag))
                    {
                        opItem.IsSelected = true;
                        opItem.Dispatcher.BeginInvoke((System.Action)(() => opItem.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
                        return;
                    }
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

            loadingFields = false;
            UpdateFeedsSummary();
        }

        private void LoadToolpathFields()
        {
            var tp = selectedToolpath;
            txtPanelHeader.Text = "Toolpath geometry";

            // Indirect's name is generated, not typed - see UpdateIndirectName - so the box is shown but
            // disabled, same idiom as a drill's hole diameter field being driven by the geometry instead of
            // editable (LoadOperationFields).
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

            fldX.Value = tp.X; fldY.Value = tp.Y;
            fldLength.Value = tp.Length; fldAngle.Value = tp.Angle;
            fldDiameter.Value = tp.Diameter; fldSize.Value = tp.Size;
            fldWidth.Value = tp.Width; fldDepthY.Value = tp.Depth;

            // Only the dimensions this geometry actually has. Indirect has none of its own - it borrows
            // whatever the source toolpath has.
            bool isLine = tp.Geometry == WorkOrderGeometryKind.Line;
            bool isCircle = tp.Geometry == WorkOrderGeometryKind.Circle;
            bool isWD = tp.Geometry == WorkOrderGeometryKind.Oval || tp.Geometry == WorkOrderGeometryKind.Rect;
            Show(fldLength, isLine);
            Show(fldAngle, isLine);
            Show(fldDiameter, isCircle);
            Show(fldSize, tp.Geometry == WorkOrderGeometryKind.Square);
            Show(fldWidth, isWD);
            Show(fldDepthY, isWD);

            Show(pnlIndirectSource, tp.IsIndirect);
            if (tp.IsIndirect)
            {
                // Every OTHER non-Indirect toolpath is a legal source - excluding Indirect ones keeps this to a
                // single hop rather than a chain WorkOrderCompiler would have to resolve recursively.
                cbxIndirectSource.Items.Clear();
                foreach (var candidate in workOrder.Toolpaths.Where(t => !ReferenceEquals(t, tp) && !t.IsIndirect))
                    cbxIndirectSource.Items.Add(candidate.Name);
                cbxIndirectSource.SelectedItem = tp.IndirectSource;
            }

            // Indirect already IS a single repeat of the source at a different X/Y - a pattern on top of that
            // would be a repeat of a repeat, and everything else about the cut lives on the source anyway (see
            // pnlPatternSection's own comment), so the whole section is hidden rather than just left blank.
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
            fldCountersinkDiameter.Value = op.CountersinkDiameter;
            chkThrough.IsChecked = op.Through;

            bool supportsThrough = WorkOrderRules.SupportsThrough(op.Kind);
            // Finishing passes and the chamfer follow the roughing operation's depth rather than setting their
            // own, so they show no depth field at all.
            bool ownsDepth = op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour
                          || op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore;

            bool isHole = op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore;

            Show(pnlThrough, supportsThrough);
            Show(fldHoleDiameter, isHole);
            // A through cut takes its depth from the stock thickness, so Total depth has nothing left to say.
            Show(fldTotalDepth, ownsDepth && !(supportsThrough && op.Through));
            Show(fldDepthOfCut, op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour);
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
            Show(fldStepover, op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.BottomFinish
                           || (op.Kind == WorkOrderOpKind.Bore && WorkOrderRules.NeedsSteppedBore(op.HoleDiameter, op.BitDiameter)));
            Show(fldWallStockToLeave, op.Kind == WorkOrderOpKind.SideFinish);
            Show(fldFloorStockToLeave, op.Kind == WorkOrderOpKind.BottomFinish);
            Show(fldChamferDepth, op.Kind == WorkOrderOpKind.Chamfer);
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

                if (tp.IsIndirect)
                {
                    UpdateIndirectName(tp);
                }
                else
                {
                    tp.Length = fldLength.Value; tp.Angle = fldAngle.Value;
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

            OnWorkOrderChanged();
        }

        private void txtName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (loadingFields || selectedToolpath == null)
                return;
            selectedToolpath.Name = txtName.Text;
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
                if (string.IsNullOrEmpty(selectedToolpath.IndirectSource))
                    selectedToolpath.IndirectSource = workOrder.Toolpaths.FirstOrDefault(t => !ReferenceEquals(t, selectedToolpath) && !t.IsIndirect)?.Name;
                UpdateIndirectName(selectedToolpath);
            }

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
            tp.Name = string.Format("@{0}({1:0.###},{2:0.###})", source, tp.X, tp.Y);
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

            var dlg = new OddJobsFeedsSpeedsDialog((OddJobsTool)op.Tool, docLabel: docLabel, showDoc: showDoc)
            {
                Owner = Window.GetWindow(this),
                // Flutes deliberately NOT set - the dialog's tool dropdown sets the right count for the
                // selected tool (2/3/1 per OddJobsTool). The old wizards all overrode it with a hardcoded 2,
                // so the 3-flute roughing bit computed its chip load as if it were 2-flute.
                // A drill's diameter is the hole itself, so it comes from the geometry, not from a bit field.
                BitDiameter = isDrill ? op.HoleDiameter : op.BitDiameter,
                SpindleRPM = op.SpindleRPM, Feed = op.Feed, PlungeFeed = op.PlungeFeed,
                DepthOfCut = doc,
                Material = material,
                IsHssDrill = op.DrillHss,
                // A ball engaging near its TIP (a bottom-finish pass skimming the floor) behaves like a much
                // smaller cutter than nominal - the advisor needs the engagement depth to say anything useful.
                EngagementDepthMm = op.Kind == WorkOrderOpKind.BottomFinish ? op.FloorStockToLeave : (double?)null
            };
            dlg.RestrictToolsFor(op.Kind);

            if (dlg.ShowDialog() != true)
                return;

            op.Tool = (int)dlg.SelectedTool;
            // A drill's size is dictated by the hole, so whatever the dialog shows there isn't the operator's
            // to change - everything else takes the dialog's diameter.
            if (!isDrill)
                op.BitDiameter = dlg.BitDiameter;
            op.SpindleRPM = dlg.SpindleRPM; op.Feed = dlg.Feed; op.PlungeFeed = dlg.PlungeFeed;
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

            OddJobsToolMemory.Remember(dlg.SelectedTool, dlg.BitDiameter, material, dlg.SpindleRPM, dlg.Feed, dlg.PlungeFeed, dlg.DepthOfCut);

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
                WorkOrderCompiler.ToolNumberFor(op), ToolName((OddJobsTool)op.Tool), dia, rpm, op.Feed, op.PlungeFeed);

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

        private static string ToolName(OddJobsTool tool)
        {
            switch (tool)
            {
                case OddJobsTool.EndMill2Flute: return "2-flute end mill";
                case OddJobsTool.RoughingEndMill3Flute: return "3-flute roughing end mill";
                case OddJobsTool.OFlute: return "O-flute";
                case OddJobsTool.BallEnd: return "ball end";
                case OddJobsTool.SurfacingBit25mm: return "surfacing bit";
                case OddJobsTool.VBit45: return "45 deg V-bit";
                case OddJobsTool.EndMill2Flute18: return "1/8\" 2-flute end mill";
                case OddJobsTool.BallEnd18: return "1/8\" ball end";
                case OddJobsTool.DrillBit: return "drill";
                case OddJobsTool.CountersinkBit38: return "3/8\" (10mm) countersink bit";
                case OddJobsTool.CountersinkBit916: return "9/16\" (14mm) countersink bit";
                case OddJobsTool.CountersinkBit1316: return "13/16\" (21mm) countersink bit";
                case OddJobsTool.CountersinkBit118: return "1-1/8\" (28mm) countersink bit";
                default: return tool.ToString();
            }
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
            Persist();
        }

        // Cheaper than a full RebuildTree (which would fight the current selection) - the structure hasn't
        // changed, only the summaries a parameter edit affects. The trailing placeholder row is left alone.
        private void RefreshTreeHeaders()
        {
            for (int t = 0; t < treeToolpaths.Items.Count && t < workOrder.Toolpaths.Count; t++)
            {
                var tpItem = (TreeViewItem)treeToolpaths.Items[t];
                var tp = workOrder.Toolpaths[t];
                SetCheckHeader(tpItem, WorkOrderRules.Summarize(tp), tp.Enabled, tp.Enabled);
                for (int i = 0; i < tp.Operations.Count && i < tpItem.Items.Count; i++)
                {
                    var op = tp.Operations[i];
                    // An operation under an unchecked toolpath keeps its own tick but is dimmed too - it isn't
                    // going to run, and showing it bright would be a lie about what Generate will emit.
                    SetCheckHeader((TreeViewItem)tpItem.Items[i], WorkOrderRules.Summarize(op), op.Enabled, op.Enabled && tp.Enabled);
                }
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
            {
                tp.Enabled = on;
                foreach (var each in tp.Operations)
                    each.Enabled = on;
            }

            // OnWorkOrderChanged refreshes every row's header, which is what repaints the cascaded ticks.
            OnWorkOrderChanged();
        }

        private void UpdateValidation()
        {
            var warnings = WorkOrderRules.Validate(workOrder);
            warnings.AddRange(ParameterWarnings());

            txtWarnings.Text = string.Join("\n", warnings);
            if (isActiveTab)
                MacroProcessor.IsGenerateReady = warnings.Count == 0 && workOrder.Toolpaths.Count > 0;

            int ops = workOrder.EnabledOperationCount;
            int tps = workOrder.Toolpaths.Count(t => workOrder.EnabledOperations(t).Any());
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
                    bool ownsDepth = op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour
                                  || op.Kind == WorkOrderOpKind.Drill || op.Kind == WorkOrderOpKind.Bore;

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
                        else if (!OddJobsFeedsSpeedsDialog.IsCountersinkBit((OddJobsTool)op.Tool))
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

            stockTransform = OddJobsStockCanvas.DrawStock(canvasDiagram);
            double scale = stockTransform.Scale;

            // Envelopes first, so the nominal outlines stay legible on top of them.
            // A held-back toolpath gets no envelope: the envelope shows where material WILL be removed, and
            // this one isn't going to remove any. An Indirect toolpath's "own operations" are borrowed from
            // its source (see WillRun/GeometrySource) - it has none of its own to check here.
            foreach (var tp in workOrder.Toolpaths)
                if (WillRun(tp))
                    foreach (var pos in tp.PatternPositions())
                        DrawEnvelope(GeometrySource(tp), pos[0], pos[1], scale);

            var geomBrushes = OddJobsStockCanvas.GeometryBrushes(StartJobConfig.Section?.Material ?? string.Empty);
            foreach (var tp in workOrder.Toolpaths)
            {
                // What actually decides the drawn shape/size/reach - the toolpath itself, or (Indirect) whatever
                // it currently points at. Position, pattern and the name label still come from tp itself.
                var geom = GeometrySource(tp);

                bool isSelected = ReferenceEquals(tp, selectedToolpath);
                // Still drawn when held back - it's geometry you authored and want to see for fit against the
                // rest - but greyed, so what's actually going to be cut reads at a glance.
                bool willRun = WillRun(tp);
                var stroke = !willRun ? geomBrushes.HeldBack : isSelected ? geomBrushes.Selected : geomBrushes.Normal;
                double thickness = isSelected ? 2d : 1d;
                var positions = tp.PatternPositions().ToList();

                // Every pattern instance is drawn: a pattern that only showed its anchor would hide exactly the
                // overlap this drawing exists to catch.
                foreach (var pos in positions)
                {
                    var center = OddJobsStockCanvas.ToPixel(stockTransform, pos[0], pos[1]);
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
                        default:
                            AddRect(center, geom.Width / 2d * scale, geom.Depth / 2d * scale, stroke, thickness, null);
                            break;
                    }

                    var dot = new Ellipse { Width = 5, Height = 5, Fill = stroke };
                    Canvas.SetLeft(dot, center.X - 2.5); Canvas.SetTop(dot, center.Y - 2.5);
                    canvasDiagram.Children.Add(dot);
                }

                // Named once, on the anchor instance - one label per instance would just be clutter.
                // Black at all times: the labels sit over the stock's own material colour (olive for MDF, tan,
                // grey for metals), and a grey or steel-blue label was unreadable against it. Selection is
                // carried by weight instead of colour.
                var anchor = OddJobsStockCanvas.ToPixel(stockTransform, tp.X, tp.Y);
                var label = new TextBlock
                {
                    Text = positions.Count > 1 ? string.Format("{0} (x{1})", tp.Name, positions.Count) : tp.Name,
                    FontSize = 13,
                    Foreground = Brushes.Black,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, anchor.X - label.DesiredSize.Width / 2d);
                Canvas.SetTop(label, anchor.Y - ShapeHalfHeightPx(geom, scale) - 17d);
                canvasDiagram.Children.Add(label);
            }
        }

        // The toolpath that actually decides drawn shape/size/reach for `tp` - itself, unless `tp` is Indirect,
        // in which case its resolved source (or `tp` itself, drawing as a default-sized placeholder, if the
        // reference is currently broken - see WorkOrderRules.ResolveIndirectSource).
        private WorkOrderToolpath GeometrySource(WorkOrderToolpath tp)
        {
            return WorkOrderRules.ResolveIndirectSource(workOrder, tp) ?? tp;
        }

        // Whether this toolpath's cut will actually show up in Generate - its own enabled operations, or
        // (Indirect) its resolved source's.
        private bool WillRun(WorkOrderToolpath tp)
        {
            var source = WorkOrderRules.ResolveIndirectSource(workOrder, tp);
            return source != null && workOrder.EnabledOperations(source).Any();
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

        // The translucent footprint of material this toolpath removes at one instance position.
        private void DrawEnvelope(WorkOrderToolpath tp, double atX, double atY, double scale)
        {
            if (tp.Operations.Count == 0)
                return;

            var center = OddJobsStockCanvas.ToPixel(stockTransform, atX, atY);
            var geomBrushes = OddJobsStockCanvas.GeometryBrushes(StartJobConfig.Section?.Material ?? string.Empty);
            var fill = geomBrushes.EnvelopeFill;
            var edge = geomBrushes.EnvelopeEdge;
            double outside = OutsideReachMm(tp) * scale;

            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Line:
                    // Drawn as one thick round-capped line: that IS the slot the bit sweeps.
                    AddLine(center, tp, scale, fill, Math.Max(1d, LineHalfWidthMm(tp) * 2d * scale));
                    break;
                case WorkOrderGeometryKind.Circle:
                {
                    double r = Math.Max(tp.Diameter / 2d * scale + outside, HoleRadiusMm(tp) * scale);
                    AddEllipse(center, r, r, edge, 1d, fill);
                    break;
                }
                case WorkOrderGeometryKind.Oval:
                    AddEllipse(center, tp.Width / 2d * scale + outside, tp.Depth / 2d * scale + outside, edge, 1d, fill);
                    break;
                case WorkOrderGeometryKind.Square:
                    AddRect(center, tp.Size / 2d * scale + outside, tp.Size / 2d * scale + outside, edge, 1d, fill);
                    break;
                default:
                    AddRect(center, tp.Width / 2d * scale + outside, tp.Depth / 2d * scale + outside, edge, 1d, fill);
                    break;
            }
        }

        private void AddLine(Point center, WorkOrderToolpath tp, double scale, Brush stroke, double thickness)
        {
            double a = tp.Angle * Math.PI / 180d;
            double dx = Math.Cos(a) * tp.Length / 2d * scale, dy = Math.Sin(a) * tp.Length / 2d * scale;
            canvasDiagram.Children.Add(new Line
            {
                X1 = center.X - dx, Y1 = center.Y + dy,   // screen Y grows downward
                X2 = center.X + dx, Y2 = center.Y - dy,
                Stroke = stroke, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        private void AddEllipse(Point center, double rx, double ry, Brush stroke, double thickness, Brush fill)
        {
            var el = new Ellipse { Width = rx * 2, Height = ry * 2, Stroke = stroke, StrokeThickness = thickness, Fill = fill };
            Canvas.SetLeft(el, center.X - rx); Canvas.SetTop(el, center.Y - ry);
            canvasDiagram.Children.Add(el);
        }

        private void AddRect(Point center, double hw, double hh, Brush stroke, double thickness, Brush fill)
        {
            var r = new Rectangle { Width = hw * 2, Height = hh * 2, Stroke = stroke, StrokeThickness = thickness, Fill = fill };
            Canvas.SetLeft(r, center.X - hw); Canvas.SetTop(r, center.Y - hh);
            canvasDiagram.Children.Add(r);
        }

        #endregion

        #region Methods required by IGrblConfigTab

        public GrblConfigType GrblConfigType { get { return GrblConfigType.SurfaceSpoilboard; } }

        private bool isActiveTab = false;
        private ProgramView programView;

        private void EnsureProgramView()
        {
            if (programView == null)
                programView = new ProgramView { Title = "Work Order" };
        }

        public void Activate(bool activate)
        {
            isActiveTab = activate;
            if (activate)
            {
                MacroProcessor.SupportsGenerateMode = true;
                MacroProcessor.AllowRunModesWhenGenerated = true;
                MacroProcessor.ActiveGenerate = Generate;
                MacroProcessor.DiscardGenerated = DiscardProgram;
                MacroProcessor.IsProgramGenerated = !string.IsNullOrEmpty(program);
                UpdateValidation();
                if (!string.IsNullOrEmpty(program))
                {
                    EnsureProgramView();
                    programView.SetProgramText(program);
                    programView.Connect();
                }
                MacroProcessor.ActiveRun = Run;
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
                    // Default (off): today's unconditional prompt, unchanged - the safety net for anyone who
                    // never touches the new Settings:App > Odd Jobs toggle. On: save without asking, unless
                    // PromptBeforeAutoSaveWorkOrder also wants a confirm first (same prompt, just opt-in).
                    bool doSave;
                    if (!AppConfig.Settings.Base.AutoSaveWorkOrderOnExit)
                        doSave = AppDialogs.Show("This work order has unsaved changes. Save before leaving?",
                            "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    else if (AppConfig.Settings.Base.PromptBeforeAutoSaveWorkOrder)
                        doSave = AppDialogs.Show("Save these work order changes now?",
                            "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    else
                        doSave = true;

                    if (doSave)
                        btnSave_Click(null, null);
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

        private void Run()
        {
            if (model == null)
                return;
            if (string.IsNullOrWhiteSpace(program))
                Generate();
            if (string.IsNullOrWhiteSpace(program))
                return;

            MacroProcessor.Run(model, "Work Order", program, true);
        }

        private void Generate()
        {
            if (model == null)
                return;

            if (workOrder.Toolpaths.Count == 0)
            {
                AppDialogs.Show("Add at least one toolpath first.", "Work Order", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var warnings = WorkOrderRules.Validate(workOrder);
            warnings.AddRange(ParameterWarnings());
            if (warnings.Count > 0)
            {
                AppDialogs.Show("Fix these first:\n\n" + string.Join("\n", warnings), "Work Order", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            // The whole of the old Setup gate, reduced to one question at the one moment it matters. Everything
            // this tab emits is written in the G59 work frame and relies on the tool length reference the
            // toolsetter established - neither of which the app can re-verify without making the operator run
            // Setup again. So it states what it's trusting and lets them answer.
            if (AppDialogs.Show(
                    "This program will be built on the cached work origin (G59) and the current tool length reference.\n\n" +
                    "If the machine has been re-homed, the origin has moved, or the tool length reference has been cleared since they were set, run Setup again first.\n\n" +
                    "Proceed?",
                    "Work Order", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return;

            program = string.Join("\r\n", WorkOrderCompiler.BuildProgram(workOrder));
            MacroProcessor.PublishGenerated("Work Order", program, EnsureProgramView, () => programView);
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = true;
        }

        public static WorkOrder SectionConfig;

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

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = WorkOrderFilter,
                AddExtension = true,
                DefaultExt = ".workorder",
                InitialDirectory = WorkOrdersFolder(),
                FileName = SuggestedFileName()
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(WorkOrder));
                using (var writer = new System.IO.StreamWriter(dlg.FileName))
                    serializer.Serialize(writer, workOrder);
                currentFilePath = dlg.FileName;
                pendingName = null;
                AppConfig.Settings.Base.LastWorkOrderFilePath = currentFilePath;
                AppConfig.Settings.Base.LastWorkOrderName = null;
                AppConfig.Settings.Base.WorkOrderDirty = false;
                AppConfig.Settings.Save();
                UpdateTitleBar();
                AppDialogs.Show(string.Format("Work order saved to\n{0}", dlg.FileName), "Work order", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not save the work order:\n" + ex.Message, "Work order", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        // Clears the tab to a blank, untitled work order - the top-level command Load/Save were missing a
        // counterpart for. No name prompt: an untitled work order is dirty by default (currentFilePath == null
        // - see AppConfig.Base.WorkOrderDirty), so Activate(false) already asks for a save (and therefore a
        // name/file) once there's actually something in it worth naming, instead of demanding a name upfront
        // for what might stay empty.
        private void MenuNew_Click(object sender, RoutedEventArgs e)
        {
            if (workOrder.Toolpaths.Count > 0 &&
                AppDialogs.Show(string.Format("Discard the current work order ({0} toolpath{1}) and start a new one?",
                        workOrder.Toolpaths.Count, workOrder.Toolpaths.Count == 1 ? "" : "s"),
                    "Work order", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

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
        }

        // The tree panel's own title bar: the work order's name with no path or extension - "Toolpaths" said
        // nothing this panel doesn't already show by being full of toolpaths, where the name of the actual
        // job being composed is worth surfacing. Full path lives in the tooltip instead of cluttering the row.
        private void UpdateTitleBar()
        {
            if (currentFilePath != null)
            {
                txtWorkOrderName.Text = System.IO.Path.GetFileNameWithoutExtension(currentFilePath);
                txtWorkOrderName.ToolTip = currentFilePath;
            }
            else if (!string.IsNullOrEmpty(pendingName))
            {
                txtWorkOrderName.Text = pendingName;
                txtWorkOrderName.ToolTip = "Not yet saved";
            }
            else
            {
                txtWorkOrderName.Text = "(untitled)";
                txtWorkOrderName.ToolTip = "Not yet saved";
            }
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

            // Loading REPLACES what's on the tab, and the tab is auto-persisted, so an unsaved composition is
            // gone for good - worth a confirmation while there's something to lose.
            if (workOrder.Toolpaths.Count > 0 &&
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
            loadingFields = false;

            RebuildTree(workOrder.Toolpaths.FirstOrDefault());
            LoadFields();
            UpdateTitleBar();
            OnWorkOrderChanged();
            // Matches what's on disk right after loading it (OnWorkOrderChanged above set this true, same as
            // any other content change - override it back to false here).
            AppConfig.Settings.Base.WorkOrderDirty = false;
            AppConfig.Settings.Save();
        }

        #endregion

        protected override void OnConfigReady()
        {
            if (model == null)
                model = DataContext as GrblViewModel;
            loadingFields = true;
            chkGroupByTool.IsChecked = workOrder.GroupByTool;
            chkSkipFirstToolChange.IsChecked = workOrder.SkipFirstToolChange;
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
