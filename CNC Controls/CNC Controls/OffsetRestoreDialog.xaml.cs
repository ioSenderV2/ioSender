/*
 * OffsetRestoreDialog.xaml.cs - review-and-edit step for a work offset restore
 *
 * Part of CNC Controls library for Grbl
 *
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    /// <summary>What a row puts back, and how.</summary>
    public enum OffsetRowKind
    {
        CoordinateSystem,   // G54-G59.3 - a G10 L2 write, no motion
        Tool,               // tool table - a G10 L1 write, no motion
        Position            // G28 / G30  - CANNOT be written; only taught by driving there
    }

    /// <summary>
    /// One line of a work offset snapshot, as something the operator can read and correct.
    /// </summary>
    /// <remarks>
    /// Only Restore raises change notification, and only because the dialog's footer has to follow it:
    /// the footer states what WILL happen given the current ticks, so it has to hear about a tick. The
    /// value columns need none - nothing reads them until Restore is pressed.
    /// </remarks>
    public class OffsetRestoreRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private bool restore = true;
        public bool Restore
        {
            get { return restore; }
            set
            {
                if (restore == value)
                    return;
                restore = value;
                var h = PropertyChanged;
                if (h != null)
                    h(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Restore)));
            }
        }

        /// <summary>What the operator sees - "G54", "Tool 3", "G30".</summary>
        public string Item { get; set; }

        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }

        /// <summary>Degrees, coordinate systems only. Null everywhere else, so the column reads blank.</summary>
        public double? Rotation { get; set; }

        public string Note { get; set; }

        internal OffsetRowKind Kind { get; set; }
        internal int P { get; set; }           // G10 P word for a coordinate system or tool
        internal string TeachCode { get; set; } // "G28.1" / "G30.1" for a position row

        internal bool MovesMachine { get { return Kind == OffsetRowKind.Position; } }
    }

    public partial class OffsetRestoreDialog : Window
    {
        /// <summary>The rows the operator left ticked. Valid when DialogResult is true.</summary>
        public List<OffsetRestoreRow> Selected { get; private set; }

        private readonly ObservableCollection<OffsetRestoreRow> rows;

        public OffsetRestoreDialog(IEnumerable<OffsetRestoreRow> parsed)
        {
            InitializeComponent();

            rows = new ObservableCollection<OffsetRestoreRow>(parsed);
            dgrRows.ItemsSource = rows;

            foreach (var r in rows)
                r.PropertyChanged += (s, e) => UpdateFooter();
            UpdateFooter();
        }

        /// <summary>
        /// Say what pressing Restore will actually do, from the ticks as they stand.
        /// </summary>
        /// <remarks>
        /// This was a fixed sentence about G28/G30 opening a program and driving the machine, shown whether
        /// or not any such row was ticked - so it contradicted the table in front of it and was reported as
        /// confusing on first use. A warning that is always there is not a warning, it is wallpaper. Now it
        /// names the positions it will drive to, and when none are ticked it says so instead.
        /// </remarks>
        private void UpdateFooter()
        {
            var positions = rows.Where(r => r.Restore && r.MovesMachine).ToList();
            int writes = rows.Count(r => r.Restore && !r.MovesMachine);

            if (positions.Count == 0)
            {
                txtWarn.Foreground = System.Windows.Media.Brushes.DimGray;
                txtWarn.Text = writes == 0
                    ? "Nothing is ticked, so Restore will do nothing."
                    : string.Format("Nothing ticked moves the machine. {0} offset{1} will be written to the controller.",
                                    writes, writes == 1 ? "" : "s");
                return;
            }

            var sb = new StringBuilder();
            sb.Append(positions.Count == 1 ? "This MOVES the machine: " : "This MOVES the machine to " + positions.Count + " positions: ");
            sb.Append(string.Join(", ", positions.Select(p => string.Format("{0} at X{1} Y{2} Z{3}",
                      p.Item, Fmt(p.X), Fmt(p.Y), Fmt(p.Z)))));
            sb.Append(". Those cannot be written - the controller only learns them from where the machine is standing - " +
                      "so they open as a program in the Job tab. Nothing moves until you read it and press Start.");

            txtWarn.Foreground = System.Windows.Media.Brushes.Firebrick;
            txtWarn.Text = sb.ToString();
        }

        private void btnNone_Click(object sender, RoutedEventArgs e)
        {
            // Commit first - see btnRestore_Click for why.
            dgrRows.CommitEdit(DataGridEditingUnit.Row, true);
            foreach (var r in rows)
                r.Restore = false;
            dgrRows.Items.Refresh();
            UpdateFooter();
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            // COMMIT THE OPEN CELL FIRST. A DataGrid holds the cell being edited in its editing state until
            // something commits it, and clicking a button does not: the typed value is on screen and the
            // bound property still holds the old one. This codebase has been bitten by the same shape before
            // - a length field that committed on LostFocus only, where a typed 0.37 wrote 0.450 - and here
            // it would mean a Z the operator had just corrected going in at its original value.
            dgrRows.CommitEdit(DataGridEditingUnit.Cell, true);
            dgrRows.CommitEdit(DataGridEditingUnit.Row, true);

            Selected = rows.Where(r => r.Restore).ToList();
            if (Selected.Count == 0)
            {
                AppDialogs.Show(this, "Nothing is ticked, so there is nothing to restore.", "Restore work offsets",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        // ---- Reading a snapshot ----

        private static readonly Regex Axis = new Regex(@"(?<a>[XYZR])(?<v>-?\d+(\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex Offset = new Regex(@"^G90G10L(?<l>[12])P(?<p>\d+)(?<rest>.*)$", RegexOptions.Compiled);

        /// <summary>
        /// Turn an Offsets_*.nc snapshot into rows.
        /// </summary>
        /// <remarks>
        /// Reads the shape GrblWorkParameters.Export writes, and nothing else: a line it does not recognise
        /// is skipped rather than guessed at. The position pair is two lines - a G0 G53 rapid followed by the
        /// G28.1/G30.1 that teaches it - so the rapid is held until its teach line says which one it was.
        /// </remarks>
        public static List<OffsetRestoreRow> Parse(IEnumerable<string> lines)
        {
            var result = new List<OffsetRestoreRow>();
            double[] pending = null;

            foreach (var raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("("))
                    continue;

                if (line.StartsWith("G0G53"))
                {
                    pending = ReadAxes(line.Substring("G0G53".Length));
                    continue;
                }

                if (line.StartsWith("G28.1") || line.StartsWith("G30.1"))
                {
                    if (pending != null)
                    {
                        string code = line.Substring(0, 5);
                        result.Add(new OffsetRestoreRow
                        {
                            Kind = OffsetRowKind.Position,
                            Item = code.Substring(0, code.Length - 2),   // "G28.1" -> "G28"
                            TeachCode = code,
                            X = Val(pending, 0),
                            Y = Val(pending, 1),
                            Z = Val(pending, 2),
                            Note = "MOVES the machine"
                        });
                        pending = null;
                    }
                    continue;
                }

                var m = Offset.Match(line);
                if (!m.Success)
                    continue;

                var v = ReadAxes(m.Groups["rest"].Value);
                bool isTool = m.Groups["l"].Value == "1";
                int p = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);

                result.Add(new OffsetRestoreRow
                {
                    Kind = isTool ? OffsetRowKind.Tool : OffsetRowKind.CoordinateSystem,
                    P = p,
                    Item = isTool ? "Tool " + p : CoordinateSystemName(p),
                    X = Val(v, 0),
                    Y = Val(v, 1),
                    Z = Val(v, 2),
                    // Rotation is a coordinate system property. An R on a TOOL line is its radius, which is a
                    // different quantity that happens to share a letter - do not let it into this column.
                    Rotation = isTool ? (double?)null : Val(v, 3)
                });
            }

            return result;
        }

        /// <summary>G10 P number to the code the operator knows it by.</summary>
        private static string CoordinateSystemName(int p)
        {
            switch (p)
            {
                case 1: return "G54";
                case 2: return "G55";
                case 3: return "G56";
                case 4: return "G57";
                case 5: return "G58";
                case 6: return "G59";
                case 7: return "G59.1";
                case 8: return "G59.2";
                case 9: return "G59.3";
                default: return "P" + p;
            }
        }

        // X, Y, Z, R as a fixed-slot array; NaN where the word was absent.
        private static double[] ReadAxes(string words)
        {
            var v = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
            foreach (Match m in Axis.Matches(words ?? string.Empty))
            {
                double d;
                if (!double.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    continue;
                switch (m.Groups["a"].Value)
                {
                    case "X": v[0] = d; break;
                    case "Y": v[1] = d; break;
                    case "Z": v[2] = d; break;
                    case "R": v[3] = d; break;
                }
            }
            return v;
        }

        private static double? Val(double[] v, int i)
        {
            return v != null && i < v.Length && !double.IsNaN(v[i]) ? (double?)v[i] : null;
        }

        // ---- Writing the chosen rows back out ----

        /// <summary>The G10 writes: coordinate systems and tools. No motion, safe to run as a macro.</summary>
        public static string BuildWrites(IEnumerable<OffsetRestoreRow> rows)
        {
            var sb = new StringBuilder();
            foreach (var r in rows.Where(r => r.Kind != OffsetRowKind.Position))
            {
                sb.Append(r.Kind == OffsetRowKind.Tool ? "G90G10L1P" : "G90G10L2P").Append(r.P);
                Append(sb, "X", r.X);
                Append(sb, "Y", r.Y);
                Append(sb, "Z", r.Z);
                // Only when non-zero, as the exporter does: firmware without ROTATION_ENABLE answers
                // error:20 to any R word and would halt the restore part way through.
                if (r.Kind == OffsetRowKind.CoordinateSystem && r.Rotation.HasValue && r.Rotation.Value != 0d)
                    Append(sb, "R", Math.Round(r.Rotation.Value, 4));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The G28/G30 half, as a program. Keeps the warning and the M0 the exporter writes, because the
        /// operator still has to see them at the machine - the table said what it would do, this says when.
        /// </summary>
        public static string BuildPositionProgram(IEnumerable<OffsetRestoreRow> rows)
        {
            var positions = rows.Where(r => r.Kind == OffsetRowKind.Position).ToList();
            if (positions.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("(MSG,WARNING: axes will be moved to the positions below, ensure the machine is homed or abort!)");
            foreach (var r in positions)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "(MSG,  {0} -> X{1} Y{2} Z{3})",
                              r.Item, Fmt(r.X), Fmt(r.Y), Fmt(r.Z)));
            sb.AppendLine("M0");

            foreach (var r in positions)
            {
                var line = new StringBuilder("G0G53");
                Append(line, "X", r.X);
                Append(line, "Y", r.Y);
                Append(line, "Z", r.Z);
                sb.AppendLine(line.ToString());
                sb.AppendLine(r.TeachCode);
            }

            sb.AppendLine("M30");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string letter, double? value)
        {
            if (value.HasValue)
                sb.Append(letter).Append(value.Value.ToString("0.####", CultureInfo.InvariantCulture));
        }

        private static string Fmt(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-";
        }
    }
}
