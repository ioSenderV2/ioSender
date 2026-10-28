/*
 * version-gate-probe - feeds real [VER:] lines through the real GrblInfo parser and asserts what
 * each date gate answers. See the csproj header for why. Exit code 0 = all checks passed.
 *
 * The three schemes under test, and what each MUST answer:
 *
 *   "1.1f"            classic grbl, no build date ever      -> every date gate FALSE (unchanged)
 *   "1.1f.20250901"   grblHAL's old date-stamped scheme     -> compares numerically (unchanged)
 *   "2.0.0"           the git-tag scheme, firmware 2.0.0    -> every date gate TRUE
 *
 * The expected values below are written out per scheme rather than computed from BuildAtLeast -
 * a test that re-derives the implementation proves only that it equals itself.
 */

using System;
using System.Reflection;
using CNC.Core;

static class VersionGateProbe
{
    static int failures = 0;

    // The two-arg overload with marshalled=true, NOT Process(string): the single-arg entry hops to
    // the captured SyncTarget first, and outside the running app there is no UI thread to capture.
    // marshalled=true is precisely "you are already on the right thread, do the work" - the same
    // state the app's own parse runs in - so nothing under test is skipped by going in here.
    static readonly MethodInfo process = typeof(GrblInfo).GetMethod(
        "Process", BindingFlags.NonPublic | BindingFlags.Static, null,
        new[] { typeof(string), typeof(bool) }, null);

    static readonly PropertyInfo isGrblHAL = typeof(GrblInfo).GetProperty(
        "IsGrblHAL", BindingFlags.Public | BindingFlags.Static);

    // Feed one $I line through the parser exactly as the comms layer would.
    static void Feed(string line)
    {
        process.Invoke(null, new object[] { line, true });
    }

    static void Check(string what, object got, object expected)
    {
        bool ok = Equals(got, expected);
        if (!ok)
            failures++;
        Console.WriteLine("  {0} {1,-34} got {2,-14} expected {3}",
                          ok ? "pass" : "FAIL", what, Describe(got), Describe(expected));
    }

    static string Describe(object v)
    {
        if (v == null) return "(null)";
        if (v is bool) return (bool)v ? "true" : "false";
        if (v is string) return "\"" + v + "\"";
        return v.ToString();
    }

    // The eight date gates this change re-expressed, by the date each one tests. Named so a failure
    // says which feature is affected, not just which number.
    static readonly Tuple<string, int>[] Gates =
    {
        Tuple.Create("getExtended ($ES/$EE)",        20200716),
        Tuple.Create("CanReferenceToolOffset (TLO)", 20200805),
        Tuple.Create("HasSimpleProbeProtect",        20200924),
        Tuple.Create("SD CanDelete",                 20210421),
        Tuple.Create("Grbl Config hal settings",     20210819),
        Tuple.Create("Grbl Config alarms/errors",    20210823),
        Tuple.Create("SD CanViewAll",                20230312),
        Tuple.Create("spindle enumeration ($SPINDLES)", 20240307),
        Tuple.Create("spindle id/num NOT swapped",   20240812),
        Tuple.Create("FTP PWD re-query",             20260309),
    };

    static void Scheme(string verLine, string expectVersion, int expectBuild,
                       string expectSemver, bool[] expectGates)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + verLine);

        Feed(verLine);

        Check("GrblInfo.Version", GrblInfo.Version, expectVersion);
        Check("GrblInfo.Build", GrblInfo.Build, expectBuild);
        Check("GrblInfo.FirmwareVersion",
              GrblInfo.FirmwareVersion == null ? null : GrblInfo.FirmwareVersion.ToString(),
              expectSemver);
        Check("GrblInfo.HasSemanticVersion", GrblInfo.HasSemanticVersion, expectSemver != null);

        for (int i = 0; i < Gates.Length; i++)
            Check("gate: " + Gates[i].Item1, GrblInfo.BuildAtLeast(Gates[i].Item2), expectGates[i]);
    }

    static int Main()
    {
        if (process == null)
        {
            Console.WriteLine("FAIL: could not reflect GrblInfo.Process(string, bool) - signature changed?");
            return 1;
        }

        // The spindle workaround and several gates are also guarded by IsGrblHAL; set it so the
        // probe is testing the version arithmetic and not that guard.
        isGrblHAL.SetValue(null, true, null);

        bool[] allFalse = new bool[Gates.Length];
        bool[] allTrue = new bool[Gates.Length];
        for (int i = 0; i < Gates.Length; i++) allTrue[i] = true;

        // Classic grbl: no build date was ever reported, so every gate was already false. This is
        // the REGRESSION check - "2.0.0 works" must not come at the cost of reading an ancient
        // controller as new.
        Scheme("[VER:1.1f:]", "1.1f", 0, null, allFalse);

        // grblHAL's old scheme, a date mid-way through the gate list: the gates on or before
        // 2021-04-21 are true, the later ones false. Hand-written, not computed.
        Scheme("[VER:1.1f.20210421:Teensy 4.1]", "1.1f.20210421", 20210421, null,
               new[] { true, true, true, true, false, false, false, false, false, false });

        // The new git-tag scheme. Every date gate must answer true - and the two in the list that
        // are consumed NEGATED (spindle id/num, which enables a bug workaround when false) are the
        // reason this probe exists.
        Scheme("[VER:2.0.0:Teensy 4.1]", "2.0.0", 0, "2.0.0", allTrue);

        // A four-part tag, and one with a date-shaped patch - both are the new scheme, both must
        // still read as semver rather than falling back to a build date.
        Scheme("[VER:2.1.0.3:]", "2.1.0.3", 0, "2.1.0.3", allTrue);
        Scheme("[VER:2.1.20260101:]", "2.1.20260101", 0, "2.1.20260101", allTrue);

        // Back to a date-stamped controller in the SAME session: the semver scheme must not be
        // left standing. This is the stale-across-controllers trap that [DRIVER:] already had, and
        // here it would silently re-arm the spindle workaround on firmware that needs it.
        Scheme("[VER:1.1f.20210421:Teensy 4.1]", "1.1f.20210421", 20210421, null,
               new[] { true, true, true, true, false, false, false, false, false, false });

        // [NVSVER:] - the settings-storage layout version, a SEPARATE thing from the firmware
        // version above and deliberately so. It arrives on the $I switch's default branch, and its
        // value is colon-free by design because the sender splits $I lines on every colon.
        Console.WriteLine();
        Console.WriteLine("=== [NVSVER:30.0-a1b2c3d4]");
        Feed("[NVSVER:30.0-a1b2c3d4]");
        Check("GrblInfo.NvsVersion", GrblInfo.NvsVersion, "30.0-a1b2c3d4");
        Check("firmware version untouched", GrblInfo.Version, "1.1f.20210421");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
