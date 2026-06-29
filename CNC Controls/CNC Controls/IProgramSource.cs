/*
 * IProgramSource.cs - part of CNC Controls library
 *
 * The read surface the job streamer (JobControl) needs to stream a program. The loaded job (GCode /
 * GCode.File) implements it; a tool can supply its OWN in-memory program so a generated run streams
 * through the flow-controlled streamer WITHOUT disturbing the loaded job. Phase 3 of the registration
 * architecture refactor (see docs/Architecture-Registration-Refactor.md): the streamer takes a program
 * SOURCE as input instead of being hardwired to the single GCode.File singleton.
 */

using System.Collections.Generic;
using System.Collections.ObjectModel;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public interface IProgramSource
    {
        bool IsLoaded { get; }
        int Blocks { get; }
        int ToolChanges { get; }
        bool HasGoPredefinedPosition { get; }
        ObservableCollection<GCodeBlock> Data { get; }
        Queue<string> Commands { get; }
        GCodeParser Parser { get; }
        GrblViewModel Model { get; }
        void ClearStatus();
    }
}
