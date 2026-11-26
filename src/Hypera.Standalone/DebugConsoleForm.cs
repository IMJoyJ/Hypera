using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Hypera.Runtime;

namespace Hypera.Standalone;

/// <summary>
/// Live, modeless Debug Console window (shortcut <c>F12</c>) displaying real-time engine diagnostics
/// (<c>[INIT]</c>, <c>[FONT]</c>, <c>[VFS]</c>, <c>[GFX]</c>, <c>[FAULT]</c>, <c>[DEBUGPRINT]</c>, <c>[EVAL]</c>),
/// live telemetry, category/text filtering, and an interactive ERB expression evaluator.
/// </summary>
public sealed class DebugConsoleForm : Form
{
    private readonly Label _telemetryLabel;
    private readonly ComboBox _categoryCombo;
    private readonly TextBox _filterTextBox;
    private readonly TextBox _logTextBox;
    private readonly TextBox _evalTextBox;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly List<EngineDebugLogEntry> _entries = [];
    private readonly List<string> _evalHistory = [];

    private StandaloneGameSession? _session;
    private int _evalHistoryCursor = -1;

    public DebugConsoleForm()
    {
        Text = "Hypera Debug Console";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(920, 560);
        MinimumSize = new Size(560, 340);
        BackColor = Color.FromArgb(22, 24, 28);
        ForeColor = Color.FromArgb(228, 232, 238);
        ShowInTaskbar = true;

        // 1. Top live telemetry header
        _telemetryLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(30, 34, 42),
            ForeColor = Color.FromArgb(142, 208, 236),
            Font = new Font("Consolas", 9f, FontStyle.Regular),
            Text = "No active game session.",
        };

