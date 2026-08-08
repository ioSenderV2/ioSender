/*
 * MacroRunner.cs - part of CNC Core library
 *
 * The macro / generated-program engine, split out of CNC.Controls.MacroProcessor. Everything here talks
 * to the CONTROLLER: the directive loop ((PREREQ) / (PROMPT) / (MBOX) / (WAITIDLE)), prerequisite
 * evaluation, dry-run neutralisation, comment sanitising, the flow-controlled burst streamer, and the
 * idle/alarm waits that hold a macro between steps.
 *
 * It never shows anything. Three seams cover what used to be inline dialogs:
 *   - plain messages go through CNC.Core.UserPrompt (the host maps them to real message boxes);
 *   - FieldPrompt   - the (PROMPT param, default, label) parameter-entry form;
 *   - HoldPrompt    - the (MBOX) hold, which on the desktop is a deliberately non-modal, non-focus-
 *                     stealing window so the operator can jog while it is up.
 * With no host registered, both seams take the least-destructive default (proceed with the macro's own
 * declared defaults) - the right behaviour for an unattended/headless run.
 *
 * The Run-bar and active-program state that used to sit alongside this (ActiveRun, SupportsGenerateMode,
 * IsProgramGenerated, ...) is NOT engine - it stays in CNC.Controls.MacroProcessor, which also keeps the
 * dialogs and forwards Run/EmitGotoG30/CoordinateSystemDefined/SaveGeneratedCopy so no call site moved.
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
        // Hook to stream a generated program with full flow control (Feed Hold/Stop live) WITHOUT touching the
        // loaded job: args are (model, name, lines, isFinalBurst, preferJobView, onDone). Set by the shell
        // (ioSender XL). EVERY streamed run goes through this - a tool that owns a ProgramView streams into it,
        // a plain macro gets its own run view - so a run never overwrites the loaded program or hijacks the Job
        // tab. Cycle Start is deferred to a background dispatcher tick (see the implementation), so this call
        // returns before the burst actually starts - onDone is invoked once it reaches a true terminal state,
        // letting StreamProgram optionally wait for it (see Flush's 'wait' parameter) instead of racing the next
        // burst against this one.
        // isFinalBurst (added alongside the wait plumbing below): true only for the macro's own fire-and-forget
        // closing burst (Flush's wait=false call) - the host uses it to tell "the whole macro just finished" apart
        // from "one more mid-macro burst just finished, more is coming right behind it".
        // preferJobView (2026-08-01): opt-in escape hatch from the "never hijack the Job tab" rule above, for
        // the one case where hijacking it is exactly the point - Work Order already made itself the loaded job
        // via GCode.File.Push/LoadText before streaming, so its own burst should show live status in the real
        // docked Job-tab list instead of a separate floating view. False for every other caller (Setup,
        // calibration, fixture tools, ...) - those must keep the "don't touch the Job tab" guarantee.
        public static System.Action<GrblViewModel, string, string[], bool, bool, System.Action> RunStreamedJobInPlace;

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

        // Name given to the in-memory program when a flush is streamed (set per run).
        private static string _streamName = "Macro";

        // True while a Run() is in flight - INCLUDING its (MBOX)/(PROMPT) holds and WaitForIdle waits,
        // which is the whole point: between streamed bursts model.IsJobRunning is FALSE while a hold
        // prompt sits waiting for the operator, so anything using IsJobRunning alone reads an active
        // macro as "idle". That exact gap let a tooling shutdown request close ioSender in the middle
        // of a Setup macro's "install probe" (MBOX) hold on 2026-08-08, abandoning the run - nothing
        // marked the run as in progress. Counter + try/finally in Run() covers every early return.
        // NOT covered: the final fire-and-forget burst (Flush wait:false) still streaming after Run()
        // returns - IsJobRunning is true for that window, so callers should check both.
        private static int runDepth = 0;
        public static bool IsRunning { get { return System.Threading.Interlocked.CompareExchange(ref runDepth, 0, 0) > 0; } }

        /// <summary>Run a macro. Returns false if it was aborted (prerequisite unmet or user cancelled).</summary>
        /// <param name="unattended">Skip every routine confirmation this macro would otherwise pop (the
        /// confirm-before-run prompt, bare mid-body (PROMPT) run-confirmations, and (MBOX) holds - all
        /// auto-answered OK/Yes) and take an unanswered (PROMPT param, default, ...) input's own default
        /// rather than asking. For a "Generate and Run" action that a tab offers explicitly (see
        /// MacroProcessor.SupportsGenerateAndRun) - NOT a general silencing knob. PREREQ failures and
        /// alarm-abort checks still apply and still stop the run; this only skips prompts that exist purely
        /// to ask "are you sure" / "ready?", not safety gates.</param>
        public static bool Run(GrblViewModel model, string name, string code, bool confirm = false, bool unattended = false, bool preferJobView = false)
        {
            if (model == null || string.IsNullOrEmpty(code))
                return true;

            // IsRunning bracket - see runDepth above. try/finally (not manual pairing) so every one of
            // RunInner's early returns and any thrown exception still clears the flag.
            System.Threading.Interlocked.Increment(ref runDepth);
            try { return RunInner(model, name, code, confirm, unattended, preferJobView); }
            finally { System.Threading.Interlocked.Decrement(ref runDepth); }
        }

        private static bool RunInner(GrblViewModel model, string name, string code, bool confirm, bool unattended, bool preferJobView)
        {
            if (string.IsNullOrEmpty(name))
                name = "Macro";

            _streamName = name;

            // A macro whose body is a single "@<path>" line is a reference to an external file;
            // load and run that file's current contents (re-read every run, so the macro can be
            // developed by editing the file - no copy/paste back into ioSender).
            if (!ResolveFileReference(ref code, name))
                return false;

            SaveGeneratedCopy(name, code);

            string[] lines = code.Replace("\r", string.Empty).Split('\n');
            var buffer = new StringBuilder();

            // 1) Prerequisites - evaluated up front, before anything is streamed. Shared with
            //    JobRunner's loaded-program gate (unified streaming engine Step 5) - ONE evaluator,
            //    per the streaming-paths audit's "the same rule written twice can drift" finding.
            var unmet = EvaluatePrereqLines(model, lines);
            if (unmet.Count > 0)
            {
                UserPrompt.Show(string.Format("Cannot run macro \"{0}\":\r\n\r\n• {1}", name, string.Join("\r\n• ", unmet)),
                    "ioSender", PromptButtons.OK, PromptIcon.Warning);
                return false;
            }

            // 2) Input prompts - gather every (PROMPT param, default [, label]) into a single
            //    dialog shown up front, then bind the entered values two ways (hybrid):
            //      - assign the globals on the controller (so $F=<file> jobs can read them), and
            //      - substitute the references in the streamed body (so inline use works on any
            //        controller and ioSender's own parser stays consistent).
            var fields = CollectPromptFields(lines);
            // An input prompt's OK/Cancel is itself the run confirmation, so a separate "Prompt to
            // run" box would be redundant - only show that when there are no input prompts to gate on.
            if (fields.Count > 0)
            {
                // Unattended: no operator to ask - each field just keeps the macro's own declared default
                // (PromptField.Value is already seeded with it by ParsePromptField) rather than showing the
                // dialog.
                if (!unattended && !PromptForFields(name, fields))
                    return false;   // cancelled

                // Assign the globals on the controller before the body runs (so $F=<file> jobs can read
                // them too) - folded into the same streamed buffer as everything else, never MDI.
                foreach (var field in fields)
                    buffer.Append(field.Param).Append('=').Append(field.Value).Append('\n');
            }
            else if (confirm && !unattended && UserPrompt.Show(string.Format("Run {0} macro?", name), "ioSender",
                        PromptButtons.YesNo, PromptIcon.Question) != PromptResult.Yes)
                return false;

            // Dry-run/verify mode: neutralise spindle-on (M3/M4), coolant-on (M7/M8) and tool-change (M6)
            // lines. Only needed here when preferJobView is FALSE: a preferJobView run (Work Order) ends up
            // as the real, non-transient GCode.File source - RunStreamedJobInPlace hands it to
            // RunControl.Run(0, false), which lands in JobControl.Run's ordinary Source.IsLoaded branch and
            // gets FULL protection there (StreamPump's own HasSpindleOrCoolantOn/HasToolChange check, from
            // the real parser, PLUS the G92 Z-offset clearance this streamer doesn't even provide) - so
            // neutralising here too was pure redundant double-handling on the exact same lines, not defense
            // in depth (confirmed while diagnosing a real hardware incident - see git history). Every OTHER
            // caller (Start Job, Auto Square, Stepper Calibration, Fixture probes) streams as a TRANSIENT
            // source, which StreamPump's own check explicitly EXCLUDES by design (dry-run must never leak
            // into a probing/wizard macro just because a loaded-job test left it armed - see
            // JobControl.Run's own comment) - for those, THIS is the only protection that exists, so it must
            // still run. Uses the real parser (not a regex) so a comment that happens to mention "M3" can't
            // cause a false positive - but only best-effort: a line the parser can't handle (some macro
            // directive/expression syntax this streamer tolerates that a strict parse might not) is left
            // exactly as it would have been before this existed, never blocked or altered.
            var dryRunParser = (model.IsDryRunMode && !preferJobView) ? new GCodeParser() : null;
            dryRunParser?.Reset();

            // Does this macro start a controller-side job ($F=<file> on an SD card)? Such a job acks
            // immediately and only THEN begins moving, so WAITIDLE after one has to allow time to observe
            // motion start before it can trust an Idle report. Nothing else does that - an ordinary burst has
            // already finished moving by the time Flush(wait:true) returns - so the long allowance is scoped
            // to the case that needs it instead of being charged to every (WAITIDLE) in every macro.
            bool sdJobPossible = false;
            foreach (var l in lines)
                if (l.TrimStart().StartsWith("$F=", StringComparison.OrdinalIgnoreCase))
                    sdJobPossible = true;

            // 3) Stream the G-code, holding at each (MBOX)/(WAITIDLE) and substituting prompt values.
            foreach (var raw in lines)
            {
                if (IsDirective(raw, "PREREQ"))
                    continue;

                if (IsDirective(raw, "PROMPT"))
                {
                    // Input prompts were collected up front; a bare (PROMPT) is just a run confirmation.
                    if (Body(raw, "PROMPT").Trim().Length == 0 && !unattended)
                    {
                        // Snapshot BEFORE Flush - see AbortedByAlarm's own comment on why sampling the
                        // CURRENT state after the burst already ran isn't enough.
                        long alarmBefore = model.AlarmEventCounter;
                        Flush(model, buffer, true, preferJobView);
                        if (AbortedByAlarm(model, name, alarmBefore))
                            return false;
                        if (UserPrompt.Show(string.Format("Run macro \"{0}\"?", name), "ioSender",
                                PromptButtons.OKCancel, PromptIcon.Question) != PromptResult.OK)
                            return false;
                    }
                    continue;
                }

                if (IsDirective(raw, "MBOX"))
                {
                    // Snapshot BEFORE Flush - see AbortedByAlarm's own comment.
                    long alarmBefore = model.AlarmEventCounter;
                    Flush(model, buffer, true, preferJobView);
                    // A burst just flushed above may have alarmed (e.g. a probe search that never triggered)
                    // without WaitForIdle in the picture at all - Flush only waits for the burst to reach SOME
                    // terminal StreamingState, it doesn't check WHICH one. Without this, the macro sailed
                    // straight on to the next (MBOX) as if nothing had gone wrong (confirmed on real hardware
                    // 2026-07-21: a failed spoilboard probe alarmed, then the very next prompt still popped up
                    // asking to position the gauge block, with the controller sitting in Alarm the whole time).
                    // Alarm-abort is checked even when unattended - this only skips the "are you ready" hold,
                    // never a real safety gate.
                    if (AbortedByAlarm(model, name, alarmBefore))
                        return false;
                    if (!unattended && !RunMBox(name, raw))
                        return false;   // Cancel / No - stop here
                    continue;
                }

                if (IsDirective(raw, "WAITIDLE"))
                {
                    Flush(model, buffer, true, preferJobView);
                    if (!WaitForIdle(model, sdJobPossible))
                    {
                        UserPrompt.Show(string.Format("Macro \"{0}\" aborted: the controller did not return to idle (alarm or connection lost).", name),
                            "ioSender", PromptButtons.OK, PromptIcon.Warning);
                        return false;
                    }
                    continue;
                }

                string line = SanitizeComment(ApplySubstitutions(raw, fields));
                buffer.Append(DryRunNeutralize(dryRunParser, line)).Append('\n');
            }
            Flush(model, buffer, false, preferJobView);   // final burst - fire and forget, same as always (don't block the caller on the physical run)

            return true;
        }

        // Persist a copy of every macro/generated program to %AppData%\ioSender\Generated\<name>.macro,
        // overwriting each run - a debugging aid so "what did Generate actually build" is always inspectable
        // on disk, since the streamed program itself (StreamProgram/RunStreamedJobInPlace) never touches the
        // filesystem. Best-effort: a write failure must never block the run itself. Public so a tab's own
        // Generate button can call it directly at generate-time (not just when MacroProcessor.Run streams it) -
        // the file is then on disk for post-mortem even if the run alarms out before completing, or the
        // operator never presses Run at all.
        public static void SaveGeneratedCopy(string name, string code)
        {
            try
            {
                var fileName = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".macro";
                System.IO.Directory.CreateDirectory(Resources.GeneratedFolder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(Resources.GeneratedFolder, fileName), code);
            }
            catch { /* best-effort - never let a diagnostic write block the actual run */ }
        }

        // Shared by every generator that parks at G30 (StartJobView, StepperCalibrationProbeWizard, ...):
        // lift to machine top, traverse to the G30 X/Y, then descend to G30 Z. #<_abs_x>/#<_abs_y> are grblHAL's
        // own live current-machine-position named parameters; every G53 move NAMES both axes explicitly (never a
        // bare "G53 G0 Z0") - a firmware bug sign-flips a homing-direction-inverted ($23) axis's parser base
        // after a G53 move that leaves it "unmoved", producing a false Alarm:2. 'L' emits one line (a caller's
        // own comment-sanitizing/line-numbering wrapper, or a plain StringBuilder.AppendLine).
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
            L("G53 G0 X[#<_abs_x>] Y[#<_abs_y>] Z0");   // lift Z to machine top, X/Y held at current
            L("G53 G0 X[#5181] Y[#5182]");              // traverse to G30 X/Y at the top
            L("G53 G0 X[#5181] Y[#5182] Z[#5183]");     // descend to G30 Z (X/Y named to avoid the unmoved-axis bug)
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
        // Applied to every streamed line (directives are consumed earlier, so only comments / g-code reach here).
        private static string SanitizeComment(string s)
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

        // Dry Run mode's spindle/coolant/tool-change suppression for THIS streamer - see the (WAITIDLE)
        // header comment's own note above. parser is null when Dry Run isn't armed (the common case), so
        // this is then a single null-check per line, not a parse. Best-effort when armed: a line the parser
        // throws on (some macro syntax this streamer otherwise tolerates without ever inspecting it) is
        // passed through unmodified rather than aborting the run - same as before this existed.
        private static string DryRunNeutralize(GCodeParser parser, string line)
        {
            if (parser == null || line.Length == 0 || line[0] == '(')   // pure comment/directive - nothing to neutralise
                return line;

            try
            {
                int tokenStart = parser.Tokens.Count;
                string toParse = line;
                // quiet:true is a DIFFERENT, lighter mode (see GCodeParser.ParseBlock's own early-out) that
                // validates a line is well-formed WITHOUT actually parsing words into Tokens at all - fine
                // for JobControl's own modal-state-only use of it, but useless here: this needs the REAL
                // tokens to inspect for M3/M4/M6/M7/M8, so it must be quiet:false. Root cause of a real
                // hardware incident - the spindle turning on during an armed Dry Run - confirmed via
                // [WO-DIAG] logging: IsDryRunMode really was True the whole way through and dryRunParser was
                // genuinely non-null, but every ParseBlock(quiet:true) call below silently produced zero
                // tokens, so this loop never matched anything and every line passed through unchanged.
                if (!parser.ParseBlock(ref toParse, false))
                    return line;

                for (int i = tokenStart; i < parser.Tokens.Count; i++)
                {
                    var t = parser.Tokens[i];
                    if (t is GCSpindleState && (t.Command == Commands.M3 || t.Command == Commands.M4))
                        return "()";
                    if (t is GCCoolantState && (t.Command == Commands.M7 || t.Command == Commands.M8))
                        return "()";
                    if (t.Command == Commands.M6)
                        return "()";
                }
            }
            catch
            {
                /* fail open - stream the line exactly as it would have been sent before Dry Run awareness existed */
            }

            return line;
        }

        // Send the accumulated g-code. EVERY burst - however small - goes through the flow-controlled job
        // streamer, never the MDI path: MDI has no character-counting flow control, so a burst sent that way
        // can overrun the controller's serial buffer (hanging it), blocks the UI thread while it goes out
        // synchronously, and leaves Feed Hold/Stop queued BEHIND it instead of taking effect immediately.
        // Reserving MDI for operator-typed text only also means a sender-side content filter (e.g. dry-run
        // mode's spindle/coolant suppression, see GCodeBlock.HasSpindleOrCoolantOn) is a hard guarantee for
        // every macro/wizard/probing run - it can't be bypassed by a burst that happens to look "too small
        // to matter".
        //
        // 'wait': RunStreamedJobInPlace only KICKS OFF a burst (Cycle Start is deferred to a dispatcher tick,
        // it does not run synchronously here) - it shares one RunControl.Source field across every burst, so a
        // second Flush() before the first burst's deferred Cycle Start has even fired would silently overwrite
        // it and drop the first burst entirely. Callers with more macro content still to come (before an
        // MBOX/WAITIDLE/prompt gate) MUST pass wait=true so this burst genuinely finishes first - restoring the
        // strict ordering the old MDI queue gave for free. The macro's FINAL burst passes wait=false (fire and
        // forget) so a "Run" click doesn't block until the physical job completes.
        private static void Flush(GrblViewModel model, StringBuilder buffer, bool wait, bool preferJobView = false)
        {
            if (buffer.Length == 0)
                return;

            string code = buffer.ToString();
            buffer.Clear();

            var lines = code.Replace("\r", string.Empty).Split('\n');

            bool hasOwordOrExpr = false;
            foreach (var l in lines)
            {
                string t = l.Trim();
                if (t.Length == 0)
                    continue;
                if (t.IndexOf("O<", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf('#') >= 0)
                    hasOwordOrExpr = true;
            }

            // O-word/#-expression lines can only be streamed verbatim when the controller evaluates
            // expressions itself (GCodeJob's passthrough, unnumbered); a controller that doesn't report
            // that support cannot be sent this content at all through ioSender - there is no safe fallback
            // (MDI is reserved for typed text), so refuse outright rather than send it unfiltered.
            if (hasOwordOrExpr && !GrblInfo.ExpressionsSupported)
            {
                UserPrompt.Show("This macro uses O-word/parameter (#) syntax, which needs the controller to support NGC expressions (EXPR). This controller does not report that support, so ioSender cannot run it.",
                    "ioSender", PromptButtons.OK, PromptIcon.Error);
                return;
            }

            if (RunStreamedJobInPlace == null)
            {
                // No streamer wired - refuse rather than flood (Feed Hold / Stop would not work).
                UserPrompt.Show(string.Format("Cannot run this program safely: the job streamer is not available, so motion would be sent without flow control and Feed Hold / Stop would be unresponsive.\r\n\r\nLoad the program in the Grbl tab and run it from there instead."),
                    "ioSender", PromptButtons.OK, PromptIcon.Error);
                return;
            }

            StreamProgram(model, lines, wait, preferJobView);
        }

        // Hand a g-code burst to the host to stream with full flow control, into a ProgramView. By default
        // WITHOUT touching the loaded job - a run flushes several bursts (a park move, then each O<...> CALL);
        // every one takes this path, so a run never overwrites the loaded program or hijacks the Job tab.
        // preferJobView opts a caller OUT of that guarantee - see RunStreamedJobInPlace's own comment.
        private static void StreamProgram(GrblViewModel model, string[] lines, bool wait, bool preferJobView = false)
        {
            var code = new List<string>();
            foreach (var l in lines)
            {
                string t = l.Trim();
                if (t.Length > 0)
                    code.Add(t);
            }
            if (code.Count == 0)
                return;

            bool done = false;
            RunStreamedJobInPlace.Invoke(model, _streamName, code.ToArray(), !wait, preferJobView, () =>
            {
                done = true;
                DebugLog.Write("macro", string.Format("StreamProgram: onDone fired, StreamingState={0} GrblState={1}",
                    model.StreamingState, model.GrblState.State));
            });

            if (wait)
                while (!done)
                    EventUtils.DoEvents();

            DebugLog.Write("macro", string.Format("StreamProgram: wait loop exited (wait={0}), StreamingState={1} GrblState={2}",
                wait, model.StreamingState, model.GrblState.State));
        }

        // Checked right after every Flush(wait:true) that precedes a (MBOX)/(PROMPT) dialog - a burst that
        // just alarmed (e.g. a probe search that never triggered) or lost the connection must stop the
        // macro here, not sail on to the next prompt as if the burst had succeeded. Same Alarm/Unknown check
        // WaitForIdle already uses for the same reason, just reached from a different gate (WAITIDLE isn't
        // the only place a burst's outcome needs checking - any MBOX/PROMPT right after G-code content does).
        // alarmBefore: model.AlarmEventCounter captured BEFORE the burst that just ran (Flush) was sent - a
        // latch, not a sampled value, so an alarm the operator already cleared (Reset+Unlock) faster than
        // this check runs is still caught, instead of being silently missed the way sampling only the
        // CURRENT GrblState.State would. See GrblViewModel.AlarmEventCounter's own comment - confirmed as a
        // real bug 2026-08-01, same race class as the fix documented above this method's own call sites.
        private static bool AbortedByAlarm(GrblViewModel model, string name, long alarmBefore)
        {
            if (model.AlarmEventCounter == alarmBefore && model.GrblState.State != GrblStates.Alarm && model.GrblState.State != GrblStates.Unknown)
                return false;
            UserPrompt.Show(string.Format("Macro \"{0}\" aborted: the controller alarmed (or the connection was lost) mid-run.", name),
                "ioSender", PromptButtons.OK, PromptIcon.Warning);
            return true;
        }

        // If 'code' is a single "@<path>" reference, replace it with the referenced file's current
        // contents (re-read on every run). Relative paths resolve against the config folder.
        // Returns false (after a message) if the file cannot be read.
        private static bool ResolveFileReference(ref string code, string name)
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

        // Block (while keeping the UI pumped) until a controller-side job has started and then
        // returned to Idle. Returns false if the controller alarms or the link appears lost.
        // The wait runs on the UI thread, so it pumps the dispatcher the same way the rest of
        // the app does (see Grbl.WaitForIdle) - background threads observe controller responses
        // while EventUtils.DoEvents keeps status reports (and the UI) flowing.
        // How long to allow for motion to be observed STARTING before concluding it already finished. Only a
        // controller-side job ($F=) can begin moving after its ack, and only then is the long allowance
        // justified; for everything else Flush(wait:true) has already returned on a real Idle detection, so
        // the full 2s was dead time charged to every (WAITIDLE) - three of them in a Start Job program, which
        // is most of the multi-second stalls seen between steps. The short value still spans two status
        // reports at the usual ~200ms cadence.
        private const int ObserveMotionStartMs = 400;

        private const int ObserveMotionStartSdMs = 2000;

        private static bool WaitForIdle(GrblViewModel model, bool sdJobPossible = false)
        {
            DebugLog.Write("macro", string.Format("WaitForIdle: enter, StreamingState={0} GrblState={1}",
                model.StreamingState, model.GrblState.State));

            // NOTE: this used to hard-fail immediately if model.StreamingState == StreamingState.Send on
            // entry ("a (WAITIDLE) reached while a flow-controlled job is streaming is a program/structure
            // error"). That check is gone: RunStreamedJobInPlace streams OUR OWN macro bursts through the
            // exact same StreamingState machinery a real loaded-job run uses, and the two are indistinguishable
            // from here. In practice, right after Flush(wait:true) returns, StreamingState can already be back
            // to Send/GrblState=Run because the burst's own trailing motion (e.g. a G30 park rapid) resumed a
            // moment after RestoreSourceOnEnd's Idle detection fired onDone - confirmed via DebugLog("macro")
            // tracing + the raw comms log 2026-07-21 (StartJobView and StepperCalibrationProbeWizard both hit
            // this). The polling below already handles that correctly by construction - it reads GrblState.State
            // fresh on every incoming status report (not gated on a property VALUE change like a PropertyChanged
            // subscription would be), waits for genuine motion to start, then requires two consecutive real Idle
            // reports before trusting completion - so falling straight into it here just waits out that trailing
            // window instead of aborting on it. A GENUINELY still-running unrelated job is still bounded by the
            // stall/disconnect check below (2 consecutive silent report timeouts), so this can't hang forever.
            var token = new CancellationToken();

            // Snapshot the alarm latch before waiting - see GrblViewModel.AlarmEventCounter's own comment.
            // Checked alongside (not instead of) the sampled GrblState.State below: the counter catches an
            // alarm the operator already cleared (Reset+Unlock) faster than this loop happened to poll,
            // which the sampled state alone would miss entirely - confirmed as a real bug 2026-08-01, where
            // that exact race let a macro silently continue past a probe-failure alarm the operator had
            // already manually cleared, moving on to its next step as if nothing had happened.
            long alarmCountAtStart = model.AlarmEventCounter;

            // A $F= job acks immediately and only then starts running, so first wait briefly for
            // the controller to actually leave Idle before watching for it to return - otherwise
            // the very first status report could still show the pre-run Idle and we would finish early.
            var sw = Stopwatch.StartNew();
            bool started = model.GrblState.State != GrblStates.Idle;

            int observeStartMs = sdJobPossible ? ObserveMotionStartSdMs : ObserveMotionStartMs;

            while (!started && sw.ElapsedMilliseconds < observeStartMs)
            {
                PumpForReport(model, token, 500);

                if (model.AlarmEventCounter != alarmCountAtStart || model.GrblState.State == GrblStates.Alarm || model.GrblState.State == GrblStates.Unknown)
                {
                    DebugLog.Write("macro", string.Format("WaitForIdle: abort - alarm seen (or GrblState={0}) while waiting to observe motion start", model.GrblState.State));
                    return false;
                }

                started = model.GrblState.State != GrblStates.Idle;
            }

            if (!started)
            {
                DebugLog.Write("macro", string.Format("WaitForIdle: never observed motion start within {0}ms - treating as already-finished", observeStartMs));
                return true;    // job finished (or produced no motion) before we could observe it running
            }

            // Wait for completion. Require two consecutive Idle reports since the planner can briefly
            // drain mid-job; bail out if status reports stop arriving (stalled or disconnected).
            int idleStreak = 0, silentReports = 0;

            while (true)
            {
                if (!PumpForReport(model, token, 5000))
                {
                    if (++silentReports >= 2)
                    {
                        DebugLog.Write("macro", "WaitForIdle: abort - 2 consecutive silent report timeouts (stalled/disconnected)");
                        return false;
                    }
                    continue;
                }
                silentReports = 0;

                if (model.AlarmEventCounter != alarmCountAtStart)
                {
                    DebugLog.Write("macro", "WaitForIdle: abort - alarm seen (latched) while waiting for completion");
                    return false;
                }

                switch (model.GrblState.State)
                {
                    case GrblStates.Alarm:
                    case GrblStates.Unknown:
                        DebugLog.Write("macro", string.Format("WaitForIdle: abort - GrblState={0} while waiting for completion", model.GrblState.State));
                        return false;

                    case GrblStates.Idle:
                        if (++idleStreak >= 2)
                        {
                            DebugLog.Write("macro", "WaitForIdle: success - 2 consecutive Idle reports");
                            return true;
                        }
                        break;

                    default:
                        idleStreak = 0;
                        break;
                }
            }
        }

        // Wait (pumping the UI) for the next response/status report from the controller.
        // Returns true if one arrived within msTimeout, false on timeout.
        private static bool PumpForReport(GrblViewModel model, CancellationToken token, int msTimeout)
        {
            bool? res = null;

            new Thread(() =>
            {
                res = WaitFor.SingleEvent<string>(
                    token,
                    null,
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    msTimeout);
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            return res == true;
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

        // The stored positions PREREQ can require that are MACHINE positions - i.e. ones a program
        // actually rapids to (G53 G0 X[#5181]...). G54..G59.3/G92 are work OFFSETS, not targets, so
        // the envelope check below deliberately does not apply to them.
        private static readonly HashSet<string> StoredMachinePositionCodes = new HashSet<string> { "G28", "G30" };

        // Is a stored machine position (G28/G30) actually REACHABLE under the controller's soft limits?
        // Returns null when it is (or when the question doesn't apply), otherwise the failure text.
        //
        // Being "set" is not enough. A G30 recorded when the pull-off was smaller - or with soft limits
        // off, or via an MPG - stays in the controller's non-volatile storage at a coordinate the parser
        // will now refuse, and NOTHING in the sender noticed: every generator that parks at G30
        // (Start Job, Work Order, stepper calibration, all via EmitGotoG30) emitted a program that
        // streamed happily and then threw Alarm:2 on the park line, mid-run, with the operator watching.
        // Observed 2026-08-06: [G30:751.428,-836.262,-34.000] against $131=840 / $27=6 puts Y 2.262 mm
        // outside its envelope, so "G53 G0 X[#5181] Y[#5182]" alarmed at Ln:220 - and the failure looked
        // like a generator bug because the preceding line used #<_abs_x>/#<_abs_y>.
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
            if (!StoredMachinePositionCodes.Contains(code) || GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) != 1)
                return null;

            // The firmware only envelope-checks axes it considers homed; -1 = the $# read gave us no
            // [HOME:] line at all, so we know nothing and must not guess.
            int homed = GrblWorkParameters.HomedMask;
            if (homed <= 0)
                return null;

            var cs = GrblWorkParameters.GetCoordinateSystem(code);
            if (cs == null)
                return null;

            double pulloff = GrblSettings.GetInteger(GrblSetting.HardLimitsEnable) == 1
                              ? GrblSettings.GetDouble(GrblSetting.HomingPulloff) : 0d;

            for (int i = 0; i < GrblInfo.NumAxes; i++)
            {
                if ((homed & (1 << i)) == 0 || double.IsNaN(cs.Values[i]))
                    continue;

                double travel = Math.Abs(GrblInfo.MaxTravel.Values[i]);
                if (travel <= 0d)
                    continue;   // $13x not configured for this axis - no envelope to check against

                double lo, hi;
                if (GrblInfo.ForceSetOrigin)
                {
                    if (GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(i))) { lo = 0d; hi = travel - pulloff; }
                    else { lo = -(travel - pulloff); hi = 0d; }
                }
                else { lo = -(travel - pulloff); hi = -pulloff; }

                double pos = cs.Values[i], limit = pos < lo ? lo : hi;
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
            // keyword+non-letter rule IsDirective uses (kept for the argument-taking directives, and
            // unchanged inside Run() itself for macro back-compat) recognized a loaded file's ordinary
            // comment "(WAITIDLE barrier test ...)" as a live directive on the very first hardware test
            // of the pump's WAITIDLE barrier, 2026-08-08 - at load time this runs against ARBITRARY
            // g-code files, not just hand-written macros, so prose comments are a real input.
            string t = line.Trim();
            if (t.Length > 1 && t[0] == '(' && t[t.Length - 1] == ')' &&
                t.Substring(1, t.Length - 2).Trim().Equals("WAITIDLE", StringComparison.OrdinalIgnoreCase))
                return "WAITIDLE";

            foreach (var keyword in new[] { "PREREQ", "PROMPT", "MBOX" })
                if (IsDirective(line, keyword))
                    return keyword;
            return null;
        }

        // True if the trimmed line is the named directive, e.g. "(MBOX ...)" / "(PREREQ ...)".
        private static bool IsDirective(string line, string keyword)
        {
            string t = line.TrimStart();
            if (!t.StartsWith("("))
                return false;
            t = t.Substring(1).TrimStart();
            return t.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
                   (t.Length == keyword.Length || !char.IsLetter(t[keyword.Length]));
        }

        // The text inside the parentheses after the keyword (and the following comma/space), e.g.
        // "(MBOX, OKCANCEL, hi)" -> "OKCANCEL, hi".
        private static string Body(string line, string keyword)
        {
            string t = line.Trim();
            int close = t.LastIndexOf(')');
            string inner = (close >= 1 ? t.Substring(1, close - 1) : t.Substring(1)).TrimStart();
            inner = inner.Substring(Math.Min(keyword.Length, inner.Length));   // drop the keyword
            return inner.TrimStart(' ', ',');
        }
    }
}
