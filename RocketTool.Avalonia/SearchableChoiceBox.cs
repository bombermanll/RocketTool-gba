using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RocketTool.Avalonia;

public sealed class SearchableChoiceBox : UserControl
{
    public static readonly DirectProperty<SearchableChoiceBox, IEnumerable<ChoiceRow>?> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<SearchableChoiceBox, IEnumerable<ChoiceRow>?>(
            nameof(ItemsSource),
            box => box.ItemsSource,
            (box, value) => box.ItemsSource = value);

    public static readonly DirectProperty<SearchableChoiceBox, ChoiceRow?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<SearchableChoiceBox, ChoiceRow?>(
            nameof(SelectedItem),
            box => box.SelectedItem,
            (box, value) => box.SelectedItem = value);

    private readonly Button _toggleButton;
    private readonly TextBlock _displayText;
    private readonly TextBlock _arrowText;
    private readonly Popup _popup;
    private readonly TextBox _searchBox;
    private readonly ListBox _listBox;
    private readonly TextBlock _emptyText;
    private IReadOnlyList<ChoiceRow> _choices = [];
    private IEnumerable<ChoiceRow>? _itemsSource;
    private ChoiceRow? _selected;

    public event EventHandler? SelectionChanged;
    public event EventHandler? TextChanged;

