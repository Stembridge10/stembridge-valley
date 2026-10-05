using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace StembridgeValley.Launcher;

/// <summary>
/// "My farm": which of your two characters you're playing, who lives on its farm, and (for the owner) rename,
/// visitors and reset. Any member can make an invite code for a friend; a player who hasn't made a farmer yet can
/// type a friend's code to start on their farm instead.
/// Everything is asked of the server (FarmClient); this window only shows the answers.
/// </summary>
internal sealed class FarmWindow : Form
{
    private readonly FarmClient client;
    private readonly string? shotsDir;
    private readonly bool autopilot;
    private readonly Label heading = new(), sub = new(), members = new(), status = new();
    private readonly Label renameLabel = new(), visitLabel = new(), inviteLabel = new(), joinLabel = new(), charLabel = new(), resetLabel = new();
    private readonly TextBox nameBox = new(), joinBox = new(), inviteBox = new();
    private readonly Button saveName = new(), visitButton = new(), inviteButton = new(), copyButton = new(), joinButton = new(), close = new(), charButton = new(), resetButton = new();
    private JsonObject? info;
    private int shot;

    public FarmWindow(FarmClient client, string? shotsDir, bool autopilot)
    {
        this.client = client;
        this.shotsDir = shotsDir;
        this.autopilot = autopilot;
        Text = "My farm - Junimo Hollow";
        ClientSize = new Size(520, 570);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Night;
        ForeColor = Palette.Ivory;
        Font = new Font("Segoe UI", 10.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Brand.AppIcon(); } catch { }

        heading.SetBounds(28, 20, 464, 34);
        heading.Font = new Font("Segoe UI Semibold", 16f);
        heading.AutoEllipsis = true;
        sub.SetBounds(28, 56, 464, 22);
        sub.ForeColor = Palette.Lilac;
        members.SetBounds(28, 84, 464, 50);
        members.ForeColor = Palette.Ivory;

        int y = 146;
        charLabel.SetBounds(28, y, 336, 22);
        charLabel.ForeColor = Palette.Lilac;
        charLabel.AutoEllipsis = true;
        Theme.Secondary(charButton);
        charButton.SetBounds(372, y - 6, 120, 34);
        charButton.Click += async (_, _) => await Switch();
        y += 50;

        Row(renameLabel, "Farm name (owner only)", nameBox, saveName, "Save", ref y);
        nameBox.MaxLength = 25;
        saveName.Click += async (_, _) => await Do("rename", new JsonObject { ["name"] = nameBox.Text });

        visitLabel.SetBounds(28, y, 336, 22);
        visitLabel.ForeColor = Palette.Lilac;
        Theme.Secondary(visitButton);
        visitButton.SetBounds(372, y - 6, 120, 34);
        visitButton.Click += async (_, _) => await Do("visitors", new JsonObject { ["open"] = info?["farm"]?["open"]?.GetValue<bool>() != true });
        y += 50;

        Row(inviteLabel, "Invite a friend: they type this code in their launcher", inviteBox, inviteButton, "New code", ref y);
        inviteBox.ReadOnly = true;
        inviteBox.Font = new Font("Consolas", 13f, FontStyle.Bold);
        inviteBox.TextAlign = HorizontalAlignment.Center;
        inviteButton.Click += async (_, _) => await Do("invite", null);
        Theme.Secondary(copyButton);
        copyButton.Text = "Copy";
        copyButton.SetBounds(372, y - 6, 120, 34);
        copyButton.Click += (_, _) => CopyInvite();
        y += 44;

        Row(joinLabel, "Got a code from a friend? Start on their farm", joinBox, joinButton, "Join", ref y);
        joinBox.CharacterCasing = CharacterCasing.Upper;
        joinBox.MaxLength = 8;
        joinBox.Font = new Font("Consolas", 13f, FontStyle.Bold);
        joinButton.Click += async (_, _) => await Do("join", new JsonObject { ["invite"] = joinBox.Text });

        resetLabel.SetBounds(28, 446, 336, 44);
        resetLabel.ForeColor = Palette.Lilac;
        Theme.Secondary(resetButton);
        resetButton.Text = "Reset farm...";
        resetButton.FlatAppearance.BorderColor = Palette.Rose;
        resetButton.ForeColor = Palette.Rose;
        resetButton.SetBounds(372, 446, 120, 34);
        resetButton.Click += async (_, _) => await AskReset();

        status.SetBounds(28, 500, 330, 50);
        status.ForeColor = Palette.Gold;
        Theme.Primary(close);
        close.Text = "Done";
        close.SetBounds(372, 510, 120, 40);
        close.Click += (_, _) => Close();
        AcceptButton = null;
        CancelButton = close;

        Controls.AddRange(new Control[] { heading, sub, members, renameLabel, nameBox, saveName, visitLabel, visitButton,
            inviteLabel, inviteBox, inviteButton, copyButton, joinLabel, joinBox, joinButton, status, close,
            charLabel, charButton, resetLabel, resetButton });
        foreach (Control c in Controls) if (c is not Label) c.Visible = c == close;
        heading.Text = "Checking your farm...";
        Shown += async (_, _) =>
        {
            await Refresh();
            if (autopilot) await Tour();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitle(Handle);
    }

    private void Row(Label label, string text, TextBox box, Button button, string buttonText, ref int y)
    {
        label.Text = text;
        label.SetBounds(28, y, 464, 22);
        label.ForeColor = Palette.Lilac;
        Theme.Box(box);
        box.SetBounds(28, y + 26, 332, 32);
        Theme.Secondary(button);
        button.Text = buttonText;
        button.SetBounds(372, y + 24, 120, 34);
        y += 72;
    }

    private async Task Refresh(string? message = null)
    {
        info = await client.Ask("info");
        if (IsDisposed) return;
        Show(info);
        if (message != null) Say(message);
        Snap();
    }

    private async Task Do(string op, JsonObject? extra)
    {
        foreach (var b in new[] { saveName, visitButton, inviteButton, joinButton, charButton, resetButton }) b.Enabled = false;
        Say("One moment...");
        JsonObject r = await client.Ask(op, extra);
        foreach (var b in new[] { saveName, visitButton, inviteButton, joinButton, charButton, resetButton }) b.Enabled = true;
        if (r["ok"]?.GetValue<bool>() != true)
        {
            Say(r["error"]?.GetValue<string>() ?? "That didn't work.", problem: true);
            Snap();
            return;
        }
        string? msg = r["message"]?.GetValue<string>();
        if (r["invite"]?.GetValue<string>() is { } code)
            msg = $"New code {code}. It works for 7 days; making a new one cancels it.";
        await Refresh(msg);
    }

    private void Show(JsonObject r)
    {
        foreach (Control c in Controls) if (c is not Label) c.Visible = c == close;
        renameLabel.Visible = visitLabel.Visible = inviteLabel.Visible = joinLabel.Visible = resetLabel.Visible = false;
        if (r["ok"]?.GetValue<bool>() != true)
        {
            heading.Text = "Couldn't load your farm";
            sub.Text = "";
            members.Text = "";
            charLabel.Text = "";
            charButton.Visible = true; // can still switch back
            charButton.Text = client.Slot == 2 ? "Use 1st" : "Use 2nd";
            Say(r["error"]?.GetValue<string>() ?? "", problem: true);
            return;
        }
        string me = r["name"]?.GetValue<string>() ?? "";
        bool started = r["started"]?.GetValue<bool>() == true;
        var farm = r["farm"] as JsonObject;
        int slot = r["slot"]?.GetValue<int>() ?? client.Slot;
        string? other = r["otherName"]?.GetValue<string>();
        charButton.Visible = true;
        charButton.Enabled = true;
        charLabel.Text = (slot == 2 ? "2nd character: " : "1st character: ") + (started ? me : "not made yet");
        charButton.Text = slot == 2 ? "Use 1st" : "Use 2nd";
        if (other != null)
            charLabel.Text += $"  (other: {other})";

        if (!started)
        {
            heading.Text = r["joining"] is { } j ? $"You'll start on {j}" : farm != null ? $"You'll start on {farm["name"]}" : "Welcome!";
            sub.Text = "You haven't made your farmer yet.";
            members.Text = r["joining"] != null ? "Press Play and make your farmer to move in." : "Press Play to make your farmer, or join a friend's farm below.";
            joinLabel.Visible = joinBox.Visible = joinButton.Visible = true;
            ShiftJoinTo(196);
            return;
        }
        if (farm == null)
        {
            heading.Text = "No farm found";
            sub.Text = "Press Play and the server will sort it out.";
            return;
        }
        bool owner = farm["owner"]?.GetValue<bool>() == true;
        bool open = farm["open"]?.GetValue<bool>() == true;
        var list = farm["members"]!.AsArray();
        int size = farm["size"]?.GetValue<int>() ?? 4;
        heading.Text = farm["name"]!.GetValue<string>();
        sub.Text = $"{list.Count}/{size} farmers  ·  {(open ? "open to visitors" : "closed to visitors")}  ·  you're {(owner ? "the owner" : "a member")}";
        members.Text = "Lives here: " + string.Join(", ", list.Select(m =>
            m!["name"]!.GetValue<string>() + (m["you"]?.GetValue<bool>() == true ? " (you)" : "") + (m["started"]?.GetValue<bool>() == true ? "" : " (not started)")));

        renameLabel.Visible = nameBox.Visible = saveName.Visible = true;
        nameBox.Enabled = saveName.Enabled = owner;
        nameBox.Text = farm["named"]?.GetValue<bool>() == true ? farm["name"]!.GetValue<string>().Replace(" Farm", "") : "";
        renameLabel.Text = owner ? "Farm name" : "Farm name (only the owner can change it)";

        visitLabel.Visible = visitButton.Visible = true;
        visitLabel.Text = open ? "Open: visitors can look but not touch." : "Closed: only your farmers can come in.";
        visitButton.Text = open ? "Close farm" : "Open farm";
        visitButton.Enabled = owner;

        bool full = list.Count >= size;
        inviteLabel.Visible = inviteBox.Visible = inviteButton.Visible = copyButton.Visible = true;
        inviteLabel.Text = full ? "Your farm is full." : "Invite a friend: they type this code in their launcher";
        inviteBox.Text = farm["invite"]?.GetValue<string>() ?? "";
        inviteButton.Enabled = !full;
        copyButton.Visible = copyButton.Enabled = inviteBox.Text.Length > 0;
        inviteButton.Text = inviteBox.Text.Length > 0 ? "New code" : "Make code";

        resetLabel.Visible = resetButton.Visible = true;
        string? resetProblem = farm["resetProblem"]?.GetValue<string>();
        resetButton.Enabled = owner && resetProblem == null;
        resetLabel.Text = !owner ? "Only the owner can reset the farm."
            : resetProblem ?? "Start this farm over (once a week). Your farmer is deleted; the others keep theirs.";
        ShiftJoinTo(-1);
        if (status.ForeColor != Palette.Rose) Say("");
    }

    /// <summary>Before you've made a farmer, the join box is the only thing on the page: move it up.
    /// (Positions are in design pixels; Windows has already scaled the window for big screens, so scale them too.)</summary>
    private void ShiftJoinTo(int y)
    {
        float k = ClientSize.Width / 520f;
        int top = y < 0 ? 196 : y;
        joinLabel.Top = (int)(top * k);
        joinBox.Top = (int)((top + 26) * k);
        joinButton.Top = (int)((top + 24) * k);
        if (y < 0) joinLabel.Visible = joinBox.Visible = joinButton.Visible = false;
    }

    /// <summary>Swap to the other character and show its farm (the launcher remembers the choice for Play).</summary>
    private bool switching;

    private async Task Switch()
    {
        if (switching) return;
        switching = true;
        charButton.Enabled = false;
        try
        {
            int want = client.Slot == 2 ? 1 : 2;
            client.Slot = want;
            Say("One moment...");
            await Refresh(want == 2 ? "Switched to your 2nd character. Press Play to use it." : "Switched to your 1st character.");
        }
        finally
        {
            switching = false;
            if (!IsDisposed) charButton.Enabled = true;
        }
    }

    private async Task AskReset()
    {
        string name = info?["farm"]?["name"]?.GetValue<string>() ?? "";
        string me = info?["name"]?.GetValue<string>() ?? "your farmer";
        using var ask = new ResetConfirm(name, me);
        if (ask.ShowDialog(this) != DialogResult.OK)
            return;
        await Do("reset", new JsonObject { ["confirm"] = ask.Typed });
    }

    private void CopyInvite()
    {
        if (inviteBox.Text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(inviteBox.Text);
            Say("Copied. Send it to your friend.");
        }
        catch
        {
            Say("Couldn't copy automatically. Select the code and press Ctrl+C.", problem: true);
        }
    }

    private void Say(string text, bool problem = false)
    {
        status.ForeColor = problem ? Palette.Rose : Palette.Gold;
        status.Text = text;
    }

    // ---------- automated test ----------

    /// <summary>Test only (SV_LAUNCHER_FARM_TOUR=1): try the buttons like a player would, with pictures, then close.</summary>
    private async Task Tour()
    {
        async Task Step(Func<bool> can, string op, Func<JsonObject?> extra, Action before)
        {
            await Task.Delay(900);
            if (IsDisposed || !can()) return;
            before();
            await Do(op, extra());
        }
        await Step(() => nameBox.Visible && nameBox.Enabled, "rename", () => new JsonObject { ["name"] = nameBox.Text }, () => nameBox.Text = "<oops>");
        await Step(() => nameBox.Visible && nameBox.Enabled, "rename", () => new JsonObject { ["name"] = nameBox.Text }, () => nameBox.Text = "Moonpetal");
        await Step(() => inviteButton.Visible && inviteButton.Enabled, "invite", () => null, () => { });
        if (copyButton.Visible && copyButton.Enabled)
        {
            copyButton.PerformClick();
            await Task.Delay(300);
            if (!string.Equals(Clipboard.GetText(), inviteBox.Text, StringComparison.Ordinal))
                throw new InvalidOperationException("The Copy button didn't put the invite code on the clipboard.");
            Snap();
        }
        await Step(() => visitButton.Visible && visitButton.Enabled, "visitors", () => new JsonObject { ["open"] = false }, () => { });
        await Step(() => visitButton.Visible && visitButton.Enabled, "visitors", () => new JsonObject { ["open"] = true }, () => { });
        if (resetButton.Visible && shotsDir != null)
        {
            await Task.Delay(600);
            using var ask = new ResetConfirm(heading.Text, info?["name"]?.GetValue<string>() ?? "");
            ask.StartPosition = FormStartPosition.Manual;
            ask.Location = new Point(Left + 20, Top + 40);
            ask.Show(this);
            await Task.Delay(400);
            ask.TypeForTest(heading.Text);
            await Task.Delay(400);
            SnapOf(ask, "reset-confirm");
            ask.Close();
        }
        // Test only: the reset step must name the exact test server, and never the live server's port.
        if (Environment.GetEnvironmentVariable("SV_LAUNCHER_TOUR_RESET") is { Length: > 0 } resetServer
            && resetServer == client.Address && !client.Address.EndsWith(":24642", StringComparison.Ordinal))
        {
            await Step(() => resetButton.Visible, "reset", () => new JsonObject { ["confirm"] = "Wrong Farm" }, () => { });
            await Step(() => resetButton.Visible && resetButton.Enabled, "reset", () => new JsonObject { ["confirm"] = heading.Text }, () => { });
        }
        if (Environment.GetEnvironmentVariable("SV_LAUNCHER_TOUR_SWITCH") == "1")
        {
            await Task.Delay(900);
            if (!IsDisposed) await Switch();
        }
        await Step(() => joinBox.Visible, "join", () => new JsonObject { ["invite"] = joinBox.Text }, () => joinBox.Text = "ZZZZZZ");
        if (Environment.GetEnvironmentVariable("SV_LAUNCHER_JOIN_CODE") is { Length: > 0 } good)
            await Step(() => joinBox.Visible, "join", () => new JsonObject { ["invite"] = joinBox.Text }, () => joinBox.Text = good);
        await Task.Delay(900);
        if (!IsDisposed) Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private void Snap() => SnapOf(this, $"farm-{++shot:00}");

    private void SnapOf(Form form, string name)
    {
        if (shotsDir == null || !form.IsHandleCreated) return;
        try
        {
            Application.DoEvents(); // let freshly shown buttons paint before the picture
            foreach (Control c in form.Controls) c.Update();
            Directory.CreateDirectory(shotsDir);
            using var bmp = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(form.Handle, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!ok) form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            }
            bmp.Save(Path.Combine(shotsDir, name + ".png"));
        }
        catch { }
    }
}
