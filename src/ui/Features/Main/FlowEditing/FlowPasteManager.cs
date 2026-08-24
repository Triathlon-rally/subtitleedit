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
        @"^\s*(?<start>\d{1,2}:\d{2}:\d{2}(?:(?:[.,]\d{1,3})|(?::\d{2}))?)\s*(?:-->|-|–|—)\s*(?<end>\d{1,2}:\d{2}:\d{2}(?:(?:[.,]\d{1,3})|(?::\d{2}))?)\s*$",
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

        var timedBlocks =
            TryParseTimedBlocks(
                normalized);

        if (timedBlocks.Count > 0)
        {
            return BuildTimedPlan(
                timedBlocks,
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

        var items =
            new List<FlowPasteItem>();

        foreach (var block in blocks)
        {
            if (block.EndTime <= block.StartTime)
            {
                return FlowPastePlan.Failure(
                    "A pasted time code has an end time before its start time.");
            }

            var subtitleTexts =
                SplitPlainTextIntoSubtitles(
                    block.Text,
                    maxCharactersPerLine);

            // A block with explicit TC may not silently create extra subtitle
            // events because there are no corresponding extra time codes.
            if (subtitleTexts.Count != 1)
            {
                return FlowPastePlan.Failure(
                    $"A time-coded subtitle exceeds the Teletext limit " +
                    $"(max 2 lines, {maxCharactersPerLine} characters per line).");
            }

            items.Add(
                new FlowPasteItem(
                    block.StartTime,
                    block.EndTime,
                    subtitleTexts[0],
                    HasExplicitTimeCodes: true));
        }

        if (items.Count == 0)
        {
            return FlowPastePlan.Failure(
                "No valid time-coded subtitles were found.");
        }

        // When pasted into a position in an existing file, explicit time codes
        // must not begin before the chosen insertion point.
        if (items[0].StartTime < insertionStart)
        {
            return FlowPastePlan.Failure(
                "The first pasted time code starts before the insertion point.");
        }

        var requiredGapMs =
            GetMinimumGapMilliseconds();

        for (var i = 1; i < items.Count; i++)
        {
            var actualGapMs =
                (items[i].StartTime -
                 items[i - 1].EndTime)
                .TotalMilliseconds;

            if (actualGapMs < requiredGapMs)
            {
                return FlowPastePlan.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The pasted time codes contain a gap of {0:0.00} s, " +
                        "but the current minimum gap is {1:0.00} s.",
                        actualGapMs / 1000.0,
                        requiredGapMs / 1000.0));
            }
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
                hasExplicitTimeCodes: true);
        }

        return FlowPastePlan.Successful(
            items,
            hasExplicitTimeCodes: true);
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
            TimeSpan.FromMilliseconds(gapMs);

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

        var tokens =
            Tokenize(
                normalized,
                maxCharactersPerLine);

        var result =
            new List<string>();

        if (tokens.Count == 0)
        {
            return result;
        }

        var currentLines =
            new List<string>(2);

        var currentLine =
            new StringBuilder();

        void FlushLine()
        {
            if (currentLine.Length == 0)
            {
                return;
            }

            currentLines.Add(
                currentLine.ToString());

            currentLine.Clear();
        }

        void FlushSubtitle()
        {
            FlushLine();

            if (currentLines.Count == 0)
            {
                return;
            }

            result.Add(
                string.Join(
                    Environment.NewLine,
                    currentLines));

            currentLines.Clear();
        }

        foreach (var token in tokens)
        {
            if (token == FlowPasteToken.ParagraphBreak)
            {
                FlushSubtitle();
                continue;
            }

            if (token == FlowPasteToken.LineBreak)
            {
                FlushLine();

                if (currentLines.Count >= 2)
                {
                    FlushSubtitle();
                }

                continue;
            }

            var word =
                token.Value;

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

            if (currentLines.Count >= 2)
            {
                FlushSubtitle();
            }

            currentLine.Append(
                word);
        }

        FlushSubtitle();

        return result;
    }

    private static List<FlowPasteToken> Tokenize(
        string text,
        int maxCharactersPerLine)
    {
        var result =
            new List<FlowPasteToken>();

        var normalized =
            text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
                .Replace(
                    '\r',
                    '\n');

        var paragraphs =
            normalized.Split(
                "\n\n",
                StringSplitOptions.None);

        for (var p = 0; p < paragraphs.Length; p++)
        {
            var lines =
                paragraphs[p].Split(
                    '\n',
                    StringSplitOptions.None);

            for (var l = 0; l < lines.Length; l++)
            {
                var words =
                    lines[l].Split(
                        new[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);

                foreach (var word in words)
                {
                    foreach (var piece in SplitLongWord(
                                 word,
                                 maxCharactersPerLine))
                    {
                        result.Add(
                            FlowPasteToken.Word(
                                piece));
                    }
                }

                if (l < lines.Length - 1)
                {
                    result.Add(
                        FlowPasteToken.LineBreak);
                }
            }

            if (p < paragraphs.Length - 1)
            {
                result.Add(
                    FlowPasteToken.ParagraphBreak);
            }
        }

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

    private static List<FlowTimedTextBlock> TryParseTimedBlocks(
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

        var result =
            new List<FlowTimedTextBlock>();

        var foundAnyTimeCode =
            false;

        var index = 0;

        while (index < lines.Length)
        {
            var line =
                lines[index].Trim();

            if (line.Length == 0)
            {
                index++;
                continue;
            }

            var match =
                TimeCodeRangeRegex.Match(
                    line);

            if (!match.Success)
            {
                if (foundAnyTimeCode)
                {
                    // Once a timed format has started, stray non-text structure
                    // is considered malformed instead of silently switching to
                    // plain-text mode.
                    return new List<FlowTimedTextBlock>();
                }

                index++;
                continue;
            }

            foundAnyTimeCode = true;

            if (!TryParseTimeCode(
                    match.Groups["start"].Value,
                    out var startTime) ||
                !TryParseTimeCode(
                    match.Groups["end"].Value,
                    out var endTime))
            {
                return new List<FlowTimedTextBlock>();
            }

            index++;

            var textLines =
                new List<string>();

            while (index < lines.Length)
            {
                var candidate =
                    lines[index];

                if (TimeCodeRangeRegex.IsMatch(
                        candidate.Trim()))
                {
                    break;
                }

                if (candidate.Length == 0 &&
                    textLines.Count > 0)
                {
                    var nextNonEmpty =
                        index + 1;

                    while (nextNonEmpty < lines.Length &&
                           string.IsNullOrWhiteSpace(
                               lines[nextNonEmpty]))
                    {
                        nextNonEmpty++;
                    }

                    if (nextNonEmpty < lines.Length &&
                        TimeCodeRangeRegex.IsMatch(
                            lines[nextNonEmpty].Trim()))
                    {
                        index =
                            nextNonEmpty;

                        break;
                    }
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
                return new List<FlowTimedTextBlock>();
            }

            result.Add(
                new FlowTimedTextBlock(
                    startTime,
                    endTime,
                    blockText));
        }

        return foundAnyTimeCode
            ? result
            : new List<FlowTimedTextBlock>();
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

            if (frameRate <= 0)
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

public sealed record FlowPasteToken(
    string Value,
    FlowPasteTokenType Type)
{
    public static readonly FlowPasteToken LineBreak =
        new(
            string.Empty,
            FlowPasteTokenType.LineBreak);

    public static readonly FlowPasteToken ParagraphBreak =
        new(
            string.Empty,
            FlowPasteTokenType.ParagraphBreak);

    public static FlowPasteToken Word(
        string value)
    {
        return new FlowPasteToken(
            value,
            FlowPasteTokenType.Word);
    }

}

public enum FlowPasteTokenType
{
    Word,
    LineBreak,
    ParagraphBreak,
}