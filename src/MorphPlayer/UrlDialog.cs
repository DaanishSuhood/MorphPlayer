using System.Windows;
using System.Windows.Controls;

namespace MorphPlayer;

/// <summary>Small prompt for a network stream URL (http, rtsp, rtmp, udp, …).</summary>
public sealed class UrlDialog : Window
{
    readonly TextBox _box;

    UrlDialog(Window owner)
    {
        Owner = owner;
        Title = "Open Network Stream";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // Colours from the theme, like the main window; the styles are in App.xaml.
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(ForegroundProperty, "Fg");
        TitleBarTheme.Follow(this);

        _box = new TextBox { Width = 460, Margin = new Thickness(0, 8, 0, 16), Style = (Style)FindResource("DialogTextBox") };
        if (Clipboard.ContainsText() && LooksLikeUrl(Clipboard.GetText().Trim()))
            _box.Text = Clipboard.GetText().Trim();

        var buttonStyle = (Style)FindResource("DialogButton");
        var ok = new Button { Content = "Play", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Style = buttonStyle };
        ok.Click += (_, _) => DialogResult = LooksLikeUrl(_box.Text.Trim());
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = buttonStyle };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { FontSize = 13, Text = "Enter a network URL (e.g. https://…, rtsp://…, udp://@:1234),\n"
                                                                + "or a web page such as a YouTube video:" });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Prompt(Window owner)
    {
        var dialog = new UrlDialog(owner);
        return dialog.ShowDialog() == true ? dialog._box.Text.Trim() : null;
    }

    static bool LooksLikeUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && !uri.IsFile;
}
