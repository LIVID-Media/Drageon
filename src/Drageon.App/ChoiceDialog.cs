using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Drageon.App;

public static class ChoiceDialog
{
    public static string? ShowText(string title, string label, string initialValue = "")
    {
        var input = new TextBox { Text = initialValue, MinWidth = 400, Margin = new Thickness(0, 6, 0, 16) };
        input.SelectAll();
        var iconStyle = (Style)Application.Current.FindResource("IconButton");
        var ok = new Button { Content = "\uE73E", ToolTip = Localization.Get("common.continue"), IsDefault = true, Style = iconStyle };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(input); panel.Children.Add(buttons);
        var window = CreateWindow(title, panel);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? input.Text : null;
    }

    public static string? Show(string title, string label, IEnumerable<(string? Value, string Label)> choices, string? selected = null, string acceptKey = "common.save")
    {
        var values = choices.Select(item => new ChoiceItem(item.Value, item.Label)).ToArray();
        var combo = new ComboBox { ItemsSource = values, DisplayMemberPath = nameof(ChoiceItem.Label), SelectedValuePath = nameof(ChoiceItem.Value), MinWidth = 360, Margin = new Thickness(0, 6, 0, 16) };
        combo.SelectedValue = selected;
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
        var iconStyle = (Style)Application.Current.FindResource("IconButton");
        var ok = new Button { Content = "\uE74E", ToolTip = Localization.Get(acceptKey), IsDefault = true, Style = iconStyle };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(combo); panel.Children.Add(buttons);
        var window = CreateWindow(title, panel);
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? (combo.SelectedItem as ChoiceItem)?.Value : null;
    }

    public static (string? Value, bool BackupCurrent, SnapshotManageAction Action)? ShowSnapshotManage(string title, IEnumerable<(string Value, string Label)> choices)
    {
        var values = choices.Select(item => new ChoiceItem(item.Value, item.Label)).ToArray(); if (values.Length == 0) return null;
        var list = new ListBox { ItemsSource = values, DisplayMemberPath = nameof(ChoiceItem.Label), SelectedValuePath = nameof(ChoiceItem.Value), SelectedIndex = 0, MinWidth = 650, MaxHeight = 300 };
        var backup = new CheckBox { Content = Localization.Get("snapshots.backupCurrent"), IsChecked = true, Margin = new Thickness(0, 12, 0, 16) };
        var iconStyle = (Style)Application.Current.FindResource("IconButton");
        var restore = new Button { Content = "\uEE71", ToolTip = Localization.Get("snapshots.restore"), IsDefault = true, Style = iconStyle, Margin = new Thickness(8, 0, 0, 0) };
        var delete = new Button { Content = "\uE74D", ToolTip = Localization.Get("snapshots.delete"), Style = iconStyle, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(restore); buttons.Children.Add(delete);
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(list); panel.Children.Add(backup); panel.Children.Add(buttons);
        var window = CreateWindow(title, panel);
        var action = SnapshotManageAction.Restore;
        restore.Click += (_, _) => { action = SnapshotManageAction.Restore; window.DialogResult = true; };
        delete.Click += (_, _) => { action = SnapshotManageAction.Delete; window.DialogResult = true; };
        return window.ShowDialog() == true ? ((list.SelectedItem as ChoiceItem)?.Value, backup.IsChecked == true, action) : null;
    }

    private static Window CreateWindow(string title, object content)
    {
        var window = new Window
        {
            Title = title, Content = content, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, MinWidth = 440
        };
        window.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Escape) return;
            window.DialogResult = false;
            eventArgs.Handled = true;
        };
        return window;
    }

    public sealed record ChoiceItem(string? Value, string Label);
}

public enum SnapshotManageAction { Restore, Delete }
