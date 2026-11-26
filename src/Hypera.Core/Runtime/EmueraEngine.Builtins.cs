using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Persistence;
using Hypera.VirtualFs;

namespace Hypera.Runtime;

public sealed partial class EmueraEngine
{
    private readonly Dictionary<int, bool> _keyStates = [];
    private readonly HashSet<int> _keyTriggered = [];
    private readonly Dictionary<int, long> _hotkeyStates = [];
    private readonly List<string> _debugLog = [];
    private readonly Dictionary<string, byte[]> _memoryVarDataFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _memoryCharaDataFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _memoryTextFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<ConfigCode, object> DefaultConfigValues = ConfigLoader.CreateDefaultConfigValues();

    public IReadOnlyList<string> DebugLog => _debugLog;

    public void ConfigureConfigValues(IReadOnlyDictionary<ConfigCode, object> rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        ConfigRawValues = rawValues;
    }

    public void SetKeyState(int vkCode, bool isDown)
    {
        bool wasDown = _keyStates.TryGetValue(vkCode, out bool prev) && prev;
        _keyStates[vkCode] = isDown;
        if (isDown && !wasDown)
        {
            _keyTriggered.Add(vkCode);
        }
    }

    public bool IsKeyDown(int vkCode) =>
        IsWindowActive && (uint)vkCode <= 255u && _keyStates.TryGetValue(vkCode, out bool down) && down;

    public bool ConsumeKeyTriggered(int vkCode) =>
        IsWindowActive && (uint)vkCode <= 255u && _keyTriggered.Remove(vkCode);

    public void SetHotkeyState(int key, long state) => _hotkeyStates[key] = state;

    public long GetHotkeyState(int key) => _hotkeyStates.TryGetValue(key, out long val) ? val : 0L;

    public void InitHotkeyStates(int mode = 0)
    {
        _hotkeyStates.Clear();
        if (mode != 0)
        {
            _hotkeyStates[0] = mode;
        }
    }

    private readonly StringBuilder _pendingDebugLine = new();

