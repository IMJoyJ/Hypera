using System.Data;
using System.Drawing;
using System.Reflection;
using Hypera.Rendering;
using Hypera.Runtime;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem;

public class PluginAPICharContext
{
    internal PluginAPICharContext(EmueraEngine? engine, long charId)
    {
        BASE = new CharInt1dWrapper(engine, charId, "BASE");
        MAXBASE = new CharInt1dWrapper(engine, charId, "MAXBASE");
        ABL = new CharInt1dWrapper(engine, charId, "ABL");
        TALENT = new CharInt1dWrapper(engine, charId, "TALENT");
        EXP = new CharInt1dWrapper(engine, charId, "EXP");
        MARK = new CharInt1dWrapper(engine, charId, "MARK");
        PALAM = new CharInt1dWrapper(engine, charId, "PALAM");
        SOURCE = new CharInt1dWrapper(engine, charId, "SOURCE");
        EX = new CharInt1dWrapper(engine, charId, "EX");
        CFLAG = new CharInt1dWrapper(engine, charId, "CFLAG");
        JUEL = new CharInt1dWrapper(engine, charId, "JUEL");
        RELATION = new CharInt1dWrapper(engine, charId, "RELATION");
        EQUIP = new CharInt1dWrapper(engine, charId, "EQUIP");
        TEQUIP = new CharInt1dWrapper(engine, charId, "TEQUIP");
        STAIN = new CharInt1dWrapper(engine, charId, "STAIN");
        GOTJUEL = new CharInt1dWrapper(engine, charId, "GOTJUEL");
        NOWEX = new CharInt1dWrapper(engine, charId, "NOWEX");
        DOWNBASE = new CharInt1dWrapper(engine, charId, "DOWNBASE");
        CUP = new CharInt1dWrapper(engine, charId, "CUP");
        CDOWN = new CharInt1dWrapper(engine, charId, "CDOWN");
        TCVAR = new CharInt1dWrapper(engine, charId, "TCVAR");

        NAME = new CharStringWrapper(engine, charId, "NAME");
        CALLNAME = new CharStringWrapper(engine, charId, "CALLNAME");
        NICKNAME = new CharStringWrapper(engine, charId, "NICKNAME");
        MASTERNAME = new CharStringWrapper(engine, charId, "MASTERNAME");

        CSTR = new CharString1dWrapper(engine, charId, "CSTR");
        CDFLAG = new CharInt2dWrapper(engine, charId, "CDFLAG");
        UserDefined = new CharUserdefinedInt1dWrapper(engine, charId);
    }

    public CharUserdefinedInt1dWrapper UserDefined;
    public CharInt1dWrapper BASE;
    public CharInt1dWrapper MAXBASE;
    public CharInt1dWrapper ABL;
    public CharInt1dWrapper TALENT;
    public CharInt1dWrapper EXP;
    public CharInt1dWrapper MARK;
    public CharInt1dWrapper PALAM;
    public CharInt1dWrapper SOURCE;
    public CharInt1dWrapper EX;
    public CharInt1dWrapper CFLAG;
    public CharInt1dWrapper JUEL;
    public CharInt1dWrapper RELATION;
    public CharInt1dWrapper EQUIP;
    public CharInt1dWrapper TEQUIP;
    public CharInt1dWrapper STAIN;
    public CharInt1dWrapper GOTJUEL;
    public CharInt1dWrapper NOWEX;
    public CharInt1dWrapper DOWNBASE;
    public CharInt1dWrapper CUP;
    public CharInt1dWrapper CDOWN;
    public CharInt1dWrapper TCVAR;

    public CharStringWrapper NAME;
    public CharStringWrapper CALLNAME;
    public CharStringWrapper NICKNAME;
    public CharStringWrapper MASTERNAME;

    public CharString1dWrapper CSTR;
    public CharInt2dWrapper CDFLAG;
}

