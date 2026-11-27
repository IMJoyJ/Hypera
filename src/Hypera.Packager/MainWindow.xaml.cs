using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Hypera.Packager;

public partial class MainWindow : Window
{
    private readonly PackagerLaunchOptions _launchOptions;
    private string _currentLanguage;
    private string _currentStatusKey = "StatusReady";
    private string? _currentProgressStageKey;
    private PackResult? _lastResult;
    private bool _isPacking;
    private bool _updatingOutputInternally;
    private bool _outputPathManuallyEdited;
    private bool _isCopyFeedbackActive;

    public MainWindow(PackagerLaunchOptions launchOptions)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        _launchOptions = launchOptions;
        _currentLanguage = PackagerStrings.NormalizeLanguage(launchOptions.LanguageCode);

        InitializeComponent();

        VersionBadgeText.Text = $"v{Program.GetVersionString()}";
        ApplyInitialLaunchOptions();
        SyncLanguageRadioButtons();
        ApplyLocalization();
    }

    private void ApplyInitialLaunchOptions()
    {
        CompiledOnlyCheckBox.IsChecked = _launchOptions.CompiledOnly;
        CompressCheckBox.IsChecked = _launchOptions.Compress;

        RandomModeComboBox.SelectedIndex = _launchOptions.RandomMode switch
        {
            RandomModeOverride.OldSfmt => 1,
            RandomModeOverride.NewRandom => 2,
            _ => 0,
        };

        if (_launchOptions.ExcludePaths is { Count: > 0 } excludes)
        {
            ExcludesTextBox.Text = string.Join("; ", excludes);
        }

        if (!string.IsNullOrWhiteSpace(_launchOptions.InitialSourceDirectory))
        {
            SourceDirTextBox.Text = _launchOptions.InitialSourceDirectory;
        }

        if (!string.IsNullOrWhiteSpace(_launchOptions.InitialOutputHepPath))
        {
            _updatingOutputInternally = true;
            OutputPathTextBox.Text = _launchOptions.InitialOutputHepPath;
            _updatingOutputInternally = false;
        }

        UpdateExcludesPlaceholderVisibility();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_launchOptions.AutoStartPack && !string.IsNullOrWhiteSpace(_launchOptions.InitialSourceDirectory))
        {
            _currentStatusKey = "StatusAutoStart";
            UpdateStatusDisplay();
            await StartPackAsync();
        }
    }

    private void SyncLanguageRadioButtons()
    {
        LangJaRadio.IsChecked = _currentLanguage == "ja";
        LangZhRadio.IsChecked = _currentLanguage == "zh-CN";
        LangEnRadio.IsChecked = _currentLanguage == "en";
    }

    private void LanguageRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string langCode } || !IsLoaded)
        {
            return;
        }

        string normalized = PackagerStrings.NormalizeLanguage(langCode);
        if (string.Equals(_currentLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _currentLanguage = normalized;
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        string lang = _currentLanguage;

        Title = PackagerStrings.Get(lang, "WindowTitle");
        AppTitleText.Text = PackagerStrings.Get(lang, "AppTitle");
        AppSubtitleText.Text = PackagerStrings.Get(lang, "AppSubtitle");
        LanguageLabelText.Text = PackagerStrings.Get(lang, "LanguageLabel");

        DropZoneTitleText.Text = PackagerStrings.Get(lang, "DropZoneTitle");
        DropZoneSubtitleText.Text = PackagerStrings.Get(lang, "DropZoneSubtitle");

        SourceDirLabelText.Text = PackagerStrings.Get(lang, "SourceDirLabel");
        SourceDirTextBox.ToolTip = PackagerStrings.Get(lang, "SourceDirTooltip");
        OutputPathLabelText.Text = PackagerStrings.Get(lang, "OutputPathLabel");
        OutputPathTextBox.ToolTip = PackagerStrings.Get(lang, "OutputPathTooltip");
        BrowseButton.Content = PackagerStrings.Get(lang, "BrowseButton");
        BrowseButton.ToolTip = PackagerStrings.Get(lang, "BrowseButtonTooltip");
        SaveAsButton.Content = PackagerStrings.Get(lang, "SaveAsButton");
        SaveAsButton.ToolTip = PackagerStrings.Get(lang, "SaveAsButtonTooltip");

        OptionsTitleText.Text = PackagerStrings.Get(lang, "OptionsTitle");
        CompiledOnlyCheckBox.Content = PackagerStrings.Get(lang, "CompiledOnlyLabel");
        CompiledOnlyCheckBox.ToolTip = PackagerStrings.Get(lang, "CompiledOnlyTooltip");
        CompressCheckBox.Content = PackagerStrings.Get(lang, "CompressLabel");
        CompressCheckBox.ToolTip = PackagerStrings.Get(lang, "CompressTooltip");
        RandomModeLabelText.Text = PackagerStrings.Get(lang, "RandomModeLabel");
        RandomModeComboBox.ToolTip = PackagerStrings.Get(lang, "RandomModeTooltip");
        RandomFollowItem.Content = PackagerStrings.Get(lang, "RandomModeFollow");
        RandomOldSfmtItem.Content = PackagerStrings.Get(lang, "RandomModeOldSfmt");
        RandomNewItem.Content = PackagerStrings.Get(lang, "RandomModeNewRandom");

        ExcludesLabelText.Text = PackagerStrings.Get(lang, "ExcludesLabel");
        ExcludesTextBox.ToolTip = PackagerStrings.Get(lang, "ExcludesTooltip");
        ExcludesPlaceholderText.Text = PackagerStrings.Get(lang, "ExcludesPlaceholder");

        PackButton.Content = _isPacking
            ? PackagerStrings.Get(lang, "PackingButton")
            : PackagerStrings.Get(lang, "PackButton");
        PackButton.ToolTip = PackagerStrings.Get(lang, "PackButtonTooltip");
        OpenOutputButton.Content = PackagerStrings.Get(lang, "OpenOutputButton");
        OpenOutputButton.ToolTip = PackagerStrings.Get(lang, "OpenOutputButtonTooltip");
        CopyDetailsButton.Content = _isCopyFeedbackActive
            ? PackagerStrings.Get(lang, "CopiedButton")
            : PackagerStrings.Get(lang, "CopyDetailsButton");
        CopyDetailsButton.ToolTip = PackagerStrings.Get(lang, "CopyDetailsButtonTooltip");
        SaveLogButton.Content = PackagerStrings.Get(lang, "SaveLogButton");
        SaveLogButton.ToolTip = PackagerStrings.Get(lang, "SaveLogButtonTooltip");

        LogHeaderText.Text = PackagerStrings.Get(lang, "LogHeader");

        if (_lastResult is not null && !_isPacking)
        {
            LogTextBox.Text = DiagnosticFormatter.FormatResult(_lastResult, lang);
        }

        UpdateStatusDisplay();
    }

    private void UpdateStatusDisplay()
    {
        string lang = _currentLanguage;
        if (_isPacking && !string.IsNullOrEmpty(_currentProgressStageKey))
        {
            StatusTextBlock.Text = PackagerStrings.Get(lang, _currentProgressStageKey);
            StatusDot.Fill = (Brush)FindResource("BorderAccentBrush");
            return;
        }

        if (_lastResult is not null)
        {
            StatusTextBlock.Text = DiagnosticFormatter.FormatStatusLine(_lastResult, lang);
            StatusDot.Fill = (Brush)FindResource(_lastResult.Success ? "StatusSuccessBrush" : "StatusErrorBrush");
            return;
        }

        StatusTextBlock.Text = PackagerStrings.Get(lang, _currentStatusKey);
        StatusDot.Fill = (Brush)FindResource("TextMutedBrush");
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_isPacking)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        string? folder = TryExtractDroppedDirectory(e.Data);
        if (folder is not null)
        {
            e.Effects = DragDropEffects.Copy;
            SetDropHighlight(active: true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            SetDropHighlight(active: false);
        }

        e.Handled = true;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        SetDropHighlight(active: false);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(active: false);
        if (_isPacking)
        {
            return;
        }

        string? folder = TryExtractDroppedDirectory(e.Data);
        if (folder is null)
        {
            return;
        }

        SetSourceDirectoryAndDefaultOutput(folder);
        e.Handled = true;
    }

    private void SetDropHighlight(bool active)
    {
        if (active)
        {
            DropZoneBorder.BorderBrush = (Brush)FindResource("BorderAccentBrush");
            DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(26, 38, 61));
            DropZoneTitleText.Text = PackagerStrings.Get(_currentLanguage, "DropZoneActive");
        }
        else
        {
            DropZoneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(21, 25, 35));
            DropZoneTitleText.Text = PackagerStrings.Get(_currentLanguage, "DropZoneTitle");
        }
    }

    private static string? TryExtractDroppedDirectory(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private void SetSourceDirectoryAndDefaultOutput(string directory)
    {
        _outputPathManuallyEdited = false;
        SourceDirTextBox.Text = directory;
        UpdateDefaultOutputFromSource(directory);
    }

    private void UpdateDefaultOutputFromSource(string sourceDir)
    {
        if (_outputPathManuallyEdited)
        {
            return;
        }

        string defaultOut = PackagerPathHelper.GetDefaultOutputHepPath(sourceDir);
        _updatingOutputInternally = true;
        OutputPathTextBox.Text = defaultOut;
        _updatingOutputInternally = false;
    }

    private void SourceDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateDefaultOutputFromSource(SourceDirTextBox.Text);
    }

    private void OutputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _updatingOutputInternally)
        {
            return;
        }

        _outputPathManuallyEdited = !string.IsNullOrWhiteSpace(OutputPathTextBox.Text);
    }

    private void ExcludesTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateExcludesPlaceholderVisibility();
    }

    private void UpdateExcludesPlaceholderVisibility()
    {
        ExcludesPlaceholderText.Visibility = string.IsNullOrEmpty(ExcludesTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasLog = !string.IsNullOrWhiteSpace(LogTextBox.Text);
        CopyDetailsButton.IsEnabled = hasLog;
        SaveLogButton.IsEnabled = hasLog;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = PackagerStrings.Get(_currentLanguage, "BrowseFolderTitle"),
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(SourceDirTextBox.Text) && Directory.Exists(SourceDirTextBox.Text))
        {
            dialog.InitialDirectory = SourceDirTextBox.Text;
        }

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            SetSourceDirectoryAndDefaultOutput(dialog.FolderName);
        }
    }

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = PackagerStrings.Get(_currentLanguage, "SaveHepTitle"),
            Filter = PackagerStrings.Get(_currentLanguage, "SaveHepFilter"),
            DefaultExt = ".hep",
            AddExtension = true,
        };

        string currentOut = OutputPathTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(currentOut))
        {
            try
            {
                string? dir = Path.GetDirectoryName(currentOut);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    dialog.InitialDirectory = dir;
                }

                dialog.FileName = Path.GetFileName(currentOut);
            }
            catch
            {
                // Ignore malformed user path.
            }
        }

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            _updatingOutputInternally = true;
            OutputPathTextBox.Text = dialog.FileName;
            _updatingOutputInternally = false;
            _outputPathManuallyEdited = true;
        }
    }

    private async void PackButton_Click(object sender, RoutedEventArgs e)
    {
        await StartPackAsync();
    }

    private async Task StartPackAsync()
    {
        if (_isPacking)
        {
            return;
        }

        RandomModeOverride randomMode = RandomModeComboBox.SelectedIndex switch
        {
            1 => RandomModeOverride.OldSfmt,
            2 => RandomModeOverride.NewRandom,
            _ => RandomModeOverride.FollowSettingJson,
        };

        var request = new PackRequest(
            SourceDirectory: SourceDirTextBox.Text,
            OutputHepPath: OutputPathTextBox.Text,
            CompiledOnly: CompiledOnlyCheckBox.IsChecked == true,
            Compress: CompressCheckBox.IsChecked == true,
            RandomMode: randomMode,
            ExcludePaths: PackagerPathHelper.ParseExcludePaths(ExcludesTextBox.Text));

        SetPackingState(true);
        _lastResult = null;
        LogTextBox.Clear();
        _currentProgressStageKey = "StageLoadingConfig";
        UpdateStatusDisplay();

        var progress = new Progress<PackProgress>(p =>
        {
            _currentProgressStageKey = p.StageKey;
            UpdateStatusDisplay();
        });

        try
        {
            PackResult result = await Task.Run(() => PackagerService.ExecutePack(request, progress));
            _lastResult = result;
            LogTextBox.Text = DiagnosticFormatter.FormatResult(result, _currentLanguage);

            PackProgressBar.IsIndeterminate = false;
            PackProgressBar.Value = 100;
            PackProgressBar.Foreground = (Brush)FindResource(result.Success ? "StatusSuccessBrush" : "StatusErrorBrush");

            OpenOutputButton.IsEnabled = result.Success && File.Exists(result.OutputHepPath);
        }
        finally
        {
            SetPackingState(false);
            _currentProgressStageKey = null;
            UpdateStatusDisplay();
        }
    }

    private void SetPackingState(bool packing)
    {
        _isPacking = packing;
        PackButton.IsEnabled = !packing;
        PackButton.Content = PackagerStrings.Get(_currentLanguage, packing ? "PackingButton" : "PackButton");
        SourceDirTextBox.IsEnabled = !packing;
        OutputPathTextBox.IsEnabled = !packing;
        BrowseButton.IsEnabled = !packing;
        SaveAsButton.IsEnabled = !packing;
        CompiledOnlyCheckBox.IsEnabled = !packing;
        CompressCheckBox.IsEnabled = !packing;
        RandomModeComboBox.IsEnabled = !packing;
        ExcludesTextBox.IsEnabled = !packing;

        if (packing)
        {
            OpenOutputButton.IsEnabled = false;
            PackProgressBar.IsIndeterminate = true;
            PackProgressBar.Foreground = (Brush)FindResource("AccentPrimaryBrush");
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        string targetPath = _lastResult?.Success == true
            ? _lastResult.OutputHepPath
            : OutputPathTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(targetPath);
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = PackagerStrings.Format(_currentLanguage, "StatusError", ex.Message);
            StatusDot.Fill = (Brush)FindResource("StatusErrorBrush");
        }
    }

    private async void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogTextBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(LogTextBox.Text);
            _isCopyFeedbackActive = true;
            CopyDetailsButton.Content = PackagerStrings.Get(_currentLanguage, "CopiedButton");
            await Task.Delay(1500);
            _isCopyFeedbackActive = false;
            CopyDetailsButton.Content = PackagerStrings.Get(_currentLanguage, "CopyDetailsButton");
        }
        catch
        {
            _isCopyFeedbackActive = false;
        }
    }

    private void SaveLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogTextBox.Text))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = PackagerStrings.Get(_currentLanguage, "SaveLogTitle"),
            Filter = PackagerStrings.Get(_currentLanguage, "SaveLogFilter"),
            DefaultExt = ".log",
            AddExtension = true,
            FileName = "hypera-packager.log",
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            try
            {
                File.WriteAllText(dialog.FileName, LogTextBox.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                StatusTextBlock.Text = PackagerStrings.Format(_currentLanguage, "LogSavedStatus", dialog.FileName);
                StatusDot.Fill = (Brush)FindResource("StatusSuccessBrush");
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = PackagerStrings.Format(_currentLanguage, "StatusError", ex.Message);
                StatusDot.Fill = (Brush)FindResource("StatusErrorBrush");
            }
        }
    }
}
