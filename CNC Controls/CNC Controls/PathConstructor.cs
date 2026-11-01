/*
 * PathConstructor.cs - part of CNC Controls library
 *
 * A XAML markup extension, moved out of CNC.Core/HelperClasses.cs: MarkupExtension, ContentProperty and
 * PropertyPath are WPF types, and they were the last thing keeping HelperClasses.cs from being portable.
 *
 * NOTE: nothing in the solution references this - no code, no XAML. It is kept rather than deleted
 * because it is inert (unlike the dead KeypressHandler removed in ab10c96, which was actively
 * misleading). If it is still unreferenced next time this file is touched, delete it.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    [ContentProperty("Parameters")]
    public class PathConstructor : MarkupExtension
    {
        public string Path { get; set; }
        public IList Parameters { get; set; }

        public PathConstructor()
        {
            Parameters = new List<object>();
        }

        public PathConstructor(string b, object p0)
        {
            Path = b;
            Parameters = new[] { p0 };
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
        //    return new PropertyPath(Path, Parameters.Cast<object>().ToArray());
            return new PropertyPath(String.Format("{0}[{1}]", Path, StringEnumConversion.ConvertToEnum<SpindleState>(Parameters[0])));
        }
    }
}
