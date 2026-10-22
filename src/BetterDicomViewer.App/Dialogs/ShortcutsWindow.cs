using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDicomViewer.App.Services;

namespace BetterDicomViewer.App.Dialogs;

/// <summary>Searchable reference of every keyboard command and mouse gesture, generated from the command registry.</summary>
public sealed class ShortcutsWindow : Window
{
    private readonly CommandRegistry _registry;
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new() { Padding = new Thickness(8, 6, 8, 6) };

    public ShortcutsWindow(CommandRegistry registry)
    {
        _registry = registry;
        Title = "Keyboard and mouse shortcuts";
        Width = 760;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search shortcuts");

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "Shortcuts", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "Every command is also in the command palette (Ctrl+K). Tool buttons show their key in the tooltip.",
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 4, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(_search);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        Content = root;

        _search.TextChanged += (_, _) => Fill();
        Loaded += (_, _) => _search.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var q = _search.Text.Trim();
        bool Match(string a, string b) => q.Length == 0 || a.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Contains(q, StringComparison.OrdinalIgnoreCase);

        foreach (var group in _registry.All.Where(c => c.Shortcuts.Count > 0).GroupBy(c => c.Category))
        {
            var rows = group.Where(c => Match(c.Title, c.ShortcutText)).Select(c => (c.Title, Keys: c.Shortcuts.Select(s => s.ToString()).ToList())).ToList();
            AddGroup(group.Key, rows);
        }
        foreach (var group in _registry.Hints.GroupBy(h => h.Category))
        {
            var rows = group.Where(h => Match(h.Action, h.Gesture)).Select(h => (h.Action, Keys: new List<string> { h.Gesture })).ToList();
            AddGroup(group.Key, rows);
        }
    }

    private void AddGroup(string title, List<(string Title, List<string> Keys)> rows)
    {
        if (rows.Count == 0) return;
        _list.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) });
        foreach (var (text, keys) in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(chips, 1);
            foreach (var k in keys)
            {
                chips.Children.Add(new Border
                {
                    Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                    BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new TextBlock { Text = k, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 12 },
                });
            }
            grid.Children.Add(chips);
            _list.Children.Add(grid);
        }
    }
}

/// <summary>Type-to-filter list of every command (Ctrl+K).</summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly CommandRegistry _registry;
    private readonly TextBox _search = new() { Padding = new Thickness(10, 8, 10, 8), FontSize = 15 };
    private readonly ListBox _list = new() { Margin = new Thickness(0, 8, 0, 0) };

    public CommandPaletteWindow(CommandRegistry registry)
    {
        _registry = registry;
        Title = "Command palette";
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search commands");
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);
        root.Children.Add(_list);
        Content = root;
        _search.TextChanged += (_, _) => Fill();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && _list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
        };
        _list.MouseDoubleClick += (_, _) => Accept();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Loaded += (_, _) => _search.Focus();
        Fill();
    }

    public AppCommand? Selected { get; private set; }

    private sealed record Item(AppCommand Command)
    {
        public override string ToString() => Command.Shortcuts.Count > 0 ? $"{Command.Title}    {Command.ShortcutText}" : Command.Title;
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var c in _registry.All.Where(c => q.Length == 0 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => (c.Title + " " + c.Category).Contains(t, StringComparison.OrdinalIgnoreCase))))
            _list.Items.Add(new Item(c));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Item item) return;
        Selected = item.Command;
        DialogResult = true;
    }
}

public sealed class ScreenshotWindow : Window
{
    private readonly RadioButton _active = new() { Content = "Active viewport", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _all = new() { Content = "All visible viewports", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _overlay = new() { Content = "Include text, measurements and orientation labels", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _hide = new() { Content = "Hide patient name, ID and dates", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };

    public ScreenshotWindow()
    {
        Title = "Save screenshot";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Capture", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_active);
        panel.Children.Add(_all);
        panel.Children.Add(_overlay);
        panel.Children.Add(_hide);
        panel.Children.Add(new TextBlock
        {
            Text = "Pixel data can still contain burned-in identifiers (common in ultrasound and screen captures).",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Choose file…", Padding = new Thickness(14, 5, 14, 5), IsDefault = true, Style = (Style)FindResource("AccentButtonStyle") };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public bool AllViewports => _all.IsChecked == true;
    public bool IncludeOverlay => _overlay.IsChecked == true;
    public bool HidePatient => _hide.IsChecked == true;
}
