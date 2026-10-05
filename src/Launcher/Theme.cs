using System.Drawing;
using System.Windows.Forms;

namespace StembridgeValley.Launcher;

/// <summary>The Starlit Hollow colors, shared by every launcher window.</summary>
internal static class Palette
{
    public static readonly Color Night = Color.FromArgb(0x19, 0x17, 0x28);    // window background
    public static readonly Color Dusk = Color.FromArgb(0x28, 0x22, 0x3E);     // header band, main button text
    public static readonly Color Field = Color.FromArgb(0x22, 0x1F, 0x36);    // text box / progress track
    public static readonly Color Ivory = Color.FromArgb(0xF4, 0xEC, 0xDD);    // main text
    public static readonly Color Lilac = Color.FromArgb(0xB9, 0xAE, 0xCB);    // muted text, borders
    public static readonly Color Lavender = Color.FromArgb(0xB8, 0xA0, 0xDE); // main button
    public static readonly Color LavenderHi = Color.FromArgb(0xCB, 0xB8, 0xEA);
    public static readonly Color Gold = Color.FromArgb(0xE4, 0xBE, 0x66);     // accent
    public static readonly Color Rose = Color.FromArgb(0xF2, 0xA7, 0xA0);     // problem hints
}

/// <summary>Shared styling for buttons, text boxes and the dark title bar.</summary>
internal static class Theme
{
    public static void Primary(Button b) => Button(b, true);
    public static void Secondary(Button b) => Button(b, false);

    public static void Button(Button b, bool main)
    {
        b.AutoSize = false;
        b.FlatStyle = FlatStyle.Flat;
        b.Font = new Font("Segoe UI Semibold", 10.5f);
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderSize = main ? 0 : 1;
        b.FlatAppearance.BorderColor = Palette.Lilac;
        b.BackColor = main ? Palette.Lavender : Palette.Night;
        b.ForeColor = main ? Palette.Dusk : Palette.Ivory;
        b.FlatAppearance.MouseOverBackColor = main ? Palette.LavenderHi : Palette.Field;
        b.FlatAppearance.MouseDownBackColor = main ? Palette.Lilac : Palette.Dusk;
    }

    public static void Box(TextBox box)
    {
        box.Font = new Font("Consolas", 11f);
        box.BackColor = Palette.Field;
        box.ForeColor = Palette.Ivory;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar in the header's dusk purple (Windows 11; older Windows keeps its own).</summary>
    public static void DarkTitle(IntPtr handle)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
            int caption = Palette.Dusk.R | Palette.Dusk.G << 8 | Palette.Dusk.B << 16;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int text = Palette.Lilac.R | Palette.Lilac.G << 8 | Palette.Lilac.B << 16;
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref caption, sizeof(int));
        }
        catch { }
    }
}
