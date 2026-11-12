using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MorphPlayer;

/// <summary>
/// One look for the parts of MorphPlayer that aren't the video: the playlist panel, menus, dialog and title bar, and
/// the colour of the video area while nothing is showing there. Text is derived from <see cref="Dark"/>.
/// </summary>
/// <param name="Stage">The video area while there's no picture, and the window behind it.</param>
/// <param name="Card">Banners and prompts shown over the video area.</param>
/// <param name="Panel">The playlist panel, the dialog and the title bar.</param>
/// <param name="Menu">Menus, tooltips and text boxes: a step up from <paramref name="Panel"/>.</param>
/// <param name="Tile">Thumbnail placeholders and plain buttons.</param>
sealed record Theme(string Id, string Name, bool Dark, Color Stage, Color Card, Color Panel, Color Menu, Color Tile,
                    Color Border, Color Text, Color TextDim);

/// <summary>
/// The themes and accent colours on offer, and the brushes the XAML uses for whichever are chosen. Every colour in
/// App.xaml and MainWindow.xaml is a {DynamicResource} set here, so a change shows at once.
///
/// There are two sets under the same names. The application's set follows the theme. <see cref="OverVideo"/>, which
/// MainWindow merges into the overlay, is always light on dark, because the control bar and banners sit on the
/// picture; only its Card and Accent follow the choice. Menus and tooltips opened from the overlay inherit its
/// resources, so they have names of their own (MenuBg, MenuFg, …) that the overlay never overrides.
/// </summary>
static class Themes
{
    /// <summary>As a theme id: dark or light, whichever Windows is set to for apps.</summary>
    public const string MatchWindows = "system";
    /// <summary>As an accent: the accent colour chosen in Windows Settings.</summary>
    public const string WindowsAccent = "windows";
    public const string DefaultTheme = "dark";
    public const string DefaultAccent = "#FF8800";

    public static IReadOnlyList<Theme> All { get; } =
    [
        new("dark", "Dark", true, Rgb(0x000000), Rgb(0x181818), Rgb(0x1C1C1C), Rgb(0x2B2B2B), Rgb(0x262626),
            Rgb(0x2A2A2A), Rgb(0xE6E6E6), Rgb(0x9A9A9A)),
        new("black", "Black", true, Rgb(0x000000), Rgb(0x0E0E0E), Rgb(0x000000), Rgb(0x121212), Rgb(0x171717),
            Rgb(0x242424), Rgb(0xE0E0E0), Rgb(0x8C8C8C)),
        new("midnight", "Midnight", true, Rgb(0x060A14), Rgb(0x101828), Rgb(0x0D1424), Rgb(0x162036), Rgb(0x1A2540),
            Rgb(0x222E4A), Rgb(0xE2E8F4), Rgb(0x8B98B8)),
        new("forest", "Forest", true, Rgb(0x050C08), Rgb(0x101C15), Rgb(0x0D1711), Rgb(0x16241B), Rgb(0x1A2B20),
            Rgb(0x223528), Rgb(0xE2EDE5), Rgb(0x8AA394)),
        new("plum", "Plum", true, Rgb(0x0C0610), Rgb(0x1B1023), Rgb(0x170D1E), Rgb(0x23162D), Rgb(0x291A35),
            Rgb(0x332243), Rgb(0xEDE4F2), Rgb(0xA391B0)),
        // The video area stays black, as a cinema screen does; the light surfaces are the ones around it.
        new("light", "Light", false, Rgb(0x000000), Rgb(0x181818), Rgb(0xF3F3F3), Rgb(0xFBFBFB), Rgb(0xE3E3E3),
            Rgb(0xD4D4D4), Rgb(0x1A1A1A), Rgb(0x5C5C5C)),
    ];

    /// <summary>The accent colours offered by name. Any other colour can be picked as a custom one.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Accents { get; } =
    [
        ("Orange", DefaultAccent), ("Red", "#E5484D"), ("Pink", "#E8508F"), ("Purple", "#9B6CF0"),
        ("Blue", "#3B9DFF"), ("Teal", "#1DB8A6"), ("Green", "#3FBF5F"), ("Gold", "#E6B422"),
    ];

    // An accent has to stand out from what it's drawn on: 3:1 is the WCAG minimum for controls and large text.
    const double MinAccentContrast = 3;

    static string _themeId = DefaultTheme;
    static string _accent = DefaultAccent;

    /// <summary>The overlay's own brushes; see the class remarks.</summary>
    public static ResourceDictionary OverVideo { get; } = new();

    /// <summary>The choice in force: a theme's id, or <see cref="MatchWindows"/>.</summary>
    public static string ThemeId => _themeId;

    /// <summary>The choice in force: "#RRGGBB", or <see cref="WindowsAccent"/>.</summary>
    public static string AccentSetting => _accent;

    /// <summary>The theme in use; for <see cref="MatchWindows"/>, the one that stands for it right now.</summary>
    public static Theme Current { get; private set; } = All[0];

    /// <summary>The accent as chosen, before it is adjusted to stand out from each background.</summary>
    public static Color Accent { get; private set; } = ParseOr(DefaultAccent, Colors.Orange);

