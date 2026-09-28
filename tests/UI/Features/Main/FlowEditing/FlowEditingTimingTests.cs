using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.FlowEditing;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;

namespace UITests.Features.Main.FlowEditing;

public class FlowEditingTimingTests
{
    private static (MainViewModel Vm, FlowEditingView View) Create(params Paragraph[] paragraphs)
    {
        var services = new ServiceCollection();
        services.AddSubtitleEditServices();
        Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<MainViewModel>();
        vm.SelectedSubtitleFormat = new Ebu();
        vm.IsFormatEbu = true;
        foreach (var p in paragraphs) vm.Subtitles.Add(new SubtitleLineViewModel(p, null!));
        return (vm, new FlowEditingView(vm));
    }

    private static void Return(FlowEditingView view, SubtitleLineViewModel source, int caret)
    {
        using var item = new FlowEditingItem(source);
        var box = new TextBox { Text = item.Text };
        box.SelectionStart = box.SelectionEnd = box.CaretIndex = caret;
        typeof(FlowEditingView).GetMethod("TextBoxOnReturnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(view, [item, box, new KeyEventArgs { Key = Key.Enter }]);
    }

    [AvaloniaFact]
    public void ReturnOnOneLinePreservesTimeAndColor()
    {
        var (vm, view) = Create(new Paragraph("<font color=\"Yellow\">Si quelqu'un a déjàlevé le doigt</font>", 1000, 6000));
        Return(view, vm.Subtitles[0], "Si quelqu'un a déjà".Length);
        Assert.Single(vm.Subtitles);
        Assert.Equal("Si quelqu'un a déjà\nlevé le doigt", FlowInlineColorProjection.Parse(vm.Subtitles[0].Text).VisibleText);
        Assert.Contains("Yellow", vm.Subtitles[0].Text);
        Assert.Equal(1000, vm.Subtitles[0].StartTime.TotalMilliseconds);
        Assert.Equal(6000, vm.Subtitles[0].EndTime.TotalMilliseconds);
    }

    [AvaloniaFact]
    public void TwoLineReturnIsAtomicAndDoesNotMoveFollower()
    {
        var saved = Se.Settings.General.MinimumBetweenLines;
        Se.Settings.General.MinimumBetweenLines = new MsOrFramesValue { Milliseconds = 200, Frames = 5 };
        try
        {
            var (vm, view) = Create(new Paragraph("Bonne chance\npour tout à l'heure.", 1000, 6000), new Paragraph("Salut.", 6400, 8200));
            Return(view, vm.Subtitles[0], 13);
            Assert.Equal(3, vm.Subtitles.Count);
            Assert.Equal(200, (vm.Subtitles[1].StartTime - vm.Subtitles[0].EndTime).TotalMilliseconds);
            Assert.Equal(1000, vm.Subtitles[0].StartTime.TotalMilliseconds);
            Assert.Equal(6000, vm.Subtitles[1].EndTime.TotalMilliseconds);
            Assert.Equal(6400, vm.Subtitles[2].StartTime.TotalMilliseconds);
            Assert.Equal(8200, vm.Subtitles[2].EndTime.TotalMilliseconds);

            var (shortVm, shortView) = Create(new Paragraph("Bonne chance\npour tout à l'heure.", 1000, 1600), new Paragraph("Salut.", 2000, 4000));
            var before = shortVm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime, p.MarginV)).ToArray();
            Return(shortView, shortVm.Subtitles[0], 13);
            Assert.Equal(before, shortVm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime, p.MarginV)).ToArray());
        }
        finally { Se.Settings.General.MinimumBetweenLines = saved; }
    }

    [AvaloniaFact]
    public void ReturnReflowsIntoAdjacentSubtitlePreservingOuterTimecodes()
    {
        var saved = Se.Settings.General.MinimumBetweenLines;
        Se.Settings.General.MinimumBetweenLines = new MsOrFramesValue { Milliseconds = 200, Frames = 5 };
        try
        {
            foreach (var colored in new[] { false, true })
            {
                var first = colored ? "<font color=\"Yellow\">Bonne chance pour</font>\n<font color=\"Cyan\">tout à l'heure.</font>" : "Bonne chance pour\ntout à l'heure.";
                var second = colored ? "<font color=\"Green\">Salut.</font>" : "Salut.";
                var (vm, view) = Create(new Paragraph(first, 1000, 4000) { MarginV = "20" },
                    new Paragraph(second, 4200, 6200) { MarginV = "22" }, new Paragraph("Suite.", 6400, 8400) { MarginV = "22" });
                var times = vm.Subtitles.Select(p => (p.StartTime, p.EndTime)).ToArray();
                var last = (vm.Subtitles[2].Text, vm.Subtitles[2].MarginV);
                Return(view, vm.Subtitles[0], "Bonne chance pour\n".Length);
                Assert.Equal(3, vm.Subtitles.Count);
                Assert.Equal("Bonne chance pour", FlowInlineColorProjection.Parse(vm.Subtitles[0].Text).VisibleText);
                var next = FlowInlineColorProjection.Parse(vm.Subtitles[1].Text);
                Assert.Equal("tout à l'heure.\nSalut.", next.VisibleText);
                Assert.Equal(times[0].StartTime, vm.Subtitles[0].StartTime);
                Assert.Equal(times[1].EndTime, vm.Subtitles[1].EndTime);
                Assert.Equal(times[2], (vm.Subtitles[2].StartTime, vm.Subtitles[2].EndTime));
                Assert.NotEqual(times[0].EndTime, vm.Subtitles[0].EndTime);
                AssertReflowTiming(vm);
                Assert.Equal(last, (vm.Subtitles[2].Text, vm.Subtitles[2].MarginV));
                Assert.Equal("22", vm.Subtitles[0].MarginV);
                Assert.Equal("20", vm.Subtitles[1].MarginV);
                if (colored)
                {
                    Assert.Contains("Yellow", vm.Subtitles[0].Text);
                    Assert.Contains(next.ColorRuns, r => r.Color == "Cyan" && r.Start == 0);
                    Assert.Contains(next.ColorRuns, r => r.Color == "Green" && r.Start == "tout à l'heure.\n".Length);
                }
            }
        }
        finally { Se.Settings.General.MinimumBetweenLines = saved; }
    }

    [AvaloniaFact]
    public void InvalidAdjacentReflowRejectsWholeOperationWithoutSplitFallback()
    {
        var saved = Se.Settings.General.MinimumBetweenLines;
        Se.Settings.General.MinimumBetweenLines = new MsOrFramesValue { Milliseconds = 200, Frames = 5 };
        try
        {
            foreach (var tooShort in new[] { false, true })
            {
                var nextText = tooShort ? "Salut." : new string('a', 34) + "\n" + new string('b', 34);
                var nextEnd = tooShort ? 2400 : 8200;
                var (vm, view) = Create(new Paragraph("Bonne chance pour\ntout à l'heure.", 1000, tooShort ? 1600 : 4000),
                    new Paragraph(nextText, tooShort ? 1800 : 4200, nextEnd), new Paragraph("Suite.", 8400, 10400));
                var before = vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime, p.MarginV)).ToArray();
                Return(view, vm.Subtitles[0], "Bonne chance pour\n".Length);
                Assert.Equal(before, vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime, p.MarginV)).ToArray());
            }
        }
        finally { Se.Settings.General.MinimumBetweenLines = saved; }
    }

    [AvaloniaFact]
    public void ExactManualTimecodesUseInclusiveNeighbourCountWithoutSplitting()
    {
        var saved = Se.Settings.General.MinimumBetweenLines;
        Se.Settings.General.MinimumBetweenLines = new MsOrFramesValue { Milliseconds = 200, Frames = 5 };
        try
        {
            static double Tc(int seconds, int frames) => ((10 * 3600 + 5 * 60 + seconds) * 25 + frames) * 40.0;
            foreach (var invalidReadingDuration in new[] { false, true })
            {
                var (vm, view) = Create(
                    new Paragraph("Bonne chance\npour tout à l'heure.", Tc(16, 13), invalidReadingDuration ? Tc(17, 3) : Tc(18, 10)) { MarginV = "20" },
                    new Paragraph(invalidReadingDuration ? "Salut à tous." : "Salut.", invalidReadingDuration ? Tc(17, 7) : Tc(18, 14), invalidReadingDuration ? Tc(17, 22) : Tc(19, 14)) { MarginV = "22" },
                    new Paragraph("Suite.", Tc(20, 0), Tc(22, 0)) { MarginV = "22" });
                var before = vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime, p.MarginV)).ToArray();
                Return(view, vm.Subtitles[0], "Bonne chance\n".Length);
                if (invalidReadingDuration)
                {
                    Assert.Equal(before, vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime, p.MarginV)).ToArray());
                    continue;
                }
                Assert.Equal(3, vm.Subtitles.Count);
                Assert.Equal("Bonne chance", FlowInlineColorProjection.Parse(vm.Subtitles[0].Text).VisibleText);
                Assert.Equal("pour tout à l'heure.\nSalut.", FlowInlineColorProjection.Parse(vm.Subtitles[1].Text).VisibleText);
                Assert.Equal(before[0].StartTime, vm.Subtitles[0].StartTime);
                Assert.Equal(before[1].EndTime, vm.Subtitles[1].EndTime);
                Assert.NotEqual(before[0].EndTime, vm.Subtitles[0].EndTime);
                Assert.NotEqual(before[1].StartTime, vm.Subtitles[1].StartTime);
                AssertReflowTiming(vm);
                Assert.Equal(before[2], (vm.Subtitles[2].Text, vm.Subtitles[2].StartTime, vm.Subtitles[2].EndTime, vm.Subtitles[2].MarginV));
                Assert.Equal(5, (vm.Subtitles[1].StartTime - vm.Subtitles[0].EndTime).TotalMilliseconds / 40 + 1);
            }
        }
        finally { Se.Settings.General.MinimumBetweenLines = saved; }
    }

    private static void AssertReflowTiming(MainViewModel vm)
    {
        Assert.Equal(5, (vm.Subtitles[1].StartTime - vm.Subtitles[0].EndTime).TotalMilliseconds / 40 + 1);
        foreach (var p in vm.Subtitles.Take(2))
        {
            var duration = (p.EndTime - p.StartTime).TotalMilliseconds;
            var count = FlowInlineColorProjection.Parse(p.Text).VisibleText.Replace("\n", "").Length;
            Assert.True(duration >= Se.Settings.General.SubtitleMinimumDisplayMilliseconds);
            Assert.True(count * 1000 / duration <= Se.Settings.General.SubtitleMaximumCharactersPerSeconds);
            Assert.Equal(0, p.StartTime.TotalMilliseconds % 40);
            Assert.Equal(0, p.EndTime.TotalMilliseconds % 40);
        }
    }

    [Fact]
    public void InclusiveNeighbourRuleDoesNotRelaxNewSplitTiming()
    {
        Assert.True(FlowTimingRules.IsContiguous(400, 560, 200));
        Assert.True(FlowTimingRules.IsContiguous(400, 600, 200));
        Assert.False(FlowTimingRules.IsContiguous(400, 520, 200));
        Assert.False(FlowTimingRules.IsContiguous(400, 800, 200));
        Assert.False(FlowTimingRules.TrySplit("Bonne chance", "pour tout à l'heure.", 0, 47 * 40,
            200, 1000, 8000, 25, 25, 15, false, 18, out _, out _, out _));
    }

    [AvaloniaFact]
    public void MergeKeepsSecondEndAndFollower()
    {
        var saved = Se.Settings.General.MinimumBetweenLines;
        Se.Settings.General.MinimumBetweenLines = new MsOrFramesValue { Milliseconds = 200, Frames = 5 };
        try
        {
            var (vm, view) = Create(new Paragraph("Bonne chance", 1000, 2440), new Paragraph("Salut.", 2600, 4560), new Paragraph("Suite", 4760, 6000));
            using var item = new FlowEditingItem(vm.Subtitles[1]);
            typeof(FlowEditingView).GetMethod("MergeWithPrevious", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [item]);
            Assert.Equal(2, vm.Subtitles.Count);
            Assert.Equal(1000, vm.Subtitles[0].StartTime.TotalMilliseconds);
            Assert.Equal(4560, vm.Subtitles[0].EndTime.TotalMilliseconds);
            Assert.Equal(4760, vm.Subtitles[1].StartTime.TotalMilliseconds);
            Assert.Equal(6000, vm.Subtitles[1].EndTime.TotalMilliseconds);
        }
        finally { Se.Settings.General.MinimumBetweenLines = saved; }
    }

    [Fact]
    public void WordWithoutAlternativeBoundaryCannotSplit()
    {
        Assert.Equal(-1, FlowTimingRules.SafeSplitIndex("Salut.", 2));
        Assert.Equal(6, FlowTimingRules.SafeSplitIndex("Hello Salut.", 8));
    }

    [Fact]
    public void EverySuccessfulAllocationHonorsMinimumsAndFrames()
    {
        for (var frames = 1; frames < 100; frames++)
        {
            if (!FlowTimingRules.TrySplit("Bonne chance", "pour tout à l'heure.", 1000, 1000 + frames * 40,
                200, 1000, 8000, 25, 25, 15, false, 18, out var firstEnd, out var secondStart, out _)) continue;
            Assert.True(firstEnd - 1000 >= 880);
            Assert.True(1000 + frames * 40 - secondStart >= 880);
            Assert.Equal(200, secondStart - firstEnd);
            Assert.Equal(0, firstEnd % 40);
            Assert.Equal(0, secondStart % 40);
        }
    }
}
