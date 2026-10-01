using Avalonia.Data.Converters;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Globalization;

namespace Nikse.SubtitleEdit.Logic.ValueConverters;

public class DoubleToDisplayShortConverter : IValueConverter
{
    public static readonly DoubleToDisplayShortConverter Instance = new();

    // Reused to avoid per-call TimeCode allocations (expected to be used from the UI thread only).
    private readonly TimeCode _formattingTimeCode = new();
    private const string ZeroFrameMode = "00.00";
    private const string ZeroTime = "00,000";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var useFrameMode = Se.Settings.General.UseFrameMode;
        if (value is double ms)
        {
            if (ms == double.MaxValue || double.IsNaN(ms))
            {
                // Sentinel for "no value" (e.g. the gap after the last line) - show nothing
                // instead of a clamped "0,000".
                return string.Empty;
            }

            _formattingTimeCode.TotalMilliseconds = ms;
            return useFrameMode
                ? FormatFrameGap(ms)
                : _formattingTimeCode.ToShortString();
        }

        return useFrameMode ? ZeroFrameMode : ZeroTime;
    }

    private static string FormatFrameGap(double milliseconds)
    {
        var frameRate = Se.Settings.General.CurrentFrameRate;
        if (frameRate <= 0)
        {
            return new TimeCode(milliseconds).ToShortStringHHMMSSFF();
        }

        // Round the complete gap once with Subtitle Edit's normal frame conversion. Both the
        // minute threshold and the displayed value must use that same whole-frame result.
        var signedFrames = SubtitleFormat.MillisecondsToFrames(milliseconds, frameRate);
        var totalFrames = Math.Abs((long)signedFrames);
        var framesPerMinute = SubtitleFormat.MillisecondsToFrames(60_000, frameRate);
        var roundedMilliseconds = SubtitleFormat.FramesToMilliseconds(totalFrames, frameRate);

        var totalSeconds = roundedMilliseconds / 1000;
        var millisecondsInSecond = roundedMilliseconds % 1000;
        var frames = SubtitleFormat.MillisecondsToFrames(millisecondsInSecond, frameRate);

        // Fractional rates can put the rounded millisecond value at the end of the preceding
        // second (for example 59,993 ms at 29.97 fps). Preserve TimeCode's existing carry rule.
        if (frames >= frameRate - 0.001)
        {
            totalSeconds++;
            frames = 0;
        }

        var sign = signedFrames < 0 ? "-" : string.Empty;
        if (totalFrames < framesPerMinute)
        {
            return $"{sign}{totalSeconds:00}:{frames:00}";
        }

        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;
        return $"{sign}{hours:00}:{minutes:00}:{seconds:00}:{frames:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