/// <summary>
/// Binary-compatible Emuera <see cref="PluginManager"/> shim routed via <see cref="AsyncLocal{T}"/>
/// so legacy plugins calling <c>PluginManager.GetInstance()</c> stay 100% isolated per <see cref="EmueraEngine"/>.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private static readonly AsyncLocal<PluginManager?> CurrentAmbient = new();

    private readonly EmueraEngine? _engine;
    private readonly Dictionary<string, IPluginMethod> _methods;
    private readonly List<PluginAssemblyLoadContext> _loadContexts = [];

    public PluginManager(EmueraEngine? engine = null)
    {
        _engine = engine;
        bool ignoreCase = engine?.Module.CompilerOptions.IgnoreCase ?? true;
        _methods = new Dictionary<string, IPluginMethod>(
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        Func<EmueraEngine?> acc = () => _engine;
        DAY = new GlobalInt1dWrapper(acc, "DAY");
        MONEY = new GlobalInt1dWrapper(acc, "MONEY");
        ITEM = new GlobalInt1dWrapper(acc, "ITEM");
        FLAG = new GlobalInt1dWrapper(acc, "FLAG");
        TFLAG = new GlobalInt1dWrapper(acc, "TFLAG");
        UP = new GlobalInt1dWrapper(acc, "UP");
        PALAMLV = new GlobalInt1dWrapper(acc, "PALAMLV");
        EXPLV = new GlobalInt1dWrapper(acc, "EXPLV");
        EJAC = new GlobalInt1dWrapper(acc, "EJAC");
        DOWN = new GlobalInt1dWrapper(acc, "DOWN");
        RESULT = new GlobalInt1dWrapper(acc, "RESULT");
        COUNT = new GlobalInt1dWrapper(acc, "COUNT");
        TARGET = new GlobalInt1dWrapper(acc, "TARGET");
        ASSI = new GlobalInt1dWrapper(acc, "ASSI");
        MASTER = new GlobalInt1dWrapper(acc, "MASTER");
        NOITEM = new GlobalInt1dWrapper(acc, "NOITEM");
        LOSEBASE = new GlobalInt1dWrapper(acc, "LOSEBASE");
        SELECTCOM = new GlobalInt1dWrapper(acc, "SELECTCOM");
        ASSIPLAY = new GlobalInt1dWrapper(acc, "ASSIPLAY");
        PREVCOM = new GlobalInt1dWrapper(acc, "PREVCOM");
        TIME = new GlobalInt1dWrapper(acc, "TIME");
        ITEMSALES = new GlobalInt1dWrapper(acc, "ITEMSALES");
        PLAYER = new GlobalInt1dWrapper(acc, "PLAYER");
        NEXTCOM = new GlobalInt1dWrapper(acc, "NEXTCOM");
        PBAND = new GlobalInt1dWrapper(acc, "PBAND");
        BOUGHT = new GlobalInt1dWrapper(acc, "BOUGHT");
        GLOBAL = new GlobalInt1dWrapper(acc, "GLOBAL");
        RANDDATA = new GlobalInt1dWrapper(acc, "RANDDATA");

        SAVESTR = new GlobalString1dWrapper(acc, "SAVESTR");
        TSTR = new GlobalString1dWrapper(acc, "TSTR");
        STR = new GlobalString1dWrapper(acc, "STR");
        RESULTS = new GlobalString1dWrapper(acc, "RESULTS");
        GLOBALS = new GlobalString1dWrapper(acc, "GLOBALS");

        NO = new GlobalConstInt1dWrapper(acc, "NO");
        ITEMPRICE = new GlobalConstInt1dWrapper(acc, "ITEMPRICE");

        ABLNAME = new GlobalConstString1dWrapper(acc, "ABLNAME");
        TALENTNAME = new GlobalConstString1dWrapper(acc, "TALENTNAME");
        EXPNAME = new GlobalConstString1dWrapper(acc, "EXPNAME");
        MARKNAME = new GlobalConstString1dWrapper(acc, "MARKNAME");
        PALAMNAME = new GlobalConstString1dWrapper(acc, "PALAMNAME");
        ITEMNAME = new GlobalConstString1dWrapper(acc, "ITEMNAME");
        TRAINNAME = new GlobalConstString1dWrapper(acc, "TRAINNAME");
        BASENAME = new GlobalConstString1dWrapper(acc, "BASENAME");
        SOURCENAME = new GlobalConstString1dWrapper(acc, "SOURCENAME");
        EXNAME = new GlobalConstString1dWrapper(acc, "EXNAME");
        EQUIPNAME = new GlobalConstString1dWrapper(acc, "EQUIPNAME");
        TEQUIPNAME = new GlobalConstString1dWrapper(acc, "TEQUIPNAME");
        FLAGNAME = new GlobalConstString1dWrapper(acc, "FLAGNAME");
        TFLAGNAME = new GlobalConstString1dWrapper(acc, "TFLAGNAME");
        CFLAGNAME = new GlobalConstString1dWrapper(acc, "CFLAGNAME");
        TCVARNAME = new GlobalConstString1dWrapper(acc, "TCVARNAME");
        CSTRNAME = new GlobalConstString1dWrapper(acc, "CSTRNAME");
        STAINNAME = new GlobalConstString1dWrapper(acc, "STAINNAME");
        CDFLAGNAME1 = new GlobalConstString1dWrapper(acc, "CDFLAGNAME1");
        CDFLAGNAME2 = new GlobalConstString1dWrapper(acc, "CDFLAGNAME2");
        STRNAME = new GlobalConstString1dWrapper(acc, "STRNAME");
        TSTRNAME = new GlobalConstString1dWrapper(acc, "TSTRNAME");
        SAVESTRNAME = new GlobalConstString1dWrapper(acc, "SAVESTRNAME");
        GLOBALNAME = new GlobalConstString1dWrapper(acc, "GLOBALNAME");
        GLOBALSNAME = new GlobalConstString1dWrapper(acc, "GLOBALSNAME");
    }

    public static PluginManager GetInstance() =>
        CurrentAmbient.Value ?? throw new InvalidOperationException(
            "No ambient PluginManager is bound to the current async execution flow. Use PluginManager.EnterScope().");

    public EmueraEngine? BoundEngine => _engine;

    public AmbientScope EnterScope() => new(this);

    public readonly struct AmbientScope : IDisposable
    {
        private readonly PluginManager? _previous;

        internal AmbientScope(PluginManager next)
        {
            _previous = CurrentAmbient.Value;
            CurrentAmbient.Value = next;
        }

        public void Dispose() => CurrentAmbient.Value = _previous;
    }

    public static PluginAPICharContext CreateCharContext(long charId) =>
        new(GetInstance()._engine, charId);

    public void LoadPluginsFromDirectory(string pluginsDirectory)
    {
        _methods.Clear();
        if (!Directory.Exists(pluginsDirectory))
        {
            return;
        }

        foreach (string dllPath in Directory.GetFiles(pluginsDirectory, "*.dll"))
        {
            string fullPath = Path.GetFullPath(dllPath);
            var alc = new PluginAssemblyLoadContext(fullPath);
            _loadContexts.Add(alc);

            Assembly pluginAssembly = alc.LoadFromAssemblyPath(fullPath);
            Type? manifestType = pluginAssembly
                .GetTypes()
                .FirstOrDefault(static t => t.Name == "PluginManifest" && typeof(PluginManifestAbstract).IsAssignableFrom(t));

            if (manifestType is null)
            {
                continue;
            }

            if (Activator.CreateInstance(manifestType) is PluginManifestAbstract manifest)
            {
                foreach (IPluginMethod method in manifest.GetPluginMethods())
                {
                    _methods[method.Name] = method;
                }
            }
        }
    }

    public void RegisterMethod(IPluginMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        _methods[method.Name] = method;
    }

    public IPluginMethod GetMethod(string name) => _methods[name];

    public bool HasMethod(string name) => _methods.ContainsKey(name);

    public void ExecuteLine(string line) =>
        throw new NotSupportedException("ExecuteLine dynamic fallback requires Wave 2 script compiler/evaluator integration.");

    public void Print(string text) => AppendText(text);
    public void PrintError(string text) => AppendText(text);
    public void PrintC(string text, bool aligmentRight = false) => AppendText(text);
    public void PrintPlain(string text) => AppendText(text);
    public void PrintPlainWithSingleLine(string text) => AppendText(text);
    public void PrintSingleLine(string text) => AppendText(text);
    public void PrintSystemLine(string text) => AppendText(text);
    public void PrintTemporaryLine(string text) => AppendText(text);
    public void PrintButton(string text, long id) =>
        _engine?.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.PrintButtonSpan, default, text, ButtonIntValue: id));
    public void PrintButtonC(string text, long id, bool aligmentRight = false) => PrintButton(text, id);
    public void PrintBar(string text = "", bool isConst = true) => AppendText(string.IsNullOrEmpty(text) ? "----------------" : text);
    public void PrintHtml(string htmlText, bool toBuffer = false) => AppendText(htmlText);
    public void PrintImage(string resourceName, int width, int height, int y, string? buttonResourceName = null, string? mapResourceName = null) =>
        _engine?.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.DrawSprite, new LogicalRect(0, y, width, height), ResourceKey: resourceName));
    public void PrintNewLine() =>
        _engine?.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.NewLine, default));
    public void FlushConsole(bool force = false) { }
    public void DebugPrint(string text) { }
    public void ClearDisplay() =>
        _engine?.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.ClearAll, default));
    public void SetBgColor(Color color) { }
    public void SetFont(string fontName) { }
    public Point GetMousePosition() => Point.Empty;
    public void WaitInput(bool oneInput = true, int timelimit = -1) { }
    public void ReadAnyKey() { }
    public void Await(int time) { }
    public void ForceStopTimer() { }
    public void Quit(bool force = false) { }

    public long[] GetCharacterIDs()
    {
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        long[] ids = new long[chars.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = chars.GetCharacterNo(i);
        }

        return ids;
    }

    public long GetIntVar(string name, int index = 0)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        return vars.TryResolveSlot(name, out int slot) ? vars.GetInt(slot, [index]) : 0L;
    }

    public string GetStrVar(string name, int index = 0)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        return vars.TryResolveSlot(name, out int slot) ? vars.GetStr(slot, [index]) : string.Empty;
    }

    public void SetIntVar(string name, long val, int index = 0)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        if (vars.TryResolveSlot(name, out int slot))
        {
            vars.SetInt(slot, val, [index]);
        }
    }

    public void SetStrVar(string name, string val, int index = 0)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        if (vars.TryResolveSlot(name, out int slot))
        {
            vars.SetStr(slot, val, [index]);
        }
    }

    public void SetCharVar(string name, long charId, string key, long value) =>
        SetCharVar(name, charId, GlobalInt1dWrapper.ResolveKeyword(_engine, name, key), value);

    public void SetCharVar(string name, long charId, long key, long value)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        if (vars.TryResolveSlot(name, out int slot))
        {
            chars.SetInt(checked((int)charId), slot, value, [key]);
        }
    }

    public long GetCharVar(string name, long charId, long key)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        return vars.TryResolveSlot(name, out int slot) ? chars.GetInt(checked((int)charId), slot, [key]) : 0L;
    }

    public long GetCharVar(string name, long charId, string key) =>
        GetCharVar(name, charId, GlobalInt1dWrapper.ResolveKeyword(_engine, name, key));

    public DataTable? GetDataTable(string name) =>
        GlobalInt1dWrapper.RequireVariables(_engine).TryGetDataTable(name);

    public IEnumerable<string> GetStackTrace() =>
        _engine?.GetSourceStackTrace().Select(static loc => $"{loc.DisplayName}:{loc.LineNo}") ?? [];

    private void AppendText(string text) =>
        _engine?.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.PrintTextSpan, default, text));

    public void Dispose()
    {
        _methods.Clear();
        foreach (PluginAssemblyLoadContext alc in _loadContexts)
        {
            alc.Unload();
        }

        _loadContexts.Clear();
    }

    public GlobalInt1dWrapper DAY;
    public GlobalInt1dWrapper MONEY;
    public GlobalInt1dWrapper ITEM;
    public GlobalInt1dWrapper FLAG;
    public GlobalInt1dWrapper TFLAG;
    public GlobalInt1dWrapper UP;
    public GlobalInt1dWrapper PALAMLV;
    public GlobalInt1dWrapper EXPLV;
    public GlobalInt1dWrapper EJAC;
    public GlobalInt1dWrapper DOWN;
    public GlobalInt1dWrapper RESULT;
    public GlobalInt1dWrapper COUNT;
    public GlobalInt1dWrapper TARGET;
    public GlobalInt1dWrapper ASSI;
    public GlobalInt1dWrapper MASTER;
    public GlobalInt1dWrapper NOITEM;
    public GlobalInt1dWrapper LOSEBASE;
    public GlobalInt1dWrapper SELECTCOM;
    public GlobalInt1dWrapper ASSIPLAY;
    public GlobalInt1dWrapper PREVCOM;
    public GlobalInt1dWrapper TIME;
    public GlobalInt1dWrapper ITEMSALES;
    public GlobalInt1dWrapper PLAYER;
    public GlobalInt1dWrapper NEXTCOM;
    public GlobalInt1dWrapper PBAND;
    public GlobalInt1dWrapper BOUGHT;
    public GlobalInt1dWrapper GLOBAL;
    public GlobalInt1dWrapper RANDDATA;

    public GlobalString1dWrapper SAVESTR;
    public GlobalString1dWrapper TSTR;
    public GlobalString1dWrapper STR;
    public GlobalString1dWrapper RESULTS;
    public GlobalString1dWrapper GLOBALS;

    public GlobalConstInt1dWrapper NO;
    public GlobalConstInt1dWrapper ITEMPRICE;

    public GlobalConstString1dWrapper ABLNAME;
    public GlobalConstString1dWrapper TALENTNAME;
    public GlobalConstString1dWrapper EXPNAME;
    public GlobalConstString1dWrapper MARKNAME;
    public GlobalConstString1dWrapper PALAMNAME;
    public GlobalConstString1dWrapper ITEMNAME;
    public GlobalConstString1dWrapper TRAINNAME;
    public GlobalConstString1dWrapper BASENAME;
    public GlobalConstString1dWrapper SOURCENAME;
    public GlobalConstString1dWrapper EXNAME;
    public GlobalConstString1dWrapper EQUIPNAME;
    public GlobalConstString1dWrapper TEQUIPNAME;
    public GlobalConstString1dWrapper FLAGNAME;
    public GlobalConstString1dWrapper TFLAGNAME;
    public GlobalConstString1dWrapper CFLAGNAME;
    public GlobalConstString1dWrapper TCVARNAME;
    public GlobalConstString1dWrapper CSTRNAME;
    public GlobalConstString1dWrapper STAINNAME;
    public GlobalConstString1dWrapper CDFLAGNAME1;
    public GlobalConstString1dWrapper CDFLAGNAME2;
    public GlobalConstString1dWrapper STRNAME;
    public GlobalConstString1dWrapper TSTRNAME;
    public GlobalConstString1dWrapper SAVESTRNAME;
    public GlobalConstString1dWrapper GLOBALNAME;
    public GlobalConstString1dWrapper GLOBALSNAME;
}
