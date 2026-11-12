using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MorphPlayer;

/// <summary>
/// Gives a window a title bar to match the theme, and keeps it matching when the theme changes. Windows 11 takes
/// the theme's own colours; Windows 10 can only be asked for its dark or light title bar. Also passes on the news
/// when Windows' own colours change, for a theme or accent that follows them.
/// </summary>
static class TitleBarTheme
{
    const int DwmwaUseImmersiveDarkMode = 20;           // Windows 10 20H1 and later
    const int DwmwaUseImmersiveDarkModeBefore20H1 = 19; // Windows 10 1809–1909
    const int DwmwaBorderColor = 34;                    // these three: Windows 11
    const int DwmwaCaptionColor = 35;
    const int DwmwaTextColor = 36;
    const int WmSettingChange = 0x001A;
    const int WmDwmColorizationColorChanged = 0x0320;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Follow(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) TitleBarTheme.Apply(hwnd, Themes.Current);
        }

        window.SourceInitialized += (_, _) =>
        {
            Apply();
            ((HwndSource)PresentationSource.FromVisual(window)).AddHook(WndProc);
        };
        Themes.Changed += Apply;
        window.Closed += (_, _) => Themes.Changed -= Apply;
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows broadcasts "ImmersiveColorSet" when the light/dark app mode or the accent colour changes.
        if (msg == WmDwmColorizationColorChanged
            || (msg == WmSettingChange && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"))
            Themes.WindowsColorsChanged();
        return IntPtr.Zero;
    }

    static void Apply(IntPtr hwnd, Theme theme)
    {
        // Sets the colour of the minimize, maximize and close glyphs, and the whole title bar on Windows 10.
        var dark = theme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));

        // Windows 10 refuses these, which leaves its own dark or light title bar.
        Set(hwnd, DwmwaCaptionColor, theme.Panel);
        Set(hwnd, DwmwaTextColor, theme.Text);
        Set(hwnd, DwmwaBorderColor, theme.Border);
    }

    static void Set(IntPtr hwnd, int attribute, Color color)
    {
        var colorRef = color.R | color.G << 8 | color.B << 16; // COLORREF: 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }
}
