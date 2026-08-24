using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main.FlowEditing;

public sealed class FlowEditingView : Border
{
    private const int LinesBefore = 4;
    private const int LinesAfter = 4;
    private const double FlowFontSizeIncrease = 2.0;

    private readonly MainViewModel _vm;
    private readonly FlowPasteManager _pasteManager = new();
    private readonly StackPanel _itemsPanel;
    private readonly ScrollViewer _scrollViewer;

    private readonly List<FlowEditingItem> _items = new();
    private readonly Dictionary<FlowEditingItem, TextBox> _textBoxes = new();
    private readonly Dictionary<FlowEditingItem, Border> _rowBorders = new();
    private readonly Dictionary<FlowEditingItem, TextBlock> _numberBlocks = new();
    private readonly HashSet<SubtitleLineViewModel> _selectedSources = new();

    private readonly INotifyCollectionChanged? _observableSubtitles;

    private SubtitleLineViewModel? _selectionAnchorSource;

    private SubtitleLineViewModel? _pendingFocusSource;
    private bool _pendingFocusAtStart;

    // Batch tools can raise many collection changes in a very short time.
    // Coalesce those notifications into one Flow rebuild instead of rebuilding
    // the complete view once for every changed subtitle.
    private bool _refreshQueued;
    private int _refreshGeneration;

