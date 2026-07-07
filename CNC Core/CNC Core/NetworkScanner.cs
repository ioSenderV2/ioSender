/*
 * NetworkScanner.cs - part of CNC Core library
 *
 * Discovers grblHAL controllers on the local network. mDNS (grblHAL.local) is tried first as a
 * courtesy, but it is unreliable in practice: consumer WiFi routers routinely drop mDNS multicast
 * (UDP 5353) between the wireless and wired segments, so a laptop on WiFi never sees a wired
 * controller even with mDNS enabled in the firmware. The reliable path is therefore an active
 * subnet scan: async-probe every host on the local /24, and confirm each candidate is really a
 * grbl-family controller by PROTOCOL, not by an open port - send the real-time status request '?'
 * and require a valid <State|...> status report back (side-effect free, works in any machine state).
 * A confirmed hit is then upgraded with $I to capture the [VER:]/[OPT:] build info.
 *
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CNC.Core
{
    public class DiscoveredController
    {
        public string Host { get; set; }        // IP address (or resolved mDNS host)
        public int Port { get; set; }
        public string Version { get; set; }      // contents of [VER:...] from $I, if read
        public string Options { get; set; }      // contents of [OPT:...] from $I, if read
        public bool IsGrblHAL { get; set; }      // true when the banner/version identifies grblHAL (vs classic grbl)
        public bool Confirmed { get; set; }      // true once a '?'/$I handshake proved it's a grbl controller

        // What the Connect dialog stores as the target.
        public string Target { get { return string.Format("{0}:{1}", Host, Port); } }

        // Dropdown entry: a plain host for an unconfirmed/seed entry, or "host:port - grblHAL 1.1f" once probed.
        public string Display
        {
            get
            {
                if (!Confirmed)
                    return Host;

                string v = Version;
                if (!string.IsNullOrEmpty(v))
                {
                    // [VER:1.1f.20250901:] -> "1.1f.20250901"
                    v = v.StartsWith("VER:") ? v.Substring(4) : v;
                    v = v.TrimEnd(':');
                }
                string kind = IsGrblHAL ? "grblHAL" : "grbl";
                return string.IsNullOrEmpty(v) ? string.Format("{0}:{1}  -  {2}", Host, Port, kind)
                                               : string.Format("{0}:{1}  -  {2} {3}", Host, Port, kind, v);
            }
        }

        public override string ToString() { return Host; }   // editable ComboBox edit-box text = host only
    }

    public static class NetworkScanner
    {
        // A grbl real-time status report: "<Idle|MPos:0.000,0.000,0.000|...>". The leading state word is
        // the discriminator - no random telnet device answers '?' with this shape.
        private static readonly Regex StatusReport =
            new Regex(@"^<(Idle|Run|Hold|Jog|Alarm|Door|Check|Home|Sleep|Tool)\b", RegexOptions.Compiled);

        private const int MaxConcurrency = 64;
        private const int ConnectTimeoutMs = 400;
        private const int GreetingGraceMs = 120;   // let a fresh controller emit its "GrblHAL ..." banner first
        private const int ReplyTimeoutMs = 800;

        /// <summary>
        /// Scan the local network for grbl-family controllers on <paramref name="port"/> (telnet, default 23).
        /// Progress reports a "scanned/total" style string. Cancellable.
        /// </summary>
        public static async Task<List<DiscoveredController>> DiscoverAsync(int port, IProgress<string> progress, CancellationToken ct)
        {
            if (port <= 0)
                port = 23;

            var found = new ConcurrentDictionary<string, DiscoveredController>();

            // Best-effort mDNS: if the OS can resolve grblHAL.local, probe it up front. Often fails on WiFi
            // (see file header) - that's fine, the subnet scan below is the real workhorse.
            foreach (var host in await ResolveMdnsAsync())
            {
                var c = await ProbeAsync(host, port, ct);
                if (c != null)
                    found[c.Target] = c;
            }

            var targets = EnumerateSubnetHosts().ToList();
            int total = targets.Count, done = 0;
            progress?.Report(string.Format("Scanning {0} hosts...", total));

            using (var sem = new SemaphoreSlim(MaxConcurrency))
            {
                var tasks = targets.Select(async ip =>
                {
                    // Guard everything: a per-host task must NEVER fault. The app treats an unobserved task
                    // exception as fatal (App.TaskSchedulerOnUnobservedTaskException -> Environment.Exit), so a
                    // faulted task caught only by Task.WhenAll would leave its siblings' faults unobserved.
                    try { await sem.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    try
                    {
                        var c = await ProbeAsync(ip.ToString(), port, ct).ConfigureAwait(false);
                        if (c != null)
                            found[c.Target] = c;
                    }
                    catch { /* swallow - probe already guards, but never let a task fault the aggregate */ }
                    finally
                    {
                        sem.Release();
                        int n = Interlocked.Increment(ref done);
                        try { progress?.Report(string.Format("Scanned {0}/{1}  -  {2} found", n, total, found.Count)); }
                        catch { }
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();   // surface a cancel to the caller (tasks above swallow it)

            return found.Values.OrderBy(c => VersionSortKey(c.Host)).ToList();
        }

        // Sort IPs numerically by last octet so the list reads naturally.
        private static long VersionSortKey(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
            }
            return long.MaxValue;
        }

        private static async Task<List<string>> ResolveMdnsAsync()
        {
            var hosts = new List<string>();
            try
            {
                var addrs = await Dns.GetHostAddressesAsync("grblHAL.local").ConfigureAwait(false);
                foreach (var a in addrs)
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                        hosts.Add(a.ToString());
            }
            catch { /* name doesn't resolve (usual on WiFi) - ignore */ }
            return hosts;
        }

        // Connect, speak grbl, confirm. Returns null for anything that isn't a grbl-family controller.
        private static async Task<DiscoveredController> ProbeAsync(string host, int port, CancellationToken ct)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    var completed = await Task.WhenAny(connect, Task.Delay(ConnectTimeoutMs)).ConfigureAwait(false);
                    if (completed != connect || !client.Connected)
                    {
                        client.Close();               // fault the pending connect promptly...
                        await Swallow(connect).ConfigureAwait(false);   // ...then observe it (no unobserved fault)
                        return null;
                    }
                    await connect.ConfigureAwait(false);   // observe any connect exception

                    client.NoDelay = true;
                    var stream = client.GetStream();

                    // Send the status request '?' and build-info '$I' back-to-back, then read EVERYTHING in a
                    // single pass. This must be one read pass, not one-per-command: NetworkStream.ReadAsync does
                    // not honour the cancellation token, so a timed-out read stays pending on the socket and
                    // silently consumes the next command's reply (which made every controller look non-grbl).
                    // A short grace first lets a freshly-connected controller emit its "GrblHAL ..." banner.
                    await Task.Delay(GreetingGraceMs).ConfigureAwait(false);
                    await WriteAsync(stream, "?").ConfigureAwait(false);
                    await WriteAsync(stream, "$I\r").ConfigureAwait(false);

                    string reply = await ReadReplyAsync(stream, ReplyTimeoutMs, ct).ConfigureAwait(false);

                    bool isGrbl = reply.Split('\n').Any(l => StatusReport.IsMatch(l.Trim()));
                    if (!isGrbl)
                        return null;

                    var ctrl = new DiscoveredController { Host = host, Port = port, Confirmed = true };
                    ctrl.IsGrblHAL = reply.IndexOf("GrblHAL", StringComparison.OrdinalIgnoreCase) >= 0;
                    ctrl.Version = ExtractBracket(reply, "VER:");   // best-effort - null if $I didn't answer
                    ctrl.Options = ExtractBracket(reply, "OPT:");
                    return ctrl;
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteAsync(NetworkStream stream, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(b, 0, b.Length).ConfigureAwait(false);
        }

        // Read the greeting + '?' status report + '$I' info in one pass, accumulating until we've seen both a
        // status report ('>') and the $I terminator ("ok"), or the timeout elapses. Single read loop: reads are
        // issued strictly one after another (never two in flight on the same stream), so no reply is lost.
        private static async Task<string> ReadReplyAsync(NetworkStream stream, int durationMs, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[1024];
            var deadline = DateTime.UtcNow.AddMilliseconds(durationMs);
            Task<int> pending = null;   // a read left in flight when we break; observed below

            while (!ct.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                // No ct on ReadAsync/Delay: NetworkStream ignores the token anyway, and an abandoned cancellable
                // Delay would itself fault unobserved. We bound by the deadline and observe the orphaned read.
                var read = stream.ReadAsync(buf, 0, buf.Length);
                pending = read;
                var completed = await Task.WhenAny(read, Task.Delay(remaining)).ConfigureAwait(false);
                if (completed != read)
                    break;   // timed out; 'read' stays pending, observed after the loop

                pending = null;
                int n;
                try { n = await read.ConfigureAwait(false); }
                catch { break; }
                if (n <= 0)
                    break;

                sb.Append(Encoding.ASCII.GetString(buf, 0, n));

                // Early-out: a status report plus the $I "ok" terminator means we have all we need - a live
                // controller is then identified in well under the timeout.
                string s = sb.ToString();
                if (s.IndexOf('>') >= 0 && (s.Contains("\nok") || s.Contains("ok\r")))
                    break;
            }

            // The pending read will fault (or complete) once the caller disposes the socket; attach a
            // fault-observing continuation so it can never surface as a fatal UnobservedTaskException.
            Forget(pending);

            return sb.ToString();
        }

        // Await a task purely to observe (swallow) its exception.
        private static async Task Swallow(Task t)
        {
            try { await t.ConfigureAwait(false); } catch { }
        }

        // Fire-and-forget observe: read .Exception when the task faults so it isn't reported as unobserved.
        private static void Forget(Task t)
        {
            if (t == null)
                return;
            t.ContinueWith(x => { var ignore = x.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // Pull the contents of a "[TAG...]" block, e.g. ExtractBracket(text, "VER:") -> "VER:1.1f.20250901:".
        private static string ExtractBracket(string text, string tag)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            int i = text.IndexOf("[" + tag, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                return null;
            int end = text.IndexOf(']', i);
            if (end < 0)
                return null;
            return text.Substring(i + 1, end - i - 1);
        }

        // Every scannable IPv4 host on the machine's local subnet(s). Capped at a /24 per interface so a
        // wide netmask (e.g. /16) can never explode into a 65k-host sweep; loopback, APIPA and /32 (Tailscale
        // and the like) are skipped, and our own address is excluded.
        private static IEnumerable<IPAddress> EnumerateSubnetHosts()
        {
            var hosts = new List<IPAddress>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var self = ua.Address;
                    if (self.ToString().StartsWith("169.254"))   // APIPA - not a real LAN
                        continue;

                    int prefix;
                    try { prefix = ua.PrefixLength; } catch { prefix = 24; }
                    if (prefix >= 31)   // /31 or /32 (point-to-point / VPN like Tailscale) - nothing to scan
                        continue;

                    int effPrefix = Math.Max(prefix, 24);   // never scan wider than a /24
                    uint selfU = ToUInt(self);
                    uint mask = effPrefix == 0 ? 0 : 0xFFFFFFFFu << (32 - effPrefix);
                    uint network = selfU & mask;
                    uint hostCount = (uint)((1L << (32 - effPrefix)) - 2);

                    for (uint h = 1; h <= hostCount; h++)
                    {
                        uint addr = network + h;
                        if (addr == selfU)
                            continue;
                        hosts.Add(FromUInt(addr));
                    }
                }
            }

            // De-dup (overlapping interfaces) preserving order.
            return hosts.GroupBy(a => a.ToString()).Select(g => g.First());
        }

        private static uint ToUInt(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint a)
        {
            return new IPAddress(new byte[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a });
        }
    }
}
