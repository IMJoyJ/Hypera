using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Hypera.Compiler;

/// <summary>
/// Categorizes how a statement's raw argument text is tokenized and parsed.
/// </summary>
public enum InstructionArgumentPattern : byte
{
    Void = 0,
    RawString = 1,
    RawStringNullable = 2,
    FormString = 3,
    FormStringNullable = 4,
    FormStringList = 5,
    IntExpr = 6,
    IntExprNullable = 7,
    IntExprList = 8,
    StrExpr = 9,
    StrExprNullable = 10,
    AnyExpr = 11,
    AnyExprNullable = 12,
    Assignment = 13,
    PrintV = 14,
    PrintDataVarInt = 15,
    StrDataVarStr = 16,
    PrintButton = 17,
    HtmlPrint = 18,
    HtmlTagSplit = 19,
    PrintImg = 20,
    PrintRect = 21,
    PrintSpace = 22,
    Color = 23,
    Bar = 24,
    Times = 25,
    InputInt = 26,
    InputStr = 27,
    TimedInputInt = 28,
    TimedInputStr = 29,
    TimedWait = 30,
    CallLike = 31,
    CallFormLike = 32,
    CallSharp = 33,
    ForLoop = 34,
    SelectCase = 35,
    CaseClause = 36,
    BitOp = 37,
    VarSet = 38,
    CVarSet = 39,
    ArrayShift = 40,
    ArrayRemove = 41,
    ArraySort = 42,
    ArrayCopy = 43,
    Split = 44,
    SwapVar = 45,
    SwapChara = 46,
    SortChara = 47,
    Power = 48,
    GetIntVar = 49,
    Varsize = 50,
    SaveData = 51,
    SaveVar = 52,
    SaveChara = 53,
    Ref = 54,
    RefByName = 55,
    DtColumnOptions = 56,
    ScopedVarDecl = 57,
}

/// <summary>
/// Metadata specification for one of the 304 built-in instructions.
/// </summary>
public sealed record BuiltinInstructionSpec(
    BuiltinInstructionKind Kind,
    string Name,
    InstructionArgumentPattern ArgPattern,
    InstructionTraitFlags Traits,
    PrintModifierFlags PrintFlags = PrintModifierFlags.None)
{
    public bool IsMethodSafe => (Traits & InstructionTraitFlags.MethodSafe) != 0;
    public bool IsPartial => (Traits & InstructionTraitFlags.Partial) != 0;
    public bool IsPrintData => (Traits & InstructionTraitFlags.IsPrintData) != 0;
    public bool IsFlowControl => (Traits & InstructionTraitFlags.FlowControl) != 0;
    public bool IsDebugOnly => (Traits & InstructionTraitFlags.DebugFunc) != 0;
}

/// <summary>
/// Authoritative catalog of all 304 built-in Emuera / EM+EE / Emuera.NET instructions.
/// </summary>
public static class BuiltinInstructionCatalog
{
    public static ImmutableArray<BuiltinInstructionSpec> All { get; } = CreateAll();

