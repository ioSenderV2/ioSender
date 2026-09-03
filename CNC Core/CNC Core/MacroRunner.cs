/*
 * MacroRunner.cs - part of CNC Core library
 *
 * The macro/directive TOOLBOX of the unified streaming engine (Step 7 retired the second engine that
 * used to live here - its own directive loop, burst streamer and DoEvents idle waits are gone; a macro
 * now streams as an ordinary job through JobRunner/StreamPump). What remains is the pure logic both
 * JobRunner and StreamPump call into: directive recognition (RecognizeDirective/IsDirective/Body),
 * prerequisite evaluation (EvaluatePrereqLines and friends), (PROMPT) field collection/substitution,
 * the (MBOX) parser, comment sanitising, @file reference resolution, and the generated-copy writer.
 *
 * It never shows anything. Three seams cover the operator dialogs:
 *   - plain messages go through CNC.Core.UserPrompt (the host maps them to real message boxes);
 *   - FieldPrompt   - the (PROMPT param, default, label) parameter-entry form;
 *   - HoldPrompt    - the (MBOX) hold, which on the desktop is a deliberately non-modal, non-focus-
 *                     stealing window so the operator can jog while it is up.
 * With no host registered, both seams take the least-destructive default (proceed with the macro's own
 * declared defaults) - the right behaviour for an unattended/headless run.
 *
 * The engine ENTRY - what used to be Run() here - is CNC.Controls.MacroProcessor.Run: load the macro
 * text as the job (push/pop around it) and start it through the ordinary streaming path.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CNC.GCode;

namespace CNC.Core
{
    public static class MacroRunner
    {
        // The (PROMPT param, default [, label]) parameter-entry form. Registered by the host; null means
        // no operator to ask, so every field keeps the macro's own declared default and the run proceeds -
        // the same thing the 'unattended' flag does deliberately.
        public static Func<string, List<PromptField>, bool> FieldPrompt;

        // The (MBOX) hold: (title, message, cancellable, yesNo) -> continue? Registered by the host. Null
        // means nothing can be shown, so the hold cannot gate anything and the run proceeds.
        public static Func<string, string, bool, bool, bool> HoldPrompt;

        private static bool PromptForFields(string title, List<PromptField> fields)
        {
            return FieldPrompt == null || FieldPrompt(title, fields);
        }

        private static bool HoldPromptOrDefault(string title, string message, bool cancellable, bool yesNo)
        {
            return HoldPrompt == null || HoldPrompt(title, message, cancellable, yesNo);
        }

        // An extensionless "@<path>" macro reference defaults to ".macro". Normally already baked into the
        // stored text by MacroCreateDialog; this is the shared definition both that dialog and the run-time
        // resolver below use, so they cannot drift. Moved here from MacroManagerDialog - it is pure string
        // work with no UI in it, and the engine needs it.
        public static string NormalizeMacroReference(string code)
        {
            if (string.IsNullOrEmpty(code))
                return code;

            string trimmed = code.TrimStart();
            if (!trimmed.StartsWith("@"))
                return code;

            string rest = trimmed.Substring(1);
            int nl = rest.IndexOfAny(new[] { '\r', '\n' });
            string path = (nl >= 0 ? rest.Substring(0, nl) : rest).Trim();
            string tail = nl >= 0 ? rest.Substring(nl) : string.Empty;

            if (path.Length == 0 || Path.HasExtension(path))
                return code;

            return "@" + path + ".macro" + tail;
        }

        // Persist a copy of every macro/generated program to %AppData%\ioSender\Generated\<name>.macro,
        // overwriting each run - a debugging aid so "what did Generate actually build" is always inspectable
        // on disk (the streamed program itself lives only in memory). Best-effort: a write failure must
        // never block the run itself. Public so a tab's own Generate button can call it directly at
        // generate-time (not just when MacroProcessor.Run loads it) - the file is then on disk for
        // post-mortem even if the run alarms out before completing, or the operator never presses Run at all.
        public static void SaveGeneratedCopy(string name, string code)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Resources.GeneratedFolder);
                System.IO.File.WriteAllText(GeneratedCopyPath(name), code);
            }
            catch { /* best-effort - never let a diagnostic write block the actual run */ }
        }

        // The one filename rule for a generated copy, shared with readers (Work Order's compile cache
        // reads back the copy it wrote) so writer and reader cannot drift on the sanitization.
        public static string GeneratedCopyPath(string name)
        {
            var fileName = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".macro";
            return System.IO.Path.Combine(Resources.GeneratedFolder, fileName);
        }

        // Shared by every generator that parks at G30 (StartJobView, StepperCalibrationProbeWizard, ...):
        // lift to machine top, traverse to the G30 X/Y, then descend to G30 Z. 'L' emits one line (a caller's
        // own comment-sanitizing/line-numbering wrapper, or a plain StringBuilder.AppendLine).
        //
        // The lift is a BARE "G53 G0 Z0" and must stay that way. It used to be
        // "G53 G0 X[#<_abs_x>] Y[#<_abs_y>] Z0", naming X/Y at the live machine position, on the reasoning
        // that a G53 leaving an axis "unmoved" sign-flips a homing-direction-inverted ($23) axis's parser
        // base and throws a false Alarm:2. That firmware bug was TESTED AND DISPROVEN on 2026-08-11: an
        // A/B/C on a matched rig (no-op-then-move / move alone / the same coordinates as LITERALS) alarmed
        // in all three arms, so the target itself was being refused whatever named it - the real cause was
        // an uninitialised work envelope (Simulator 55dc91b). Naming X/Y cost twice over:
        //
        //   1. #<_abs_x> is read when the block is PARSED, not when the machine gets there. In a STREAMED
        //      program with a full planner buffer that froze whatever position was current at parse time -
        //      G30's, during a tool-change park - into a later rapid, producing full-speed diagonals
        //      mid-job (reverted once in 0166eba6, and it came straight back here).
        //   2. It made the lift depend on the CURRENT X/Y being inside the soft-limit envelope. grblHAL's
        //      jog clamp ($40=1) parks an axis exactly ON the envelope boundary, and re-commanding that
        //      same coordinate is then refused: Alarm:2 on the park line, machine motionless, mid-run.
        //      Observed 2026-09-02 with Y clamped to -839.004 against a -839.000 floor.
        //
        // A bare Z-only G53 holds X/Y by not mentioning them, so neither failure is reachable.
        public static void EmitGotoG30(System.Action<string> L)
        {
            // DO NOT wrap these in an o-word conditional. Tried 2026-08-02 to skip the lift-and-drop when
            // already parked at G30; on real hardware the program streamed to completion - the g-code activity
            // window scrolled normally - and the machine never moved at all, for the whole run.
            //
            // Cause: o-word FLOW CONTROL has never been streamed to a controller by this app. Every IF/WHILE
            // here lives inside a .macro FILE on the controller (pcorner, tc, ...), which grblHAL can seek
            // within; all 13 o-word sites in generated code are "O<name> CALL" into one of those files. A
            // streamed program isn't seekable, so the IF was swallowed - and took the rest of the program's
            // motion with it.
            //
            // If the redundant round trip is worth removing, decide it in C# at generate time - the caller
            // already knows the live position via GrblViewModel.MachinePosition - and just don't emit these
            // lines. Never by asking the controller to branch mid-stream.
            L("G53 G0 Z0");                             // lift Z to machine top, X/Y held by NOT naming them
            L("G53 G0 X[#5181] Y[#5182]");              // traverse to G30 X/Y at the top
            L("G53 G0 X[#5181] Y[#5182] Z[#5183]");     // descend to G30 Z (#5181-#5183 = stored G30, static NVS)
        }

        // grblHAL rejects a line over its receive-buffer size outright ("Max characters per line exceeded -
        // Received command line was not executed") and the stream never recovers from the lost line - so an
        // auto-generated comment with interpolated names (a probe/fixture name, say) can silently break an
        // entire run. Unlike G-code content, a comment's exact wording doesn't matter to the controller, so a
        // too-long PURE comment line ("(...)" with nothing before/after) gets its interior shortened rather
        // than sent whole and rejected. 200 is a conservative margin under grblHAL's common 256-byte line buffer.
        private const int MaxCommentLineLength = 200;

        // grblHAL ends a g-code comment at the FIRST ')', so any '(' or ')' INSIDE a (comment) corrupts the
        // block - the text after the inner ')' is parsed as g-code (e.g. "1 depth pass(es)" -> stray ", DOC...").
        // Replace parens between the outer '(' .. ')' with '[' .. ']' so generated comments are always well-formed.
        // Public since Step 7: MacroProcessor.Run applies it per line at LOAD time (skipping directive rows,
        // which the pump consumes and never sends) - the same protection the retired streaming loop gave.
        public static string SanitizeComment(string s)
        {
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
                return s;

            var sb = new StringBuilder(s.Length);
            sb.Append(s, 0, open + 1);
            for (int i = open + 1; i < close; i++)
                sb.Append(s[i] == '(' ? '[' : s[i] == ')' ? ']' : s[i]);
            sb.Append(s, close, s.Length - close);

            string result = sb.ToString();
            if (result.Length > MaxCommentLineLength && open == 0 && close == result.Length - 1)
            {
                int keep = MaxCommentLineLength - (open + 1) - 4;   // total = "(" + keep + "...)"
                result = result.Substring(0, open + 1) + result.Substring(open + 1, keep) + "...)";
            }
            return result;
        }

        // If 'code' is a single "@<path>" reference, replace it with the referenced file's current
        // contents (re-read on every run). Relative paths resolve against the config folder.
        // Returns false (after a message) if the file cannot be read. Public since Step 7:
        // MacroProcessor.Run (the unified-engine entry) resolves the reference before loading the
        // text as the job, exactly as the retired Run() loop here did before streaming it.
        public static bool ResolveFileReference(ref string code, string name)
        {
            // Extensionless @<path> defaults to ".macro" - normally already baked into the stored text
            // by MacroCreateDialog, this is a safety net for references normalized before that existed.
            code = NormalizeMacroReference(code);

            string trimmed = code.TrimStart();
            if (!trimmed.StartsWith("@"))
                return true;

            string path = trimmed.Substring(1);
            int nl = path.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0)
                path = path.Substring(0, nl);
            path = path.Trim();

            try
            {
                if (!Path.IsPathRooted(path))   // throws on a path with illegal characters
                    path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, path);
                code = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                UserPrompt.Show(string.Format("Macro \"{0}\" references a file that could not be read:\r\n\r\n{1}\r\n\r\n{2}", name, path, ex.Message),
                    "ioSender", PromptButtons.OK, PromptIcon.Warning);
                return false;
            }

            return true;
        }

        // A single (PROMPT ...) input field.
        // Public: the host builds the parameter-entry form from these (see FieldPrompt) and writes the
        // operator's entry back into Value.
        public class PromptField
        {
            public string Inner;    // parameter name inside the brackets, e.g. "_probe_radius"
            public string Label;    // text shown next to the input box
            public string Value;    // default, then the value the user entered

            public string Param { get { return "#<" + Inner + ">"; } }
        }

        // The one field collector (unified streaming engine Step 4b): every (PROMPT param, default
        // [, label]) in the lines, deduped by parameter name - bare (PROMPT) rows contribute nothing
        // here (they are mid-stream checkpoints, not inputs). Public so JobRunner's up-front pass on a
        // LOADED program uses exactly this; Run() above goes through it too.
        public static List<PromptField> CollectPromptFields(IEnumerable<string> lines)
        {
            var fields = new List<PromptField>();
            foreach (var raw in lines)
            {
                if (!IsDirective(raw, "PROMPT"))
                    continue;
                var field = ParsePromptField(raw);
                if (field != null && !fields.Any(f => f.Inner.Equals(field.Inner, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(field);
            }
            return fields;
        }

        // Public face of the FieldPrompt seam for JobRunner's up-front pass - same dialog, same
        // no-host default (proceed with declared defaults). Returns false only on operator Cancel.
        public static bool ShowFieldPrompt(string title, List<PromptField> fields)
        {
            return PromptForFields(title, fields);
        }

        // True when this row is a PROMPT directive with NO field body - the mid-stream run-confirmation
        // form. The pump treats these as an operator checkpoint (MBOX machinery); field-form rows are
        // consumed silently there because their work happened up front.
        public static bool IsBarePrompt(string line)
        {
            return IsDirective(line, "PROMPT") && Body(line, "PROMPT").Trim().Length == 0;
        }

        // Parse "(PROMPT param, default [, label])" into a field. Returns null for a bare (PROMPT).
        private static PromptField ParsePromptField(string raw)
        {
            string body = Body(raw, "PROMPT").Trim();
            if (body.Length == 0)
                return null;

            string[] parts = body.Split(new[] { ',' }, 3);
            string inner = CanonInner(parts[0]);
            if (inner == null)
                return null;

            string label = parts.Length > 2 ? parts[2].Trim() : string.Empty;

            return new PromptField {
                Inner = inner,
                Label = label.Length > 0 ? label : inner,
                Value = parts.Length > 1 ? parts[1].Trim() : "0"
            };
        }

        // Normalise a parameter name to the inside of a global named parameter reference, e.g.
        // "#<_radius>" / "#_radius" / "_radius" / "radius" -> "_radius".
        private static string CanonInner(string s)
        {
            s = s.Trim();
            if (s.StartsWith("#"))
                s = s.Substring(1).Trim();
            if (s.StartsWith("<") && s.EndsWith(">"))
                s = s.Substring(1, s.Length - 2).Trim();
            if (s.Length == 0)
                return null;
            if (!s.StartsWith("_"))     // force global scope so the value survives the program / $F= files
                s = "_" + s;

            return s;
        }

        // Replace every #<_name> reference in the line with the value the user entered. Public for
        // StreamPump's send-time substitution (Step 4b) - the stored program rows stay untouched.
        public static string ApplySubstitutions(string line, List<PromptField> fields)
        {
            foreach (var field in fields)
                line = Regex.Replace(line, @"#<\s*" + Regex.Escape(field.Inner) + @"\s*>", field.Value, RegexOptions.IgnoreCase);

            return line;
        }

        // The one PREREQ evaluator (unified streaming engine Step 5): collect every (PREREQ ...)
        // condition from the given lines, fetch a fresh $# when any condition needs it (homed state /
        // stored positions go stale in the change-based status report), and return the unmet failure
        // texts - empty means run. Public so JobRunner's pre-Cycle-Start gate on a LOADED program uses
        // exactly this, not a re-implementation; Run() above goes through it too.
        public static List<string> EvaluatePrereqLines(GrblViewModel model, IEnumerable<string> lines)
        {
            var conditions = new List<string>();
            foreach (var raw in lines)
            {
                if (!IsDirective(raw, "PREREQ"))
                    continue;
                foreach (var arg in Body(raw, "PREREQ").Split(','))
                {
                    string cond = arg.Trim();   // original case kept - build options match case-sensitively
                    if (cond.Length > 0)
                        conditions.Add(cond);
                }
            }

            if (conditions.Any(c => c.Equals("homed", StringComparison.OrdinalIgnoreCase) || CoordinateSystemCodes.Contains(c.ToUpperInvariant())))
                GrblWorkParameters.Get(model);

            var unmet = new List<string>();
            foreach (var cond in conditions)
            {
                string fail = EvalPrereq(model, cond);
                if (fail != null)
                    unmet.Add(fail);
            }
            return unmet;
        }

        private static string EvalPrereq(GrblViewModel model, string cond)
        {
            switch (cond.ToLowerInvariant())
            {
                case "":
                    return null;
                case "homed":
                    // Prefer the fresh $# [HOME:...] mask (grblHAL sys.homed.mask) - it survives a position-loss
                    // alarm that leaves the cached HomedState stale. >0 = homed, 0 = unhomed. But if $# could not
                    // be read (-1: timed out / no [HOME:] line) DON'T fail closed - fall back to the live homed
                    // state, otherwise a homed machine is wrongly reported "not homed" (and every $#-derived
                    // prereq - G28/G30/G59.3 - fails with it).
                    if (GrblWorkParameters.HomedMask >= 0)
                        return GrblWorkParameters.HomedMask > 0 ? null : "the machine is not homed";
                    return model.HomedState == HomedState.Homed ? null : "the machine is not homed";
                case "tlo":
                case "tloref":
                    return model.IsTloReferenceSet ? null : "the tool length offset reference is not set";
                case "idle":
                    return model.GrblState.State == GrblStates.Idle ? null : "the machine is not idle";
                case "noalarm":
                case "notalarm":
                    return model.GrblState.State != GrblStates.Alarm ? null : "the machine is in an alarm state";
                case "connected":
                    return model.GrblState.State != GrblStates.Unknown ? null : "the controller is not connected";
                default:
                    // A coordinate-system code (case-insensitive)?
                    string code = cond.ToUpperInvariant();
                    if (CoordinateSystemCodes.Contains(code))
                    {
                        if (!CoordinateSystemDefined(code))
                            return string.Format("{0} is not set", code);
                        // Set, but for a stored MACHINE position that is only half the question - it also
                        // has to be somewhere the controller will let us rapid to. See StoredPositionUnreachable.
                        return StoredPositionUnreachable(code);
                    }

                    // Otherwise require it to be a controller $I build option (NEWOPT), matched
                    // exactly and case-sensitively (e.g. EXPR, TC, THC).
                    return BuildOptionPresent(cond) ? null : string.Format("the controller build option '{0}' is not present", cond);
            }
        }

        // True if 'option' is one of the controller's $I build options (NEWOPT), matched exactly
        // and case-sensitively (e.g. EXPR, TC, THC).
        private static bool BuildOptionPresent(string option)
        {
            if (string.IsNullOrEmpty(GrblInfo.NewOptions))
                return false;

            foreach (var opt in GrblInfo.NewOptions.Split(','))
                if (opt == option)
                    return true;

            return false;
        }

        // grbl stored positions / work coordinate systems that PREREQ can require.
        private static readonly HashSet<string> CoordinateSystemCodes = new HashSet<string> {
            "G28", "G30", "G92", "G54", "G55", "G56", "G57", "G58", "G59", "G59.1", "G59.2", "G59.3"
        };

        // A stored position/offset is treated as "set" if any axis is non-zero. grbl has no explicit
        // "is defined" flag - these default to zero - so one deliberately left at machine zero would
        // read as unset. Values come from the $# report (GrblWorkParameters) - call GrblWorkParameters.Get(model)
        // first for a fresh read (Run's own PREREQ path does this once up front; a caller checking a stored
        // position OUTSIDE a PREREQ string, e.g. StartJobView's G28 fixture, must fetch it itself). Public so
        // callers besides Run's own PREREQ evaluator (e.g. StartJobView, deciding whether to prompt the operator
        // to set G28 before Generate) can reuse the exact same "is it set" definition instead of duplicating it.
        public static bool CoordinateSystemDefined(string code)
        {
            var cs = GrblWorkParameters.GetCoordinateSystem(code);
            if (cs == null)
                return false;

            for (int i = 0; i < GrblInfo.NumAxes; i++)
                if (!double.IsNaN(cs.Values[i]) && Math.Abs(cs.Values[i]) > 0.0001d)
                    return true;

            return false;
        }

        // The stored positions PREREQ can require that a program actually rapids to, so the envelope
        // check below applies to them.
        //
        // This deliberately includes the work coordinate systems. It used to be G28/G30 only, on the
        // reasoning that "G54..G59.3 are work OFFSETS, not targets". That was wrong: selecting a WCS
        // and going to its origin makes the stored offset a target in machine coordinates, which is
        // exactly what tc.macro does - "G59.3" followed by "G0 X0 Y0". A G59.3 taught 4 mm inside the
        // Y pull-off band passed the "is it set" half of the PREREQ, started the job, prompted the
        // operator to fit the probe, and only THEN threw Alarm:2 on the move to X0 Y0.
        //
        // False positives are not a concern: this only ever runs for a code a macro NAMES in its
        // PREREQ line, and naming one is declaring the macro will go there. G92 stays out - it is a
        // transient offset applied on top of the active WCS, not a position anything rapids to.
        private static readonly HashSet<string> StoredMachinePositionCodes = new HashSet<string> {
            "G28", "G30", "G54", "G55", "G56", "G57", "G58", "G59", "G59.1", "G59.2", "G59.3"
        };

        // Is a stored position (G28/G30, or a WCS origin) actually REACHABLE under the soft limits?
        // Returns null when it is (or when the question doesn't apply), otherwise the failure text.
        //
        // Being "set" is not enough. A G30 recorded when the pull-off was smaller - or with soft limits
        // off, or via an MPG - stays in the controller's non-volatile storage at a coordinate the parser
        // will now refuse, and NOTHING in the sender noticed: every generator that parks at G30
        // (Start Job, Work Order, stepper calibration, all via EmitGotoG30) emitted a program that
        // streamed happily and then threw Alarm:2 on the park line, mid-run, with the operator watching.
        // Observed 2026-08-06: [G30:751.428,-836.262,-34.000] against $131=840 / $27=6 puts Y 2.262 mm
        // outside its envelope, so "G53 G0 X[#5181] Y[#5182]" alarmed at Ln:220 - and the failure looked
        // like a generator bug because the preceding line used #<_abs_x>/#<_abs_y> (that lift is a bare
        // "G53 G0 Z0" now - see EmitGotoG30).
        //
        // The envelope mirrors grblHAL's own limits_set_work_envelope()/check_travel_limits(), which is
        // ALSO exactly what JogController.BuildCommand / JogBaseControl.ClampMachine already clamp jogs
        // to - that rule is proven on real machines, so it is reused rather than re-derived:
        //     $22 bit 3 (ForceSetOrigin) + the axis's $23 bit  -> 0 .. +(travel - pulloff)
        //     $22 bit 3, $23 bit clear                         -> -(travel - pulloff) .. 0
        //     no $22 bit 3                                     -> -(travel - pulloff) .. -pulloff
        // with pulloff = $27 only when hard limits ($21) are on, matching the firmware's own condition.
        //
        // Fails OPEN in every "can't know" case - soft limits off, travel not configured, homed state
        // unknown, axis not homed - because the firmware itself only checks homed axes with $20 on, and
        // a false refusal here blocks a job that would have run perfectly well.
        public static string StoredPositionUnreachable(string code)
        {
            if (!StoredMachinePositionCodes.Contains(code))
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("run", "StoredPositionUnreachable(" + code + "): SKIP - not a checked code");
                return null;
            }

            if (GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) != 1)
            {
                if (DebugLog.Enabled)
                {
                    // -1 means the LOOKUP missed, not that soft limits are off - GetInteger returns -1 for
                    // "no such entry". Say which it is, because the two have completely different causes:
                    // an operator who turned $20 off, versus a settings collection that never got populated
                    // (or got cleared) - the latter silently disables this guard AND the jog clamp.
                    var d = GrblSettings.Get(GrblSetting.SoftLimitsEnable);
                    DebugLog.Write("run", string.Format(
                        "StoredPositionUnreachable({0}): SKIP - $20 reads {1}; GrblSettings.Count={2} IsLoaded={3} entry={4} ids near 20=[{5}]",
                        code, GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable),
                        GrblSettings.Settings.Count, GrblSettings.IsLoaded,
                        d == null ? "MISSING" : ("Id=" + d.Id + " Value='" + d.Value + "'"),
                        string.Join(",", GrblSettings.Settings.Where(s => s.Id >= 18 && s.Id <= 32)
                                                             .Select(s => s.Id + "=" + s.Value).ToArray())));
                }
                return null;
            }

            // The firmware only envelope-checks axes it considers homed; -1 = the $# read gave us no
            // [HOME:] line at all, so we know nothing and must not guess.
            int homed = GrblWorkParameters.HomedMask;
            if (homed <= 0)
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("run", string.Format("StoredPositionUnreachable({0}): SKIP - HomedMask {1}", code, homed));
                return null;
            }

            var cs = GrblWorkParameters.GetCoordinateSystem(code);
            if (cs == null)
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("run", "StoredPositionUnreachable(" + code + "): SKIP - GetCoordinateSystem returned null");
                return null;
            }

            double pulloff = GrblSettings.GetInteger(GrblSetting.HardLimitsEnable) == 1
                              ? GrblSettings.GetDouble(GrblSetting.HomingPulloff) : 0d;

            for (int i = 0; i < GrblInfo.NumAxes; i++)
            {
                if ((homed & (1 << i)) == 0 || double.IsNaN(cs.Values[i]))
                    continue;

                double travel = Math.Abs(GrblInfo.MaxTravel.Values[i]);
                if (travel <= 0d)
                {
                    if (DebugLog.Enabled)
                        DebugLog.Write("run", string.Format("StoredPositionUnreachable({0}): {1} SKIP - $13x travel reads {2}",
                            code, AxisLetter(i), travel));
                    continue;   // $13x not configured for this axis - no envelope to check against
                }

                double lo, hi;
                if (GrblInfo.ForceSetOrigin)
                {
                    if (GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(i))) { lo = 0d; hi = travel - pulloff; }
                    else { lo = -(travel - pulloff); hi = 0d; }
                }
                else { lo = -(travel - pulloff); hi = -pulloff; }

                double pos = cs.Values[i], limit = pos < lo ? lo : hi;

                if (DebugLog.Enabled)
                    DebugLog.Write("run", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "StoredPositionUnreachable({0}): {1} pos={2:0.###} envelope={3:0.###}..{4:0.###} travel={5:0.###} pulloff={6:0.###} fso={7} homingdir={8} -> {9}",
                        code, AxisLetter(i), pos, lo, hi, travel, pulloff, GrblInfo.ForceSetOrigin,
                        GrblInfo.HomingDirection, (pos < lo || pos > hi) ? "OUTSIDE" : "inside"));
                if (pos < lo || pos > hi)
                    return string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        "{0} is stored outside the machine's soft-limit travel: {1} {2:0.###} is {3:0.###} mm beyond the limit of {4:0.###}. " +
                        "Move the machine inside its travel and set {0} again.",
                        code, AxisLetter(i), pos, Math.Abs(pos - limit), limit);
            }

            return null;
        }

        // Axis letter for an index, for operator-facing text ("Y"). GrblInfo.AxisLetters is remapped for
        // the controller's actual axis set, so it is the one source to ask.
        private static string AxisLetter(int axis)
        {
            string letters = GrblInfo.AxisLetters;
            return axis >= 0 && axis < letters.Length ? letters.Substring(axis, 1) : ("axis " + axis);
        }

        // Public face of RunMBox for the unified streaming engine (Step 4a): StreamPump consumes an
        // (MBOX ...) row and shows the SAME prompt through the SAME HoldPrompt seam this class's own
        // Run() loop uses - one parser, one dialog, no drift. Null host (headless/unattended) proceeds,
        // same as always.
        public static bool ShowMBox(string name, string line)
        {
            return RunMBox(name, line);
        }

        // Show the message box for an (MBOX...) line; returns false if the user cancelled (Cancel/No).
        private static bool RunMBox(string name, string line)
        {
            string body = Body(line, "MBOX").Trim();   // "OKCANCEL, message" or "message"
            bool cancellable = false, yesNo = false;

            int comma = body.IndexOf(',');
            string head = (comma >= 0 ? body.Substring(0, comma) : body).Trim().ToUpperInvariant();
            if (head == "OK" || head == "OKCANCEL" || head == "YESNO")
            {
                cancellable = head == "OKCANCEL" || head == "YESNO";
                yesNo = head == "YESNO";
                body = comma >= 0 ? body.Substring(comma + 1).Trim() : string.Empty;
            }

            if (body == string.Empty)
                body = "(no message)";

            return HoldPromptOrDefault(name, body, cancellable, yesNo);
        }

        // DRAFT/UNVERIFIED 2026-08-08 - not yet built or hardware-tested, see
        // docs/Architecture-Unified-Streaming-Engine.md Step 2. General-purpose counterpart to the
        // keyword-specific IsDirective below: used by GCodeJob (GCodeBlock.Directive) to flag a directive
        // line at LOAD time, before there's any reason to ask "is it specifically a PREREQ" - a plain
        // Load File needs this too, not just this class's own Run() loop. Returns the canonical uppercase
        // keyword, or null if the line isn't one of the four directives at all.
        public static string RecognizeDirective(string line)
        {
            // WAITIDLE takes no arguments, so require the EXACT form "(WAITIDLE)" here. The loose
            // keyword+non-letter rule IsDirective uses (kept for the argument-taking directives)
            // recognized a loaded file's ordinary comment "(WAITIDLE barrier test ...)" as a live
            // directive on the very first hardware test of the pump's WAITIDLE barrier, 2026-08-08 -
            // at load time this runs against ARBITRARY g-code files, not just hand-written macros, so
            // prose comments are a real input. N-word stripped first (StripLineNumber): a file that
            // arrives ALREADY numbered on disk must flag its directives too.
            string t = StripLineNumber(line).Trim();
            if (t.Length > 1 && t[0] == '(' && t[t.Length - 1] == ')' &&
                t.Substring(1, t.Length - 2).Trim().Equals("WAITIDLE", StringComparison.OrdinalIgnoreCase))
                return "WAITIDLE";

            foreach (var keyword in new[] { "PREREQ", "PROMPT", "MBOX" })
                if (IsDirective(line, keyword))
                    return keyword;
            return null;
        }

        // Strip a leading N-word ("N20 (PREREQ...)" -> "(PREREQ...)"). Line numbering is prepended at
        // LOAD time (GCodeJob), AFTER the Directive flag is captured but onto the STORED Data text -
        // and every evaluator that re-reads that text lands here. Found on real hardware 2026-08-08
        // 15:04: a rebooted (unhomed, TLO-less) machine ran a work order that declared
        // "(PREREQ, connected, homed, noalarm, tlo, ...)" - the gate entered on the Directive flag,
        // but EvaluatePrereqLines re-parsed the numbered rows, matched nothing, collected ZERO
        // conditions and passed vacuously. The run then died on the wire (error:2 reading the wiped
        // #<_tlo_ref>) instead of being refused up front. Same hole covered PROMPT field collection,
        // IsBarePrompt, and RunMBox's body parsing in any numbered program.
        private static string StripLineNumber(string line)
        {
            string t = line.TrimStart();
            if (t.Length > 1 && (t[0] == 'N' || t[0] == 'n') && char.IsDigit(t[1]))
            {
                int i = 2;
                while (i < t.Length && char.IsDigit(t[i]))
                    i++;
                t = t.Substring(i).TrimStart();
            }
            return t;
        }

        // True if the trimmed line is the named directive, e.g. "(MBOX ...)" / "(PREREQ ...)" -
        // tolerating a load-time-prepended N-word (see StripLineNumber).
        private static bool IsDirective(string line, string keyword)
        {
            string t = StripLineNumber(line);
            if (!t.StartsWith("("))
                return false;
            t = t.Substring(1).TrimStart();
            return t.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
                   (t.Length == keyword.Length || !char.IsLetter(t[keyword.Length]));
        }

        // The text inside the parentheses after the keyword (and the following comma/space), e.g.
        // "(MBOX, OKCANCEL, hi)" -> "OKCANCEL, hi". N-word tolerated, same as IsDirective.
        private static string Body(string line, string keyword)
        {
            string t = StripLineNumber(line).Trim();
            int close = t.LastIndexOf(')');
            string inner = (close >= 1 ? t.Substring(1, close - 1) : t.Substring(1)).TrimStart();
            inner = inner.Substring(Math.Min(keyword.Length, inner.Length));   // drop the keyword
            return inner.TrimStart(' ', ',');
        }
    }
}
