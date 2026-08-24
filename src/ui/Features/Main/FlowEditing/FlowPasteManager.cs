using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main.FlowEditing;

public sealed class FlowPasteManager
{
    private static readonly Regex TimeCodeRangeRegex = new(
        @"^\s*(?<start>\d{1,2}:\d{2}:\d{2}(?:(?:[.,]\d{1,3})|(?::\d{2}))?)\s*(?:-->|->|–|—|-|\t+|\s{2,})\s*(?<end>\d{1,2}:\d{2}:\d{2}(?:(?:[.,]\d{1,3})|(?::\d{2}))?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex TimeCodeLikeRegex = new(
        @"\d{1,2}:\d{2}:\d{2}(?:(?:[.,]\d{1,3})|(?::\d{2}))?",
        RegexOptions.Compiled);

    private static readonly Regex NumericCueRegex = new(
        @"^\s*\d+\s*$",
        RegexOptions.Compiled);

    public FlowPastePlan BuildPlan(
        string clipboardText,
        TimeSpan insertionStart,
        TimeSpan? nextExistingSubtitleStart,
        bool hasColor)
    {
        var normalized =
            NormalizeClipboardText(
                clipboardText);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return FlowPastePlan.Failure(
                "Clipboard contains no text.");
        }

        var timedParse =
            ParseTimedBlocks(
                normalized);

        if (timedParse.HasTimeCodes)
        {
            if (!timedParse.Success)
            {
                return FlowPastePlan.Failure(
                    timedParse.ErrorMessage ??
                    "The pasted time codes could not be parsed.");
            }

            return BuildTimedPlan(
                timedParse.Blocks,
                insertionStart,
                nextExistingSubtitleStart,
                hasColor);
        }

