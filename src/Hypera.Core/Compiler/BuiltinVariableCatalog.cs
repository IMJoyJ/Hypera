using System.Collections.Frozen;
using System.Collections.Immutable;
using Hypera.Bytecode;

namespace Hypera.Compiler;

/// <summary>
/// Static metadata for a built-in Emuera variable or pseudo-variable.
/// </summary>
public sealed record BuiltinVariableSpec(
    string Name,
    VariableValueKind ValueKind,
    int ArrayRank,
    ImmutableArray<int> DefaultDimensions,
    bool IsCharaData = false,
    bool IsSavedata = false,
    bool IsGlobal = false,
    bool IsConst = false,
    bool IsCalc = false,
    bool IsReadOnly = false,
    bool IsFunctionLocal = false,
    bool IsDynamicUserContainer = false,
    string? AssociatedCsvNameTable = null);

/// <summary>
/// Authoritative catalog of all 171 active Emuera built-in variables (161 named tokens + 10 dynamic user-var containers).
/// </summary>
public static class BuiltinVariableCatalog
{
    public static ImmutableArray<BuiltinVariableSpec> All { get; } = CreateAll();

    public static FrozenDictionary<string, BuiltinVariableSpec> CaseInsensitive { get; } =
        All.Where(static v => !v.IsDynamicUserContainer)
           .DistinctBy(static v => v.Name, StringComparer.OrdinalIgnoreCase)
           .ToFrozenDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, BuiltinVariableSpec> CaseSensitive { get; } =
        All.Where(static v => !v.IsDynamicUserContainer)
           .DistinctBy(static v => v.Name, StringComparer.Ordinal)
           .ToFrozenDictionary(static v => v.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, bool ignoreCase, out BuiltinVariableSpec? spec) =>
        (ignoreCase ? CaseInsensitive : CaseSensitive).TryGetValue(name, out spec);

