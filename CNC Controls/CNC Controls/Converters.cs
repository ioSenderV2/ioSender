/*
 * Converters.cs - part of CNC Controls library for Grbl
 *
 * v0.47 / 2026-02-26 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Controls;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public static class Converters
    {
        public static StringCollectionToTextConverter StringCollectionToTextConverter = new StringCollectionToTextConverter();
        public static LatheModeToStringConverter LatheModeToStringConverter = new LatheModeToStringConverter();
        public static GrblStateToColorConverter GrblStateToColorConverter = new GrblStateToColorConverter();
        public static EncoderModeToColorConverter EncoderModeToColorConverter = new EncoderModeToColorConverter();
        public static GrblStateToStringConverter GrblStateToStringConverter = new GrblStateToStringConverter();
        public static GrblStateToTooltipConverter GrblStateToTooltipConverter = new GrblStateToTooltipConverter();
        public static BlocksToStringConverter BlocksToStringConverter = new BlocksToStringConverter();
        public static GrblStateToBooleanConverter GrblStateToBooleanConverter = new GrblStateToBooleanConverter();
        public static GrblStateToIsJoggingConverter GrblStateToIsJoggingConverter = new GrblStateToIsJoggingConverter();
        public static HomedStateToColorConverter HomedStateToColorConverter = new HomedStateToColorConverter();
        public static IsHomingEnabledConverter IsHomingEnabledConverter = new IsHomingEnabledConverter();
        public static HomedStateToBooleanConverter HomedStateToBooleanConverter = new HomedStateToBooleanConverter();
        public static LogicalNotConverter LogicalNotConverter = new LogicalNotConverter();
        public static LogicalAndConverter LogicalAndConverter = new LogicalAndConverter();
        public static LogicalOrConverter LogicalOrConverter = new LogicalOrConverter();
        public static BoolToVisibleConverter BoolToVisibleConverter = new BoolToVisibleConverter();
        public static NotBoolToVisibleConverter NotBoolToVisibleConverter = new NotBoolToVisibleConverter();
        public static BoolToColorConverter BoolToColorConverter = new BoolToColorConverter();
        public static IsAxisVisibleConverter HasAxisConverter = new IsAxisVisibleConverter();
        public static IsSignalVisibleConverter IsSignalVisibleConverter = new IsSignalVisibleConverter();
        public static EnumValueToBooleanConverter EnumValueToBooleanConverter = new EnumValueToBooleanConverter();
        public static StringAddToConverter StringAddToConverter = new StringAddToConverter();
        public static MultiLineConverter MultiLineConverter = new MultiLineConverter();
        public static PositionToStringConverter PositionToStringConverter = new PositionToStringConverter();
        public static FeedSpeedToStringConverter FeedSpeedToStringConverter = new FeedSpeedToStringConverter();
        public static AxisLetterToJogPlusConverter AxisLetterToJogPlusConverter = new AxisLetterToJogPlusConverter();
        public static AxisLetterToJogMinusConverter AxisLetterToJogMinusConverter = new AxisLetterToJogMinusConverter();

        internal static string numBlocks = LibStrings.FindResource("NumBlocks");
        internal static string blockOfBlocks = LibStrings.FindResource("BlockOfBlocks");
        internal static Lazy<Dictionary<GrblStates, string>> grblState = new Lazy<Dictionary<GrblStates, string>>(() =>
            new Dictionary<GrblStates, string> {
                { GrblStates.Unknown, LibStrings.FindResource("StateUnknown") },
                { GrblStates.Idle, LibStrings.FindResource("StateIdle") },
                { GrblStates.Run, LibStrings.FindResource("StateRun") },
                { GrblStates.Tool, LibStrings.FindResource("StateTool") },
                { GrblStates.Hold, LibStrings.FindResource("StateHold") },
                { GrblStates.Home, LibStrings.FindResource("StateHome") },
                { GrblStates.Check, LibStrings.FindResource("StateCheck") },
                { GrblStates.Jog, LibStrings.FindResource("StateJog") },
                { GrblStates.Alarm, LibStrings.FindResource("StateAlarm") },
                { GrblStates.Door, LibStrings.FindResource("StateDoor") },
                { GrblStates.Sleep, LibStrings.FindResource("StateSleep") }
            });

        // Plain-language help shown as the State field tooltip, aimed at newcomers.
        internal static Lazy<Dictionary<GrblStates, string>> grblStateHelp = new Lazy<Dictionary<GrblStates, string>>(() =>
            new Dictionary<GrblStates, string> {
                { GrblStates.Unknown, "Not connected, or the controller state is unknown." },
                { GrblStates.Idle, "Ready - idle and waiting for commands." },
                { GrblStates.Run, "Running - executing a program or commanded motion." },
                { GrblStates.Hold, string.Format("{0} - motion is paused. Press {1} to resume.", RunLabels.FeedHold, RunLabels.CycleStart) },
                { GrblStates.Jog, "Jogging - a manual move is in progress." },
                { GrblStates.Home, "Homing - seeking the machine's reference (limit) switches." },
                { GrblStates.Check, "Check mode - G-code is parsed and validated but not executed; no motion. Turn it off to cut for real." },
                { GrblStates.Tool, "Tool change - waiting for a manual tool change; follow the prompt, then resume." },
                { GrblStates.Door, "Safety door open - motion is suspended until the door is closed and you resume." },
                { GrblStates.Sleep, "Sleep - the controller is parked. Reset to continue." }
            });
    }

    // Program-outline group header -> that section's estimated run time, e.g. "~4 min".
    //
    // Two inputs on purpose: the section NAME (what to look up) and GCodeRunTimeIndex.Version (WHEN to
    // look). The estimate is computed off the UI thread after the load, so it does not exist when these
    // headers are first built; bumping Version is what brings the visible ones back to re-read it. The
    // second value is deliberately unused beyond that.
    public class SectionRunTimeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string section = values != null && values.Length > 0 ? values[0] as string : null;
            return GCodeRunTimeIndex.Instance.Lookup(section);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Adapted from: https://stackoverflow.com/questions/4353186/binding-observablecollection-to-a-textbox/8847910#8847910
    public class StringCollectionToTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var data = values[0] as ObservableCollection<string>;

            if (data != null && data.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                foreach (var s in data)
                {
                    sb.AppendLine(s.ToString());
                }
                return sb.ToString();
            }
            else
                return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // --

    public class LatheModeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string result = string.Empty;

            if (value is LatheMode && (LatheMode)value != LatheMode.Disabled)
                result = (LatheMode)value == LatheMode.Radius ? "Radius" : "Diameter";

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class AxisLetterToJogPlusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string result = string.Empty;

            if (value is string)
                result = (string)value + "+";

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    public class AxisLetterToJogMinusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string result = string.Empty;

            if (value is string)
                result = (string)value + "-";

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BlocksToStringConverter : IMultiValueConverter
    {
        public object Convert(object[] value, Type targetType, object parameter, CultureInfo culture)
        {
            return value[0] is int && value[1] is int ? (string.Format((int)value[1] == 0 ? Converters.numBlocks : Converters.blockOfBlocks, value[1], value[0])) : string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class PositionToStringConverter : IMultiValueConverter
    {
        public object Convert(object[] value, Type targetType, object parameter, CultureInfo culture)
        {
            string res = string.Empty;
            string format = value.Length > 1 && value[1] is string ? value[1] as string : "####0.000";

            if(value[0] is Position) switch(GrblInfo.NumAxes)
            {
                case 4:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format),
                                           (value[0] as Position).A.ToInvariantString(format));
                    break;

                case 5:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format),
                                           (value[0] as Position).A.ToInvariantString(format),
                                            (value[0] as Position).B.ToInvariantString(format));
                    break;

                case 6:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format),
                                           (value[0] as Position).A.ToInvariantString(format),
                                            (value[0] as Position).B.ToInvariantString(format),
                                             (value[0] as Position).C.ToInvariantString(format));
                    break;

                case 7:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format),
                                           (value[0] as Position).A.ToInvariantString(format),
                                            (value[0] as Position).B.ToInvariantString(format),
                                             (value[0] as Position).C.ToInvariantString(format),
                                              (value[0] as Position).U.ToInvariantString(format));
                    break;

                case 8:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format),
                                           (value[0] as Position).A.ToInvariantString(format),
                                            (value[0] as Position).B.ToInvariantString(format),
                                             (value[0] as Position).C.ToInvariantString(format),
                                              (value[0] as Position).U.ToInvariantString(format),
                                               (value[0] as Position).V.ToInvariantString(format));
                    break;

                case 9:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format),
                                           (value[0] as Position).A.ToInvariantString(format),
                                            (value[0] as Position).B.ToInvariantString(format),
                                             (value[0] as Position).C.ToInvariantString(format),
                                              (value[0] as Position).U.ToInvariantString(format),
                                               (value[0] as Position).V.ToInvariantString(format),
                                                (value[0] as Position).W.ToInvariantString(format));
                    break;

                default:
                    res = string.Format(GrblInfo.PositionFormatString,
                                        (value[0] as Position).X.ToInvariantString(format),
                                         (value[0] as Position).Y.ToInvariantString(format),
                                          (value[0] as Position).Z.ToInvariantString(format));
                    break;
            }

            return res;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FeedSpeedToStringConverter : IMultiValueConverter
    {
        public object Convert(object[] value, Type targetType, object parameter, CultureInfo culture)
        {
            return value.Length == 2 && value[0] is double && value[1] is double
                    ? string.Format("F: {0}  S: {1}", ((double)value[0]).ToInvariantString(), ((double)value[1]).ToInvariantString())
                    : string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class GrblStateToColorConverter : IValueConverter
    {
        // Ported verbatim from GrblViewModel, where the colour used to be computed into GrblState.Color
        // as the state was parsed. Which colour represents a machine state is presentation policy, so it
        // belongs here rather than in the machine model - CNC.Core no longer references System.Windows.Media.
        public static Color ForState(GrblState state)
        {
            switch (state.State)
            {
                case GrblStates.Run:
                    return Colors.LightGreen;

                case GrblStates.Alarm:
                    return Colors.Red;

                case GrblStates.Jog:
                    return Colors.Yellow;

                case GrblStates.Tool:
                    return Colors.LightSalmon;

                case GrblStates.Hold:
                    return Colors.LightSalmon;

                case GrblStates.Door:
                    return state.Substate == 0 ? Colors.LightSalmon : (state.Substate == 1 ? Colors.Red : Colors.Beige);

                case GrblStates.Home:
                case GrblStates.Sleep:
                    return Colors.LightSkyBlue;

                case GrblStates.Check:
                    return Colors.White;

                default:
                    return Colors.White;
            }
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Brush result = Brushes.White;

            if (value is GrblState)
                result = new SolidColorBrush(ForState((GrblState)value));

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IsHomingEnabledConverter : IMultiValueConverter
    {
        public object Convert(object[] value, Type targetType, object parameter, CultureInfo culture)
        {
            GrblStates state = value[0] is GrblState ? ((GrblState)value[0]).State : GrblStates.Unknown;

            // If ALARM:11 homing is required
            bool result = state == GrblStates.Alarm && ((GrblState)value[0]).Substate == 11;

            // value[1] = IsJobRunning
            // value[2] = IsSleeping

            if (!result && GrblInfo.HomingEnabled && value.Length > 2 && value[1] is bool && !(bool)value[1] && value[2] is bool && !(bool)value[2])
                result = state != GrblStates.Unknown && !((GrblState)value[0]).MPG && (state == GrblStates.Idle || state == GrblStates.Alarm || !GrblInfo.IsGrblHAL);

            return result;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class HomedStateToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Brush result = System.Windows.SystemColors.ControlBrush;

            if (value is HomedState) switch ((HomedState)value)
            {
                case HomedState.NotHomed:
                    result = Brushes.LightYellow;
                    break;

                case HomedState.Homed:
                    result = Brushes.LightGreen;
                    break;
            }

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class HomedStateToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is HomedState && (HomedState)value == HomedState.Homed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class EncoderModeToColorConverter : IMultiValueConverter
    {
        public static SolidColorBrush ReadOnlyBackGround { get; } = (SolidColorBrush)(new BrushConverter().ConvertFrom("#FFF8F8F8"));

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool result = true;

            foreach (var value in values)
                result &= value is bool && (bool)value;

            return values.Length == 2 && values[0] is GrblEncoderMode && !values[0].Equals(GrblEncoderMode.Unknown) && values[1] is GrblEncoderMode && values[0].Equals(values[1]) ? Brushes.Salmon : ReadOnlyBackGround;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class GrblStateToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string result = string.Empty;

            Converters.grblState.Value.TryGetValue(((GrblState)value).State, out result);
            int substate = ((GrblState)value).State == GrblStates.Alarm && ((GrblState)value).LastAlarm > 0 ? ((GrblState)value).LastAlarm : ((GrblState)value).Substate;

            if (value is GrblState && ((GrblState)value).State != GrblStates.Unknown) 
                result = (result == string.Empty ? ((GrblState)value).State.ToString().ToUpper() : result) + (substate == -1 ? "" : (":" + substate.ToString()));

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Produces a plain-language tooltip for the State field; for an alarm it appends the
    // controller's specific alarm message (e.g. "ALARM 4: Probe fail ...") so a newcomer can
    // tell what the code means without looking it up.
    public class GrblStateToTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is GrblState))
                return null;

            var gs = (GrblState)value;

            string help;
            if (!Converters.grblStateHelp.Value.TryGetValue(gs.State, out help))
                help = gs.State.ToString();

            if (gs.State == GrblStates.Alarm)
            {
                int code = gs.LastAlarm > 0 ? gs.LastAlarm : gs.Substate;
                string msg;
                GrblAlarms.List.TryGetValue(code, out msg);
                help = "ALARM " + code + (string.IsNullOrEmpty(msg) ? "" : ": " + msg)
                     + "\n\nThe machine is locked. Clear the cause, then Unlock ($X) - or Home if the position may be lost.";
            }

            return help;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class GrblStateToBooleanConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return values.Length == 2 && values[0] is GrblState && values[1] is GrblStates && ((GrblState)values[0]).State == (GrblStates)values[1];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class GrblStateToIsJoggingConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is GrblState && ((GrblState)value).State == GrblStates.Jog;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Appended after the (localized) DRO header text - blank before a controller connection has reported a
    // work coordinate system, " (G54)" once GrblViewModel.WorkCoordinateSystem has a real value. A separate
    // run rather than baked into the header string itself, so the translated "DRO" word (several locales have
    // a real translation, not just the literal word - de-DE "Anzeige", ru-RU/uk-UA/zh-CN their own) stays
    // driven by the normal x:Uid/CSV mechanism instead of being replaced by an English-only binding.
    public class WcsHeaderSuffixConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string wcs = value as string;
            return string.IsNullOrEmpty(wcs) ? string.Empty : string.Format(" ({0})", wcs);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Per-axis DRO field tooltip - so hovering a single axis shows THAT axis's own live work offset without
    // checking the separate Offsets flyout (see WcsHeaderSuffixConverter for the DRO group header's own,
    // whole-panel "(G54)" indicator this complements). ConverterParameter carries the axis letter (fixed -
    // "X"/"Y"/etc, the underlying WorkPositionOffset property name - not whatever a remapped axis Label
    // happens to display). No tooltip at all (null) until a controller has actually reported a WCS - showing
    // "0.000" before any connection would look like a real reading.
    //
    // Deliberately NOT worded "<WCS> offset" - GrblViewModel.WorkPositionOffset is grblHAL's WCO, which bundles
    // the active WCS's own stored value with G92 and any active tool length offset (G43.1) - see
    // macros/pcorner.macro's own comment ("WCO = G5x offset + G92 offset + TLO"). Confirmed as a real,
    // user-facing mislabel 2026-07-31: the Offsets grid showed G54's Z stored at 0.000, but this tooltip said
    // "G54 Z offset: -9.341" - correct NUMBER (that's really what's shifting Z right now), wrong claim about
    // where it came from (an active TLO, not G54's table entry). Naming the WCS but not claiming the number
    // IS its stored value avoids repeating that.
    //
    // A 3rd binding (values[2], GrblViewModel.ToolOffset.Z) is optional - only Z's own MultiBinding in
    // DROControl.xaml supplies it, since TLO is a Z-axis-only concept (see GrblViewModel's own TLO parsing,
    // which folds a reported TLO straight into ToolOffset.Z and always zeroes ToolOffset.X/Y - confirmed
    // 2026-07-31, there is no X/Y TLO to show). Its ABSENCE (not just a null/NaN value) is what decides
    // whether "TLO" is mentioned at all - X/Y/A/B/C/U/V/W say "(G54 + G92 combined)" with no TLO wording,
    // since it plays no part in their offset; only Z (which always supplies a 3rd binding, even if the value
    // itself is NaN before TLO is known) says "+ TLO".
    public class WcsOffsetTooltipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string wcs = values.Length > 0 ? values[0] as string : null;
            if (string.IsNullOrEmpty(wcs) || values.Length < 2 || !(values[1] is double offset))
                return null;

            string line2;
            if (values.Length <= 2)
                line2 = string.Format(CultureInfo.CurrentCulture, "({0} + G92 combined)", wcs);
            else
            {
                double? tlo = values[2] is double t && !double.IsNaN(t) ? t : (double?)null;
                line2 = tlo.HasValue
                    ? string.Format(CultureInfo.CurrentCulture, "({0} + G92 + TLO {1:0.000} combined)", wcs, tlo.Value)
                    : string.Format(CultureInfo.CurrentCulture, "({0} + G92 + TLO combined)", wcs);
            }

            // Spell the relation out, not just the total. The DRO is not "where the machine is" - it is where
            // the machine is MINUS every offset in effect, and that difference is exactly what a zero means.
            // Sign convention is the firmware's own (grblHAL gcode.c, the G10 L20 case): all three offsets
            // SUBTRACT, so a tool length offset moves the readout the same direction G92 does, not the opposite.
            string line3 = string.Format(CultureInfo.CurrentCulture, "Work = machine - ({0} + G92 + TLO)", wcs);

            return string.Format(CultureInfo.CurrentCulture, "Total {0} offset: {1:0.000}\n{2}\n{3}", parameter, offset, line2, line3);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class LogicalNotConverter : IValueConverter
    {
        public IValueConverter FinalConverter { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool result = (value is bool ? !(bool)value : ((value is bool?) ? (bool?)value != true : ((value is int) ? (int)value == 0 : false))) || value == null;

            return FinalConverter == null ? result : FinalConverter.Convert(result, targetType, parameter, culture);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(bool)value;
        }
    }

    public class LogicalAndConverter : IMultiValueConverter
    {
        public IValueConverter FinalConverter { get; set; }

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool result = true;

            foreach (var value in values)
                result &= value is bool && (bool)value;

            return FinalConverter == null ? result : FinalConverter.Convert(result, targetType, parameter, culture);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // machine position = work position (values[0]) + work-coordinate offset (values[1]).
    // Rides the live Position notifications the DRO uses; a NaN/absent offset is treated as 0.
    public class PositionSumConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            double work = values.Length > 0 && values[0] is double w ? w : double.NaN;
            double offset = values.Length > 1 && values[1] is double o && !double.IsNaN(o) ? o : 0d;
            return double.IsNaN(work) ? double.NaN : work + offset;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class LogicalOrConverter : IMultiValueConverter
    {
        public IValueConverter FinalConverter { get; set; }

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool result = false;

            foreach (var value in values)
                result |= value is bool && (bool)value;

            return FinalConverter == null ? result : FinalConverter.Convert(result, targetType, parameter, culture);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool && (bool)value ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }

    // Inverse of BoolToVisibleConverter: false -> Visible, true -> Collapsed. Handy for the two-state
    // program-view header (Load buttons visible when NO file is loaded).
    public class NotBoolToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool && (bool)value ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility && (Visibility)value != Visibility.Visible;
        }
    }

    public class BoolToColorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool result = true;

            foreach (var value in values)
                result &= value is bool && (bool)value;

            return values.Length == 3 && values[0] is bool && (bool)values[0] ? values[1] : values[2];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IsAxisVisibleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool enabled = false;

            if(values.Length == 2 && values[0] is int && values[1] is int && (int)values[0] >= (int)values[1])
                enabled = ((int)values[0] & (int)values[1]) != 0;

            if(values.Length == 2 && values[0] is AxisFlags && values[1] is AxisFlags)
                enabled = ((AxisFlags)values[0]).HasFlag((AxisFlags)values[1]);

            return enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IsSignalVisibleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool enabled = false;

            if (values.Length == 2 && values[0] is int && values[1] is int && (int)values[0] >= (int)values[1])
                enabled = ((int)values[0] & (int)values[1]) != 0;

            if (values.Length == 2 && values[0] is EnumFlags<Signals> && values[1] is Signals)
                enabled = ((EnumFlags<Signals>)values[0]).Value.HasFlag((Signals)values[1]);

            return enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StringAddToConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return values.Length == 2 ? values[0].ToString() + string.Format((string)parameter, values[1].ToString()) : string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class EnumValueToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            string checkValue = value.ToString();
            string targetValue = parameter.ToString();
            return checkValue.Equals(targetValue,
                     StringComparison.InvariantCultureIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return null;

            bool useValue = (bool)value;
            string targetValue = parameter.ToString();
            if (useValue)
                return Enum.Parse(targetType, targetValue);

            return null;
        }
    }

    // by  D4rth B4n3 - https://stackoverflow.com/questions/30627368/how-to-create-a-tooltip-to-display-multiple-validation-errors-for-a-single-contr
    public class MultiLineConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(values[0] is IEnumerable<ValidationError>))
                return null;

            //string.Join(",", (List<string>)logic.Model.GetErrors(e.PropertyName)));

            var val = values[0] as IEnumerable<ValidationError>;

            string retVal = "";

            foreach (var itm in val)
            {
                if (retVal.Length > 0)
                    retVal += "\n";
                retVal += itm.ErrorContent;

            }
            return retVal;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