    /// <summary>Raised after the brushes change, for whatever XAML can't reach, such as title bars.</summary>
    public static event Action? Changed;

    /// <summary>Puts a theme and accent in force. Anything unrecognised, such as a theme a newer version saved, becomes the default.</summary>
    public static void Apply(string? themeId, string? accent)
    {
        _themeId = themeId == MatchWindows || All.Any(t => t.Id == themeId) ? themeId! : DefaultTheme;
        _accent = accent == WindowsAccent ? WindowsAccent : TryParse(accent, out var parsed) ? ToHex(parsed) : DefaultAccent;

        var id = _themeId == MatchWindows ? WindowsAppsAreDark() ? "dark" : "light" : _themeId;
        var theme = Current = All.First(t => t.Id == id);
        Accent = _accent == WindowsAccent ? WindowsAccentColor() : ParseOr(_accent, Accent);

        var app = Application.Current.Resources;
        var ink = theme.Dark ? Colors.White : Colors.Black;
        var onPanel = Readable(Accent, theme.Panel);
        Set(app, "Stage", theme.Stage);
        Set(app, "Panel", theme.Panel);
        Set(app, "Card", theme.Panel);
        Set(app, "Tile", theme.Tile);
        Set(app, "Border", theme.Border);
        Set(app, "Fg", theme.Text);
        Set(app, "FgDim", theme.TextDim);
        Set(app, "Hover", Alpha(ink, theme.Dark ? 0x33 : 0x1C));
        Set(app, "ItemHover", Alpha(ink, theme.Dark ? 0x18 : 0x0F));
        Set(app, "Selected", Alpha(ink, theme.Dark ? 0x38 : 0x24));
        Set(app, "Divider", Alpha(ink, theme.Dark ? 0x40 : 0x30));
        Set(app, "Track", Alpha(ink, theme.Dark ? 0x55 : 0x40));
        Set(app, "Thumb", theme.Dark ? Colors.White : theme.Text);
        Set(app, "Accent", onPanel);
        Set(app, "OnAccent", Luminance(onPanel) > 0.4 ? Colors.Black : Colors.White);
        Set(app, "MenuBg", theme.Menu);
        Set(app, "MenuFg", theme.Text);
        Set(app, "MenuFgDim", theme.TextDim);
        Set(app, "MenuHover", Alpha(ink, theme.Dark ? 0x24 : 0x14));
        Set(app, "MenuBorder", Alpha(ink, theme.Dark ? 0x2C : 0x28));
        Set(app, "MenuAccent", Readable(Accent, theme.Menu));

        // The picture can be any colour, so what's drawn over it keeps to light on dark whatever the theme.
        Set(OverVideo, "Fg", Rgb(0xE6E6E6));
        Set(OverVideo, "FgDim", Rgb(0x9A9A9A));
        Set(OverVideo, "Hover", Alpha(Colors.White, 0x33));
        Set(OverVideo, "Divider", Alpha(Colors.White, 0x40));
        Set(OverVideo, "Track", Alpha(Colors.White, 0x55));
        Set(OverVideo, "Thumb", Colors.White);
        Set(OverVideo, "Card", Alpha(theme.Card, 0xE0));
        Set(OverVideo, "Accent", Readable(Accent, theme.Stage));

        Changed?.Invoke();
    }

    /// <summary>
    /// Call when Windows says its colours changed. Only matters while the theme or accent follows Windows.
    /// </summary>
    public static void WindowsColorsChanged()
    {
        if (_themeId == MatchWindows || _accent == WindowsAccent) Apply(_themeId, _accent);
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Rgb(rgb);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Settings → Personalization → Colors → "Choose your default app mode".</summary>
    public static bool WindowsAppsAreDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>Settings → Personalization → Colors → "Accent color".</summary>
    public static Color WindowsAccentColor()
    {
        // Stored as 0xAABBGGRR. WindowGlassColor is the nearest thing WPF knows, for a PC without the value.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int abgr)
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        var glass = SystemParameters.WindowGlassColor;
        return Color.FromRgb(glass.R, glass.G, glass.B);
    }

    /// <summary>
    /// The accent, moved toward white or black only as far as it takes to stand out from the background. Most
    /// pairs come back unchanged; it catches the likes of navy on black, or gold on the light theme.
    /// </summary>
    static Color Readable(Color accent, Color background)
    {
        var toward = Luminance(background) < 0.5 ? Colors.White : Colors.Black;
        var color = accent;
        for (var step = 1; step <= 20 && Contrast(color, background) < MinAccentContrast; step++)
        {
            var t = step / 20.0;
            color = Color.FromRgb(Mix(accent.R, toward.R, t), Mix(accent.G, toward.G, t), Mix(accent.B, toward.B, t));
        }
        return color;
    }

    static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);

    /// <summary>WCAG contrast ratio, from 1 (the same) to 21 (black on white).</summary>
    static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG relative luminance, from 0 (black) to 1 (white).</summary>
    static double Luminance(Color c)
    {
        static double Linear(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    static Color Alpha(Color color, int alpha) => Color.FromArgb((byte)alpha, color.R, color.G, color.B);

    static Color ParseOr(string hex, Color fallback) => TryParse(hex, out var color) ? color : fallback;
}
