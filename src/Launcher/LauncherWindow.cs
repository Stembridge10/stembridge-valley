using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StembridgeValley.Launcher;

/// <summary>
/// The friendly launcher window: a cozy header, one big status line, a progress bar,
/// and only the one box or button that the current step needs.
/// </summary>
internal sealed class LauncherWindow : Form, IUi
{
    // "Starlit Hollow" palette (owner-approved).
    private static Color Night => Palette.Night;
    private static Color Dusk => Palette.Dusk;
    private static Color Field => Palette.Field;
    private static Color Ivory => Palette.Ivory;
    private static Color Lilac => Palette.Lilac;
    private static Color Lavender => Palette.Lavender;
    private static Color LavenderHi => Palette.LavenderHi;
    private static Color Gold => Palette.Gold;
    private static Color Rose => Palette.Rose;

    private readonly Label title = new(), detail = new(), hint = new();
    private readonly ProgressStrip bar = new();
    private readonly TextBox box = new();
    private readonly Button primary = new(), secondary = new(), browse = new();
    private readonly FlowLayoutPanel buttons = new();
    private TaskCompletionSource<string?>? answer;
    private readonly string? shotsDir = Environment.GetEnvironmentVariable("SV_LAUNCHER_SHOTS");
    private readonly string? autopilot = Environment.GetEnvironmentVariable("SV_LAUNCHER_AUTOPILOT");
    private int shot;
    private bool finished;

