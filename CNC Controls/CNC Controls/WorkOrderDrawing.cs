/*
 * WorkOrderDrawing.cs - "Save Drawing": the Work Order stock diagram as a dimensioned PDF sheet.
 *
 * The sheet is the drawing plus a feature table, which is how a real shop drawing handles more than
 * a handful of features: a hole chart beats forty leader lines fighting each other. So the drawing
 * itself carries only the dimensions that have nowhere else to live - the stock's overall size, the
 * keep-out margin and the origin - and every feature's position, size, operations and tooling are
 * read off the table, keyed to the drawing by the (A)/(B)/(C) prefix on each toolpath's label.
 *
 * The drawing is NOT redrawn here. WorkOrderView.DrawInto builds it into an off-screen Canvas sized
 * to the paper - the identical code that paints the on-screen preview - and RenderCanvas below walks
 * those WPF shapes into PDF operators. That is the whole point of the arrangement: there is one
 * body of code that knows what a work order looks like, so the sheet taken to the machine cannot
 * describe a different arrangement from the one on screen.
 *
 * Every text column is measured with the PDF font's own widths and ellipsized to its column, because
 * a name overrunning into the next column is worse on paper than a truncated one - on screen you can
 * widen a pane, on paper you cannot.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNC.Core;

namespace CNC.Controls
{
    public static class WorkOrderDrawing
    {
        /// <summary>
        /// Draws the whole stock diagram into <paramref name="target"/> and returns the mm-to-pixel
        /// transform it used. <paramref name="labelPrefix"/> prefixes each toolpath's label, which is
        /// how the feature-table ID gets onto the drawing. Implemented by WorkOrderView.DrawInto.
        /// </summary>
        public delegate OddJobsStockCanvas.Transform DiagramDrawer(Canvas target, Func<WorkOrderToolpath, string> labelPrefix);

        // US Letter landscape. The sheet is laid out in PDF points throughout (1/72"), origin bottom-left.
        private const double PageW = 792d, PageH = 612d;
        private const double Margin = 26d;

        // Canvas pixels per PDF point when the diagram is drawn off-screen. Above 1 because the diagram
        // code sizes its labels and markers in fixed pixels (a 13 px toolpath label, a 5 px instance dot);
        // drawing into a canvas this much larger and scaling down on the way into the PDF lands them at a
        // drawing-sized 8 pt and 3 pt instead of shouting off the page.
        private const double CanvasPerPoint = 1.6d;

        // Room around the diagram for the dimension lines, which are drawn in PDF space (the canvas'
        // own 20 px stock margin is nowhere near enough for a dimension line and its text).
        private const double DimPadLeft = 34d, DimPadBottom = 30d, DimPadTop = 10d, DimPadRight = 12d;

        private static readonly double[] Black = { 0d, 0d, 0d };
        private static readonly double[] Grey = { 0.42d, 0.45d, 0.48d };
        private static readonly double[] Rule = { 0.72d, 0.75d, 0.78d };

        /// <summary>
        /// Writes the sheet. <paramref name="name"/> titles it - the work order's own name, or
        /// "(untitled)" for one that has never been saved.
        /// </summary>
        public static void Save(string path, WorkOrder wo, string name, DiagramDrawer draw)
        {
            var rows = BuildRows(wo);

            var doc = new PdfDocument();
            var page = doc.AddPage(PageW, PageH);

            double titleBottom = TitleBlock(page, wo, name, rows);
            double avail = titleBottom - Margin;

            // Sharing one page between the drawing and the table only works while the table is short.
            // Past about half the page it starts squeezing the drawing into a stamp - and it is going to
            // spill to a second page anyway - so at that point the drawing gets page 1 to itself and the
            // table starts clean on page 2. The drawing is the thing being taken to the machine; it is
            // not the part to shrink.
            bool tableSharesPage = TableHeight(rows) <= avail * 0.5d;
            double drawBottom = tableSharesPage ? Margin + TableHeight(rows) + 14d : Margin;

            Diagram(page, wo, draw, Margin, drawBottom, PageW - Margin, titleBottom - 6d);

            int start = tableSharesPage ? Table(page, rows, Margin, drawBottom - 14d, 0) : 0;
            while (start < rows.Count)
            {
                var cont = doc.AddPage(PageW, PageH);
                cont.FillColor(Black[0], Black[1], Black[2]);
                cont.Text(Margin, PageH - Margin - 12d, 12d, name, true);
                cont.FillColor(Grey[0], Grey[1], Grey[2]);
                cont.TextRight(PageW - Margin, PageH - Margin - 12d, 9d, "Feature schedule");
                cont.StrokeColor(Rule[0], Rule[1], Rule[2]);
                cont.LineWidth(0.6d);
                cont.Line(Margin, PageH - Margin - 20d, PageW - Margin, PageH - Margin - 20d);

                int next = Table(cont, rows, Margin, PageH - Margin - 26d, start);
                if (next == start)
                    break;   // a single row taller than a whole page cannot happen, but never spin on it
                start = next;
            }

            doc.Save(path);
        }

        // ---- title block -----------------------------------------------------------------------------

        /// <summary>Returns the Y the drawing may start at.</summary>
        private static double TitleBlock(PdfPage p, WorkOrder wo, string name, List<Row> rows)
        {
            double top = PageH - Margin;
            var s = StartJobConfig.Section;

            p.FillColor(Black[0], Black[1], Black[2]);
            p.Text(Margin, top - 13d, 14d, name, true);

            var facts = new List<string>();
            facts.Add(string.Format(CultureInfo.InvariantCulture, "Stock {0:0.#} × {1:0.#} × {2:0.#} mm",
                                    StockW(), StockD(), s != null ? s.Thickness : 0d));
            if (s != null && !string.IsNullOrEmpty(s.Material))
                facts.Add(s.Material);
            facts.Add("Origin " + WcsName(wo, s));
            facts.Add(string.Format(CultureInfo.InvariantCulture, "{0} toolpath{1}",
                                    wo.Toolpaths.Count, wo.Toolpaths.Count == 1 ? "" : "s"));

            p.FillColor(Grey[0], Grey[1], Grey[2]);
            p.Text(Margin, top - 25d, 8d, string.Join("   ·   ", facts.ToArray()));
            p.TextRight(PageW - Margin, top - 25d, 8d,
                        "ioSender  ·  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

            p.StrokeColor(Rule[0], Rule[1], Rule[2]);
            p.LineWidth(0.6d);
            p.Line(Margin, top - 32d, PageW - Margin, top - 32d);

            return top - 32d;
        }

        private static string WcsName(WorkOrder wo, StartJobSettings s)
        {
            // 0 = "follow Setup", which resolves to whatever Setup's own WCS is - printing "Follow Setup"
            // on a sheet someone reads next week would name no coordinate system at all, so resolve it.
            int wcs = wo.Wcs > 0 ? wo.Wcs : (s != null ? s.Wcs : 1);
            return "G" + (53 + Math.Max(1, wcs)).ToString(CultureInfo.InvariantCulture);
        }

        private static double StockW() { var s = StartJobConfig.Section; return s != null && s.Width > 0d ? s.Width : 100d; }
        private static double StockD() { var s = StartJobConfig.Section; return s != null && s.Height > 0d ? s.Height : 100d; }

        // ---- the drawing -----------------------------------------------------------------------------

        private static void Diagram(PdfPage p, WorkOrder wo, DiagramDrawer draw,
                                    double x0, double y0, double x1, double y1)
        {
            double boxX = x0 + DimPadLeft, boxY = y0 + DimPadBottom;
            double boxW = Math.Max(40d, x1 - DimPadRight - boxX), boxH = Math.Max(40d, y1 - DimPadTop - boxY);

            // Shape the off-screen canvas to the STOCK's aspect ratio rather than the page area's. The
            // diagram centres the stock in whatever canvas it is given, so a canvas of the wrong shape
            // spends the difference on empty margin INSIDE the drawing - where nothing can use it - and
            // shrinks the stock to boot. Matching the aspect here puts that space outside instead, where
            // centring the box turns it into an even margin.
            double aspect = StockW() / Math.Max(0.001d, StockD());
            double innerW = boxW, innerH = boxW / aspect;
            if (innerH > boxH)
            {
                innerH = boxH;
                innerW = boxH * aspect;
            }
            double innerX = boxX + (boxW - innerW) / 2d, innerY = boxY + (boxH - innerH) / 2d;

            var canvas = new Canvas { Width = innerW * CanvasPerPoint, Height = innerH * CanvasPerPoint };
            canvas.Measure(new Size(canvas.Width, canvas.Height));
            canvas.Arrange(new Rect(0d, 0d, canvas.Width, canvas.Height));

            var ids = Ids(wo);
            var t = draw(canvas, tp => { string id; return ids.TryGetValue(tp, out id) ? "(" + id + ") " : string.Empty; });

            // Canvas pixels -> points. Canvas Y grows DOWN, PDF Y grows UP, hence the flip about the top.
            Func<double, double> px = cx => innerX + cx / CanvasPerPoint;
            Func<double, double> py = cy => innerY + innerH - cy / CanvasPerPoint;

            p.Save();
            p.ClipRect(x0, y0, x1 - x0, y1 - y0);
            RenderCanvas(p, canvas, px, py, 1d / CanvasPerPoint);
            p.Restore();

            // The stock's own footprint in points, from the transform the diagram actually used - so the
            // dimension lines measure the rectangle that got drawn, not a separately computed one.
            double sx0 = px(t.OriginX), sy0 = py(t.OriginY);
            double sx1 = px(t.OriginX + StockW() * t.Scale), sy1 = py(t.OriginY - StockD() * t.Scale);

            p.StrokeColor(Black[0], Black[1], Black[2]);
            p.FillColor(Black[0], Black[1], Black[2]);
            p.LineWidth(0.5d);

            HorizontalDim(p, sx0, sx1, sy0 - 18d, sy0, Num(StockW()));
            VerticalDim(p, sy0, sy1, sx0 - 20d, sx0, Num(StockD()));

            // The origin is the one point on the sheet every number is measured from, so it gets said
            // outright rather than left as the orange dot the preview marks it with.
            p.FillColor(0.85d, 0.25d, 0.1d);
            p.Text(sx0 + 4d, sy0 - 9.5d, 6.5d, "X0 Y0  (" + CornerName() + ")", true);

            double inset = OddJobsStockCanvas.KeepOutInset();
            if (inset > 0d)
            {
                // Inside the top edge, where it names the dotted rectangle it belongs to. Outside the
                // stock it read as a note about the sheet rather than a line on the drawing.
                p.FillColor(0.75d, 0.15d, 0.15d);
                p.TextRight(sx1 - 4d, sy1 - 9d, 6.5d, "keep-out " + Num(inset) + " mm");
            }
        }

        private static string CornerName()
        {
            var s = StartJobConfig.Section;
            string c = s != null ? s.Corner : "FrontLeft";
            switch (c)
            {
                case "FrontRight": return "front right";
                case "BackLeft": return "back left";
                case "BackRight": return "back right";
                default: return "front left";
            }
        }

        private static string Num(double v) { return v.ToString("0.0#", CultureInfo.InvariantCulture); }

        // ---- dimension lines -------------------------------------------------------------------------

        private const double ArrowLen = 6d, ArrowHalf = 1.9d;

        private static void Arrow(PdfPage p, double x, double y, double dx, double dy)
        {
            p.MoveTo(x, y);
            p.LineTo(x + dx * ArrowLen - dy * ArrowHalf, y + dy * ArrowLen + dx * ArrowHalf);
            p.LineTo(x + dx * ArrowLen + dy * ArrowHalf, y + dy * ArrowLen - dx * ArrowHalf);
            p.ClosePath();
            p.Fill(false);
        }

        /// <summary>Dimension line at <paramref name="y"/>, with extension lines back up to the feature.</summary>
        private static void HorizontalDim(PdfPage p, double xa, double xb, double y, double featureY, string text)
        {
            p.Line(xa, y - 3d, xa, featureY);
            p.Line(xb, y - 3d, xb, featureY);
            p.Line(xa, y, xb, y);
            Arrow(p, xa, y, 1d, 0d);
            Arrow(p, xb, y, -1d, 0d);

            // The text sits on a cleared patch of the dimension line rather than above it - centred over
            // the line it would collide with the stock outline on a shallow drawing.
            string s = text;
            double w = p.TextWidth(s, 7.5d) + 6d, cx = (xa + xb) / 2d;
            p.Save();
            p.FillColor(1d, 1d, 1d);
            p.Rect(cx - w / 2d, y - 3d, w, 8.5d);
            p.Fill(false);
            p.Restore();
            p.TextCentered(cx, y - 2.6d, 7.5d, s);
        }

        private static void VerticalDim(PdfPage p, double ya, double yb, double x, double featureX, string text)
        {
            p.Line(x - 3d, ya, featureX, ya);
            p.Line(x - 3d, yb, featureX, yb);
            p.Line(x, ya, x, yb);
            Arrow(p, ya < yb ? x : x, ya, 0d, ya < yb ? 1d : -1d);
            Arrow(p, x, yb, 0d, ya < yb ? -1d : 1d);

            string s = text;
            double h = p.TextWidth(s, 7.5d) + 6d, cy = (ya + yb) / 2d;
            p.Save();
            p.FillColor(1d, 1d, 1d);
            p.Rect(x - 3d, cy - h / 2d, 8.5d, h);
            p.Fill(false);
            p.Restore();
            p.TextRotatedCentered(x - 2.6d, cy, 90d, 7.5d, s);
        }

        // ---- WPF canvas -> PDF -------------------------------------------------------------------------

        /// <summary>
        /// Emits every shape the diagram put on the canvas. Handles exactly what WorkOrderView.DrawInto
        /// and OddJobsStockCanvas.DrawStock produce - Rectangle, Ellipse, Line, Polyline, Path and
        /// TextBlock - and skips anything else rather than guessing, so a new shape kind shows up as a
        /// missing feature on the sheet instead of a wrong one.
        /// </summary>
        private static void RenderCanvas(PdfPage p, Canvas canvas, Func<double, double> px, Func<double, double> py, double k)
        {
            foreach (UIElement el in canvas.Children)
            {
                var rect = el as Rectangle;
                if (rect != null)
                {
                    double l = Canvas.GetLeft(rect), t = Canvas.GetTop(rect);
                    p.Rect(px(l), py(t + rect.Height), rect.Width * k, rect.Height * k);
                    Paint(p, rect.Fill, rect.Stroke, rect.StrokeThickness * k, rect.StrokeDashArray, false);
                    continue;
                }

                var ell = el as Ellipse;
                if (ell != null)
                {
                    double l = Canvas.GetLeft(ell), t = Canvas.GetTop(ell);
                    p.Ellipse(px(l + ell.Width / 2d), py(t + ell.Height / 2d), ell.Width / 2d * k, ell.Height / 2d * k);
                    Paint(p, ell.Fill, ell.Stroke, ell.StrokeThickness * k, ell.StrokeDashArray, false);
                    continue;
                }

                var line = el as Line;
                if (line != null)
                {
                    p.MoveTo(px(line.X1), py(line.Y1));
                    p.LineTo(px(line.X2), py(line.Y2));
                    Caps(p, line.StrokeStartLineCap);
                    Paint(p, null, line.Stroke, line.StrokeThickness * k, line.StrokeDashArray, false);
                    p.ButtCaps();
                    continue;
                }

                var poly = el as Polyline;
                if (poly != null)
                {
                    if (poly.Points.Count < 2)
                        continue;
                    p.MoveTo(px(poly.Points[0].X), py(poly.Points[0].Y));
                    for (int i = 1; i < poly.Points.Count; i++)
                        p.LineTo(px(poly.Points[i].X), py(poly.Points[i].Y));
                    Caps(p, poly.StrokeStartLineCap);
                    Paint(p, null, poly.Stroke, poly.StrokeThickness * k, poly.StrokeDashArray, false);
                    p.ButtCaps();
                    continue;
                }

                var path = el as System.Windows.Shapes.Path;
                if (path != null)
                {
                    // Carved text and SVG artwork: closed polygonal figures, filled even-odd so a
                    // counter (the hole in an O) punches out exactly as the carve engine's own inside
                    // test treats it. DrawInto builds these with line segments only.
                    var geo = path.Data as PathGeometry;
                    if (geo == null)
                        continue;
                    bool any = false;
                    foreach (var fig in geo.Figures)
                    {
                        p.MoveTo(px(fig.StartPoint.X), py(fig.StartPoint.Y));
                        foreach (var seg in fig.Segments)
                        {
                            var ls = seg as LineSegment;
                            if (ls != null)
                                p.LineTo(px(ls.Point.X), py(ls.Point.Y));
                        }
                        p.ClosePath();
                        any = true;
                    }
                    if (any)
                        Paint(p, path.Fill, path.Stroke, path.StrokeThickness * k, null, geo.FillRule == FillRule.EvenOdd);
                    else
                        p.EndPath();
                    continue;
                }

                var text = el as TextBlock;
                if (text != null)
                {
                    if (string.IsNullOrEmpty(text.Text))
                        continue;
                    // Every TextBlock the diagram emits is a toolpath label, positioned centred on its
                    // anchor using WPF's own measurement. Re-centre on the same midpoint using the PDF
                    // font's width, because Helvetica is not the font WPF measured.
                    double l = Canvas.GetLeft(text), t = Canvas.GetTop(text);
                    double cx = l + text.DesiredSize.Width / 2d;
                    double baseline = text.BaselineOffset;
                    if (double.IsNaN(baseline) || baseline <= 0d)
                        baseline = text.FontSize * 0.8d;
                    SetFill(p, text.Foreground, Black);
                    p.TextCentered(px(cx), py(t + baseline), text.FontSize * k, text.Text,
                                   text.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight());
                    continue;
                }
            }
        }

        private static void Caps(PdfPage p, PenLineCap cap)
        {
            if (cap == PenLineCap.Round)
                p.RoundCaps();
            else
                p.ButtCaps();
        }

        /// <summary>
        /// Closes the path just built with the right painting operator for the brushes it was given.
        /// A path with neither a fill nor a stroke is still consumed (EndPath) - leaving it open would
        /// silently merge into whatever shape is emitted next.
        /// </summary>
        private static void Paint(PdfPage p, Brush fill, Brush stroke, double width, DoubleCollection dash, bool evenOdd)
        {
            bool hasFill = SetFill(p, fill, null);
            bool hasStroke = stroke != null && width > 0d;

            if (hasStroke)
            {
                SetStroke(p, stroke);
                p.LineWidth(Math.Max(0.25d, width));
                if (dash != null && dash.Count > 0)
                    p.Dash(dash.Select(d => Math.Max(0.4d, d * Math.Max(0.25d, width))).ToArray());
                else
                    p.Dash();
            }

            if (hasFill && hasStroke) p.FillAndStroke(evenOdd);
            else if (hasFill) p.Fill(evenOdd);
            else if (hasStroke) p.Stroke();
            else p.EndPath();

            if (hasStroke)
                p.Dash();
        }

        /// <summary>Returns whether a fill colour was actually set (a null or transparent brush is no fill).</summary>
        private static bool SetFill(PdfPage p, Brush b, double[] fallback)
        {
            var c = ColorOf(b);
            if (c == null)
            {
                if (fallback == null)
                    return false;
                p.FillColor(fallback[0], fallback[1], fallback[2]);
                return true;
            }
            p.FillColor(c[0], c[1], c[2]);
            return true;
        }

        private static void SetStroke(PdfPage p, Brush b)
        {
            var c = ColorOf(b) ?? Black;
            p.StrokeColor(c[0], c[1], c[2]);
        }

        /// <summary>
        /// A brush as r/g/b in 0..1, or null when there is nothing to paint. A translucent brush is
        /// composited onto white here rather than made transparent in the PDF: the envelope footprints
        /// are drawn translucent precisely so the outline stays readable through them, and flattening
        /// keeps that look without the sheet needing a transparency group.
        /// </summary>
        private static double[] ColorOf(Brush b)
        {
            var sb = b as SolidColorBrush;
            if (sb == null)
                return null;
            var c = sb.Color;
            double a = c.A / 255d * sb.Opacity;
            if (a <= 0.004d)
                return null;
            return new[] { 1d - a + c.R / 255d * a, 1d - a + c.G / 255d * a, 1d - a + c.B / 255d * a };
        }

        // ---- feature table ---------------------------------------------------------------------------

        private class Row
        {
            public bool IsOperation;
            public bool Dimmed;              // held back / disabled: shown, but visibly not part of the cut
            public string Id, Name, Detail, X, Y, Qty, Tool;
        }

        private const double RowFeature = 12.5d, RowOp = 10d, HeaderH = 15d;
        private const double FontFeature = 8d, FontOp = 7d;

        // Column geometry, in points from the sheet's left margin. Left-aligned columns give an x to
        // start at; the numeric ones give an x to end at, so their digits line up.
        private const double ColId = 32d, ColName = 54d, ColGeom = 208d;
        private const double ColXr = 466d, ColYr = 512d, ColQtyr = 544d, ColTool = 554d, ColEnd = 766d;
        private const double OpIndent = 64d, OpDetailEnd = 458d;

        private static List<Row> BuildRows(WorkOrder wo)
        {
            var rows = new List<Row>();
            var ids = Ids(wo);

            foreach (var tp in wo.Toolpaths)
            {
                // The count the sheet prints is what will actually be CUT, which for an Indirect toolpath
                // is its source's pattern, not its own - the same resolution WorkOrderRules.Summarize
                // makes for the tree, reused so the two cannot disagree about the same toolpath.
                var placements = WorkOrderRules.Expand(wo, tp);
                int qty = placements.Sum(pl => pl.Geometry.PatternPositions(pl.X, pl.Y).Count());
                var at = WorkOrderRules.ResolvedCenter(wo, tp);
                bool live = tp.Enabled;

                rows.Add(new Row
                {
                    Id = ids[tp],
                    Name = tp.Name + (live ? string.Empty : "   (held back)"),
                    Detail = WorkOrderRules.DescribeGeometry(tp),
                    X = at[0].ToString("0.0#", CultureInfo.InvariantCulture),
                    Y = at[1].ToString("0.0#", CultureInfo.InvariantCulture),
                    Qty = qty > 1 ? qty.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    Dimmed = !live
                });

                // An Indirect toolpath runs its SOURCE's operations, so list those - naming the source,
                // because they are not this toolpath's to edit.
                var opOwner = WorkOrderRules.ResolveIndirectSource(wo, tp) ?? tp;
                foreach (var op in opOwner.Operations)
                    rows.Add(new Row
                    {
                        IsOperation = true,
                        Name = WorkOrderRules.Summarize(op) + (op.Enabled ? string.Empty : "   (off)"),
                        Tool = ToolText(op),
                        Dimmed = !live || !op.Enabled
                    });

                if (opOwner.Operations.Count == 0)
                    rows.Add(new Row { IsOperation = true, Name = "no operations - cuts nothing", Dimmed = true });
            }

            return rows;
        }

        private static string ToolText(WorkOrderOperation op)
        {
            string t = "T" + op.Tool.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(op.ToolName))
                t += " " + op.ToolName;
            return string.Format(CultureInfo.InvariantCulture, "{0}  ·  F{1:0} · S{2:0}", t, op.Feed, op.SpindleRPM);
        }

        /// <summary>A, B, ... Z, AA, AB - the drawing label and the table row are keyed by this.</summary>
        private static Dictionary<WorkOrderToolpath, string> Ids(WorkOrder wo)
        {
            var d = new Dictionary<WorkOrderToolpath, string>();
            for (int i = 0; i < wo.Toolpaths.Count; i++)
            {
                string s = string.Empty;
                for (int n = i; ; n = n / 26 - 1)
                {
                    s = (char)('A' + n % 26) + s;
                    if (n < 26)
                        break;
                }
                d[wo.Toolpaths[i]] = s;
            }
            return d;
        }

        private static double TableHeight(List<Row> rows)
        {
            double h = HeaderH;
            foreach (var r in rows)
                h += r.IsOperation ? RowOp : RowFeature;
            return h;
        }

        /// <summary>
        /// The first row that will not fit below <paramref name="top"/>, decided BEFORE anything is
        /// drawn - a PDF content stream is append-only, so a row emitted and then reconsidered is a row
        /// printed twice.
        ///
        /// A feature and its operations are one thing to read ("what does C actually do"), so the break
        /// never lands between them: operation rows stranded at the foot of a page are pushed over to
        /// print under their own feature. Backing up is refused when it would give up the whole page,
        /// which is what stops a feature with more operations than fit anywhere from spinning forever.
        /// </summary>
        private static int BreakAt(List<Row> rows, double top, int from)
        {
            double cursor = top - HeaderH;
            int i = from;
            for (; i < rows.Count; i++)
            {
                double h = rows[i].IsOperation ? RowOp : RowFeature;
                if (cursor - h < Margin)
                    break;
                cursor -= h;
            }

            if (i < rows.Count && rows[i].IsOperation)
            {
                int j = i;
                while (j > from && rows[j].IsOperation)
                    j--;
                if (j > from)
                    i = j;
            }

            return i;
        }

        /// <summary>
        /// Draws rows from <paramref name="from"/> downward starting at <paramref name="top"/>, and
        /// returns the index of the first row that would not fit - equal to rows.Count when all fitted.
        /// </summary>
        private static int Table(PdfPage p, List<Row> rows, double left, double top, int from)
        {
            int end = BreakAt(rows, top, from);
            if (end == from)
                return from;

            double y = top - HeaderH + 4d;

            p.FillColor(Black[0], Black[1], Black[2]);
            p.Text(ColId, y, 7d, "ID", true);
            p.Text(ColName, y, 7d, from == 0 ? "Feature / operation" : "Feature / operation (continued)", true);
            p.Text(ColGeom, y, 7d, "Geometry", true);
            p.TextRight(ColXr, y, 7d, "X mm", true);
            p.TextRight(ColYr, y, 7d, "Y mm", true);
            p.TextRight(ColQtyr, y, 7d, "Qty", true);
            p.Text(ColTool, y, 7d, "Tool, feed, speed", true);

            p.StrokeColor(Black[0], Black[1], Black[2]);
            p.LineWidth(0.7d);
            p.Line(left, top - HeaderH, ColEnd, top - HeaderH);

            double cursor = top - HeaderH;
            for (int i = from; i < end; i++)
            {
                var r = rows[i];
                double h = r.IsOperation ? RowOp : RowFeature;
                double baseline = cursor - h + 2.5d;
                var col = r.Dimmed ? Grey : Black;
                p.FillColor(col[0], col[1], col[2]);

                if (r.IsOperation)
                {
                    p.Text(OpIndent, baseline, FontOp, p.Ellipsize(r.Name, OpDetailEnd - OpIndent, FontOp));
                    if (!string.IsNullOrEmpty(r.Tool))
                        p.Text(ColTool, baseline, FontOp, p.Ellipsize(r.Tool, ColEnd - ColTool, FontOp));
                }
                else
                {
                    // A hairline above each feature groups it with its own operations, which is the
                    // reading the table has to support: "what does feature C actually do".
                    if (i > from)
                    {
                        p.StrokeColor(Rule[0], Rule[1], Rule[2]);
                        p.LineWidth(0.4d);
                        p.Line(left, cursor, ColEnd, cursor);
                        p.FillColor(col[0], col[1], col[2]);
                    }
                    p.Text(ColId, baseline, FontFeature, r.Id, true);
                    p.Text(ColName, baseline, FontFeature, p.Ellipsize(r.Name, ColGeom - ColName - 6d, FontFeature, true), true);
                    p.Text(ColGeom, baseline, FontFeature, p.Ellipsize(r.Detail, ColXr - ColGeom - 26d, FontFeature));
                    p.TextRight(ColXr, baseline, FontFeature, r.X);
                    p.TextRight(ColYr, baseline, FontFeature, r.Y);
                    p.TextRight(ColQtyr, baseline, FontFeature, r.Qty);
                }

                cursor -= h;
            }

            return end;
        }
    }
}