    public void AppendDebugLog(string message, bool newLine = true)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _pendingDebugLine.Append(message);
        }

        if (newLine)
        {
            _debugLog.Add(_pendingDebugLine.ToString());
            _pendingDebugLine.Clear();
        }
    }

    public void ClearDebugLog()
    {
        _pendingDebugLine.Clear();
        _debugLog.Clear();
    }

    // -------------------------------------------------------------------------
    // GETCONFIG / GETCONFIGS Resolution
    // -------------------------------------------------------------------------

    internal long ResolveConfigInt(string key)
    {
        if (!ConfigLoader.TryResolveConfigCode(key, out ConfigCode code))
        {
            return 0L;
        }

        ReplaceConfiguration rep = FastVariables?.ReplaceConfig ?? Module.ReplaceConfig;
        CompilerOptions comp = Module.CompilerOptions;

        switch (code)
        {
            // Live RuntimeOptions
            case ConfigCode.WindowX: return Viewport.LogicalWidth;
            case ConfigCode.WindowY: return Viewport.LogicalHeight;
            case ConfigCode.FontSize: return Options.FontSize;
            case ConfigCode.LineHeight: return Options.LineHeight;
            case ConfigCode.PrintCPerLine: return Options.PrintCPerLine;
            case ConfigCode.PrintCLength: return Options.PrintCLength;
            case ConfigCode.SaveDataNos: return Options.SaveDataNos;
            case ConfigCode.MaxLog: return Options.MaxLog;
            case ConfigCode.InfiniteLoopAlertTime: return Options.InfiniteLoopAlertTimeMs;
            case ConfigCode.UseMouse: return Options.UseMouse ? 1L : 0L;
            case ConfigCode.UseSaveFolder: return Options.UseSaveFolder ? 1L : 0L;
            case ConfigCode.SystemSaveInBinary: return Options.SystemSaveInBinary ? 1L : 0L;
            case ConfigCode.ZipSaveData: return Options.ZipSaveData ? 1L : 0L;
            case ConfigCode.UseDebugCommand: return Options.UseDebugCommand ? 1L : 0L;
            case ConfigCode.ButtonWrap: return Options.ButtonWrap ? 1L : 0L;
            case ConfigCode.AllowLongInputByMouse: return Options.AllowLongInputByMouse ? 1L : 0L;
            case ConfigCode.ForeColor: return (Options.ForeColorRgba >> 8) & 0x00FF_FFFFu;
            case ConfigCode.BackColor: return (Options.BackColorRgba >> 8) & 0x00FF_FFFFu;
            case ConfigCode.FocusColor: return (Options.FocusColorRgba >> 8) & 0x00FF_FFFFu;
            case ConfigCode.LogColor: return (Options.LogColorRgba >> 8) & 0x00FF_FFFFu;

            // Live ReplaceConfiguration
            case ConfigCode.MoneyFirst: return rep.MoneyFirst ? 1L : 0L;
            case ConfigCode.MaxShopItem: return rep.MaxShopItem;
            case ConfigCode.ComAbleDefault: return rep.ComAbleDefault;
            case ConfigCode.pbandDef: return rep.PbandDef;
            case ConfigCode.RelationDef: return rep.RelationDef;

            // Live CompilerOptions
            case ConfigCode.IgnoreCase: return comp.IgnoreCase ? 1L : 0L;
            case ConfigCode.CompatiFunctionNoignoreCase: return comp.CompatiFunctionNoIgnoreCase ? 1L : 0L;
            case ConfigCode.SystemAllowFullSpace: return comp.SystemAllowFullSpace ? 1L : 0L;
            case ConfigCode.SystemIgnoreTripleSymbol: return comp.SystemIgnoreTripleSymbol ? 1L : 0L;
            case ConfigCode.CompatiErrorLine: return comp.CompatiErrorLine ? 1L : 0L;
            case ConfigCode.CompatiRAND: return comp.CompatiRAND ? 1L : 0L;
            case ConfigCode.CompatiCALLNAME: return comp.CompatiCALLNAME ? 1L : 0L;
            case ConfigCode.CompatiCallEvent: return comp.CompatiCallEvent ? 1L : 0L;
            case ConfigCode.CompatiSPChara: return comp.CompatiSPChara ? 1L : 0L;
            case ConfigCode.CompatiFuncArgOptional: return comp.CompatiFuncArgOptional ? 1L : 0L;
            case ConfigCode.CompatiFuncArgAutoConvert: return comp.CompatiFuncArgAutoConvert ? 1L : 0L;
            case ConfigCode.TimesNotRigorousCalculation: return comp.TimesNotRigorousCalculation ? 1L : 0L;
            case ConfigCode.SystemNoTarget: return comp.SystemNoTarget ? 1L : 0L;
            case ConfigCode.SystemIgnoreStringSet: return comp.SystemIgnoreStringSet ? 1L : 0L;
            case ConfigCode.UseERD: return comp.UseERD ? 1L : 0L;
            case ConfigCode.VarsizeDimConfig: return comp.VarsizeDimConfig ? 1L : 0L;
            case ConfigCode.CheckDuplicateIdentifier: return comp.CheckDuplicateIdentifier ? 1L : 0L;
            case ConfigCode.IgnoreUncalledFunction: return comp.IgnoreUncalledFunction ? 1L : 0L;
            case ConfigCode.AllowFunctionOverloading: return comp.AllowFunctionOverloading ? 1L : 0L;
            case ConfigCode.WarnFunctionOverloading: return comp.WarnFunctionOverloading ? 1L : 0L;
            case ConfigCode.WarnNormalFunctionOverloading: return comp.WarnNormalFunctionOverloading ? 1L : 0L;
        }

        object? raw = null;
        if (ConfigRawValues is not null && ConfigRawValues.TryGetValue(code, out object? configured))
        {
            raw = configured;
        }
        else if (DefaultConfigValues.TryGetValue(code, out object? defVal))
        {
            raw = defVal;
        }

        return raw switch
        {
            bool b => b ? 1L : 0L,
            int i => i,
            long l => l,
            uint u => (u >> 8) & 0x00FF_FFFFu,
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
            _ => 0L,
        };
    }

    internal string ResolveConfigStr(string key)
    {
        if (!ConfigLoader.TryResolveConfigCode(key, out ConfigCode code))
        {
            return string.Empty;
        }

        ReplaceConfiguration rep = FastVariables?.ReplaceConfig ?? Module.ReplaceConfig;
        switch (code)
        {
            case ConfigCode.FontName: return Options.FontName;
            case ConfigCode.MoneyLabel: return rep.MoneyLabel;
            case ConfigCode.LoadLabel: return rep.LoadLabel;
            case ConfigCode.DrawLineString: return rep.DrawLineString;
            case ConfigCode.BarChar1: return rep.BarChar1.ToString();
            case ConfigCode.BarChar2: return rep.BarChar2.ToString();
            case ConfigCode.TitleMenuString0: return rep.TitleMenuString0;
            case ConfigCode.TitleMenuString1: return rep.TitleMenuString1;
            case ConfigCode.TimeupLabel: return rep.TimeupLabel;
            case ConfigCode.ReplaceContinuationBR: return Module.CompilerOptions.ReplaceContinuationBR;
        }

        object? raw = null;
        if (ConfigRawValues is not null && ConfigRawValues.TryGetValue(code, out object? configured))
        {
            raw = configured;
        }
        else if (DefaultConfigValues.TryGetValue(code, out object? defVal))
        {
            raw = defVal;
        }

        return raw switch
        {
            string s => s,
            char c => c.ToString(),
            Enum e => e.ToString(),
            _ => string.Empty,
        };
    }

    // -------------------------------------------------------------------------
    // Method Function Lookup & Dynamic Invocation (EXISTMETH / GETMETH / GETMETHS)
    // -------------------------------------------------------------------------

    internal bool TryGetMethodFunction(string name, out FunctionDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (_functionsByName.TryGetValue(name.Trim(), out int fnId) &&
            (uint)fnId < (uint)Module.Functions.Length &&
            Module.Functions[fnId].IsMethodFunction)
        {
            descriptor = Module.Functions[fnId];
            return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // ENUMFILES (VFS + SaveDirectory)
    // -------------------------------------------------------------------------

    internal int EnumerateFilesForScript(string dirArg, string pattern, bool recursive, out string[] matchedPaths)
    {
        matchedPaths = [];
        string normalizedDir = VfsPathNormalizer.Normalize(dirArg ?? string.Empty);
        string effectivePattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;

        bool vfsHasDir = string.IsNullOrEmpty(normalizedDir) || FileSystem.DirectoryExists(normalizedDir);
        bool saveHasDir = false;
        string? saveFullDir = null;
        if (_saveDirectory is not null)
        {
            try
            {
                saveFullDir = string.IsNullOrEmpty(normalizedDir)
                    ? _saveDirectory.RootDirectory
                    : _saveDirectory.ResolveConfinedPath(normalizedDir);
                saveHasDir = Directory.Exists(saveFullDir);
            }
            catch
            {
                saveHasDir = false;
            }
        }

        if (!vfsHasDir && !saveHasDir)
        {
            return -1;
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (vfsHasDir)
        {
            foreach (string relPath in FileSystem.EnumerateFiles(normalizedDir, "*", recursive))
            {
                string fileName = Path.GetFileName(relPath);
                if (!FileSystemName.MatchesSimpleExpression(effectivePattern, fileName, ignoreCase: true))
                {
                    continue;
                }

                string winRel = relPath.Replace('/', '\\');
                if (seen.Add(winRel))
                {
                    results.Add(winRel);
                }
            }
        }

        if (saveHasDir && saveFullDir is not null)
        {
            SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string fullFile in Directory.EnumerateFiles(saveFullDir, "*", opt))
            {
                string fileName = Path.GetFileName(fullFile);
                if (!FileSystemName.MatchesSimpleExpression(effectivePattern, fileName, ignoreCase: true))
                {
                    continue;
                }

                string rel = Path.GetRelativePath(_saveDirectory!.RootDirectory, fullFile).Replace('/', '\\');
                if (seen.Add(rel))
                {
                    results.Add(rel);
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        matchedPaths = [.. results];
        return matchedPaths.Length;
    }

    // -------------------------------------------------------------------------
    // SAVEVAR / LOADVAR / SAVECHARA / LOADCHARA / CHK*DATA / FIND_*DATA
    // -------------------------------------------------------------------------

    internal void SaveVarData(string name, string description, IReadOnlyList<string> varNames)
    {
        VariableStore vars = RequireVariableStore();
        EraSaveFormat fmt = Options.ZipSaveData ? EraSaveFormat.GZipBinary : EraSaveFormat.Binary;
        var snap = new EraSaveSnapshot
        {
            Header = new EraSaveHeader(vars.GameBase.Code, vars.GameBase.Version, description ?? string.Empty, EraSaveFileType.Var, EraSaveSerializer.CurrentBinaryVersion, fmt),
        };

        foreach (string rawName in varNames)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            if (!vars.TryResolveExistingSlot(rawName.Trim(), out int slotId))
            {
                throw new KeyNotFoundException($"SAVEVAR: unknown variable '{rawName}'.");
            }

            VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
            if (slot.IsCharaData || slot.IsConst || slot.CalcKind != BuiltinCalcKind.None)
            {
                throw new InvalidOperationException($"SAVEVAR: variable '{slot.Name}' cannot be saved with SAVEVAR.");
            }

            if (slot.IntData is not null)
            {
                snap.Variables[slot.Name] = PackIntSaveValue(slot.IntData, Math.Max(1, slot.Rank), slot.Dim0, slot.Dim1, slot.Dim2);
            }
            else if (slot.StrData is not null)
            {
                snap.Variables[slot.Name] = PackStrSaveValue(slot.StrData, Math.Max(1, slot.Rank), slot.Dim0, slot.Dim1, slot.Dim2);
            }
        }

        byte[] payload = EraSaveSerializer.Save(snap, fmt);
        _memoryVarDataFiles[name.Trim()] = payload;
        if (_saveDirectory is not null)
        {
            string rel = _saveDirectory.GetDatVirtualPath(name, isCharaVar: false);
            _saveDirectory.WriteAllBytes(rel, payload);
        }
    }

    internal bool LoadVarData(string name)
    {
        VariableStore vars = RequireVariableStore();
        if (!TryReadDatBytes(name, isCharaVar: false, out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        EraSaveSnapshot snap = EraSaveSerializer.Load(bytes);
        if (snap.Header.FileType != EraSaveFileType.Var)
        {
            return false;
        }

        foreach ((string varName, EraSaveValue val) in snap.Variables)
        {
            if (!vars.TryResolveExistingSlot(varName, out int slotId))
            {
                continue;
            }

            VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
            if (slot.IsConst || slot.IsCharaData)
            {
                continue;
            }

            UnpackSaveValueIntoSlot(slot, val);
        }

        return true;
    }

    internal void SaveCharaData(string name, string description, IReadOnlyList<int> charaIndices)
    {
        VariableStore vars = RequireVariableStore();
        CharacterStore chars = vars.Characters;
        EraSaveFormat fmt = Options.ZipSaveData ? EraSaveFormat.GZipBinary : EraSaveFormat.Binary;

        var seen = new HashSet<int>();
        var snap = new EraSaveSnapshot
        {
            Header = new EraSaveHeader(vars.GameBase.Code, vars.GameBase.Version, description ?? string.Empty, EraSaveFileType.CharVar, EraSaveSerializer.CurrentBinaryVersion, fmt),
        };

        foreach (int idx in charaIndices)
        {
            if ((uint)idx >= (uint)chars.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(charaIndices), $"SAVECHARA: character index {idx} is out of range [0..{chars.Count}).");
            }

            if (!seen.Add(idx))
            {
                throw new InvalidOperationException($"SAVECHARA: duplicate character index {idx}.");
            }

            snap.Characters.Add(BuildCharacterSaveData(chars, idx));
        }

        byte[] payload = EraSaveSerializer.Save(snap, fmt);
        _memoryCharaDataFiles[name.Trim()] = payload;
        if (_saveDirectory is not null)
        {
            string rel = _saveDirectory.GetDatVirtualPath(name, isCharaVar: true);
            _saveDirectory.WriteAllBytes(rel, payload);
        }
    }

    internal bool LoadCharaData(string name)
    {
        VariableStore vars = RequireVariableStore();
        if (!TryReadDatBytes(name, isCharaVar: true, out byte[]? bytes) || bytes is null)
        {
            return false;
        }

        EraSaveSnapshot snap = EraSaveSerializer.Load(bytes);
        if (snap.Header.FileType != EraSaveFileType.CharVar)
        {
            return false;
        }

        CharacterStore chars = vars.Characters;
        foreach (EraCharacterSaveData cData in snap.Characters)
        {
            int idx = chars.AddVoidCharacter();
            if (chars.NoSlotId >= 0) chars.SetInt0D(idx, chars.NoSlotId, cData.No);
            if (chars.IsAssiSlotId >= 0) chars.SetInt0D(idx, chars.IsAssiSlotId, cData.IsAssi);
            if (chars.NameSlotId >= 0) chars.SetStr0D(idx, chars.NameSlotId, cData.Name);
            if (chars.CallNameSlotId >= 0) chars.SetStr0D(idx, chars.CallNameSlotId, cData.CallName);
            if (chars.NickNameSlotId >= 0) chars.SetStr0D(idx, chars.NickNameSlotId, cData.NickName);
            if (chars.MasterNameSlotId >= 0) chars.SetStr0D(idx, chars.MasterNameSlotId, cData.MasterName);
            ApplyCharacterVarDict(chars, idx, cData.BuiltinVariables);
            ApplyCharacterVarDict(chars, idx, cData.UserVariables);
        }

        return true;
    }

    internal long CheckDatFile(string name, bool isCharaVar, out string message)
    {
        VariableStore vars = RequireVariableStore();
        if (!TryReadDatBytes(name, isCharaVar, out byte[]? bytes) || bytes is null)
        {
            message = "File not found";
            return 1L;
        }

        return ValidateSaveHeaderBytes(bytes, vars.GameBase, isCharaVar ? EraSaveFileType.CharVar : EraSaveFileType.Var, out message);
    }

    internal long CheckGlobalData(out string message)
    {
        VariableStore vars = RequireVariableStore();
        if (_saveDirectory is not null)
        {
            string rel = _saveDirectory.GetGlobalSaveVirtualPath(Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(rel);
            if (File.Exists(fullPath))
            {
                return ValidateSaveHeaderBytes(File.ReadAllBytes(fullPath), vars.GameBase, EraSaveFileType.Global, out message);
            }
        }

        if (_memoryGlobalSnapshot is not null)
        {
            return ValidateSaveHeader(_memoryGlobalSnapshot.Header, vars.GameBase, EraSaveFileType.Global, out message);
        }

        message = "Global save file not found";
        return 1L;
    }

    internal string[] FindDatFiles(string pattern, bool isCharaVar)
    {
        string effectivePattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;
        string prefix = isCharaVar ? "chara_" : "var_";
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, byte[]> mem = isCharaVar ? _memoryCharaDataFiles : _memoryVarDataFiles;
        foreach (string key in mem.Keys)
        {
            if (FileSystemName.MatchesSimpleExpression(effectivePattern, key, ignoreCase: true) && seen.Add(key))
            {
                names.Add(key);
            }
        }

        if (_saveDirectory is not null)
        {
            string datDir = _saveDirectory.ResolveConfinedPath("dat");
            if (Directory.Exists(datDir))
            {
                foreach (string file in Directory.EnumerateFiles(datDir, $"{prefix}*.dat", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    {
                        string inner = fileName[prefix.Length..^4];
                        if (FileSystemName.MatchesSimpleExpression(effectivePattern, inner, ignoreCase: true) && seen.Add(inner))
                        {
                            names.Add(inner);
                        }
                    }
                }
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. names];
    }

    // -------------------------------------------------------------------------
    // SAVETEXT / LOADTEXT / OUTPUTLOG
    // -------------------------------------------------------------------------

    internal bool SaveTextFile(string content, bool isIntNo, long fileNo, string pathArg, bool forceSavDir, bool forceUtf8)
    {
        try
        {
            string relPath;
            if (isIntNo)
            {
                if (fileNo < 0 || fileNo > int.MaxValue)
                {
                    return false;
                }

                string fileName = $"txt{fileNo:00}.txt";
                relPath = (forceSavDir || Options.UseSaveFolder) ? $"sav/{fileName}" : fileName;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(pathArg))
                {
                    return false;
                }

                relPath = VfsPathNormalizer.Normalize(pathArg);
                if (relPath.Length == 0)
                {
                    return false;
                }

                string ext = VfsPathNormalizer.GetExtension(relPath).TrimStart('.');
                bool allowed = _saveDirectory is not null
                    ? _saveDirectory.IsExtensionAllowed(relPath)
                    : Options.ValidExtensions.Any(e => e.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    relPath = Path.ChangeExtension(relPath, ".txt").Replace('\\', '/');
                }
            }

            _memoryTextFiles[relPath] = content ?? string.Empty;
            if (_saveDirectory is not null)
            {
                Encoding enc = forceUtf8
                    ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                    : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                _saveDirectory.SaveText(relPath, content ?? string.Empty, enc);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal string LoadTextFile(bool isIntNo, long fileNo, string pathArg, bool forceSavDir, bool forceUtf8)
    {
        try
        {
            string relPath;
            if (isIntNo)
            {
                if (fileNo < 0 || fileNo > int.MaxValue)
                {
                    return string.Empty;
                }

                string fileName = $"txt{fileNo:00}.txt";
                relPath = (forceSavDir || Options.UseSaveFolder) ? $"sav/{fileName}" : fileName;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(pathArg))
                {
                    return string.Empty;
                }

                relPath = VfsPathNormalizer.Normalize(pathArg);
                if (relPath.Length == 0)
                {
                    return string.Empty;
                }

                string ext = VfsPathNormalizer.GetExtension(relPath).TrimStart('.');
                bool allowed = _saveDirectory is not null
                    ? _saveDirectory.IsExtensionAllowed(relPath)
                    : Options.ValidExtensions.Any(e => e.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    return string.Empty;
                }
            }

            string? loaded = null;
            if (_saveDirectory is not null && _saveDirectory.FileExists(relPath))
            {
                Encoding? enc = forceUtf8 ? Encoding.UTF8 : null;
                loaded = _saveDirectory.LoadText(relPath, enc);
            }
            else if (_memoryTextFiles.TryGetValue(relPath, out string? memText))
            {
                loaded = memText;
            }
            else if (FileSystem.FileExists(relPath))
            {
                byte[] bytes = FileSystem.ReadAllBytes(relPath);
                loaded = forceUtf8 ? Encoding.UTF8.GetString(bytes) : EncodingDetector.Default.Decode(bytes, out _);
            }

            return loaded is null ? string.Empty : loaded.Replace("\r", string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal bool WriteOutputLog(string filename, bool hideTimestamp)
    {
        try
        {
            IReadOnlyList<string> lines = ConsoleBuffer.GetAllPlainTextLines();
            var sb = new StringBuilder();
            if (!hideTimestamp)
            {
                sb.AppendLine($"=== OutputLog {DateTime.Now:yyyy/MM/dd HH:mm:ss} ===");
            }

            foreach (string line in lines)
            {
                sb.AppendLine(line);
            }

            string content = sb.ToString();
            LastOutputLog = content;

            string rel = string.IsNullOrWhiteSpace(filename)
                ? "emuera.log"
                : VfsPathNormalizer.Normalize(filename);
            if (rel.Length == 0)
            {
                rel = "emuera.log";
            }

            _memoryTextFiles[rel] = content;
            if (_saveDirectory is not null)
            {
                string fullPath = _saveDirectory.ResolveConfinedPath(rel);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Shared Character & Header Helpers
    // -------------------------------------------------------------------------

    private bool TryReadDatBytes(string name, bool isCharaVar, out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (_saveDirectory is not null)
        {
            try
            {
                string rel = _saveDirectory.GetDatVirtualPath(trimmed, isCharaVar);
                if (_saveDirectory.FileExists(rel))
                {
                    bytes = _saveDirectory.ReadAllBytes(rel);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        Dictionary<string, byte[]> mem = isCharaVar ? _memoryCharaDataFiles : _memoryVarDataFiles;
        return mem.TryGetValue(trimmed, out bytes);
    }

    private static long ValidateSaveHeaderBytes(byte[] bytes, GameBaseData gameBase, EraSaveFileType expectedType, out string message)
    {
        try
        {
            EraSaveHeader header = EraSaveSerializer.PeekHeader(bytes);
            return ValidateSaveHeader(header, gameBase, expectedType, out message);
        }
        catch (Exception ex)
        {
            message = $"Invalid save data: {ex.Message}";
            return 4L;
        }
    }

    private static long ValidateSaveHeader(EraSaveHeader header, GameBaseData gameBase, EraSaveFileType expectedType, out string message)
    {
        if (header.FileType != expectedType)
        {
            message = $"Save data type mismatch: expected {expectedType}, got {header.FileType}.";
            return 4L;
        }

        if (gameBase.Code != 0 && header.ScriptUniqueCode != gameBase.Code)
        {
            message = "Game code mismatch.";
            return 2L;
        }

        if (gameBase.Version != 0 &&
            header.ScriptVersion != gameBase.Version &&
            (gameBase.AllowVersion < 0 || header.ScriptVersion < gameBase.AllowVersion))
        {
            message = "Game version mismatch.";
            return 3L;
        }

        message = header.SaveDataText;
        return 0L;
    }

    private static EraCharacterSaveData BuildCharacterSaveData(CharacterStore chars, int c)
    {
        var charaData = new EraCharacterSaveData
        {
            No = chars.NoSlotId >= 0 ? chars.GetInt0D(c, chars.NoSlotId) : 0L,
            IsAssi = chars.IsAssiSlotId >= 0 ? chars.GetInt0D(c, chars.IsAssiSlotId) : 0L,
            Name = chars.NameSlotId >= 0 ? chars.GetStr0D(c, chars.NameSlotId) : string.Empty,
            CallName = chars.CallNameSlotId >= 0 ? chars.GetStr0D(c, chars.CallNameSlotId) : string.Empty,
            NickName = chars.NickNameSlotId >= 0 ? chars.GetStr0D(c, chars.NickNameSlotId) : string.Empty,
            MasterName = chars.MasterNameSlotId >= 0 ? chars.GetStr0D(c, chars.MasterNameSlotId) : string.Empty,
        };

        foreach (CharacterSlotSchema schema in chars.IntSlotSchemas)
        {
            if (!schema.IsSavedata ||
                schema.GlobalSlotId == chars.NoSlotId ||
                schema.GlobalSlotId == chars.IsAssiSlotId)
            {
                continue;
            }

            ReadOnlySpan<long> raw = chars.GetIntSpan(c, schema.GlobalSlotId);
            EraSaveValue val = PackIntSaveValue(raw, schema.SubRank, schema.Dim0, schema.Dim1, schema.Dim2);
            if (schema.IsUserDefined)
            {
                charaData.UserVariables[schema.Name] = val;
            }
            else
            {
                charaData.BuiltinVariables[schema.Name] = val;
            }
        }

        foreach (CharacterSlotSchema schema in chars.StrSlotSchemas)
        {
            if (!schema.IsSavedata ||
                schema.GlobalSlotId == chars.NameSlotId ||
                schema.GlobalSlotId == chars.CallNameSlotId ||
                schema.GlobalSlotId == chars.NickNameSlotId ||
                schema.GlobalSlotId == chars.MasterNameSlotId)
            {
                continue;
            }

            ReadOnlySpan<string> raw = chars.GetStrSpan(c, schema.GlobalSlotId);
            EraSaveValue val = PackStrSaveValue(raw, schema.SubRank, schema.Dim0, schema.Dim1, schema.Dim2);
            if (schema.IsUserDefined)
            {
                charaData.UserVariables[schema.Name] = val;
            }
            else
            {
                charaData.BuiltinVariables[schema.Name] = val;
            }
        }

        return charaData;
    }
}
