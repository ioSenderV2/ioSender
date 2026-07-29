/*
 * MacroPinnedListControl.xaml.cs - part of CNC Controls library
 *
 * Compact main-page/left-column form of the Macros panel (MainPanelRegistry's "Macros" item now
 * supports both this and the sidebar flyout form, MacroExecuteControl). Fixed ~4-row-tall ListBox,
 * pinned macros floated to the top, with a real scrollbar to reach the rest and pin more - no
 * focus-driven expand/collapse (that made every click double as either "run" or "pin", leaving no
 * safe way to just scroll and browse).
 */

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MacroPinnedListControl : UserControl
    {
        private ObservableCollection<CNC.GCode.Macro> _macros;

        public MacroPinnedListControl()
        {
            InitializeComponent();
            // Instantiated here (not via PropertyMetadata default) - a mutable default value on a
            // DependencyProperty is shared across every instance of the control, which would make
            // every placed Macros panel share one displayed list.
            DisplayedItems = new ObservableCollection<CNC.GCode.Macro>();
        }

        public static readonly DependencyProperty DisplayedItemsProperty = DependencyProperty.Register(nameof(DisplayedItems), typeof(ObservableCollection<CNC.GCode.Macro>), typeof(MacroPinnedListControl));
        public ObservableCollection<CNC.GCode.Macro> DisplayedItems
        {
            get { return (ObservableCollection<CNC.GCode.Macro>)GetValue(DisplayedItemsProperty); }
            set { SetValue(DisplayedItemsProperty, value); }
        }

        public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.Register(nameof(IsEmpty), typeof(bool), typeof(MacroPinnedListControl), new PropertyMetadata(true));
        public bool IsEmpty
        {
            get { return (bool)GetValue(IsEmptyProperty); }
            set { SetValue(IsEmptyProperty, value); }
        }

        private void MacroPinnedListControl_Loaded(object sender, RoutedEventArgs e)
        {
            _macros = AppConfig.Settings.Macros;
            _macros.CollectionChanged += (s, ev) =>
            {
                IsEmpty = _macros.Count == 0;
                RefreshDisplay();
            };
            IsEmpty = _macros.Count == 0;
            RefreshDisplay();
        }

        // Every macro, pinned ones first (in pinned order) then the rest - the box shows ~4 rows and
        // scrolls for more. Also drops any pinned Id whose macro was deleted since.
        private void RefreshDisplay()
        {
            var ids = AppConfig.Settings.Base.PinnedMacros;
            var byId = _macros.ToDictionary(m => m.Id);

            var stale = ids.Where(id => !byId.ContainsKey(id)).ToList();
            if (stale.Count > 0)
            {
                foreach (var id in stale)
                    ids.Remove(id);
                AppConfig.Settings.Save();
            }

            DisplayedItems.Clear();

            foreach (var id in ids)
                if (byId.TryGetValue(id, out var m))
                    DisplayedItems.Add(m);

            foreach (var m in _macros)
                if (!DisplayedItems.Contains(m))
                    DisplayedItems.Add(m);
        }

        // Focus leaving the list entirely (not just moving between its own buttons) - scroll back to
        // the top so the pinned top-4 is what's showing whenever the user isn't actively scrolling to
        // find something to pin.
        private void MacroListBox_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool within && !within && macroListBox.Items.Count > 0)
                macroListBox.ScrollIntoView(macroListBox.Items[0]);
        }

        private void macroButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CNC.GCode.Macro macro)
            {
                if (MacroProcessor.Run(DataContext as GrblViewModel, macro.Name, macro.Code, macro.ConfirmOnExecute))
                    AppConfig.Settings.RecordMacroRun(macro.Id);
            }
        }

        // Pins the macro to slot 0 (top), pushing the rest down one; whatever falls off slot 3 (the
        // 5th) loses its pinned status automatically - the only way anything gets unpinned. Re-pinning
        // an already-pinned macro just moves it back to slot 0. Scrolls back to the top afterwards so
        // the refreshed top-4 is immediately visible instead of leaving the list scrolled wherever it was.
        private void pinButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CNC.GCode.Macro macro)
            {
                var ids = AppConfig.Settings.Base.PinnedMacros;
                ids.Remove(macro.Id);
                ids.Insert(0, macro.Id);
                while (ids.Count > 4)
                    ids.RemoveAt(ids.Count - 1);
                AppConfig.Settings.Save();
                RefreshDisplay();

                if (macroListBox.Items.Count > 0)
                    macroListBox.ScrollIntoView(macroListBox.Items[0]);
            }
        }
    }

    // Pin-icon color: green while the macro is in the pinned shortlist, grey otherwise. Bound to the
    // whole row's Macro (plain {Binding}, no Path) since pinned state isn't a property on Macro itself.
    public class MacroPinnedColorConverter : System.Windows.Data.IValueConverter
    {
        private static readonly System.Windows.Media.Brush Pinned = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly System.Windows.Media.Brush Unpinned = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99));

        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is CNC.GCode.Macro macro && AppConfig.Settings.Base.PinnedMacros.Contains(macro.Id) ? Pinned : Unpinned;
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new System.NotImplementedException();
        }
    }
}