    public SearchableChoiceBox()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _displayText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _arrowText = new TextBlock
        {
            Text = "▾",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var buttonContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        buttonContent.Children.Add(_displayText);
        Grid.SetColumn(_arrowText, 1);
        buttonContent.Children.Add(_arrowText);

        _toggleButton = new Button
        {
            Content = buttonContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(255, 249, 238)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(148, 127, 96)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(43, 32, 22))
        };
        _searchBox = new TextBox
        {
            Watermark = "搜索...",
            Margin = new Thickness(6),
            MinHeight = 30
        };
        _listBox = new ListBox
        {
            MaxHeight = 320,
            MinWidth = 160
        };
        _emptyText = new TextBlock
        {
            Text = "没有匹配结果",
            IsVisible = false,
            Margin = new Thickness(10, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        };
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(184, 174, 158)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Children =
                    {
                        _searchBox,
                        _emptyText,
                        _listBox
                    }
                }
            }
        };

        var grid = new Grid();
        grid.Children.Add(_toggleButton);
        grid.Children.Add(_popup);
        Content = grid;

        _toggleButton.Click += (_, _) => OpenDropDown();
        _toggleButton.KeyDown += OnToggleKeyDown;
        _searchBox.TextChanged += (_, _) => RefreshList();
        _searchBox.KeyDown += OnSearchKeyDown;
        _listBox.Tapped += (_, _) => SelectHighlighted();
        _listBox.KeyDown += OnListBoxKeyDown;
        SizeChanged += (_, _) => SyncDropDownWidth();
    }

    public IEnumerable<ChoiceRow>? ItemsSource
    {
        get => _itemsSource;
        set
        {
            if (!SetAndRaise(ItemsSourceProperty, ref _itemsSource, value)) return;
            _choices = value?.ToArray() ?? [];
            if (_selected is not null)
                _selected = _choices.FirstOrDefault(c => c.Id == _selected.Id);
            RefreshDisplay();
            RefreshList();
        }
    }

    public ChoiceRow? SelectedItem
    {
        get => _selected;
        set
        {
            if (value is not null)
                SetSelected(value, raise: false);
            else
                ClearSelected(raise: false);
        }
    }

    public string? Text
    {
        get => _selected?.ToString() ?? string.Empty;
        set
        {
            _selected = FindExact(value);
            RefreshDisplay(value);
            RefreshList();
        }
    }

    public string? PlaceholderText { get; set; }

    public double MaxDropDownHeight
    {
        get => _listBox.MaxHeight;
        set => _listBox.MaxHeight = value;
    }

    public void FocusEditor() => _toggleButton.Focus();

    private void OnToggleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter or Key.Space)
        {
            OpenDropDown();
            e.Handled = true;
            return;
        }

        if (IsTextEditingKey(e))
        {
            OpenDropDown();
            e.Handled = false;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            FocusListFirst();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SelectHighlightedOrFirst();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseDropDown();
            e.Handled = true;
        }
    }

    private void OnListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SelectHighlighted();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseDropDown();
            e.Handled = true;
        }
    }

    private void OpenDropDown()
    {
        _searchBox.Text = string.Empty;
        RefreshList();
        SyncDropDownWidth();
        _popup.IsOpen = true;

        Dispatcher.UIThread.Post(() =>
        {
            _searchBox.Focus();
            _searchBox.CaretIndex = _searchBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void CloseDropDown()
    {
        _popup.IsOpen = false;
        _toggleButton.Focus();
    }

    private void SelectHighlightedOrFirst()
    {
        if (_listBox.SelectedItem is not ChoiceRow && _listBox.ItemCount > 0)
            _listBox.SelectedIndex = 0;
        SelectHighlighted();
    }

    private void SelectHighlighted()
    {
        if (_listBox.SelectedItem is ChoiceRow row)
            SetSelected(row, raise: true);
    }

    private void SetSelected(ChoiceRow row, bool raise)
    {
        SetAndRaise(SelectedItemProperty, ref _selected, row);
        RefreshDisplay();
        _listBox.SelectedItem = row;
        _popup.IsOpen = false;

        if (raise)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearSelected(bool raise)
    {
        SetAndRaise(SelectedItemProperty, ref _selected, null);
        _listBox.SelectedItem = null;
        RefreshDisplay();

        if (raise)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshList()
    {
        var term = _searchBox.Text?.Trim() ?? string.Empty;
        var rows = string.IsNullOrWhiteSpace(term)
            ? _choices
            : _choices.Where(choice => MatchesChoice(choice, term)).ToArray();
        _listBox.ItemsSource = rows;
        _emptyText.IsVisible = rows.Count == 0;
        _listBox.IsVisible = rows.Count > 0;
        _listBox.SelectedItem = _selected is not null ? rows.FirstOrDefault(c => c.Id == _selected.Id) : null;
        if (_listBox.SelectedItem is null && rows.Count > 0 && !string.IsNullOrWhiteSpace(term))
            _listBox.SelectedIndex = 0;
    }

    private void FocusListFirst()
    {
        if (_listBox.ItemCount > 0 && _listBox.SelectedIndex < 0)
            _listBox.SelectedIndex = 0;
        _listBox.Focus();
    }

    private void RefreshDisplay(string? fallbackText = null)
    {
        var text = _selected?.ToString();
        if (string.IsNullOrWhiteSpace(text)) text = fallbackText;
        var brush = string.IsNullOrWhiteSpace(text)
            ? new SolidColorBrush(Color.FromRgb(108, 98, 85))
            : new SolidColorBrush(Color.FromRgb(43, 32, 22));
        _displayText.Text = string.IsNullOrWhiteSpace(text) ? PlaceholderDisplay() : text;
        _displayText.Foreground = brush;
        _arrowText.Foreground = brush;
        _toggleButton.Foreground = brush;
        _toggleButton.Background = new SolidColorBrush(Color.FromRgb(255, 249, 238));
        _toggleButton.BorderBrush = new SolidColorBrush(Color.FromRgb(148, 127, 96));
    }

    private string PlaceholderDisplay()
        => string.IsNullOrWhiteSpace(PlaceholderText) ? "请选择" : PlaceholderText;

    private void SyncDropDownWidth()
    {
        var width = Math.Max(Bounds.Width, MinWidth);
        if (width <= 0) width = 180;
        _listBox.Width = width;
        _listBox.MinWidth = width;
        if (_popup.Child is Border border)
        {
            border.Width = width;
            border.MinWidth = width;
        }
    }

    private ChoiceRow? FindExact(string? text)
    {
        var term = text?.Trim();
        if (string.IsNullOrWhiteSpace(term)) return null;
        return _choices.FirstOrDefault(c => string.Equals(c.Name, term, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(c.Display, term, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(c.ToString(), term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesChoice(ChoiceRow choice, string term)
        => choice.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
           || (choice.Display?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
           || choice.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool IsTextEditingKey(KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete or Key.Space) return true;
        if (e.Key is Key.A or Key.B or Key.C or Key.D or Key.E or Key.F or Key.G or Key.H or Key.I or Key.J or Key.K or Key.L or Key.M or Key.N or Key.O or Key.P or Key.Q or Key.R or Key.S or Key.T or Key.U or Key.V or Key.W or Key.X or Key.Y or Key.Z) return true;
        if (e.Key is >= Key.D0 and <= Key.D9) return true;
        if (e.Key is >= Key.NumPad0 and <= Key.NumPad9) return true;
        return false;
    }
}
