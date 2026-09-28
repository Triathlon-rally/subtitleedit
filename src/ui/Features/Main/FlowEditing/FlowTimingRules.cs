using System;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Main.FlowEditing;

// Also compiled by SubtitleWebEditor: Flow must use the same timing contract on both surfaces.
internal static class FlowTimingRules
{
    internal static bool IsContiguous(double firstEndMs, double nextStartMs, double gapMs)
    {
        var elapsedMs = nextStartMs - firstEndMs;
        var minimumMs = Math.Max(0, gapMs);
        // Existing-neighbour reflow counts both boundary pictures: :10 through :14
        // is five pictures. Also retain the previously accepted interval boundary
        // (:10 -> :15). Larger deliberate pauses do not become Flow neighbours.
        // This does not change the gap allocation or duration checks of TrySplit.
        return elapsedMs >= 0 && elapsedMs + 40 >= minimumMs - 0.5 && elapsedMs <= minimumMs + 0.5;
    }

    internal static bool TryReflow(string first, string second, double startMs, double endMs,
        double gapMs, double minimumMs, double maximumMs, double maximumCps, double preferredCps,
        out double firstEndMs, out double secondStartMs, out string error)
    {
        // Reflow uses the combined outer range and the agreed inclusive picture count:
        // five gap pictures span four frame intervals. New-subtitle split allocation is unchanged.
        var gapIntervalsMs = Math.Max(0, Math.Ceiling(gapMs / 40) - 1) * 40;
        // Reuse the allocator with the configured minimum/CPS as hard limits: no
        // short-duration or reading-tolerance compression for this redistribution.
        var valid = TrySplit(first, second, startMs, endMs, gapIntervalsMs, minimumMs, maximumMs,
            maximumCps, preferredCps, 0, false, 1, out firstEndMs, out secondStartMs, out error);
        if (!valid)
            error = "Flow reflow rejected: the combined time range cannot fit both reading durations and the full gap.";
        return valid;
    }

    internal static bool FitsExistingTime(string text, double startMs, double endMs,
        double minimumMs, double maximumMs, double maximumCps,
        double tolerancePercent, bool acceptShort, int shortFrames)
    {
        if (string.IsNullOrWhiteSpace(text) || !double.IsFinite(startMs) || !double.IsFinite(endMs) ||
            endMs <= startMs ||
            Math.Abs(startMs / 40 - Math.Round(startMs / 40)) > 0.00001 ||
            Math.Abs(endMs / 40 - Math.Round(endMs / 40)) > 0.00001)
            return false;
        var count = text.Count(c => c != '\r' && c != '\n');
        var requiredFrames = (int)Math.Ceiling(Math.Max(minimumMs, count * 1000.0 / maximumCps) / 40);
        // Identical accepted floor to TrySplit, without redistributing any time.
        var acceptedFrames = acceptShort ? Math.Max(1, shortFrames) :
            Math.Max(1, (int)Math.Ceiling(requiredFrames * Math.Max(0, 1 - tolerancePercent / 100)));
        var durationFrames = (int)Math.Round((endMs - startMs) / 40);
        return durationFrames >= acceptedFrames && durationFrames <= Math.Floor(maximumMs / 40);
    }

    internal static int SafeSplitIndex(string text, int caret)
    {
        var index = Math.Clamp(caret, 0, text.Length);
        if (index == 0 || index == text.Length || char.IsWhiteSpace(text[index - 1]) || char.IsWhiteSpace(text[index]))
            return index;
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start > 0) return start;
        var end = index;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
        return end < text.Length ? end : -1;
    }

    internal static bool TrySplit(string first, string second, double startMs, double endMs,
        double gapMs, double minimumMs, double maximumMs, double maximumCps,
        double preferredCps, double tolerancePercent, bool acceptShort, int shortFrames,
        out double firstEndMs, out double secondStartMs, out string error)
    {
        firstEndMs = secondStartMs = 0;
        error = "Flow split rejected: not enough time for valid reading durations and the full gap. Following subtitles were not moved.";
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) ||
            !double.IsFinite(startMs) || !double.IsFinite(endMs) || endMs <= startMs)
            return false;
        // Do not round the outer range outward or silently change existing timecodes.
        if (Math.Abs(startMs / 40 - Math.Round(startMs / 40)) > 0.00001 ||
            Math.Abs(endMs / 40 - Math.Round(endMs / 40)) > 0.00001)
        {
            error = "Flow split requires frame-aligned timecodes at 25 fps.";
            return false;
        }
        var gapFrames = (int)Math.Ceiling(Math.Max(0, gapMs) / 40);
        var available = (int)Math.Round((endMs - startMs) / 40) - gapFrames;
        var counts = new[] { first, second }.Select(t => Math.Max(1, t.Count(c => c != '\r' && c != '\n'))).ToArray();
        // Same accepted floors as ARTE: round required reading duration up to frames, then apply tolerance.
        var required = counts.Select(n => (int)Math.Ceiling(Math.Max(minimumMs, n * 1000.0 / maximumCps) / 40)).ToArray();
        var floors = required.Select(n => acceptShort ? Math.Max(1, shortFrames) :
            Math.Max(1, (int)Math.Ceiling(n * Math.Max(0, 1 - tolerancePercent / 100)))).ToArray();
        var maximum = (int)Math.Floor(maximumMs / 40);
        if (floors.Any(n => n > maximum) || available < floors.Sum() || available > 2L * maximum)
            return false;
        var weights = counts.Select(n => Math.Clamp(n * 1000.0 / preferredCps, minimumMs, maximumMs)).ToArray();
        var low = Math.Max(floors[0], available - maximum);
        var high = Math.Min(maximum, available - floors[1]);
        var firstFrames = Math.Clamp((int)Math.Round(available * weights[0] / weights.Sum()), low, high);
        firstEndMs = startMs + firstFrames * 40;
        secondStartMs = firstEndMs + gapFrames * 40;
        error = string.Empty;
        return true;
    }
}
