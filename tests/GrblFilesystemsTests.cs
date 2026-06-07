using System;
using System.Collections.Generic;
using CNC.Controls;

class FsTest
{
    static int fail = 0, pass = 0;

    static void Eq(object got, object want, string what)
    {
        bool ok = Equals(got, want);
        Console.WriteLine((ok ? "  ok  " : " FAIL ") + what + "  =>  got=[" + got + "] want=[" + want + "]");
        if (ok) pass++; else fail++;
    }

    static void IsNull(object got, string what)
    {
        bool ok = got == null;
        Console.WriteLine((ok ? "  ok  " : " FAIL ") + what + "  =>  " + (ok ? "null" : "NOT null"));
        if (ok) pass++; else fail++;
    }

    static void Main()
    {
        // --- ParseMountLine: well-formed littlefs ---
        var m = GrblFilesystems.ParseMountLine("[FS:littlefs@/littlefs size 1048576, free 480256]");
        Eq(m != null ? m.Name : "(null)", "littlefs", "littlefs name");
        Eq(m != null ? m.Path : "(null)", "/littlefs", "littlefs path");
        Eq(m != null ? m.Size : -9, 1048576L, "littlefs size");
        Eq(m != null ? m.Free : -9, 480256L, "littlefs free");
        Eq(m != null && m.IsRoot, false, "littlefs not root");

        // --- SD at root ---
        var sd = GrblFilesystems.ParseMountLine("[FS:SD@/ size 15931539456, free 15930000000]");
        Eq(sd != null ? sd.Name : "(null)", "SD", "SD name");
        Eq(sd != null ? sd.Path : "(null)", "/", "SD path");
        Eq(sd != null && sd.IsRoot, true, "SD is root");

        // --- trailing slash normalised, no size/free reported ---
        var t = GrblFilesystems.ParseMountLine("[FS:littlefs@/littlefs/]");
        Eq(t != null ? t.Path : "(null)", "/littlefs", "trailing slash trimmed");
        Eq(t != null ? t.Size : -9, -1L, "missing size -> -1");
        Eq(t != null ? t.Free : -9, -1L, "missing free -> -1");

        // --- 'free' without comma separator ---
        var nc = GrblFilesystems.ParseMountLine("[FS:lfs@/ size 100 free 50]");
        Eq(nc != null ? nc.Free : -9, 50L, "free without comma");

        // --- non-FS lines must not parse ---
        IsNull(GrblFilesystems.ParseMountLine("[FILE:foo.nc|SIZE:10]"), "[FILE:] is not a mount");
        IsNull(GrblFilesystems.ParseMountLine("ok"), "'ok' is not a mount");
        IsNull(GrblFilesystems.ParseMountLine("error:65"), "error is not a mount");
        IsNull(GrblFilesystems.ParseMountLine(""), "empty is not a mount");

        // --- HumanSize ---
        Eq(GrblFilesystems.HumanSize(0), "0 B", "HumanSize 0");
        Eq(GrblFilesystems.HumanSize(512), "512 B", "HumanSize 512");
        Eq(GrblFilesystems.HumanSize(1024), "1.0 KB", "HumanSize 1KB");
        Eq(GrblFilesystems.HumanSize(480256), "469.0 KB", "HumanSize 469KB");
        Eq(GrblFilesystems.HumanSize(1048576), "1.0 MB", "HumanSize 1MB");
        Eq(GrblFilesystems.HumanSize(-1), "?", "HumanSize unknown");

        // --- QualifiedName ---
        Eq(GrblFilesystems.QualifiedName("/", "a.nc"), "a.nc", "root keeps bare name");
        Eq(GrblFilesystems.QualifiedName("", "a.nc"), "a.nc", "empty path keeps bare name");
        Eq(GrblFilesystems.QualifiedName("/littlefs", "tc.macro"), "/littlefs/tc.macro", "submount absolute");
        Eq(GrblFilesystems.QualifiedName("/littlefs/", "tc.macro"), "/littlefs/tc.macro", "submount trailing slash");
        Eq(GrblFilesystems.QualifiedName("/littlefs", "/littlefs/tc.macro"), "/littlefs/tc.macro", "already-absolute kept");
        Eq(GrblFilesystems.QualifiedName("", "/foo.nc"), "/foo.nc", "absolute with empty mount");

        // --- FreeSpaceSummary ---
        var sum = GrblFilesystems.FreeSpaceSummary(new List<FsMount> {
            new FsMount { Name = "SD", Path = "/", Size = 15931539456, Free = 15930000000 },
            new FsMount { Name = "littlefs", Path = "/littlefs", Size = 1048576, Free = 480256 }
        });
        Eq(sum.Contains("SD:") && sum.Contains("littlefs:") && sum.Contains("469.0 KB free"), true, "summary has both mounts");
        Console.WriteLine("        summary = \"" + sum + "\"");

        Console.WriteLine();
        Console.WriteLine("RESULT: " + pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
