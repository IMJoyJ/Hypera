using System.Collections.Frozen;
using System.Collections.Immutable;
using Hypera.Bytecode;

namespace Hypera.Compiler;

/// <summary>
/// Parameter constraint kind for built-in expression method signature validation.
/// </summary>
public enum MethodParamKind : byte
{
    Int = 0,
    Str = 1,
    Any = 2,
    SameAsFirst = 3,
    CharaVar = 4,
    RefInt1D = 5,
    RefInt2D = 6,
    RefInt3D = 7,
    RefIntAny = 8,
    RefStr = 9,
    RefStr1D = 10,
    RefAny1D = 11,
    RefAnyArray = 12,
    RefIntScalar = 13,
}

/// <summary>
/// Describes one valid parameter overload of a built-in expression method.
/// </summary>
public sealed record MethodOverloadSpec(
    ImmutableArray<MethodParamKind> ParameterKinds,
    int OptionalFromIndex = int.MaxValue,
    bool IsVariadic = false,
    bool AllowConstRef = true,
    bool VariadicPairsKeyAndValue = false)
{
    public int MinArgs => Math.Min(OptionalFromIndex, ParameterKinds.Length);
}

/// <summary>
/// Static specification of one of the 270 built-in expression methods.
/// </summary>
public sealed record BuiltinMethodSpec(
    BuiltinMethodKind Kind,
    string Name,
    VariableValueKind ReturnKind,
    ImmutableArray<MethodOverloadSpec> Overloads,
    bool CanConstantFold = false);

/// <summary>
/// Authoritative catalog of all 270 Emuera built-in expression methods and their overload signatures.
/// </summary>
public static class BuiltinMethodCatalog
{
    public static ImmutableArray<BuiltinMethodSpec> All { get; } = CreateAll();

