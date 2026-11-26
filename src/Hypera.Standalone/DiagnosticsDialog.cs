using System.Drawing;
using System.Windows.Forms;

namespace Hypera.Standalone;

/// <summary>
/// Scrollable monospace dialog used to display batch compiler diagnostics, runtime faults with
/// ERB call stacks, <c>DEBUGPRINT</c> logs, and runtime package telemetry.
/// </summary>
public sealed class DiagnosticsDialog : Form
{
    public DiagnosticsDialog(
        string title,
        string bodyText,
        bool showRestartButton = false)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 560);
        MinimumSize = new Size(520, 320);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(230, 232, 236);

        var textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(16, 18, 22),
            ForeColor = Color.FromArgb(228, 232, 238),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f, FontStyle.Regular),
            Text = bodyText,
            SelectionStart = 0,
            SelectionLength = 0,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8, 7, 8, 7),
            BackColor = Color.FromArgb(28, 30, 36),
        };

        var closeBtn = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Width = 96,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 56, 66),
            ForeColor = Color.White,
        };

        var copyBtn = new Button
        {
            Text = "Copy All",
            Width = 96,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 96, 128),
            ForeColor = Color.White,
        };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(bodyText);
                copyBtn.Text = "Copied!";
            }
            catch
            {
                // Ignore clipboard lock contention.
            }
        };

        buttonPanel.Controls.Add(closeBtn);
        buttonPanel.Controls.Add(copyBtn);

        if (showRestartButton)
        {
            var restartBtn = new Button
            {
                Text = "Restart Game",
                DialogResult = DialogResult.Retry,
                Width = 116,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(46, 125, 86),
                ForeColor = Color.White,
            };
            buttonPanel.Controls.Add(restartBtn);
        }

        Controls.Add(textBox);
        Controls.Add(buttonPanel);
        CancelButton = closeBtn;
    }
}
