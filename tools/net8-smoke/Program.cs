using System;
using System.Threading;
using CNC.Core;
using CNC.GCode;

class Program
{
    static int fails = 0;

    static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + (detail == "" ? "" : "   " + detail));
        if (!ok) fails++;
    }

    static int Main(string[] args)
    {
        Console.WriteLine("CNC.Core on " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Console.WriteLine();

        // A headless host registers no UI hooks at all: no UiContext, no EventUtils.Pump, no
        // KeyboardFactory, no SecretStore provider. Everything must degrade to inline/no-op.
        Check("UiContext unregistered -> IsCurrent true (runs inline)", UiContext.IsCurrent);
        UiContext.Send(() => { });
        UiContext.Post(() => { });
        UiContext.Run(() => { });
        Check("UiContext Send/Post/Run are safe with no host", true);

        EventUtils.DoEvents();
        Check("EventUtils.DoEvents with no pump is a no-op", true);

        // Portable geometry + the WCS rotation that replaced RP.Math
        var p = new Point3D(10d, 0d, 5d).RotateZ(0d, 0d, Math.PI / 2d);
        Check("Point3D.RotateZ works", Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y - 10d) < 1e-9 && p.Z == 5d, p.ToString());

        // Config store + secrets, both pointed at a scratch dir
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iosender-net8-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp);
        Resources.ConfigPath = tmp + System.IO.Path.DirectorySeparatorChar;

        SecretStore.Set("smoke", "value");
        Check("SecretStore round-trips on the portable file store", SecretStore.Get("smoke") == "value");

        // Port enumeration with NO description provider - the WMI half is Windows-only and lives in the client
        var ports = new SerialPorts();
        Console.WriteLine("      ports seen: " + (ports.Ports.Count == 0 ? "(none)" : string.Join(", ", System.Linq.Enumerable.Select(ports.Ports, x => x.Name))));
        Check("port enumeration runs without WMI", true);

        // G-code parsing - the real machining logic
        var parser = new GCodeParser();
        string b1 = "G1 X10 Y20 F500", b2 = "G2 X0 Y0 I-10 J0";
        bool parsed = parser.ParseBlock(ref b1, false) && parser.ParseBlock(ref b2, false);
        Check("GCodeParser parses linear + arc blocks", parsed, "tokens=" + parser.Tokens.Count);

        if (args.Length > 0)
        {
            string port = args[0];
            Console.WriteLine();
            Console.WriteLine("--- opening " + port + " (READ-ONLY: '?' status and '$I' build info only)");

            // No SynchronizationContext: replies must arrive inline on the read thread.
            var stream = new SerialStream(port, 100, null);
            Check("serial port opened", stream.IsOpen);

            if (stream.IsOpen)
            {
                int replies = 0;
                string last = null;
                Comms.com.DataReceived += (s) => { replies++; last = s; };

                for (int i = 0; i < 10; i++)
                {
                    Comms.com.WriteByte((byte)'?');
                    Thread.Sleep(200);
                }

                Check("status reports received over serial on .NET 8", replies > 0, "replies=" + replies);
                Console.WriteLine("      last: " + (last ?? "(none)"));

                var info = Comms.com.GetReply("$I");
                Console.WriteLine("      $I -> " + (string.IsNullOrEmpty(info) ? "(empty)" : info));

                stream.Close();
                Check("port closed cleanly", !stream.IsOpen);
            }
        }
        else
            Console.WriteLine("\n(no port argument - skipped the live controller test)");

        try { System.IO.Directory.Delete(tmp, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
    }
}
