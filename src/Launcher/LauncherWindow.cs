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
    private static readonly Color Cream = Color.FromArgb(255, 248, 231);
    private static readonly Color Leaf = Color.FromArgb(76, 140, 58);
    private static readonly Color LeafDark = Color.FromArgb(52, 104, 40);
    private static readonly Color Wood = Color.FromArgb(120, 78, 44);
    private static readonly Color Ink = Color.FromArgb(58, 44, 30);
    private static readonly Color Soft = Color.FromArgb(128, 108, 86);
    private static readonly Color Berry = Color.FromArgb(176, 64, 52);

    private readonly Label title = new(), detail = new(), hint = new();
    private readonly ProgressBar bar = new();
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
        BackColor = Cream;
        Font = new Font("Segoe UI", 10.5f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var header = new HeaderPanel { Dock = DockStyle.Top, Height = 104 };
        Controls.Add(header);

        title.SetBounds(36, 128, 488, 34);
        title.Font = new Font("Segoe UI Semibold", 15f);
        title.ForeColor = Ink;
        title.AutoEllipsis = true;

        detail.SetBounds(36, 166, 488, 64);
        detail.ForeColor = Soft;

        bar.SetBounds(36, 238, 488, 14);
        bar.Style = ProgressBarStyle.Continuous;
        bar.Visible = false;

        box.SetBounds(36, 236, 488, 30);
        box.Font = new Font("Consolas", 11f);
        box.Visible = false;
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; primary.PerformClick(); }
        };

        hint.SetBounds(36, 270, 488, 22);
        hint.ForeColor = Berry;
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
        browse.Click += (_, _) => Browse();

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
            bar.Style = p < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (p >= 0) bar.Value = Math.Clamp(p, 0, 100);
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
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 180, 150);
        b.BackColor = main ? Leaf : Color.White;
        b.ForeColor = main ? Color.White : Ink;
        b.FlatAppearance.MouseOverBackColor = main ? LeafDark : Color.FromArgb(250, 240, 220);
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
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(shotsDir, $"{++shot:00}-{Slug(title.Text)}.png"));
        }
        catch { }
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>Green header with the name and a little row of hand-drawn sprouts.</summary>
    private sealed class HeaderPanel : Panel
    {
        public HeaderPanel() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var sky = new LinearGradientBrush(ClientRectangle, Color.FromArgb(96, 166, 72), Leaf, LinearGradientMode.Vertical))
                g.FillRectangle(sky, ClientRectangle);
            // hills
            using (var hill = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
            {
                g.FillEllipse(hill, -60, 62, 260, 120);
                g.FillEllipse(hill, 330, 70, 300, 120);
            }
            // a wooden strip along the bottom
            using (var wood = new SolidBrush(Wood))
                g.FillRectangle(wood, 0, Height - 8, Width, 8);
            // sprouts
            for (int i = 0; i < 6; i++)
                Sprout(g, Width - 200 + i * 30, Height - 14, i % 2 == 0 ? 1f : 0.8f);

            using var big = new Font("Segoe UI Semibold", 24f);
            using var small = new Font("Segoe UI", 12f);
            g.DrawString("Junimo Hollow", big, Brushes.White, 30, 14 * DeviceDpi / 96f);
            using var soft = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            g.DrawString("A shared Stardew Valley world", small, soft, 33, 60 * DeviceDpi / 96f);
        }

        private static void Sprout(Graphics g, float x, float y, float s)
        {
            using var stem = new Pen(Color.FromArgb(230, 255, 250, 210), 2.5f * s);
            using var leaf = new SolidBrush(Color.FromArgb(235, 255, 250, 210));
            g.DrawLine(stem, x, y, x, y - 16 * s);
            g.FillEllipse(leaf, x - 11 * s, y - 22 * s, 11 * s, 7 * s);
            g.FillEllipse(leaf, x, y - 26 * s, 11 * s, 7 * s);
        }
    }
}
