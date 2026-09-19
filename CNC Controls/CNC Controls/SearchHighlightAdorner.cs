/*
 * SearchHighlightAdorner.cs - part of CNC Controls library
 *
 * Paints find-in-text highlights over a TextBox: a soft fill behind every match, a stronger one behind
 * the current match.
 *
 * Why an adorner rather than TextBox.Select():
 *
 *  - Select() marks ONE range, so the other matches are invisible. You could only find them by pressing
 *    F3 and watching the counter go up.
 *  - Even that one range often did not render. Focus stays in the search box while you type, so the
 *    scrollback never receives focus, and WPF's inactive-selection rendering is not dependable for a
 *    TextBox that has never been focused - IsInactiveSelectionHighlightEnabled notwithstanding.
 *  - The console rebuilds txtOutput.Text wholesale on a coalescing timer (ConsoleControl.FlushLog), and
 *    assigning Text drops the selection outright. With output arriving, any selection was gone inside
 *    250 ms.
 *
 * An adorner is immune to all three: it is our own drawing, it does not care about focus, and it survives
 * a Text reset because the owner re-supplies the offsets afterwards.
 *
 * COST. This draws only the matches on currently VISIBLE lines - bounded by GetFirstVisibleLineIndex /
 * GetLastVisibleLineIndex - so a query matching thousands of lines still costs a screenful of rectangles.
 * With no query it renders nothing at all, which is the normal state of the console: not searching pays
 * nothing. That matters here; this scrollback has a history of being the thing that saturates the UI
 * thread (see FlushLog's header).
 */

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CNC.Controls
{
    public class SearchHighlightAdorner : Adorner
    {
        private readonly TextBox owner;
        private readonly List<int> offsets = new List<int>();
        private int matchLength;
        private int currentIndex = -1;

        // Translucent so the text underneath stays readable - an adorner draws ON TOP of the control.
        private static readonly Brush AllMatchesFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xE0, 0x4A)));
        private static readonly Brush CurrentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x8A, 0x1E)));
        private static readonly Pen CurrentPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0xDD, 0xD8, 0x5A, 0x00))), 1d));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

        public SearchHighlightAdorner(TextBox adornedElement) : base(adornedElement)
        {
            owner = adornedElement;
            IsHitTestVisible = false;   // never swallow a click meant for the text underneath
        }

        /// <summary>
        /// Replace the highlighted set. Offsets are character indexes into the TextBox's current text;
        /// <paramref name="current"/> is an index INTO that list, or -1 for none.
        /// </summary>
        public void SetMatches(IEnumerable<int> matchOffsets, int length, int current)
        {
            offsets.Clear();
            if (matchOffsets != null && length > 0)
                offsets.AddRange(matchOffsets);
            matchLength = length;
            currentIndex = current;
            InvalidateVisual();
        }

        public void Clear()
        {
            if (offsets.Count == 0 && currentIndex < 0)
                return;
            offsets.Clear();
            currentIndex = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (offsets.Count == 0 || matchLength <= 0)
                return;

            int textLength = (owner.Text ?? string.Empty).Length;

            // Visible line window. A scrollback of thousands of lines must not cost thousands of rect
            // computations per repaint - and GetRectFromCharacterIndex is not cheap.
            int firstLine, lastLine;
            try
            {
                firstLine = owner.GetFirstVisibleLineIndex();
                lastLine = owner.GetLastVisibleLineIndex();
            }
            catch
            {
                return;   // no layout yet
            }
            if (firstLine < 0 || lastLine < firstLine)
                return;

            var clip = new Rect(0, 0, owner.ActualWidth, owner.ActualHeight);
            dc.PushClip(new RectangleGeometry(clip));

            for (int i = 0; i < offsets.Count; i++)
            {
                int start = offsets[i];
                if (start < 0 || start + matchLength > textLength)
                    continue;

                int line;
                try { line = owner.GetLineIndexFromCharacterIndex(start); }
                catch { continue; }
                if (line < firstLine || line > lastLine)
                    continue;

                Rect a, b;
                try
                {
                    // txtOutput is TextWrapping="NoWrap", so a match never spans two lines: the leading edge
                    // of its first character and the trailing edge of its last describe the whole run.
                    a = owner.GetRectFromCharacterIndex(start, false);
                    b = owner.GetRectFromCharacterIndex(start + matchLength, true);
                }
                catch { continue; }

                if (a.IsEmpty || b.IsEmpty || double.IsInfinity(a.X) || double.IsInfinity(b.X))
                    continue;

                double x = System.Math.Min(a.X, b.X);
                double w = System.Math.Abs(b.X - a.X);
                if (w <= 0d)
                    continue;

                var r = new Rect(x, a.Y, w, a.Height > 0 ? a.Height : b.Height);
                if (!r.IntersectsWith(clip))
                    continue;

                bool isCurrent = i == currentIndex;
                dc.DrawRectangle(isCurrent ? CurrentFill : AllMatchesFill, isCurrent ? CurrentPen : null, r);
            }

            dc.Pop();
        }
    }
}
