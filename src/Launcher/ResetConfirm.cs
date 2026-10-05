using System.Drawing;
using System.Windows.Forms;

namespace StembridgeValley.Launcher;

/// <summary>"Are you sure?" for a farm reset: says exactly what goes, and only goes ahead once the farm's name is typed.</summary>
internal sealed class ResetConfirm : Form
{
    private readonly TextBox box = new();
    public string Typed => box.Text.Trim();

    public ResetConfirm(string farmName, string farmerName)
    {
        Text = "Reset farm - Junimo Hollow";
        ClientSize = new Size(480, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Night;
        ForeColor = Palette.Ivory;
        Font = new Font("Segoe UI", 10.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Brand.AppIcon(); } catch { }

        var head = new Label { Text = $"Reset {farmName}?", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Palette.Rose, AutoEllipsis = true };
        head.SetBounds(24, 18, 432, 32);
        var text = new Label
        {
            Text = $"Your farmer {farmerName} is deleted, with everything they carry. The farm starts over as fresh land: "
                 + "crops, buildings, chests, projects and the quarry all go.\n\n"
                 + "Anyone else on the farm moves to a farm of their own and keeps everything.\n"
                 + "You can do this once a week. The server keeps a backup.",
            ForeColor = Palette.Ivory,
        };
        text.SetBounds(24, 56, 432, 140);
        var ask = new Label { Text = $"Type {farmName} to confirm:", ForeColor = Palette.Lilac };
        ask.SetBounds(24, 200, 432, 22);
        Theme.Box(box);
        box.SetBounds(24, 226, 432, 32);
        var yes = new Button { Text = "Reset farm", Enabled = false };
        Theme.Primary(yes);
        yes.BackColor = Palette.Rose;
        yes.SetBounds(216, 276, 120, 38);
        yes.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var no = new Button { Text = "Cancel" };
        Theme.Secondary(no);
        no.SetBounds(344, 276, 112, 38);
        no.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        box.TextChanged += (_, _) => yes.Enabled = string.Equals(Typed, farmName, StringComparison.OrdinalIgnoreCase);
        CancelButton = no;
        Controls.AddRange(new Control[] { head, text, ask, box, yes, no });
    }

    /// <summary>Automated test pictures only.</summary>
    public void TypeForTest(string text) => box.Text = text;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitle(Handle);
    }
}
