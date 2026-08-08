/*
 * StreamPump.cs - part of CNC Controls library
 *
 * Background G-code send/ack pump - runs the job flow control off the WPF UI thread.
 *
 */

/*

The job line-pump traditionally runs on the WPF UI thread: controller acks are marshalled to the UI
dispatcher, and ResponseReceived -> SendNextLine sends the next line there. Heavy UI work (3D view,
grid scroll) then delays the dispatcher, the next line goes out late, the controller's planner buffer
drains, and motion stutters.

StreamPump moves ONLY the latency-critical send/ack flow control onto a dedicated background thread:

  - Acks are tapped straight off the comms read thread via Comms.com.ReplyClassified (no UI dispatcher;
    replaced the old single-purpose AckSink 2026-08-08 - see docs/Architecture-Unified-Streaming-Engine.md),
    fed into a BlockingCollection the pump thread consumes. Status reports arrive on the same event now
    too (logged only for now - not yet consumed by real dispatch logic).
  - The pump does standard grbl character-counting flow control (keep <= serialSize bytes in flight)
    and writes job lines directly via Comms (BlockingWrites = synchronous, so back-to-back lines can't
    overlap; blocks only this thread - desired backpressure - never the UI).
  - Display only (the grid "Sent" marks, BlockExecuting, ScrollPosition) is marshalled back to the UI,
    coalesced at Background priority so a fast job can't flood the dispatcher. The pump's progress never
    waits on the display drain.
  - The state machine stays on the UI thread (JobControl). The pump just signals job-finished / error.

Threading contract: every accounting field below is touched ONLY by the pump thread (after Start). The
UI thread interacts only through Start/Abort and the volatile PendingLine/Suspended/IsActive flags.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace CNC.Core
{
    // Lightweight file tracer for diagnosing stay-put/macro streaming (Load Stock). Writes to
    // %TEMP%\iosender-startjob.log. Cleared at the start of each small (<200-block) run so each
    // reproduction is self-contained; large cutting jobs are not traced (Enabled=false) to avoid bloat.
    public static class PumpLog
    {
        private static readonly object gate = new object();
        public static bool Enabled = false;
        public static readonly string FilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iosender-startjob.log");

        public static void Clear() { try { System.IO.File.WriteAllText(FilePath, string.Empty); } catch { } }

        public static void W(string msg)
        {
            if (!Enabled)
                return;
            try { lock (gate) System.IO.File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n"); }
            catch { }
        }
    }

    public class StreamPump
    {
        // one in-flight line awaiting its ack; Index = -1 for the synthetic M0 a breakpoint appends
        private struct Sent
        {
            public int Index;
            public int Length;
            public Sent(int index, int length) { Index = index; Length = length; }
        }

        private readonly GrblViewModel model;
        // How a coalesced display update gets onto the host's UI thread. Injected rather than taken as a
        // Dispatcher so this class carries no WPF - but ALSO so the host keeps the priority decision, which
        // matters here: this marshals per-line status markers into the program list, and ioSender posts it at
        // DispatcherPriority.Background deliberately so it can never compete with streaming or with operator
        // input. UiContext.Post would run it at Normal, which is the wrong direction (see the Feed Hold
        // starvation fix in AppConfig.OpenStreamFor for what Normal-priority marshalling costs).
        // Null = no display to update; the marks are still dequeued, just never rendered.
        private readonly System.Action<System.Action> displayMarshal;

        // How a STATE-MACHINE callback (job finished / error / check-mode error) gets onto the host's UI
        // thread. Deliberately separate from displayMarshal: ioSender posts these at DispatcherPriority
        // Normal, because control flow must outrank the coalesced status-marker drain above. Collapsing the
        // two would either starve the state machine behind display work or promote display work to compete
        // with streaming. Null = run inline on the pump thread (headless).
        private readonly System.Action<System.Action> controlMarshal;

        // Probe-streaming throttle: once a probe (G38) has been streamed, cap look-ahead to this many
        // lines. Lives with the pump that enforces it; JobControl's legacy sender reads it from here.
        public const int ProbeLookahead = 10;

        private IProgramSource source;
        private int serialSize;
        private bool useBuffering, sendComments, startSimulator;
        private System.Action onJobFinished;
        private System.Action<string> onError;
        // Check mode ($C): every line is streamed and reported regardless of error, instead of the normal
        // abort-on-first-error behavior - see onCheckError's own comment. No error text/line index is
        // passed through: MarkSent (below) already writes the real per-line response (including error
        // text) into Source.Data for every line via the normal coalesced Drain path, same as any other
        // run - onCheckError only needs to drive the state-machine bookkeeping that path doesn't cover.
        private bool continueOnError;
        private System.Action onCheckError;

        // ---- pump-thread-owned accounting (no locking; single-thread access after Start) ----
        private int sendIdx;            // next block index to send (-1 = nothing left)
        private int pgmEndLine;         // last block to send (program-end or RunToBlock bound)
        private int serialUsed;         // bytes sent but not yet acked
        private bool started;           // toggled by the "%" demarcation
        private bool probePending, jobHasProbe;
        private readonly Queue<Sent> inflight = new Queue<Sent>();

        // (WAITIDLE) dispatch barrier - the first directive the pump acts on itself (unified streaming
        // engine Step 3, docs/Architecture-Unified-Streaming-Engine.md). Armed when SendNext consumes a
        // Directive=="WAITIDLE" row (sender-side, nothing written to the wire); released on the pump
        // thread once everything outstanding is acked AND two consecutive <Idle| status reports arrive -
        // the same success condition MacroRunner.WaitForIdle proved on hardware, just event-driven.
        // volatile: the pump thread owns arming/clearing, but the comms READ thread reads it to decide
        // whether to forward status sentinels into the ack channel (below), so the flag itself is the
        // one cross-thread toggle. idleStreak stays pump-thread-only.
        private volatile bool waitIdleBarrier;
        private int idleStreak;

        // (MBOX ...) dispatch barrier (unified streaming engine Step 4a). Armed when SendNext consumes
        // a Directive=="MBOX" row; the prompt is shown (host UI thread, via controlMarshal) only once
        // everything before it is ACKED - faithful to MacroRunner's Flush(wait:true)-then-prompt
        // ordering. Deliberately NOT motion-idle: macros that need motion done first already chain
        // (WAITIDLE) before (MBOX), and both tc.macro and pcorner.macro do exactly that. OK releases
        // through the ack-channel sentinel; Cancel routes to the HOST's own Stop path (onOperatorCancel,
        // wired to JobRunner.Stop) because prior moves may still be physically executing - user-confirmed
        // design decision over a soft-finish, 2026-08-08.
        private volatile bool mboxBarrier;
        private bool mboxPromptPending;         // pump thread only: prompt not yet dispatched to the host
        private string mboxLine;                // the raw (MBOX ...) row text, parsed by MacroRunner.ShowMBox
        private System.Action onOperatorCancel; // host's Stop path; null (headless, no host) falls back to onError

        // (PROMPT ...) field values collected by the host up front (Step 4b) - substituted into each
        // line at SEND time so the stored program rows stay untouched (inspection/re-run stability,
        // same principle as dry-run's local line rewrites). Null/empty = no prompts, zero cost.
        private List<MacroRunner.PromptField> promptFields;

        private Thread thread;
        private BlockingCollection<string> acks;
        private CancellationTokenSource cts;
        private volatile bool aborted;

        // ---- cross-thread state ----
        public volatile int PendingLine;    // last acked real line - read by JobControl for the tool-change boundary
        public volatile bool Suspended;     // UI sets this during a tool change so jog/MDI acks aren't consumed as job acks
        public volatile bool IsActive;      // a job is streaming through the pump
        // Set by JobRunner.OnLineNumberChanged once the controller's reported Ln: has actually matched a
        // block of THIS program - i.e. proof that execution-driven progress works here. While it is set, the
        // ack below stops writing "ok" markers, because they would run ahead of the tool by the whole planner
        // buffer; the line-number handler owns them instead. Never set for a program without N words, or a
        // controller that does not report Ln:, so those keep the original ack-driven display.
        public volatile bool ExecutionDrivenProgress;

        // ---- coalesced display marshaling (UI thread drains) ----
        private readonly ConcurrentQueue<KeyValuePair<int, string>> sentMarks = new ConcurrentQueue<KeyValuePair<int, string>>();
        private volatile int latestBlock = -1, latestScroll = -1;
        private int drainPending = 0;
        // Count of modal-reset prolog lines (source.Commands) sent ahead of the first block on a mid-program
        // start; their acks are swallowed in Run so they don't advance job-line accounting.
        private int preambleAcks = 0;

        public StreamPump(GrblViewModel model, System.Action<System.Action> controlMarshal,
                          System.Action<System.Action> displayMarshal)
        {
            this.model = model;
            this.controlMarshal = controlMarshal;
            this.displayMarshal = displayMarshal;
        }

        private void PostControl(System.Action action)
        {
            if (controlMarshal != null)
                controlMarshal(action);
            else
                action();
        }

        public void Start(IProgramSource source, int fromBlock, int pgmEndLine, int serialSize, bool useBuffering,
                          bool sendComments, bool startSimulator, System.Action onJobFinished, System.Action<string> onError,
                          bool continueOnError = false, System.Action onCheckError = null, System.Action onOperatorCancel = null,
                          List<MacroRunner.PromptField> promptFields = null)
        {
            this.promptFields = (promptFields != null && promptFields.Count > 0) ? promptFields : null;
            this.source = source;
            this.serialSize = serialSize;
            this.useBuffering = useBuffering;
            this.sendComments = sendComments;
            this.startSimulator = startSimulator;
            this.onJobFinished = onJobFinished;
            this.onError = onError;
            this.continueOnError = continueOnError;
            this.onCheckError = onCheckError;
            this.onOperatorCancel = onOperatorCancel;

            sendIdx = fromBlock;
            this.pgmEndLine = pgmEndLine;
            serialUsed = 0;
            started = probePending = jobHasProbe = false;
            waitIdleBarrier = false;
            idleStreak = 0;
            mboxBarrier = false;
            mboxPromptPending = false;
            mboxLine = null;
            inflight.Clear();
            while (sentMarks.TryDequeue(out _)) { }
            latestBlock = latestScroll = -1;
            drainPending = 0;

            PendingLine = fromBlock;
            Suspended = false;
            ExecutionDrivenProgress = false;   // re-proved per run, never carried over from the last program
            aborted = false;
            IsActive = true;

            cts = new CancellationTokenSource();
            acks = new BlockingCollection<string>();

            // PumpLog is enabled/cleared by MacroProcessor for stay-put macro runs (Load Stock); normal jobs leave
            // it disabled. Just record the pump's start parameters when tracing is on.
            PumpLog.W(string.Format("PUMP START from={0} pgmEnd={1} blocks={2} serialSize={3} useBuffering={4} sendComments={5}",
                fromBlock, pgmEndLine, source?.Blocks, serialSize, useBuffering, sendComments));

            Comms.com.BlockingWrites = true;
            // Tap classified replies straight off the read thread. -= before += : ReplyClassified is a
            // real multicast event (2026-08-08, replaced the old single-purpose AckSink property), and
            // JobRunner REUSES this same pump instance across jobs rather than recreating it - a
            // property assignment always safely replaced the old handler, but += accumulates, so a
            // Start() that ever ran before Abort()/Run()'s finally unsubscribed the previous one would
            // silently double-process every ack. -= on a not-currently-subscribed handler is a no-op, so
            // this is always safe regardless of what state the previous run left it in.
            Comms.com.ReplyClassified -= OnReplyClassified;
            Comms.com.ReplyClassified += OnReplyClassified;

            // Re-establish modal state for a mid-program start (units / plane / distance mode): "Start from this
            // toolpath" queues a G90 G94 / G17 / G21 prolog on source.Commands. The legacy streamer drained that
            // via SendNextLine, but this pump streams source.Data directly - so send those lines here, FIRST,
            // ahead of the first block. Their acks are swallowed in Run (they are not job lines). Without this the
            // run inherits whatever units the controller was left in; if it was G20, the toolpath's literal mm
            // coordinates are read as inches -> targets off the table -> Alarm:2 soft limit on the first rapid.
            preambleAcks = 0;
            if (source.Commands != null)
                while (source.Commands.Count > 0)
                {
                    Comms.com.WriteCommand(source.Commands.Dequeue());
                    preambleAcks++;
                }

            thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "StreamPump" };
            thread.Start();
        }

        // Called on the UI thread to stop the pump (Stop/Reset/Alarm/connection-lost). Idempotent.
        public void Abort()
        {
            aborted = true;
            if (Comms.com != null)
                Comms.com.ReplyClassified -= OnReplyClassified;   // stop routing replies to a dying pump
            cts?.Cancel();                  // unblock the ack Take
        }

        // The classified-reply handler installed on Comms.com for the pump's lifetime (see Start/Abort/
        // Run's finally). Runs ON THE COMMS READ THREAD - must not block (see the interface doc-comment).
        // Ack/Nak: EXACTLY the old AckSink closure's behavior, unchanged - dropped while Suspended (tool
        // change) so jog/MDI acks are not mistaken for job-line acks; they fall through to the UI path,
        // which ignores them. Status: new 2026-08-08 - logged only for now, so the signal's presence can
        // be confirmed on real hardware; nothing consumes it yet. The WAITIDLE dispatch barrier that will
        // actually use it is separate, not-yet-built work - see docs/Architecture-Unified-Streaming-Engine.md.
        private void OnReplyClassified(Comms.ReplyClass cls, string reply)
        {
            if (Suspended)
                return;
            if (cls == Comms.ReplyClass.Ack || cls == Comms.ReplyClass.Nak)
                acks.Add(reply);
            else if (cls == Comms.ReplyClass.Status)
            {
                if (DebugLog.Enabled)
                    // DebugLog, not PumpLog: PumpLog writes to a DIFFERENT file (%TEMP%\iosender-startjob.log)
                    // and is disabled by default - the wrong choice for "confirm this signal reaches the pump
                    // on real hardware", which is the whole point right now. DebugLog is what -debuglog/
                    // latest_debug.log already show for [jobrunner]/[jog] this session.
                    DebugLog.Write("pump", "STATUS " + reply);
                // (WAITIDLE) barrier feed: classify idle-or-not HERE (a string prefix check, cheap and
                // non-blocking) and let the pump thread do everything else. Gated on the armed flag so
                // ordinary jobs never carry status traffic in their ack channel.
                if (waitIdleBarrier)
                    acks.Add(reply.StartsWith("<Idle") ? StatusIdleTick : StatusBusyTick);
            }
        }

        // Sentinel pushed through the ack channel by KickIdle so the nudge is handled on the pump thread (the
        // owner of serialUsed/inflight/sendIdx) - never touch that accounting from the UI thread.
        private const string IdleKick = "\0idlekick";

        // Status-report sentinels for the (WAITIDLE) barrier, same ack-channel trick as IdleKick: the
        // read thread classifies the report (idle or not) and enqueues one of these; the pump thread -
        // the owner of inflight/idleStreak - does all the actual deciding. Only enqueued while the
        // barrier is armed, so normal jobs never pay for the ~5/s status traffic in their ack channel.
        private const string StatusIdleTick = "\0status-idle";
        private const string StatusBusyTick = "\0status-busy";

        // (MBOX) answer sentinels: pushed into the ack channel from the HOST's UI thread when the
        // operator answers the prompt, handled on the pump thread like everything else that touches
        // dispatch accounting. BlockingCollection.Add is thread-safe, so this needs no extra locking.
        private const string MboxOkTick = "\0mbox-ok";
        private const string MboxCancelTick = "\0mbox-cancel";

        // Called from the UI thread when the controller is confirmed idle while the pump still believes a job is
        // streaming - i.e. the pump stalled (it thinks the controller's buffer is full, but an idle controller has
        // drained it, so some acks must have been missed; or all lines were sent but a tail ack never arrived).
        // An O-word/macro program (Load Stock) can hit this. Handled on the pump thread via the ack channel.
        public void KickIdle()
        {
            PumpLog.W(string.Format("KICK requested  aborted={0}", aborted));
            if (!aborted && acks != null)
                try { acks.Add(IdleKick); } catch { }
        }

        private void Run()
        {
            try
            {
                SendNext();                 // initial buffer fill
                while (!aborted)
                {
                    string ack;
                    try { ack = acks.Take(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (aborted)
                        break;
                    // Barrier status sentinels are not acks - route them out BEFORE the preamble swallow
                    // or a WAITIDLE early in a program with a modal-reset prolog would eat them as acks.
                    if (ack == StatusIdleTick || ack == StatusBusyTick)
                    {
                        OnBarrierStatus(ack == StatusIdleTick);
                        continue;
                    }
                    if (ack == MboxOkTick || ack == MboxCancelTick)
                    {
                        OnMboxAnswer(ack == MboxOkTick);
                        continue;
                    }
                    if (preambleAcks > 0 && ack != IdleKick)
                    {
                        preambleAcks--;     // swallow a modal-reset prolog ack - not a job line
                        continue;
                    }
                    if (ack == IdleKick)
                        OnIdleKick();
                    else
                        OnAck(ack);
                }
            }
            catch (Exception)
            {
                // never let a pump-thread exception take down the app; the UI state machine still owns recovery
            }
            finally
            {
                if (Comms.com != null)
                {
                    Comms.com.ReplyClassified -= OnReplyClassified;
                    Comms.com.BlockingWrites = false;
                }
                IsActive = false;
            }
        }

        // Send lines while there is RX-buffer room (grbl character counting), honouring the probe barrier.
        private void SendNext()
        {
            while (sendIdx >= 0 && !aborted)
            {
                if (probePending || waitIdleBarrier || mboxBarrier)   // hold everything: probe in flight, (WAITIDLE) waiting out motion, or (MBOX) awaiting the operator
                {
                    PumpLog.W(string.Format("HOLD barrier  sendIdx={0} inflight={1} used={2} waitIdle={3} mbox={4}", sendIdx, inflight.Count, serialUsed, waitIdleBarrier, mboxBarrier));
                    break;
                }

                GCodeBlock block = source.Data[sendIdx];

                // (WAITIDLE) - consumed by the SENDER, never written to the wire (unified streaming
                // engine Step 3; MacroRunner.Run did exactly this in its own loop). Arm the barrier and
                // stop dispatching; OnBarrierStatus releases it once everything outstanding is acked and
                // two consecutive <Idle| reports prove the physical motion is done - an ack only proves
                // the controller BUFFERED a move, which is the race WAITIDLE exists to close. Skipped in
                // check mode ($C, continueOnError): the controller reports <Check|, never <Idle|, so an
                // armed barrier could never release - and a syntax check has no motion to wait out anyway.
                if (block.Directive == "WAITIDLE" && !continueOnError)
                {
                    bool wLast = pgmEndLine == sendIdx;
                    MarkSent(sendIdx, "wait");
                    latestBlock = sendIdx;
                    ScheduleDrain();
                    waitIdleBarrier = true;
                    idleStreak = 0;
                    PumpLog.W(string.Format("WAITIDLE armed @idx={0} inflight={1} last={2}", sendIdx, inflight.Count, wLast));
                    if (DebugLog.Enabled)   // PumpLog is off for ordinary jobs - arm/clear are rare, log both ways
                        DebugLog.Write("pump", string.Format("WAITIDLE armed @idx={0} inflight={1} last={2}", sendIdx, inflight.Count, wLast));
                    sendIdx = wLast ? -1 : sendIdx + 1;
                    break;
                }

                // (MBOX ...) - consumed by the SENDER (see the mboxBarrier field comment for the full
                // design). Prompt deferred to MaybeShowMbox: it must not appear until everything before
                // this row is acked. Skipped in check mode, same reasoning as WAITIDLE - a syntax check
                // has no operator steps to hold for.
                if (block.Directive == "MBOX" && !continueOnError)
                {
                    bool mLast = pgmEndLine == sendIdx;
                    MarkSent(sendIdx, "hold");
                    latestBlock = sendIdx;
                    ScheduleDrain();
                    mboxBarrier = true;
                    mboxPromptPending = true;
                    mboxLine = block.Data;
                    PumpLog.W(string.Format("MBOX armed @idx={0} inflight={1} last={2}", sendIdx, inflight.Count, mLast));
                    if (DebugLog.Enabled)
                        DebugLog.Write("pump", string.Format("MBOX armed @idx={0} inflight={1} last={2}", sendIdx, inflight.Count, mLast));
                    sendIdx = mLast ? -1 : sendIdx + 1;
                    MaybeShowMbox();   // nothing outstanding? prompt right away
                    break;
                }

                // (PROMPT ...) rows (Step 4b): the FIELD form's work happened up front in JobRunner
                // (dialog + preamble assignments), so its row is consumed as a no-op. The BARE form is
                // a mid-stream operator checkpoint - reuse the MBOX barrier wholesale with a canned
                // confirmation; Cancel takes the same proven Abort() route.
                if (block.Directive == "PROMPT" && !continueOnError)
                {
                    if (!MacroRunner.IsBarePrompt(block.Data))
                    {
                        MarkSent(sendIdx, "ok");
                        latestBlock = sendIdx;
                        ScheduleDrain();
                        bool pLast = pgmEndLine == sendIdx;
                        sendIdx = pLast ? -1 : sendIdx + 1;
                        if (pLast)
                        {
                            // Degenerate but real: a field row as the program's LAST line leaves no
                            // later ack to run the finished check - do it here (barrier-tail pattern).
                            if (inflight.Count == 0)
                            {
                                PumpLog.W("JOB FINISHED (prompt row was tail)");
                                aborted = true;
                                PostControl(onJobFinished);
                            }
                            break;
                        }
                        continue;       // a consumed input row must not stall dispatch
                    }

                    bool bLast = pgmEndLine == sendIdx;
                    MarkSent(sendIdx, "hold");
                    latestBlock = sendIdx;
                    ScheduleDrain();
                    mboxBarrier = true;
                    mboxPromptPending = true;
                    mboxLine = "(MBOX, OKCANCEL, Ready to continue?)";
                    PumpLog.W(string.Format("PROMPT checkpoint armed @idx={0} inflight={1} last={2}", sendIdx, inflight.Count, bLast));
                    if (DebugLog.Enabled)
                        DebugLog.Write("pump", string.Format("PROMPT checkpoint armed @idx={0}", sendIdx));
                    sendIdx = bLast ? -1 : sendIdx + 1;
                    MaybeShowMbox();
                    break;
                }

                string line = block.Data;
                int len = block.Length;

                // (PROMPT) field substitution at SEND time (Step 4b): the wire gets the operator's
                // values, the stored row keeps its #<_name> references. len must track the ACTUAL
                // bytes written - the substituted line's length differs from block.Length, and the
                // grbl character-count flow control lives or dies on that number.
                if (promptFields != null && line.IndexOf("#<", StringComparison.Ordinal) >= 0)
                {
                    line = MacroRunner.ApplySubstitutions(line, promptFields);
                    len = line.Length + 1;
                }

                // Comments are sent as an empty comment when "Send comments" is off - except to the simulator,
                // which parses (TOOL ...) comments. Use a local length; never mutate the shared block.
                if (block.IsComment && !sendComments && !startSimulator)
                {
                    line = "()";
                    len = line.Length + 1;
                }

                // Dry-run/verify mode: neutralise spindle-on (M3/M4) and coolant-on (M7/M8) so the operator
                // can watch the toolpath move without the spindle or coolant ever actually activating,
                // regardless of what the loaded program contains - the Z-offset alone is NOT a safety
                // feature, it only avoids hitting stock. Local rewrite only (same pattern as the comment
                // case above); HasSpindleOrCoolantOn is precomputed at load time from the real G-code
                // parser's tokens (GCodeJob.ParseFileLines/AddBlock), not a fragile regex re-check here.
                else if (model.IsDryRunMode && block.HasSpindleOrCoolantOn)
                {
                    line = "()";
                    len = line.Length + 1;
                }

                // Dry-run mode: also skip the program's own tool changes (M6) entirely, rather than let
                // them run - dry run never cuts, so which physical tool is in the spindle doesn't matter,
                // and running tc.macro (or any M6 handler) here risks it interacting badly with the Z-
                // offset G92 still active for the rest of the dry run (confirmed on real hardware: it
                // pushed a toolsetter approach move out of travel - Alarm:2 - and a stale hang-watchdog
                // timer then reset the controller). Skipping means the dry run just runs straight through
                // without pausing for a tool swap.
                else if (model.IsDryRunMode && block.HasToolChange)
                {
                    line = "()";
                    len = line.Length + 1;
                }

                if (serialUsed < serialSize - len && (!jobHasProbe || inflight.Count < ProbeLookahead))
                {
                    // program-end markers (mirror the legacy SendNextLine bookkeeping)
                    if (line == "%")
                    {
                        if (!(started = !started))
                            pgmEndLine = sendIdx;
                    }
                    else if (block.ProgramEnd)
                        pgmEndLine = sendIdx;

                    bool isLast = pgmEndLine == sendIdx;

                    MarkSent(sendIdx, "*");
                    serialUsed += len;
                    inflight.Enqueue(new Sent(sendIdx, len));
                    Comms.com.WriteString(line + '\r');
                    PumpLog.W(string.Format("SEND idx={0} len={1} used={2} inflight={3} last={4}  '{5}'", sendIdx, len, serialUsed, inflight.Count, isLast, line));

                    if (block.BreakAt)
                    {
                        const int m0Len = 3;        // "M0\r"
                        serialUsed += m0Len;
                        inflight.Enqueue(new Sent(-1, m0Len));
                        Comms.com.WriteString("M0" + '\r');
                    }

                    // Barrier on a streamed probe (G38) AND on an O-word CALL: an O<...> CALL runs a controller-
                    // side macro that itself moves/probes (e.g. Load Stock's pcorner.macro, whose G38s are in the
                    // macro - not in this streamed line - so the G38 test alone never fired for it). Piling the
                    // lines that follow into the controller's RX while that macro runs breaks grblHAL's O-word
                    // handling and stalls the run right after the CALL (the tail - final G30 park + M2 - never
                    // executes). Hold the stream until the CALL has fully completed (everything outstanding acked).
                    if (line.IndexOf("G38", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("O<", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        probePending = jobHasProbe = true;
                        PumpLog.W(string.Format("BARRIER set @idx={0}", sendIdx));
                    }

                    sendIdx = isLast ? -1 : sendIdx + 1;

                    if (!useBuffering || probePending)
                        break;
                }
                else
                {
                    PumpLog.W(string.Format("HOLD bufferfull sendIdx={0} used={1} need={2} inflight={3}", sendIdx, serialUsed, serialSize - len, inflight.Count));
                    break;                          // buffer full / probe look-ahead cap reached
                }
            }
        }

        private void OnAck(string ack)
        {
            if (inflight.Count == 0)                 // stray ack (e.g. a late jog/MDI reply) - ignore
                return;

            Sent s = inflight.Dequeue();
            serialUsed -= s.Length;
            if (serialUsed < 0)
                serialUsed = 0;

            PumpLog.W(string.Format("ACK  idx={0} used={1} inflight={2} sendIdx={3} barrier={4}  '{5}'", s.Index, serialUsed, inflight.Count, sendIdx, probePending, ack));

            // probe barrier clears once everything outstanding (including the G38, whose ok arrives only after
            // the probe finishes) has been acked.
            if (probePending && inflight.Count == 0)
            {
                probePending = false;
                PumpLog.W("BARRIER clear");
            }

            MaybeShowMbox();   // an armed (MBOX) prompts once the lines before it are all acked

            if (s.Index >= 0)                        // a real program line (not the synthetic M0)
            {
                PendingLine = s.Index;
                // An "ok" says the controller PARSED and BUFFERED this line, never that it cut it - with a
                // full planner buffer that is a hundred-odd lines ahead of the tool. On short segments the
                // whole file reads complete while the spindle is still in the first shape (reported
                // 2026-08-06 on a 10-square test: every line marked through to the closing M0 during square
                // one). Once the Ln: reports have proved themselves, they own the markers and the scroll.
                // Errors are never withheld - an error:N is not progress, it is why the job just stopped.
                if (!(ExecutionDrivenProgress && ack == "ok"))
                {
                    MarkSent(s.Index, ack);
                    if (s.Index > 5)
                        latestScroll = s.Index - 5;
                }
                latestBlock = s.Index;
                ScheduleDrain();
            }

            if (ack.StartsWith("error"))
            {
                if (!continueOnError)
                {
                    aborted = true;
                    PostControl(() => onError(ack));
                    return;
                }

                // Check mode: report every error via the state-machine callback but keep streaming to the
                // end, matching the legacy check-mode streamer's behavior this replaces. The per-line
                // Sent text (including this error) was already written by MarkSent above, same as any
                // other line.
                PostControl(onCheckError);
            }

            if (sendIdx < 0 && inflight.Count == 0)  // everything sent and acked
            {
                PumpLog.W("JOB FINISHED (all sent+acked)");
                aborted = true;
                PostControl(onJobFinished);
                return;
            }

            SendNext();
        }

        // Pump thread only. An armed (MBOX) shows its prompt once nothing is outstanding - the prompt
        // itself runs on the HOST's UI thread (controlMarshal; ShowHoldPrompt blocks there in a nested
        // DispatcherFrame, so the UI keeps pumping and the operator can jog). The answer comes back via
        // the ack-channel sentinels; the pump thread never blocks on the operator.
        private void MaybeShowMbox()
        {
            if (!mboxBarrier || !mboxPromptPending || inflight.Count != 0 || aborted)
                return;
            mboxPromptPending = false;
            string line = mboxLine;
            PumpLog.W("MBOX prompting");
            PostControl(() =>
            {
                bool ok = MacroRunner.ShowMBox(string.Empty, line);
                try { acks.Add(ok ? MboxOkTick : MboxCancelTick); } catch { }
            });
        }

        // The operator answered the (MBOX) prompt (pump thread, via the sentinel). OK: release and
        // resume, mirroring OnBarrierStatus's completion handling for a barrier-as-last-row program.
        // Cancel: the HOST's Stop path (JobRunner.Stop - feed hold, teardown, operator-stopped state),
        // because lines before the MBOX may still be physically executing; the pump does NOT tear
        // itself down - Stop's AbortPump does that properly. Null host (headless): onError fallback.
        private void OnMboxAnswer(bool ok)
        {
            if (!mboxBarrier || aborted)
                return;

            mboxBarrier = false;
            PumpLog.W(string.Format("MBOX {0}  sendIdx={1}", ok ? "OK" : "CANCELLED", sendIdx));
            if (DebugLog.Enabled)
                DebugLog.Write("pump", string.Format("MBOX {0}  sendIdx={1}", ok ? "OK" : "CANCELLED", sendIdx));

            if (!ok)
            {
                if (onOperatorCancel != null)
                    PostControl(onOperatorCancel);
                else
                    PostControl(() => onError("cancelled at operator prompt"));
                return;
            }

            if (sendIdx >= 0)
                SendNext();
            if (sendIdx < 0 && inflight.Count == 0)
            {
                PumpLog.W("JOB FINISHED (mbox was tail)");
                aborted = true;
                PostControl(onJobFinished);
            }
        }

        // A status report arrived while the (WAITIDLE) barrier is armed (read thread enqueued a sentinel;
        // this runs on the pump thread, which owns all the accounting). Release needs BOTH: everything
        // outstanding acked (inflight empty - lines before the WAITIDLE may still be awaiting their ok)
        // AND two consecutive idle reports - one report can catch the planner momentarily drained mid-job,
        // and can also land in the sub-report-interval window between the last ack and motion visibly
        // starting; two reports ~200ms apart span both (MacroRunner.WaitForIdle's own proven condition).
        private void OnBarrierStatus(bool idle)
        {
            if (!waitIdleBarrier || aborted)
                return;

            if (idle && inflight.Count == 0)
            {
                if (++idleStreak >= 2)
                {
                    waitIdleBarrier = false;
                    idleStreak = 0;
                    PumpLog.W(string.Format("WAITIDLE clear  sendIdx={0}", sendIdx));
                    if (DebugLog.Enabled)
                        DebugLog.Write("pump", string.Format("WAITIDLE clear  sendIdx={0}", sendIdx));
                    // Mirror OnIdleKick's completion logic: the barrier row may have been the last line,
                    // in which case no further ack will ever run OnAck's finished check - do it here.
                    if (sendIdx >= 0)
                        SendNext();
                    if (sendIdx < 0 && inflight.Count == 0)
                    {
                        PumpLog.W("JOB FINISHED (waitidle was tail)");
                        aborted = true;
                        PostControl(onJobFinished);
                    }
                }
            }
            else
                idleStreak = 0;   // moving (or acks still outstanding) - any partial streak is stale
        }

        // The controller is confirmed idle but we still think a job is in flight (see KickIdle). An idle
        // controller has drained its buffer, so any serialUsed/inflight left is from acks we never saw.
        private void OnIdleKick()
        {
            PumpLog.W(string.Format("KICK handled  sendIdx={0} inflight={1} used={2} barrier={3} aborted={4}", sendIdx, inflight.Count, serialUsed, probePending, aborted));
            if (aborted)
                return;

            if (sendIdx >= 0)
            {
                // Lines remain but the pump believed the buffer was full (or is holding the O-word/probe barrier):
                // the controller is idle, so its buffer is empty and the in-flight CALL/probe has finished. Drop
                // the stale accounting, release the barrier, and resume sending the remainder (final G30 + M2).
                serialUsed = 0;
                probePending = false;
                inflight.Clear();
                SendNext();
                // If that was the last of it (nothing left to send AND nothing newly queued), finish now.
                if (sendIdx < 0 && inflight.Count == 0)
                {
                    aborted = true;
                    PostControl(onJobFinished);
                }
            }
            else if (inflight.Count > 0)
            {
                // Everything was sent; only a tail ack is missing and the controller is idle - the job is done.
                aborted = true;
                PostControl(onJobFinished);
            }
        }

        // ---- display marshaling (coalesced, Background priority) ----

        private void MarkSent(int index, string mark)
        {
            sentMarks.Enqueue(new KeyValuePair<int, string>(index, mark));
            ScheduleDrain();
        }

        private void ScheduleDrain()
        {
            if (Interlocked.Exchange(ref drainPending, 1) == 0)
            {
                if (displayMarshal != null)
                    displayMarshal(Drain);
                else
                    Drain();            // headless: no UI thread to hop to
            }
        }

        private void Drain()
        {
            Interlocked.Exchange(ref drainPending, 0);

            KeyValuePair<int, string> mark;
            var data = source.Data;
            while (sentMarks.TryDequeue(out mark))
            {
                if (mark.Key >= 0 && mark.Key < data.Count)
                    data[mark.Key].Sent = mark.Value;
            }

            int block = latestBlock;
            if (block >= 0)
                model.BlockExecuting = block;

            int scroll = latestScroll;
            if (scroll >= 0)
                model.ScrollPosition = scroll;
        }
    }
}
