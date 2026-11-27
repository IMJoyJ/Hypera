using System.Globalization;

namespace Hypera.Packager;

/// <summary>
/// Built-in UI localization dictionary for <see cref="Hypera.Packager"/>.
/// Default language is Japanese (<c>ja</c>), with live switching to Simplified Chinese (<c>zh-CN</c>) and English (<c>en</c>).
/// </summary>
public static class PackagerStrings
{
    public const string DefaultLanguage = "ja";

    public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages =
    [
        ("ja", "日本語"),
        ("zh-CN", "简体中文"),
        ("en", "English"),
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = new(StringComparer.Ordinal)
        {
            ["WindowTitle"] = "Hypera HEP パッケージャ",
            ["AppTitle"] = "Hypera HEP パッケージャ",
            ["AppSubtitle"] = "Emuera / ERB プロジェクトを厳格コンパイルし、単一 .hep アーカイブへパッケージングします",
            ["LanguageLabel"] = "表示言語:",

            ["DropZoneTitle"] = "ゲームフォルダをここにドラッグ＆ドロップ",
            ["DropZoneSubtitle"] = "フォルダをウィンドウ内へドロップするか、Hypera.Packager.exe アイコンへ直接ドロップすると自動パックを実行します",
            ["DropZoneActive"] = "ドロップしてゲームフォルダを設定します...",

            ["SourceDirLabel"] = "ゲームフォルダ",
            ["SourceDirTooltip"] = "CSV / ERB / ERH / リソースを含む Emuera ゲームのルートディレクトリ",
            ["OutputPathLabel"] = "出力 .hep ファイル",
            ["OutputPathTooltip"] = "生成される .hep パッケージの保存先パス",
            ["BrowseButton"] = "参照...",
            ["BrowseButtonTooltip"] = "ゲームフォルダを選択します",
            ["SaveAsButton"] = "保存先...",
            ["SaveAsButtonTooltip"] = "出力 .hep ファイルの保存先を指定します",

            ["OptionsTitle"] = "パッケージ設定",
            ["CompiledOnlyLabel"] = "コンパイル済み配布モード（ERB/ERH/sav/実行ファイルを除外）",
            ["CompiledOnlyTooltip"] = "有効にすると ERB/ERH ソースコード、sav フォルダ、*.exe/*.dll/*.pdb 等を除外し、コンパイル済み game.heb と必要リソースのみを同梱します",
            ["CompressLabel"] = "Deflate 圧縮を有効化",
            ["CompressTooltip"] = "パッケージ内の各エントリを Deflate 圧縮し、アーカイブサイズを削減します",
            ["RandomModeLabel"] = "乱数モード (UseNewRandom)",
            ["RandomModeTooltip"] = "setting.json の既定値に従うか、ロールバック再現性のある旧 SFMT または .NET Random を強制指定します",
            ["RandomModeFollow"] = "setting.json に従う（既定）",
            ["RandomModeOldSfmt"] = "旧 SFMT（ロールバック決定論的）",
            ["RandomModeNewRandom"] = "新 .NET Random（ロールバック時に再生成）",
            ["ExcludesLabel"] = "追加の除外パス（セミコロン ; または改行区切り）",
            ["ExcludesTooltip"] = "追加で除外する相対パスまたはフォルダプレフィックス（例: temp/; debug/notes.txt）",
            ["ExcludesPlaceholder"] = "例: temp/; extra/debug.txt",

            ["PackButton"] = ".hep へパック開始",
            ["PackingButton"] = "パック処理中...",
            ["PackButtonTooltip"] = "スクリプトを検証・コンパイルし、.hep パッケージを生成します",
            ["OpenOutputButton"] = "出力フォルダを開く",
            ["OpenOutputButtonTooltip"] = "エクスプローラーで出力先フォルダを開きます",
            ["CopyDetailsButton"] = "詳細をコピー",
            ["CopiedButton"] = "コピーしました",
            ["CopyDetailsButtonTooltip"] = "ログとコンパイル診断の詳細をクリップボードにコピーします",
            ["SaveLogButton"] = "ログを保存...",
            ["SaveLogButtonTooltip"] = "ログと診断詳細をテキストファイルに保存します",

            ["LogHeader"] = "処理ログ / コンパイル診断詳細",
            ["StatusReady"] = "待機中：ゲームフォルダを選択またはドロップしてください。",
            ["StatusAutoStart"] = "起動引数を検出しました。自動パックを開始します...",
            ["StageLoadingConfig"] = "設定ファイル (config / setting.json) を読み込み中...",
            ["StageLoadingCsv"] = "CSV / ERD / RENAME ゲームデータを読み込み中...",
            ["StageCompiling"] = "ERH / ERB スクリプトをコンパイル検証中...",
            ["StagePacking"] = ".hep アーカイブを生成中...",
            ["StatusSuccess"] = "完了：{0} 件のエントリを '{1}' にパックしました（{2}）。",
            ["StatusCompileFailed"] = "コンパイル失敗：エラー {0} 件、警告 {1} 件。下の詳細を確認してください。",
            ["StatusError"] = "エラー：{0}",
            ["StatusCancelled"] = "処理がキャンセルされました。",
            ["LogSavedStatus"] = "ログを '{0}' に保存しました。",

            ["BrowseFolderTitle"] = "パッケージ対象のゲームフォルダを選択",
            ["SaveHepTitle"] = "出力 .hep ファイルを指定",
            ["SaveHepFilter"] = "Hypera Engine Package (*.hep)|*.hep|すべてのファイル (*.*)|*.*",
            ["SaveLogTitle"] = "ログ・診断詳細を保存",
            ["SaveLogFilter"] = "ログファイル (*.log;*.txt)|*.log;*.txt|すべてのファイル (*.*)|*.*",

            ["SummarySuccessHeader"] = "=== パック成功 ===",
            ["SummaryCompileFailedHeader"] = "=== コンパイルエラーによりパックを中止しました ===",
            ["SummaryErrorHeader"] = "=== パック処理エラー ===",
            ["SummarySource"] = "ソースフォルダ: {0}",
            ["SummaryOutput"] = "出力ファイル  : {0}",
            ["SummaryEntries"] = "収録エントリ数: {0}",
            ["SummarySize"] = "出力サイズ    : {0}",
            ["SummaryBytecode"] = "バイトコード  : game.heb ({0} 関数 / {1} 命令)",
            ["SummaryBytecodeNone"] = "バイトコード  : なし（関数 0 件）",
            ["SummaryWarnings"] = "警告数        : {0}",
            ["SummaryErrors"] = "エラー数      : {0}",
            ["SummaryFileCount"] = "対象ファイル数: {0}",
            ["SummaryDiagnosticsHeader"] = "--- 診断メッセージ一覧 ---",
            ["SummaryExceptionHeader"] = "--- 例外詳細 ---",

            ["ValidationMissingSource"] = "ソースとなるゲームフォルダを指定してください。",
            ["ValidationSourceNotFound"] = "指定されたゲームフォルダが見つかりません: '{0}'",
            ["ValidationMissingOutput"] = "出力先の .hep ファイルパスを指定してください。",
            ["ValidationOutputIsDirectory"] = "出力先パスはファイルである必要がありますが、既存フォルダが指定されています: '{0}'",
        },

        ["zh-CN"] = new(StringComparer.Ordinal)
        {
            ["WindowTitle"] = "Hypera HEP 打包器",
            ["AppTitle"] = "Hypera HEP 打包器",
            ["AppSubtitle"] = "严格编译 Emuera / ERB 工程并打包为单个 .hep 归档文件",
            ["LanguageLabel"] = "界面语言:",

            ["DropZoneTitle"] = "将游戏目录拖拽到此处",
            ["DropZoneSubtitle"] = "拖入游戏文件夹到窗口任意位置，或直接拖到 Hypera.Packager.exe 图标上即可自动开始打包",
            ["DropZoneActive"] = "松开鼠标以设置游戏目录...",

            ["SourceDirLabel"] = "游戏目录",
            ["SourceDirTooltip"] = "包含 CSV / ERB / ERH / 资源文件的 Emuera 游戏根目录",
            ["OutputPathLabel"] = "输出 .hep 路径",
            ["OutputPathTooltip"] = "生成的 .hep 归档文件输出路径",
            ["BrowseButton"] = "浏览...",
            ["BrowseButtonTooltip"] = "选择游戏目录",
            ["SaveAsButton"] = "另存为...",
            ["SaveAsButtonTooltip"] = "选择输出 .hep 文件的保存位置",

            ["OptionsTitle"] = "打包选项",
            ["CompiledOnlyLabel"] = "仅分发编译产物（剥离 ERB/ERH/sav/可执行文件）",
            ["CompiledOnlyTooltip"] = "开启后将剔除 ERB/ERH 源码、sav 存档目录及 *.exe/*.dll/*.pdb 等二进制文件，仅保留编译后的 game.heb 与运行资源",
            ["CompressLabel"] = "启用 Deflate 压缩",
            ["CompressTooltip"] = "对包内条目启用 Deflate 压缩以减小 .hep 文件体积",
            ["RandomModeLabel"] = "随机数模式 (UseNewRandom)",
            ["RandomModeTooltip"] = "选择遵循 setting.json 默认值，或强制使用旧版确定性 SFMT / 新版 .NET Random",
            ["RandomModeFollow"] = "遵循 setting.json（默认）",
            ["RandomModeOldSfmt"] = "旧版 SFMT（回滚确定性）",
            ["RandomModeNewRandom"] = "新版 .NET Random（回滚重新生成）",
            ["ExcludesLabel"] = "自定义排除路径（分号 ; 或换行分隔）",
            ["ExcludesTooltip"] = "额外排除的相对路径或目录前缀（例如: temp/; debug/notes.txt）",
            ["ExcludesPlaceholder"] = "例如: temp/; extra/debug.txt",

            ["PackButton"] = "开始打包为 .hep",
            ["PackingButton"] = "正在编译并打包...",
            ["PackButtonTooltip"] = "执行静态编译校验并生成 .hep 归档文件",
            ["OpenOutputButton"] = "打开输出目录",
            ["OpenOutputButtonTooltip"] = "在文件资源管理器中打开输出目录并定位文件",
            ["CopyDetailsButton"] = "复制详细信息",
            ["CopiedButton"] = "已复制",
            ["CopyDetailsButtonTooltip"] = "将完整日志与编译诊断详情复制到剪贴板",
            ["SaveLogButton"] = "保存日志...",
            ["SaveLogButtonTooltip"] = "将完整日志与诊断详情导出为文本文件",

            ["LogHeader"] = "打包日志 / 编译诊断详情",
            ["StatusReady"] = "就绪：请选择或拖入游戏目录。",
            ["StatusAutoStart"] = "检测到启动参数，正在自动开始打包...",
            ["StageLoadingConfig"] = "正在加载配置 (config / setting.json)...",
            ["StageLoadingCsv"] = "正在加载 CSV / ERD / RENAME 游戏数据...",
            ["StageCompiling"] = "正在编译并校验 ERH / ERB 脚本...",
            ["StagePacking"] = "正在写入 .hep 归档文件...",
            ["StatusSuccess"] = "打包成功：共写入 {0} 个条目至 '{1}'（{2}）。",
            ["StatusCompileFailed"] = "编译失败：共 {0} 个错误，{1} 个警告。请查看下方详细信息。",
            ["StatusError"] = "错误：{0}",
            ["StatusCancelled"] = "操作已取消。",
            ["LogSavedStatus"] = "日志已保存至 '{0}'。",

            ["BrowseFolderTitle"] = "选择要打包的游戏目录",
            ["SaveHepTitle"] = "指定输出 .hep 文件",
            ["SaveHepFilter"] = "Hypera Engine Package (*.hep)|*.hep|所有文件 (*.*)|*.*",
            ["SaveLogTitle"] = "保存日志与诊断详情",
            ["SaveLogFilter"] = "日志文件 (*.log;*.txt)|*.log;*.txt|所有文件 (*.*)|*.*",

            ["SummarySuccessHeader"] = "=== 打包成功 ===",
            ["SummaryCompileFailedHeader"] = "=== 编译失败，已中止打包 ===",
            ["SummaryErrorHeader"] = "=== 打包过程中发生错误 ===",
            ["SummarySource"] = "源目录   : {0}",
            ["SummaryOutput"] = "输出文件 : {0}",
            ["SummaryEntries"] = "条目数量 : {0}",
            ["SummarySize"] = "文件大小 : {0}",
            ["SummaryBytecode"] = "字节码   : game.heb（{0} 个函数 / {1} 条指令）",
            ["SummaryBytecodeNone"] = "字节码   : 无（0 个函数）",
            ["SummaryWarnings"] = "警告数量 : {0}",
            ["SummaryErrors"] = "错误数量 : {0}",
            ["SummaryFileCount"] = "涉及文件 : {0}",
            ["SummaryDiagnosticsHeader"] = "--- 诊断详情列表 ---",
            ["SummaryExceptionHeader"] = "--- 异常详情 ---",

            ["ValidationMissingSource"] = "请指定源游戏目录。",
            ["ValidationSourceNotFound"] = "找不到指定的游戏目录：'{0}'",
            ["ValidationMissingOutput"] = "请指定输出 .hep 文件路径。",
            ["ValidationOutputIsDirectory"] = "输出路径必须为文件路径，不能是已存在的目录：'{0}'",
        },

        ["en"] = new(StringComparer.Ordinal)
        {
            ["WindowTitle"] = "Hypera HEP Packager",
            ["AppTitle"] = "Hypera HEP Packager",
            ["AppSubtitle"] = "Strictly compile Emuera / ERB projects and package them into a single .hep archive",
            ["LanguageLabel"] = "Language:",

            ["DropZoneTitle"] = "Drag & Drop a Game Folder Here",
            ["DropZoneSubtitle"] = "Drop a folder onto this window, or drop it directly onto Hypera.Packager.exe to auto-pack",
            ["DropZoneActive"] = "Release to select game directory...",

            ["SourceDirLabel"] = "Source Directory",
            ["SourceDirTooltip"] = "Root Emuera game directory containing CSV / ERB / ERH / asset files",
            ["OutputPathLabel"] = "Output .hep Path",
            ["OutputPathTooltip"] = "Destination path for the generated .hep package",
            ["BrowseButton"] = "Browse...",
            ["BrowseButtonTooltip"] = "Select source game directory",
            ["SaveAsButton"] = "Save As...",
            ["SaveAsButtonTooltip"] = "Choose output .hep file location",

            ["OptionsTitle"] = "Package Options",
            ["CompiledOnlyLabel"] = "Compiled-only distribution (strip ERB/ERH/sav/binaries)",
            ["CompiledOnlyTooltip"] = "Strips raw ERB/ERH scripts, sav/ files, and host binaries (*.exe/*.dll/*.pdb), keeping compiled game.heb and runtime assets",
            ["CompressLabel"] = "Compress entries (Deflate)",
            ["CompressTooltip"] = "Compresses archive entries using optimal Deflate compression",
            ["RandomModeLabel"] = "Random Mode (UseNewRandom)",
            ["RandomModeTooltip"] = "Follow setting.json default or override UseNewRandom with Old SFMT or New .NET Random",
            ["RandomModeFollow"] = "Follow setting.json (Default)",
            ["RandomModeOldSfmt"] = "Old SFMT (Deterministic rollback)",
            ["RandomModeNewRandom"] = "New .NET Random (Fresh on rollback)",
            ["ExcludesLabel"] = "Extra Exclude Paths (semicolon ; or newline separated)",
            ["ExcludesTooltip"] = "Additional relative file paths or directory prefixes to exclude (e.g. temp/; debug/notes.txt)",
            ["ExcludesPlaceholder"] = "e.g. temp/; extra/debug.txt",

            ["PackButton"] = "Pack to .hep",
            ["PackingButton"] = "Packing...",
            ["PackButtonTooltip"] = "Compile scripts and build the .hep package",
            ["OpenOutputButton"] = "Open Output Folder",
            ["OpenOutputButtonTooltip"] = "Open the output directory in File Explorer",
            ["CopyDetailsButton"] = "Copy Details",
            ["CopiedButton"] = "Copied!",
            ["CopyDetailsButtonTooltip"] = "Copy full log and diagnostic details to the clipboard",
            ["SaveLogButton"] = "Save Log...",
            ["SaveLogButtonTooltip"] = "Save log and diagnostic details to a text file",

            ["LogHeader"] = "Output Log & Compiler Diagnostics",
            ["StatusReady"] = "Ready: select or drop a game folder to begin.",
            ["StatusAutoStart"] = "Launch folder detected. Starting automatic pack...",
            ["StageLoadingConfig"] = "Loading configuration (config / setting.json)...",
            ["StageLoadingCsv"] = "Loading CSV / ERD / RENAME game data...",
            ["StageCompiling"] = "Compiling ERH / ERB scripts...",
            ["StagePacking"] = "Writing .hep archive...",
            ["StatusSuccess"] = "Success: packed {0} entries into '{1}' ({2}).",
            ["StatusCompileFailed"] = "Compilation failed: {0} error(s), {1} warning(s). See details below.",
            ["StatusError"] = "Error: {0}",
            ["StatusCancelled"] = "Operation cancelled.",
            ["LogSavedStatus"] = "Saved log to '{0}'.",

            ["BrowseFolderTitle"] = "Select Game Directory to Package",
            ["SaveHepTitle"] = "Save Output .hep Package",
            ["SaveHepFilter"] = "Hypera Engine Package (*.hep)|*.hep|All Files (*.*)|*.*",
            ["SaveLogTitle"] = "Save Log & Diagnostics",
            ["SaveLogFilter"] = "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",

            ["SummarySuccessHeader"] = "=== Pack Succeeded ===",
            ["SummaryCompileFailedHeader"] = "=== Compilation Failed - Pack Aborted ===",
            ["SummaryErrorHeader"] = "=== Pack Failed ===",
            ["SummarySource"] = "Source Dir : {0}",
            ["SummaryOutput"] = "Output File: {0}",
            ["SummaryEntries"] = "Entries    : {0}",
            ["SummarySize"] = "Size       : {0}",
            ["SummaryBytecode"] = "Bytecode   : game.heb ({0} functions / {1} instructions)",
            ["SummaryBytecodeNone"] = "Bytecode   : none (0 functions)",
            ["SummaryWarnings"] = "Warnings   : {0}",
            ["SummaryErrors"] = "Errors     : {0}",
            ["SummaryFileCount"] = "Files      : {0}",
            ["SummaryDiagnosticsHeader"] = "--- Diagnostics ---",
            ["SummaryExceptionHeader"] = "--- Exception Details ---",

            ["ValidationMissingSource"] = "Please specify a source game directory.",
            ["ValidationSourceNotFound"] = "Source directory not found: '{0}'",
            ["ValidationMissingOutput"] = "Please specify an output .hep file path.",
            ["ValidationOutputIsDirectory"] = "Output path must be a file, not an existing directory: '{0}'",
        },
    };

    public static IReadOnlyCollection<string> AllKeys => Packs[DefaultLanguage].Keys;

    public static string NormalizeLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return DefaultLanguage;
        }

        string trimmed = code.Trim();
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        if (trimmed.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }

        return Packs.ContainsKey(trimmed) ? trimmed : DefaultLanguage;
    }

    public static string Get(string? languageCode, string key)
    {
        string lang = NormalizeLanguage(languageCode);
        if (Packs.TryGetValue(lang, out Dictionary<string, string>? dict) && dict.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (Packs[DefaultLanguage].TryGetValue(key, out string? fallback))
        {
            return fallback;
        }

        return key;
    }

    public static string Format(string? languageCode, string key, params object?[] args)
    {
        string template = Get(languageCode, key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
