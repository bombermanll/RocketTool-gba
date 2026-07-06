using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RocketTool.Core;

namespace RocketTool.Avalonia;

public sealed class VersionSelectionWindow : Window
{
    private readonly ComboBox _profileBox;
    private readonly TextBlock _profileIdText;
    private readonly Button _continueButton;
    private readonly Action<GameProfile> _openProfile;

    public VersionSelectionWindow(IReadOnlyList<GameProfile> profiles, Action<GameProfile> openProfile)
    {
        _openProfile = openProfile;
        Title = "火箭队修改工具 - 选择游戏版本";
        Width = 620;
        Height = 280;
        MinWidth = 520;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 14,
            Margin = new Thickness(28)
        };
        root.Children.Add(new TextBlock
        {
            Text = "选择要修改的游戏版本",
            FontSize = 27,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(16, 46, 74))
        });

        _profileBox = new ComboBox
        {
            ItemsSource = profiles,
            PlaceholderText = "请选择游戏版本",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 42
        };
        _profileBox.SelectionChanged += OnSelectionChanged;
        Grid.SetRow(_profileBox, 1);
        root.Children.Add(_profileBox);

        _profileIdText = new TextBlock
        {
            Text = "配置 ID：选择版本后显示",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        };
        Grid.SetRow(_profileIdText, 2);
        root.Children.Add(_profileIdText);

        _continueButton = new Button
        {
            Content = "进入修改器",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 130,
            MinHeight = 40
        };
        _continueButton.Click += (_, _) => OpenSelectedProfile();
        Grid.SetRow(_continueButton, 3);
        root.Children.Add(_continueButton);
        Content = root;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_profileBox.SelectedItem is not GameProfile profile)
        {
            _continueButton.IsEnabled = false;
            _profileIdText.Text = "配置 ID：选择版本后显示";
            return;
        }

        _continueButton.IsEnabled = true;
        _profileIdText.Text = $"配置 ID：{profile.Id}";
    }

    private void OpenSelectedProfile()
    {
        if (_profileBox.SelectedItem is not GameProfile profile) return;
        _continueButton.IsEnabled = false;
        _openProfile(profile);
        Close();
    }
}
