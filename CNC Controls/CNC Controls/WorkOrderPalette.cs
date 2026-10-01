/*
 * WorkOrderPalette.cs - one colour and one letter per toolpath, shared by the stock diagram and the
 * saved drawing's feature table.
 *
 * Before this, every toolpath drew in the same steel blue and was told apart by a text label printed
 * on the drawing. With more than a handful of features those labels collided with each other and
 * with the geometry, which is what made the sheet unreadable: the name of a toolpath is far wider
 * than the toolpath, so the thing that overlaps is the writing, not the shapes.
 *
 * So identity moved off the text and onto a balloon - a filled circle carrying a letter - and colour
 * links the balloon to its shape and to its row in the table. Colour is never the ONLY channel: the
 * letter always says which feature this is, so the drawing still works printed in mono, photocopied,
 * or read by someone who can't separate the hues. The palette repeats past its length; that is
 * cosmetic, because two features sharing a colour never share a letter.
 *
 * Colours are all mid-to-dark and saturated on purpose. The stock is drawn in its material's own
 * colour - olive for MDF, tan for hardwood, grey for metals - and a pale or washed-out hue vanishes
 * against those. Dark also means white text sits legibly inside every balloon.
 */

using System.Collections.Generic;
using System.Windows.Media;

namespace CNC.Controls
{
    public static class WorkOrderPalette
    {
        // Ten hues that stay apart from each other and from every material's stock colour. Ordered so
        // that the first few - the ones a small work order actually uses - are maximally distinct.
        //
        // NONE of them is a grey or a near-grey, and that is a rule rather than a coincidence: grey is
        // spoken for below as "held back". A slate blue sat in this list at first and the eighth feature
        // came out looking like one that had been switched off.
        private static readonly Color[] Hues =
        {
            Color.FromRgb(0x1F, 0x6F, 0xB2),   // blue
            Color.FromRgb(0xC0, 0x39, 0x2B),   // red
            Color.FromRgb(0x1E, 0x84, 0x49),   // green
            Color.FromRgb(0x7D, 0x3C, 0x98),   // purple
            Color.FromRgb(0xD9, 0x77, 0x06),   // amber
            Color.FromRgb(0x0E, 0x7C, 0x7B),   // teal
            Color.FromRgb(0xC2, 0x41, 0x8E),   // magenta
            Color.FromRgb(0x3B, 0x4C, 0xCA),   // indigo
            Color.FromRgb(0x8B, 0x5A, 0x2B),   // brown
            Color.FromRgb(0x55, 0x6B, 0x2F)    // dark olive
        };

        /// <summary>Grey, for a toolpath that is held back - not part of the cut, so not part of the key.</summary>
        public static readonly Color HeldBack = Color.FromRgb(0x8A, 0x8F, 0x94);

        public static Color ColorFor(int index)
        {
            return Hues[((index % Hues.Length) + Hues.Length) % Hues.Length];
        }

        private static readonly Dictionary<Color, Brush> brushes = new Dictionary<Color, Brush>();

        /// <summary>
        /// A frozen brush for a colour. Frozen because the diagram is rebuilt on every edit and every
        /// mouse drag - a fresh unfrozen brush per shape per redraw is pure churn.
        /// </summary>
        public static Brush BrushFor(Color c)
        {
            Brush b;
            if (brushes.TryGetValue(c, out b))
                return b;
            var sb = new SolidColorBrush(c);
            sb.Freeze();
            brushes[c] = sb;
            return sb;
        }

        public static Brush BrushFor(int index) { return BrushFor(ColorFor(index)); }

        /// <summary>
        /// The same colour at <paramref name="alpha"/>, already composited onto white rather than left
        /// translucent. The envelope footprints are drawn over the stock's own colour, and a real alpha
        /// would tint them by whatever material is selected - so a green envelope on olive MDF came out
        /// a different green from the same envelope on tan. Flattening keeps one envelope colour per
        /// feature whatever it sits on.
        /// </summary>
        public static Brush TintFor(int index, double alpha)
        {
            var c = ColorFor(index);
            return BrushFor(Color.FromRgb(Mix(c.R, alpha), Mix(c.G, alpha), Mix(c.B, alpha)));
        }

        private static byte Mix(byte v, double alpha)
        {
            return (byte)System.Math.Round(255d * (1d - alpha) + v * alpha);
        }

        /// <summary>
        /// A, B, ... Z, AA, AB - the balloon on the drawing and the ID column of the feature table are
        /// the same string, produced here so the two cannot drift apart.
        /// </summary>
        public static string Id(int index)
        {
            string s = string.Empty;
            for (int n = index; ; n = n / 26 - 1)
            {
                s = (char)('A' + n % 26) + s;
                if (n < 26)
                    break;
            }
            return s;
        }
    }
}
