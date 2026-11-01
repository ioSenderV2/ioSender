/*
 * IToolpathView.cs - part of CNC GCodeViewer
 *
 * What the Job tab's centre 3D component has to be able to do, so its host can drive it without
 * knowing which one it got.
 *
 * Why this exists: JobWorkspace held the 3D view as a RenderControl and obtained it with
 * "ctl as RenderControl". When CarveView replaced RenderControl as the registered component that cast
 * started returning NULL - CarveView and RenderControl are siblings, both UserControl, neither derived
 * from the other - so ShowToolpath() and ClearToolpath() silently became no-ops. Nothing failed
 * loudly: CarveView subscribes to FileName itself, so loading a program still drew, and only the
 * CLEAR went missing. That surfaced as a finished job's carved lettering still sitting on the stock
 * after the program had evaporated (reported 2026-08-10).
 *
 * A cast that returns null when the type is wrong is exactly the shape of failure that hides: the
 * call sites keep compiling and keep running, doing nothing. An interface makes the requirement
 * explicit, so a future third implementation either satisfies it or fails to compile.
 */

using System.Collections.Generic;
using CNC.GCode;

namespace CNC.Controls.Viewer
{
    public interface IToolpathView
    {
        /// <summary>Show this program's toolpath.</summary>
        void Open(List<GCodeToken> tokens);

        /// <summary>
        /// No program is loaded any more: drop the toolpath AND anything derived from it, so the view
        /// stops depicting material removed by a program that is gone.
        /// </summary>
        void Close();
    }
}
