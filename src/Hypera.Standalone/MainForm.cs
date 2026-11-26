using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Views.WinForms;

namespace Hypera.Standalone;

/// <summary>
/// Classic Emuera-inspired standalone desktop window combining a menu bar, full-fidelity Skia
/// <see cref="HyperaSkiaControl"/> viewport, synchronized scrollback bar, and bottom command input bar.
/// </summary>
public sealed class MainForm : Form
{
    private readonly StandaloneLaunchOptions _initialOptions;
    private readonly MenuStrip _menuStrip;
    private readonly ToolStripMenuItem _muteMenuItem;
    private readonly ToolStripMenuItem _fullscreenMenuItem;
    private readonly Panel _viewportHostPanel;
    private readonly HyperaSkiaControl _skiaView;
    private readonly VScrollBar _scrollBar;
    private readonly Panel _inputBarPanel;
    private readonly TextBox _inputTextBox;
    private readonly Label _statusLabel;
    private readonly List<string> _inputHistory = [];

    private StandaloneGameSession? _session;
    private DebugConsoleForm? _debugConsoleForm;
    private Icon? _customWindowIcon;
    private int _historyCursor = -1;
    private bool _syncingScrollBar;
    private bool _syncingTextBox;
    private bool _isFullscreen;
    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private Rectangle _savedBounds;
    private bool _menuWasVisibleBeforeFullscreen;

    public MainForm(StandaloneLaunchOptions initialOptions)
    {
        _initialOptions = initialOptions;

        Text = "Hypera Standalone";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        ForeColor = Color.FromArgb(192, 192, 192);
        KeyPreview = true;
        AllowDrop = true;
        MinimumSize = new Size(480, 360);
        Size = new Size(1024, 768);

        // 1. Top MenuStrip (classic Emuera menu layout)
        _menuStrip = new MenuStrip
        {
            Dock = DockStyle.Top,
            RenderMode = ToolStripRenderMode.System,
        };

        var fileMenu = new ToolStripMenuItem("&File");
        var openPackageItem = new ToolStripMenuItem("Open Package / Binary (.hep, .heb, .zip)...", null, OnOpenPackageClick)
        {
            ShortcutKeys = Keys.Control | Keys.O,
        };
        var openFolderItem = new ToolStripMenuItem("Open Game Folder...", null, OnOpenFolderClick)
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.O,
        };
        var restartItem = new ToolStripMenuItem("&Restart Game (BEGIN TITLE)", null, (_, _) => RestartCurrentTarget())
        {
            ShortcutKeys = Keys.Control | Keys.R,
        };
        var saveLogItem = new ToolStripMenuItem("&Save Console Log...", null, OnSaveLogClick)
        {
            ShortcutKeys = Keys.Control | Keys.S,
        };
        var openSaveDirItem = new ToolStripMenuItem("Open Save Directory", null, OnOpenSaveDirectoryClick);
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        fileMenu.DropDownItems.AddRange(
        [
            openPackageItem,
            openFolderItem,
            new ToolStripSeparator(),
            restartItem,
            saveLogItem,
            openSaveDirItem,
            new ToolStripSeparator(),
            exitItem,
        ]);

