/*
 * GamepadInput.cs - part of CNC Controls library
 *
 * Owns the Xbox controller input stack (ControllerService + ControllerMapper) for the application.
 *
 * This lives client-side, with the keyboard handler, because a gamepad is a human input device sitting on
 * the operator's desk - not machine logic. Its output (a JogCommand, a realtime byte) is what crosses to
 * the controller; the polling and button mapping do not. XInput is also a Windows P/Invoke
 * (xinput1_4.dll), which compiles anywhere but throws DllNotFoundException off Windows, so keeping it in
 * CNC.Core would have left a hidden platform dependency there.
 *
 * Ownership moved here from GrblViewModel's constructor, which is also a fix: EVERY GrblViewModel built
 * one and called Start(), and ioSender XL constructs three (the main model plus the throwaway ones in
 * OffsetView and ToolView) - so three 60Hz XInput pollers ran, two of them feeding mappers that were
 * inert anyway (ControllerMapper gates on GrblState != Unknown). There is now exactly one, attached to
 * the main model.
 */

using CNC.Core;

namespace CNC.Controls
{
    public static class GamepadInput
    {
        /// <summary>Polling/button-edge service. Null until Attach has run (or if XInput is unavailable).</summary>
        public static ControllerService Service { get; private set; }

        /// <summary>Button -> action mapping driving jog and realtime commands.</summary>
        public static ControllerMapper Mapper { get; private set; }

        /// <summary>
        /// Bind gamepad input to the application's main model. Called once, from MainWindow, at the point
        /// the main GrblViewModel is registered. Safe to call again - subsequent calls are ignored.
        /// </summary>
        public static void Attach(GrblViewModel model)
        {
            if (Service != null || model == null)
                return;

            try
            {
                Service = new ControllerService();
                Mapper = new ControllerMapper(model, Service);
                Service.Start();
            }
            catch
            {
                // XInput unavailable (no runtime, not Windows) - controller support simply stays inert,
                // which is what the original construction site did with its own catch.
                Service = null;
                Mapper = null;
            }
        }
    }
}
