/*
 * ProgramSourceConverter.cs - part of ioSender XL
 *
 * Formats GrblViewModel.FileName into a friendly "source" label for the program-view title:
 * a file path, a folder name, or a tool name (e.g. "Surface Spoilboard"). Strips the lathe
 * wizards' "Wizard:" prefix and shows a placeholder when no program is loaded.
 */

using System;
using System.Globalization;
using System.Windows.Data;

namespace GCode_Sender
{
    public class ProgramSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            if (string.IsNullOrEmpty(s))
                return "(no program loaded)";
            if (s.StartsWith("Wizard:", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Wizard:".Length).Trim();
            return s;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
