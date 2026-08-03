/*
 * NullToVisibilityConverter.cs - part of CNC Controls library
 *
 * Visible when the bound value is non-null, Collapsed when it is null. Used by the settings
 * navigation tree so a node with no status colour shows no status dot at all.
 */

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CNC.Controls
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