        return BuildPlainTextPlan(
            normalized,
            insertionStart,
            nextExistingSubtitleStart,
            hasColor);
    }

    private static FlowPastePlan BuildPlainTextPlan(
        string text,
        TimeSpan insertionStart,
        TimeSpan? nextExistingSubtitleStart,
        bool hasColor)
    {
        var maxCharactersPerLine =
            hasColor ? 36 : 37;

        var subtitleTexts =
            SplitPlainTextIntoSubtitles(
                text,
                maxCharactersPerLine);

        if (subtitleTexts.Count == 0)
        {
            return FlowPastePlan.Failure(
                "Clipboard contains no usable subtitle text.");
        }

        var gapMs =
            GetMinimumGapMilliseconds();

        var cursorMs =
            insertionStart.TotalMilliseconds;

        var items =
            new List<FlowPasteItem>();

        foreach (var subtitleText in subtitleTexts)
        {
            var durationMs =
                CalculateDurationMilliseconds(
                    subtitleText);

            var startMs =
                cursorMs;

            var endMs =
                startMs + durationMs;

            items.Add(
                new FlowPasteItem(
                    TimeSpan.FromMilliseconds(startMs),
                    TimeSpan.FromMilliseconds(endMs),
                    subtitleText,
                    HasExplicitTimeCodes: false));

            cursorMs =
                endMs + gapMs;
        }

        var requiredEnd =
            items[^1].EndTime;

        var fitResult =
            CheckAvailableSpace(
                insertionStart,
                requiredEnd,
                nextExistingSubtitleStart);

        if (!fitResult.Success)
        {
            return FlowPastePlan.Failure(
                fitResult.ErrorMessage!,
                items,
                hasExplicitTimeCodes: false);
        }

        return FlowPastePlan.Successful(
            items,
            hasExplicitTimeCodes: false);
    }

    private static FlowPastePlan BuildTimedPlan(
        IReadOnlyList<FlowTimedTextBlock> blocks,
        TimeSpan insertionStart,
        TimeSpan? nextExistingSubtitleStart,
        bool hasColor)
    {
        var maxCharactersPerLine =
            hasColor ? 36 : 37;

        var requiredGapMs =
            GetMinimumGapMilliseconds();

        var items =
            new List<FlowPasteItem>();

        foreach (var block in blocks)
        {
            if (block.EndTime <= block.StartTime)
            {
                return FlowPastePlan.Failure(
                    "A pasted time code has an end time before its start time.");
            }

            var normalizedText =
                NormalizeTimedSubtitleText(
                    block.Text);

            if (!TryRebalanceTimedSubtitle(
                    normalizedText,
                    maxCharactersPerLine,
                    out var finalText))
            {
                return FlowPastePlan.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "A time-coded subtitle exceeds the Teletext limit " +
                        "(max 2 lines, {0} characters per line).",
                        maxCharactersPerLine));
            }

            items.Add(
                new FlowPasteItem(
                    block.StartTime,
                    block.EndTime,
                    finalText,
                    HasExplicitTimeCodes: true));
        }

        if (items.Count == 0)
        {
            return FlowPastePlan.Failure(
                "No valid time-coded subtitles were found.");
        }

        // Explicit TCs are absolute. They are never shifted to the selected
        // Flow position. The selected anchor merely defines the earliest
        // available insertion point.
        if (items[0].StartTime < insertionStart)
        {
            return FlowPastePlan.Failure(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Paste not possible. The first pasted subtitle starts at {0}, " +
                    "before the available insertion time {1}.",
                    FormatTime(items[0].StartTime),
                    FormatTime(insertionStart)));
        }

        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].StartTime < items[i - 1].EndTime)
            {
                return FlowPastePlan.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Paste not possible. Pasted subtitles {0} and {1} overlap.",
                        i,
                        i + 1));
            }

            var actualGapMs =
                (items[i].StartTime -
                 items[i - 1].EndTime)
                .TotalMilliseconds;

            if (actualGapMs + 0.5 < requiredGapMs)
            {
                return FlowPastePlan.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Paste not possible. The pasted time codes contain a gap of " +
                        "{0:0.00} s, but the current minimum gap is {1:0.00} s.",
                        actualGapMs / 1000.0,
                        requiredGapMs / 1000.0));
            }
        }

        if (nextExistingSubtitleStart.HasValue)
        {
            var latestAllowedEnd =
                nextExistingSubtitleStart.Value -
                TimeSpan.FromMilliseconds(
                    requiredGapMs);

            if (items[^1].EndTime > latestAllowedEnd)
            {
                var availableSeconds =
                    Math.Max(
                        0.0,
                        (latestAllowedEnd -
                         insertionStart)
                        .TotalSeconds);

                var requiredSeconds =
                    Math.Max(
                        0.0,
                        (items[^1].EndTime -
                         insertionStart)
                        .TotalSeconds);

                return FlowPastePlan.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Not enough time before the next subtitle. " +
                        "The pasted time-coded block requires {0:0.00} s, " +
                        "but only {1:0.00} s are available.",
                        requiredSeconds,
                        availableSeconds));
            }
        }

        return FlowPastePlan.Successful(
            items,
            hasExplicitTimeCodes: true);
    }

    private static FlowTimedParseResult ParseTimedBlocks(
        string text)
    {
        var lines =
            text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
                .Replace(
                    '\r',
                    '\n')
                .Split('\n');

        var hasTimeCodes =
            lines.Any(
                line =>
                    TimeCodeLikeRegex.IsMatch(line));

        if (!hasTimeCodes)
        {
            return FlowTimedParseResult.NoTimeCodes();
        }

        var blocks =
            new List<FlowTimedTextBlock>();

        var index = 0;

        while (index < lines.Length)
        {
            while (index < lines.Length &&
                   string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            // SRT-style cue number.
            if (NumericCueRegex.IsMatch(lines[index]) &&
                index + 1 < lines.Length &&
                TimeCodeRangeRegex.IsMatch(lines[index + 1].Trim()))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            var timeLine =
                lines[index].Trim();

            var match =
                TimeCodeRangeRegex.Match(
                    timeLine);

            if (!match.Success)
            {
                return FlowTimedParseResult.Failure(
                    $"Time-coded paste could not be parsed near: \"{timeLine}\"");
            }

            if (!TryParseTimeCode(
                    match.Groups["start"].Value,
                    out var startTime) ||
                !TryParseTimeCode(
                    match.Groups["end"].Value,
                    out var endTime))
            {
                return FlowTimedParseResult.Failure(
                    $"Invalid time code: \"{timeLine}\"");
            }

            index++;

            var textLines =
                new List<string>();

            while (index < lines.Length)
            {
                var candidate =
                    lines[index];

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    var lookAhead =
                        index + 1;

                    while (lookAhead < lines.Length &&
                           string.IsNullOrWhiteSpace(lines[lookAhead]))
                    {
                        lookAhead++;
                    }

                    if (lookAhead >= lines.Length)
                    {
                        index =
                            lookAhead;

                        break;
                    }

                    if (NumericCueRegex.IsMatch(lines[lookAhead]) &&
                        lookAhead + 1 < lines.Length &&
                        TimeCodeRangeRegex.IsMatch(
                            lines[lookAhead + 1].Trim()))
                    {
                        index =
                            lookAhead;

                        break;
                    }

                    if (TimeCodeRangeRegex.IsMatch(
                            lines[lookAhead].Trim()))
                    {
                        index =
                            lookAhead;

                        break;
                    }

                    // A blank line inside cue text is kept as an intentional
                    // line break. Teletext validation will reject >2 lines.
                    textLines.Add(
                        string.Empty);

                    index++;
                    continue;
                }

                if (TimeCodeRangeRegex.IsMatch(
                        candidate.Trim()))
                {
                    break;
                }

                if (NumericCueRegex.IsMatch(candidate) &&
                    index + 1 < lines.Length &&
                    TimeCodeRangeRegex.IsMatch(
                        lines[index + 1].Trim()))
                {
                    break;
                }

                textLines.Add(
                    candidate);

                index++;
            }

            var blockText =
                string.Join(
                    Environment.NewLine,
                    textLines)
                    .Trim();

            if (blockText.Length == 0)
            {
                return FlowTimedParseResult.Failure(
                    $"No subtitle text follows time code {FormatTime(startTime)}.");
            }

            blocks.Add(
                new FlowTimedTextBlock(
                    startTime,
                    endTime,
                    blockText));
        }

        if (blocks.Count == 0)
        {
            return FlowTimedParseResult.Failure(
                "Time codes were detected, but no valid time-coded subtitles were found.");
        }

        return FlowTimedParseResult.Successful(
            blocks);
    }

    private static bool TryRebalanceTimedSubtitle(
        string text,
        int maxCharactersPerLine,
        out string result)
    {
        var normalized =
            text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
                .Replace(
                    '\r',
                    '\n')
                .Trim();

        var lines =
            normalized
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

        if (lines.Length <= 2 &&
            lines.All(
                line =>
                    line.Length <=
                    maxCharactersPerLine))
        {
            result =
                string.Join(
                    Environment.NewLine,
                    lines);

            return true;
        }

        var words =
            normalized
                .Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            result =
                string.Empty;

            return false;
        }

        var bestSplit =
            -1;

        var bestDifference =
            int.MaxValue;

        for (var i = 1; i < words.Length; i++)
        {
            var first =
                string.Join(
                    " ",
                    words.Take(i));

            var second =
                string.Join(
                    " ",
                    words.Skip(i));

            if (first.Length > maxCharactersPerLine ||
                second.Length > maxCharactersPerLine)
            {
                continue;
            }

            var difference =
                Math.Abs(
                    first.Length -
                    second.Length);

            if (difference < bestDifference)
            {
                bestDifference =
                    difference;

                bestSplit =
                    i;
            }
        }

        if (bestSplit <= 0)
        {
            result =
                normalized;

            return false;
        }

        result =
            string.Join(
                " ",
                words.Take(bestSplit)) +
            Environment.NewLine +
            string.Join(
                " ",
                words.Skip(bestSplit));

        return true;
    }

    private static string NormalizeTimedSubtitleText(
        string text)
    {
        return text
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace(
                '\r',
                '\n')
            .Trim();
    }

    private static FlowPasteSpaceResult CheckAvailableSpace(
        TimeSpan insertionStart,
        TimeSpan requiredEnd,
        TimeSpan? nextExistingSubtitleStart)
    {
        if (!nextExistingSubtitleStart.HasValue)
        {
            return FlowPasteSpaceResult.Ok();
        }

        var gapMs =
            GetMinimumGapMilliseconds();

        var latestAllowedEnd =
            nextExistingSubtitleStart.Value -
            TimeSpan.FromMilliseconds(
                gapMs);

        if (requiredEnd <= latestAllowedEnd)
        {
            return FlowPasteSpaceResult.Ok();
        }

        var requiredSeconds =
            Math.Max(
                0.0,
                (requiredEnd - insertionStart)
                .TotalSeconds);

        var availableSeconds =
            Math.Max(
                0.0,
                (latestAllowedEnd - insertionStart)
                .TotalSeconds);

        return FlowPasteSpaceResult.Fail(
            string.Format(
                CultureInfo.InvariantCulture,
                "Not enough time before the next subtitle. " +
                "The pasted text requires {0:0.00} s, but only {1:0.00} s are available.",
                requiredSeconds,
                availableSeconds));
    }

    private static double CalculateDurationMilliseconds(
        string text)
    {
        var visibleCharacters =
            CountVisibleCharacters(
                text);

        var maxCps =
            Se.Settings.General
                .SubtitleMaximumCharactersPerSeconds;

        var minimumDisplayMs =
            Math.Max(
                1.0,
                Se.Settings.General
                    .SubtitleMinimumDisplayMilliseconds);

        var durationFromCpsMs =
            maxCps > 0
                ? visibleCharacters / maxCps * 1000.0
                : minimumDisplayMs;

        var durationMs =
            Math.Max(
                minimumDisplayMs,
                durationFromCpsMs);

        var maximumDisplayMs =
            Se.Settings.General
                .SubtitleMaximumDisplayMilliseconds;

        if (maximumDisplayMs > 0)
        {
            durationMs =
                Math.Min(
                    durationMs,
                    maximumDisplayMs);
        }

        return durationMs;
    }

    private static int CountVisibleCharacters(
        string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;

        foreach (var ch in text)
        {
            if (ch != '\r' &&
                ch != '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static List<string> SplitPlainTextIntoSubtitles(
        string text,
        int maxCharactersPerLine)
    {
        var normalized =
            NormalizeClipboardText(
                text);

        var words =
            normalized
                .Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(
                    word =>
                        SplitLongWord(
                            word,
                            maxCharactersPerLine))
                .ToList();

        var result =
            new List<string>();

        var lines =
            new List<string>(2);

        var currentLine =
            new StringBuilder();

        void FlushLine()
        {
            if (currentLine.Length == 0)
            {
                return;
            }

            lines.Add(
                currentLine.ToString());

            currentLine.Clear();
        }

        void FlushSubtitle()
        {
            FlushLine();

            if (lines.Count == 0)
            {
                return;
            }

            result.Add(
                string.Join(
                    Environment.NewLine,
                    lines));

            lines.Clear();
        }

        foreach (var word in words)
        {
            if (currentLine.Length == 0)
            {
                currentLine.Append(
                    word);

                continue;
            }

            if (currentLine.Length + 1 + word.Length <=
                maxCharactersPerLine)
            {
                currentLine.Append(' ');
                currentLine.Append(
                    word);

                continue;
            }

            FlushLine();

            if (lines.Count >= 2)
            {
                FlushSubtitle();
            }

            currentLine.Append(
                word);
        }

        FlushSubtitle();

        return result;
    }

    private static IEnumerable<string> SplitLongWord(
        string word,
        int maxCharactersPerLine)
    {
        if (word.Length <= maxCharactersPerLine)
        {
            yield return word;
            yield break;
        }

        var index = 0;

        while (index < word.Length)
        {
            var length =
                Math.Min(
                    maxCharactersPerLine,
                    word.Length - index);

            yield return
                word.Substring(
                    index,
                    length);

            index += length;
        }
    }

    private static bool TryParseTimeCode(
        string value,
        out TimeSpan result)
    {
        result =
            TimeSpan.Zero;

        var normalized =
            value.Trim();

        // HH:MM:SS:FF
        var frameParts =
            normalized.Split(':');

        if (frameParts.Length == 4 &&
            int.TryParse(
                frameParts[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var hours) &&
            int.TryParse(
                frameParts[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var minutes) &&
            int.TryParse(
                frameParts[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var seconds) &&
            int.TryParse(
                frameParts[3],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var frames))
        {
            var frameRate =
                Se.Settings.General
                    .CurrentFrameRate;

            if (frameRate <= 0)
            {
                frameRate =
                    Se.Settings.General
                        .DefaultFrameRate;
            }

            if (frameRate <= 0 ||
                frames < 0 ||
                frames >= Math.Ceiling(frameRate))
            {
                return false;
            }

            var totalSeconds =
                hours * 3600.0 +
                minutes * 60.0 +
                seconds +
                frames / frameRate;

            result =
                TimeSpan.FromSeconds(
                    totalSeconds);

            return true;
        }

        normalized =
            normalized.Replace(
                ',',
                '.');

        var formats =
            new[]
            {
                @"h\:mm\:ss",
                @"hh\:mm\:ss",
                @"h\:mm\:ss\.f",
                @"hh\:mm\:ss\.f",
                @"h\:mm\:ss\.ff",
                @"hh\:mm\:ss\.ff",
                @"h\:mm\:ss\.fff",
                @"hh\:mm\:ss\.fff",
            };

        return TimeSpan.TryParseExact(
            normalized,
            formats,
            CultureInfo.InvariantCulture,
            TimeSpanStyles.None,
            out result);
    }

    private static string NormalizeClipboardText(
        string text)
    {
        return (text ?? string.Empty)
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace(
                '\r',
                '\n')
            .Trim();
    }

    private static double GetMinimumGapMilliseconds()
    {
        return Math.Max(
            0.0,
            Se.Settings.General.MinimumBetweenLines
                .GetMilliseconds());
    }

    private static string FormatTime(
        TimeSpan value)
    {
        return value.ToString(
            @"hh\:mm\:ss\.fff",
            CultureInfo.InvariantCulture);
    }
}

public sealed record FlowPasteItem(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text,
    bool HasExplicitTimeCodes);

public sealed record FlowTimedTextBlock(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text);

public sealed class FlowPastePlan
{
    private FlowPastePlan(
        bool success,
        IReadOnlyList<FlowPasteItem> items,
        bool hasExplicitTimeCodes,
        string? errorMessage)
    {
        Success = success;
        Items = items;
        HasExplicitTimeCodes = hasExplicitTimeCodes;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    public IReadOnlyList<FlowPasteItem> Items { get; }

    public bool HasExplicitTimeCodes { get; }

    public string? ErrorMessage { get; }

    public static FlowPastePlan Successful(
        IReadOnlyList<FlowPasteItem> items,
        bool hasExplicitTimeCodes)
    {
        return new FlowPastePlan(
            true,
            items,
            hasExplicitTimeCodes,
            null);
    }

    public static FlowPastePlan Failure(
        string errorMessage,
        IReadOnlyList<FlowPasteItem>? items = null,
        bool hasExplicitTimeCodes = false)
    {
        return new FlowPastePlan(
            false,
            items ?? Array.Empty<FlowPasteItem>(),
            hasExplicitTimeCodes,
            errorMessage);
    }
}

public sealed record FlowPasteSpaceResult(
    bool Success,
    string? ErrorMessage)
{
    public static FlowPasteSpaceResult Ok()
    {
        return new FlowPasteSpaceResult(
            true,
            null);
    }

    public static FlowPasteSpaceResult Fail(
        string errorMessage)
    {
        return new FlowPasteSpaceResult(
            false,
            errorMessage);
    }
}

public sealed class FlowTimedParseResult
{
    private FlowTimedParseResult(
        bool hasTimeCodes,
        bool success,
        IReadOnlyList<FlowTimedTextBlock> blocks,
        string? errorMessage)
    {
        HasTimeCodes = hasTimeCodes;
        Success = success;
        Blocks = blocks;
        ErrorMessage = errorMessage;
    }

    public bool HasTimeCodes { get; }

    public bool Success { get; }

    public IReadOnlyList<FlowTimedTextBlock> Blocks { get; }

    public string? ErrorMessage { get; }

    public static FlowTimedParseResult NoTimeCodes()
    {
        return new FlowTimedParseResult(
            false,
            true,
            Array.Empty<FlowTimedTextBlock>(),
            null);
    }

    public static FlowTimedParseResult Successful(
        IReadOnlyList<FlowTimedTextBlock> blocks)
    {
        return new FlowTimedParseResult(
            true,
            true,
            blocks,
            null);
    }

    public static FlowTimedParseResult Failure(
        string errorMessage)
    {
        return new FlowTimedParseResult(
            true,
            false,
            Array.Empty<FlowTimedTextBlock>(),
            errorMessage);
    }
}
