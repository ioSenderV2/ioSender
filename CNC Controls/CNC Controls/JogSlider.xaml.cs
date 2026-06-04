/*
 * JogSlider.xaml.cs - part of CNC Controls library for Grbl
 *
 * Compact stepper for the jog panels: [-] / [+] pill buttons step through the
 * presets (0..Maximum) with the current value shown between them. The unit lives
 * in the Header. It selects which preset is active; values are edited on Settings:App.
 *
 */

using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class JogSlider : UserControl
    {
        public JogSlider()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(JogSlider), new PropertyMetadata(string.Empty));
        public string Header { get { return (string)GetValue(HeaderProperty); } set { SetValue(HeaderProperty, value); } }

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(JogSlider), new PropertyMetadata(3));
        public int Maximum { get { return (int)GetValue(MaximumProperty); } set { SetValue(MaximumProperty, value); } }

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(JogSlider),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
        public int SelectedIndex { get { return (int)GetValue(SelectedIndexProperty); } set { SetValue(SelectedIndexProperty, value); } }

        public static readonly DependencyProperty ValueTextProperty =
            DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(JogSlider), new PropertyMetadata(string.Empty));
        public string ValueText { get { return (string)GetValue(ValueTextProperty); } set { SetValue(ValueTextProperty, value); } }

        private void Inc_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedIndex < Maximum)
                SelectedIndex++;
        }

        private void Dec_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedIndex > 0)
                SelectedIndex--;
        }
    }
}
