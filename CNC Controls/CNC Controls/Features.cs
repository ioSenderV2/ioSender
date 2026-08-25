/*
 * Features.cs - part of CNC Controls library
 *
 * Compile-time switches for work that is finished and compiling, but deliberately not offered yet.
 *
 * Deliberately NOT AppConfig settings: these are not the operator's choice, they are ours, and a
 * setting implies a supported thing that can be turned on. They are also not #if - code behind a
 * preprocessor symbol stops being compiled, so it rots silently while everything around it moves on.
 * A const bool keeps every path type-checked and refactored along with the rest of the codebase, at
 * the cost of a dead-code warning the compiler is welcome to make.
 *
 * Lives in CNC.Controls rather than in ioSender XL because the things that must agree about a hidden
 * feature span both: the menu item (ioSender XL), and the keyboard-shortcut catalogue that lists the
 * same command as bindable (CNC.Controls). A flag only one of them can see is how a feature ends up
 * hidden in one place and offered in the other.
 */

namespace CNC.Controls
{
    public static class Features
    {
        /// <summary>
        /// "File > Load SVG Laser Job..." - the SVG-to-laser converter, its dialog, persisted settings
        /// and artwork placement. All present and building; held back until it has had time on a real
        /// machine, because a laser job that reaches the table with the wrong exposure is not a defect
        /// to let someone else find for us.
        ///
        /// Setting this true must restore it everywhere at once - the File menu entry, its keyboard
        /// action registration, and its row in Settings > Keyboard. Grep the symbol before assuming a
        /// new call site is covered.
        /// </summary>
        public const bool SvgLaserJob = false;
    }
}
