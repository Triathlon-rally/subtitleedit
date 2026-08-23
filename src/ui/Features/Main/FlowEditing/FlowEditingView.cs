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
    private readonly StackPanel _itemsPanel;
    private readonly ScrollViewer _scrollViewer;

    private readonly List<FlowEditingItem> _items = new();
    private readonly Dictionary<FlowEditingItem, TextBox> _textBoxes = new();
    private readonly Dictionary<FlowEditingItem, Border> _rowBorders = new();
    private readonly Dictionary<FlowEditingItem, TextBlock> _numberBlocks = new();

    private readonly INotifyCollectionChanged? _observableSubtitles;

    private SubtitleLineViewModel? _pendingFocusSource;
    private bool _pendingFocusAtStart;

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
        DisposeItems();

        _itemsPanel.Children.Clear();
        _textBoxes.Clear();
        _rowBorders.Clear();
        _numberBlocks.Clear();

        var subtitles = _vm.Subtitles.ToList();

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
            (_, _) => SelectItem(item);

        textBox.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) => SelectItem(item),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

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

        _textBoxes[item] = textBox;
        _rowBorders[item] = rowBorder;
        _numberBlocks[item] = number;

        return rowBorder;
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

        var previousVisibleText =
            FlowTextParser.Parse(previous.Text).Text;

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
                ReferenceEquals(
                    item.Source,
                    _vm.SelectedSubtitle);

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

        if (e.PropertyName !=
            nameof(MainViewModel.SelectedSubtitle))
        {
            return;
        }

        if (IsSubtitleCurrentlyVisible(
                _vm.SelectedSubtitle))
        {
            UpdateSelectionVisuals();
            ApplyPendingFocus();
            return;
        }

        Refresh();
    }

    private void SubtitlesOnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (IsVisible)
        {
            Refresh();
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