    public LauncherWindow()
    {
        Text = "Junimo Hollow";
        ClientSize = new Size(560, 380);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Night;
        ForeColor = Ivory;
        Font = new Font("Segoe UI", 10.5f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Brand.AppIcon(); } catch { }

        var header = new HeaderPanel { Dock = DockStyle.Top, Height = 104 };
        Controls.Add(header);

        title.SetBounds(36, 128, 488, 34);
        title.Font = new Font("Segoe UI Semibold", 15f);
        title.ForeColor = Ivory;
        title.AutoEllipsis = true;

        detail.SetBounds(36, 166, 488, 64);
        detail.ForeColor = Lilac;

        bar.SetBounds(36, 240, 488, 10);
        bar.Track = Field;
        bar.Fill = Lavender;
        bar.Visible = false;

        box.SetBounds(36, 236, 488, 30);
        box.Font = new Font("Consolas", 11f);
        box.BackColor = Field;
        box.ForeColor = Ivory;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Visible = false;
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; primary.PerformClick(); }
        };

        hint.SetBounds(36, 270, 488, 22);
        hint.ForeColor = Rose;
        hint.Font = new Font("Segoe UI", 9.5f);

        buttons.SetBounds(36, 306, 488, 48);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;
        Style(primary, true);
        Style(secondary, false);
        Style(browse, false);
        buttons.Controls.AddRange(new Control[] { primary, secondary, browse });
        primary.Click += (_, _) => Answer(box.Visible ? box.Text : "yes");
        secondary.Click += (_, _) => Answer(null);
        browse.Click += (_, _) => { if (farm != null && answer != null && readyShowing) OpenFarm(); else Browse(); };

        Controls.AddRange(new Control[] { title, detail, bar, box, hint, buttons });
        ShowButtons();

        FormClosing += (_, e) =>
        {
            if (finished || answer?.Task.IsCompleted == false)
            {
                answer?.TrySetResult(null);
                return;
            }
            // Mid-download: closing is fine, the next start picks up again.
        };

        if (autopilot != null)
        {
            // Automated test: place the window out of the way (second monitor if any) and don't take focus.
            StartPosition = FormStartPosition.Manual;
            var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!;
            Location = new Point(screen.WorkingArea.Right - Width - 40, screen.WorkingArea.Top + 40);
        }
    }

    protected override bool ShowWithoutActivation => autopilot != null;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitle(Handle);
    }

    // ---------- IUi ----------

    public void Status(string text, string? more = null) => Ui(() =>
    {
        title.Text = text;
        detail.Text = more ?? "";
        hint.Text = "";
        box.Visible = false;
        ShowButtons();
        Snap();
    });

    public void Progress(int? percent) => Ui(() =>
    {
        bar.Visible = percent != null && !box.Visible;
        if (percent is int p)
        {
            bar.Percent = p < 0 ? null : Math.Clamp(p, 0, 100);
        }
    });

    public Task<string?> AskInvite(string? problem) => Ask(() =>
    {
        title.Text = "Welcome! Paste your invite code";
        detail.Text = "Type /play in the Junimo Hollow Discord and the bot will send you a code that starts with sv:";
        hint.Text = problem ?? "";
        box.Text = "";
        box.UseSystemPasswordChar = false;
        box.Visible = true;
        bar.Visible = false;
        ShowButtons(("Next", true), ("Close", false));
    }, "invite");

    public Task<string?> AskGameFolder(string? problem) => Ask(() =>
    {
        title.Text = "Where is Stardew Valley installed?";
        detail.Text = "We couldn't find it automatically. Pick the folder that has \"Stardew Valley.exe\" in it.";
        hint.Text = problem ?? "";
        box.Text = "";
        box.Visible = true;
        bar.Visible = false;
        ShowButtons(("Next", true), ("Close", false), ("Browse...", null));
    }, "folder");

    public async Task<bool> Confirm(string head, string text, string yes) => await Ask(() =>
    {
        title.Text = head;
        detail.Text = text;
        hint.Text = "";
        box.Visible = false;
        bar.Visible = false;
        ShowButtons((yes, true), ("Not now", false));
    }, "confirm") != null;

    public async Task<bool> Problem(string head, string text, bool canRetry) => await Ask(() =>
    {
        title.Text = head;
        detail.Text = text;
        hint.Text = "";
        box.Visible = false;
        bar.Visible = false;
        if (canRetry) ShowButtons(("Try again", true), ("Close", false));
        else ShowButtons(("Close", false));
    }, "problem") != null;

    private FarmClient? farm;
    private bool readyShowing;

    public async Task<bool> Ready(FarmClient? farmClient)
    {
        farm = farmClient;
        bool play = await Ask(() =>
        {
            readyShowing = true;
            title.Text = "Ready to play";
            detail.Text = farm != null
                ? "Your mods are up to date. Press Play to join, or open My farm to rename it, invite a friend, or see who lives there."
                  + (farm.Slot == 2 ? " (You're on your second character.)" : "")
                : "Your mods are up to date. Press Play to join the server.";
            hint.Text = "";
            box.Visible = false;
            bar.Visible = false;
            if (farm != null) ShowButtons(("Play", true), ("Close", false), ("My farm", null));
            else ShowButtons(("Play", true), ("Close", false));
        }, "ready") != null;
        readyShowing = false;
        return play;
    }

    private void OpenFarm()
    {
        if (farm == null) return;
        using var w = new FarmWindow(farm, shotsDir == null ? null : Path.Combine(shotsDir, "farm"), autopilot != null);
        w.ShowDialog(this);
        if (readyShowing) detail.Text = "Your mods are up to date. Press Play to join, or open My farm to rename it, invite a friend, or see who lives there."
            + (farm.Slot == 2 ? " (You're on your second character.)" : "");
        if (autopilot != null) primary.PerformClick();
    }

    public void Playing() => Ui(() =>
    {
        title.Text = "Have fun!";
        detail.Text = "Stardew Valley is starting and will join the server by itself. You can leave this window minimized; it closes when you quit the game.";
        hint.Text = "";
        box.Visible = false;
        bar.Visible = false;
        ShowButtons();
        Snap();
        if (autopilot == null) WindowState = FormWindowState.Minimized;
    });

    public void Done(string text) => Ui(() =>
    {
        finished = true;
        Snap();
        Close();
    });

    // ---------- helpers ----------

    private Task<string?> Ask(Action show, string kind)
    {
        var tcs = new TaskCompletionSource<string?>();
        Ui(() =>
        {
            answer = tcs;
            show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (box.Visible) box.Focus(); else primary.Focus();
            Snap();
            if (autopilot != null)
                AutoAnswer(kind);
        });
        return tcs.Task;
    }

    private void AutoAnswer(string kind)
    {
        // Test only: answer like a player would, a moment after the screen appears.
        var t = new System.Windows.Forms.Timer { Interval = 600 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (kind == "invite") { box.Text = NextScripted(); primary.PerformClick(); }
            else if (kind == "confirm") primary.PerformClick();
            else if (kind == "ready" && farm != null && Environment.GetEnvironmentVariable("SV_LAUNCHER_FARM_TOUR") == "1") OpenFarm();
            else if (kind == "ready") primary.PerformClick();
            else Answer(null);
        };
        t.Start();
    }

    private int scripted;
    private string NextScripted()
    {
        string[] parts = autopilot!.Split('|');
        return parts[Math.Min(scripted++, parts.Length - 1)];
    }

    private void Answer(string? value)
    {
        var tcs = answer;
        answer = null;
        tcs?.TrySetResult(value);
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { Description = "Pick the Stardew Valley folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = dlg.SelectedPath;
    }

    private void ShowButtons(params (string text, bool? primaryButton)[] list)
    {
        primary.Visible = secondary.Visible = browse.Visible = false;
        foreach (var (text, kind) in list)
        {
            Button b = kind == true ? primary : kind == false ? secondary : browse;
            b.Text = text;
            b.Visible = true;
        }
        // Close with nothing else to do: make it the main button.
        Colors(secondary, !primary.Visible && secondary.Visible);
    }

    /// <summary>Size and font are set once (Windows scales them for big/high-DPI screens); only colors change later.</summary>
    private static void Style(Button b, bool main)
    {
        b.AutoSize = false;
        b.Size = new Size(main ? 140 : 120, 40);
        b.FlatStyle = FlatStyle.Flat;
        b.Font = new Font("Segoe UI Semibold", 10.5f);
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(10, 0, 0, 0);
        Colors(b, main);
    }

    private static void Colors(Button b, bool main)
    {
        b.FlatAppearance.BorderSize = main ? 0 : 1;
        b.FlatAppearance.BorderColor = Lilac;
        b.BackColor = main ? Lavender : Night;
        b.ForeColor = main ? Dusk : Ivory;
        b.FlatAppearance.MouseOverBackColor = main ? LavenderHi : Field;
        b.FlatAppearance.MouseDownBackColor = main ? Lilac : Dusk;
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    private void Snap()
    {
        if (shotsDir == null || !IsHandleCreated) return;
        try
        {
            Directory.CreateDirectory(shotsDir);
            using var bmp = new Bitmap(Width, Height);
            // The window's own composed frame (title bar included), not a desktop capture.
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
                if (!ok) DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            }
            bmp.Save(Path.Combine(shotsDir, $"{++shot:00}-{Slug(title.Text)}.png"));
        }
        catch { }
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>Dusk-purple header: the leaf-arch logo, the name, and a few small stars.</summary>
    private sealed class HeaderPanel : Panel
    {
        private readonly Image? mark = Brand.Mark();

        public HeaderPanel() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float k = DeviceDpi / 96f;
            g.Clear(Dusk);

            // a few quiet stars on the right
            (float x, float y, float s, bool gold)[] stars =
            {
                (0.70f, 0.22f, 3.0f, true), (0.78f, 0.55f, 1.6f, false), (0.85f, 0.18f, 1.8f, false),
                (0.90f, 0.48f, 3.6f, true), (0.95f, 0.26f, 1.4f, false), (0.64f, 0.62f, 1.4f, false),
            };
            foreach (var st in stars)
                Star(g, Width * st.x, Height * st.y, st.s * k, st.gold ? Gold : Color.FromArgb(170, Lilac));

            // gold rule along the bottom
            using (var rule = new SolidBrush(Gold))
                g.FillRectangle(rule, 0, Height - 3 * k, Width, 3 * k);

            float logo = 72 * k;
            if (mark != null)
                g.DrawImage(mark, 22 * k, (Height - 3 * k - logo) / 2, logo, logo);

            using var big = new Font("Segoe UI Semibold", 22f);
            using var small = new Font("Segoe UI", 12f);
            using var ivory = new SolidBrush(Ivory);
            using var lilac = new SolidBrush(Lilac);
            g.DrawString("Junimo Hollow", big, ivory, 104 * k, 18 * k);
            g.DrawString("A shared Stardew Valley world", small, lilac, 107 * k, 56 * k);
        }

        /// <summary>Four-point sparkle like the one inside the logo.</summary>
        private static void Star(Graphics g, float cx, float cy, float r, Color c)
        {
            float w = r * 0.32f;
            var pts = new[]
            {
                new PointF(cx, cy - r * 2), new PointF(cx + w, cy - w), new PointF(cx + r * 2, cy), new PointF(cx + w, cy + w),
                new PointF(cx, cy + r * 2), new PointF(cx - w, cy + w), new PointF(cx - r * 2, cy), new PointF(cx - w, cy - w),
            };
            using var b = new SolidBrush(c);
            g.FillPolygon(b, pts);
        }
    }

    /// <summary>Flat progress bar in the palette (the Windows one ignores colors). Null percent = busy shimmer.</summary>
    private sealed class ProgressStrip : Control
    {
        private readonly System.Windows.Forms.Timer tick = new() { Interval = 30 };
        private int? percent = 0;
        private float phase;
        public Color Track { get; set; } = Color.DimGray;
        public Color Fill { get; set; } = Color.White;

        public ProgressStrip()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            tick.Tick += (_, _) => { phase = (phase + 0.012f) % 1.4f; Invalidate(); };
        }

        public int? Percent
        {
            get => percent;
            set { percent = value; tick.Enabled = value == null && Visible; Invalidate(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            tick.Enabled = percent == null && Visible;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Night);
            float r = Height / 2f;
            using (var track = new SolidBrush(Track))
                Pill(g, track, 0, 0, Width, Height, r);
            using var fill = new SolidBrush(Fill);
            if (percent is int p)
            {
                if (p > 0) Pill(g, fill, 0, 0, Math.Max(Height, Width * p / 100f), Height, r);
            }
            else
            {
                float seg = Width * 0.3f;
                float x = (phase - 0.3f) * Width;
                var clip = g.Clip;
                g.SetClip(new RectangleF(0, 0, Width, Height));
                Pill(g, fill, x, 0, seg, Height, r);
                g.Clip = clip;
            }
        }

        private static void Pill(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            using var path = new GraphicsPath();
            path.AddArc(x, y, 2 * r, 2 * r, 90, 180);
            path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 180);
            path.CloseFigure();
            g.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tick.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Logo and icon, embedded in the exe.</summary>
internal static class Brand
{
    private static Stream? Res(string name) =>
        typeof(Brand).Assembly.GetManifestResourceStream("JunimoHollow." + name);

    public static Image? Mark()
    {
        using var s = Res("logo-mark.png");
        if (s == null) return null;
        using var img = Image.FromStream(s);
        return new Bitmap(img); // detach from the stream
    }

    public static Icon? AppIcon()
    {
        using var s = Res("app.ico");
        return s == null ? null : new Icon(s);
    }
}
