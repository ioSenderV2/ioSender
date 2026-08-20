/*
 * HeightMapConfig.cs - part of CNC Controls library
 *
 * What the operator chose on the Height Map tab, kept between sessions.
 *
 * These were plain fields on the view, which meant they survived exactly as long as the view instance did -
 * and the Area choice did not even manage that, because the radio button's IsChecked comes from the XAML
 * every time the view is built. So "Full work surface" had to be re-selected on every visit, while the
 * numbers beside it appeared to be remembered, which is a worse state than forgetting everything: it
 * teaches you to trust the page and then quietly reverts the one setting that changes what the run does.
 *
 * Kept here rather than next to the view for the reason WorkSurface is: AppConfig registers the config
 * sections and lives in CNC Controls, which cannot reference ioSender XL.
 */

using System.Xml.Serialization;
using CNC.Core;

namespace CNC.Controls
{
    public class HeightMapConfig
    {
        /// <summary>
        /// True when the grid covers the whole work surface (and sets the origin from it) rather than the
        /// loaded program's extent. The one setting here that changes what the run actually does.
        /// </summary>
        public bool FullWorkSurface { get; set; } = false;

        /// <summary>Points per axis across the full work surface. 4 x 4 is 16 probes.</summary>
        public int DivisionsX { get; set; } = 4;
        public int DivisionsY { get; set; } = 4;

        /// <summary>
        /// How much lower than the previous point the next may be and still be found, mm. See the view's
        /// own notes on why this must never be zero.
        /// </summary>
        public double DropAllowance { get; set; } = 5d;

        /// <summary>Hold at each point so a touch plate can be moved.</summary>
        public bool HoldAtEachPoint { get; set; } = true;

        /// <summary>The live instance from the config store; never null so callers need no guard.</summary>
        [XmlIgnore]
        public static HeightMapConfig Current
        {
            get { return ConfigStore.Get<HeightMapConfig>() ?? fallback; }
        }

        private static readonly HeightMapConfig fallback = new HeightMapConfig();
    }
}
