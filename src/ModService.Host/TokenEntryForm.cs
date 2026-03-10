using System.Drawing;
using System.Windows.Forms;

namespace ModService.Host;

public sealed class TokenEntryForm : Form
{
    private readonly TextBox _tokenTextBox;

    public TokenEntryForm()
    {
        Text = "Set GitHub Token";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 150);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Paste a GitHub personal access token. It will be stored in ProgramData for the elevated app."
        }, 0, 0);

        _tokenTextBox = new TextBox
        {
            Dock = DockStyle.Top,
            UseSystemPasswordChar = true
        };
        root.Controls.Add(_tokenTextBox, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft
        };

        var okButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "Save"
        };
        okButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(Token))
            {
                return;
            }

            DialogResult = DialogResult.None;
            MessageBox.Show(
                this,
                "GitHub token cannot be empty.",
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        var cancelButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Cancel"
        };

        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public string Token => _tokenTextBox.Text.Trim();
}
