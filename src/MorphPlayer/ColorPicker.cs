using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MorphPlayer;

/// <summary>The Windows colour dialog, for a custom accent colour. WPF has no colour picker of its own.</summary>
static class ColorPicker
{
    const int CcRgbInit = 0x1;   // start on the colour passed in
    const int CcFullOpen = 0x2;  // show the full picker, not only the basic swatches
    const int CcAnyColor = 0x100;
    const int CustomColorCount = 16;

    [StructLayout(LayoutKind.Sequential)]
    struct ChooseColorInfo
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public int Result;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    static extern bool ChooseColor(ref ChooseColorInfo info);

    /// <summary>Null if the dialog was cancelled.</summary>
    public static Color? Pick(Window owner, Color initial)
    {
        // The dialog insists on somewhere to keep its row of 16 "custom colors". The first shows the colour in use;
        // the rest start white.
        var custom = Marshal.AllocHGlobal(CustomColorCount * sizeof(int));
        try
        {
            Marshal.WriteInt32(custom, ToColorRef(initial));
            for (var i = 1; i < CustomColorCount; i++) Marshal.WriteInt32(custom, i * sizeof(int), 0xFFFFFF);
            var info = new ChooseColorInfo
            {
                StructSize = Marshal.SizeOf<ChooseColorInfo>(),
                Owner = new WindowInteropHelper(owner).Handle,
                Result = ToColorRef(initial),
                CustomColors = custom,
                Flags = CcRgbInit | CcFullOpen | CcAnyColor,
            };
            if (!ChooseColor(ref info)) return null;
            return Color.FromRgb((byte)info.Result, (byte)(info.Result >> 8), (byte)(info.Result >> 16));
        }
        finally
        {
            Marshal.FreeHGlobal(custom);
        }
    }

    static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16; // 0x00BBGGRR
}