        // 2. Filter & action toolbar
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(6, 5, 6, 4),
            BackColor = Color.FromArgb(26, 28, 34),
            WrapContents = false,
        };

        var catLabel = new Label
        {
            Text = "Category:",
            AutoSize = true,
            Padding = new Padding(2, 5, 2, 0),
            ForeColor = Color.FromArgb(190, 196, 208),
        };

        _categoryCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 128,
            BackColor = Color.FromArgb(36, 40, 48),
            ForeColor = Color.White,
        };
        _categoryCombo.Items.AddRange(["ALL", "DEBUGPRINT", "FONT", "VFS", "GFX", "IO", "INIT", "FAULT", "EVAL"]);
        _categoryCombo.SelectedIndex = 0;
        _categoryCombo.SelectedIndexChanged += (_, _) => RebuildLogDisplay();

        var filterLabel = new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Padding = new Padding(8, 5, 2, 0),
            ForeColor = Color.FromArgb(190, 196, 208),
        };

        _filterTextBox = new TextBox
        {
            Width = 220,
            BackColor = Color.FromArgb(16, 18, 22),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _filterTextBox.TextChanged += (_, _) => RebuildLogDisplay();

        var copyBtn = CreateToolButton("Copy All", Color.FromArgb(44, 96, 128));
        copyBtn.Click += OnCopyAllClick;

        var saveBtn = CreateToolButton("Save...", Color.FromArgb(52, 72, 92));
        saveBtn.Click += OnSaveLogClick;

        var clearBtn = CreateToolButton("Clear", Color.FromArgb(92, 54, 54));
        clearBtn.Click += OnClearLogClick;

        toolbar.Controls.AddRange([catLabel, _categoryCombo, filterLabel, _filterTextBox, copyBtn, saveBtn, clearBtn]);

        // 3. Center scrollable monospace log output
        _logTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(14, 16, 20),
            ForeColor = Color.FromArgb(224, 228, 236),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f, FontStyle.Regular),
        };

        // 4. Bottom interactive ERB expression evaluator bar
        var evalPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            Padding = new Padding(6, 4, 6, 4),
            BackColor = Color.FromArgb(26, 28, 34),
        };

        var evalPrompt = new Label
        {
            Dock = DockStyle.Left,
            Width = 56,
            Text = "Eval >",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(118, 220, 164),
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
        };

        var evalBtn = new Button
        {
            Dock = DockStyle.Right,
            Width = 68,
            Text = "Run",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(46, 118, 84),
            ForeColor = Color.White,
        };
        evalBtn.Click += (_, _) => ExecuteEvalExpression();

        _evalTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(14, 16, 20),
            ForeColor = Color.FromArgb(236, 240, 246),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f, FontStyle.Regular),
            PlaceholderText = "Inspect variable or expression (e.g. CHKFONT(\"xonFont\"), CHARANUM, CFLAG:MASTER:0, NAME:MASTER)",
        };
        _evalTextBox.KeyDown += OnEvalKeyDown;

        evalPanel.Controls.Add(_evalTextBox);
        evalPanel.Controls.Add(evalPrompt);
        evalPanel.Controls.Add(evalBtn);

        Controls.Add(_logTextBox);
        Controls.Add(evalPanel);
        Controls.Add(toolbar);
        Controls.Add(_telemetryLabel);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _refreshTimer.Tick += (_, _) => UpdateTelemetry();
        _refreshTimer.Start();
    }

    public void AttachSession(StandaloneGameSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.Engine.DebugEntryLogged -= OnEngineDebugEntryLogged;
        }

        _session = session;
        _entries.Clear();

        if (_session is not null)
        {
            _entries.AddRange(_session.Engine.TraceLog);
            _session.Engine.DebugEntryLogged += OnEngineDebugEntryLogged;
        }

        RebuildLogDisplay();
        UpdateTelemetry();
    }

    private void OnEngineDebugEntryLogged(EngineDebugLogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => AppendEntryOnUiThread(entry));
            }
            catch
            {
                // Window closing concurrently.
            }

            return;
        }

        AppendEntryOnUiThread(entry);
    }

    private void AppendEntryOnUiThread(EngineDebugLogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_entries.Count >= 4000)
        {
            _entries.RemoveRange(0, 1000);
            _entries.Add(entry);
            RebuildLogDisplay();
            return;
        }

        _entries.Add(entry);
        if (MatchesFilter(entry))
        {
            _logTextBox.AppendText(entry.FormatLine() + Environment.NewLine);
        }
    }

    private bool MatchesFilter(EngineDebugLogEntry entry)
    {
        string selectedCat = _categoryCombo.SelectedItem?.ToString() ?? "ALL";
        if (!selectedCat.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            !entry.Category.Equals(selectedCat, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string needle = _filterTextBox.Text.Trim();
        if (needle.Length > 0 &&
            !entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
            !(entry.SourceLocation?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        return true;
    }

    private void RebuildLogDisplay()
    {
        var sb = new StringBuilder(_entries.Count * 80);
        foreach (EngineDebugLogEntry entry in _entries)
        {
            if (MatchesFilter(entry))
            {
                sb.AppendLine(entry.FormatLine());
            }
        }

        _logTextBox.Text = sb.ToString();
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void UpdateTelemetry()
    {
        if (_session is null)
        {
            _telemetryLabel.Text = "No active game session.";
            return;
        }

        EmueraEngine e = _session.Engine;
        string topFrame = e.CallStack.Count > 0 ? $"@{e.CallStack[^1].FunctionName}" : "(idle)";
        int charCount = e.Characters?.Count ?? 0;
        _telemetryLabel.Text =
            $"Status: {e.Status} | Frame: {topFrame} | Fonts: {e.Fonts.CustomFontCount} aliases | Sprites: {e.Sprites.Count} | Charas: {charCount} | Lines: {e.ConsoleBuffer.LineCount} | Log: {_entries.Count}";
    }

    private void OnEvalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            ExecuteEvalExpression();
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Up && _evalHistory.Count > 0)
        {
            _evalHistoryCursor = _evalHistoryCursor < 0 ? _evalHistory.Count - 1 : Math.Max(0, _evalHistoryCursor - 1);
            _evalTextBox.Text = _evalHistory[_evalHistoryCursor];
            _evalTextBox.SelectionStart = _evalTextBox.TextLength;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down && _evalHistory.Count > 0 && _evalHistoryCursor >= 0)
        {
            _evalHistoryCursor++;
            if (_evalHistoryCursor >= _evalHistory.Count)
            {
                _evalHistoryCursor = -1;
                _evalTextBox.Clear();
            }
            else
            {
                _evalTextBox.Text = _evalHistory[_evalHistoryCursor];
                _evalTextBox.SelectionStart = _evalTextBox.TextLength;
            }

            e.Handled = true;
        }
    }

    private void ExecuteEvalExpression()
    {
        string expr = _evalTextBox.Text.Trim();
        if (expr.Length == 0 || _session is null)
        {
            return;
        }

        if (_evalHistory.Count == 0 || !string.Equals(_evalHistory[^1], expr, StringComparison.Ordinal))
        {
            _evalHistory.Add(expr);
        }

        _evalHistoryCursor = -1;
        _evalTextBox.Clear();

        bool ok = _session.Engine.TryEvaluateDebugExpression(expr, out string result);
        _session.Engine.RecordDiagnostic(
            "EVAL",
            ok ? $"{expr}  =>  {result}" : $"{expr}  =>  {result}",
            includeCallerLocation: false);
    }

    private void OnCopyAllClick(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_logTextBox.Text))
        {
            try { Clipboard.SetText(_logTextBox.Text); } catch { }
        }
    }

    private void OnClearLogClick(object? sender, EventArgs e)
    {
        _session?.Engine.ClearDebugLog();
        _entries.Clear();
        _logTextBox.Clear();
    }

    private void OnSaveLogClick(object? sender, EventArgs e)
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Save Debug Console Log",
            Filter = "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
            FileName = "hypera_debug.log",
        };

        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(sfd.FileName, _logTextBox.Text, Encoding.UTF8);
        }
    }

    private static Button CreateToolButton(string text, Color backColor) =>
        new()
        {
            Text = text,
            Width = 74,
            Height = 24,
            Margin = new Padding(6, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
        };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Hide instead of destroying so F12 re-opens cleanly at the same screen location.
            e.Cancel = true;
            Hide();
            return;
        }

        DetachAndCleanup();
        base.OnFormClosing(e);
    }

    public void DetachAndCleanup()
    {
        _refreshTimer.Stop();
        if (_session is not null)
        {
            _session.Engine.DebugEntryLogged -= OnEngineDebugEntryLogged;
            _session = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachAndCleanup();
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
