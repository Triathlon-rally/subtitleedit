using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main.FlowEditing;

public sealed class FlowEditingView : Border
{
    private const int LinesBefore = 4;
    private const int LinesAfter = 4;

    private readonly MainViewModel _vm;
    private readonly StackPanel _itemsPanel;
    private readonly ScrollViewer _scrollViewer;
    private readonly List<FlowEditingItem> _items = new();
    private readonly INotifyCollectionChanged? _observableSubtitles;

    public FlowEditingView(MainViewModel vm)
    {
        _vm = vm;
        Padding = new Thickness(4);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
        CornerRadius = new CornerRadius(4);
        MinHeight = 220;

        _itemsPanel = new StackPanel
        {
            Spacing = 3,
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _itemsPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        Child = _scrollViewer;

        _vm.PropertyChanged += VmOnPropertyChanged;

        _observableSubtitles = _vm.Subtitles as INotifyCollectionChanged;
        if (_observableSubtitles != null)
        {
            _observableSubtitles.CollectionChanged += SubtitlesOnCollectionChanged;
        }

        DetachedFromVisualTree += (_, _) => Detach();
    }

    public void Refresh()
    {
        DisposeItems();
        _itemsPanel.Children.Clear();

        var subtitles = _vm.Subtitles.ToList();

        if (subtitles.Count == 0)
        {
            _itemsPanel.Children.Add(new TextBlock
            {
                Text = "No subtitles",
                Opacity = 0.65,
                Margin = new Thickness(8),
            });

            return;
        }

        var selectedIndex = _vm.SelectedSubtitle == null
            ? 0
            : subtitles.IndexOf(_vm.SelectedSubtitle);

        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        var first = Math.Max(0, selectedIndex - LinesBefore);
        var last = Math.Min(subtitles.Count - 1, selectedIndex + LinesAfter);

        for (var i = first; i <= last; i++)
        {
            var subtitle = subtitles[i];
            var item = new FlowEditingItem(subtitle);

            _items.Add(item);

            _itemsPanel.Children.Add(
                MakeRow(
                    item,
                    ReferenceEquals(subtitle, _vm.SelectedSubtitle)));
        }
    }

    private Control MakeRow(FlowEditingItem item, bool isCurrent)
    {
        var number = new TextBlock
        {
            Width = 42,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 6, 0),
            Opacity = 0.65,
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
            MinHeight = 34,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            FontSize = Se.Settings.Appearance.SubtitleTextBoxFontSize,
            FontWeight = Se.Settings.Appearance.SubtitleTextBoxFontBold
                ? FontWeight.Bold
                : FontWeight.Normal,
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

        textBox.GotFocus += (_, _) => SelectItem(item);

        textBox.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) => SelectItem(item),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (!string.IsNullOrEmpty(
                Se.Settings.Appearance.SubtitleTextBoxAndGridFontName))
        {
            textBox.FontFamily = new FontFamily(
                Se.Settings.Appearance.SubtitleTextBoxAndGridFontName);
        }

        var timeCode = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(4, 0, 0, 2),
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
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        grid.Children.Add(number);

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(4, 2),
            CornerRadius = new CornerRadius(3),
            Background = isCurrent
                ? new SolidColorBrush(Color.FromArgb(28, 128, 128, 128))
                : Brushes.Transparent,
        };
    }

    private void SelectItem(FlowEditingItem item)
    {
        if (!ReferenceEquals(_vm.SelectedSubtitle, item.Source))
        {
            _vm.SelectedSubtitle = item.Source;
        }
    }

    private void VmOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedSubtitle))
        {
            Refresh();
        }
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
    }

    private void Detach()
    {
        DisposeItems();

        _vm.PropertyChanged -= VmOnPropertyChanged;

        if (_observableSubtitles != null)
        {
            _observableSubtitles.CollectionChanged -=
                SubtitlesOnCollectionChanged;
        }
    }
}