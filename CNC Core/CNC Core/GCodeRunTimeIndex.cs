/*
 * GCodeRunTimeIndex.cs - part of CNC Core library
 *
 * Per-section run-time estimates for the loaded program, published where a group header can find them.
 *
 * The program outline's group headers are built by WPF from a CollectionViewGroup, whose only content is
 * the section name and an item count - there is no per-section object to hang an estimate on, and the
 * estimate does not exist yet when those headers are first built (it is computed off the UI thread after
 * the load, see GCodeProgram.EstimateRunTime). So the headers look their name up HERE, and Version bumping
 * is what tells their bindings to look again once the answer lands. Only the handful of visible headers
 * re-evaluate; nothing is regrouped or re-sorted.
 */

using System.Collections.Generic;

namespace CNC.Core
{
    public class GCodeRunTimeIndex : ViewModelBase
    {
        public static GCodeRunTimeIndex Instance { get; } = new GCodeRunTimeIndex();

        private Dictionary<string, string> bySection = new Dictionary<string, string>();
        private int _version;

        /// <summary>Bumped whenever the estimates change - a binding watches this to re-read Lookup.</summary>
        public int Version
        {
            get { return _version; }
            private set { _version = value; OnPropertyChanged(); }
        }

        /// <summary>Formatted estimate for a section name, or empty when there is none (yet).</summary>
        public string Lookup(string section)
        {
            if (string.IsNullOrEmpty(section))
                return string.Empty;
            var map = bySection;   // published as a whole; never mutated in place - see Publish
            string value;
            return map.TryGetValue(section, out value) ? value : string.Empty;
        }

        /// <summary>
        /// Replace the estimates. A NEW dictionary is swapped in rather than the existing one being
        /// cleared and refilled, so a UI-thread Lookup racing this background publish reads one complete
        /// set or the other, never a half-built one.
        /// </summary>
        public void Publish(Dictionary<string, string> estimates)
        {
            bySection = estimates ?? new Dictionary<string, string>();
            Version = Version + 1;
        }

        public void Clear()
        {
            Publish(null);
        }
    }
}
