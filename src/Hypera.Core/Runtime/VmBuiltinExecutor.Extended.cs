using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Rendering;
using SkiaSharp;

namespace Hypera.Runtime;

internal static partial class VmBuiltinExecutor
{
    private enum EnumMatchAction : byte
    {
        BeginsWith = 0,
        EndsWith = 1,
        Contains = 2,
    }

    // -------------------------------------------------------------------------
    // Reflection, Enumeration & Dynamic Method Invocation
    // -------------------------------------------------------------------------

    private static long EvalIsDefined(EmueraEngine engine, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || engine.Module.DefinedMacros.IsDefaultOrEmpty)
        {
            return 0L;
        }

        string trimmed = name.Trim();
        StringComparison cmp = engine.Module.CompilerOptions.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (string macro in engine.Module.DefinedMacros)
        {
            if (string.Equals(macro, trimmed, cmp))
            {
                return 1L;
            }
        }

        return 0L;
    }

    private static long EvalEnumFunctions(EmueraEngine engine, VariableStore vars, CallWindow w, EnumMatchAction action)
    {
        string needle = w.GetStr(0).ToUpperInvariant();
        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FunctionDescriptor fn in engine.Module.Functions)
        {
            if (fn.EventFlags != EventDirectiveFlags.None)
            {
                continue;
            }

            if (MatchesEnumNeedle(fn.Name, needle, action) && seen.Add(fn.Name))
            {
                matches.Add(fn.Name);
            }
        }

        Span<string> dst = w.HasArg(1) && w.GetTag(1) == CallArgTag.ByRef
            ? w.ResolveRefStrSpan(1)
            : vars.ResultsSpan;
        return CopyMatchesToSpan(matches, dst);
    }

    private static long EvalEnumVariables(VariableStore vars, CallWindow w, EnumMatchAction action)
    {
        string needle = w.GetStr(0).ToUpperInvariant();
        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in vars.VariableNames)
        {
            if (name.Contains('@', StringComparison.Ordinal))
            {
                continue;
            }

            if (MatchesEnumNeedle(name, needle, action) && seen.Add(name))
            {
                matches.Add(name);
            }
        }

        Span<string> dst = w.HasArg(1) && w.GetTag(1) == CallArgTag.ByRef
            ? w.ResolveRefStrSpan(1)
            : vars.ResultsSpan;
        return CopyMatchesToSpan(matches, dst);
    }

    private static long EvalEnumMacros(EmueraEngine engine, VariableStore vars, CallWindow w, EnumMatchAction action)
    {
        string needle = w.GetStr(0).ToUpperInvariant();
        var matches = new List<string>();
        if (!engine.Module.DefinedMacros.IsDefaultOrEmpty)
        {
            foreach (string macro in engine.Module.DefinedMacros)
            {
                if (MatchesEnumNeedle(macro, needle, action))
                {
                    matches.Add(macro);
                }
            }
        }

        Span<string> dst = w.HasArg(1) && w.GetTag(1) == CallArgTag.ByRef
            ? w.ResolveRefStrSpan(1)
            : vars.ResultsSpan;
        return CopyMatchesToSpan(matches, dst);
    }

    private static long EvalEnumFiles(EmueraEngine engine, VariableStore vars, CallWindow w)
    {
        string dir = w.GetStr(0);
        string pattern = w.HasArg(1) ? w.GetStr(1, "*") : "*";
        bool recursive = w.HasArg(2) && w.GetInt(2) != 0;

        int status = engine.EnumerateFilesForScript(dir, pattern, recursive, out string[] matched);
        if (status < 0)
        {
            return -1L;
        }

        Span<string> dst = w.HasArg(3) && w.GetTag(3) == CallArgTag.ByRef
            ? w.ResolveRefStrSpan(3)
            : vars.ResultsSpan;
        return CopyMatchesToSpan(matched, dst);
    }

    private static bool MatchesEnumNeedle(string candidate, string upperNeedle, EnumMatchAction action)
    {
        if (upperNeedle.Length == 0)
        {
            return true;
        }

        if (candidate.Length < upperNeedle.Length)
        {
            return false;
        }

        string upper = candidate.ToUpperInvariant();
        return action switch
        {
            EnumMatchAction.BeginsWith => upper.StartsWith(upperNeedle, StringComparison.Ordinal),
            EnumMatchAction.EndsWith => upper.EndsWith(upperNeedle, StringComparison.Ordinal),
            _ => upper.Contains(upperNeedle, StringComparison.Ordinal),
        };
    }

    private static long CopyMatchesToSpan(IReadOnlyList<string> matches, Span<string> dst)
    {
        int copyCount = Math.Min(matches.Count, dst.Length);
        for (int i = 0; i < copyCount; i++)
        {
            dst[i] = matches[i];
        }

        return matches.Count;
    }

    private static long EvalGetMethInt(EmueraEngine engine, CallFrame activeFrame, CallWindow w)
    {
        string fnName = w.GetStr(0);
        if (!engine.TryGetMethodFunction(fnName, out FunctionDescriptor? target) || target is null)
        {
            if (w.HasArg(1))
            {
                return w.GetInt(1);
            }

            throw new KeyNotFoundException($"GETMETH: function '@{fnName}' is not defined.");
        }

        if (target.MethodReturnKind == VariableValueKind.Str)
        {
            throw new InvalidOperationException($"GETMETH: function '@{fnName}' returns a string (#FUNCTIONS); use GETMETHS.");
        }

        return InvokeDynamicMethod(engine, activeFrame, target, w, expectsStr: false).IntValue;
    }

    private static string EvalGetMethStr(EmueraEngine engine, CallFrame activeFrame, CallWindow w)
    {
        string fnName = w.GetStr(0);
        if (!engine.TryGetMethodFunction(fnName, out FunctionDescriptor? target) || target is null)
        {
            if (w.HasArg(1))
            {
                return w.GetStr(1);
            }

            throw new KeyNotFoundException($"GETMETHS: function '@{fnName}' is not defined.");
        }

        if (target.MethodReturnKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"GETMETHS: function '@{fnName}' returns an integer (#FUNCTION); use GETMETH.");
        }

        return InvokeDynamicMethod(engine, activeFrame, target, w, expectsStr: true).StrValue;
    }

    private static (long IntValue, string StrValue) InvokeDynamicMethod(
        EmueraEngine engine,
        CallFrame activeFrame,
        FunctionDescriptor target,
        CallWindow w,
        bool expectsStr)
    {
        int forwardedCount = Math.Max(0, w.Count - 2);
        bool spill = forwardedCount > BytecodeAbi.MaxInlineUserCallArgs;
        int intRegCount = Math.Max(1, (spill ? forwardedCount * 2 : forwardedCount) + 1);
        int strRegCount = Math.Max(1, forwardedCount + 1);

        var hostFrame = new CallFrame(
            activeFrame.FunctionId,
            activeFrame.FunctionName,
            entryPc: engine.Module.Instructions.Length,
            intRegisterCount: intRegCount,
            strRegisterCount: strRegCount)
        {
            LocalSlots = activeFrame.LocalSlots,
        };

        ushort intBase = 1;
        ushort strBase = 1;
        var tags = forwardedCount == 0 ? [] : new CallArgTag[forwardedCount];

        for (int i = 0; i < forwardedCount; i++)
        {
            int srcIdx = i + 2;
            CallArgTag tag = w.GetTag(srcIdx);
            tags[i] = tag;
            if (tag == CallArgTag.Str)
            {
                hostFrame.StrRegisters[strBase + i] = w.GetStr(srcIdx);
            }
            else if (tag is CallArgTag.Int or CallArgTag.ByRef)
            {
                hostFrame.IntRegisters[intBase + i] = w.GetInt(srcIdx);
            }

            if (spill)
            {
                hostFrame.IntRegisters[intBase + forwardedCount + i] = (long)tag;
            }
        }

        ushort aux = BytecodeAbi.PackUserCallAux(tags, expectsStr, isMethodCall: true);
        var callInsn = Instruction.Reg4(OpCode.Call, 0, intBase, strBase, (ushort)forwardedCount, target.FunctionId, aux);

        int depthBefore = engine.InternalCallStack.Count;
        engine.InternalCallStack.Add(hostFrame);
        try
        {
            engine.InvokeStaticCall(hostFrame, callInsn, isTry: false, isJump: false);
            while (engine.InternalCallStack.Count > depthBefore + 1)
            {
                EngineStepStatus st = VmDispatcher.ExecuteStep(engine, 100_000, minCallDepth: depthBefore + 1);
                if (st is EngineStepStatus.Faulted or EngineStepStatus.WaitingInput or EngineStepStatus.Completed)
                {
                    break;
                }
            }

            return (hostFrame.IntRegisters[0], hostFrame.StrRegisters[0] ?? string.Empty);
        }
        finally
        {
            while (engine.InternalCallStack.Count > depthBefore)
            {
                engine.InternalCallStack.RemoveAt(engine.InternalCallStack.Count - 1);
            }
        }
    }

    // -------------------------------------------------------------------------
    // VARSETEX (Dynamic Variable Bulk Assignment)
    // -------------------------------------------------------------------------

    private static long EvalVarSetEx(EmueraEngine engine, CallFrame frame, CallWindow w)
    {
        VariableStore vars = engine.RequireVariableStore();
        string rawExpr = w.GetStr(0).Trim();
        if (string.IsNullOrEmpty(rawExpr))
        {
            throw new ArgumentException("VARSETEX target variable name cannot be empty.");
        }

        string[] parts = SplitTopLevelColonsSimple(rawExpr);
        string varName = parts[0].Trim();
        long[] subscripts = new long[Math.Max(0, parts.Length - 1)];
        for (int i = 1; i < parts.Length; i++)
        {
            string subStr = parts[i].Trim();
            if (long.TryParse(subStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long literal))
            {
                subscripts[i - 1] = literal;
            }
            else
            {
                subscripts[i - 1] = engine.ActiveDynamicEvaluator.EvaluateIntExpression(engine, frame, subStr);
            }
        }

        bool fillAll = !w.HasArg(2) || w.GetInt(2) != 0;
        int start = w.HasArg(3) ? (int)w.GetInt(3) : 0;
        bool hasEnd = w.HasArg(4);
        int endArg = hasEnd ? (int)w.GetInt(4) : -1;

        bool valIsStr = w.HasArg(1) && w.GetTag(1) == CallArgTag.Str;
        long fillInt = w.HasArg(1) && !valIsStr ? w.GetInt(1) : 0L;
        string fillStr = w.HasArg(1) && valIsStr ? w.GetStr(1) : string.Empty;

        // 1. Frame local variable (#DIM DYNAMIC / REF)
        if (frame.TryGetLocal(varName, vars.CompilerOptions.IgnoreCase, out FrameLocalStorage? loc) && loc is not null)
        {
            int[] locDims = loc.Rank switch
            {
                1 => [loc.Dim0],
                2 => [loc.Dim0, loc.Dim1],
                3 => [loc.Dim0, loc.Dim1, loc.Dim2],
                _ => [Math.Max(1, loc.TotalLength)],
            };

            if (loc.ValueKind == VariableValueKind.Str)
            {
                if (w.HasArg(1) && !valIsStr) throw new InvalidOperationException($"VARSETEX: cannot assign integer to string variable '{varName}'.");
                Span<string> span = loc.GetStrSpan();
                ApplyVarSetExSlice(span, fillStr, locDims, subscripts, fillAll, start, hasEnd ? endArg : -1);
            }
            else
            {
                if (valIsStr) throw new InvalidOperationException($"VARSETEX: cannot assign string to integer variable '{varName}'.");
                Span<long> span = loc.GetIntSpan();
                ApplyVarSetExSlice(span, fillInt, locDims, subscripts, fillAll, start, hasEnd ? endArg : -1);
            }

            return 1L;
        }

        // 2. Static / Character variable
        string resolved = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(vars, frame, varName, null);
        if (!vars.TryResolveSlot(resolved, out int slotId))
        {
            throw new KeyNotFoundException($"VARSETEX: unknown variable '{varName}'.");
        }

        VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
        if (slot.IsConst || slot.IsReadOnly)
        {
            throw new InvalidOperationException($"VARSETEX: variable '{slot.Name}' is read-only/const.");
        }

        if (slot.ValueKind == VariableValueKind.Str)
        {
            if (w.HasArg(1) && !valIsStr) throw new InvalidOperationException($"VARSETEX: cannot assign integer to string variable '{slot.Name}'.");
            if (slot.IsCharaData)
            {
                int charaIdx = subscripts.Length > 0 ? checked((int)subscripts[0]) : checked((int)vars.Target);
                Span<string> span = vars.Characters.GetStrSpan(charaIdx, slotId);
                int e = hasEnd ? endArg : span.Length;
                SpanBuiltins.VarSet(span, fillStr, start, e);
            }
            else
            {
                int[] dims = slot.Rank switch
                {
                    <= 1 => [slot.Dim0],
                    2 => [slot.Dim0, slot.Dim1],
                    _ => [slot.Dim0, slot.Dim1, slot.Dim2],
                };
                ApplyVarSetExSlice(slot.StrData.AsSpan(), fillStr, dims, subscripts, fillAll, start, hasEnd ? endArg : -1);
            }
        }
        else
        {
            if (valIsStr) throw new InvalidOperationException($"VARSETEX: cannot assign string to integer variable '{slot.Name}'.");
            if (slot.IsCharaData)
            {
                int charaIdx = subscripts.Length > 0 ? checked((int)subscripts[0]) : checked((int)vars.Target);
                Span<long> span = vars.Characters.GetIntSpan(charaIdx, slotId);
                int e = hasEnd ? endArg : span.Length;
                SpanBuiltins.VarSet(span, fillInt, start, e);
            }
            else
            {
                int[] dims = slot.Rank switch
                {
                    <= 1 => [slot.Dim0],
                    2 => [slot.Dim0, slot.Dim1],
                    _ => [slot.Dim0, slot.Dim1, slot.Dim2],
                };
                ApplyVarSetExSlice(slot.IntData.AsSpan(), fillInt, dims, subscripts, fillAll, start, hasEnd ? endArg : -1);
            }
        }

        return 1L;
    }

    private static void ApplyVarSetExSlice<T>(
        Span<T> flat,
        T value,
        IReadOnlyList<int> dims,
        long[] subscripts,
        bool fillAll,
        int start,
        int endExclusive)
    {
        if (dims.Count <= 1)
        {
            int s = Math.Clamp(start, 0, flat.Length);
            int e = endExclusive < 0 ? flat.Length : Math.Clamp(endExclusive, s, flat.Length);
            flat[s..e].Fill(value);
            return;
        }

        if (dims.Count == 2)
        {
            int d0 = Math.Max(1, dims[0]);
            int d1 = Math.Max(1, dims[1]);
            if (fillAll && subscripts.Length == 0 && start == 0 && endExclusive < 0)
            {
                flat.Fill(value);
                return;
            }

            int rowStart = fillAll ? 0 : (subscripts.Length >= 1 ? Math.Clamp((int)subscripts[0], 0, d0 - 1) : 0);
            int rowEnd = fillAll ? d0 : rowStart + 1;
            int colBase = !fillAll && subscripts.Length >= 2 ? (int)subscripts[1] : 0;
            int s = Math.Clamp(colBase + start, 0, d1);
            int e = endExclusive < 0 ? d1 : Math.Clamp(colBase + endExclusive, s, d1);
            for (int r = rowStart; r < rowEnd; r++)
            {
                int offset = r * d1;
                if (offset + e <= flat.Length)
                {
                    flat.Slice(offset + s, e - s).Fill(value);
                }
            }

            return;
        }

        int len0 = Math.Max(1, dims[0]);
        int len1 = Math.Max(1, dims[1]);
        int len2 = Math.Max(1, dims[2]);
        if (fillAll && subscripts.Length == 0 && start == 0 && endExclusive < 0)
        {
            flat.Fill(value);
            return;
        }

        int i0Start = fillAll ? 0 : (subscripts.Length >= 1 ? Math.Clamp((int)subscripts[0], 0, len0 - 1) : 0);
        int i0End = fillAll ? len0 : i0Start + 1;
        int i1Start = fillAll ? 0 : (subscripts.Length >= 2 ? Math.Clamp((int)subscripts[1], 0, len1 - 1) : 0);
        int i1End = fillAll ? len1 : i1Start + 1;
        int base2 = !fillAll && subscripts.Length >= 3 ? (int)subscripts[2] : 0;
        int s2 = Math.Clamp(base2 + start, 0, len2);
        int e2 = endExclusive < 0 ? len2 : Math.Clamp(base2 + endExclusive, s2, len2);

        for (int i0 = i0Start; i0 < i0End; i0++)
        {
            for (int i1 = i1Start; i1 < i1End; i1++)
            {
                int offset = ((i0 * len1) + i1) * len2;
                if (offset + e2 <= flat.Length)
                {
                    flat.Slice(offset + s2, e2 - s2).Fill(value);
                }
            }
        }
    }

    private static string[] SplitTopLevelColonsSimple(string s)
    {
        var parts = new List<string>(4);
        int depth = 0;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']' && depth > 0) depth--;
                else if (c == ':' && depth == 0)
                {
                    parts.Add(s[start..i]);
                    start = i + 1;
                }
            }
        }

        parts.Add(s[start..]);
        return [.. parts];
    }

    // -------------------------------------------------------------------------
    // Graphics, Memory, HTML_TAGSPLIT, BGImage, Tooltip
    // -------------------------------------------------------------------------

    private static long EvalGraphicsGetTextSize(EmueraEngine engine, VariableStore vars, CallWindow w)
    {
        string text = w.GetStr(0);
        string fontName = w.GetStr(1);
        float fontSize = (float)Math.Max(1L, w.GetInt(2, engine.Options.FontSize));
        FontStyleFlags style = w.HasArg(3) ? (FontStyleFlags)(w.GetInt(3) & 0xF) : FontStyleFlags.Regular;

        SKSize size = engine.Graphics.GetTextSize(text, fontName, fontSize, style);
        long width = (long)Math.Round(size.Width);
        long height = (long)Math.Round(size.Height);
        if (vars.ResultSpan.Length > 1)
        {
            vars.ResultSpan[1] = height;
        }

        return width;
    }

    private static long EvalClearMemory()
    {
        long before;
        long after;
        using (Process p1 = Process.GetCurrentProcess())
        {
            before = p1.WorkingSet64;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using (Process p2 = Process.GetCurrentProcess())
        {
            after = p2.WorkingSet64;
        }

        return Math.Max(0L, before - after);
    }

    private static void ExecuteHtmlTagSplit(VariableStore vars, CallWindow w)
    {
        string html = w.GetStr(0);
        string[] tokens = HtmlPrintEngine.TagSplit(html);

        Span<string> dstArr = w.Count >= 2 && w.GetTag(1) == CallArgTag.ByRef
            ? w.ResolveRefStrSpan(1)
            : vars.ResultsSpan;
        int copyCount = Math.Min(tokens.Length, dstArr.Length);
        for (int i = 0; i < copyCount; i++)
        {
            dstArr[i] = tokens[i];
        }

        if (w.Count >= 3 && w.GetTag(2) == CallArgTag.ByRef)
        {
            w.WriteRefScalarInt(2, tokens.Length);
        }
        else
        {
            vars.Result = tokens.Length;
        }
    }

    private static void ExecuteSetBgImage(EmueraEngine engine, CallWindow w)
    {
        if (!w.HasArg(0))
        {
            return;
        }

        string spriteName = w.GetStr(0);
        int depth = 0;
        float opacity = 1f;

        if (w.HasArg(1))
        {
            if (w.GetTag(1) == CallArgTag.Int)
            {
                depth = (int)w.GetInt(1);
            }
            else if (int.TryParse(w.GetStr(1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDepth))
            {
                depth = parsedDepth;
            }
        }

        if (w.HasArg(2))
        {
            if (w.GetTag(2) == CallArgTag.Int)
            {
                long rawOp = w.GetInt(2);
                opacity = rawOp > 1 ? rawOp / 255f : rawOp;
            }
            else if (float.TryParse(w.GetStr(2).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedOp))
            {
                opacity = parsedOp > 1f ? parsedOp / 255f : parsedOp;
            }
        }

        engine.ConsoleBuffer.SetBackgroundImage(spriteName, depth, opacity);
    }

    private static void ExecuteTooltipInstruction(EmueraEngine engine, BuiltinInstructionKind kind, CallWindow w)
    {
        TooltipState cur = engine.ConsoleBuffer.Tooltip;
        engine.ConsoleBuffer.Tooltip = kind switch
        {
            BuiltinInstructionKind.TOOLTIP_SETCOLOR => cur with
            {
                ForeColorRgba = Rgb24ToRgba(w.GetInt(0)),
                BackColorRgba = Rgb24ToRgba(w.GetInt(1)),
            },
            BuiltinInstructionKind.TOOLTIP_SETDELAY => cur with { DelayMs = Math.Clamp((int)w.GetInt(0), 0, 32767) },
            BuiltinInstructionKind.TOOLTIP_SETDURATION => cur with { DurationMs = Math.Clamp((int)w.GetInt(0), 0, 32767) },
            BuiltinInstructionKind.TOOLTIP_SETFONT => cur with { FontFamily = w.GetStr(0) },
            BuiltinInstructionKind.TOOLTIP_SETFONTSIZE => cur with { FontSize = Math.Max(1, (int)w.GetInt(0)) },
            BuiltinInstructionKind.TOOLTIP_CUSTOM => cur with { CustomEnabled = w.GetInt(0) != 0 },
            BuiltinInstructionKind.TOOLTIP_FORMAT => cur with { Format = (int)w.GetInt(0) },
            BuiltinInstructionKind.TOOLTIP_IMG => cur with { ImageEnabled = w.GetInt(0) != 0 },
            _ => cur,
        };
    }

    // -------------------------------------------------------------------------
    // SAVEVAR / SAVECHARA Instruction Helpers
    // -------------------------------------------------------------------------

    private static void ExecuteSaveVar(EmueraEngine engine, CallWindow w)
    {
        string name = w.GetStr(0);
        string desc = w.HasArg(1) ? w.GetStr(1) : string.Empty;
        var varNames = new List<string>(Math.Max(0, w.Count - 2));
        for (int i = 2; i < w.Count; i++)
        {
            varNames.Add(w.GetStr(i));
        }

        engine.SaveVarData(name, desc, varNames);
    }

    private static void ExecuteSaveChara(EmueraEngine engine, CallWindow w)
    {
        string name = w.GetStr(0);
        string desc = w.HasArg(1) ? w.GetStr(1) : string.Empty;
        var indices = new List<int>(Math.Max(0, w.Count - 2));
        for (int i = 2; i < w.Count; i++)
        {
            indices.Add(checked((int)w.GetInt(i)));
        }

        engine.SaveCharaData(name, desc, indices);
    }

    // -------------------------------------------------------------------------
    // Legacy Display & Parameter Update Instructions (PRINT_* / UPCHECK / CUPCHECK)
    // -------------------------------------------------------------------------

    private static void ExecuteLegacyPrintCharaStat(EmueraEngine engine, VariableStore vars, BuiltinInstructionKind kind, int charaIdx)
    {
        CharacterStore chars = vars.Characters;
        if ((uint)charaIdx >= (uint)chars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(charaIdx), $"{kind}: character index {charaIdx} out of range [0..{chars.Count}).");
        }

        (string varName, string nameVarName) = kind switch
        {
            BuiltinInstructionKind.PRINT_ABL => ("ABL", "ABLNAME"),
            BuiltinInstructionKind.PRINT_TALENT => ("TALENT", "TALENTNAME"),
            BuiltinInstructionKind.PRINT_MARK => ("MARK", "MARKNAME"),
            BuiltinInstructionKind.PRINT_EXP => ("EXP", "EXPNAME"),
            _ => ("PALAM", "PALAMNAME"),
        };

        if (!vars.TryResolveExistingSlot(varName, out int valSlotId))
        {
            return;
        }

        ReadOnlySpan<long> values = chars.GetIntSpan(charaIdx, valSlotId);
        ReadOnlySpan<string> names = vars.TryResolveExistingSlot(nameVarName, out int nameSlotId)
            ? vars.GetStrSpan(nameSlotId)
            : [];

        int count = Math.Min(values.Length, names.Length);
        var lineSb = new StringBuilder();
        int perLine = Math.Max(1, engine.Options.PrintCPerLine);
        int col = 0;

        for (int i = 0; i < count; i++)
        {
            string label = names[i];
            long val = values[i];
            if (string.IsNullOrEmpty(label) || val == 0)
            {
                continue;
            }

            if (kind == BuiltinInstructionKind.PRINT_TALENT)
            {
                lineSb.Append('[').Append(label).Append("] ");
            }
            else if (kind == BuiltinInstructionKind.PRINT_MARK)
            {
                lineSb.Append(label).Append(":LV").Append(val).Append(' ');
            }
            else if (kind == BuiltinInstructionKind.PRINT_PALAM)
            {
                long lv = vars.GetPalamLv(val, 15);
                string bar = EastAsianStringOps.BarStr(val, Math.Max(1L, val), 10, vars.ReplaceConfig.BarChar1, vars.ReplaceConfig.BarChar2);
                lineSb.Append(label).Append("Lv").Append(lv).Append(bar).Append(val).Append(' ');
            }
            else
            {
                lineSb.Append(label).Append(':').Append(val).Append(' ');
            }

            col++;
            if (kind != BuiltinInstructionKind.PRINT_TALENT && col >= perLine)
            {
                engine.PrintStyled(lineSb.ToString(), PrintModifierFlags.NewLine);
                lineSb.Clear();
                col = 0;
            }
        }

        if (lineSb.Length > 0)
        {
            engine.PrintStyled(lineSb.ToString(), PrintModifierFlags.NewLine);
        }
    }

    private static void ExecutePrintItem(EmueraEngine engine, VariableStore vars)
    {
        if (!vars.TryResolveExistingSlot("ITEM", out int itemSlot) || !vars.TryResolveExistingSlot("ITEMNAME", out int nameSlot))
        {
            return;
        }

        ReadOnlySpan<long> items = vars.GetIntSpan(itemSlot);
        ReadOnlySpan<string> names = vars.GetStrSpan(nameSlot);
        int count = Math.Min(items.Length, names.Length);
        var sb = new StringBuilder();

        for (int i = 0; i < count; i++)
        {
            if (items[i] <= 0 || string.IsNullOrEmpty(names[i]))
            {
                continue;
            }

            sb.Append(names[i]);
            if (items[i] > 1)
            {
                sb.Append('(').Append(items[i]).Append(')');
            }

            sb.Append(' ');
        }

        engine.PrintStyled(sb.Length > 0 ? sb.ToString() : "なし", PrintModifierFlags.NewLine);
    }

    private static void ExecutePrintShopItem(EmueraEngine engine, VariableStore vars)
    {
        if (!vars.TryResolveExistingSlot("ITEMSALES", out int salesSlot) || !vars.TryResolveExistingSlot("ITEMNAME", out int nameSlot))
        {
            return;
        }

        ReadOnlySpan<long> sales = vars.GetIntSpan(salesSlot);
        ReadOnlySpan<string> names = vars.GetStrSpan(nameSlot);
        ReadOnlySpan<long> prices = vars.TryResolveExistingSlot("ITEMPRICE", out int priceSlot)
            ? vars.GetIntSpan(priceSlot)
            : [];

        int limit = Math.Min(vars.ReplaceConfig.MaxShopItem, Math.Min(sales.Length, names.Length));
        int perLine = Math.Max(1, engine.Options.PrintCPerLine);
        int col = 0;

        for (int i = 0; i < limit; i++)
        {
            if (sales[i] == 0 || string.IsNullOrEmpty(names[i]))
            {
                continue;
            }

            long price = i < prices.Length ? prices[i] : 0L;
            string money = EastAsianStringOps.MoneyStr(price, null, vars.ReplaceConfig.MoneyLabel, vars.ReplaceConfig.MoneyFirst);
            string cell = $"[{i}] {names[i]}({money})";
            engine.PrintButtonColumn(cell, i, rightAlign: false);
            col++;
            if (col >= perLine)
            {
                engine.PrintNewLine();
                col = 0;
            }
        }

        if (col > 0)
        {
            engine.PrintNewLine();
        }
    }

    private static void ExecuteUpCheck(EmueraEngine engine, VariableStore vars)
    {
        int targetIdx = checked((int)vars.Target);
        if ((uint)targetIdx >= (uint)vars.Characters.Count)
        {
            return;
        }

        if (!vars.TryResolveExistingSlot("UP", out int upSlot) ||
            !vars.TryResolveExistingSlot("DOWN", out int downSlot) ||
            !vars.TryResolveExistingSlot("PALAM", out int palamSlot))
        {
            return;
        }

        Span<long> up = vars.GetIntSpan(upSlot);
        Span<long> down = vars.GetIntSpan(downSlot);
        Span<long> palam = vars.Characters.GetIntSpan(targetIdx, palamSlot);
        ReadOnlySpan<string> names = vars.TryResolveExistingSlot("PALAMNAME", out int nameSlot)
            ? vars.GetStrSpan(nameSlot)
            : [];

        int count = Math.Min(palam.Length, Math.Min(up.Length, down.Length));
        for (int i = 0; i < count; i++)
        {
            long u = up[i];
            long d = down[i];
            if (u == 0 && d == 0)
            {
                continue;
            }

            long before = palam[i];
            long after = before + u - d;
            string label = i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : $"PALAM:{i}";
            engine.PrintStyled($"{label} {before}+{u}-{d}={after}", PrintModifierFlags.NewLine);
            palam[i] = after;
            up[i] = 0;
            down[i] = 0;
        }
    }

    private static void ExecuteCUpCheck(EmueraEngine engine, VariableStore vars, int charaIdx)
    {
        if ((uint)charaIdx >= (uint)vars.Characters.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(charaIdx), $"CUPCHECK: character index {charaIdx} out of range [0..{vars.Characters.Count}).");
        }

        if (!vars.TryResolveExistingSlot("CUP", out int cupSlot) ||
            !vars.TryResolveExistingSlot("CDOWN", out int cdownSlot) ||
            !vars.TryResolveExistingSlot("PALAM", out int palamSlot))
        {
            return;
        }

        Span<long> cup = vars.Characters.GetIntSpan(charaIdx, cupSlot);
        Span<long> cdown = vars.Characters.GetIntSpan(charaIdx, cdownSlot);
        Span<long> palam = vars.Characters.GetIntSpan(charaIdx, palamSlot);
        ReadOnlySpan<string> names = vars.TryResolveExistingSlot("PALAMNAME", out int nameSlot)
            ? vars.GetStrSpan(nameSlot)
            : [];

        int count = Math.Min(palam.Length, Math.Min(cup.Length, cdown.Length));
        for (int i = 0; i < count; i++)
        {
            long u = cup[i];
            long d = cdown[i];
            if (u == 0 && d == 0)
            {
                continue;
            }

            long before = palam[i];
            long after = before + u - d;
            string label = i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : $"PALAM:{i}";
            engine.PrintStyled($"{label} {before}+{u}-{d}={after}", PrintModifierFlags.NewLine);
            palam[i] = after;
            cup[i] = 0;
            cdown[i] = 0;
        }
    }
}
