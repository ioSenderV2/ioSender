/*
 * GrblFilesystems.cs - part of CNC Controls library
 *
 * Discovery and helpers for the controller's mounted filesystems (grblHAL $FI),
 * so the SD Card view can present SD + LittleFS in one combined browser.
 *
 * v0.01 / 2026-06-07 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CNC.Controls
{
    // One mounted filesystem as reported by grblHAL's $FI command.
    public class FsMount
    {
        public string Name { get; set; }    // volume label, e.g. "SD", "littlefs"
        public string Path { get; set; }     // mount path, e.g. "/" or "/littlefs"
        public long Size { get; set; }        // total bytes, -1 if not reported
        public long Free { get; set; }        // free bytes,  -1 if not reported

        // The root-mounted volume keeps bare relative names; sub-mounts need an absolute path.
        public bool IsRoot { get { return string.IsNullOrEmpty(Path) || Path == "/"; } }
    }

    // Pure (no I/O, no WPF) parsing/formatting helpers for the filesystem browser, so they
    // can be unit tested without a controller or the UI stack. The live $FI/$F querying that
    // uses these lives in GrblSDCard (SDCardView.xaml.cs), which has the Comms dependency.
    public static class GrblFilesystems
    {
        // [FS:<name>@<path> size <bytes>, free <bytes>]   (size / free are optional and order-tolerant)
        private static readonly Regex FsLine = new Regex(
            @"^\s*\[FS:(?<name>[^@\]]+)@(?<path>[^\s\]]+)(?:\s+size\s+(?<size>\d+))?(?:[\s,]+free\s+(?<free>\d+))?",
            RegexOptions.Compiled);

        // Parse one $FI response line into an FsMount, or null if the line is not an [FS:...] entry.
        public static FsMount ParseMountLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return null;

            Match m = FsLine.Match(line);
            if (!m.Success)
                return null;

            long size = -1, free = -1;
            if (m.Groups["size"].Success)
                long.TryParse(m.Groups["size"].Value, out size);
            if (m.Groups["free"].Success)
                long.TryParse(m.Groups["free"].Value, out free);

            string path = m.Groups["path"].Value.Trim();
            if (path.Length > 1)
                path = path.TrimEnd('/');           // normalise "/littlefs/" -> "/littlefs", keep "/"

            return new FsMount {
                Name = m.Groups["name"].Value.Trim(),
                Path = path,
                Size = size,
                Free = free
            };
        }

        // Human-readable byte size, e.g. "480 KB", "14.8 GB"; "?" when unknown (-1).
        public static string HumanSize(long bytes)
        {
            if (bytes < 0)
                return "?";

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024.0 && i < units.Length - 1)
            {
                v /= 1024.0;
                i++;
            }
            return (i == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[i];
        }

        // Path to use for $F= / $FD= / $F<= against a file on a given mount. The root volume keeps the
        // bare file name (so single-SD controllers behave exactly as before); sub-mounts get an absolute
        // path so the controller targets the right filesystem regardless of the current working directory.
        public static string QualifiedName(string mountPath, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.StartsWith("/"))
                return fileName;                    // already absolute (some builds return full paths)
            if (string.IsNullOrEmpty(mountPath) || mountPath == "/")
                return fileName;
            return mountPath.TrimEnd('/') + "/" + fileName;
        }

        // One-line free-space banner across all mounts, e.g. "SD: 14.8 GB free  ·  littlefs: 468 KB free".
        // Only filesystems that actually report free space are listed - a bare "<name>: mounted" carries no
        // information (the file list already shows the filesystem is there), so those are omitted and the whole
        // banner simply disappears when the controller reports no sizes.
        public static string FreeSpaceSummary(IEnumerable<FsMount> mounts)
        {
            var parts = new List<string>();
            foreach (FsMount fs in mounts)
            {
                if (fs.Free >= 0)
                    parts.Add(fs.Name + ": " + HumanSize(fs.Free) + " free" + (fs.Size >= 0 ? " of " + HumanSize(fs.Size) : ""));
            }
            return string.Join("    ·    ", parts);
        }
    }
}
