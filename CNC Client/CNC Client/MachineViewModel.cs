/*
 * MachineViewModel.cs - part of CNC Client
 *
 * The client's view model of the machine: the name-compatible twin of (a growing subset of)
 * CNC.Core.GrblViewModel, fed EXCLUSIVELY by the wire (MachineMirror over the delta stream) plus
 * client-local coordination state. WPF bindings resolve by property NAME, so a view whose
 * DataContext moves from GrblViewModel to this class keeps its XAML untouched - that is the whole
 * migration strategy for the contracts-only client discipline.
 *
 * Grown per consumer, deliberately: a property is added here when a migrating view needs it, never
 * speculatively. Current consumers: CNC Controls Camera (the first project to drop CNC Core).
 *
 * Two kinds of members, keep them distinct:
 *  - Machine truth: read-only, derived from the mirror. Never settable from the client; mutation
 *    goes through the command channels.
 *  - Client coordination: state that lives BETWEEN client views (IsProbing, the camera-probe hub) -
 *    it used to ride on GrblViewModel as an event hub, but both ends are client-side, so in the
 *    split world it lives here and never touches the wire.
 */

using CNC.Core;     // ViewModelBase (CNC.Common assembly - shared infra, NOT the Core assembly)

namespace CNC.Client
{
    public class MachineViewModel : ViewModelBase
    {
        private readonly MachineMirror mirror;
        private bool _isProbing = false;

        public MachineViewModel(MachineMirror mirror)
        {
            this.mirror = mirror;
            // Forward the mirror's wire-shaped notifications under the twin's property names. The
            // mirror notifies on the delta thread; ViewModelBase.OnPropertyChanged hops to the UI
            // thread when one is registered, so bound views are safe without per-view marshalling.
            mirror.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(MachineMirror.MachinePosition):
                        OnPropertyChanged(nameof(MachinePosition));
                        break;
                    case nameof(MachineMirror.Position):
                        OnPropertyChanged(nameof(Position));
                        break;
                    case nameof(MachineMirror.IsMetric):
                        OnPropertyChanged(nameof(IsMetric));
                        OnPropertyChanged(nameof(UnitFactor));
                        break;
                }
            };
        }

        public MachineMirror Mirror { get { return mirror; } }

        // ---- Machine truth (wire-fed, read-only) ----

        /// <summary>Machine coordinates (MPos), reported units. Unset axes are NaN.</summary>
        public Position MachinePosition { get { return Position.FromWire(mirror.MachinePosition); } }

        /// <summary>Work coordinates - the derived MPos-WCO field, NOT the wire's WorkPosition,
        /// which is only populated when the controller reports WPos ($10). Same trap the mirror
        /// window hit (fc1333e8): read THIS for "where am I in work coords".</summary>
        public Position Position { get { return Position.FromWire(mirror.Position); } }

        /// <summary>Units mode from $13: true = metric.</summary>
        public bool IsMetric { get { return mirror.IsMetric; } }

        /// <summary>Multiplier that normalizes reported coordinates to millimeters (25.4 when the
        /// controller reports inches) - same semantics as MeasureViewModel.UnitFactor.</summary>
        public double UnitFactor { get { return mirror.IsMetric ? 1.0d : 25.4d; } }

        // ---- Client coordination (never on the wire) ----

        /// <summary>True while the probing view is active - set by ProbingView, consumed by views
        /// that publish probe positions (camera). Client-to-client state, not machine truth.</summary>
        public bool IsProbing { get { return _isProbing; } set { _isProbing = value; OnPropertyChanged(); } }

        /// <summary>Camera-probe hub: the camera view publishes a position, the probing view
        /// consumes it. Moved here from GrblViewModel - both ends are client-side.</summary>
        public System.Action<Position> OnCameraProbe;

        public void CameraProbed(Position position)
        {
            OnCameraProbe?.Invoke(position);
        }
    }
}
