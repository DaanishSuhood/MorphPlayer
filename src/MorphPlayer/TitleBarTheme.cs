using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MorphPlayer;

/// <summary>
/// Gives a window a dark or light title bar to match the Windows app mode (Settings → Personalization →
/// Colors), and follows it when the user changes it. Windows draws a light title bar unless asked otherwise.
/// </summary>
static class TitleBarTheme
{
    const int DwmwaUseImmersiveDarkMode = 20;           // Windows 10 20H1 and later
    const int DwmwaUseImmersiveDarkModeBefore20H1 = 19; // Windows 10 1809–1909
    const int WmSettingChange = 0x001A;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Follow(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(window);
            Apply(source.Handle);
            source.AddHook(WndProc);
        };
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows broadcasts "ImmersiveColorSet" when the light/dark app mode changes.
        if (msg == WmSettingChange && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            Apply(hwnd);
        return IntPtr.Zero;
    }

    static void Apply(IntPtr hwnd)
    {
        var dark = AppsUseDarkTheme() ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
    }

    static bool AppsUseDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