    private static ImmutableArray<BuiltinVariableSpec> CreateAll()
    {
        ImmutableArray<int> dim0 = [];
        ImmutableArray<int> int1000 = [1000];
        ImmutableArray<int> int10000 = [10000];
        ImmutableArray<int> int625 = [625];
        ImmutableArray<int> str100 = [100];
        ImmutableArray<int> str20000 = [20000];
        ImmutableArray<int> int100x100 = [100, 100];
        ImmutableArray<int> int1x1 = [1, 1];
        ImmutableArray<int> int100x100x100 = [100, 100, 100];

        var list = new List<BuiltinVariableSpec>(175)
        {
            // Global Saved Integer 1D Arrays (52: 26 named + A..Z)
            Int1D("DAY", int1000, saved: true, csv: "DAY"),
            Int1D("MONEY", int1000, saved: true, csv: "MONEY"),
            Int1D("ITEM", int1000, saved: true, csv: "ITEM"),
            Int1D("FLAG", int10000, saved: true, csv: "FLAG"),
            Int1D("TFLAG", int1000, saved: false, csv: "TFLAG"),
            Int1D("UP", int1000, saved: false, csv: "PALAM"),
            Int1D("PALAMLV", int1000, saved: true, csv: "PALAM"),
            Int1D("EXPLV", int1000, saved: true, csv: "EXP"),
            Int1D("EJAC", int1000, saved: true),
            Int1D("DOWN", int1000, saved: false, csv: "PALAM"),
            Int1D("RESULT", int1000, saved: false),
            Int1D("COUNT", int1000, saved: true),
            Int1D("TARGET", int1000, saved: true),
            Int1D("ASSI", int1000, saved: true),
            Int1D("MASTER", int1000, saved: true),
            Int1D("NOITEM", int1000, saved: true),
            Int1D("LOSEBASE", int1000, saved: false, csv: "BASE"),
            Int1D("SELECTCOM", int1000, saved: true, csv: "TRAIN"),
            Int1D("ASSIPLAY", int1000, saved: true),
            Int1D("PREVCOM", int1000, saved: true, csv: "TRAIN"),
            Int1D("TIME", int1000, saved: true, csv: "TIME"),
            Int1D("ITEMSALES", int1000, saved: true, csv: "ITEM"),
            Int1D("PLAYER", int1000, saved: true),
            Int1D("NEXTCOM", int1000, saved: true, csv: "TRAIN"),
            Int1D("PBAND", int1000, saved: true, csv: "ITEM"),
            Int1D("BOUGHT", int1000, saved: true, csv: "ITEM"),
        };

        for (char ch = 'A'; ch <= 'Z'; ch++)
        {
            list.Add(Int1D(ch.ToString(), int1000, saved: true));
        }

        // Extended Global Integer 1D Arrays (2)
        list.Add(new BuiltinVariableSpec("GLOBAL", VariableValueKind.Int, 1, int1000, IsGlobal: true, AssociatedCsvNameTable: "GLOBAL"));
        list.Add(Int1D("RANDDATA", int625, saved: true));

        // Global String 1D Arrays (5) + 0D Scalar SAVEDATA_TEXT (1)
        list.Add(Str1D("SAVESTR", str100, saved: true, csv: "SAVESTR"));
        list.Add(Str1D("TSTR", str100, saved: true, csv: "TSTR"));
        list.Add(Str1D("STR", str20000, saved: false, csv: "STR"));
        list.Add(Str1D("RESULTS", str100, saved: false));
        list.Add(new BuiltinVariableSpec("GLOBALS", VariableValueKind.Str, 1, str100, IsGlobal: true, AssociatedCsvNameTable: "GLOBALS"));
        list.Add(new BuiltinVariableSpec("SAVEDATA_TEXT", VariableValueKind.Str, 0, dim0));

        // Character 0D Scalar-Per-Chara Variables (2 Int + 4 Str = 6)
        list.Add(new BuiltinVariableSpec("ISASSI", VariableValueKind.Int, 0, dim0, IsCharaData: true, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("NO", VariableValueKind.Int, 0, dim0, IsCharaData: true, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("NAME", VariableValueKind.Str, 0, dim0, IsCharaData: true, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("CALLNAME", VariableValueKind.Str, 0, dim0, IsCharaData: true, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("NICKNAME", VariableValueKind.Str, 0, dim0, IsCharaData: true, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("MASTERNAME", VariableValueKind.Str, 0, dim0, IsCharaData: true, IsSavedata: true));

        // Character 1D Per-Chara Arrays (21 Int + 1 Str = 22)
        list.Add(CharaInt1D("BASE", int1000, "BASE"));
        list.Add(CharaInt1D("MAXBASE", int1000, "BASE"));
        list.Add(CharaInt1D("ABL", int1000, "ABL"));
        list.Add(CharaInt1D("TALENT", int1000, "TALENT"));
        list.Add(CharaInt1D("EXP", int1000, "EXP"));
        list.Add(CharaInt1D("MARK", int1000, "MARK"));
        list.Add(CharaInt1D("PALAM", int1000, "PALAM"));
        list.Add(CharaInt1D("SOURCE", int1000, "SOURCE"));
        list.Add(CharaInt1D("EX", int1000, "EX"));
        list.Add(CharaInt1D("CFLAG", int1000, "CFLAG"));
        list.Add(CharaInt1D("JUEL", int1000, "PALAM"));
        list.Add(CharaInt1D("RELATION", int1000));
        list.Add(CharaInt1D("EQUIP", int1000, "EQUIP"));
        list.Add(CharaInt1D("TEQUIP", int1000, "TEQUIP"));
        list.Add(CharaInt1D("STAIN", int1000, "STAIN"));
        list.Add(CharaInt1D("GOTJUEL", int1000, "PALAM"));
        list.Add(CharaInt1D("NOWEX", int1000, "EX"));
        list.Add(CharaInt1D("DOWNBASE", int1000, "BASE"));
        list.Add(CharaInt1D("CUP", int1000, "PALAM"));
        list.Add(CharaInt1D("CDOWN", int1000, "PALAM"));
        list.Add(CharaInt1D("TCVAR", int1000, "TCVAR"));
        list.Add(new BuiltinVariableSpec("CSTR", VariableValueKind.Str, 1, str100, IsCharaData: true, IsSavedata: true, AssociatedCsvNameTable: "CSTR"));

        // Character 2D Per-Chara Array (1)
        list.Add(new BuiltinVariableSpec("CDFLAG", VariableValueKind.Int, 2, int1x1, IsCharaData: true, IsSavedata: true, AssociatedCsvNameTable: "CDFLAG"));

        // Global 2D & 3D Saved Integer Arrays (6 + 2 = 8)
        list.Add(new BuiltinVariableSpec("DITEMTYPE", VariableValueKind.Int, 2, int100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("DA", VariableValueKind.Int, 2, int100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("DB", VariableValueKind.Int, 2, int100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("DC", VariableValueKind.Int, 2, int100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("DD", VariableValueKind.Int, 2, int100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("DE", VariableValueKind.Int, 2, int100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("TA", VariableValueKind.Int, 3, int100x100x100, IsSavedata: true));
        list.Add(new BuiltinVariableSpec("TB", VariableValueKind.Int, 3, int100x100x100, IsSavedata: true));

        // Read-only CSV Constant 1D Arrays (1 Int + 28 Str = 29)
        list.Add(new BuiltinVariableSpec("ITEMPRICE", VariableValueKind.Int, 1, int1000, IsConst: true, IsReadOnly: true, AssociatedCsvNameTable: "ITEM"));
        string[] csvNameVars =
        [
            "ABLNAME", "TALENTNAME", "EXPNAME", "MARKNAME", "PALAMNAME", "ITEMNAME", "TRAINNAME",
            "BASENAME", "SOURCENAME", "EXNAME", "EQUIPNAME", "TEQUIPNAME", "FLAGNAME", "TFLAGNAME",
            "CFLAGNAME", "TCVARNAME", "CSTRNAME", "STAINNAME", "CDFLAGNAME1", "CDFLAGNAME2",
            "STRNAME", "TSTRNAME", "SAVESTRNAME", "GLOBALNAME", "GLOBALSNAME",
            "DAYNAME", "TIMENAME", "MONEYNAME",
        ];
        foreach (string nv in csvNameVars)
        {
            string baseTable = nv.EndsWith("NAME1", StringComparison.Ordinal) || nv.EndsWith("NAME2", StringComparison.Ordinal)
                ? nv
                : nv[..^4];
            list.Add(new BuiltinVariableSpec(nv, VariableValueKind.Str, 1, str100, IsConst: true, IsReadOnly: true, AssociatedCsvNameTable: baseTable));
        }

        // GAMEBASE Read-only 0D Scalars (7 Str + 5 Int = 12)
        list.Add(CalcStr0D("GAMEBASE_AUTHER", readOnly: true));
        list.Add(CalcStr0D("GAMEBASE_AUTHOR", readOnly: true));
        list.Add(CalcStr0D("GAMEBASE_INFO", readOnly: true));
        list.Add(CalcStr0D("GAMEBASE_YEAR", readOnly: true));
        list.Add(CalcStr0D("GAMEBASE_TITLE", readOnly: true));
        list.Add(CalcStr0D("GAMEBASE_URL", readOnly: true));
        list.Add(CalcStr0D("GAMEBASE_VERSIONNAME", readOnly: true));
        list.Add(CalcInt0D("GAMEBASE_GAMECODE", readOnly: true));
        list.Add(CalcInt0D("GAMEBASE_VERSION", readOnly: true));
        list.Add(CalcInt0D("GAMEBASE_ALLOWVERSION", readOnly: true));
        list.Add(CalcInt0D("GAMEBASE_DEFAULTCHARA", readOnly: true));
        list.Add(CalcInt0D("GAMEBASE_NOITEM", readOnly: true));

        // Pseudo / Calculated Variables (14: RAND is 1D, others 0D; WINDOW_TITLE is writable!)
        list.Add(new BuiltinVariableSpec("RAND", VariableValueKind.Int, 1, int1000, IsCalc: true, IsReadOnly: true));
        list.Add(CalcInt0D("CHARANUM", readOnly: true));
        list.Add(CalcStr0D("LASTLOAD_TEXT", readOnly: true));
        list.Add(CalcInt0D("LASTLOAD_VERSION", readOnly: true));
        list.Add(CalcInt0D("LASTLOAD_NO", readOnly: true));
        list.Add(CalcInt0D("LINECOUNT", readOnly: true));
        list.Add(CalcInt0D("ISTIMEOUT", readOnly: true));
        list.Add(CalcInt0D("__INT_MAX__", readOnly: true));
        list.Add(CalcInt0D("__INT_MIN__", readOnly: true));
        list.Add(CalcStr0D("EMUERA_VERSION", readOnly: true));
        list.Add(CalcStr0D("WINDOW_TITLE", readOnly: false));
        list.Add(CalcStr0D("MONEYLABEL", readOnly: true));
        list.Add(CalcStr0D("DRAWLINESTR", readOnly: true));
        list.Add(CalcStr0D("__FILE__", readOnly: true));
        list.Add(CalcStr0D("__FILE__", readOnly: true));
        list.Add(CalcStr0D("__FUNCTION__", readOnly: true));
        list.Add(CalcStr0D("__FUNCTION__", readOnly: true));
        list.Add(CalcInt0D("__LINE__", readOnly: true));
        list.Add(CalcInt0D("__LINE__", readOnly: true));

        // Function-Static Local Arrays (4)
        list.Add(new BuiltinVariableSpec("LOCAL", VariableValueKind.Int, 1, int1000, IsFunctionLocal: true));
        list.Add(new BuiltinVariableSpec("ARG", VariableValueKind.Int, 1, int1000, IsFunctionLocal: true));
        list.Add(new BuiltinVariableSpec("LOCALS", VariableValueKind.Str, 1, str100, IsFunctionLocal: true));
        list.Add(new BuiltinVariableSpec("ARGS", VariableValueKind.Str, 1, str100, IsFunctionLocal: true));

        // Dynamic User-Variable Container Descriptors (10) -> Total = 161 + 10 = 171
        list.Add(new BuiltinVariableSpec("VAR", VariableValueKind.Int, 1, [1], IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("VARS", VariableValueKind.Str, 1, [1], IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("VAR2D", VariableValueKind.Int, 2, [1, 1], IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("VARS2D", VariableValueKind.Str, 2, [1, 1], IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("VAR3D", VariableValueKind.Int, 3, [1, 1, 1], IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("VARS3D", VariableValueKind.Str, 3, [1, 1, 1], IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("CVAR", VariableValueKind.Int, 1, [1], IsCharaData: true, IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("CVARS", VariableValueKind.Str, 1, [1], IsCharaData: true, IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("CVAR2D", VariableValueKind.Int, 2, [1, 1], IsCharaData: true, IsDynamicUserContainer: true));
        list.Add(new BuiltinVariableSpec("CVARS2D", VariableValueKind.Str, 2, [1, 1], IsCharaData: true, IsDynamicUserContainer: true));

        return [.. list];
    }

    private static BuiltinVariableSpec Int1D(string name, ImmutableArray<int> dims, bool saved, string? csv = null) =>
        new(name, VariableValueKind.Int, 1, dims, IsSavedata: saved, AssociatedCsvNameTable: csv);

    private static BuiltinVariableSpec Str1D(string name, ImmutableArray<int> dims, bool saved, string? csv = null) =>
        new(name, VariableValueKind.Str, 1, dims, IsSavedata: saved, AssociatedCsvNameTable: csv);

    private static BuiltinVariableSpec CharaInt1D(string name, ImmutableArray<int> dims, string? csv = null) =>
        new(name, VariableValueKind.Int, 1, dims, IsCharaData: true, IsSavedata: true, AssociatedCsvNameTable: csv);

    private static BuiltinVariableSpec CalcInt0D(string name, bool readOnly) =>
        new(name, VariableValueKind.Int, 0, [], IsCalc: true, IsReadOnly: readOnly);

    private static BuiltinVariableSpec CalcStr0D(string name, bool readOnly) =>
        new(name, VariableValueKind.Str, 0, [], IsCalc: true, IsReadOnly: readOnly);
}
