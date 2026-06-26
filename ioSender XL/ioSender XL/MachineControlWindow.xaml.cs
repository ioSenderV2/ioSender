/*
 * MachineControlWindow.xaml.cs - part of ioSender XL
 *
 * A non-modal floating host for MachineControlPanel, shown by the .nc generate tools (Load Stock, ...) when
 * a program is run, so the operator can drive/monitor the run (status, feed hold, override, MDI) without
 * leaving the tool's tab. One shared instance; bound to the live GrblViewModel each time it is shown. It
 * tears itself down when the controller reports an error (the run has failed - the panel is no longer useful).
 */

using System;
using System.ComponentModel;
using System.Windows;
using CNC.Core;

namespace GCode_Sender
{
    public partial class MachineControlWindow : Window
    {
        private static MachineControlWindow _instance;
        private GrblViewModel _model;

        private MachineControlWindow()
        {
            InitializeComponent();
        }

        // Show (or re-surface) the shared machine-control panel for the given model.
        public static void ShowFor(GrblViewModel model, Window owner)
        {
            if (_instance == null)
            {
                _instance = new MachineControlWindow { Owner = owner };
                _instance.Closed += (s, e) => { _instance.Detach(); _instance = null; };
                if (owner != null && !double.IsNaN(owner.Left))
                {
                    _instance.Left = owner.Left + 60d;
                    _instance.Top = owner.Top + 130d;
                }
            }

            _instance.Attach(model);              // panel + child controls inherit DataContext
            if (!_instance.IsVisible)
                _instance.Show();
            _instance.Activate();
        }

        private void Attach(GrblViewModel model)
        {
            Detach();
            _model = model;
            DataContext = model;
            if (_model != null)
                _model.PropertyChanged += Model_PropertyChanged;
        }

        private void Detach()
        {
            if (_model != null)
                _model.PropertyChanged -= Model_PropertyChanged;
            _model = null;
        }

        // The run failed if the controller reports an error (GrblError != 0 on any error:N response) - close
        // the panel. Watching GrblError (a code), not the Message text, which is only the description.
        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GrblViewModel.GrblError) && (_model?.GrblError ?? 0) != 0)
                Dispatcher.BeginInvoke(new System.Action(Close));
        }
    }
}
