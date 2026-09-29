using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Tools.CheckArteErrors;

namespace UITests.Features.Tools.CheckArteErrors;

public class ArtePreviewTests
{
    private static CheckArteErrorsViewModel Create(Subtitle source, params string[] checks)
    {
        var vm = new CheckArteErrorsViewModel(new StubWindowService(), new StubFileHelper());
        foreach (var check in vm.Checks)
        {
            check.IsSelected = checks.Contains(check.Name);
        }
        // Avoid Initialize's persisted gap-setting side effect in tests.
        typeof(CheckArteErrorsViewModel).GetField("_sourceSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new Subtitle(source, generateNewId: false));
        Analyze(vm);
        return vm;
    }

    private static void Analyze(CheckArteErrorsViewModel vm) =>
        typeof(CheckArteErrorsViewModel).GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Instance,
            Type.EmptyTypes)!.Invoke(vm, null);

    [AvaloniaTheory]
    [InlineData(16, 20, 12, "out", true)]
    [InlineData(16, 5, 12, "in", true)]
    [InlineData(16, 5, 12, "none", false)]
    [InlineData(5, 5, 12, "none", true)]
    [InlineData(11, 5, 12, "in", true)]
    [InlineData(10, 5, 12, "none", true)]
    [InlineData(9, 7, 12, "none", true)]
    [InlineData(16, 5, 18, "valid", true)]
    public void MinimumDuration_UsesOneCompleteSideAndPreservesNeighbours(int beforeGap, int afterGap, int frames, string expected, bool acceptShortDurations)
    {
        using var settings = new SettingsScope("General.SubtitleMinimumDisplayMilliseconds", "General.SubtitleMaximumDisplayMilliseconds", "General.SubtitleMaximumCharactersPerSeconds");
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMinimumDisplayMilliseconds = 1000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumDisplayMilliseconds = 8000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumCharactersPerSeconds = 25;
        var start = ((10 * 3600 + 2 * 60 + 41) * 25 + 6) * 40.0;
        var end = start + frames * 40;
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("Previous", start - beforeGap * 40 - 2000, start - beforeGap * 40));
        source.Paragraphs.Add(new Paragraph("Cue", start, end));
        source.Paragraphs.Add(new Paragraph("Next", end + afterGap * 40, end + afterGap * 40 + 2000));
        source.Paragraphs.Add(new Paragraph("Later", end + 6000, end + 8000));
        var vm = Create(source, "Display duration");
        vm.AcceptShortDurations = acceptShortDurations;
        vm.ShortMinimumFrames = 18;
        vm.MinimumGapFrames = 5;
        Analyze(vm);
        var fix = vm.Fixes.SingleOrDefault(f => f.Index == 2);
        if (expected == "valid") { Assert.Null(fix); return; }
        Assert.NotNull(fix);
        Assert.Equal(expected != "none", fix.CanBeFixed);
        if (expected == "in") { Assert.Contains("TC In", fix.Reason); Assert.DoesNotContain("No safe", fix.Reason); }
        foreach (var item in vm.Fixes) item.Apply = item == fix && fix.CanBeFixed;
        vm.OkCommand.Execute(null);
        var result = vm.FixedSubtitle ?? source;
        Assert.Equal(expected == "in" ? end - 18 * 40 : start, result.Paragraphs[1].StartTime.TotalMilliseconds);
        Assert.Equal(expected == "out" ? start + 18 * 40 : end, result.Paragraphs[1].EndTime.TotalMilliseconds);
        foreach (var i in new[] { 0, 2, 3 })
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(source.Paragraphs[i]), System.Text.Json.JsonSerializer.Serialize(result.Paragraphs[i]));
        Assert.Equal(start, source.Paragraphs[1].StartTime.TotalMilliseconds);
        Assert.Equal(end, source.Paragraphs[1].EndTime.TotalMilliseconds);
        if (expected != "none" && acceptShortDurations) Assert.DoesNotContain(vm.Fixes, f => f.Index == 2);
    }

    [AvaloniaFact]
    public void MinimumDuration_StrictFollowUpUsesTcInFallback()
    {
        using var settings = new SettingsScope("General.SubtitleMinimumDisplayMilliseconds", "General.SubtitleMaximumDisplayMilliseconds", "General.SubtitleMaximumCharactersPerSeconds");
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMinimumDisplayMilliseconds = 1000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumDisplayMilliseconds = 8000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumCharactersPerSeconds = 25;
        var start = ((10 * 3600 + 2 * 60 + 41) * 25) * 40.0;
        var end = start + 18 * 40;
        var previousEnd = start - 16 * 40;
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("Previous", previousEnd - 2000, previousEnd));
        source.Paragraphs.Add(new Paragraph("Cue", start, end));
        source.Paragraphs.Add(new Paragraph("Next", end + 5 * 40, end + 5 * 40 + 2000));
        var vm = Create(source, "Display duration");
        vm.AcceptShortDurations = false;
        vm.ReadingDurationTolerancePercent = 15;
        vm.ShortMinimumFrames = 18;
        vm.MinimumGapFrames = 5;
        Analyze(vm);
        var fix = Assert.Single(vm.Fixes, item => item.Index == 2 &&
            item.FixKind == CheckArteErrorsViewModel.ArteFixKind.DisplayDuration);
        Assert.True(fix.CanBeFixed);
        Assert.Contains("TC In", fix.Reason);
        Assert.Equal("0 s 18 fr", fix.BeforePreview);
        Assert.Equal("1 s 00 fr", fix.AfterPreview);
        fix.Apply = true;
        vm.OkCommand.Execute(null);
        var result = vm.FixedSubtitle!;
        Assert.Equal(end - 25 * 40, result.Paragraphs[1].StartTime.TotalMilliseconds);
        Assert.Equal(end, result.Paragraphs[1].EndTime.TotalMilliseconds);
        Assert.Equal(9 * 40, result.Paragraphs[1].StartTime.TotalMilliseconds - result.Paragraphs[0].EndTime.TotalMilliseconds);
        foreach (var i in new[] { 0, 2 })
        {
            Assert.Equal(source.Paragraphs[i].Text, result.Paragraphs[i].Text);
            Assert.Equal(source.Paragraphs[i].StartTime.TotalMilliseconds, result.Paragraphs[i].StartTime.TotalMilliseconds);
            Assert.Equal(source.Paragraphs[i].EndTime.TotalMilliseconds, result.Paragraphs[i].EndTime.TotalMilliseconds);
        }
    }

    [AvaloniaTheory]
    [InlineData(4, true, false)]
    [InlineData(6, true, true)]
    [InlineData(100, true, true)]
    [InlineData(100, false, true)]
    public void MinimumDuration_RespectsDocumentBoundaries(int startFrame, bool hasFollower, bool canFix)
    {
        var start = startFrame * 40.0;
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("Cue", start, start + 480));
        if (hasFollower) source.Paragraphs.Add(new Paragraph("Next", start + 680, start + 2680));
        var vm = Create(source, "Display duration");
        vm.AcceptShortDurations = true;
        vm.ShortMinimumFrames = 18;
        vm.MinimumGapFrames = 5;
        Analyze(vm);
        var fix = Assert.Single(vm.Fixes, item => item.Index == 1 &&
            item.FixKind == CheckArteErrorsViewModel.ArteFixKind.DisplayDuration);
        Assert.Equal(canFix, fix.CanBeFixed);
        if (!canFix) return;
        fix.Apply = true;
        vm.OkCommand.Execute(null);
        Assert.Equal(hasFollower ? start - 240 : start, vm.FixedSubtitle!.Paragraphs[0].StartTime.TotalMilliseconds);
        Assert.Equal(hasFollower ? start + 480 : start + 720, vm.FixedSubtitle.Paragraphs[0].EndTime.TotalMilliseconds);
    }

    [AvaloniaTheory]
    [InlineData("08", false, "08")]
    [InlineData("08", true, "2D")]
    [InlineData("0F", false, "0F")]
    [InlineData("0F", true, "2F")]
    public void TargetHeader_PreservesProgrammeStartAndSupportsUndo(string language, bool sdh, string code)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var header = new Nikse.SubtitleEdit.Core.SubtitleFormats.Ebu.EbuGeneralSubtitleInformation
        {
            TimeCodeStartOfProgramme = "01000000",
            OriginalProgrammeTitle = "Programme title".PadRight(32),
        };
        var source = new Subtitle { Header = header.ToString() };
        source.Paragraphs.Add(new Paragraph("Hello", 3600000, 3602000));
        var vm = Create(source);
        vm.SelectedLanguage = vm.Languages.Single(item => item.Code == language);
        vm.IsSdh = sdh;
        Analyze(vm);
        Assert.Single(vm.Fixes);
        Assert.Equal(25.0, vm.SelectedSourceFrameRate);
        vm.OkCommand.Execute(null);
        var result = Nikse.SubtitleEdit.Core.SubtitleFormats.Ebu.ReadHeader(System.Text.Encoding.GetEncoding(850).GetBytes(vm.FixedSubtitle!.Header));
        Assert.Equal("850", result.CodePageNumber);
        Assert.Equal("STL25.01", result.DiskFormatCode);
        Assert.Equal("2", result.DisplayStandardCode);
        Assert.Equal("00", result.CharacterCodeTableNumber);
        Assert.Equal(code, result.LanguageCode);
        Assert.Equal("40", result.MaximumNumberOfDisplayableCharactersInAnyTextRow);
        Assert.Equal("23", result.MaximumNumberOfDisplayableRows);
        Assert.Equal("01000000", result.TimeCodeStartOfProgramme);
        Assert.Equal(header.OriginalProgrammeTitle, result.OriginalProgrammeTitle);
        Assert.Equal(3600000, vm.FixedSubtitle.Paragraphs[0].StartTime.TotalMilliseconds);
        Assert.Empty(vm.Fixes);
        vm.UndoCommand.Execute(null);
        Assert.Equal(source.Header, vm.FixedSubtitle.Header);
    }

    [AvaloniaFact]
    public void CorrectAndUndo_KeepDialogOpenAndPublishSnapshots()
    {
        using var settings = new SettingsScope("General.SubtitleMinimumDisplayMilliseconds", "General.SubtitleMaximumDisplayMilliseconds", "General.SubtitleMaximumCharactersPerSeconds");
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMinimumDisplayMilliseconds = 1000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumDisplayMilliseconds = 10000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumCharactersPerSeconds = 25;
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<i>" + string.Join(" ", Enumerable.Repeat("Hello world", 12)) + "</i>", 1000, 13000));
        var vm = Create(source, "Teletext line length / control codes", "Italic formatting (not allowed)");
        var published = new List<Subtitle>();
        vm.ApplyToMainSubtitle = subtitle => published.Add(subtitle);
        var window = new CheckArteErrorsWindow(vm);
        window.Show();
        try
        {
            Assert.False(vm.UndoCommand.CanExecute(null));
            vm.OkCommand.Execute(null);
            Assert.True(window.IsVisible);
            Assert.True(vm.UndoCommand.CanExecute(null));
            Assert.Single(published);
            Assert.True(published[0].Paragraphs.Count > 1);
            Assert.Empty(vm.Fixes);
            vm.OkCommand.Execute(null); // no-op must not consume an undo step
            Assert.Single(published);
            vm.UndoCommand.Execute(null);
            Assert.True(window.IsVisible);
            Assert.Equal(2, published.Count);
            Assert.Single(published[1].Paragraphs);
            Assert.Equal(source.Paragraphs[0].Text, published[1].Paragraphs[0].Text);
            Assert.Equal(1000, published[1].Paragraphs[0].StartTime.TotalMilliseconds);
            Assert.Equal(13000, published[1].Paragraphs[0].EndTime.TotalMilliseconds);
            Assert.NotEmpty(vm.Fixes);
            Assert.False(vm.UndoCommand.CanExecute(null));
            vm.CancelCommand.Execute(null);
            Assert.False(window.IsVisible);
            Assert.Equal(2, published.Count); // close must not publish again
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(2800)]
    [InlineData(3100)]
    public void GapAndOverlap_OfferOneFixAndApplyFiveFrames(double previousEnd)
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("First", 1000, previousEnd));
        source.Paragraphs.Add(new Paragraph("Next", 2960, 5000));
        var vm = Create(source, "Minimum gaps", "Overlapping display times");
        Assert.Single(vm.Fixes);
        Assert.True(vm.Fixes[0].CanBeFixed);
        vm.OkCommand.Execute(null);
        Assert.Equal(2760, vm.FixedSubtitle!.Paragraphs[0].EndTime.TotalMilliseconds);
        Assert.Equal(previousEnd, source.Paragraphs[0].EndTime.TotalMilliseconds);
    }

    [AvaloniaFact]
    public void Groups_KeepSharedSelectionAndExpansionAcrossAnalysis()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph(" <i>Hello</i> ", 1000, 4000));
        var vm = Create(source, "Italic formatting (not allowed)", "Unneeded spaces");
        Assert.Equal(2, vm.FixGroups.Count);
        var italic = vm.FixGroups.Single(g => g.Name == "Italic formatting");
        Assert.Equal("Italic formatting (1)", italic.Header);
        Assert.False(italic.IsExpanded);
        Assert.Same(vm.Fixes.Single(f => f.GroupName == italic.Name), italic.Items[0]);
        italic.InvertSelectionCommand.Execute(null);
        Assert.False(italic.Items[0].Apply);
        Assert.True(vm.FixGroups.Single(g => g.Name == "Unneeded spaces").Items[0].Apply);
        italic.SelectAllCommand.Execute(null);
        Assert.True(italic.Items[0].Apply);
        italic.Items[0].Apply = false;
        italic.IsExpanded = true;
        italic.IsExpanded = false;
        Assert.False(italic.Items[0].Apply);
        vm.FixesSelectAllCommand.Execute(null);
        Assert.True(italic.Items[0].Apply);
        italic.IsExpanded = true;
        vm.Checks.Single(c => c.Name == "Unneeded spaces").IsSelected = false;
        Assert.Single(vm.FixGroups);
        Assert.True(vm.FixGroups[0].IsExpanded);
        Assert.Equal(vm.Fixes.Count, vm.FixGroups.Sum(g => g.Items.Count));
    }

    [AvaloniaFact]
    public void ItalicCheck_RemovesOnlyItalicAndUpdatesWhenUnchecked()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<i>Hello</i>\n<font color=\"red\">world</font>", 1000, 4000));
        var vm = Create(source, "Italic formatting (not allowed)");
        Assert.Single(vm.Fixes);
        Assert.Equal("Hello\n<font color=\"red\">world</font>", vm.Fixes[0].After);
        vm.Checks.Single(c => c.Name == "Italic formatting (not allowed)").IsSelected = false;
        Assert.Empty(vm.Fixes);
        vm.Checks.Single(c => c.Name == "Italic formatting (not allowed)").IsSelected = true;
        vm.OkCommand.Execute(null);
        Assert.Equal("Hello\n<font color=\"red\">world</font>", vm.FixedSubtitle!.Paragraphs[0].Text);
        Assert.Contains("<i>", source.Paragraphs[0].Text);
    }

    [AvaloniaFact]
    public void TeletextColors_MapHexColorsToTheNearestStandardColor()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<font color=\"f02030\">Red</font> <font color=\"blue\">blue</font>", 1000, 4000));
        var vm = Create(source, "Teletext colors");
        vm.IsSdh = true;
        Analyze(vm);

        var fix = Assert.Single(vm.Fixes.Where(item => item.Reason.Contains("nearest Teletext standard color")));
        Assert.True(fix.CanBeFixed);
        Assert.Equal("<font color=\"Red\">Red</font> <font color=\"Blue\">blue</font>", fix.After);

        vm.OkCommand.Execute(null);
        Assert.Equal(fix.After, vm.FixedSubtitle!.Paragraphs[0].Text);
    }

    [AvaloniaFact]
    public void TeletextLinePosition_CorrectsInvalidBottomStartsForDoubleHeight()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph(string.Empty, 0, 200) { MarginV = "23" });
        source.Paragraphs.Add(new Paragraph("One line", 1000, 4000) { MarginV = "23" });
        source.Paragraphs.Add(new Paragraph("First line\nSecond line", 5000, 8000) { MarginV = "22" });
        var vm = Create(source, "Teletext line position");

        Assert.Collection(vm.Fixes,
            blank => Assert.Equal("22", blank.After),
            oneLine => Assert.Equal("22", oneLine.After),
            twoLines => Assert.Equal("20", twoLines.After));

        vm.OkCommand.Execute(null);
        Assert.Equal("22", vm.FixedSubtitle!.Paragraphs[0].MarginV);
        Assert.Equal("22", vm.FixedSubtitle.Paragraphs[1].MarginV);
        Assert.Equal("20", vm.FixedSubtitle.Paragraphs[2].MarginV);
    }

    [AvaloniaFact]
    public void NormalArteSubtitle_ConvertsTeletextColorsToYellow()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<font color=\"Blue\">Hello</font>", 1000, 4000));
        var vm = Create(source, "Teletext colors");

        var fix = Assert.Single(vm.Fixes);
        Assert.Equal("<font color=\"Yellow\">Hello</font>", fix.After);
        vm.OkCommand.Execute(null);
        Assert.Equal(fix.After, vm.FixedSubtitle!.Paragraphs[0].Text);
    }

    [AvaloniaFact]
    public void NormalArteSubtitle_UsesYellowConsistentlyWhenTheFileUsesColor()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<font color=\"Red\">Red</font>", 1000, 4000));
        source.Paragraphs.Add(new Paragraph("No color", 5000, 8000));
        var vm = Create(source, "Teletext colors");

        Assert.Equal("<font color=\"Yellow\">Red</font>", vm.Fixes.Single(item => item.Index == 1).After);
        Assert.Equal("<font color=\"Yellow\">No color</font>", vm.Fixes.Single(item => item.Index == 2).After);
    }

    [AvaloniaFact]
    public void NormalArteSubtitle_RemovesSdhBoxingBeforeNormalizingToYellow()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<font color=\"Red\">Colored cue</font>", 1000, 4000));
        source.Paragraphs.Add(new Paragraph("<box color=\"White\">Previously boxed SDH cue</box>", 5000, 8000));
        var vm = Create(source, "Teletext colors");

        var boxingFix = vm.Fixes.Single(item => item.Index == 2);
        Assert.Equal("<font color=\"Yellow\">Previously boxed SDH cue</font>", boxingFix.After);
        Assert.Contains("boxing is removed", boxingFix.Reason);

        vm.OkCommand.Execute(null);
        Assert.Equal(boxingFix.After, vm.FixedSubtitle!.Paragraphs[1].Text);
    }

    [AvaloniaTheory]
    [InlineData("08")]
    [InlineData("0F")]
    public void EveryNormalArteLanguage_RemovesSdhBoxing(string languageCode)
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<box color=\"White\">Boxed text</box>", 1000, 4000));
        var vm = Create(source, "Teletext colors");
        vm.SelectedLanguage = vm.Languages.Single(item => item.Code == languageCode);
        vm.IsSdh = false;
        Analyze(vm);

        var fix = Assert.Single(vm.Fixes);
        Assert.Equal("Boxed text", fix.After);
        Assert.Contains("boxing is removed", fix.Reason);
    }

    [AvaloniaFact]
    public void NonStlSource_CreatesAnArteEbuStlTargetHeader()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("Hello", 1000, 4000));
        var vm = Create(source);

        var headerFix = Assert.Single(vm.Fixes.Where(item => item.Reason.Contains("Create an ARTE EBU STL target header")));
        Assert.True(headerFix.CanBeFixed);
        vm.OkCommand.Execute(null);

        Assert.True(Nikse.SubtitleEdit.Core.SubtitleFormats.Ebu.IsStlHeader(vm.FixedSubtitle!.Header));
        Assert.Contains("STL25.01", vm.FixedSubtitle.Header);
        Assert.Equal("Hello", vm.FixedSubtitle.Paragraphs[0].Text);
    }

    [AvaloniaFact]
    public void MissingBlankSubtitle_IsCreatedAtThePreviousFullHour()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("First subtitle", (10 * 60 * 60 + 82) * 1000, (10 * 60 * 60 + 85) * 1000));
        var vm = Create(source, "ARTE blank subtitle");

        var blank = Assert.Single(vm.Fixes.Where(item => item.Reason.Contains("five-frame blank/control")));
        Assert.Contains("10:00:00:00", blank.After);
        vm.OkCommand.Execute(null);

        Assert.Equal(string.Empty, vm.FixedSubtitle!.Paragraphs[0].Text);
        Assert.Equal("22", vm.FixedSubtitle.Paragraphs[0].MarginV);
        Assert.Equal(10 * 60 * 60 * 1000, vm.FixedSubtitle.Paragraphs[0].StartTime.TotalMilliseconds);
        Assert.Equal(10 * 60 * 60 * 1000 + 200, vm.FixedSubtitle.Paragraphs[0].EndTime.TotalMilliseconds);
    }

    [AvaloniaFact]
    public void BlankSubtitle_AtStartTimeCode_WithText_IsReportedWithoutAutomaticFix()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("Must not be here", 10 * 60 * 60 * 1000, 10 * 60 * 60 * 1000 + 200));
        var vm = Create(source, "ARTE blank subtitle");

        var fix = Assert.Single(vm.Fixes, item => item.GroupName == "ARTE blank subtitle");
        Assert.False(fix.CanBeFixed);
        Assert.Contains("must be an empty five-frame blank/control subtitle", fix.Reason);
    }

    [AvaloniaFact]
    public void BlankSubtitle_WithWrongDuration_IsReported()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph(string.Empty, 10 * 60 * 60 * 1000, 10 * 60 * 60 * 1000 + 400));
        var vm = Create(source, "ARTE blank subtitle");

        var fix = Assert.Single(vm.Fixes, item => item.GroupName == "ARTE blank subtitle");
        Assert.False(fix.CanBeFixed);
        Assert.Contains("last exactly five frames", fix.Reason);
    }

    [AvaloniaFact]
    public void CorrectBlankSubtitle_ProducesNoBlankFix()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph(string.Empty, 10 * 60 * 60 * 1000, 10 * 60 * 60 * 1000 + 200));
        var vm = Create(source, "ARTE blank subtitle");

        Assert.DoesNotContain(vm.Fixes, item => item.GroupName == "ARTE blank subtitle");
    }

    [AvaloniaFact]
    public void FrameRateConversion_KeepsArteStartTimeCodeReference()
    {
        const double startTimeCodeMs = 10 * 60 * 60 * 1000;
        const double sourceFrameRate = 23.976;
        const double targetFrameRate = 25.0;

        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph(
            string.Empty,
            startTimeCodeMs,
            startTimeCodeMs + 200));

        source.Paragraphs.Add(new Paragraph(
            "First subtitle",
            startTimeCodeMs + 43_000,
            startTimeCodeMs + 45_000));

        var vm = Create(source, "ARTE blank subtitle");
        vm.SelectedSourceFrameRate = sourceFrameRate;
        Analyze(vm);
        vm.OkCommand.Execute(null);

        var result = vm.FixedSubtitle!;

        // The ARTE programme start is an absolute reference and must never move.
        Assert.Equal(startTimeCodeMs, result.Paragraphs[0].StartTime.TotalMilliseconds);

        // Only the distance from the ARTE programme start is frame-rate converted.
        var factor = (24_000.0 / 1_001.0) / targetFrameRate;

        var expectedStart =
            startTimeCodeMs + 43_000 * factor;

        Assert.Equal(
            expectedStart,
            result.Paragraphs[1].StartTime.TotalMilliseconds,
            precision: 6);
    }

    [AvaloniaFact]
    public void AutomaticSplit_KeepsTextTimingAndLaterEdits()
    {
        var source = new Subtitle();
        source.Paragraphs.Add(new Paragraph("<i>" + string.Join(" ", Enumerable.Repeat("Hello world", 12)) + "</i>", 1000, 13000));
        source.Paragraphs.Add(new Paragraph("<i>Later subtitle</i>", 14000, 16000));
        using var settings = new SettingsScope("General.SubtitleMinimumDisplayMilliseconds", "General.SubtitleMaximumDisplayMilliseconds", "General.SubtitleMaximumCharactersPerSeconds");
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMinimumDisplayMilliseconds = 1000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumDisplayMilliseconds = 10000;
        Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.SubtitleMaximumCharactersPerSeconds = 25;
        var vm = Create(source, "Teletext line length / control codes", "Italic formatting (not allowed)");
        var split = Assert.Single(vm.Fixes, f => f.Reason.Contains("method 1"));
        Assert.True(split.Apply);
        Assert.Contains("00:00:01:00 → 00:00:13:00 | 12 s 00 fr", split.BeforePreview);
        Assert.Contains("fr", split.AfterPreview);
        vm.OkCommand.Execute(null);
        var result = vm.FixedSubtitle!;
        Assert.True(result.Paragraphs.Count > 2);
        Assert.Equal("Later subtitle", result.Paragraphs[^1].Text);
        Assert.All(result.Paragraphs, p => Assert.DoesNotContain("<i>", p.Text));
        var parts = result.Paragraphs.Take(result.Paragraphs.Count - 1).ToList();
        static string Words(string text) => Regex.Replace(HtmlUtil.RemoveHtmlTags(text, true), @"\s+", " ").Trim();
        Assert.Equal(Words(source.Paragraphs[0].Text), Words(string.Join(" ", parts.Select(p => p.Text))));
        Assert.Equal(1000, parts[0].StartTime.TotalMilliseconds);
        Assert.Equal(13000, parts[^1].EndTime.TotalMilliseconds);
        Assert.All(parts, p => Assert.True(p.EndTime.TotalMilliseconds > p.StartTime.TotalMilliseconds));
        for (var i = 1; i < parts.Count; i++)
        {
            Assert.True(parts[i].StartTime.TotalMilliseconds - parts[i - 1].EndTime.TotalMilliseconds >= 200);
        }
    }
}
