/*
 * PdfDocument.cs - a minimal, dependency-free PDF writer.
 *
 * v1.0 / 2026-08-26
 *
 * Written for Work Order's "Save Drawing" (WorkOrderDrawing.cs), which needs to put vector lines,
 * filled outlines and text on a page. That is the whole of what PDF is asked for here, so this
 * implements exactly that and nothing else: no images, no transparency, no embedded fonts.
 *
 * Why hand-rolled rather than a library: CNC.Core carries no NuGet dependencies at all and is
 * compiled against net8.0 as a portability probe (CNC.Core.net8.csproj). A PDF content stream for
 * paths and text is a handful of postfix operators, and the base-14 fonts (Helvetica here) are
 * guaranteed present in every viewer with no font file to embed - so the whole cost is the widths
 * table below, which is what lets text be centred and right-aligned.
 *
 * Coordinates are PDF's own: points (1/72"), origin at the BOTTOM-left, +Y up. Callers working in
 * a screen-style top-down space flip on the way in - see WorkOrderDrawing.
 *
 * Everything WRITTEN is 7-bit ASCII: text strings are escaped to octal on the way out, so the byte
 * offsets the xref table records cannot be disturbed by an encoding choice further up. (This source
 * file itself is UTF-8 WITH a BOM, as the repo's other non-ASCII sources are - the widths table
 * below names its characters literally, and a BOM-less file with high bytes is read in the system
 * codepage by some compilers.)
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CNC.Core
{
    public class PdfDocument
    {
        private readonly List<PdfPage> pages = new List<PdfPage>();

        public PdfPage AddPage(double widthPt, double heightPt)
        {
            var p = new PdfPage(widthPt, heightPt);
            pages.Add(p);
            return p;
        }

        /// <summary>US Letter landscape, the default sheet - 11 x 8.5 inches.</summary>
        public PdfPage AddLetterLandscape() { return AddPage(792d, 612d); }

        public void Save(string path) { File.WriteAllBytes(path, ToBytes()); }

        public byte[] ToBytes()
        {
            // Object numbering, fixed up front so /Parent and /Contents references can be written
            // before the objects they name exist:
            //   1            catalog
            //   2            page tree
            //   3, 4         Helvetica, Helvetica-Bold
            //   5 + 2i       page i
            //   6 + 2i       page i's content stream
            var body = new MemoryStream();
            var offsets = new List<long> { 0 };   // index 0 is the free-list head, never a real object

            Action<string> put = s =>
            {
                var b = Encoding.ASCII.GetBytes(s);
                body.Write(b, 0, b.Length);
            };
            Action<int, string> obj = (n, s) =>
            {
                while (offsets.Count <= n)
                    offsets.Add(0);
                offsets[n] = body.Position;
                put(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + s + "\nendobj\n");
            };

            // The binary comment on line 2 is the convention that marks the file as non-text, so a
            // transfer that would "helpfully" translate line endings is told not to.
            put("%PDF-1.4\n");
            body.WriteByte(0x25); body.WriteByte(0xE2); body.WriteByte(0xE3); body.WriteByte(0xCF); body.WriteByte(0xD3);
            body.WriteByte(0x0A);

            var kids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
                kids.Append(i == 0 ? "" : " ").Append(5 + 2 * i).Append(" 0 R");

            obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
            obj(2, "<< /Type /Pages /Kids [" + kids + "] /Count " + pages.Count + " >>");
            obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

            for (int i = 0; i < pages.Count; i++)
            {
                int pageNo = 5 + 2 * i, streamNo = 6 + 2 * i;
                obj(pageNo,
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + N(pages[i].Width) + " " + N(pages[i].Height) + "]" +
                    " /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents " + streamNo + " 0 R >>");

                var content = Encoding.ASCII.GetBytes(pages[i].Content);
                while (offsets.Count <= streamNo)
                    offsets.Add(0);
                offsets[streamNo] = body.Position;
                put(streamNo.ToString(CultureInfo.InvariantCulture) + " 0 obj\n<< /Length " + content.Length + " >>\nstream\n");
                body.Write(content, 0, content.Length);
                put("\nendstream\nendobj\n");
            }

            long xref = body.Position;
            var x = new StringBuilder();
            x.Append("xref\n0 ").Append(offsets.Count).Append("\n0000000000 65535 f \n");
            for (int i = 1; i < offsets.Count; i++)
                x.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            x.Append("trailer\n<< /Size ").Append(offsets.Count).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            put(x.ToString());

            return body.ToArray();
        }

        internal static string N(double v)
        {
            // PDF wants a plain decimal - no exponent, no thousands separator, no culture comma.
            if (Math.Abs(v) < 0.0005d)
                return "0";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// One page's content stream, built up by the drawing calls. Paths follow PDF's own model: a
    /// sequence of MoveTo/LineTo/CurveTo/Close construction operators, then exactly one painting
    /// operator (Stroke/Fill/FillAndStroke) which also clears the path.
    /// </summary>
    public class PdfPage
    {
        public readonly double Width, Height;
        private readonly StringBuilder sb = new StringBuilder();

        internal PdfPage(double w, double h) { Width = w; Height = h; }

        internal string Content { get { return sb.ToString(); } }

        private static string N(double v) { return PdfDocument.N(v); }

        public void Save() { sb.Append("q\n"); }
        public void Restore() { sb.Append("Q\n"); }

        public void StrokeColor(double r, double g, double b) { sb.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" RG\n"); }
        public void FillColor(double r, double g, double b) { sb.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg\n"); }
        public void LineWidth(double pt) { sb.Append(N(pt)).Append(" w\n"); }

        /// <summary>Round caps and joins, so a polyline drawn as a cut path reads like the cut does.</summary>
        public void RoundCaps() { sb.Append("1 J 1 j\n"); }
        public void ButtCaps() { sb.Append("0 J 0 j\n"); }

        /// <summary>No argument (or an empty pattern) clears the dash back to solid.</summary>
        public void Dash(params double[] pattern)
        {
            if (pattern == null || pattern.Length == 0) { sb.Append("[] 0 d\n"); return; }
            sb.Append('[');
            for (int i = 0; i < pattern.Length; i++)
                sb.Append(i == 0 ? "" : " ").Append(N(pattern[i]));
            sb.Append("] 0 d\n");
        }

        public void MoveTo(double x, double y) { sb.Append(N(x)).Append(' ').Append(N(y)).Append(" m\n"); }
        public void LineTo(double x, double y) { sb.Append(N(x)).Append(' ').Append(N(y)).Append(" l\n"); }
        public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            sb.Append(N(x1)).Append(' ').Append(N(y1)).Append(' ').Append(N(x2)).Append(' ').Append(N(y2))
              .Append(' ').Append(N(x3)).Append(' ').Append(N(y3)).Append(" c\n");
        }
        public void ClosePath() { sb.Append("h\n"); }

        public void Stroke() { sb.Append("S\n"); }
        public void Fill(bool evenOdd) { sb.Append(evenOdd ? "f*\n" : "f\n"); }
        public void FillAndStroke(bool evenOdd) { sb.Append(evenOdd ? "B*\n" : "B\n"); }
        public void EndPath() { sb.Append("n\n"); }

        public void Line(double x1, double y1, double x2, double y2) { MoveTo(x1, y1); LineTo(x2, y2); Stroke(); }

        /// <summary>x/y is the LOWER-left corner, as PDF's own 're' operator takes it.</summary>
        public void Rect(double x, double y, double w, double h)
        {
            sb.Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(w)).Append(' ').Append(N(h)).Append(" re\n");
        }

        /// <summary>
        /// An ellipse as four cubic Beziers. 0.5523 is the standard circle-from-Bezier constant
        /// (4/3 * (sqrt(2) - 1)) - the error against a true circle is under 0.02% of the radius,
        /// which at drawing scale is well under the width of the line drawing it.
        /// </summary>
        public void Ellipse(double cx, double cy, double rx, double ry)
        {
            const double k = 0.5522847498d;
            double ox = rx * k, oy = ry * k;
            MoveTo(cx - rx, cy);
            CurveTo(cx - rx, cy + oy, cx - ox, cy + ry, cx, cy + ry);
            CurveTo(cx + ox, cy + ry, cx + rx, cy + oy, cx + rx, cy);
            CurveTo(cx + rx, cy - oy, cx + ox, cy - ry, cx, cy - ry);
            CurveTo(cx - ox, cy - ry, cx - rx, cy - oy, cx - rx, cy);
            ClosePath();
        }

        public void ClipRect(double x, double y, double w, double h)
        {
            Rect(x, y, w, h);
            sb.Append("W n\n");
        }

        // ---- text -------------------------------------------------------------------------------

        /// <summary>Baseline-left text.</summary>
        public void Text(double x, double y, double size, string s, bool bold = false)
        {
            if (string.IsNullOrEmpty(s))
                return;
            sb.Append("BT /").Append(bold ? "F2 " : "F1 ").Append(N(size)).Append(" Tf ")
              .Append(N(x)).Append(' ').Append(N(y)).Append(" Td ").Append(Escape(s)).Append(" Tj ET\n");
        }

        public void TextCentered(double cx, double y, double size, string s, bool bold = false)
        {
            Text(cx - TextWidth(s, size, bold) / 2d, y, size, s, bold);
        }

        public void TextRight(double rx, double y, double size, string s, bool bold = false)
        {
            Text(rx - TextWidth(s, size, bold), y, size, s, bold);
        }

        /// <summary>Baseline-left text rotated counter-clockwise by <paramref name="degrees"/> about (x,y).</summary>
        public void TextRotated(double x, double y, double degrees, double size, string s, bool bold = false)
        {
            if (string.IsNullOrEmpty(s))
                return;
            double r = degrees * Math.PI / 180d, c = Math.Cos(r), n = Math.Sin(r);
            sb.Append("BT /").Append(bold ? "F2 " : "F1 ").Append(N(size)).Append(" Tf ")
              .Append(N(c)).Append(' ').Append(N(n)).Append(' ').Append(N(-n)).Append(' ').Append(N(c)).Append(' ')
              .Append(N(x)).Append(' ').Append(N(y)).Append(" Tm ").Append(Escape(s)).Append(" Tj ET\n");
        }

        /// <summary>Rotated text centred on its own baseline midpoint - for a vertical dimension.</summary>
        public void TextRotatedCentered(double cx, double cy, double degrees, double size, string s, bool bold = false)
        {
            double half = TextWidth(s, size, bold) / 2d;
            double r = degrees * Math.PI / 180d;
            TextRotated(cx - Math.Cos(r) * half, cy - Math.Sin(r) * half, degrees, size, s, bold);
        }

        public double TextWidth(string s, double size, bool bold = false)
        {
            if (string.IsNullOrEmpty(s))
                return 0d;
            double w = 0d;
            foreach (char c in s)
                w += Widths.Of(c, bold);
            return w * size / 1000d;
        }

        /// <summary>
        /// Trims to fit, appending an ellipsis - a name that ran into the next table column would be
        /// worse than a truncated one, because the drawing is the thing being taken to the machine.
        /// </summary>
        public string Ellipsize(string s, double maxWidth, double size, bool bold = false)
        {
            if (string.IsNullOrEmpty(s) || TextWidth(s, size, bold) <= maxWidth)
                return s;
            for (int n = s.Length - 1; n > 0; n--)
            {
                string t = s.Substring(0, n) + "...";
                if (TextWidth(t, size, bold) <= maxWidth)
                    return t;
            }
            return string.Empty;
        }

        /// <summary>
        /// A PDF literal string. Anything outside printable ASCII goes out as a 3-digit octal escape,
        /// which keeps the whole file 7-bit and makes the byte offsets in the xref table independent
        /// of how the .NET string was encoded. Characters WinAnsi cannot represent become '?'.
        /// </summary>
        private static string Escape(string s)
        {
            var b = new StringBuilder("(");
            foreach (char ch in s)
            {
                if (ch == '(' || ch == ')' || ch == '\\')
                    b.Append('\\').Append(ch);
                else if (ch >= ' ' && ch <= '~')
                    b.Append(ch);
                else
                    b.Append('\\').Append(Convert.ToString(Widths.ToWinAnsi(ch), 8).PadLeft(3, '0'));
            }
            return b.Append(')').ToString();
        }
    }

    /// <summary>
    /// Helvetica advance widths, in 1/1000 em, for the printable ASCII range - the numbers straight
    /// out of the Adobe AFM files that every viewer's built-in Helvetica matches.
    ///
    /// Latin-1 accented letters are not tabulated: in Helvetica an accented letter has exactly its
    /// base letter's advance (Aacute is A's width, ntilde is n's), so they fold onto the base letter
    /// instead - correct, and it keeps the table to something readable.
    /// </summary>
    internal static class Widths
    {
        private const char Oslash = 'Ø';      // the diameter sign, as drawings write it
        private const char Multiply = '×';
        private const char Degree = '°';
        private const char PlusMinus = '±';
        private const char Nbsp = ' ';

        // ASCII 32..126.
        private static readonly short[] Regular = {
            278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
            1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,
            667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
            333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,
            556,556,333,500,278,556,500,722,500,500,500,334,260,334,584
        };
        private static readonly short[] Bold = {
            278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
            975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,
            667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
            333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,
            611,611,389,556,333,611,556,778,556,556,500,389,280,389,584
        };

        internal static double Of(char c, bool bold)
        {
            var t = bold ? Bold : Regular;
            if (c >= ' ' && c <= '~')
                return t[c - ' '];

            switch (c)
            {
                case Oslash: return 778;
                case Multiply: return 584;
                case Degree: return 400;
                case PlusMinus: return 584;
                case Nbsp: return t[0];
                case '‘': case '’': return bold ? 278 : 222;   // curly single quotes
                case '“': case '”': return bold ? 500 : 333;   // curly double quotes
                case '–': return 556;                               // en dash
                case '—': return 1000;                              // em dash
                case '…': return 1000;                              // ellipsis
            }

            char b = BaseLetter(c);
            return b >= ' ' && b <= '~' ? t[b - ' '] : t['n' - ' '];
        }

        /// <summary>The WinAnsi code point to escape this char as, or '?' where there isn't one.</summary>
        internal static int ToWinAnsi(char c)
        {
            if (c >= 0xA0 && c <= 0xFF)
                return c;                       // the Latin-1 upper half IS WinAnsi over that range
            switch (c)
            {
                case '€': return 0x80;     // euro
                case '‚': return 0x82;
                case 'ƒ': return 0x83;
                case '„': return 0x84;
                case '…': return 0x85;     // ellipsis
                case '†': return 0x86;
                case '‡': return 0x87;
                case 'ˆ': return 0x88;
                case '‰': return 0x89;
                case 'Š': return 0x8A;
                case '‹': return 0x8B;
                case 'Œ': return 0x8C;
                case 'Ž': return 0x8E;
                case '‘': return 0x91;
                case '’': return 0x92;
                case '“': return 0x93;
                case '”': return 0x94;
                case '•': return 0x95;     // bullet
                case '–': return 0x96;     // en dash
                case '—': return 0x97;     // em dash
                case '˜': return 0x98;
                case '™': return 0x99;
                case 'š': return 0x9A;
                case '›': return 0x9B;
                case 'œ': return 0x9C;
                case 'ž': return 0x9E;
                case 'Ÿ': return 0x9F;
            }
            return '?';
        }

        /// <summary>Base letter of a Latin-1 accented char, or '\0' if it isn't one.</summary>
        private static char BaseLetter(char c)
        {
            const string Accented = "ÀÁÂÃÄÅÇÈÉÊË" +
                                    "ÌÍÎÏÑÒÓÔÕÖ" +
                                    "ÙÚÛÜÝ" +
                                    "àáâãäåçèéêë" +
                                    "ìíîïñòóôõö" +
                                    "ùúûüýÿ";
            const string Bases    = "AAAAAACEEEE" + "IIIINOOOOO" + "UUUUY" +
                                    "aaaaaaceeee" + "iiiinooooo" + "uuuuyy";
            int i = Accented.IndexOf(c);
            return i < 0 ? '\0' : Bases[i];
        }
    }
}
