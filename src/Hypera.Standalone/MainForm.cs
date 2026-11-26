using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Hypera.Configuration;
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
    private readonly Panel _loadingOverlayPanel;
    private readonly Panel _loadingCardPanel;
    private readonly Label _loadingTitleLabel;
    private readonly Label _loadingStageLabel;
    private readonly SmoothProgressBar _loadingProgressBar;
    private readonly Label _loadingDetailLabel;
    private readonly System.Windows.Forms.Timer _loadingAnimTimer;
    private readonly Stopwatch _loadingStopwatch = new();
    private readonly Panel _inputBarPanel;
    private readonly TextBox _inputTextBox;
    private readonly Label _statusLabel;
    private readonly List<string> _inputHistory = [];

    private StandaloneGameSession? _session;
    private DebugConsoleForm? _debugConsoleForm;
    private Icon? _customWindowIcon;
    private CancellationTokenSource? _loadCts;
    private int _loadGeneration;
    private bool _isLoading;
    private bool _isMenuOpen;
    private int _currentLoadPercent;
    private string? _currentLoadDetail;
    private int _historyCursor = -1;
    private bool _syncingScrollBar;
    private bool _syncingTextBox;
    private bool _isFullscreen;
    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private Rectangle _savedBounds;
    private bool _menuWasVisibleBeforeFullscreen;
    private string _windowPrefPath;
    private WindowStatePreference _windowPref;
    private bool _desiredMaximized;
    private bool _hasPersistedNormalBounds;
    private bool _isWindowShown;
    private bool _isApplyingWindowSettings;
    private int _lastScrollMax = -1;
    private int _lastScrollCurrent = -1;

    public MainForm(StandaloneLaunchOptions initialOptions)
    {
        _initialOptions = initialOptions;
        _windowPrefPath = WindowStatePreferenceStore.ResolveFilePath(
            initialOptions.Target.SaveDirectory,
            AppContext.BaseDirectory);
        _windowPref = WindowStatePreferenceStore.Load(_windowPrefPath);
        _desiredMaximized = _windowPref.IsMaximized;
        _hasPersistedNormalBounds = _windowPref.HasNormalSize;

        Text = "Hypera Standalone";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        ForeColor = Color.FromArgb(192, 192, 192);
        KeyPreview = true;
        AllowDrop = true;
        MinimumSize = new Size(480, 360);
        Size = new Size(1024, 768);
        ApplyInitialWindowPreference();

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
        var resetSizeItem = new ToolStripMenuItem("Fit Window to Logical Viewport (1:1)", null, (_, _) => FitWindowToLogicalViewport(persistPreference: true));
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
        _menuStrip.MenuActivate += (_, _) => _isMenuOpen = true;
        _menuStrip.MenuDeactivate += (_, _) =>
        {
            _isMenuOpen = false;
            BeginInvoke(FocusInputTextBox);
        };

        // 2. Center Viewport Panel: Skia canvas + right-edge scrollback bar + startup loading overlay
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
            TabStop = false,
        };
        _scrollBar.ValueChanged += OnScrollBarValueChanged;
        _scrollBar.MouseUp += (_, _) => BeginInvoke(FocusInputTextBox);
        _scrollBar.Scroll += (_, e) =>
        {
            if (e.Type == ScrollEventType.EndScroll)
            {
                BeginInvoke(FocusInputTextBox);
            }
        };

        _skiaView = new HyperaSkiaControl
        {
            Dock = DockStyle.Fill,
            AutoSubmitEngineInput = true,
            FocusOnMouseDown = false,
            BackColor = Color.Black,
        };
        _skiaView.ScrollbackChanged += (_, _) => SyncScrollBarFromView();
        _skiaView.ContentUpdated += (_, _) => OnEngineContentUpdated();
        _skiaView.EngineFaulted += OnEngineFaulted;
        _skiaView.EngineRestartRequested += (_, _) => BeginInvoke(RestartCurrentTarget);
        _skiaView.EngineCompleted += (_, _) => BeginInvoke(Close);
        _skiaView.LogicalPointerUp += (_, _) => FocusInputTextBox();

        // Dark-themed loading overlay displayed immediately while game bytecode/variables load asynchronously
        _loadingOverlayPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 12, 16),
            Visible = false,
        };
        _loadingOverlayPanel.Resize += (_, _) => UpdateLoadingOverlayLayout();

        _loadingCardPanel = new Panel
        {
            BackColor = Color.FromArgb(18, 21, 28),
        };
        _loadingCardPanel.Paint += (_, pe) =>
        {
            using var borderPen = new Pen(Color.FromArgb(52, 62, 82), 1f);
            pe.Graphics.DrawRectangle(borderPen, 0, 0, _loadingCardPanel.Width - 1, _loadingCardPanel.Height - 1);
        };

        _loadingTitleLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            ForeColor = Color.FromArgb(238, 242, 248),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Loading Hypera...",
        };

        _loadingStageLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            ForeColor = Color.FromArgb(178, 190, 208),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Preparing runtime...",
        };

        _loadingProgressBar = new SmoothProgressBar();

        _loadingDetailLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            ForeColor = Color.FromArgb(132, 146, 168),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "0%  •  0.0s",
        };

        _loadingCardPanel.Controls.Add(_loadingTitleLabel);
        _loadingCardPanel.Controls.Add(_loadingStageLabel);
        _loadingCardPanel.Controls.Add(_loadingProgressBar);
        _loadingCardPanel.Controls.Add(_loadingDetailLabel);
        _loadingOverlayPanel.Controls.Add(_loadingCardPanel);

        _loadingAnimTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _loadingAnimTimer.Tick += OnLoadingAnimTick;

        _viewportHostPanel.Controls.Add(_skiaView);
        _viewportHostPanel.Controls.Add(_scrollBar);
        _viewportHostPanel.Controls.Add(_loadingOverlayPanel);
        _loadingOverlayPanel.BringToFront();

        // 3. Bottom Emuera Input Bar
        _inputBarPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Padding = new Padding(4, 3, 4, 3),
            BackColor = Color.FromArgb(18, 18, 22),
        };
        _inputBarPanel.MouseUp += (_, _) => FocusInputTextBox();

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
        _statusLabel.MouseUp += (_, _) => FocusInputTextBox();

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
        _inputTextBox.MouseWheel += OnInputTextBoxMouseWheel;

        _inputBarPanel.Controls.Add(_inputTextBox);
        _inputBarPanel.Controls.Add(_statusLabel);
        UpdateInputBarHeight();

        // Dock order matters in WinForms: Fill added first, then Top/Bottom
        Controls.Add(_viewportHostPanel);
        Controls.Add(_inputBarPanel);
        Controls.Add(_menuStrip);
        MainMenuStrip = _menuStrip;

        Load += OnFormLoad;
        Shown += OnFormShown;
        Activated += (_, _) =>
        {
            if (_session is not null)
            {
                _session.Engine.IsWindowActive = true;
            }

            BeginInvoke(FocusInputTextBox);
        };
        Deactivate += (_, _) =>
        {
            if (_session is not null)
            {
                _session.Engine.IsWindowActive = false;
                _session.Engine.IsMouseSkip = false;
            }
        };
        KeyDown += OnGlobalKeyDown;
        KeyUp += OnGlobalKeyUp;
        DragEnter += OnFormDragEnter;
        DragDrop += OnFormDragDrop;
        Resize += OnFormResize;
        Move += OnFormMove;
        ResizeEnd += OnFormResizeEnd;
        FormClosing += OnFormClosing;
    }

    private void ApplyInitialWindowPreference()
    {
        _isApplyingWindowSettings = true;
        try
        {
            if (_windowPref.HasNormalSize)
            {
                int w = Math.Max(MinimumSize.Width, _windowPref.NormalWidth!.Value);
                int h = Math.Max(MinimumSize.Height, _windowPref.NormalHeight!.Value);
                Size = new Size(w, h);
            }

            if (_windowPref.HasNormalPosition)
            {
                int x = _windowPref.NormalX!.Value;
                int y = _windowPref.NormalY!.Value;
                Rectangle wa = Screen.FromPoint(new Point(x, y)).WorkingArea;
                int clampedX = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width));
                int clampedY = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
                StartPosition = FormStartPosition.Manual;
                Location = new Point(clampedX, clampedY);
            }

            if (_desiredMaximized)
            {
                WindowState = FormWindowState.Maximized;
            }
        }
        finally
        {
            _isApplyingWindowSettings = false;
        }
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        if (_initialOptions.Target.Kind != StandaloneTargetKind.None)
        {
            // Display the loading screen immediately so the window appears on screen without waiting for load I/O
            ShowLoadingOverlay(_initialOptions.Target);
            _inputTextBox.Enabled = false;
            _statusLabel.Text = "Loading...";
        }
        else
        {
            _statusLabel.Text = "No game loaded (Ctrl+O)";
        }
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        _isWindowShown = true;
        UpdateInputBarHeight();
        EnsureClientInsideWorkingArea();

        if (_initialOptions.Target.Kind != StandaloneTargetKind.None)
        {
            _ = LoadTargetAsync(_initialOptions.Target, _initialOptions.IsDebugMode);
        }
        else
        {
            PromptOpenInitialGame();
        }
    }

    private void OnFormResize(object? sender, EventArgs e)
    {
        if (!_isWindowShown || _isApplyingWindowSettings || _isFullscreen || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        bool isNowMaximized = WindowState == FormWindowState.Maximized;
        if (isNowMaximized != _desiredMaximized)
        {
            _desiredMaximized = isNowMaximized;
            if (!isNowMaximized)
            {
                EnsureClientInsideWorkingArea();
                CaptureNormalBounds();
            }

            PersistWindowPreference();
        }
        else if (!isNowMaximized)
        {
            CaptureNormalBounds();
        }
    }

    private void OnFormMove(object? sender, EventArgs e)
    {
        if (_isWindowShown && !_isApplyingWindowSettings && !_isFullscreen && WindowState == FormWindowState.Normal)
        {
            CaptureNormalBounds();
        }
    }

    private void OnFormResizeEnd(object? sender, EventArgs e)
    {
        if (_isWindowShown && !_isApplyingWindowSettings && !_isFullscreen && WindowState == FormWindowState.Normal)
        {
            CaptureNormalBounds();
            PersistWindowPreference();
        }
    }

    private void CaptureNormalBounds()
    {
        if (WindowState != FormWindowState.Normal || _isFullscreen)
        {
            return;
        }

        Rectangle b = Bounds;
        if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height)
        {
            _windowPref = _windowPref with
            {
                NormalX = b.X,
                NormalY = b.Y,
                NormalWidth = b.Width,
                NormalHeight = b.Height,
            };
            _hasPersistedNormalBounds = true;
        }
    }

    private void PersistWindowPreference(bool? explicitMaximized = null)
    {
        bool saveMaximized = explicitMaximized ?? _desiredMaximized;
        _windowPref = _windowPref with { IsMaximized = saveMaximized };
        WindowStatePreferenceStore.TrySave(_windowPrefPath, _windowPref);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateInputBarHeight();
        UpdateLoadingOverlayLayout();
        EnsureClientInsideWorkingArea();
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
            _ = LoadTargetAsync(target, _initialOptions.IsDebugMode);
        }
    }

    private void ShowLoadingOverlay(StandaloneLaunchTarget target)
    {
        string displayName = Path.GetFileName(target.Path);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = target.Path;
        }

        _currentLoadPercent = 0;
        _currentLoadDetail = displayName;
        _loadingTitleLabel.Text = $"Loading {displayName}";
        _loadingStageLabel.Text = "Opening game package...";
        _loadingProgressBar.Value = 0;
        _loadingProgressBar.IsAnimating = true;
        _loadingStopwatch.Restart();
        RefreshLoadingDetailText();

        Text = $"{displayName} - Loading...";
        _loadingOverlayPanel.Visible = true;
        _loadingOverlayPanel.BringToFront();
        UpdateLoadingOverlayLayout();
        _loadingAnimTimer.Start();
    }

    private void UpdateLoadingOverlayLayout()
    {
        float dpiScale = MathF.Max(1f, DeviceDpi / 96f);
        int padX = (int)MathF.Round(40f * dpiScale);
        int padY = (int)MathF.Round(30f * dpiScale);
        int gapTitle = (int)MathF.Round(12f * dpiScale);
        int gapStage = (int)MathF.Round(16f * dpiScale);
        int gapBar = (int)MathF.Round(12f * dpiScale);
        int barHeight = Math.Max(14, (int)MathF.Round(18f * dpiScale));

        const TextFormatFlags measureFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        Size maxProbe = new(int.MaxValue, int.MaxValue);
        Size titleSize = TextRenderer.MeasureText(_loadingTitleLabel.Text, _loadingTitleLabel.Font, maxProbe, measureFlags);
        Size stageSize = TextRenderer.MeasureText(_loadingStageLabel.Text, _loadingStageLabel.Font, maxProbe, measureFlags);
        Size detailSize = TextRenderer.MeasureText(_loadingDetailLabel.Text, _loadingDetailLabel.Font, maxProbe, measureFlags);

        int hostW = _loadingOverlayPanel.ClientSize.Width > 0 ? _loadingOverlayPanel.ClientSize.Width : ClientSize.Width;
        int hostH = _loadingOverlayPanel.ClientSize.Height > 0 ? _loadingOverlayPanel.ClientSize.Height : ClientSize.Height;
        if (hostW <= 0 || hostH <= 0)
        {
            return;
        }

        int outerMargin = (int)MathF.Round(48f * dpiScale);
        int maxCardW = Math.Max(360, hostW - (outerMargin * 2));
        int minCardW = (int)MathF.Round(680f * dpiScale);
        int ratioCardW = (int)MathF.Round(hostW * 0.56f);
        int contentCardW = Math.Max(titleSize.Width, Math.Max(stageSize.Width, detailSize.Width))
                           + (padX * 2)
                           + (int)MathF.Round(40f * dpiScale);

        int cardW = Math.Min(maxCardW, Math.Max(Math.Max(minCardW, ratioCardW), contentCardW));
        int innerW = Math.Max(200, cardW - (padX * 2));

        int titleH = titleSize.Height + (int)MathF.Round(6f * dpiScale);
        int stageH = stageSize.Height + (int)MathF.Round(4f * dpiScale);
        int detailH = detailSize.Height + (int)MathF.Round(4f * dpiScale);

        int y = padY;
        _loadingTitleLabel.Bounds = new Rectangle(padX, y, innerW, titleH);
        y += titleH + gapTitle;
        _loadingStageLabel.Bounds = new Rectangle(padX, y, innerW, stageH);
        y += stageH + gapStage;
        _loadingProgressBar.Bounds = new Rectangle(padX, y, innerW, barHeight);
        y += barHeight + gapBar;
        _loadingDetailLabel.Bounds = new Rectangle(padX, y, innerW, detailH);
        y += detailH + padY;

        int cardH = y;
        int cardX = Math.Max(0, (hostW - cardW) / 2);
        int cardY = Math.Max(0, (hostH - cardH) / 2);
        _loadingCardPanel.Bounds = new Rectangle(cardX, cardY, cardW, cardH);
        _loadingCardPanel.Invalidate();
    }

    private void HideLoadingOverlay()
    {
        _loadingAnimTimer.Stop();
        _loadingStopwatch.Stop();
        _loadingProgressBar.IsAnimating = false;
        _loadingOverlayPanel.Visible = false;
    }

    private void UpdateLoadingProgress(GameLoadProgress report)
    {
        bool stageChanged = !string.Equals(_loadingStageLabel.Text, report.Stage, StringComparison.Ordinal);
        _currentLoadPercent = Math.Max(_currentLoadPercent, Math.Clamp(report.Percent, 0, 100));
        _currentLoadDetail = report.Detail;
        if (stageChanged)
        {
            _loadingStageLabel.Text = report.Stage;
        }

        _loadingProgressBar.Value = _currentLoadPercent;
        _statusLabel.Text = $"Loading {_currentLoadPercent}%";
        RefreshLoadingDetailText();

        if (stageChanged ||
            TextRenderer.MeasureText(
                _loadingDetailLabel.Text,
                _loadingDetailLabel.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width > _loadingDetailLabel.Width)
        {
            UpdateLoadingOverlayLayout();
        }
    }

    private void OnLoadingAnimTick(object? sender, EventArgs e)
    {
        if (!_loadingOverlayPanel.Visible)
        {
            return;
        }

        _loadingProgressBar.AdvanceShimmer();
        RefreshLoadingDetailText();
    }

    private void RefreshLoadingDetailText()
    {
        string elapsed = string.Create(CultureInfo.InvariantCulture, $"{_loadingStopwatch.Elapsed.TotalSeconds:0.0}s");
        _loadingDetailLabel.Text = string.IsNullOrWhiteSpace(_currentLoadDetail)
            ? $"{_currentLoadPercent}%  •  {elapsed}"
            : $"{_currentLoadPercent}%  •  {_currentLoadDetail}  •  {elapsed}";
    }

    private void FocusInputTextBox()
    {
        if (IsDisposed || !Visible || _isLoading || _isMenuOpen || !_inputTextBox.Enabled || !_inputTextBox.Visible)
        {
            return;
        }

        if (ActiveForm != this)
        {
            return;
        }

        if (!_inputTextBox.Focused)
        {
            _inputTextBox.Focus();
        }
    }

    private async Task LoadTargetAsync(StandaloneLaunchTarget target, bool isDebugMode)
    {
        int loadGeneration = ++_loadGeneration;

        CancellationTokenSource? previousCts = _loadCts;
        _loadCts = null;
        if (previousCts is not null)
        {
            try { previousCts.Cancel(); } catch { }
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        CancellationToken ct = cts.Token;

        _isLoading = true;
        _skiaView.StopLoop();
        _skiaView.Engine = null;

        StandaloneGameSession? previous = _session;
        _session = null;
        previous?.Dispose();

        _inputTextBox.Enabled = false;
        _statusLabel.Text = "Loading 0%";
        ShowLoadingOverlay(target);

        var progress = new Progress<GameLoadProgress>(report =>
        {
            if (loadGeneration == _loadGeneration && !IsDisposed)
            {
                UpdateLoadingProgress(report);
            }
        });

        try
        {
            StandaloneGameSession session = await Task.Run(
                () => StandaloneGameSession.Load(
                    target,
                    isDebugMode: isDebugMode,
                    enableHardwareAudio: true,
                    progress: progress,
                    cancellationToken: ct),
                ct);

            if (ct.IsCancellationRequested || loadGeneration != _loadGeneration || IsDisposed)
            {
                session.Dispose();
                return;
            }

            _loadingProgressBar.Value = 100;
            _session = session;
            _debugConsoleForm?.AttachSession(session);
            ApplySessionWindowSettings(session);

            HideLoadingOverlay();
            _isLoading = false;
            _inputTextBox.Enabled = true;

            _skiaView.Engine = session.Engine;
            _skiaView.StartLoop();
            _inputTextBox.Clear();
            OnEngineContentUpdated();
            FocusInputTextBox();
        }
        catch (OperationCanceledException)
        {
            if (loadGeneration == _loadGeneration && !IsDisposed)
            {
                HideLoadingOverlay();
                _isLoading = false;
                _inputTextBox.Enabled = true;
                _statusLabel.Text = "Canceled";
            }
        }
        catch (EmueraCompileException compileEx)
        {
            if (loadGeneration == _loadGeneration && !IsDisposed)
            {
                HideLoadingOverlay();
                _isLoading = false;
                _inputTextBox.Enabled = true;
                string report = StandaloneGameSession.FormatCompileDiagnostics(compileEx);
                _statusLabel.Text = "Compilation failed";
                using var dlg = new DiagnosticsDialog("Hypera Compiler Diagnostics", report);
                dlg.ShowDialog(this);
            }
        }
        catch (Exception ex)
        {
            if (loadGeneration == _loadGeneration && !IsDisposed)
            {
                HideLoadingOverlay();
                _isLoading = false;
                _inputTextBox.Enabled = true;
                _statusLabel.Text = "Load failed";
                using var dlg = new DiagnosticsDialog("Failed to Load Game", ex.ToString());
                dlg.ShowDialog(this);
            }
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

        UpdateInputBarHeight();

        _windowPrefPath = WindowStatePreferenceStore.ResolveFilePath(
            session.Target.SaveDirectory,
            AppContext.BaseDirectory);

        if (!_isFullscreen)
        {
            _isApplyingWindowSettings = true;
            try
            {
                bool setPos = session.GetConfigBool(ConfigCode.SetWindowPos, false);
                int posX = session.GetConfigInt(ConfigCode.WindowPosX, 0);
                int posY = session.GetConfigInt(ConfigCode.WindowPosY, 0);
                Rectangle targetWorkArea = setPos
                    ? Screen.FromPoint(new Point(posX, posY)).WorkingArea
                    : Screen.FromControl(this).WorkingArea;

                if (!sizable)
                {
                    if (WindowState == FormWindowState.Maximized)
                    {
                        WindowState = FormWindowState.Normal;
                    }

                    FitWindowToLogicalViewport(targetWorkArea);
                    if (setPos)
                    {
                        int clampedX = Math.Clamp(posX, targetWorkArea.Left, Math.Max(targetWorkArea.Left, targetWorkArea.Right - Width));
                        int clampedY = Math.Clamp(posY, targetWorkArea.Top, Math.Max(targetWorkArea.Top, targetWorkArea.Bottom - Height));
                        StartPosition = FormStartPosition.Manual;
                        Location = new Point(clampedX, clampedY);
                    }

                    EnsureClientInsideWorkingArea(targetWorkArea);
                }
                else if (_desiredMaximized)
                {
                    if (WindowState != FormWindowState.Maximized)
                    {
                        WindowState = FormWindowState.Maximized;
                    }
                }
                else
                {
                    if (WindowState == FormWindowState.Maximized)
                    {
                        WindowState = FormWindowState.Normal;
                    }

                    if (!_hasPersistedNormalBounds)
                    {
                        FitWindowToLogicalViewport(targetWorkArea);
                        if (setPos)
                        {
                            int clampedX = Math.Clamp(posX, targetWorkArea.Left, Math.Max(targetWorkArea.Left, targetWorkArea.Right - Width));
                            int clampedY = Math.Clamp(posY, targetWorkArea.Top, Math.Max(targetWorkArea.Top, targetWorkArea.Bottom - Height));
                            StartPosition = FormStartPosition.Manual;
                            Location = new Point(clampedX, clampedY);
                        }
                    }

                    EnsureClientInsideWorkingArea(targetWorkArea);
                }
            }
            finally
            {
                _isApplyingWindowSettings = false;
            }
        }

        _muteMenuItem.Checked = session.AudioDriver?.IsMuted == true;
    }

    private void UpdateInputBarHeight()
    {
        float dpiScale = MathF.Max(1f, DeviceDpi / 96f);
        int statusW = (int)MathF.Round(190f * dpiScale);
        if (_statusLabel.Width != statusW)
        {
            _statusLabel.Width = statusW;
        }

        int padX = Math.Max(4, (int)MathF.Round(4f * dpiScale));
        int padY = Math.Max(3, (int)MathF.Round(3f * dpiScale));
        if (_inputBarPanel.Padding.Left != padX || _inputBarPanel.Padding.Top != padY)
        {
            _inputBarPanel.Padding = new Padding(padX, padY, padX, padY);
        }

        int preferred = _inputTextBox.PreferredHeight;
        int targetHeight = Math.Max((int)MathF.Round(28f * dpiScale), preferred + _inputBarPanel.Padding.Vertical + 2);
        if (_inputBarPanel.Height != targetHeight)
        {
            _inputBarPanel.Height = targetHeight;
        }
    }

    private void FitWindowToLogicalViewport(Rectangle? explicitWorkArea = null, bool persistPreference = false)
    {
        if (_session is null || _isFullscreen)
        {
            return;
        }

        bool previousGuard = _isApplyingWindowSettings;
        _isApplyingWindowSettings = true;
        try
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }

            UpdateInputBarHeight();

            LogicalViewport vp = _session.Engine.Viewport;
            int extraWidth = _scrollBar.Width;
            int extraHeight = (_menuStrip.Visible ? _menuStrip.Height : 0) + _inputBarPanel.Height;
            int desiredClientW = vp.LogicalWidth + extraWidth;
            int desiredClientH = vp.LogicalHeight + extraHeight;

            Size frameDelta = Size - ClientSize;
            Rectangle workArea = explicitWorkArea ?? Screen.FromControl(this).WorkingArea;

            int targetW = Math.Min(desiredClientW + frameDelta.Width, workArea.Width);
            int targetH = Math.Min(desiredClientH + frameDelta.Height, workArea.Height);
            Size = new Size(Math.Max(MinimumSize.Width, targetW), Math.Max(MinimumSize.Height, targetH));
            EnsureClientInsideWorkingArea(workArea);
        }
        finally
        {
            _isApplyingWindowSettings = previousGuard;
        }

        if (persistPreference)
        {
            _desiredMaximized = false;
            CaptureNormalBounds();
            PersistWindowPreference(explicitMaximized: false);
        }
    }

    /// <summary>
    /// Guarantees the window and its entire client area (especially the bottom input bar) remain
    /// strictly above the Windows taskbar and inside the monitor's <see cref="Screen.WorkingArea"/>
    /// across all PerMonitorV2 DPI scales and DWM non-client border metrics.
    /// </summary>
    private void EnsureClientInsideWorkingArea(Rectangle? explicitWorkArea = null)
    {
        if (_isFullscreen || WindowState != FormWindowState.Normal)
        {
            return;
        }

        Rectangle workArea = explicitWorkArea ?? Screen.FromRectangle(Bounds).WorkingArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        int newW = Math.Clamp(Width, Math.Min(MinimumSize.Width, workArea.Width), workArea.Width);
        int newH = Math.Clamp(Height, Math.Min(MinimumSize.Height, workArea.Height), workArea.Height);
        if (newW != Width || newH != Height)
        {
            Size = new Size(newW, newH);
        }

        int newX = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        int newY = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        if (newX != Left || newY != Top)
        {
            Location = new Point(newX, newY);
        }

        if (!IsHandleCreated)
        {
            return;
        }

        // Verify actual physical screen position of the bottom-right client corner (bottom of _inputBarPanel),
        // keeping a 1px buffer above the Windows taskbar edge.
        Point clientBottomRight = PointToScreen(new Point(ClientSize.Width, ClientSize.Height));
        int bottomOverflow = clientBottomRight.Y - (workArea.Bottom - 1);
        if (bottomOverflow > 0)
        {
            int lift = Math.Min(bottomOverflow, Math.Max(0, Top - workArea.Top));
            if (lift > 0)
            {
                Top -= lift;
                bottomOverflow -= lift;
            }

            if (bottomOverflow > 0)
            {
                Height = Math.Max(Math.Min(MinimumSize.Height, workArea.Height), Height - bottomOverflow);
            }
        }

        int rightOverflow = clientBottomRight.X - workArea.Right;
        if (rightOverflow > 0)
        {
            int shift = Math.Min(rightOverflow, Math.Max(0, Left - workArea.Left));
            if (shift > 0)
            {
                Left -= shift;
                rightOverflow -= shift;
            }

            if (rightOverflow > 0)
            {
                Width = Math.Max(Math.Min(MinimumSize.Width, workArea.Width), Width - rightOverflow);
            }
        }
    }

    private void RestartCurrentTarget()
    {
        if (_session is null)
        {
            return;
        }

        _ = LoadTargetAsync(_session.Target, _initialOptions.IsDebugMode);
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
        if (maxScroll == _lastScrollMax && currentScroll == _lastScrollCurrent)
        {
            return;
        }

        _lastScrollMax = maxScroll;
        _lastScrollCurrent = currentScroll;

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

    private void OnInputTextBoxMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_session is null || _isLoading)
        {
            return;
        }

        int linesPerNotch = Math.Max(1, _session.Engine.Options.ScrollHeight);
        int deltaLines = (e.Delta / 120) * linesPerNotch;
        if (deltaLines != 0)
        {
            _skiaView.ScrollBy(deltaLines);
        }

        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
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
            _ = LoadTargetAsync(target, _initialOptions.IsDebugMode);
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
        _isApplyingWindowSettings = true;
        try
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
        finally
        {
            _isApplyingWindowSettings = false;
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
                _ = LoadTargetAsync(target, _initialOptions.IsDebugMode);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Unsupported Drop Target", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        bool finalMaximized = _isFullscreen
            ? _savedWindowState == FormWindowState.Maximized
            : WindowState == FormWindowState.Minimized
                ? _desiredMaximized
                : WindowState == FormWindowState.Maximized;
        if (!_isFullscreen && WindowState == FormWindowState.Normal)
        {
            CaptureNormalBounds();
        }

        PersistWindowPreference(explicitMaximized: finalMaximized);

        _loadGeneration++;
        CancellationTokenSource? cts = _loadCts;
        _loadCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        _loadingAnimTimer.Stop();
        _loadingAnimTimer.Dispose();

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

    /// <summary>
    /// Flicker-free dark-mode progress bar with immediate zero-lag updates and a subtle activity shimmer.
    /// </summary>
    private sealed class SmoothProgressBar : Control
    {
        private int _value;
        private bool _isAnimating;
        private float _shimmerPhase;

        public SmoothProgressBar()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (_value != clamped)
                {
                    _value = clamped;
                    Invalidate();
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsAnimating
        {
            get => _isAnimating;
            set
            {
                if (_isAnimating != value)
                {
                    _isAnimating = value;
                    _shimmerPhase = 0f;
                    Invalidate();
                }
            }
        }

        public void AdvanceShimmer()
        {
            if (!_isAnimating)
            {
                return;
            }

            _shimmerPhase += 0.032f;
            if (_shimmerPhase > 1.35f)
            {
                _shimmerPhase = -0.35f;
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 2 || h <= 2)
            {
                return;
            }

            using (var trackBrush = new SolidBrush(Color.FromArgb(26, 30, 40)))
            {
                g.FillRectangle(trackBrush, 0, 0, w, h);
            }

            int innerW = w - 2;
            int innerH = h - 2;
            int fillW = (int)Math.Round(innerW * (_value / 100.0));
            if (fillW > 0)
            {
                var fillRect = new Rectangle(1, 1, fillW, innerH);
                using var fillBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(1, 1, Math.Max(1, innerW), Math.Max(1, innerH)),
                    Color.FromArgb(46, 114, 232),
                    Color.FromArgb(92, 168, 255),
                    0f);
                g.FillRectangle(fillBrush, fillRect);
            }

            if (_isAnimating && _value < 100)
            {
                int bandW = Math.Max(48, innerW / 4);
                int bandX = 1 + (int)Math.Round(_shimmerPhase * innerW) - (bandW / 2);
                Rectangle clip = new(1, 1, innerW, innerH);
                g.SetClip(clip);
                using var shimmerBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(bandX, 1, bandW, innerH),
                    Color.FromArgb(0, 148, 206, 255),
                    Color.FromArgb(72, 196, 230, 255),
                    0f);
                shimmerBrush.SetSigmaBellShape(0.5f, 1f);
                g.FillRectangle(shimmerBrush, bandX, 1, bandW, innerH);
                g.ResetClip();
            }

            using var borderPen = new Pen(Color.FromArgb(52, 62, 80), 1f);
            g.DrawRectangle(borderPen, 0, 0, w - 1, h - 1);
        }
    }
}