        var editMenu = new ToolStripMenuItem("&Edit");
        var copyLogItem = new ToolStripMenuItem("&Copy Console Text", null, OnCopyConsoleTextClick)
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.C,
        };
        var clearInputItem = new ToolStripMenuItem("Clear Input Box", null, OnClearInputClick);
        editMenu.DropDownItems.AddRange([copyLogItem, clearInputItem]);

        var viewMenu = new ToolStripMenuItem("&View");
        var scrollBottomItem = new ToolStripMenuItem("Scroll to Bottom", null, OnScrollToBottomClick)
        {
            ShortcutKeys = Keys.Control | Keys.End,
        };
        var resetSizeItem = new ToolStripMenuItem("Fit Window to Logical Viewport (1:1)", null, (_, _) => FitWindowToLogicalViewport());
        _fullscreenMenuItem = new ToolStripMenuItem("&Fullscreen", null, (_, _) => ToggleFullscreen())
        {
            ShortcutKeys = Keys.F11,
            CheckOnClick = false,
        };
        _muteMenuItem = new ToolStripMenuItem("&Mute Audio", null, (_, _) => ToggleMute())
        {
            ShortcutKeys = Keys.Control | Keys.M,
            CheckOnClick = false,
        };
        viewMenu.DropDownItems.AddRange(
        [
            scrollBottomItem,
            resetSizeItem,
            _fullscreenMenuItem,
            new ToolStripSeparator(),
            _muteMenuItem,
        ]);

        var debugMenu = new ToolStripMenuItem("&Debug");
        var debugLogItem = new ToolStripMenuItem("Open &Debug Console...", null, OnShowDebugLogClick)
        {
            ShortcutKeys = Keys.F12,
        };
        var runtimeInfoItem = new ToolStripMenuItem("Runtime & Package Info...", null, OnShowRuntimeInfoClick);
        debugMenu.DropDownItems.AddRange([debugLogItem, runtimeInfoItem]);

        var helpMenu = new ToolStripMenuItem("&Help");
        var aboutItem = new ToolStripMenuItem("About Hypera Standalone", null, OnShowAboutClick);
        helpMenu.DropDownItems.Add(aboutItem);

        _menuStrip.Items.AddRange([fileMenu, editMenu, viewMenu, debugMenu, helpMenu]);

        // 2. Center Viewport Panel: Skia canvas + right-edge scrollback bar
        _viewportHostPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };

        _scrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            Minimum = 0,
            Maximum = 0,
            LargeChange = 10,
            SmallChange = 1,
            Enabled = false,
        };
        _scrollBar.ValueChanged += OnScrollBarValueChanged;

        _skiaView = new HyperaSkiaControl
        {
            Dock = DockStyle.Fill,
            AutoSubmitEngineInput = true,
            BackColor = Color.Black,
        };
        _skiaView.ScrollbackChanged += (_, _) => SyncScrollBarFromView();
        _skiaView.ContentUpdated += (_, _) => OnEngineContentUpdated();
        _skiaView.EngineFaulted += OnEngineFaulted;
        _skiaView.EngineRestartRequested += (_, _) => BeginInvoke(RestartCurrentTarget);
        _skiaView.EngineCompleted += (_, _) => BeginInvoke(Close);

        _viewportHostPanel.Controls.Add(_skiaView);
        _viewportHostPanel.Controls.Add(_scrollBar);

        // 3. Bottom Emuera Input Bar
        _inputBarPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Padding = new Padding(4, 3, 4, 3),
            BackColor = Color.FromArgb(18, 18, 22),
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = false,
            Width = 170,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(148, 160, 176),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Text = "Ready",
        };

        _inputTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(12, 12, 16),
            ForeColor = Color.FromArgb(224, 224, 224),
            Font = new Font("MS Gothic", 10.5f, FontStyle.Regular),
            TabIndex = 0,
        };
        _inputTextBox.KeyDown += OnInputTextBoxKeyDown;
        _inputTextBox.KeyPress += OnInputTextBoxKeyPress;
        _inputTextBox.TextChanged += OnInputTextBoxTextChanged;

        _inputBarPanel.Controls.Add(_inputTextBox);
        _inputBarPanel.Controls.Add(_statusLabel);

        // Dock order matters in WinForms: Fill added first, then Top/Bottom
        Controls.Add(_viewportHostPanel);
        Controls.Add(_inputBarPanel);
        Controls.Add(_menuStrip);
        MainMenuStrip = _menuStrip;

        Load += OnFormLoad;
        Activated += (_, _) => { if (_session is not null) _session.Engine.IsWindowActive = true; };
        Deactivate += (_, _) => { if (_session is not null) _session.Engine.IsWindowActive = false; };
        KeyDown += OnGlobalKeyDown;
        KeyUp += OnGlobalKeyUp;
        DragEnter += OnFormDragEnter;
        DragDrop += OnFormDragDrop;
        FormClosing += OnFormClosing;
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        if (_initialOptions.Target.Kind != StandaloneTargetKind.None)
        {
            LoadTarget(_initialOptions.Target, _initialOptions.IsDebugMode);
        }
        else
        {
            _statusLabel.Text = "No game loaded (Ctrl+O)";
            PromptOpenInitialGame();
        }
    }

    private void PromptOpenInitialGame()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Open Hypera Game Package (.hep / .heb / .zip)",
            Filter = "Hypera Packages (*.hep;*.heb;*.zip)|*.hep;*.heb;*.zip|All Files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            StandaloneLaunchTarget target = StandaloneLaunchResolver.ResolveExplicitPath(
                ofd.FileName,
                explicitSaveDir: null,
                useOverlay: _initialOptions.UseOverlay);
            LoadTarget(target, _initialOptions.IsDebugMode);
        }
    }

    private void LoadTarget(StandaloneLaunchTarget target, bool isDebugMode)
    {
        _skiaView.StopLoop();
        _skiaView.Engine = null;

        StandaloneGameSession? previous = _session;
        _session = null;
        previous?.Dispose();

        _statusLabel.Text = "Loading...";
        Cursor = Cursors.WaitCursor;
        Application.DoEvents();

        try
        {
            StandaloneGameSession session = StandaloneGameSession.Load(
                target,
                isDebugMode: isDebugMode,
                enableHardwareAudio: true);

            _session = session;
            _debugConsoleForm?.AttachSession(session);
            ApplySessionWindowSettings(session);

            _skiaView.Engine = session.Engine;
            _skiaView.StartLoop();
            _inputTextBox.Clear();
            _inputTextBox.Focus();
            OnEngineContentUpdated();
        }
        catch (EmueraCompileException compileEx)
        {
            string report = StandaloneGameSession.FormatCompileDiagnostics(compileEx);
            _statusLabel.Text = "Compilation failed";
            using var dlg = new DiagnosticsDialog("Hypera Compiler Diagnostics", report);
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Load failed";
            using var dlg = new DiagnosticsDialog("Failed to Load Game", ex.ToString());
            dlg.ShowDialog(this);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ApplySessionWindowSettings(StandaloneGameSession session)
    {
        Text = session.GetEffectiveWindowTitle();

        // Custom icon (e.g. resources/cow.webp in eraOCG2)
        Icon? nextIcon = session.TryLoadWindowIcon();
        if (nextIcon is not null)
        {
            Icon? old = _customWindowIcon;
            _customWindowIcon = nextIcon;
            Icon = nextIcon;
            old?.Dispose();
        }

        bool useMenu = session.GetConfigBool(ConfigCode.UseMenu, true);
        _menuStrip.Visible = useMenu;

        bool sizable = session.GetConfigBool(ConfigCode.SizableWindow, true);
        FormBorderStyle = sizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
        MaximizeBox = sizable;

        // Style bottom input box to blend with the game's palette and typography
        uint bgRgba = session.Engine.Options.BackColorRgba;
        uint fgRgba = session.Engine.Options.ForeColorRgba;
        Color consoleBg = Color.FromArgb((int)((bgRgba >> 24) & 0xFF), (int)((bgRgba >> 16) & 0xFF), (int)((bgRgba >> 8) & 0xFF));
        Color consoleFg = Color.FromArgb((int)((fgRgba >> 24) & 0xFF), (int)((fgRgba >> 16) & 0xFF), (int)((fgRgba >> 8) & 0xFF));
        _viewportHostPanel.BackColor = consoleBg;
        _skiaView.BackColor = consoleBg;
        _inputTextBox.BackColor = consoleBg;
        _inputTextBox.ForeColor = consoleFg;

        try
        {
            string fontFamily = string.IsNullOrWhiteSpace(session.Engine.Options.FontName)
                ? "MS Gothic"
                : session.Engine.Options.FontName;
            _inputTextBox.Font = new Font(fontFamily, 10.5f, FontStyle.Regular);
        }
        catch
        {
            // Fall back to default font if system font isn't installed for GDI.
        }

        if (!_isFullscreen)
        {
            FitWindowToLogicalViewport();

            bool setPos = session.GetConfigBool(ConfigCode.SetWindowPos, false);
            if (setPos)
            {
                int posX = session.GetConfigInt(ConfigCode.WindowPosX, 0);
                int posY = session.GetConfigInt(ConfigCode.WindowPosY, 0);
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                int clampedX = Math.Clamp(posX, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                int clampedY = Math.Clamp(posY, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
                StartPosition = FormStartPosition.Manual;
                Location = new Point(clampedX, clampedY);
            }

            if (session.GetConfigBool(ConfigCode.WindowMaximixed, false))
            {
                WindowState = FormWindowState.Maximized;
            }
        }

        _muteMenuItem.Checked = session.AudioDriver?.IsMuted == true;
    }

    private void FitWindowToLogicalViewport()
    {
        if (_session is null || _isFullscreen)
        {
            return;
        }

        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }

        LogicalViewport vp = _session.Engine.Viewport;
        int extraWidth = _scrollBar.Width;
        int extraHeight = (_menuStrip.Visible ? _menuStrip.Height : 0) + _inputBarPanel.Height;
        int desiredClientW = vp.LogicalWidth + extraWidth;
        int desiredClientH = vp.LogicalHeight + extraHeight;

        Size frameDelta = Size - ClientSize;
        Rectangle workArea = Screen.FromControl(this).WorkingArea;

        int targetW = Math.Min(desiredClientW + frameDelta.Width, workArea.Width);
        int targetH = Math.Min(desiredClientH + frameDelta.Height, workArea.Height);
        Size = new Size(Math.Max(MinimumSize.Width, targetW), Math.Max(MinimumSize.Height, targetH));
    }

    private void RestartCurrentTarget()
    {
        if (_session is null)
        {
            return;
        }

        LoadTarget(_session.Target, _initialOptions.IsDebugMode);
    }

    private void OnEngineContentUpdated()
    {
        if (_session is null)
        {
            return;
        }

        EmueraEngine engine = _session.Engine;

        // Keep WindowTitle synced with runtime WINDOW_TITLE mutations
        string title = _session.GetEffectiveWindowTitle();
        if (!string.Equals(Text, title, StringComparison.Ordinal))
        {
            Text = title;
        }

        // Sync SETTEXTBOX / CLEARTEXTBOX from engine into the WinForms TextBox
        if (!string.Equals(_inputTextBox.Text, engine.TextBoxText, StringComparison.Ordinal))
        {
            _syncingTextBox = true;
            try
            {
                _inputTextBox.Text = engine.TextBoxText;
                _inputTextBox.SelectionStart = _inputTextBox.Text.Length;
            }
            finally
            {
                _syncingTextBox = false;
            }
        }

        SyncScrollBarFromView();
        UpdateStatusBadge(engine);
    }

    private void UpdateStatusBadge(EmueraEngine engine)
    {
        string text = engine.Status switch
        {
            EngineStepStatus.Running or EngineStepStatus.Yielded => "Running...",
            EngineStepStatus.Faulted => "Faulted",
            EngineStepStatus.Completed => "Completed",
            EngineStepStatus.WaitingInput when engine.PendingInputRequest is { } req => FormatWaitingStatus(req),
            _ => "Ready",
        };

        if (!string.Equals(_statusLabel.Text, text, StringComparison.Ordinal))
        {
            _statusLabel.Text = text;
        }
    }

    private string FormatWaitingStatus(InputRequest req)
    {
        long? remMs = _skiaView.RemainingTimeoutMs;
        string timerSuffix = remMs.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" ({remMs.Value / 1000.0:0.0}s)")
            : string.Empty;

        return req.Kind switch
        {
            InputRequestKind.AnyKey => "WAIT" + timerSuffix,
            InputRequestKind.TimedWait => "TWAIT" + timerSuffix,
            InputRequestKind.MouseClick => "MOUSE/KEY" + timerSuffix,
            InputRequestKind.OneInt => "ONEINPUT" + timerSuffix,
            InputRequestKind.OneString => "ONEINPUTS" + timerSuffix,
            InputRequestKind.StringValue => "INPUTS" + timerSuffix,
            _ => "INPUT" + timerSuffix,
        };
    }

    private void SyncScrollBarFromView()
    {
        int maxScroll = _skiaView.MaxScrollbackOffset;
        int currentScroll = Math.Clamp(_skiaView.ScrollbackOffset, 0, maxScroll);

        _syncingScrollBar = true;
        try
        {
            if (maxScroll <= 0)
            {
                _scrollBar.Enabled = false;
                _scrollBar.Minimum = 0;
                _scrollBar.Maximum = 0;
                _scrollBar.Value = 0;
            }
            else
            {
                int large = Math.Max(1, Math.Min(20, maxScroll));
                _scrollBar.Enabled = true;
                _scrollBar.Minimum = 0;
                _scrollBar.LargeChange = large;
                _scrollBar.Maximum = maxScroll + large - 1;
                // Bottom of scrollbar == ScrollbackOffset 0 (latest lines)
                _scrollBar.Value = Math.Clamp(maxScroll - currentScroll, 0, maxScroll);
            }
        }
        finally
        {
            _syncingScrollBar = false;
        }
    }

    private void OnScrollBarValueChanged(object? sender, EventArgs e)
    {
        if (_syncingScrollBar)
        {
            return;
        }

        int maxScroll = _skiaView.MaxScrollbackOffset;
        int nextOffset = Math.Clamp(maxScroll - _scrollBar.Value, 0, maxScroll);
        _skiaView.ScrollbackOffset = nextOffset;
    }

    private void OnInputTextBoxTextChanged(object? sender, EventArgs e)
    {
        if (_syncingTextBox || _session is null)
        {
            return;
        }

        _session.Engine.TextBoxText = _inputTextBox.Text;
    }

    private void OnInputTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Enter:
            {
                string submitted = _inputTextBox.Text;
                if (!string.IsNullOrEmpty(submitted) &&
                    (_inputHistory.Count == 0 || !string.Equals(_inputHistory[^1], submitted, StringComparison.Ordinal)))
                {
                    _inputHistory.Add(submitted);
                    if (_inputHistory.Count > 200)
                    {
                        _inputHistory.RemoveAt(0);
                    }
                }

                _historyCursor = -1;
                _syncingTextBox = true;
                _inputTextBox.Clear();
                _session.Engine.TextBoxText = string.Empty;
                _syncingTextBox = false;

                _skiaView.SubmitLineInput(submitted);
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            case Keys.Escape:
                _inputTextBox.Clear();
                _historyCursor = -1;
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;

            case Keys.Up when _inputHistory.Count > 0:
                _historyCursor = _historyCursor < 0 ? _inputHistory.Count - 1 : Math.Max(0, _historyCursor - 1);
                _inputTextBox.Text = _inputHistory[_historyCursor];
                _inputTextBox.SelectionStart = _inputTextBox.Text.Length;
                e.Handled = true;
                return;

            case Keys.Down when _inputHistory.Count > 0 && _historyCursor >= 0:
                _historyCursor++;
                if (_historyCursor >= _inputHistory.Count)
                {
                    _historyCursor = -1;
                    _inputTextBox.Clear();
                }
                else
                {
                    _inputTextBox.Text = _inputHistory[_historyCursor];
                    _inputTextBox.SelectionStart = _inputTextBox.Text.Length;
                }

                e.Handled = true;
                return;

            case Keys.PageUp:
                _skiaView.ScrollBy(5);
                e.Handled = true;
                return;

            case Keys.PageDown:
                _skiaView.ScrollBy(-5);
                e.Handled = true;
                return;
        }

        if (_inputTextBox.TextLength == 0 &&
            _session.Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest.Kind: InputRequestKind.AnyKey or InputRequestKind.MouseClick } &&
            e.Modifiers == Keys.None &&
            e.KeyCode is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.F11 or Keys.F12))
        {
            if (_skiaView.TrySubmitImmediateKey(e.KeyCode))
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        }
    }

    private void OnInputTextBoxKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar) || _session is null)
        {
            return;
        }

        if (_inputTextBox.TextLength == 0 &&
            _session.Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest.Kind: InputRequestKind.OneInt or InputRequestKind.OneString or InputRequestKind.AnyKey })
        {
            if (_skiaView.TrySubmitImmediateChar(e.KeyChar))
            {
                e.Handled = true;
            }
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        _session?.Engine.SetKeyState((int)e.KeyCode, true);

        // If user starts typing while the Skia canvas has focus, route smoothly to the Emuera input box
        if (!_inputTextBox.Focused &&
            e.Modifiers == Keys.None &&
            _session?.Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest.Kind: InputRequestKind.IntValue or InputRequestKind.StringValue } &&
            ((e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) ||
             (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9) ||
             (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) ||
             e.KeyCode is Keys.OemMinus or Keys.Subtract))
        {
            _inputTextBox.Focus();
        }
    }

    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        _session?.Engine.SetKeyState((int)e.KeyCode, false);
    }

    private void OnEngineFaulted(object? sender, Exception ex)
    {
        if (_session is null)
        {
            return;
        }

        string details = StandaloneGameSession.FormatRuntimeFault(ex, _session.Engine);
        BeginInvoke(() =>
        {
            using var dlg = new DiagnosticsDialog("Hypera Runtime Fault", details, showRestartButton: true);
            if (dlg.ShowDialog(this) == DialogResult.Retry)
            {
                RestartCurrentTarget();
            }
        });
    }

    private void OnClearInputClick(object? sender, EventArgs e) => _inputTextBox.Clear();

    private void OnScrollToBottomClick(object? sender, EventArgs e) => _skiaView.ScrollToBottom();

    private void OnOpenPackageClick(object? sender, EventArgs e) => PromptOpenInitialGame();

    private void OnOpenFolderClick(object? sender, EventArgs e)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Select an Emuera / Hypera Game Folder",
            UseDescriptionForTitle = true,
        };

        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            StandaloneLaunchTarget target = StandaloneLaunchResolver.ResolveExplicitPath(
                fbd.SelectedPath,
                explicitSaveDir: null,
                useOverlay: _initialOptions.UseOverlay);
            LoadTarget(target, _initialOptions.IsDebugMode);
        }
    }

    private void OnSaveLogClick(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Save Console Output Log",
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            FileName = "emuera.log.txt",
        };

        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            IReadOnlyList<string> lines = _session.Engine.ConsoleBuffer.GetAllPlainTextLines();
            File.WriteAllLines(sfd.FileName, lines, System.Text.Encoding.UTF8);
        }
    }

    private void OnOpenSaveDirectoryClick(object? sender, EventArgs e)
    {
        string? dir = _session?.Target.SaveDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    private void OnCopyConsoleTextClick(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        string allText = string.Join(Environment.NewLine, _session.Engine.ConsoleBuffer.GetAllPlainTextLines());
        if (!string.IsNullOrEmpty(allText))
        {
            Clipboard.SetText(allText);
        }
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _savedBorderStyle = FormBorderStyle;
            _savedWindowState = WindowState;
            _savedBounds = Bounds;
            _menuWasVisibleBeforeFullscreen = _menuStrip.Visible;

            _menuStrip.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _isFullscreen = true;
            _fullscreenMenuItem.Checked = true;
        }
        else
        {
            _isFullscreen = false;
            _fullscreenMenuItem.Checked = false;
            _menuStrip.Visible = _menuWasVisibleBeforeFullscreen;
            FormBorderStyle = _savedBorderStyle;
            WindowState = _savedWindowState;
            if (_savedWindowState == FormWindowState.Normal)
            {
                Bounds = _savedBounds;
            }
        }
    }

    private void ToggleMute()
    {
        if (_session?.AudioDriver is null)
        {
            return;
        }

        _session.AudioDriver.IsMuted = !_session.AudioDriver.IsMuted;
        _muteMenuItem.Checked = _session.AudioDriver.IsMuted;
    }

    private void OnShowDebugLogClick(object? sender, EventArgs e)
    {
        if (_debugConsoleForm is null || _debugConsoleForm.IsDisposed)
        {
            _debugConsoleForm = new DebugConsoleForm();
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Clamp(Right + 8, wa.Left, Math.Max(wa.Left, wa.Right - _debugConsoleForm.Width));
            int y = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - _debugConsoleForm.Height));
            _debugConsoleForm.Location = new Point(x, y);
        }

        _debugConsoleForm.AttachSession(_session);
        if (!_debugConsoleForm.Visible)
        {
            _debugConsoleForm.Show(this);
        }

        _debugConsoleForm.BringToFront();
        _debugConsoleForm.Activate();
    }

    private void OnShowRuntimeInfoClick(object? sender, EventArgs e)
    {
        string info = _session?.BuildRuntimeInfoText() ?? "No game session is currently loaded.";
        using var dlg = new DiagnosticsDialog("Hypera Runtime & Package Info", info);
        dlg.ShowDialog(this);
    }

    private void OnShowAboutClick(object? sender, EventArgs e)
    {
        MessageBox.Show(
            this,
            "Hypera Standalone 0.1.0\n\n" +
            "Direct binary Emuera-compatible runner built on .NET 10, 16-byte Register VM, and SkiaSharp.\n\n" +
            "Supports HYPRBNDL single-file bundles, .hep packages, .heb bytecode, .zip archives, and raw game folders.",
            "About Hypera Standalone",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnFormDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            try
            {
                StandaloneLaunchTarget target = StandaloneLaunchResolver.ResolveExplicitPath(
                    paths[0],
                    explicitSaveDir: null,
                    useOverlay: _initialOptions.UseOverlay);
                LoadTarget(target, _initialOptions.IsDebugMode);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Unsupported Drop Target", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _skiaView.StopLoop();
        _skiaView.Engine = null;
        if (_debugConsoleForm is not null && !_debugConsoleForm.IsDisposed)
        {
            _debugConsoleForm.DetachAndCleanup();
            _debugConsoleForm.Dispose();
            _debugConsoleForm = null;
        }

        _session?.Dispose();
        _session = null;
        _customWindowIcon?.Dispose();
        _customWindowIcon = null;
    }
}