    public static FrozenDictionary<string, BuiltinMethodSpec> CaseInsensitive { get; } =
        All.ToFrozenDictionary(static m => m.Name, StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, BuiltinMethodSpec> CaseSensitive { get; } =
        All.ToFrozenDictionary(static m => m.Name, StringComparer.Ordinal);

    public static FrozenDictionary<BuiltinMethodKind, BuiltinMethodSpec> ByKind { get; } =
        All.ToFrozenDictionary(static m => m.Kind);

    public static bool TryGet(string name, bool ignoreCase, out BuiltinMethodSpec? spec) =>
        (ignoreCase ? CaseInsensitive : CaseSensitive).TryGetValue(name, out spec);

    private static ImmutableArray<BuiltinMethodSpec> CreateAll()
    {
        const MethodParamKind I = MethodParamKind.Int;
        const MethodParamKind S = MethodParamKind.Str;
        const MethodParamKind A = MethodParamKind.Any;
        const MethodParamKind Same = MethodParamKind.SameAsFirst;
        const MethodParamKind Chara = MethodParamKind.CharaVar;
        const MethodParamKind RI1 = MethodParamKind.RefInt1D;
        const MethodParamKind RI2 = MethodParamKind.RefInt2D;
        const MethodParamKind RI3 = MethodParamKind.RefInt3D;
        const MethodParamKind RIAny = MethodParamKind.RefIntAny;
        const MethodParamKind RS = MethodParamKind.RefStr;
        const MethodParamKind RS1 = MethodParamKind.RefStr1D;
        const MethodParamKind RA1 = MethodParamKind.RefAny1D;
        const MethodParamKind RAAny = MethodParamKind.RefAnyArray;
        const MethodParamKind RIScalar = MethodParamKind.RefIntScalar;

        return
        [
            // Character Data (19)
            M(BuiltinMethodKind.GETCHARA, VariableValueKind.Int, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.GETSPCHARA, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.CSVNAME, VariableValueKind.Str, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.CSVCALLNAME, VariableValueKind.Str, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.CSVNICKNAME, VariableValueKind.Str, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.CSVMASTERNAME, VariableValueKind.Str, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.CSVCSTR, VariableValueKind.Str, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVBASE, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVABL, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVMARK, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVEXP, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVRELATION, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVTALENT, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVCFLAG, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVEQUIP, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.CSVJUEL, VariableValueKind.Int, Ov([I, I, I], opt: 2)),
            M(BuiltinMethodKind.FINDCHARA, VariableValueKind.Int, Ov([Chara, Same, I, I], opt: 2)),
            M(BuiltinMethodKind.FINDLASTCHARA, VariableValueKind.Int, Ov([Chara, Same, I, I], opt: 2)),
            M(BuiltinMethodKind.EXISTCSV, VariableValueKind.Int, Ov([I, I], opt: 1)),

            // General & Console State (23)
            M(BuiltinMethodKind.VARSIZE, VariableValueKind.Int, Ov([S, I], opt: 1)),
            M(BuiltinMethodKind.CHKFONT, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.CHKDATA, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.ISSKIP, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.MOUSESKIP, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.MESSKIP, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETCOLOR, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETDEFCOLOR, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETFOCUSCOLOR, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETBGCOLOR, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETDEFBGCOLOR, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETSTYLE, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETFONT, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.BARSTR, VariableValueKind.Str, Ov([I, I, I])),
            M(BuiltinMethodKind.CURRENTALIGN, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.CURRENTREDRAW, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.COLOR_FROMNAME, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.COLOR_FROMRGB, VariableValueKind.Int, Ov([I, I, I]), fold: true),
            M(BuiltinMethodKind.CHKVARDATA, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.CHKCHARADATA, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.CHKGLOBALDATA, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.FIND_VARDATA, VariableValueKind.Int, Ov([S], opt: 0)),
            M(BuiltinMethodKind.FIND_CHARADATA, VariableValueKind.Int, Ov([S], opt: 0)),

            // Constant & Time Queries (8)
            M(BuiltinMethodKind.MONEYSTR, VariableValueKind.Str, Ov([I, S], opt: 1)),
            M(BuiltinMethodKind.PRINTCPERLINE, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.PRINTCLENGTH, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.SAVENOS, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETTIME, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETTIMES, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.GETMILLISECOND, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETSECOND, VariableValueKind.Int, Ov([])),

            // Math & RNG (12)
            M(BuiltinMethodKind.RAND, VariableValueKind.Int, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.MIN, VariableValueKind.Int, Ov([I, I], opt: 1, variadic: true), fold: true),
            M(BuiltinMethodKind.MAX, VariableValueKind.Int, Ov([I, I], opt: 1, variadic: true), fold: true),
            M(BuiltinMethodKind.ABS, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.POWER, VariableValueKind.Int, Ov([I, I]), fold: true),
            M(BuiltinMethodKind.SQRT, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.CBRT, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.LOG, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.LOG10, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.EXPONENT, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.SIGN, VariableValueKind.Int, Ov([I]), fold: true),
            M(BuiltinMethodKind.LIMIT, VariableValueKind.Int, Ov([I, I, I]), fold: true),

            // Variable & Array Operations (22)
            M(BuiltinMethodKind.SUMARRAY, VariableValueKind.Int, Ov([RIAny, I, I], opt: 1)),
            M(BuiltinMethodKind.SUMCARRAY, VariableValueKind.Int, Ov([RIAny, I, I], opt: 1)),
            M(BuiltinMethodKind.MATCH, VariableValueKind.Int, Ov([RA1, Same, I, I], opt: 2)),
            M(BuiltinMethodKind.CMATCH, VariableValueKind.Int, Ov([RA1, Same, I, I], opt: 2)),
            M(BuiltinMethodKind.GROUPMATCH, VariableValueKind.Int, Ov([A, Same], opt: 1, variadic: true), fold: true),
            M(BuiltinMethodKind.NOSAMES, VariableValueKind.Int, Ov([A, Same], opt: 1, variadic: true), fold: true),
            M(BuiltinMethodKind.ALLSAMES, VariableValueKind.Int, Ov([A, Same], opt: 1, variadic: true), fold: true),
            M(BuiltinMethodKind.MAXARRAY, VariableValueKind.Int, Ov([RI1, I, I], opt: 1)),
            M(BuiltinMethodKind.MAXCARRAY, VariableValueKind.Int, Ov([RI1, I, I], opt: 1)),
            M(BuiltinMethodKind.MINARRAY, VariableValueKind.Int, Ov([RI1, I, I], opt: 1)),
            M(BuiltinMethodKind.MINCARRAY, VariableValueKind.Int, Ov([RI1, I, I], opt: 1)),
            M(BuiltinMethodKind.GETBIT, VariableValueKind.Int, Ov([I, I]), fold: true),
            M(BuiltinMethodKind.GETNUM, VariableValueKind.Int, Ov([RAAny, S, I], opt: 2)),
            M(BuiltinMethodKind.GETPALAMLV, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.GETEXPLV, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.FINDELEMENT, VariableValueKind.Int, Ov([RA1, Same, I, I, I], opt: 2)),
            M(BuiltinMethodKind.FINDLASTELEMENT, VariableValueKind.Int, Ov([RA1, Same, I, I, I], opt: 2)),
            M(BuiltinMethodKind.INRANGE, VariableValueKind.Int, Ov([I, I, I]), fold: true),
            M(BuiltinMethodKind.INRANGEARRAY, VariableValueKind.Int, Ov([RI1, I, I, I, I], opt: 3)),
            M(BuiltinMethodKind.INRANGECARRAY, VariableValueKind.Int, Ov([RI1, I, I, I, I], opt: 3)),
            M(BuiltinMethodKind.GETNUMB, VariableValueKind.Int, Ov([S, S])),
            M(BuiltinMethodKind.ARRAYMSORT, VariableValueKind.Int, Ov([RA1, RAAny], opt: 1, variadic: true, allowConstRef: false)),

            // String & Config Operations (27)
            M(BuiltinMethodKind.STRLENS, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.STRLENSU, VariableValueKind.Int, Ov([S]), fold: true),
            M(BuiltinMethodKind.SUBSTRING, VariableValueKind.Str, Ov([S, I, I], opt: 1)),
            M(BuiltinMethodKind.SUBSTRINGU, VariableValueKind.Str, Ov([S, I, I], opt: 1), fold: true),
            M(BuiltinMethodKind.STRFIND, VariableValueKind.Int, Ov([S, S, I], opt: 2)),
            M(BuiltinMethodKind.STRFINDU, VariableValueKind.Int, Ov([S, S, I], opt: 2), fold: true),
            M(BuiltinMethodKind.STRCOUNT, VariableValueKind.Int, Ov([S, S])),
            M(BuiltinMethodKind.TOSTR, VariableValueKind.Str, Ov([I, S], opt: 1), fold: true),
            M(BuiltinMethodKind.TOINT, VariableValueKind.Int, Ov([S]), fold: true),
            M(BuiltinMethodKind.TOUPPER, VariableValueKind.Str, Ov([S]), fold: true),
            M(BuiltinMethodKind.TOLOWER, VariableValueKind.Str, Ov([S]), fold: true),
            M(BuiltinMethodKind.TOHALF, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.TOFULL, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.LINEISEMPTY, VariableValueKind.Int, Ov([])),
            M2(BuiltinMethodKind.REPLACE, VariableValueKind.Str,
                Ov([S, S, S, I], opt: 3),
                Ov([S, S, RS1, I])),
            M(BuiltinMethodKind.UNICODE, VariableValueKind.Str, Ov([I]), fold: true),
            M(BuiltinMethodKind.UNICODEBYTE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.CONVERT, VariableValueKind.Str, Ov([I, I]), fold: true),
            M(BuiltinMethodKind.ISNUMERIC, VariableValueKind.Int, Ov([S]), fold: true),
            M(BuiltinMethodKind.ESCAPE, VariableValueKind.Str, Ov([S]), fold: true),
            M(BuiltinMethodKind.ENCODETOUNI, VariableValueKind.Int, Ov([S, I], opt: 1), fold: true),
            M(BuiltinMethodKind.CHARATU, VariableValueKind.Str, Ov([S, I]), fold: true),
            M(BuiltinMethodKind.GETLINESTR, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.STRFORM, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.STRJOIN, VariableValueKind.Str, Ov([RAAny, S, I, I], opt: 1)),
            M(BuiltinMethodKind.GETCONFIG, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.GETCONFIGS, VariableValueKind.Str, Ov([S])),

            // HTML / Rich Text (7)
            M(BuiltinMethodKind.HTML_GETPRINTEDSTR, VariableValueKind.Str, Ov([I], opt: 0)),
            M(BuiltinMethodKind.HTML_POPPRINTINGSTR, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.HTML_TOPLAINTEXT, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.HTML_ESCAPE, VariableValueKind.Str, Ov([S]), fold: true),
            M(BuiltinMethodKind.HTML_STRINGLEN, VariableValueKind.Int, Ov([S, I], opt: 1)),
            M(BuiltinMethodKind.HTML_SUBSTRING, VariableValueKind.Str, Ov([S, I])),
            M(BuiltinMethodKind.HTML_STRINGLINES, VariableValueKind.Int, Ov([S, I])),

            // Sprites, Graphics & CBG (54)
            M(BuiltinMethodKind.SPRITECREATED, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.SPRITEWIDTH, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.SPRITEHEIGHT, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.SPRITEMOVE, VariableValueKind.Int, Ov([S, I, I])),
            M(BuiltinMethodKind.SPRITESETPOS, VariableValueKind.Int, Ov([S, I, I])),
            M(BuiltinMethodKind.SPRITEPOSX, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.SPRITEPOSY, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.SPRITEGETCOLOR, VariableValueKind.Int, Ov([S, I, I])),
            M2(BuiltinMethodKind.SPRITECREATE, VariableValueKind.Int,
                Ov([S, I]),
                Ov([S, I, I, I, I, I])),
            M(BuiltinMethodKind.SPRITEDISPOSE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.SPRITEDISPOSEALL, VariableValueKind.Int, Ov([I], opt: 0)),
            M(BuiltinMethodKind.SPRITEANIMECREATE, VariableValueKind.Int, Ov([S, I, I])),
            M(BuiltinMethodKind.SPRITEANIMEADDFRAME, VariableValueKind.Int, Ov([S, I, I, I, I, I, I, I, I])),
            M(BuiltinMethodKind.SETANIMETIMER, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.CLIENTWIDTH, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.CLIENTHEIGHT, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETKEY, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GETKEYTRIGGERED, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.MOUSEX, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.MOUSEY, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.MOUSEB, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.ISACTIVE, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.SAVETEXT, VariableValueKind.Int, Ov([S, A, I, I], opt: 2)),
            M(BuiltinMethodKind.LOADTEXT, VariableValueKind.Str, Ov([A, I, I], opt: 1)),
            M(BuiltinMethodKind.GCREATED, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GWIDTH, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GHEIGHT, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GGETCOLOR, VariableValueKind.Int, Ov([I, I, I])),
            M(BuiltinMethodKind.GCREATE, VariableValueKind.Int, Ov([I, I, I])),
            M(BuiltinMethodKind.GCREATEFROMFILE, VariableValueKind.Int, Ov([I, S, I], opt: 2)),
            M(BuiltinMethodKind.GDISPOSE, VariableValueKind.Int, Ov([I])),
            M2(BuiltinMethodKind.GCLEAR, VariableValueKind.Int,
                Ov([I, I]),
                Ov([I, I, I, I, I, I])),
            M(BuiltinMethodKind.GFILLRECTANGLE, VariableValueKind.Int, Ov([I, I, I, I, I])),
            M(BuiltinMethodKind.GDRAWSPRITE, VariableValueKind.Int,
                [
                    Ov([I, S]),
                    Ov([I, S, I, I]),
                    Ov([I, S, I, I, I, I, RI2], opt: 6),
                    Ov([I, S, I, I, I, I, RI3], opt: 6),
                ]),
            M(BuiltinMethodKind.GSETCOLOR, VariableValueKind.Int, Ov([I, I, I, I])),
            M2(BuiltinMethodKind.GDRAWG, VariableValueKind.Int,
                Ov([I, I, I, I, I, I, I, I, I, I, RI2], opt: 10),
                Ov([I, I, I, I, I, I, I, I, I, I, RI3], opt: 10)),
            M(BuiltinMethodKind.GDRAWGWITHMASK, VariableValueKind.Int, Ov([I, I, I, I, I])),
            M(BuiltinMethodKind.GSETBRUSH, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.GSETFONT, VariableValueKind.Int, Ov([I, S, I, I], opt: 2)),
            M(BuiltinMethodKind.GSETPEN, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.GSAVE, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.GLOAD, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.GROTATE, VariableValueKind.Int, Ov([I, I, I, I], opt: 2)),
            M(BuiltinMethodKind.GDRAWGWITHROTATE, VariableValueKind.Int, Ov([I, I, I, I, I], opt: 3)),
            M(BuiltinMethodKind.GDRAWTEXT, VariableValueKind.Int, Ov([I, S, I, I], opt: 2)),
            M(BuiltinMethodKind.GGETFONT, VariableValueKind.Str, Ov([I])),
            M(BuiltinMethodKind.GGETFONTSIZE, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GGETFONTSTYLE, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GGETTEXTSIZE, VariableValueKind.Int, Ov([S, S, I, I], opt: 3)),
            M(BuiltinMethodKind.GGETBRUSH, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GGETPEN, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GGETPENWIDTH, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.GDRAWLINE, VariableValueKind.Int, Ov([I, I, I, I, I])),
            M(BuiltinMethodKind.GDASHSTYLE, VariableValueKind.Int, Ov([I, I, I])),
            M(BuiltinMethodKind.CBGSETG, VariableValueKind.Int, Ov([I, I, I, I])),
            M(BuiltinMethodKind.CBGSETSPRITE, VariableValueKind.Int, Ov([S, I, I, I])),
            M(BuiltinMethodKind.CBGCLEAR, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.CBGCLEARBUTTON, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.CBGREMOVERANGE, VariableValueKind.Int, Ov([I, I])),
            M(BuiltinMethodKind.CBGREMOVEBMAP, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.CBGSETBMAPG, VariableValueKind.Int, Ov([I])),
            M(BuiltinMethodKind.CBGSETBUTTONSPRITE, VariableValueKind.Int, Ov([I, S, S, I, I, I, S], opt: 6)),

            // Reflection, Files, Regex & Misc Extensions (39)
            M(BuiltinMethodKind.OUTPUTLOG, VariableValueKind.Int, Ov([S, I], opt: 0)),
            M(BuiltinMethodKind.EXISTFILE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.EXISTVAR, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.ISDEFINED, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.ENUMFUNCBEGINSWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMFUNCENDSWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMFUNCWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMVARBEGINSWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMVARENDSWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMVARWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMMACROBEGINSWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMMACROENDSWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMMACROWITH, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.ENUMFILES, VariableValueKind.Int, Ov([S, S, I, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.GETVAR, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.GETVARS, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.SETVAR, VariableValueKind.Int, Ov([S, A])),
            M(BuiltinMethodKind.VARSETEX, VariableValueKind.Int, Ov([S, A, I, I, I], opt: 2)),
            M2(BuiltinMethodKind.ARRAYMSORTEX, VariableValueKind.Int,
                Ov([S, RS1, I, I], opt: 2),
                Ov([RI1, RS1, I, I], opt: 2)),
            M2(BuiltinMethodKind.REGEXPMATCH, VariableValueKind.Int,
                Ov([S, S, I], opt: 2),
                Ov([S, S, RIScalar, RS1], allowConstRef: false)),
            M(BuiltinMethodKind.MOVETEXTBOX, VariableValueKind.Int, Ov([I, I, I])),
            M(BuiltinMethodKind.RESUMETEXTBOX, VariableValueKind.Int, Ov([I, I, I], opt: 0)),
            M(BuiltinMethodKind.EXISTSOUND, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.EXISTFUNCTION, VariableValueKind.Int, Ov([S, I], opt: 1)),
            M(BuiltinMethodKind.GETMEMORYUSAGE, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.CLEARMEMORY, VariableValueKind.Int, Ov([])),
            M(BuiltinMethodKind.GETTEXTBOX, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.SETTEXTBOX, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.ERDNAME, VariableValueKind.Str, Ov([RAAny, I, I], opt: 2)),
            M(BuiltinMethodKind.GETDISPLAYLINE, VariableValueKind.Str, Ov([I])),
            M(BuiltinMethodKind.GETDOINGFUNCTION, VariableValueKind.Str, Ov([])),
            M(BuiltinMethodKind.FLOWINPUT, VariableValueKind.Int, Ov([I, I, I, I], opt: 1)),
            M(BuiltinMethodKind.FLOWINPUTS, VariableValueKind.Int, Ov([I, S], opt: 1)),
            M(BuiltinMethodKind.GETMETH, VariableValueKind.Int, Ov([S, I, A, A, A, A, A], opt: 1, variadic: true)),
            M(BuiltinMethodKind.GETMETHS, VariableValueKind.Str, Ov([S, S, A, A, A, A, A], opt: 1, variadic: true)),
            M(BuiltinMethodKind.EXISTMETH, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.BITMAP_CACHE_ENABLE, VariableValueKind.Int, Ov([I], opt: 0)),
            M(BuiltinMethodKind.HOTKEY_STATE, VariableValueKind.Int, Ov([I, I], opt: 1)),
            M(BuiltinMethodKind.HOTKEY_STATE_INIT, VariableValueKind.Int, Ov([I], opt: 0)),

            // XML (18)
            M(BuiltinMethodKind.XML_DOCUMENT, VariableValueKind.Int, Ov([A, S])),
            M(BuiltinMethodKind.XML_RELEASE, VariableValueKind.Int, Ov([A])),
            M2(BuiltinMethodKind.XML_GET, VariableValueKind.Int,
                Ov([A, S, I, I], opt: 2),
                Ov([A, S, RS1, I], opt: 3, allowConstRef: false)),
            M2(BuiltinMethodKind.XML_GET_BYNAME, VariableValueKind.Int,
                Ov([A, S, I, I], opt: 2),
                Ov([A, S, RS1, I], opt: 3, allowConstRef: false)),
            M2(BuiltinMethodKind.XML_SET, VariableValueKind.Int,
                Ov([I, S, S, I, I], opt: 3),
                Ov([RS, S, S, I, I], opt: 3, allowConstRef: false)),
            M(BuiltinMethodKind.XML_SET_BYNAME, VariableValueKind.Int,
                Ov([S, S, S, I, I], opt: 3)),
            M(BuiltinMethodKind.XML_EXIST, VariableValueKind.Int, Ov([A, S], opt: 1)),
            M(BuiltinMethodKind.XML_TOSTR, VariableValueKind.Str, Ov([A])),
            M2(BuiltinMethodKind.XML_ADDNODE, VariableValueKind.Int,
                Ov([I, S, S, I, I], opt: 3),
                Ov([RS, S, S, I, I], opt: 3, allowConstRef: false)),
            M(BuiltinMethodKind.XML_ADDNODE_BYNAME, VariableValueKind.Int,
                Ov([S, S, S, I, I], opt: 3)),
            M2(BuiltinMethodKind.XML_REMOVENODE, VariableValueKind.Int,
                Ov([I, S, I], opt: 2),
                Ov([RS, S, I], opt: 2, allowConstRef: false)),
            M(BuiltinMethodKind.XML_REMOVENODE_BYNAME, VariableValueKind.Int,
                Ov([S, S, I], opt: 2)),
            M(BuiltinMethodKind.XML_REPLACE, VariableValueKind.Int,
                [
                    Ov([A, S]),
                    Ov([I, S, S, I], opt: 3),
                    Ov([RS, S, S, I], opt: 3, allowConstRef: false),
                ]),
            M(BuiltinMethodKind.XML_REPLACE_BYNAME, VariableValueKind.Int,
                [
                    Ov([S, S]),
                    Ov([S, S, S, I], opt: 3),
                ]),
            M2(BuiltinMethodKind.XML_ADDATTRIBUTE, VariableValueKind.Int,
                Ov([I, S, S, S, I, I], opt: 4),
                Ov([RS, S, S, S, I, I], opt: 4, allowConstRef: false)),
            M(BuiltinMethodKind.XML_ADDATTRIBUTE_BYNAME, VariableValueKind.Int,
                Ov([S, S, S, S, I, I], opt: 4)),
            M2(BuiltinMethodKind.XML_REMOVEATTRIBUTE, VariableValueKind.Int,
                Ov([I, S, I], opt: 2),
                Ov([RS, S, I], opt: 2, allowConstRef: false)),
            M(BuiltinMethodKind.XML_REMOVEATTRIBUTE_BYNAME, VariableValueKind.Int,
                Ov([S, S, I], opt: 2)),

            // MAP (12)
            M(BuiltinMethodKind.MAP_CREATE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.MAP_EXIST, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.MAP_RELEASE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.MAP_GET, VariableValueKind.Str, Ov([S, S])),
            M(BuiltinMethodKind.MAP_CLEAR, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.MAP_SIZE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.MAP_HAS, VariableValueKind.Int, Ov([S, S])),
            M(BuiltinMethodKind.MAP_SET, VariableValueKind.Int, Ov([S, S, S])),
            M(BuiltinMethodKind.MAP_REMOVE, VariableValueKind.Int, Ov([S, S])),
            M2(BuiltinMethodKind.MAP_GETKEYS, VariableValueKind.Str,
                Ov([S, I], opt: 1),
                Ov([S, RS1, I], opt: 2, allowConstRef: false)),
            M(BuiltinMethodKind.MAP_TOXML, VariableValueKind.Str, Ov([S])),
            M(BuiltinMethodKind.MAP_FROMXML, VariableValueKind.Int, Ov([S, S])),

            // DataTable (21) -> Total = 270
            M(BuiltinMethodKind.DT_CREATE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.DT_EXIST, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.DT_RELEASE, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.DT_NOCASE, VariableValueKind.Int, Ov([S, I])),
            M(BuiltinMethodKind.DT_CLEAR, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.DT_COLUMN_ADD, VariableValueKind.Int, Ov([S, S, A, I], opt: 2)),
            M(BuiltinMethodKind.DT_COLUMN_NAMES, VariableValueKind.Int, Ov([S, RS1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.DT_COLUMN_EXIST, VariableValueKind.Int, Ov([S, S])),
            M(BuiltinMethodKind.DT_COLUMN_REMOVE, VariableValueKind.Int, Ov([S, S])),
            M(BuiltinMethodKind.DT_COLUMN_LENGTH, VariableValueKind.Int, Ov([S])),
            M2(BuiltinMethodKind.DT_ROW_ADD, VariableValueKind.Int,
                Ov([S, S, A], opt: 1, variadic: true, variadicPairs: true),
                Ov([S, RS1, RA1, I])),
            M2(BuiltinMethodKind.DT_ROW_SET, VariableValueKind.Int,
                Ov([S, I, S, A], opt: 2, variadic: true, variadicPairs: true),
                Ov([S, I, RS1, RA1, I])),
            M2(BuiltinMethodKind.DT_ROW_REMOVE, VariableValueKind.Int,
                Ov([S, I]),
                Ov([S, RI1, I])),
            M(BuiltinMethodKind.DT_ROW_LENGTH, VariableValueKind.Int, Ov([S])),
            M(BuiltinMethodKind.DT_CELL_GET, VariableValueKind.Int, Ov([S, I, S, I], opt: 3)),
            M(BuiltinMethodKind.DT_CELL_ISNULL, VariableValueKind.Int, Ov([S, I, S, I], opt: 3)),
            M(BuiltinMethodKind.DT_CELL_GETS, VariableValueKind.Str, Ov([S, I, S, I], opt: 3)),
            M(BuiltinMethodKind.DT_CELL_SET, VariableValueKind.Int, Ov([S, I, S, A, I], opt: 3)),
            M(BuiltinMethodKind.DT_SELECT, VariableValueKind.Int, Ov([S, S, S, RI1], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.DT_TOXML, VariableValueKind.Str, Ov([S, RS], opt: 1, allowConstRef: false)),
            M(BuiltinMethodKind.DT_FROMXML, VariableValueKind.Int, Ov([S, S, S], opt: 2)),
        ];
    }

    private static MethodOverloadSpec Ov(
        ReadOnlySpan<MethodParamKind> kinds,
        int opt = int.MaxValue,
        bool variadic = false,
        bool allowConstRef = true,
        bool variadicPairs = false) =>
        new([.. kinds], opt, variadic, allowConstRef, variadicPairs);

    private static BuiltinMethodSpec M(
        BuiltinMethodKind kind,
        VariableValueKind ret,
        MethodOverloadSpec overload,
        bool fold = false) =>
        new(kind, kind.ToString(), ret, [overload], fold);

    private static BuiltinMethodSpec M2(
        BuiltinMethodKind kind,
        VariableValueKind ret,
        MethodOverloadSpec ov1,
        MethodOverloadSpec ov2,
        bool fold = false) =>
        new(kind, kind.ToString(), ret, [ov1, ov2], fold);

    private static BuiltinMethodSpec M(
        BuiltinMethodKind kind,
        VariableValueKind ret,
        ReadOnlySpan<MethodOverloadSpec> overloads,
        bool fold = false) =>
        new(kind, kind.ToString(), ret, [.. overloads], fold);
}
