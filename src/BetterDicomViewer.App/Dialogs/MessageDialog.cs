using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterDicomViewer.App.Dialogs;

/// <summary>Dark, themed replacement for MessageBox (the Win32 message box ignores the app theme).</summary>
public sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message, string? details, string primary, string? secondary)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 700;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(details))
        {
            var box = new TextBox
            {
                Text = details,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            panel.Children.Add(new Expander { Header = "Details", Content = box, Margin = new Thickness(0, 12, 0, 0) });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = primary, MinWidth = 96, Padding = new Thickness(14, 5, 14, 5), IsDefault = true, Style = (Style)FindResource("AccentButtonStyle") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        if (secondary is not null)
        {
            var cancel = new Button { Content = secondary, MinWidth = 96, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);
        }
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static void Show(Window? owner, string title, string message, string? details = null)
    {
        var d = new MessageDialog(title, message, details, "OK", null);
        if (owner is { IsLoaded: true }) d.Owner = owner;
        d.ShowDialog();
    }

    public static bool Confirm(Window? owner, string title, string message, string confirm, string cancel = "Cancel")
    {
        var d = new MessageDialog(title, message, null, confirm, cancel);
        if (owner is { IsLoaded: true }) d.Owner = owner;
        return d.ShowDialog() == true;
    }
}