    public FlowEditingView(MainViewModel vm)
    {
        _vm = vm;

        Padding = new Thickness(4);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(
            Color.FromArgb(70, 128, 128, 128));
        CornerRadius = new CornerRadius(4);
        MinHeight = 220;

        _itemsPanel = new StackPanel
        {
            Spacing = 3,
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _itemsPanel,
            VerticalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        Child = _scrollViewer;

        _vm.PropertyChanged += VmOnPropertyChanged;

        _observableSubtitles =
            _vm.Subtitles as INotifyCollectionChanged;

        if (_observableSubtitles != null)
        {
            _observableSubtitles.CollectionChanged +=
                SubtitlesOnCollectionChanged;
        }

        DetachedFromVisualTree += (_, _) => Detach();
    }

    public void Refresh()
    {
        // A direct refresh supersedes any deferred refresh already queued.
        _refreshQueued = false;
        _refreshGeneration++;

        DisposeItems();

        _itemsPanel.Children.Clear();
        _textBoxes.Clear();
        _rowBorders.Clear();
        _numberBlocks.Clear();

        var subtitles = _vm.Subtitles.ToList();

        _selectedSources.RemoveWhere(
            source => !subtitles.Contains(source));

        if (_selectedSources.Count == 0 &&
            _vm.SelectedSubtitle != null &&
            subtitles.Contains(_vm.SelectedSubtitle))
        {
            _selectedSources.Add(
                _vm.SelectedSubtitle);

            _selectionAnchorSource =
                _vm.SelectedSubtitle;
        }

        if (subtitles.Count == 0)
        {
            _itemsPanel.Children.Add(
                new TextBlock
                {
                    Text = "No subtitles",
                    Opacity = 0.65,
                    Margin = new Thickness(8),
                });

            return;
        }

        var selectedIndex =
            _vm.SelectedSubtitle == null
                ? 0
                : subtitles.IndexOf(_vm.SelectedSubtitle);

        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        for (var i = 0; i < subtitles.Count; i++)

        {
            var subtitle = subtitles[i];

            var item =
                new FlowEditingItem(subtitle);

            _items.Add(item);

            _itemsPanel.Children.Add(
                MakeRow(
                    item,
                    _selectedSources.Contains(
                        subtitle) ||
                    ReferenceEquals(
                        subtitle,
                        _vm.SelectedSubtitle)));
        }

        ApplyPendingFocus();
    }

    private Control MakeRow(
        FlowEditingItem item,
        bool isCurrent)
    {
        var number = new TextBlock
        {
            Width = 48,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 7, 8, 0),
            Opacity = isCurrent ? 1.0 : 0.65,
            FontWeight = isCurrent
                ? FontWeight.SemiBold
                : FontWeight.Normal,
            FontSize =
                Se.Settings.Appearance.SubtitleTextBoxFontSize,
        };

        number.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(FlowEditingItem.Number))
            {
                Source = item,
            });

        var textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 38,

            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),

            FocusAdorner = null,

            Padding = new Thickness(5, 3),

            FontSize =
                Se.Settings.Appearance.SubtitleTextBoxFontSize +
                FlowFontSizeIncrease,

            FontWeight = FontWeight.Normal,
        };

        textBox.Bind(
            TextBox.TextProperty,
            new Binding(nameof(FlowEditingItem.Text))
            {
                Source = item,
                Mode = BindingMode.TwoWay,
            });

        textBox.Bind(
            TextBox.ForegroundProperty,
            new Binding(nameof(FlowEditingItem.Foreground))
            {
                Source = item,
                Mode = BindingMode.OneWay,
            });

        textBox.GotFocus +=
            (_, _) =>
            {
                if (!_selectedSources.Contains(
                        item.Source))
                {
                    SelectItem(item);
                }
            };

        textBox.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => HandlePointerSelection(
                item,
                textBox,
                e),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Flow paste is handled before the TextBox inserts raw clipboard text.
        textBox.AddHandler(
            InputElement.KeyDownEvent,
            async (_, e) => await TextBoxOnPasteKeyDownAsync(item, textBox, e),
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Return must be handled before the TextBox inserts a third line.
        textBox.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => TextBoxOnReturnKeyDown(item, textBox, e),
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Arrow navigation must be handled before the TextBox moves the caret.
        textBox.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => TextBoxOnArrowKeyDown(item, textBox, e),
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Backspace merge works reliably after the TextBox key processing.
        textBox.AddHandler(
            InputElement.KeyUpEvent,
            (_, e) => TextBoxOnBackspaceKeyUp(item, textBox, e),
            Avalonia.Interactivity.RoutingStrategies.Tunnel |
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);

        if (!string.IsNullOrEmpty(
                Se.Settings.Appearance
                    .SubtitleTextBoxAndGridFontName))
        {
            textBox.FontFamily =
                new FontFamily(
                    Se.Settings.Appearance
                        .SubtitleTextBoxAndGridFontName);
        }

        var timeCode = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.5,
            Margin = new Thickness(5, 0, 0, 3),
        };

        timeCode.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(FlowEditingItem.TimeCode))
            {
                Source = item,
            });

        var content = new StackPanel();

        content.Children.Add(textBox);
        content.Children.Add(timeCode);

        var grid = new Grid
        {
            ColumnDefinitions =
                new ColumnDefinitions("Auto,*"),
        };

        grid.Children.Add(number);

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var rowBorder = new Border
        {
            Child = grid,
            Padding = new Thickness(5, 3),
            Margin = new Thickness(0, 1),
            CornerRadius = new CornerRadius(4),
            Background = GetRowBackground(isCurrent),
        };

        rowBorder.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => HandlePointerSelection(
                item,
                rowBorder,
                e),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        rowBorder.ContextMenu =
            CreateFlowContextMenu(
                item,
                textBox,
                includeTextEditingItems: false);

        textBox.ContextMenu =
            CreateFlowContextMenu(
                item,
                textBox,
                includeTextEditingItems: true);

        _textBoxes[item] = textBox;
        _rowBorders[item] = rowBorder;
        _numberBlocks[item] = number;

        return rowBorder;
    }

    private ContextMenu CreateFlowContextMenu(
        FlowEditingItem item,
        TextBox textBox,
        bool includeTextEditingItems)
    {
        var items =
            new List<object>();

        if (includeTextEditingItems)
        {
            var cutMenuItem =
                new MenuItem
                {
                    Header = "Cut",
                };

            cutMenuItem.Click +=
                (_, _) =>
                    textBox.Cut();

            var copyMenuItem =
                new MenuItem
                {
                    Header = "Copy",
                };

            copyMenuItem.Click +=
                (_, _) =>
                    textBox.Copy();

            var pasteMenuItem =
                new MenuItem
                {
                    Header = "Paste",
                };

            pasteMenuItem.Click +=
                async (_, _) =>
                {
                    SelectItem(item);

                    // At the end of the subtitle, Paste means Flow paste.
                    // Inside the subtitle, keep normal TextBox paste.
                    if (textBox.SelectionStart ==
                            textBox.SelectionEnd &&
                        textBox.CaretIndex >=
                            (textBox.Text ??
                             string.Empty).Length)
                    {
                        await PasteAfterSubtitleAsync(
                            item);
                    }
                    else
                    {
                        textBox.Paste();
                    }
                };

            items.Add(
                cutMenuItem);

            items.Add(
                copyMenuItem);

            items.Add(
                pasteMenuItem);

            items.Add(
                new Separator());
        }

        var insertAfterMenuItem =
            new MenuItem
            {
                Header = "Insert subtitle after",
            };

        insertAfterMenuItem.Click +=
            (_, _) =>
            {
                SelectItem(item);

                if (!CreateSubtitleAfter(item))
                {
                    ShowPasteWarning(
                        item,
                        "Not enough time to insert a subtitle here.");
                }
            };

        var pasteAfterMenuItem =
            new MenuItem
            {
                Header = "Paste after subtitle",
            };

        pasteAfterMenuItem.Click +=
            async (_, _) =>
            {
                SelectItem(item);

                await PasteAfterSubtitleAsync(
                    item);
            };

        items.Add(
            insertAfterMenuItem);

        items.Add(
            pasteAfterMenuItem);

        items.Add(
            new Separator());

        var deleteSelectedMenuItem =
            new MenuItem
            {
                Header =
                    _selectedSources.Count > 1
                        ? "Delete selected subtitles"
                        : "Delete subtitle",
            };

        deleteSelectedMenuItem.Click +=
            (_, _) =>
            {
                if (!_selectedSources.Contains(
                        item.Source))
                {
                    SelectItem(item);
                }

                DeleteSelectedSubtitles();
            };

        items.Add(
            deleteSelectedMenuItem);

        return new ContextMenu
        {
            ItemsSource =
                items,
        };
    }

    private void HandlePointerSelection(
        FlowEditingItem item,
        Control control,
        PointerPressedEventArgs e)
    {
        var point =
            e.GetCurrentPoint(
                control);

        var isRightClick =
            point.Properties.IsRightButtonPressed;

        if (isRightClick)
        {
            if (!_selectedSources.Contains(
                    item.Source))
            {
                SelectItem(item);
            }

            return;
        }

        var toggle =
            e.KeyModifiers.HasFlag(
                KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(
                KeyModifiers.Meta);

        var range =
            e.KeyModifiers.HasFlag(
                KeyModifiers.Shift);

        if (range)
        {
            SelectRangeTo(
                item.Source);

            // PointerPressed tunnels through both the Flow row and the TextBox.
            // Without marking the event handled, Shift/Cmd selection runs twice
            // when clicking directly in the text. On macOS this made Cmd-click
            // add and immediately remove the same subtitle again.
            e.Handled = true;

            return;
        }

        if (toggle)
        {
            ToggleSelection(
                item.Source);

            // Prevent the same Cmd/Ctrl-click from being processed a second
            // time by the nested TextBox handler.
            e.Handled = true;

            return;
        }

        SelectItem(item);
    }

    private void ToggleSelection(
        SubtitleLineViewModel source)
    {
        if (_selectedSources.Contains(source))
        {
            if (_selectedSources.Count > 1)
            {
                _selectedSources.Remove(source);
            }
        }
        else
        {
            _selectedSources.Add(source);
        }

        _selectionAnchorSource =
            source;

        _vm.SelectedSubtitle =
            source;

        UpdateSelectionVisuals();
    }

    private void SelectRangeTo(
        SubtitleLineViewModel source)
    {
        var subtitles =
            _vm.Subtitles.ToList();

        var anchor =
            _selectionAnchorSource ??
            _vm.SelectedSubtitle ??
            source;

        var anchorIndex =
            subtitles.IndexOf(anchor);

        var sourceIndex =
            subtitles.IndexOf(source);

        if (anchorIndex < 0 ||
            sourceIndex < 0)
        {
            _selectedSources.Clear();
            _selectedSources.Add(source);
        }
        else
        {
            _selectedSources.Clear();

            var start =
                Math.Min(
                    anchorIndex,
                    sourceIndex);

            var end =
                Math.Max(
                    anchorIndex,
                    sourceIndex);

            for (var i = start; i <= end; i++)
            {
                if (!subtitles[i].IsReferenceOnly)
                {
                    _selectedSources.Add(
                        subtitles[i]);
                }
            }
        }

        _vm.SelectedSubtitle =
            source;

        UpdateSelectionVisuals();
    }

    private void DeleteSelectedSubtitles()
    {
        if (_selectedSources.Count == 0)
        {
            return;
        }

        var subtitles =
            _vm.Subtitles;

        var indices =
            _selectedSources
                .Select(subtitles.IndexOf)
                .Where(index => index >= 0)
                .OrderByDescending(index => index)
                .ToList();

        if (indices.Count == 0)
        {
            return;
        }

        var firstIndex =
            indices.Min();

        foreach (var index in indices)
        {
            if (index >= 0 &&
                index < subtitles.Count &&
                !subtitles[index].IsReferenceOnly)
            {
                subtitles.RemoveAt(
                    index);
            }
        }

        RenumberSubtitles();

        _selectedSources.Clear();
        _selectionAnchorSource =
            null;

        if (subtitles.Count > 0)
        {
            var newIndex =
                Math.Min(
                    firstIndex,
                    subtitles.Count - 1);

            var selected =
                subtitles[newIndex];

            _selectedSources.Add(
                selected);

            _selectionAnchorSource =
                selected;

            _vm.SelectedSubtitle =
                selected;
        }
        else
        {
            _vm.SelectedSubtitle =
                null;
        }

        Refresh();
    }

    private static IBrush GetRowBackground(bool isCurrent)
    {
        return isCurrent
            ? new SolidColorBrush(
                Color.FromArgb(
                    78,
                    160,
                    160,
                    160))
            : Brushes.Transparent;
    }

    private async System.Threading.Tasks.Task TextBoxOnPasteKeyDownAsync(
        FlowEditingItem item,
        TextBox textBox,
        KeyEventArgs e)
    {
        var isPaste =
            e.Key == Key.V &&
            (e.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
             e.KeyModifiers.HasFlag(KeyModifiers.Control));

        if (!isPaste)
        {
            return;
        }

        // Flow owns Cmd/Ctrl+V only at the end of a subtitle. Inside the text,
        // keep the TextBox's normal paste behaviour.
        if (textBox.SelectionStart != textBox.SelectionEnd ||
            textBox.CaretIndex < (textBox.Text ?? string.Empty).Length)
        {
            return;
        }

        e.Handled = true;

        await PasteAfterSubtitleAsync(
            item);
    }

    private async System.Threading.Tasks.Task<bool> PasteAfterSubtitleAsync(
        FlowEditingItem item)
    {
        var subtitles =
            _vm.Subtitles;

        var sourceIndex =
            subtitles.IndexOf(
                item.Source);

        if (sourceIndex < 0)
        {
            return false;
        }

        var clipboard =
            TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard == null)
        {
            return false;
        }

        var clipboardText =
            await clipboard.TryGetTextAsync();

        if (string.IsNullOrWhiteSpace(
                clipboardText))
        {
            ShowPasteWarning(
                item,
                "Clipboard contains no text.");

            return false;
        }

        var parsedSource =
            FlowTextParser.Parse(
                item.Source.Text);

        // Plain text pasted into EBU STL always receives an explicit yellow
        // Teletext colour code. Therefore EBU planning uses 36 characters.
        var hasColor =
            _vm.IsFormatEbu ||
            !string.IsNullOrWhiteSpace(
                parsedSource.ColorToken);

        var gapMs =
            Math.Max(
                0.0,
                Se.Settings.General.MinimumBetweenLines
                    .GetMilliseconds());

        var insertionStart =
            item.Source.EndTime +
            TimeSpan.FromMilliseconds(
                gapMs);

        TimeSpan? nextExistingSubtitleStart =
            null;

        if (sourceIndex + 1 < subtitles.Count)
        {
            var nextSubtitle =
                subtitles[sourceIndex + 1];

            if (!nextSubtitle.IsReferenceOnly)
            {
                nextExistingSubtitleStart =
                    nextSubtitle.StartTime;
            }
        }

        var plan =
            _pasteManager.BuildPlan(
                clipboardText,
                insertionStart,
                nextExistingSubtitleStart,
                hasColor);

        if (!plan.Success)
        {
            ShowPasteWarning(
                item,
                plan.ErrorMessage ??
                "Paste not possible.");

            return false;
        }

        if (plan.Items.Count == 0)
        {
            return false;
        }

        InsertPastePlanAfter(
            item,
            plan);

        return true;
    }

    private void InsertPastePlanAfter(
        FlowEditingItem currentItem,
        FlowPastePlan plan)
    {
        var subtitles = _vm.Subtitles;
        var sourceIndex =
            subtitles.IndexOf(currentItem.Source);

        if (sourceIndex < 0)
        {
            return;
        }

        var insertIndex =
            sourceIndex + 1;

        SubtitleLineViewModel? lastInserted =
            null;

        foreach (var pasteItem in plan.Items)
        {
            var newSubtitle =
                new SubtitleLineViewModel(
                    currentItem.Source,
                    generateNewId: true)
                {
                    Text = pasteItem.Text,
                };

            if (_vm.IsFormatEbu)
            {
                // Plain text pasted into EBU STL gets an explicit yellow colour
                // code. Horizontal alignment and TT position are inherited
                // explicitly from the anchor subtitle.
                var parsedSource =
                    FlowTextParser.Parse(
                        currentItem.Source.Text);

                newSubtitle.MarginV =
                    currentItem.Source.MarginV;

                var visibleText =
                    pasteItem.Text;

                var taggedText =
                    $"<font color=\"yellow\">{visibleText}</font>";

                if (!string.IsNullOrWhiteSpace(
                        parsedSource.AlignmentToken))
                {
                    taggedText =
                        parsedSource.AlignmentToken +
                        taggedText;
                }

                newSubtitle.Text =
                    taggedText;

                var sourceLineCount =
                    GetPlainLineCount(
                        currentItem.Source.Text);

                var targetLineCount =
                    GetPlainLineCount(
                        newSubtitle.Text);

                var newRow =
                    TeletextRowHelper.GetRowKeepingBottomEdge(
                        currentItem.Source.MarginV,
                        sourceLineCount,
                        targetLineCount,
                        Configuration.Settings.SubtitleSettings
                            .EbuStlTeletextUseDoubleHeight);

                if (newRow.HasValue)
                {
                    newSubtitle.MarginV =
                        newRow.Value.ToString(
                            CultureInfo.InvariantCulture);
                }
            }

            newSubtitle.SetStartTimeOnly(
                pasteItem.StartTime);

            newSubtitle.EndTime =
                pasteItem.EndTime;

            subtitles.Insert(
                insertIndex,
                newSubtitle);

            insertIndex++;
            lastInserted =
                newSubtitle;
        }

        RenumberSubtitles();

        if (lastInserted == null)
        {
            return;
        }

        _pendingFocusSource =
            lastInserted;

        _pendingFocusAtStart =
            false;

        _vm.SelectedSubtitle =
            lastInserted;

        Refresh();

        Dispatcher.UIThread.Post(() =>
        {
            var targetItem =
                _items.FirstOrDefault(
                    x => ReferenceEquals(
                        x.Source,
                        lastInserted));

            if (targetItem != null)
            {
                FocusTextBox(
                    targetItem,
                    focusAtStart: false);
            }

            CenterSelectedSubtitleInFlow();
        });
    }

    private void ShowPasteWarning(
        FlowEditingItem item,
        string message)
    {
        if (!_textBoxes.TryGetValue(
                item,
                out var textBox))
        {
            return;
        }

        ToolTip.SetTip(
            textBox,
            message);

        ToolTip.SetIsOpen(
            textBox,
            true);

        DispatcherTimer.RunOnce(
            () =>
            {
                ToolTip.SetIsOpen(
                    textBox,
                    false);
            },
            TimeSpan.FromSeconds(3.0));
    }

    private void TextBoxOnReturnKeyDown(
        FlowEditingItem item,
        TextBox textBox,
        KeyEventArgs e)
    {
        if ((e.Key != Key.Enter && e.Key != Key.Return) ||
            e.KeyModifiers != KeyModifiers.None ||
            textBox.SelectionStart != textBox.SelectionEnd)
        {
            return;
        }

        var text = textBox.Text ?? string.Empty;

        // A one-line subtitle behaves differently depending on the caret:
        // - Return inside the text creates the second text line in the same UT.
        // - Return at the very end creates a new subtitle after the current one.
        if (GetPlainLineCount(text) < 2)
        {
            if (textBox.CaretIndex >= text.Length)
            {
                e.Handled = true;
                CreateSubtitleAfter(item);
                return;
            }

            if (_vm.IsFormatEbu)
            {
                var source = item.Source;
                var oldLineCount = GetPlainLineCount(source.Text);
                var originalMarginV = source.MarginV;

                Dispatcher.UIThread.Post(() =>
                {
                    RebalanceTeletextSubtitle(source);

                    var newLineCount =
                        GetPlainLineCount(source.Text);

                    var newRow =
                        TeletextRowHelper.GetRowKeepingBottomEdge(
                            originalMarginV,
                            oldLineCount,
                            newLineCount,
                            Configuration.Settings.SubtitleSettings
                                .EbuStlTeletextUseDoubleHeight);

                    if (newRow.HasValue)
                    {
                        source.MarginV =
                            newRow.Value.ToString(
                                CultureInfo.InvariantCulture);
                    }
                });
            }

            return;
        }

        // A Flow subtitle may never grow beyond two text lines.
        // If Return would create a third line, consume the key. Split only when
        // both sides of the caret contain visible text; otherwise Return does
        // nothing instead of creating an empty subtitle or a third line.
        e.Handled = true;
        SplitAtCaret(item, textBox);
    }

    private bool CreateSubtitleAfter(
        FlowEditingItem currentItem)
    {
        var source = currentItem.Source;

        if (source.IsReferenceOnly)
        {
            return false;
        }

        var subtitles = _vm.Subtitles;
        var sourceIndex = subtitles.IndexOf(source);

        if (sourceIndex < 0)
        {
            return false;
        }

        var gapMs =
            Math.Max(
                0.0,
                Se.Settings.General.MinimumBetweenLines
                    .GetMilliseconds());

        var startMs =
            source.EndTime.TotalMilliseconds +
            gapMs;

        // If there is already a following subtitle, never silently push it.
        // For now we only create the new empty UT when there is enough room.
        if (sourceIndex + 1 < subtitles.Count)
        {
            var next = subtitles[sourceIndex + 1];

            if (!next.IsReferenceOnly &&
                startMs >= next.StartTime.TotalMilliseconds)
            {
                return false;
            }
        }

        var newSubtitle =
            new SubtitleLineViewModel(
                source,
                generateNewId: true)
            {
                Text = string.Empty,
            };

        if (_vm.IsFormatEbu)
        {
            var parsedSource =
                FlowTextParser.Parse(
                    source.Text);

            newSubtitle.MarginV =
                source.MarginV;

            var taggedEmptyText =
                "<font color=\"yellow\"></font>";

            if (!string.IsNullOrWhiteSpace(
                    parsedSource.AlignmentToken))
            {
                taggedEmptyText =
                    parsedSource.AlignmentToken +
                    taggedEmptyText;
            }

            newSubtitle.Text =
                taggedEmptyText;
        }

        var defaultDurationMs =
            Math.Max(
                1.0,
                Se.Settings.General.NewEmptyDefaultMs);

        var endMs =
            startMs + defaultDurationMs;

        if (sourceIndex + 1 < subtitles.Count)
        {
            var next = subtitles[sourceIndex + 1];

            if (!next.IsReferenceOnly)
            {
                var latestEndMs =
                    next.StartTime.TotalMilliseconds -
                    gapMs;

                endMs =
                    Math.Min(
                        endMs,
                        latestEndMs);
            }
        }

        if (endMs <= startMs)
        {
            return false;
        }

        newSubtitle.SetStartTimeOnly(
            TimeSpan.FromMilliseconds(startMs));

        newSubtitle.EndTime =
            TimeSpan.FromMilliseconds(endMs);

        if (_vm.IsFormatEbu)
        {
            var oneLineRow =
                TeletextRowHelper.GetRowKeepingBottomEdge(
                    source.MarginV,
                    GetPlainLineCount(source.Text),
                    1,
                    Configuration.Settings.SubtitleSettings
                        .EbuStlTeletextUseDoubleHeight);

            if (oneLineRow.HasValue)
            {
                newSubtitle.MarginV =
                    oneLineRow.Value.ToString(
                        CultureInfo.InvariantCulture);
            }
            else
            {
                newSubtitle.MarginV =
                    source.MarginV;
            }
        }

        subtitles.Insert(
            sourceIndex + 1,
            newSubtitle);

        RenumberSubtitles();

        _pendingFocusSource =
            newSubtitle;

        _pendingFocusAtStart =
            true;

        _vm.SelectedSubtitle =
            newSubtitle;

        Refresh();

        Dispatcher.UIThread.Post(() =>
        {
            var targetItem =
                _items.FirstOrDefault(
                    x => ReferenceEquals(
                        x.Source,
                        newSubtitle));

            if (targetItem != null)
            {
                FocusTextBox(
                    targetItem,
                    focusAtStart: true);
            }

            CenterSelectedSubtitleInFlow();
        });

        return true;
    }

    private bool SplitAtCaret(
        FlowEditingItem currentItem,
        TextBox textBox)
    {
        var source = currentItem.Source;

        if (source.IsReferenceOnly)
        {
            return false;
        }

        var subtitles = _vm.Subtitles;
        var sourceIndex = subtitles.IndexOf(source);

        if (sourceIndex < 0)
        {
            return false;
        }

        var text = textBox.Text ?? string.Empty;
        var caretIndex = textBox.CaretIndex;

        // Do not create an empty subtitle before or after the current one.
        if (caretIndex <= 0 || caretIndex >= text.Length)
        {
            return false;
        }

        var visibleBefore = text[..caretIndex].Trim();
        var visibleAfter = text[caretIndex..].Trim();

        if (visibleBefore.Length == 0 ||
            visibleAfter.Length == 0)
        {
            return false;
        }

        var originalStartMs = source.StartTime.TotalMilliseconds;
        var originalEndMs = source.EndTime.TotalMilliseconds;

        // Keep the hidden EBU/HTML colour tag when synchronizing the visible
        // Flow text back to the source subtitle.
        var sourceText =
            FlowTextParser.ApplyEditedText(
                source.Text,
                text);

        source.Text = sourceText;

        // The Flow caret index belongs to the visible text (font tags stripped),
        // while SplitManager expects an index in Source.Text. After
        // ApplyEditedText the visible text occurs as one contiguous substring,
        // so translate the caret into the tagged source string.
        var visibleTextStart =
            sourceText.IndexOf(
                text,
                StringComparison.Ordinal);

        if (visibleTextStart < 0)
        {
            return false;
        }

        var sourceCaretIndex =
            visibleTextStart + caretIndex;

        var originalMarginV = source.MarginV;
        var originalLineCount = GetPlainLineCount(source.Text);

        // Beta 23's SplitManager already provides the behaviour we need here:
        // proportional timing based on text length, configured minimum gap,
        // tag handling and automatic breaking of overlong split halves.
        var splitManager = new SplitManager();
        splitManager.Split(
            subtitles,
            source,
            sourceCaretIndex,
            string.Empty);

        if (sourceIndex + 1 >= subtitles.Count)
        {
            return false;
        }

        var newSubtitle = subtitles[sourceIndex + 1];

        if (ReferenceEquals(newSubtitle, source))
        {
            return false;
        }

        if (_vm.IsFormatEbu)
        {
            // Both halves must obey the same Teletext rule set as the normal
            // EBU editor: max two lines, 37 chars without colour and 36 with
            // colour. Rebalance before timing so CPS uses the final text.
            RebalanceTeletextSubtitle(source);
            RebalanceTeletextSubtitle(newSubtitle);
        }

        RedistributeSplitTiming(
            source,
            newSubtitle,
            originalStartMs,
            originalEndMs);

        RenumberSubtitles();

        if (_vm.IsFormatEbu)
        {
            AdjustTeletextRowAfterSplit(
                source,
                originalMarginV,
                originalLineCount);

            AdjustTeletextRowAfterSplit(
                newSubtitle,
                originalMarginV,
                originalLineCount);
        }

        _pendingFocusSource = newSubtitle;
        _pendingFocusAtStart = true;

        _vm.SelectedSubtitle = newSubtitle;

        Refresh();

        Dispatcher.UIThread.Post(() =>
        {
            var targetItem =
                _items.FirstOrDefault(
                    x => ReferenceEquals(
                        x.Source,
                        newSubtitle));

            if (targetItem != null)
            {
                FocusTextBox(
                    targetItem,
                    focusAtStart: true);
            }

            CenterSelectedSubtitleInFlow();
        });

        return true;
    }

    private static bool IsValidTeletextVisibleText(
        string text,
        int maxCharacters)
    {
        var normalized =
            text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
                .Replace(
                    '\r',
                    '\n');

        var lines =
            normalized
                .Split('\n')
                .Where(line => line.Length > 0)
                .ToArray();

        return
            lines.Length >= 1 &&
            lines.Length <= 2 &&
            lines.All(
                line => line.Length <= maxCharacters);
    }

    private static void RebalanceTeletextSubtitle(
        SubtitleLineViewModel subtitle)
    {
        var parsed =
            FlowTextParser.Parse(
                subtitle.Text);

        var maxCharacters =
            string.IsNullOrWhiteSpace(
                parsed.ColorToken)
                ? 37
                : 36;

        var rebalanced =
            RebalanceTeletextVisibleText(
                parsed.Text,
                maxCharacters);

        if (rebalanced == parsed.Text)
        {
            return;
        }

        subtitle.Text =
            FlowTextParser.ApplyEditedText(
                subtitle.Text,
                rebalanced);
    }

    private static string RebalanceTeletextVisibleText(
        string text,
        int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            return text;
        }

        var normalized =
            text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
                .Replace(
                    '\r',
                    '\n');

        var existingLines =
            normalized
                .Split('\n')
                .Select(line => line.Trim())
                .ToList();

        // A split may leave a leading/trailing empty line (for example when
        // the caret was directly beside an existing line break). Teletext must
        // count only real text rows, otherwise a one-line result incorrectly
        // remains on the two-line TT position.
        while (existingLines.Count > 0 &&
               existingLines[0].Length == 0)
        {
            existingLines.RemoveAt(0);
        }

        while (existingLines.Count > 0 &&
               existingLines[^1].Length == 0)
        {
            existingLines.RemoveAt(
                existingLines.Count - 1);
        }

        normalized =
            string.Join(
                Environment.NewLine,
                existingLines);

        if (existingLines.Count <= 2 &&
            existingLines.All(
                line => line.Length <= maxCharacters))
        {
            return normalized;
        }

        // Rebalance only the visible text. Colour tags are restored afterwards
        // by FlowTextParser.ApplyEditedText.
        var words =
            normalized
                .Split(
                    new[] { ' ', '\t', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return string.Empty;
        }

        var flattened =
            string.Join(
                " ",
                words);

        if (flattened.Length <= maxCharacters)
        {
            return flattened;
        }

        // Find the best word boundary that keeps both lines within the current
        // Teletext width (37 without colour, 36 with colour). Prefer the most
        // balanced split.
        var bestSplit = -1;
        var bestDifference = int.MaxValue;

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

            if (first.Length > maxCharacters ||
                second.Length > maxCharacters)
            {
                continue;
            }

            var difference =
                Math.Abs(
                    first.Length -
                    second.Length);

            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestSplit = i;
            }
        }

        if (bestSplit > 0)
        {
            return
                string.Join(
                    " ",
                    words.Take(bestSplit)) +
                Environment.NewLine +
                string.Join(
                    " ",
                    words.Skip(bestSplit));
        }

        // If no word-boundary split can satisfy the limit, keep a strict
        // two-line Teletext result by using the last possible boundary in the
        // first maxCharacters characters. This only splits a word when there
        // is no legal word-boundary alternative.
        if (flattened.Length <= maxCharacters * 2)
        {
            var splitIndex =
                Math.Min(
                    maxCharacters,
                    flattened.Length);

            var preferredSpace =
                flattened.LastIndexOf(
                    ' ',
                    splitIndex - 1,
                    splitIndex);

            if (preferredSpace > 0)
            {
                splitIndex =
                    preferredSpace;
            }

            var first =
                flattened[..splitIndex]
                    .TrimEnd();

            var second =
                flattened[splitIndex..]
                    .TrimStart();

            if (first.Length <= maxCharacters &&
                second.Length <= maxCharacters)
            {
                return
                    first +
                    Environment.NewLine +
                    second;
            }

            // Absolute fallback for a single overlong word.
            first =
                flattened[..maxCharacters];

            second =
                flattened[maxCharacters..];

            if (second.Length <= maxCharacters)
            {
                return
                    first +
                    Environment.NewLine +
                    second;
            }
        }

        // More than two legal Teletext lines are required. Auto-flow will later
        // turn this into additional subtitles; until then do not silently lose
        // or truncate text.
        return normalized;
    }

    private static void RedistributeSplitTiming(
        SubtitleLineViewModel first,
        SubtitleLineViewModel second,
        double originalStartMs,
        double originalEndMs)
    {
        var totalDurationMs =
            Math.Max(
                2.0,
                originalEndMs - originalStartMs);

        var configuredGapMs =
            Math.Max(
                0.0,
                Se.Settings.General.MinimumBetweenLines
                    .GetMilliseconds());

        // Never let the gap consume the entire original subtitle window.
        var gapMs =
            Math.Min(
                configuredGapMs,
                Math.Max(0.0, totalDurationMs - 2.0));

        var availableMs =
            Math.Max(
                2.0,
                totalDurationMs - gapMs);

        var firstCharacters =
            Math.Max(
                1,
                CountCharactersWithoutLineBreaks(
                    FlowTextParser.Parse(first.Text).Text));

        var secondCharacters =
            Math.Max(
                1,
                CountCharactersWithoutLineBreaks(
                    FlowTextParser.Parse(second.Text).Text));

        var totalCharacters =
            firstCharacters + secondCharacters;

        var maxCps =
            Se.Settings.General
                .SubtitleMaximumCharactersPerSeconds;

        var minimumDisplayMs =
            Math.Max(
                1.0,
                Se.Settings.General
                    .SubtitleMinimumDisplayMilliseconds);

        var firstCpsMinimumMs =
            maxCps > 0
                ? firstCharacters / maxCps * 1000.0
                : 0.0;

        var secondCpsMinimumMs =
            maxCps > 0
                ? secondCharacters / maxCps * 1000.0
                : 0.0;

        var firstMinimumMs =
            Math.Max(
                minimumDisplayMs,
                firstCpsMinimumMs);

        var secondMinimumMs =
            Math.Max(
                minimumDisplayMs,
                secondCpsMinimumMs);

        double firstDurationMs;
        double secondDurationMs;

        if (firstMinimumMs + secondMinimumMs <= availableMs)
        {
            // Start with a text-proportional split of the original usable time.
            firstDurationMs =
                availableMs *
                firstCharacters /
                totalCharacters;

            secondDurationMs =
                availableMs - firstDurationMs;

            // Respect CPS and minimum display duration for both halves.
            if (firstDurationMs < firstMinimumMs)
            {
                firstDurationMs = firstMinimumMs;
                secondDurationMs =
                    availableMs - firstDurationMs;
            }

            if (secondDurationMs < secondMinimumMs)
            {
                secondDurationMs = secondMinimumMs;
                firstDurationMs =
                    availableMs - secondDurationMs;
            }
        }
        else
        {
            // The original subtitle simply has too little time to satisfy both
            // configured minima. Keep the original time window and share the
            // usable duration in proportion to the two required minima.
            var requiredTotalMs =
                firstMinimumMs + secondMinimumMs;

            firstDurationMs =
                availableMs *
                firstMinimumMs /
                requiredTotalMs;

            secondDurationMs =
                availableMs - firstDurationMs;
        }

        firstDurationMs =
            Math.Max(
                1.0,
                firstDurationMs);

        secondDurationMs =
            Math.Max(
                1.0,
                secondDurationMs);

        // Preserve the original outer time codes exactly and put the configured
        // minimum gap between the two new subtitles.
        var firstEndMs =
            originalStartMs + firstDurationMs;

        var secondStartMs =
            firstEndMs + gapMs;

        // Guard against rounding or an impossible very-short source subtitle.
        if (secondStartMs >= originalEndMs)
        {
            secondStartMs =
                Math.Max(
                    originalStartMs + 1.0,
                    originalEndMs - 1.0);

            firstEndMs =
                Math.Max(
                    originalStartMs + 1.0,
                    secondStartMs - gapMs);
        }

        first.EndTime =
            TimeSpan.FromMilliseconds(firstEndMs);

        second.SetStartTimeOnly(
            TimeSpan.FromMilliseconds(secondStartMs));

        second.EndTime =
            TimeSpan.FromMilliseconds(originalEndMs);
    }

    private void RenumberSubtitles()
    {
        var number = 1;

        foreach (var subtitle in _vm.Subtitles)
        {
            if (subtitle.IsReferenceOnly)
            {
                continue;
            }

            subtitle.Number = number;
            number++;
        }
    }

    private static void AdjustTeletextRowAfterSplit(
        SubtitleLineViewModel subtitle,
        string originalMarginV,
        int originalLineCount)
    {
        var newLineCount =
            GetPlainLineCount(subtitle.Text);

        var newRow =
            TeletextRowHelper.GetRowKeepingBottomEdge(
                originalMarginV,
                originalLineCount,
                newLineCount,
                Configuration.Settings.SubtitleSettings
                    .EbuStlTeletextUseDoubleHeight);

        if (newRow.HasValue)
        {
            subtitle.MarginV =
                newRow.Value.ToString(
                    CultureInfo.InvariantCulture);
        }
    }

        private void TextBoxOnArrowKeyDown(
        FlowEditingItem item,
        TextBox textBox,
        KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (textBox.SelectionStart != textBox.SelectionEnd)
        {
            return;
        }

        var text = textBox.Text ?? string.Empty;
        var caretIndex = textBox.CaretIndex;

        if (e.Key == Key.Left && caretIndex == 0)
        {
            if (MoveToAdjacentSubtitle(item, direction: -1, focusAtStart: false))
            {
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Right && caretIndex >= text.Length)
        {
            if (MoveToAdjacentSubtitle(item, direction: 1, focusAtStart: true))
            {
                e.Handled = true;
            }
        }
    }

    private void TextBoxOnBackspaceKeyUp(
        FlowEditingItem item,
        TextBox textBox,
        KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None ||
            textBox.SelectionStart != textBox.SelectionEnd)
        {
            return;
        }

        var isBackspace =
            e.Key == Key.Back ||
            (OperatingSystem.IsMacOS() && e.Key == Key.Delete);

        if (isBackspace && textBox.CaretIndex == 0)
        {
            if (MergeWithPrevious(item))
            {
                e.Handled = true;
            }
        }
    }

    private bool MergeWithPrevious(
        FlowEditingItem currentItem)
    {
        var subtitles = _vm.Subtitles.ToList();

        var currentIndex =
            subtitles.IndexOf(currentItem.Source);

        if (currentIndex <= 0)
        {
            return false;
        }

        var previous =
            subtitles[currentIndex - 1];

        if (previous.IsReferenceOnly ||
            currentItem.Source.IsReferenceOnly)
        {
            return false;
        }

        var actualGapMs =
            currentItem.Source.StartTime.TotalMilliseconds -
            previous.EndTime.TotalMilliseconds;

        var allowedGapMs =
            Math.Max(
                0.0,
                Se.Settings.General.MinimumBetweenLines
                    .GetMilliseconds());

        // Flow Backspace removes a normal subtitle boundary. It must not absorb
        // a deliberate larger timing gap into one long subtitle duration.
        if (actualGapMs > allowedGapMs + 0.5)
        {
            ShowMergeGapWarning(
                currentItem,
                actualGapMs,
                allowedGapMs);

            // Return true so the Backspace key is consumed even though no merge
            // took place. Both subtitles and their timing stay untouched.
            return true;
        }

        var previousParsed =
            FlowTextParser.Parse(previous.Text);

        var currentParsed =
            FlowTextParser.Parse(currentItem.Source.Text);

        var previousVisibleText =
            previousParsed.Text;

        if (_vm.IsFormatEbu)
        {
            var mergedVisibleText =
                (previousParsed.Text.TrimEnd() + " " +
                 currentParsed.Text.TrimStart()).Trim();

            var hasColor =
                !string.IsNullOrWhiteSpace(previousParsed.ColorToken) ||
                !string.IsNullOrWhiteSpace(currentParsed.ColorToken);

            var maxCharacters =
                hasColor ? 36 : 37;

            var rebalanced =
                RebalanceTeletextVisibleText(
                    mergedVisibleText,
                    maxCharacters);

            if (!IsValidTeletextVisibleText(
                    rebalanced,
                    maxCharacters))
            {
                ShowMergeTextTooLongWarning(
                    currentItem,
                    maxCharacters);

                return true;
            }
        }

        var visibleCharactersBeforeJoin =
            CountCharactersWithoutLineBreaks(
                previousVisibleText);

        var oldLineCount =
            GetPlainLineCount(previous.Text);

        var originalMarginV =
            previous.MarginV;

         // Make sure the Flow row is also the selected subtitle in MainViewModel.
        // The normal SE merge command operates on SelectedSubtitle.
        if (!ReferenceEquals(
                _vm.SelectedSubtitle,
                currentItem.Source))
        {
            _vm.SelectedSubtitle =
                currentItem.Source;
        }

        // Re-use Subtitle Edit's existing merge logic for timing,
        // removal of the second subtitle, renumbering and selection.
        _vm.MergeWithLineBeforeCommand.Execute(null);

        if (!_vm.Subtitles.Contains(previous))
        {
            return false;
        }

        if (_vm.IsFormatEbu)
        {
            var newLineCount =
                GetPlainLineCount(previous.Text);

            var newRow =
                TeletextRowHelper.GetRowKeepingBottomEdge(
                    originalMarginV,
                    oldLineCount,
                    newLineCount,
                    Configuration.Settings.SubtitleSettings
                        .EbuStlTeletextUseDoubleHeight);

            if (newRow.HasValue)
            {
                previous.MarginV =
                    newRow.Value.ToString(
                        CultureInfo.InvariantCulture);
            }
        }

        _pendingFocusSource = previous;
        _pendingFocusAtStart = false;

        Dispatcher.UIThread.Post(() =>
        {
            var targetItem =
                _items.FirstOrDefault(
                    x => ReferenceEquals(
                        x.Source,
                        previous));

            if (targetItem == null ||
                !_textBoxes.TryGetValue(
                    targetItem,
                    out var mergedTextBox))
            {
                Refresh();

                Dispatcher.UIThread.Post(() =>
                    FocusMergedTextAtJoin(
                        previous,
                        visibleCharactersBeforeJoin));

                return;
            }

            FocusMergedTextAtJoin(
                previous,
                visibleCharactersBeforeJoin);
        });

        return true;
    }

    private void ShowMergeTextTooLongWarning(
        FlowEditingItem item,
        int maxCharacters)
    {
        if (!_textBoxes.TryGetValue(
                item,
                out var textBox))
        {
            return;
        }

        var message =
            string.Format(
                CultureInfo.InvariantCulture,
                "Merge not possible – too many characters (max {0} per line)",
                maxCharacters);

        ToolTip.SetTip(
            textBox,
            message);

        ToolTip.SetIsOpen(
            textBox,
            true);

        DispatcherTimer.RunOnce(
            () =>
            {
                ToolTip.SetIsOpen(
                    textBox,
                    false);
            },
            TimeSpan.FromSeconds(2.5));
    }

    private void ShowMergeGapWarning(
        FlowEditingItem item,
        double actualGapMs,
        double allowedGapMs)
    {
        if (!_textBoxes.TryGetValue(
                item,
                out var textBox))
        {
            return;
        }

        var message =
            string.Format(
                CultureInfo.InvariantCulture,
                "Gap too large to merge ({0:0.00} s; max {1:0.00} s)",
                actualGapMs / 1000.0,
                allowedGapMs / 1000.0);

        ToolTip.SetTip(
            textBox,
            message);

        ToolTip.SetIsOpen(
            textBox,
            true);

        DispatcherTimer.RunOnce(
            () =>
            {
                ToolTip.SetIsOpen(
                    textBox,
                    false);
            },
            TimeSpan.FromSeconds(2.5));
    }

    private void FocusMergedTextAtJoin(
        SubtitleLineViewModel source,
        int visibleCharactersBeforeJoin)
    {
        var targetItem =
            _items.FirstOrDefault(
                x => ReferenceEquals(
                    x.Source,
                    source));

        if (targetItem == null ||
            !_textBoxes.TryGetValue(
                targetItem,
                out var textBox))
        {
            return;
        }

        textBox.Focus();

        var text =
            textBox.Text ?? string.Empty;

        var caretIndex =
            GetCaretIndexForVisibleCharacterCount(
                text,
                visibleCharactersBeforeJoin);

        textBox.CaretIndex = caretIndex;
        textBox.SelectionStart = caretIndex;
        textBox.SelectionEnd = caretIndex;
    }

    private static int CountCharactersWithoutLineBreaks(
        string text)
    {
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

    private static int GetCaretIndexForVisibleCharacterCount(
        string text,
        int visibleCharacterCount)
    {
        if (visibleCharacterCount <= 0)
        {
            return 0;
        }

        var count = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' &&
                text[i] != '\n')
            {
                count++;
            }

            if (count >= visibleCharacterCount)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    private static int GetPlainLineCount(
        string? sourceText)
    {
        var text =
            FlowTextParser.Parse(
                sourceText ?? string.Empty).Text;

        text =
            text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);

        if (text.Length == 0)
        {
            return 1;
        }

        return text.Split('\n').Length;
    }

    private bool MoveToAdjacentSubtitle(
        FlowEditingItem currentItem,
        int direction,
        bool focusAtStart)
    {
        var subtitles = _vm.Subtitles.ToList();

        var currentIndex =
            subtitles.IndexOf(currentItem.Source);

        if (currentIndex < 0)
        {
            return false;
        }

        var targetIndex =
            currentIndex + direction;

        if (targetIndex < 0 ||
            targetIndex >= subtitles.Count)
        {
            return false;
        }

        var targetSource =
            subtitles[targetIndex];

        var targetItem =
            _items.FirstOrDefault(
                item => ReferenceEquals(
                    item.Source,
                    targetSource));

        if (targetItem != null)
        {
            _vm.SelectedSubtitle = targetSource;

            UpdateSelectionVisuals();

            FocusTextBox(
                targetItem,
                focusAtStart);

            return true;
        }

        _pendingFocusSource = targetSource;
        _pendingFocusAtStart = focusAtStart;

        _vm.SelectedSubtitle = targetSource;

        return true;
    }

    private void FocusTextBox(
        FlowEditingItem item,
        bool focusAtStart)
    {
        if (!_textBoxes.TryGetValue(
                item,
                out var textBox))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();

            var textLength =
                (textBox.Text ?? string.Empty).Length;

            var caretIndex =
                focusAtStart
                    ? 0
                    : textLength;

            textBox.CaretIndex = caretIndex;
            textBox.SelectionStart = caretIndex;
            textBox.SelectionEnd = caretIndex;
        });
    }

    private void ApplyPendingFocus()
    {
        if (_pendingFocusSource == null)
        {
            return;
        }

        var targetItem =
            _items.FirstOrDefault(
                item => ReferenceEquals(
                    item.Source,
                    _pendingFocusSource));

        if (targetItem == null)
        {
            return;
        }

        var focusAtStart =
            _pendingFocusAtStart;

        _pendingFocusSource = null;

        FocusTextBox(
            targetItem,
            focusAtStart);
    }

    private void SelectItem(
        FlowEditingItem item)
    {
        _selectedSources.Clear();

        _selectedSources.Add(
            item.Source);

        _selectionAnchorSource =
            item.Source;

        if (!ReferenceEquals(
                _vm.SelectedSubtitle,
                item.Source))
        {
            _vm.SelectedSubtitle =
                item.Source;
        }

        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var item in _items)
        {
            var isCurrent =
                _selectedSources.Contains(
                    item.Source);

            if (_rowBorders.TryGetValue(
                    item,
                    out var border))
            {
                border.Background =
                    GetRowBackground(isCurrent);
            }

            if (_numberBlocks.TryGetValue(
                    item,
                    out var number))
            {
                number.Opacity =
                    isCurrent ? 1.0 : 0.65;

                number.FontWeight =
                    isCurrent
                        ? FontWeight.SemiBold
                        : FontWeight.Normal;
            }
        }
    }

    private bool IsSubtitleCurrentlyVisible(
        SubtitleLineViewModel? subtitle)
    {
        if (subtitle == null)
        {
            return false;
        }

        return _items.Any(
            item => ReferenceEquals(
                item.Source,
                subtitle));
    }

      private void VmOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.SelectedSubtitle) &&
            e.PropertyName != nameof(MainViewModel.SelectedSubtitleIndex))
        {
            return;
        }

        // A click in the main subtitle grid can update SelectedSubtitleIndex
        // before/without the Flow view seeing SelectedSubtitle. Synchronize
        // the selected object explicitly from the grid index.
        if (e.PropertyName == nameof(MainViewModel.SelectedSubtitleIndex) &&
            _vm.SelectedSubtitleIndex.HasValue)
        {
            var index = _vm.SelectedSubtitleIndex.Value;

            if (index >= 0 &&
                index < _vm.Subtitles.Count)
            {
                var selected = _vm.Subtitles[index];

                if (!ReferenceEquals(
                        _vm.SelectedSubtitle,
                        selected))
                {
                    _vm.SelectedSubtitle = selected;
                    return;
                }
            }
        }

        if (IsSubtitleCurrentlyVisible(
                _vm.SelectedSubtitle))
        {
            if (_vm.SelectedSubtitle != null &&
                !_selectedSources.Contains(
                    _vm.SelectedSubtitle))
            {
                _selectedSources.Clear();

                _selectedSources.Add(
                    _vm.SelectedSubtitle);

                _selectionAnchorSource =
                    _vm.SelectedSubtitle;
            }

            UpdateSelectionVisuals();
            ApplyPendingFocus();
            CenterSelectedSubtitleInFlow();
            return;
        }

        Refresh();

        Dispatcher.UIThread.Post(
            CenterSelectedSubtitleInFlow);
    }
    private void CenterSelectedSubtitleInFlow()
    {
        var selected =
            _vm.SelectedSubtitle;

        if (selected == null)
        {
            return;
        }

        var item =
            _items.FirstOrDefault(
                x => ReferenceEquals(
                    x.Source,
                    selected));

        if (item == null ||
            !_rowBorders.TryGetValue(
                item,
                out var border))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var point =
                border.TranslatePoint(
                    new Point(0, 0),
                    _itemsPanel);

            if (!point.HasValue)
            {
                return;
            }

            var targetOffset =
                point.Value.Y +
                border.Bounds.Height / 2 -
                _scrollViewer.Viewport.Height / 2;

            var maxOffset =
                Math.Max(
                    0,
                    _scrollViewer.Extent.Height -
                    _scrollViewer.Viewport.Height);

            targetOffset =
                Math.Max(
                    0,
                    Math.Min(
                        targetOffset,
                        maxOffset));

            _scrollViewer.Offset =
                new Vector(
                    _scrollViewer.Offset.X,
                    targetOffset);
        });
    }
    private void QueueRefresh()
    {
        if (!IsVisible)
        {
            return;
        }

        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        var generation = ++_refreshGeneration;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation != _refreshGeneration)
                {
                    return;
                }

                _refreshQueued = false;

                if (!IsVisible)
                {
                    return;
                }

                Refresh();
            },
            DispatcherPriority.Background);
    }

    private void SubtitlesOnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (IsVisible)
        {
            QueueRefresh();
        }
    }

    private void DisposeItems()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }

        _items.Clear();

        _textBoxes.Clear();
        _rowBorders.Clear();
        _numberBlocks.Clear();
    }

    private void Detach()
    {
        DisposeItems();

        _vm.PropertyChanged -=
            VmOnPropertyChanged;

        if (_observableSubtitles != null)
        {
            _observableSubtitles.CollectionChanged -=
                SubtitlesOnCollectionChanged;
        }
    }
}