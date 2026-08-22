using System;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Nikse.SubtitleEdit.Features.Main.FlowEditing;

public static partial class FlowTextParser
{
    private static readonly Regex FontColorRegex = new(
        "<font\\s+[^>]*color\\s*=\\s*[\\\"']?(?<color>#[0-9a-fA-F]{6}|#[0-9a-fA-F]{8}|[a-zA-Z]+)[\\\"']?[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FontTagRegex = new(
        "</?font\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FlowTextInfo Parse(string? text)
    {
        var source = text ?? string.Empty;
        var match = FontColorRegex.Match(source);
        var colorToken = match.Success ? match.Groups["color"].Value : null;
        var cleanText = FontTagRegex.Replace(source, string.Empty);
        return new FlowTextInfo(cleanText, colorToken, ToBrush(colorToken));
    }

    public static string ApplyEditedText(string? originalText, string editedText)
    {
        var parsed = Parse(originalText);
        if (string.IsNullOrWhiteSpace(parsed.ColorToken))
        {
            return editedText;
        }

        return $"<font color=\"{parsed.ColorToken}\">{editedText}</font>";
    }

    private static IBrush? ToBrush(string? colorToken)
    {
        if (string.IsNullOrWhiteSpace(colorToken))
        {
            return null;
        }

        try
        {
            return new SolidColorBrush(Color.Parse(colorToken));
        }
        catch
        {
            return colorToken.ToLowerInvariant() switch
            {
                "white" => Brushes.White,
                "yellow" => Brushes.Yellow,
                "cyan" => Brushes.Cyan,
                "red" => Brushes.Red,
                "green" => Brushes.Green,
                "blue" => Brushes.Blue,
                "magenta" => Brushes.Magenta,
                "black" => Brushes.Black,
                _ => null,
            };
        }
    }
}

public sealed record FlowTextInfo(string Text, string? ColorToken, IBrush? Foreground);