    public static FrozenDictionary<string, BuiltinInstructionSpec> CaseInsensitive { get; } =
        All.ToFrozenDictionary(static x => x.Name, StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, BuiltinInstructionSpec> CaseSensitive { get; } =
        All.ToFrozenDictionary(static x => x.Name, StringComparer.Ordinal);

    public static FrozenDictionary<BuiltinInstructionKind, BuiltinInstructionSpec> ByKind { get; } =
        All.ToFrozenDictionary(static x => x.Kind);

    public static bool TryGet(
        string name,
        bool ignoreCase,
        bool allowScopedVarInstructions,
        out BuiltinInstructionSpec? spec)
    {
        if (!(ignoreCase ? CaseInsensitive : CaseSensitive).TryGetValue(name, out spec))
        {
            return false;
        }

        if (!allowScopedVarInstructions && spec.Kind is BuiltinInstructionKind.VARI or BuiltinInstructionKind.VARS)
        {
            spec = null;
            return false;
        }

        return true;
    }

    private static ImmutableArray<BuiltinInstructionSpec> CreateAll()
    {
        const InstructionTraitFlags Safe = InstructionTraitFlags.MethodSafe;
        const InstructionTraitFlags Ext = InstructionTraitFlags.Extended;
        const InstructionTraitFlags SafeExt = Safe | Ext;
        const InstructionTraitFlags Flow = InstructionTraitFlags.FlowControl;
        const InstructionTraitFlags Part = InstructionTraitFlags.Partial;
        const InstructionTraitFlags Prn = InstructionTraitFlags.IsPrint;
        const InstructionTraitFlags Inp = InstructionTraitFlags.IsInput;

        var list = new List<BuiltinInstructionSpec>(306);

        // Assignment
        list.Add(new(BuiltinInstructionKind.SET, "SET", InstructionArgumentPattern.Assignment, Safe));

        // Programmatically register all 84 PRINT* / PRINTSINGLE* / PRINTC* / PRINTN* variants with exact flags!
        BuiltinInstructionKind[] printFamily =
        [
            BuiltinInstructionKind.PRINT, BuiltinInstructionKind.PRINTL, BuiltinInstructionKind.PRINTW,
            BuiltinInstructionKind.PRINTV, BuiltinInstructionKind.PRINTVL, BuiltinInstructionKind.PRINTVW,
            BuiltinInstructionKind.PRINTS, BuiltinInstructionKind.PRINTSL, BuiltinInstructionKind.PRINTSW,
            BuiltinInstructionKind.PRINTFORM, BuiltinInstructionKind.PRINTFORML, BuiltinInstructionKind.PRINTFORMW,
            BuiltinInstructionKind.PRINTFORMS, BuiltinInstructionKind.PRINTFORMSL, BuiltinInstructionKind.PRINTFORMSW,
            BuiltinInstructionKind.PRINTK, BuiltinInstructionKind.PRINTKL, BuiltinInstructionKind.PRINTKW,
            BuiltinInstructionKind.PRINTVK, BuiltinInstructionKind.PRINTVKL, BuiltinInstructionKind.PRINTVKW,
            BuiltinInstructionKind.PRINTSK, BuiltinInstructionKind.PRINTSKL, BuiltinInstructionKind.PRINTSKW,
            BuiltinInstructionKind.PRINTFORMK, BuiltinInstructionKind.PRINTFORMKL, BuiltinInstructionKind.PRINTFORMKW,
            BuiltinInstructionKind.PRINTFORMSK, BuiltinInstructionKind.PRINTFORMSKL, BuiltinInstructionKind.PRINTFORMSKW,
            BuiltinInstructionKind.PRINTD, BuiltinInstructionKind.PRINTDL, BuiltinInstructionKind.PRINTDW,
            BuiltinInstructionKind.PRINTVD, BuiltinInstructionKind.PRINTVDL, BuiltinInstructionKind.PRINTVDW,
            BuiltinInstructionKind.PRINTSD, BuiltinInstructionKind.PRINTSDL, BuiltinInstructionKind.PRINTSDW,
            BuiltinInstructionKind.PRINTFORMD, BuiltinInstructionKind.PRINTFORMDL, BuiltinInstructionKind.PRINTFORMDW,
            BuiltinInstructionKind.PRINTFORMSD, BuiltinInstructionKind.PRINTFORMSDL, BuiltinInstructionKind.PRINTFORMSDW,
            BuiltinInstructionKind.PRINTSINGLE, BuiltinInstructionKind.PRINTSINGLEV, BuiltinInstructionKind.PRINTSINGLES,
            BuiltinInstructionKind.PRINTSINGLEFORM, BuiltinInstructionKind.PRINTSINGLEFORMS,
            BuiltinInstructionKind.PRINTSINGLEK, BuiltinInstructionKind.PRINTSINGLEVK, BuiltinInstructionKind.PRINTSINGLESK,
            BuiltinInstructionKind.PRINTSINGLEFORMK, BuiltinInstructionKind.PRINTSINGLEFORMSK,
            BuiltinInstructionKind.PRINTSINGLED, BuiltinInstructionKind.PRINTSINGLEVD, BuiltinInstructionKind.PRINTSINGLESD,
            BuiltinInstructionKind.PRINTSINGLEFORMD, BuiltinInstructionKind.PRINTSINGLEFORMSD,
            BuiltinInstructionKind.PRINTC, BuiltinInstructionKind.PRINTLC, BuiltinInstructionKind.PRINTFORMC, BuiltinInstructionKind.PRINTFORMLC,
            BuiltinInstructionKind.PRINTCK, BuiltinInstructionKind.PRINTLCK, BuiltinInstructionKind.PRINTFORMCK, BuiltinInstructionKind.PRINTFORMLCK,
            BuiltinInstructionKind.PRINTCD, BuiltinInstructionKind.PRINTLCD, BuiltinInstructionKind.PRINTFORMCD, BuiltinInstructionKind.PRINTFORMLCD,
            BuiltinInstructionKind.PRINTN, BuiltinInstructionKind.PRINTVN, BuiltinInstructionKind.PRINTSN,
            BuiltinInstructionKind.PRINTFORMN, BuiltinInstructionKind.PRINTFORMSN,
        ];
        foreach (BuiltinInstructionKind pk in printFamily)
        {
            list.Add(BuildPrintSpec(pk));
        }

        // PRINTDATA family (9)
        BuiltinInstructionKind[] printDataFamily =
        [
            BuiltinInstructionKind.PRINTDATA, BuiltinInstructionKind.PRINTDATAL, BuiltinInstructionKind.PRINTDATAW,
            BuiltinInstructionKind.PRINTDATAK, BuiltinInstructionKind.PRINTDATAKL, BuiltinInstructionKind.PRINTDATAKW,
            BuiltinInstructionKind.PRINTDATAD, BuiltinInstructionKind.PRINTDATADL, BuiltinInstructionKind.PRINTDATADW,
        ];
        foreach (BuiltinInstructionKind pdk in printDataFamily)
        {
            list.Add(BuildPrintDataSpec(pdk));
        }

        // Buttons, Plain, Rich Print & Console Layout
        list.Add(new(BuiltinInstructionKind.PRINTBUTTON, "PRINTBUTTON", InstructionArgumentPattern.PrintButton, SafeExt));
        list.Add(new(BuiltinInstructionKind.PRINTBUTTONC, "PRINTBUTTONC", InstructionArgumentPattern.PrintButton, SafeExt));
        list.Add(new(BuiltinInstructionKind.PRINTBUTTONLC, "PRINTBUTTONLC", InstructionArgumentPattern.PrintButton, SafeExt, PrintModifierFlags.LeftAlignColumn));
        list.Add(new(BuiltinInstructionKind.PRINTPLAIN, "PRINTPLAIN", InstructionArgumentPattern.RawStringNullable, SafeExt, PrintModifierFlags.PlainText));
        list.Add(new(BuiltinInstructionKind.PRINTPLAINFORM, "PRINTPLAINFORM", InstructionArgumentPattern.FormStringNullable, SafeExt, PrintModifierFlags.PlainText));
        list.Add(new(BuiltinInstructionKind.PRINT_ABL, "PRINT_ABL", InstructionArgumentPattern.IntExpr, Safe));
        list.Add(new(BuiltinInstructionKind.PRINT_TALENT, "PRINT_TALENT", InstructionArgumentPattern.IntExpr, Safe));
        list.Add(new(BuiltinInstructionKind.PRINT_MARK, "PRINT_MARK", InstructionArgumentPattern.IntExpr, Safe));
        list.Add(new(BuiltinInstructionKind.PRINT_EXP, "PRINT_EXP", InstructionArgumentPattern.IntExpr, Safe));
        list.Add(new(BuiltinInstructionKind.PRINT_PALAM, "PRINT_PALAM", InstructionArgumentPattern.IntExpr, Safe));
        list.Add(new(BuiltinInstructionKind.PRINT_ITEM, "PRINT_ITEM", InstructionArgumentPattern.Void, Safe));
        list.Add(new(BuiltinInstructionKind.PRINT_SHOPITEM, "PRINT_SHOPITEM", InstructionArgumentPattern.Void, Safe));
        list.Add(new(BuiltinInstructionKind.DRAWLINE, "DRAWLINE", InstructionArgumentPattern.Void, Safe));
        list.Add(new(BuiltinInstructionKind.CUSTOMDRAWLINE, "CUSTOMDRAWLINE", InstructionArgumentPattern.RawString, SafeExt));
        list.Add(new(BuiltinInstructionKind.DRAWLINEFORM, "DRAWLINEFORM", InstructionArgumentPattern.FormString, SafeExt));
        list.Add(new(BuiltinInstructionKind.BAR, "BAR", InstructionArgumentPattern.Bar, SafeExt | Prn));
        list.Add(new(BuiltinInstructionKind.BARL, "BARL", InstructionArgumentPattern.Bar, SafeExt | Prn, PrintModifierFlags.NewLine));
        list.Add(new(BuiltinInstructionKind.CLEARLINE, "CLEARLINE", InstructionArgumentPattern.IntExpr, SafeExt | Prn));
        list.Add(new(BuiltinInstructionKind.REUSELASTLINE, "REUSELASTLINE", InstructionArgumentPattern.FormStringNullable, SafeExt | Prn));
        list.Add(new(BuiltinInstructionKind.HTML_PRINT, "HTML_PRINT", InstructionArgumentPattern.HtmlPrint, SafeExt));
        list.Add(new(BuiltinInstructionKind.HTML_TAGSPLIT, "HTML_TAGSPLIT", InstructionArgumentPattern.HtmlTagSplit, SafeExt));
        list.Add(new(BuiltinInstructionKind.HTML_PRINT_ISLAND, "HTML_PRINT_ISLAND", InstructionArgumentPattern.HtmlPrint, SafeExt, PrintModifierFlags.HtmlIsland));
        list.Add(new(BuiltinInstructionKind.HTML_PRINT_ISLAND_CLEAR, "HTML_PRINT_ISLAND_CLEAR", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.PRINT_IMG, "PRINT_IMG", InstructionArgumentPattern.PrintImg, SafeExt));
        list.Add(new(BuiltinInstructionKind.PRINT_RECT, "PRINT_RECT", InstructionArgumentPattern.PrintRect, SafeExt));
        list.Add(new(BuiltinInstructionKind.PRINT_SPACE, "PRINT_SPACE", InstructionArgumentPattern.PrintSpace, SafeExt));

        // Input & Wait (NOT MethodSafe!)
        list.Add(new(BuiltinInstructionKind.WAIT, "WAIT", InstructionArgumentPattern.Void, Prn));
        list.Add(new(BuiltinInstructionKind.WAITANYKEY, "WAITANYKEY", InstructionArgumentPattern.Void, Prn));
        list.Add(new(BuiltinInstructionKind.FORCEWAIT, "FORCEWAIT", InstructionArgumentPattern.Void, Prn));
        list.Add(new(BuiltinInstructionKind.TWAIT, "TWAIT", InstructionArgumentPattern.TimedWait, Prn | Ext));
        list.Add(new(BuiltinInstructionKind.AWAIT, "AWAIT", InstructionArgumentPattern.AnyExprNullable, Ext));
        list.Add(new(BuiltinInstructionKind.INPUT, "INPUT", InstructionArgumentPattern.InputInt, Prn | Inp));
        list.Add(new(BuiltinInstructionKind.INPUTS, "INPUTS", InstructionArgumentPattern.InputStr, Prn | Inp));
        list.Add(new(BuiltinInstructionKind.ONEINPUT, "ONEINPUT", InstructionArgumentPattern.InputInt, Prn | Inp | Ext));
        list.Add(new(BuiltinInstructionKind.ONEINPUTS, "ONEINPUTS", InstructionArgumentPattern.InputStr, Prn | Inp | Ext));
        list.Add(new(BuiltinInstructionKind.TINPUT, "TINPUT", InstructionArgumentPattern.TimedInputInt, Prn | Inp | Ext));
        list.Add(new(BuiltinInstructionKind.TINPUTS, "TINPUTS", InstructionArgumentPattern.TimedInputStr, Prn | Inp | Ext));
        list.Add(new(BuiltinInstructionKind.TONEINPUT, "TONEINPUT", InstructionArgumentPattern.TimedInputInt, Prn | Inp | Ext));
        list.Add(new(BuiltinInstructionKind.TONEINPUTS, "TONEINPUTS", InstructionArgumentPattern.TimedInputStr, Prn | Inp | Ext));
        list.Add(new(BuiltinInstructionKind.BINPUT, "BINPUT", InstructionArgumentPattern.InputInt, Prn | Inp));
        list.Add(new(BuiltinInstructionKind.BINPUTS, "BINPUTS", InstructionArgumentPattern.InputStr, Prn | Inp));
        list.Add(new(BuiltinInstructionKind.ONEBINPUT, "ONEBINPUT", InstructionArgumentPattern.InputInt, Prn | Inp));
        list.Add(new(BuiltinInstructionKind.ONEBINPUTS, "ONEBINPUTS", InstructionArgumentPattern.InputStr, Prn | Inp));
        list.Add(new(BuiltinInstructionKind.INPUTMOUSEKEY, "INPUTMOUSEKEY", InstructionArgumentPattern.IntExprNullable, Ext));
        list.Add(new(BuiltinInstructionKind.INPUTANY, "INPUTANY", InstructionArgumentPattern.Void, Ext));

        // Character & Training Parameter Helpers
        list.Add(new(BuiltinInstructionKind.TIMES, "TIMES", InstructionArgumentPattern.Times, Safe));
        list.Add(new(BuiltinInstructionKind.UPCHECK, "UPCHECK", InstructionArgumentPattern.Void, Safe));
        list.Add(new(BuiltinInstructionKind.CUPCHECK, "CUPCHECK", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.ADDCHARA, "ADDCHARA", InstructionArgumentPattern.IntExprList, Safe));
        list.Add(new(BuiltinInstructionKind.ADDSPCHARA, "ADDSPCHARA", InstructionArgumentPattern.IntExprList, Safe));
        list.Add(new(BuiltinInstructionKind.ADDDEFCHARA, "ADDDEFCHARA", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.ADDVOIDCHARA, "ADDVOIDCHARA", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.DELCHARA, "DELCHARA", InstructionArgumentPattern.IntExprList, Safe));
        list.Add(new(BuiltinInstructionKind.DELALLCHARA, "DELALLCHARA", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.PICKUPCHARA, "PICKUPCHARA", InstructionArgumentPattern.IntExprList, SafeExt));
        list.Add(new(BuiltinInstructionKind.SWAPCHARA, "SWAPCHARA", InstructionArgumentPattern.SwapChara, SafeExt));
        list.Add(new(BuiltinInstructionKind.COPYCHARA, "COPYCHARA", InstructionArgumentPattern.SwapChara, SafeExt));
        list.Add(new(BuiltinInstructionKind.ADDCOPYCHARA, "ADDCOPYCHARA", InstructionArgumentPattern.IntExprList, Safe));
        list.Add(new(BuiltinInstructionKind.SORTCHARA, "SORTCHARA", InstructionArgumentPattern.SortChara, SafeExt));
        list.Add(new(BuiltinInstructionKind.RESET_STAIN, "RESET_STAIN", InstructionArgumentPattern.IntExpr, SafeExt));

        // System Flow & Persistence
        list.Add(new(BuiltinInstructionKind.PUTFORM, "PUTFORM", InstructionArgumentPattern.FormStringNullable, Safe));
        list.Add(new(BuiltinInstructionKind.QUIT, "QUIT", InstructionArgumentPattern.Void, InstructionTraitFlags.None));
        list.Add(new(BuiltinInstructionKind.QUIT_AND_RESTART, "QUIT_AND_RESTART", InstructionArgumentPattern.Void, InstructionTraitFlags.None));
        list.Add(new(BuiltinInstructionKind.FORCE_QUIT, "FORCE_QUIT", InstructionArgumentPattern.Void, InstructionTraitFlags.None));
        list.Add(new(BuiltinInstructionKind.FORCE_QUIT_AND_RESTART, "FORCE_QUIT_AND_RESTART", InstructionArgumentPattern.Void, InstructionTraitFlags.None));
        list.Add(new(BuiltinInstructionKind.BEGIN, "BEGIN", InstructionArgumentPattern.RawString, Flow));
        list.Add(new(BuiltinInstructionKind.FORCE_BEGIN, "FORCE_BEGIN", InstructionArgumentPattern.RawString, Flow));
        list.Add(new(BuiltinInstructionKind.CALLTRAIN, "CALLTRAIN", InstructionArgumentPattern.IntExpr, Ext | Flow));
        list.Add(new(BuiltinInstructionKind.STOPCALLTRAIN, "STOPCALLTRAIN", InstructionArgumentPattern.Void, Ext | Flow));
        list.Add(new(BuiltinInstructionKind.DOTRAIN, "DOTRAIN", InstructionArgumentPattern.IntExpr, Ext | Flow));
        list.Add(new(BuiltinInstructionKind.SAVEGAME, "SAVEGAME", InstructionArgumentPattern.Void, Flow));
        list.Add(new(BuiltinInstructionKind.LOADGAME, "LOADGAME", InstructionArgumentPattern.Void, Flow));
        list.Add(new(BuiltinInstructionKind.SAVEDATA, "SAVEDATA", InstructionArgumentPattern.SaveData, SafeExt));
        list.Add(new(BuiltinInstructionKind.LOADDATA, "LOADDATA", InstructionArgumentPattern.IntExpr, Ext | Flow));
        list.Add(new(BuiltinInstructionKind.DELDATA, "DELDATA", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.SAVEGLOBAL, "SAVEGLOBAL", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.LOADGLOBAL, "LOADGLOBAL", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.RESETDATA, "RESETDATA", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.RESETGLOBAL, "RESETGLOBAL", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.SAVEVAR, "SAVEVAR", InstructionArgumentPattern.SaveVar, SafeExt));
        list.Add(new(BuiltinInstructionKind.LOADVAR, "LOADVAR", InstructionArgumentPattern.StrExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.SAVECHARA, "SAVECHARA", InstructionArgumentPattern.SaveChara, SafeExt));
        list.Add(new(BuiltinInstructionKind.LOADCHARA, "LOADCHARA", InstructionArgumentPattern.StrExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.UPDATECHECK, "UPDATECHECK", InstructionArgumentPattern.Void, SafeExt));

        // Structured & Unstructured Control Flow
        list.Add(new(BuiltinInstructionKind.SIF, "SIF", InstructionArgumentPattern.IntExprNullable, Safe | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.IF, "IF", InstructionArgumentPattern.IntExprNullable, Safe | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.ELSE, "ELSE", InstructionArgumentPattern.Void, Safe | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.ELSEIF, "ELSEIF", InstructionArgumentPattern.IntExprNullable, Safe | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.ENDIF, "ENDIF", InstructionArgumentPattern.Void, Safe | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.SELECTCASE, "SELECTCASE", InstructionArgumentPattern.SelectCase, SafeExt | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CASE, "CASE", InstructionArgumentPattern.CaseClause, SafeExt | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CASEELSE, "CASEELSE", InstructionArgumentPattern.Void, SafeExt | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.ENDSELECT, "ENDSELECT", InstructionArgumentPattern.Void, SafeExt | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.REPEAT, "REPEAT", InstructionArgumentPattern.IntExpr, Safe | Flow | Part));
        list.Add(new(BuiltinInstructionKind.REND, "REND", InstructionArgumentPattern.Void, Safe | Flow | Part));
        list.Add(new(BuiltinInstructionKind.FOR, "FOR", InstructionArgumentPattern.ForLoop, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.NEXT, "NEXT", InstructionArgumentPattern.Void, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.WHILE, "WHILE", InstructionArgumentPattern.IntExpr, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.WEND, "WEND", InstructionArgumentPattern.Void, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.DO, "DO", InstructionArgumentPattern.Void, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.LOOP, "LOOP", InstructionArgumentPattern.IntExpr, SafeExt | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CONTINUE, "CONTINUE", InstructionArgumentPattern.Void, Safe | Flow));
        list.Add(new(BuiltinInstructionKind.BREAK, "BREAK", InstructionArgumentPattern.Void, Safe | Flow));

        // Returns, Restarts, Labels & Function Calls
        list.Add(new(BuiltinInstructionKind.RETURN, "RETURN", InstructionArgumentPattern.IntExprList, Flow));
        list.Add(new(BuiltinInstructionKind.RETURNFORM, "RETURNFORM", InstructionArgumentPattern.FormStringNullable, Ext | Flow));
        list.Add(new(BuiltinInstructionKind.RETURNF, "RETURNF", InstructionArgumentPattern.AnyExprNullable, SafeExt | Flow));
        list.Add(new(BuiltinInstructionKind.RESTART, "RESTART", InstructionArgumentPattern.Void, SafeExt | Flow));
        list.Add(new(BuiltinInstructionKind.GOTO, "GOTO", InstructionArgumentPattern.CallLike, Safe | Flow | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.GOTOFORM, "GOTOFORM", InstructionArgumentPattern.CallFormLike, SafeExt | Flow | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYGOTO, "TRYGOTO", InstructionArgumentPattern.CallLike, SafeExt | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYGOTOFORM, "TRYGOTOFORM", InstructionArgumentPattern.CallFormLike, SafeExt | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCGOTO, "TRYCGOTO", InstructionArgumentPattern.CallLike, SafeExt | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.IsTryC | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCGOTOFORM, "TRYCGOTOFORM", InstructionArgumentPattern.CallFormLike, SafeExt | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.IsTryC | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CALL, "CALL", InstructionArgumentPattern.CallLike, Flow | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CALLFORM, "CALLFORM", InstructionArgumentPattern.CallFormLike, Ext | Flow | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCALL, "TRYCALL", InstructionArgumentPattern.CallLike, Ext | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCALLFORM, "TRYCALLFORM", InstructionArgumentPattern.CallFormLike, Ext | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCCALL, "TRYCCALL", InstructionArgumentPattern.CallLike, Ext | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.IsTryC | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCCALLFORM, "TRYCCALLFORM", InstructionArgumentPattern.CallFormLike, Ext | Flow | InstructionTraitFlags.IsTry | InstructionTraitFlags.IsTryC | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.JUMP, "JUMP", InstructionArgumentPattern.CallLike, Flow | InstructionTraitFlags.IsJump | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.JUMPFORM, "JUMPFORM", InstructionArgumentPattern.CallFormLike, Ext | Flow | InstructionTraitFlags.IsJump | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYJUMP, "TRYJUMP", InstructionArgumentPattern.CallLike, Ext | Flow | InstructionTraitFlags.IsJump | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYJUMPFORM, "TRYJUMPFORM", InstructionArgumentPattern.CallFormLike, Ext | Flow | InstructionTraitFlags.IsJump | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCJUMP, "TRYCJUMP", InstructionArgumentPattern.CallLike, Ext | Flow | InstructionTraitFlags.IsJump | InstructionTraitFlags.IsTry | InstructionTraitFlags.IsTryC | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCJUMPFORM, "TRYCJUMPFORM", InstructionArgumentPattern.CallFormLike, Ext | Flow | InstructionTraitFlags.IsJump | InstructionTraitFlags.IsTry | InstructionTraitFlags.IsTryC | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CALLEVENT, "CALLEVENT", InstructionArgumentPattern.RawString, Ext | Flow));
        list.Add(new(BuiltinInstructionKind.CALLF, "CALLF", InstructionArgumentPattern.CallLike, SafeExt | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CALLFORMF, "CALLFORMF", InstructionArgumentPattern.CallFormLike, SafeExt | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCALLF, "TRYCALLF", InstructionArgumentPattern.CallLike, SafeExt | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.TRYCALLFORMF, "TRYCALLFORMF", InstructionArgumentPattern.CallFormLike, SafeExt | InstructionTraitFlags.IsTry | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CALLSHARP, "CALLSHARP", InstructionArgumentPattern.CallSharp, SafeExt | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.CATCH, "CATCH", InstructionArgumentPattern.Void, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.ENDCATCH, "ENDCATCH", InstructionArgumentPattern.Void, SafeExt | Flow | Part));
        list.Add(new(BuiltinInstructionKind.TRYCALLLIST, "TRYCALLLIST", InstructionArgumentPattern.Void, Ext | Flow | Part | InstructionTraitFlags.IsTry));
        list.Add(new(BuiltinInstructionKind.TRYJUMPLIST, "TRYJUMPLIST", InstructionArgumentPattern.Void, Ext | Flow | Part | InstructionTraitFlags.IsJump | InstructionTraitFlags.IsTry));
        list.Add(new(BuiltinInstructionKind.TRYGOTOLIST, "TRYGOTOLIST", InstructionArgumentPattern.Void, SafeExt | Flow | Part | InstructionTraitFlags.IsTry));
        list.Add(new(BuiltinInstructionKind.FUNC, "FUNC", InstructionArgumentPattern.CallFormLike, SafeExt | Flow | Part | InstructionTraitFlags.ForceSetArg));
        list.Add(new(BuiltinInstructionKind.ENDFUNC, "ENDFUNC", InstructionArgumentPattern.Void, SafeExt | Flow | Part));

        // PRINTDATA / STRDATA Block Clause Instructions
        list.Add(new(BuiltinInstructionKind.DATA, "DATA", InstructionArgumentPattern.RawStringNullable, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.DATAFORM, "DATAFORM", InstructionArgumentPattern.FormStringNullable, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.ENDDATA, "ENDDATA", InstructionArgumentPattern.Void, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.DATALIST, "DATALIST", InstructionArgumentPattern.Void, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.ENDLIST, "ENDLIST", InstructionArgumentPattern.Void, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.STRDATA, "STRDATA", InstructionArgumentPattern.StrDataVarStr, SafeExt | Part));

        // Styling, Colors, Fonts, Skip & Console Controls
        list.Add(new(BuiltinInstructionKind.SETCOLOR, "SETCOLOR", InstructionArgumentPattern.Color, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETCOLORBYNAME, "SETCOLORBYNAME", InstructionArgumentPattern.RawString, SafeExt));
        list.Add(new(BuiltinInstructionKind.RESETCOLOR, "RESETCOLOR", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETBGCOLOR, "SETBGCOLOR", InstructionArgumentPattern.Color, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETBGCOLORBYNAME, "SETBGCOLORBYNAME", InstructionArgumentPattern.RawString, SafeExt));
        list.Add(new(BuiltinInstructionKind.RESETBGCOLOR, "RESETBGCOLOR", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETBGIMAGE, "SETBGIMAGE", InstructionArgumentPattern.FormStringList, SafeExt));
        list.Add(new(BuiltinInstructionKind.REMOVEBGIMAGE, "REMOVEBGIMAGE", InstructionArgumentPattern.FormStringList, SafeExt));
        list.Add(new(BuiltinInstructionKind.CLEARBGIMAGE, "CLEARBGIMAGE", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.FONTBOLD, "FONTBOLD", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.FONTITALIC, "FONTITALIC", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.FONTREGULAR, "FONTREGULAR", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.FONTSTYLE, "FONTSTYLE", InstructionArgumentPattern.IntExprNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETFONT, "SETFONT", InstructionArgumentPattern.StrExprNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.ALIGNMENT, "ALIGNMENT", InstructionArgumentPattern.RawString, SafeExt));
        list.Add(new(BuiltinInstructionKind.REDRAW, "REDRAW", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.FORCEKANA, "FORCEKANA", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.SKIPDISP, "SKIPDISP", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.SKIPLOG, "SKIPLOG", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.NOSKIP, "NOSKIP", InstructionArgumentPattern.Void, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.ENDNOSKIP, "ENDNOSKIP", InstructionArgumentPattern.Void, SafeExt | Part));
        list.Add(new(BuiltinInstructionKind.CLEARTEXTBOX, "CLEARTEXTBOX", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.BREAKBUTTON, "BREAKBUTTON", InstructionArgumentPattern.AnyExprNullable, SafeExt));

        // Tooltips (8)
        list.Add(new(BuiltinInstructionKind.TOOLTIP_SETCOLOR, "TOOLTIP_SETCOLOR", InstructionArgumentPattern.SwapChara, SafeExt));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_SETDELAY, "TOOLTIP_SETDELAY", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_SETDURATION, "TOOLTIP_SETDURATION", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_SETFONT, "TOOLTIP_SETFONT", InstructionArgumentPattern.StrExpr, Ext));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_SETFONTSIZE, "TOOLTIP_SETFONTSIZE", InstructionArgumentPattern.IntExpr, Ext));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_CUSTOM, "TOOLTIP_CUSTOM", InstructionArgumentPattern.IntExpr, Ext));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_FORMAT, "TOOLTIP_FORMAT", InstructionArgumentPattern.IntExpr, Ext));
        list.Add(new(BuiltinInstructionKind.TOOLTIP_IMG, "TOOLTIP_IMG", InstructionArgumentPattern.IntExpr, Ext));

        // Audio (6)
        list.Add(new(BuiltinInstructionKind.PLAYSOUND, "PLAYSOUND", InstructionArgumentPattern.HtmlPrint, SafeExt));
        list.Add(new(BuiltinInstructionKind.STOPSOUND, "STOPSOUND", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.PLAYBGM, "PLAYBGM", InstructionArgumentPattern.StrExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.STOPBGM, "STOPBGM", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETSOUNDVOLUME, "SETSOUNDVOLUME", InstructionArgumentPattern.IntExpr, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETBGMVOLUME, "SETBGMVOLUME", InstructionArgumentPattern.IntExpr, SafeExt));

        // Bit, Array, String, Math & RNG Statement Instructions
        list.Add(new(BuiltinInstructionKind.STRLEN, "STRLEN", InstructionArgumentPattern.RawStringNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.STRLENFORM, "STRLENFORM", InstructionArgumentPattern.FormStringNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.STRLENU, "STRLENU", InstructionArgumentPattern.RawStringNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.STRLENFORMU, "STRLENFORMU", InstructionArgumentPattern.FormStringNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.SPLIT, "SPLIT", InstructionArgumentPattern.Split, SafeExt));
        list.Add(new(BuiltinInstructionKind.SWAP, "SWAP", InstructionArgumentPattern.SwapVar, SafeExt));
        list.Add(new(BuiltinInstructionKind.SETBIT, "SETBIT", InstructionArgumentPattern.BitOp, SafeExt));
        list.Add(new(BuiltinInstructionKind.CLEARBIT, "CLEARBIT", InstructionArgumentPattern.BitOp, SafeExt));
        list.Add(new(BuiltinInstructionKind.INVERTBIT, "INVERTBIT", InstructionArgumentPattern.BitOp, SafeExt));
        list.Add(new(BuiltinInstructionKind.VARSET, "VARSET", InstructionArgumentPattern.VarSet, SafeExt));
        list.Add(new(BuiltinInstructionKind.CVARSET, "CVARSET", InstructionArgumentPattern.CVarSet, SafeExt));
        list.Add(new(BuiltinInstructionKind.ARRAYSHIFT, "ARRAYSHIFT", InstructionArgumentPattern.ArrayShift, SafeExt));
        list.Add(new(BuiltinInstructionKind.ARRAYREMOVE, "ARRAYREMOVE", InstructionArgumentPattern.ArrayRemove, SafeExt));
        list.Add(new(BuiltinInstructionKind.ARRAYSORT, "ARRAYSORT", InstructionArgumentPattern.ArraySort, SafeExt));
        list.Add(new(BuiltinInstructionKind.ARRAYCOPY, "ARRAYCOPY", InstructionArgumentPattern.ArrayCopy, SafeExt));
        list.Add(new(BuiltinInstructionKind.RANDOMIZE, "RANDOMIZE", InstructionArgumentPattern.IntExprNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.DUMPRAND, "DUMPRAND", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.INITRAND, "INITRAND", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.REF, "REF", InstructionArgumentPattern.Ref, SafeExt));
        list.Add(new(BuiltinInstructionKind.REFBYNAME, "REFBYNAME", InstructionArgumentPattern.RefByName, SafeExt));
        list.Add(new(BuiltinInstructionKind.VARSIZE, "VARSIZE", InstructionArgumentPattern.Varsize, SafeExt));
        list.Add(new(BuiltinInstructionKind.GETTIME, "GETTIME", InstructionArgumentPattern.Void, SafeExt));
        list.Add(new(BuiltinInstructionKind.POWER, "POWER", InstructionArgumentPattern.Power, SafeExt));
        list.Add(new(BuiltinInstructionKind.PRINTCPERLINE, "PRINTCPERLINE", InstructionArgumentPattern.GetIntVar, SafeExt));
        list.Add(new(BuiltinInstructionKind.SAVENOS, "SAVENOS", InstructionArgumentPattern.GetIntVar, SafeExt));
        list.Add(new(BuiltinInstructionKind.ENCODETOUNI, "ENCODETOUNI", InstructionArgumentPattern.FormStringNullable, SafeExt));
        list.Add(new(BuiltinInstructionKind.DT_COLUMN_OPTIONS, "DT_COLUMN_OPTIONS", InstructionArgumentPattern.DtColumnOptions, SafeExt));

        // Debug & Diagnostics
        list.Add(new(BuiltinInstructionKind.DEBUGPRINT, "DEBUGPRINT", InstructionArgumentPattern.RawStringNullable, SafeExt | InstructionTraitFlags.DebugFunc, PrintModifierFlags.DebugOnly));
        list.Add(new(BuiltinInstructionKind.DEBUGPRINTL, "DEBUGPRINTL", InstructionArgumentPattern.RawStringNullable, SafeExt | InstructionTraitFlags.DebugFunc, PrintModifierFlags.DebugOnly | PrintModifierFlags.NewLine));
        list.Add(new(BuiltinInstructionKind.DEBUGPRINTFORM, "DEBUGPRINTFORM", InstructionArgumentPattern.FormStringNullable, SafeExt | InstructionTraitFlags.DebugFunc, PrintModifierFlags.DebugOnly));
        list.Add(new(BuiltinInstructionKind.DEBUGPRINTFORML, "DEBUGPRINTFORML", InstructionArgumentPattern.FormStringNullable, SafeExt | InstructionTraitFlags.DebugFunc, PrintModifierFlags.DebugOnly | PrintModifierFlags.NewLine));
        list.Add(new(BuiltinInstructionKind.DEBUGCLEAR, "DEBUGCLEAR", InstructionArgumentPattern.Void, SafeExt | InstructionTraitFlags.DebugFunc));
        list.Add(new(BuiltinInstructionKind.ASSERT, "ASSERT", InstructionArgumentPattern.IntExpr, SafeExt | InstructionTraitFlags.DebugFunc));
        list.Add(new(BuiltinInstructionKind.THROW, "THROW", InstructionArgumentPattern.FormStringNullable, SafeExt));

        // Emuera.NET Scoped Variable Instructions (JSON-gated)
        list.Add(new(BuiltinInstructionKind.VARI, "VARI", InstructionArgumentPattern.ScopedVarDecl, SafeExt));
        list.Add(new(BuiltinInstructionKind.VARS, "VARS", InstructionArgumentPattern.ScopedVarDecl, SafeExt));

        return [.. list];
    }

    private static BuiltinInstructionSpec BuildPrintSpec(BuiltinInstructionKind kind)
    {
        string name = kind.ToString();
        ReadOnlySpan<char> rest = name.AsSpan(5); // after "PRINT"
        InstructionTraitFlags traits = InstructionTraitFlags.IsPrint;
        PrintModifierFlags pf = PrintModifierFlags.None;

        if (rest.StartsWith("SINGLE", StringComparison.Ordinal))
        {
            traits |= InstructionTraitFlags.PrintSingle | InstructionTraitFlags.Extended;
            pf |= PrintModifierFlags.SingleLine;
            rest = rest[6..];
        }

        InstructionArgumentPattern pattern;
        if (rest.StartsWith("FORMS", StringComparison.Ordinal))
        {
            pattern = InstructionArgumentPattern.StrExpr;
            rest = rest[5..];
        }
        else if (rest.StartsWith("FORM", StringComparison.Ordinal))
        {
            pattern = InstructionArgumentPattern.FormStringNullable;
            rest = rest[4..];
        }
        else if (rest.StartsWith("V", StringComparison.Ordinal))
        {
            pattern = InstructionArgumentPattern.PrintV;
            rest = rest[1..];
        }
        else if (rest.StartsWith("S", StringComparison.Ordinal))
        {
            pattern = InstructionArgumentPattern.StrExpr;
            rest = rest[1..];
        }
        else
        {
            pattern = InstructionArgumentPattern.RawStringNullable;
        }

        if (rest.StartsWith("LC", StringComparison.Ordinal))
        {
            traits |= InstructionTraitFlags.Extended;
            pf |= PrintModifierFlags.LeftAlignColumn;
            rest = rest[2..];
        }
        else if (rest.StartsWith("C", StringComparison.Ordinal))
        {
            rest = rest[1..];
        }

        if (rest.StartsWith("K", StringComparison.Ordinal))
        {
            traits |= InstructionTraitFlags.IsPrintKFunc | InstructionTraitFlags.Extended;
            pf |= PrintModifierFlags.ForceKana;
            rest = rest[1..];
        }

        if (rest.StartsWith("D", StringComparison.Ordinal))
        {
            traits |= InstructionTraitFlags.IsPrintDFunc | InstructionTraitFlags.Extended;
            pf |= PrintModifierFlags.DefaultColor;
            rest = rest[1..];
        }

        if (rest.SequenceEqual("L"))
        {
            traits |= InstructionTraitFlags.PrintNewLine;
            pf |= PrintModifierFlags.NewLine;
        }
        else if (rest.SequenceEqual("W"))
        {
            traits |= InstructionTraitFlags.PrintNewLine | InstructionTraitFlags.PrintWaitInput;
            pf |= PrintModifierFlags.NewLine | PrintModifierFlags.WaitInput;
        }
        else if (rest.SequenceEqual("N"))
        {
            traits |= InstructionTraitFlags.PrintWaitInput | InstructionTraitFlags.Extended;
            pf |= PrintModifierFlags.WaitInput | PrintModifierFlags.NoNewLineWait;
        }

        // PRINT variants without W/N are safe inside #FUNCTION (WAIT-bearing W/N variants are NOT MethodSafe!)
        if ((pf & PrintModifierFlags.WaitInput) == 0)
        {
            traits |= InstructionTraitFlags.MethodSafe;
        }

        return new BuiltinInstructionSpec(kind, name, pattern, traits, pf);
    }

    private static BuiltinInstructionSpec BuildPrintDataSpec(BuiltinInstructionKind kind)
    {
        string name = kind.ToString();
        ReadOnlySpan<char> rest = name.AsSpan(9); // after "PRINTDATA"
        InstructionTraitFlags traits = InstructionTraitFlags.IsPrint | InstructionTraitFlags.IsPrintData | InstructionTraitFlags.Extended | InstructionTraitFlags.Partial;
        PrintModifierFlags pf = PrintModifierFlags.None;

        if (rest.StartsWith("K", StringComparison.Ordinal))
        {
            traits |= InstructionTraitFlags.IsPrintKFunc;
            pf |= PrintModifierFlags.ForceKana;
            rest = rest[1..];
        }

        if (rest.StartsWith("D", StringComparison.Ordinal))
        {
            traits |= InstructionTraitFlags.IsPrintDFunc;
            pf |= PrintModifierFlags.DefaultColor;
            rest = rest[1..];
        }

        if (rest.SequenceEqual("L"))
        {
            traits |= InstructionTraitFlags.PrintNewLine;
            pf |= PrintModifierFlags.NewLine;
        }
        else if (rest.SequenceEqual("W"))
        {
            traits |= InstructionTraitFlags.PrintNewLine | InstructionTraitFlags.PrintWaitInput;
            pf |= PrintModifierFlags.NewLine | PrintModifierFlags.WaitInput;
        }

        if ((pf & PrintModifierFlags.WaitInput) == 0)
        {
            traits |= InstructionTraitFlags.MethodSafe;
        }

        return new BuiltinInstructionSpec(kind, name, InstructionArgumentPattern.PrintDataVarInt, traits, pf);
    }
}
