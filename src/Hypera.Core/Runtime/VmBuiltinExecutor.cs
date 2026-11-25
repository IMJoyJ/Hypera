using System.Globalization;
using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Data;
using Hypera.Rendering;

namespace Hypera.Runtime;

/// <summary>
/// Unpacks register call windows (<c>B = intBase, C = strBase, D = argCount, Aux = packed CallArgTags</c>)
/// and dispatches all built-in Emuera expression methods, statements, console/graphics ops, and EM+EE container ops.
/// </summary>
internal static class VmBuiltinExecutor
{
    internal readonly ref struct CallWindow
    {
        public readonly EmueraEngine Engine;
        public readonly CallFrame Frame;
        public readonly ushort IntBase;
        public readonly ushort StrBase;
        public readonly int Count;
        public readonly ushort Aux;

        public CallWindow(EmueraEngine engine, CallFrame frame, Instruction insn)
        {
            Engine = engine;
            Frame = frame;
            IntBase = insn.B;
            StrBase = insn.C;
            Count = insn.D;
            Aux = insn.Aux;
        }

        public CallArgTag GetTag(int index)
        {
            if ((uint)index >= (uint)Count)
            {
                return CallArgTag.Omitted;
            }

            if (Count <= BytecodeAbi.MaxInlineBuiltinArgs)
            {
                return BytecodeAbi.UnpackBuiltinArgTag(Aux, index);
            }

            int spillReg = IntBase + Count + index;
            return spillReg < Frame.IntRegisters.Length ? (CallArgTag)Frame.IntRegisters[spillReg] : CallArgTag.Omitted;
        }

        public bool HasArg(int index) => (uint)index < (uint)Count && GetTag(index) != CallArgTag.Omitted;

        public long GetInt(int index, long defaultValue = 0L) =>
            HasArg(index) && (IntBase + index) < Frame.IntRegisters.Length
                ? Frame.IntRegisters[IntBase + index]
                : defaultValue;

        public string GetStr(int index, string defaultValue = "") =>
            HasArg(index) && (StrBase + index) < Frame.StrRegisters.Length
                ? (Frame.StrRegisters[StrBase + index] ?? defaultValue)
                : defaultValue;

        public string GetAnyAsKeyString(int index)
        {
            CallArgTag tag = GetTag(index);
            if (tag == CallArgTag.Str)
            {
                return GetStr(index);
            }

            if (tag == CallArgTag.ByRef)
            {
                return ReadRefScalarStr(index);
            }

            return GetInt(index).ToString(CultureInfo.InvariantCulture);
        }

        public (int SlotId, VariableSlotOrigin Origin, int FixedIndex0) GetByRef(int index) =>
            BytecodeAbi.UnpackByRefTarget(Frame.IntRegisters[IntBase + index]);

        public Span<long> ResolveRefIntSpan(int index)
        {
            var (slotId, origin, fixedIdx0) = GetByRef(index);
            if (origin is VariableSlotOrigin.FrameLocal or VariableSlotOrigin.RefPassthrough)
            {
                return Frame.LocalSlots[slotId].GetIntSpan();
            }

            VariableStore vars = Engine.RequireVariableStore();
            VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
            if (slot.IsCharaData)
            {
                int charaIdx = fixedIdx0 >= 0 ? fixedIdx0 : checked((int)vars.Target);
                return vars.Characters.GetIntSpan(charaIdx, slotId);
            }

            return vars.GetIntSpan(slotId);
        }

        public Span<string> ResolveRefStrSpan(int index)
        {
            var (slotId, origin, fixedIdx0) = GetByRef(index);
            if (origin is VariableSlotOrigin.FrameLocal or VariableSlotOrigin.RefPassthrough)
            {
                return Frame.LocalSlots[slotId].GetStrSpan();
            }

            VariableStore vars = Engine.RequireVariableStore();
            VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
            if (slot.IsCharaData)
            {
                int charaIdx = fixedIdx0 >= 0 ? fixedIdx0 : checked((int)vars.Target);
                return vars.Characters.GetStrSpan(charaIdx, slotId);
            }

            return vars.GetStrSpan(slotId);
        }

        public VariableValueKind ResolveRefValueKind(int index)
        {
            var (slotId, origin, _) = GetByRef(index);
            if (origin is VariableSlotOrigin.FrameLocal or VariableSlotOrigin.RefPassthrough)
            {
                return Frame.LocalSlots[slotId].ValueKind;
            }

            return Engine.RequireVariableStore().GetSlotOrThrow(slotId).ValueKind;
        }

        public void WriteRefScalarInt(int index, long value)
        {
            Span<long> span = ResolveRefIntSpan(index);
            var (_, _, fixedIdx0) = GetByRef(index);
            int targetIdx = fixedIdx0 >= 0 && fixedIdx0 < span.Length ? fixedIdx0 : 0;
            if ((uint)targetIdx < (uint)span.Length)
            {
                span[targetIdx] = value;
            }
        }

        public string ReadRefScalarStr(int index)
        {
            Span<string> span = ResolveRefStrSpan(index);
            var (_, _, fixedIdx0) = GetByRef(index);
            int targetIdx = fixedIdx0 >= 0 && fixedIdx0 < span.Length ? fixedIdx0 : 0;
            return (uint)targetIdx < (uint)span.Length ? span[targetIdx] : string.Empty;
        }

        public void WriteRefScalarStr(int index, string value)
        {
            Span<string> span = ResolveRefStrSpan(index);
            var (_, _, fixedIdx0) = GetByRef(index);
            int targetIdx = fixedIdx0 >= 0 && fixedIdx0 < span.Length ? fixedIdx0 : 0;
            if ((uint)targetIdx < (uint)span.Length)
            {
                span[targetIdx] = value ?? string.Empty;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Console & Rich Layout Opcodes (0x00C0..0x00D1)
    // -------------------------------------------------------------------------

    public static bool ExecuteConsoleOp(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var flags = (PrintModifierFlags)insn.Aux;
        if ((flags & PrintModifierFlags.DebugOnly) != 0 && !engine.Options.UseDebugCommand)
        {
            return false;
        }

        if (engine.IsSkipDisplay && insn.Op is not (OpCode.SetColor or OpCode.SetBgColor or OpCode.SetStyle or OpCode.SetAlignment or OpCode.SetFont))
        {
            return false;
        }

        string[] strRegs = frame.StrRegisters;
        long[] intRegs = frame.IntRegisters;

        switch (insn.Op)
        {
            case OpCode.Print:
            {
                string text = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                engine.PrintStyled(text, flags);
                return (flags & (PrintModifierFlags.WaitInput | PrintModifierFlags.NoNewLineWait)) != 0 && engine.IssueAnyKeyWait();
            }

            case OpCode.PrintLine:
            {
                string text = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                engine.PrintStyled(text, flags | PrintModifierFlags.NewLine);
                return false;
            }

            case OpCode.PrintWait:
            {
                string text = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                PrintModifierFlags eff = (flags & PrintModifierFlags.NoNewLineWait) != 0
                    ? (flags | PrintModifierFlags.WaitInput)
                    : (flags | PrintModifierFlags.NewLine | PrintModifierFlags.WaitInput);
                engine.PrintStyled(text, eff);
                return engine.IssueAnyKeyWait();
            }

            case OpCode.PrintSingle:
            {
                string text = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                engine.PrintStyled(text, flags | PrintModifierFlags.SingleLine | PrintModifierFlags.NewLine);
                return false;
            }

            case OpCode.PrintColumn:
            {
                string text = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                bool rightAlign = (flags & PrintModifierFlags.LeftAlignColumn) == 0;
                engine.PrintColumnCell(text, rightAlign, flags);
                return false;
            }

            case OpCode.PrintButton:
            {
                string label = (strRegs[insn.A] ?? string.Empty).Replace("\n", string.Empty, StringComparison.Ordinal);
                bool isStrButton = (insn.Aux & 0x8000) != 0;
                if (isStrButton)
                {
                    engine.PrintButtonSpan(label, isInt: false, intVal: 0L, strVal: strRegs[insn.B] ?? string.Empty);
                }
                else
                {
                    engine.PrintButtonSpan(label, isInt: true, intVal: intRegs[insn.B], strVal: null);
                }

                return false;
            }

            case OpCode.PrintBar:
            {
                VariableStore vars = engine.RequireVariableStore();
                string bar = EastAsianStringOps.BarStr(
                    intRegs[insn.A],
                    intRegs[insn.B],
                    intRegs[insn.C],
                    vars.ReplaceConfig.BarChar1,
                    vars.ReplaceConfig.BarChar2);
                engine.PrintStyled(bar, flags);
                return false;
            }

            case OpCode.DrawLine:
            {
                string? custom = insn.Aux != 0 && insn.A < strRegs.Length ? strRegs[insn.A] : null;
                engine.PrintDrawLine(custom);
                return false;
            }

            case OpCode.ClearLine:
                engine.ClearLines(insn.A < intRegs.Length ? (int)Math.Max(0, intRegs[insn.A]) : Math.Max(0, insn.Immediate));
                return false;

            case OpCode.ReuseLastLine:
            {
                string text = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                engine.ReuseLastLine(text);
                return false;
            }

            case OpCode.HtmlPrint:
            {
                string html = insn.A < strRegs.Length ? (strRegs[insn.A] ?? string.Empty) : string.Empty;
                bool isIsland = (flags & PrintModifierFlags.HtmlIsland) != 0;
                bool noNewLine = (flags & PrintModifierFlags.HtmlNoNewLine) != 0;
                engine.PrintHtml(html, isIsland, noNewLine);
                return false;
            }

            case OpCode.PrintImg:
            {
                string resName = strRegs[insn.A] ?? string.Empty;
                int w = insn.B < intRegs.Length ? (int)intRegs[insn.B] : engine.Options.FontSize;
                int h = insn.C < intRegs.Length ? (int)intRegs[insn.C] : engine.Options.FontSize;
                int y = insn.D < intRegs.Length ? (int)intRegs[insn.D] : 0;
                engine.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.DrawSprite, new LogicalRect(0, y, w, h), ResourceKey: resName));
                return false;
            }

            case OpCode.PrintShape:
            {
                int x = insn.A < intRegs.Length ? (int)intRegs[insn.A] : 0;
                int y = insn.B < intRegs.Length ? (int)intRegs[insn.B] : 0;
                int w = insn.C < intRegs.Length ? (int)intRegs[insn.C] : 0;
                int h = insn.D < intRegs.Length ? (int)intRegs[insn.D] : 0;
                engine.AppendDisplayOperation(new DisplayOperation(DisplayOperationKind.DrawShape, new LogicalRect(x, y, w, h), ColorRgba: engine.CurrentForeColorRgba));
                return false;
            }

            case OpCode.SetColor:
                if (insn.Aux == 0xFFFF)
                {
                    engine.ResetForeColor();
                }
                else
                {
                    engine.SetForeColorRgb24(intRegs[insn.A]);
                }

                return false;

            case OpCode.SetBgColor:
                if (insn.Aux == 0xFFFF)
                {
                    engine.ResetBackColor();
                }
                else
                {
                    engine.SetBackColorRgb24(intRegs[insn.A]);
                }

                return false;

            case OpCode.SetStyle:
                engine.CurrentFontStyle = insn.Aux == 0xFFFF ? 0 : (int)intRegs[insn.A];
                return false;

            case OpCode.SetAlignment:
            {
                string align = insn.A < strRegs.Length ? (strRegs[insn.A] ?? "LEFT") : "LEFT";
                engine.SetTextAlignment(align);
                return false;
            }

            case OpCode.SetFont:
                engine.CurrentFontName = insn.Aux == 0xFFFF || insn.A >= strRegs.Length || string.IsNullOrEmpty(strRegs[insn.A])
                    ? engine.Options.FontName
                    : strRegs[insn.A];
                return false;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Dedicated Graphics / Sprite / CBG Opcodes (0x00E0..0x00EC)
    // -------------------------------------------------------------------------

    public static void ExecuteGraphicsOp(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        long[] ir = frame.IntRegisters;
        string[] sr = frame.StrRegisters;

        switch (insn.Op)
        {
            case OpCode.GraphicsCreate:
                ir[insn.A] = engine.Graphics.Create((int)ir[insn.B], (int)ir[insn.C], (int)ir[insn.D]) ? 1L : 0L;
                break;

            case OpCode.GraphicsDispose:
                ir[insn.A] = engine.Graphics.DisposeSurface((int)ir[insn.B]) ? 1L : 0L;
                break;

            case OpCode.GraphicsClear:
                ir[insn.A] = engine.Graphics.Clear((int)ir[insn.B], (uint)ir[insn.C]) ? 1L : 0L;
                break;

            case OpCode.SpriteDispose:
                ir[insn.A] = engine.Sprites.DisposeSprite(sr[insn.B] ?? string.Empty) ? 1L : 0L;
                break;

            case OpCode.CbgSetGraphics:
                engine.ConsoleBuffer.CbgSetGraphics((int)ir[insn.B], (int)ir[insn.C], (int)ir[insn.D], insn.Immediate);
                if (insn.A < ir.Length) ir[insn.A] = 1L;
                break;

            case OpCode.CbgSetButtonMap:
                engine.ConsoleBuffer.CbgSetButtonMap((int)ir[insn.B]);
                if (insn.A < ir.Length) ir[insn.A] = 1L;
                break;

            case OpCode.CbgSetSprite:
                engine.ConsoleBuffer.CbgSetSprite(sr[insn.B] ?? string.Empty, (int)ir[insn.C], (int)ir[insn.D], insn.Immediate);
                if (insn.A < ir.Length) ir[insn.A] = 1L;
                break;

            case OpCode.CbgClear:
                engine.ConsoleBuffer.CbgClear();
                if (insn.A < ir.Length) ir[insn.A] = 1L;
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Extended Subsystem Opcodes: DataTableOp / XmlOp / MapOp / AudioOp
    // -------------------------------------------------------------------------

    public static void ExecuteDataTableOp(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var subOp = (DataTableSubOp)(insn.Immediate != 0 ? insn.Immediate : insn.Aux);
        var w = new CallWindow(engine, frame, insn);
        if (subOp == DataTableSubOp.ToXml)
        {
            frame.StrRegisters[insn.A] = EvalDataTableToXml(engine, w);
        }
        else if (subOp == DataTableSubOp.CellGets)
        {
            frame.StrRegisters[insn.A] = EvalDataTableCellGets(engine, w);
        }
        else
        {
            long res = EvalDataTableInt(engine, subOp, w);
            if (insn.A < frame.IntRegisters.Length)
            {
                frame.IntRegisters[insn.A] = res;
            }
        }
    }

    public static void ExecuteXmlOp(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var subOp = (XmlSubOp)(insn.Immediate != 0 ? insn.Immediate : insn.Aux);
        var w = new CallWindow(engine, frame, insn);
        if (subOp == XmlSubOp.ToStr)
        {
            frame.StrRegisters[insn.A] = engine.RequireVariableStore().Xmls.ToStr(w.GetAnyAsKeyString(0));
        }
        else
        {
            frame.IntRegisters[insn.A] = EvalXmlInt(engine, subOp, w);
        }
    }

    public static void ExecuteMapOp(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var subOp = (MapSubOp)(insn.Immediate != 0 ? insn.Immediate : insn.Aux);
        var w = new CallWindow(engine, frame, insn);
        if (subOp is MapSubOp.Get or MapSubOp.GetKeys or MapSubOp.ToXml)
        {
            frame.StrRegisters[insn.A] = EvalMapStr(engine, subOp, w);
        }
        else
        {
            frame.IntRegisters[insn.A] = EvalMapInt(engine, subOp, w);
        }
    }

    public static void ExecuteAudioOp(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var subOp = (AudioSubOp)(insn.Immediate != 0 ? insn.Immediate : insn.Aux);
        var w = new CallWindow(engine, frame, insn);
        switch (subOp)
        {
            case AudioSubOp.PlaySound:
            {
                string path = w.GetStr(0);
                int repeatCount = w.HasArg(1) ? (int)w.GetInt(1, 1) : 1;
                int ch = w.HasArg(2) ? Math.Clamp((int)w.GetInt(2), 0, 9) : 0;
                engine.Audio.PlaySoundEffect(ch, path, loop: repeatCount <= 0 || repeatCount > 1);
                break;
            }
            case AudioSubOp.StopSound:
                if (w.HasArg(0))
                {
                    int ch = (int)w.GetInt(0);
                    if (ch >= 0 && ch < 10) engine.Audio.StopSoundEffect(ch);
                    else engine.Audio.StopAllSoundEffects();
                }
                else
                {
                    engine.Audio.StopAllSoundEffects();
                }

                break;
            case AudioSubOp.PlayBgm:
                engine.Audio.PlayBgm(w.GetStr(0), loop: true);
                break;
            case AudioSubOp.StopBgm:
                engine.Audio.StopBgm();
                break;
            case AudioSubOp.SetSoundVolume:
            {
                int vol = (int)w.GetInt(0);
                for (int ch = 0; ch < 10; ch++)
                {
                    engine.Audio.SetSoundEffectVolume(ch, vol);
                }

                break;
            }
            case AudioSubOp.SetBgmVolume:
                engine.Audio.SetBgmVolume((int)w.GetInt(0));
                break;
        }
    }

    // -------------------------------------------------------------------------
    // BuiltinCallInt (270 Built-in Expression Methods Returning Int64)
    // -------------------------------------------------------------------------

    public static long ExecuteBuiltinMethodInt(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var kind = (BuiltinMethodKind)insn.Immediate;
        var w = new CallWindow(engine, frame, insn);
        VariableStore vars = engine.RequireVariableStore();
        CharacterStore chars = vars.Characters;

        switch (kind)
        {
            case BuiltinMethodKind.ResolveKeywordIndex:
            {
                string varName = w.GetStr(0);
                int dim = (int)w.GetInt(1, 1);
                string key = w.GetStr(2);
                if (vars.TryResolveKeywordIndex(varName, dim, key, out long resolved))
                {
                    return resolved;
                }

                throw new KeyNotFoundException($"Unknown CSV/ERD keyword '{key}' for variable '{varName}'.");
            }

            // Math & RNG
            case BuiltinMethodKind.RAND:
                return w.Count <= 1
                    ? vars.Random.NextInt64(w.GetInt(0))
                    : vars.Random.NextInt64(w.GetInt(0, 0L), w.GetInt(1));

            case BuiltinMethodKind.ABS:
                return SpanBuiltins.Abs(w.GetInt(0));

            case BuiltinMethodKind.SIGN:
                return SpanBuiltins.Sign(w.GetInt(0));

            case BuiltinMethodKind.SQRT:
                return SpanBuiltins.Sqrt(w.GetInt(0));

            case BuiltinMethodKind.CBRT:
                return SpanBuiltins.Cbrt(w.GetInt(0));

            case BuiltinMethodKind.LOG:
                return SpanBuiltins.Log(w.GetInt(0));

            case BuiltinMethodKind.LOG10:
                return SpanBuiltins.Log10(w.GetInt(0));

            case BuiltinMethodKind.EXPONENT:
                return SpanBuiltins.Exponent(w.GetInt(0));

            case BuiltinMethodKind.POWER:
                return SpanBuiltins.Power(w.GetInt(0), w.GetInt(1));

            case BuiltinMethodKind.LIMIT:
                return SpanBuiltins.Limit(w.GetInt(0), w.GetInt(1), w.GetInt(2));

            case BuiltinMethodKind.MIN:
            {
                long best = w.GetInt(0);
                for (int i = 1; i < w.Count; i++)
                {
                    long v = w.GetInt(i);
                    if (v < best) best = v;
                }

                return best;
            }

            case BuiltinMethodKind.MAX:
            {
                long best = w.GetInt(0);
                for (int i = 1; i < w.Count; i++)
                {
                    long v = w.GetInt(i);
                    if (v > best) best = v;
                }

                return best;
            }

            // Bitwise & Array Queries
            case BuiltinMethodKind.GETBIT:
                return SpanBuiltins.GetBit(w.GetInt(0), w.GetInt(1));

            case BuiltinMethodKind.INRANGE:
                return SpanBuiltins.InRange(w.GetInt(0), w.GetInt(1), w.GetInt(2));

            case BuiltinMethodKind.SUMARRAY:
            {
                ReadOnlySpan<long> span = w.ResolveRefIntSpan(0);
                int start = w.HasArg(1) ? (int)w.GetInt(1) : 0;
                int end = w.HasArg(2) ? (int)w.GetInt(2) : span.Length;
                return SpanBuiltins.SumArray(span, start, end);
            }

            case BuiltinMethodKind.MAXARRAY:
            case BuiltinMethodKind.MINARRAY:
            {
                ReadOnlySpan<long> span = w.ResolveRefIntSpan(0);
                int start = w.HasArg(1) ? (int)w.GetInt(1) : 0;
                int end = w.HasArg(2) ? (int)w.GetInt(2) : span.Length;
                return kind == BuiltinMethodKind.MAXARRAY
                    ? SpanBuiltins.MaxArray(span, start, end)
                    : SpanBuiltins.MinArray(span, start, end);
            }

            case BuiltinMethodKind.INRANGEARRAY:
            {
                ReadOnlySpan<long> span = w.ResolveRefIntSpan(0);
                long min = w.GetInt(1);
                long max = w.GetInt(2);
                int start = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                int end = w.HasArg(4) ? (int)w.GetInt(4) : span.Length;
                return SpanBuiltins.InRangeArray(span, min, max, start, end);
            }

            case BuiltinMethodKind.MATCH:
            {
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    ReadOnlySpan<string> span = w.ResolveRefStrSpan(0);
                    int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                    int end = w.HasArg(3) ? (int)w.GetInt(3) : span.Length;
                    return SpanBuiltins.Match(span, w.GetStr(1), start, end);
                }
                else
                {
                    ReadOnlySpan<long> span = w.ResolveRefIntSpan(0);
                    int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                    int end = w.HasArg(3) ? (int)w.GetInt(3) : span.Length;
                    return SpanBuiltins.Match(span, w.GetInt(1), start, end);
                }
            }

            case BuiltinMethodKind.CMATCH:
            {
                var (slotId, _, fixedSub) = w.GetByRef(0);
                ReadOnlySpan<long> sub = fixedSub >= 0 ? [fixedSub] : [];
                int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                int end = w.HasArg(3) ? (int)w.GetInt(3) : chars.Count;
                return w.GetTag(1) == CallArgTag.Str
                    ? chars.CMatchStr(slotId, sub, w.GetStr(1), start, end)
                    : chars.CMatchInt(slotId, sub, w.GetInt(1), start, end);
            }

            case BuiltinMethodKind.SUMCARRAY:
            {
                var (slotId, _, fixedSub) = w.GetByRef(0);
                ReadOnlySpan<long> sub = fixedSub >= 0 ? [fixedSub] : [];
                int start = w.HasArg(1) ? (int)w.GetInt(1) : 0;
                int end = w.HasArg(2) ? (int)w.GetInt(2) : chars.Count;
                return chars.SumCArray(slotId, sub, start, end);
            }

            case BuiltinMethodKind.MAXCARRAY:
            case BuiltinMethodKind.MINCARRAY:
            {
                var (slotId, _, fixedSub) = w.GetByRef(0);
                ReadOnlySpan<long> sub = fixedSub >= 0 ? [fixedSub] : [];
                int start = w.HasArg(1) ? (int)w.GetInt(1) : 0;
                int end = w.HasArg(2) ? (int)w.GetInt(2) : chars.Count;
                return chars.MaxCArray(slotId, sub, start, end, isMax: kind == BuiltinMethodKind.MAXCARRAY);
            }

            case BuiltinMethodKind.INRANGECARRAY:
            {
                var (slotId, _, fixedSub) = w.GetByRef(0);
                ReadOnlySpan<long> sub = fixedSub >= 0 ? [fixedSub] : [];
                int start = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                int end = w.HasArg(4) ? (int)w.GetInt(4) : chars.Count;
                return chars.InRangeCArray(slotId, sub, w.GetInt(1), w.GetInt(2), start, end);
            }

            case BuiltinMethodKind.FINDELEMENT:
            case BuiltinMethodKind.FINDLASTELEMENT:
            {
                bool isLast = kind == BuiltinMethodKind.FINDLASTELEMENT;
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    ReadOnlySpan<string> span = w.ResolveRefStrSpan(0);
                    int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                    int end = w.HasArg(3) ? (int)w.GetInt(3) : span.Length;
                    bool isExact = w.HasArg(4) && w.GetInt(4) != 0;
                    return SpanBuiltins.FindElement(span, w.GetStr(1), vars.Regexes, start, end, isExact, isLast);
                }
                else
                {
                    ReadOnlySpan<long> span = w.ResolveRefIntSpan(0);
                    int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                    int end = w.HasArg(3) ? (int)w.GetInt(3) : span.Length;
                    return SpanBuiltins.FindElement(span, w.GetInt(1), start, end, isLast);
                }
            }

            case BuiltinMethodKind.GROUPMATCH:
            {
                if (w.GetTag(0) == CallArgTag.Str)
                {
                    string target = w.GetStr(0);
                    long hits = 0;
                    for (int i = 1; i < w.Count; i++)
                    {
                        if (string.Equals(target, w.GetStr(i), StringComparison.Ordinal)) hits++;
                    }

                    return hits;
                }
                else
                {
                    long target = w.GetInt(0);
                    long hits = 0;
                    for (int i = 1; i < w.Count; i++)
                    {
                        if (target == w.GetInt(i)) hits++;
                    }

                    return hits;
                }
            }

            case BuiltinMethodKind.ALLSAMES:
            {
                if (w.Count <= 1) return 1L;
                if (w.GetTag(0) == CallArgTag.Str)
                {
                    var arr = new string[w.Count];
                    for (int i = 0; i < w.Count; i++) arr[i] = w.GetStr(i);
                    return SpanBuiltins.AllSames(arr);
                }
                else
                {
                    var arr = new long[w.Count];
                    for (int i = 0; i < w.Count; i++) arr[i] = w.GetInt(i);
                    return SpanBuiltins.AllSames(arr);
                }
            }

            case BuiltinMethodKind.NOSAMES:
            {
                if (w.Count <= 1) return 1L;
                if (w.GetTag(0) == CallArgTag.Str)
                {
                    var arr = new string[w.Count];
                    for (int i = 0; i < w.Count; i++) arr[i] = w.GetStr(i);
                    return SpanBuiltins.NoSames(arr);
                }
                else
                {
                    var arr = new long[w.Count];
                    for (int i = 0; i < w.Count; i++) arr[i] = w.GetInt(i);
                    return SpanBuiltins.NoSames(arr);
                }
            }

            case BuiltinMethodKind.ARRAYMSORT:
                return EvalArrayMultiSort(engine, w);

            case BuiltinMethodKind.ARRAYMSORTEX:
                return EvalArrayMultiSortEx(engine, w);

            case BuiltinMethodKind.GETPALAMLV:
                return vars.GetPalamLv(w.GetInt(0), w.GetInt(1));

            case BuiltinMethodKind.GETEXPLV:
                return vars.GetExpLv(w.GetInt(0), w.GetInt(1));

            case BuiltinMethodKind.GETNUM:
            {
                var (slotId, _, _) = w.GetByRef(0);
                VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
                int dim = w.HasArg(2) ? (int)w.GetInt(2, 1) : 1;
                return vars.TryResolveKeywordIndex(slot.Name, dim, w.GetStr(1), out long idx) ? idx : -1L;
            }

            case BuiltinMethodKind.GETNUMB:
                return vars.TryResolveKeywordIndex(w.GetStr(0), 1, w.GetStr(1), out long idxB) ? idxB : -1L;

            case BuiltinMethodKind.VARSIZE:
            {
                string vName = w.GetStr(0);
                int dim = w.HasArg(1) ? (int)w.GetInt(1, 0) : 0;
                if (frame.TryGetLocal(vName, vars.CompilerOptions.IgnoreCase, out FrameLocalStorage? loc) && loc is not null)
                {
                    return loc.GetDimensionLength(dim);
                }

                string resolved = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(vars, frame, vName, null);
                if (!vars.TryResolveSlot(resolved, out int slotId))
                {
                    return 0L;
                }

                VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
                if (slot.IsCharaData && chars.TryGetSchema(slotId, out CharacterSlotSchema? cs) && cs is not null)
                {
                    return dim switch { 0 => cs.Dim0, 1 => cs.Dim1, 2 => cs.Dim2, _ => 0 };
                }

                return slot.GetDimensionLength(dim);
            }

            // Character Queries
            case BuiltinMethodKind.GETCHARA:
                return chars.GetChara(w.GetInt(0), w.GetInt(1, 0L));

            case BuiltinMethodKind.GETSPCHARA:
                return chars.GetSpChara(w.GetInt(0));

            case BuiltinMethodKind.EXISTCSV:
                return chars.HasTemplate(w.GetInt(0), sp: w.GetInt(1, 0L) != 0) ? 1L : 0L;

            case BuiltinMethodKind.FINDCHARA:
            case BuiltinMethodKind.FINDLASTCHARA:
            {
                bool isLast = kind == BuiltinMethodKind.FINDLASTCHARA;
                var (slotId, _, fixedSub) = w.GetByRef(0);
                ReadOnlySpan<long> sub = fixedSub >= 0 ? [fixedSub] : [];
                int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                int end = w.HasArg(3) ? (int)w.GetInt(3) : chars.Count;
                return w.GetTag(1) == CallArgTag.Str
                    ? chars.FindCharaStr(slotId, sub, w.GetStr(1), start, end, isLast)
                    : chars.FindCharaInt(slotId, sub, w.GetInt(1), start, end, isLast);
            }

            case BuiltinMethodKind.CSVBASE:
            case BuiltinMethodKind.CSVABL:
            case BuiltinMethodKind.CSVMARK:
            case BuiltinMethodKind.CSVEXP:
            case BuiltinMethodKind.CSVRELATION:
            case BuiltinMethodKind.CSVTALENT:
            case BuiltinMethodKind.CSVCFLAG:
            case BuiltinMethodKind.CSVEQUIP:
            case BuiltinMethodKind.CSVJUEL:
                return EvalCsvCharaInt(chars, kind, w.GetInt(0), (int)w.GetInt(1), w.GetInt(2) != 0);

            // String Int Queries
            case BuiltinMethodKind.STRLENS:
                return EastAsianStringOps.GetStrlenS(w.GetStr(0), engine.Options.LanguageCodePage);

            case BuiltinMethodKind.STRLENSU:
                return EastAsianStringOps.GetStrlenSU(w.GetStr(0));

            case BuiltinMethodKind.STRFIND:
                return EastAsianStringOps.StrFind(w.GetStr(0), w.GetStr(1), w.GetInt(2, 0L), engine.Options.LanguageCodePage);

            case BuiltinMethodKind.STRFINDU:
                return EastAsianStringOps.StrFindU(w.GetStr(0), w.GetStr(1), w.GetInt(2, 0L));

            case BuiltinMethodKind.STRCOUNT:
                return EastAsianStringOps.StrCount(w.GetStr(0), w.GetStr(1), vars.Regexes);

            case BuiltinMethodKind.ISNUMERIC:
                return EastAsianStringOps.IsNumeric(w.GetStr(0)) ? 1L : 0L;

            case BuiltinMethodKind.ENCODETOUNI:
                return EastAsianStringOps.EncodeToUni(w.GetStr(0), (int)w.GetInt(1, 0L));

            case BuiltinMethodKind.UNICODEBYTE:
                return EastAsianStringOps.UnicodeByte(w.GetStr(0));

            case BuiltinMethodKind.LINEISEMPTY:
                return !engine.ConsoleBuffer.HasPendingLine ? 1L : 0L;

            case BuiltinMethodKind.REGEXPMATCH:
            {
                string src = w.GetStr(0);
                string pat = w.GetStr(1);
                if (w.Count == 4 && w.GetTag(2) == CallArgTag.ByRef && w.GetTag(3) == CallArgTag.ByRef)
                {
                    Span<string> outGroups = w.ResolveRefStrSpan(3);
                    int mCount = vars.Regexes.Match(src, pat, out int gCount, outGroups);
                    w.WriteRefScalarInt(2, gCount);
                    return mCount;
                }

                bool writeToResults = w.Count >= 3 && w.GetInt(2) != 0;
                int matches = vars.Regexes.Match(src, pat, out int groups, writeToResults ? vars.ResultsSpan : default);
                if (writeToResults && vars.ResultSpan.Length > 1)
                {
                    vars.ResultSpan[1] = groups;
                }

                return matches;
            }

            // Time, Color & Console Queries
            case BuiltinMethodKind.PRINTCPERLINE:
                return engine.Options.PrintCPerLine;

            case BuiltinMethodKind.PRINTCLENGTH:
                return engine.Options.PrintCLength;

            case BuiltinMethodKind.SAVENOS:
                return engine.Options.SaveDataNos;

            case BuiltinMethodKind.GETMILLISECOND:
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            case BuiltinMethodKind.GETSECOND:
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            case BuiltinMethodKind.GETTIME:
            {
                DateTime now = DateTime.Now;
                long packed = now.Year;
                packed = (packed * 100) + now.Month;
                packed = (packed * 100) + now.Day;
                packed = (packed * 100) + now.Hour;
                packed = (packed * 100) + now.Minute;
                packed = (packed * 100) + now.Second;
                packed = (packed * 1000) + now.Millisecond;
                return packed;
            }

            case BuiltinMethodKind.GETCOLOR:
                return RgbaToRgb24(engine.CurrentForeColorRgba);

            case BuiltinMethodKind.GETDEFCOLOR:
                return RgbaToRgb24(engine.Options.ForeColorRgba);

            case BuiltinMethodKind.GETBGCOLOR:
                return RgbaToRgb24(engine.CurrentBackColorRgba);

            case BuiltinMethodKind.GETDEFBGCOLOR:
                return RgbaToRgb24(engine.Options.BackColorRgba);

            case BuiltinMethodKind.GETFOCUSCOLOR:
                return RgbaToRgb24(engine.Options.FocusColorRgba);

            case BuiltinMethodKind.GETSTYLE:
                return engine.CurrentFontStyle;

            case BuiltinMethodKind.CURRENTREDRAW:
                return engine.CurrentRedrawMode;

            case BuiltinMethodKind.ISSKIP:
                return engine.IsSkipDisplay ? 1L : 0L;

            case BuiltinMethodKind.COLOR_FROMRGB:
            {
                long r = w.GetInt(0);
                long g = w.GetInt(1);
                long b = w.GetInt(2);
                if ((ulong)r > 255 || (ulong)g > 255 || (ulong)b > 255)
                {
                    throw new ArgumentOutOfRangeException(nameof(r), "COLOR_FROMRGB channels must be in 0..255.");
                }

                return (r << 16) | (g << 8) | b;
            }

            case BuiltinMethodKind.COLOR_FROMNAME:
                return RenderingColor.TryColorFromName(w.GetStr(0), out long rgb24) ? rgb24 : -1L;

            case BuiltinMethodKind.CLIENTWIDTH:
                return engine.Viewport.LogicalWidth;

            case BuiltinMethodKind.CLIENTHEIGHT:
                return engine.Viewport.LogicalHeight;

            case BuiltinMethodKind.CHKFONT:
                return engine.Fonts.HasFont(w.GetStr(0)) ? 1L : 0L;

            case BuiltinMethodKind.CHKDATA:
                return engine.CheckSaveSlotExists((int)w.GetInt(0), out string summary)
                    ? SetResults0AndReturn(vars, summary, 0L)
                    : SetResults0AndReturn(vars, "No save data", 1L);

            case BuiltinMethodKind.EXISTFILE:
                return engine.FileSystem.FileExists(w.GetStr(0)) ? 1L : 0L;

            case BuiltinMethodKind.EXISTSOUND:
                return engine.CheckSoundExists(w.GetStr(0)) ? 1L : 0L;

            case BuiltinMethodKind.EXISTFUNCTION:
                return engine.FunctionExists(w.GetStr(0)) ? 1L : 0L;

            case BuiltinMethodKind.HTML_STRINGLEN:
                return w.GetInt(1, 0L) != 0
                    ? HtmlPrintEngine.ToPlainText(w.GetStr(0)).Length
                    : EastAsianStringOps.GetStrlenS(HtmlPrintEngine.ToPlainText(w.GetStr(0)), engine.Options.LanguageCodePage);

            // Graphics / Sprite / CBG Built-in Methods
            case BuiltinMethodKind.GCREATE:
                return engine.Graphics.Create((int)w.GetInt(0), (int)w.GetInt(1), (int)w.GetInt(2)) ? 1L : 0L;
            case BuiltinMethodKind.GCREATEFROMFILE:
                return engine.Graphics.CreateFromFile((int)w.GetInt(0), engine.FileSystem, w.GetStr(1), w.HasArg(2) ? (int)w.GetInt(2) : 0) ? 1L : 0L;
            case BuiltinMethodKind.GDISPOSE:
                return engine.Graphics.DisposeSurface((int)w.GetInt(0)) ? 1L : 0L;
            case BuiltinMethodKind.GCREATED:
                return engine.Graphics.IsCreated((int)w.GetInt(0)) ? 1L : 0L;
            case BuiltinMethodKind.GWIDTH:
                return engine.Graphics.TryGetSurface((int)w.GetInt(0), out GraphicsSurface gw) ? gw.Width : 0L;
            case BuiltinMethodKind.GHEIGHT:
                return engine.Graphics.TryGetSurface((int)w.GetInt(0), out GraphicsSurface gh) ? gh.Height : 0L;
            case BuiltinMethodKind.GCLEAR:
                return w.Count >= 6
                    ? (engine.Graphics.ClearRect((int)w.GetInt(0), (uint)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3), (int)w.GetInt(4), (int)w.GetInt(5)) ? 1L : 0L)
                    : (engine.Graphics.Clear((int)w.GetInt(0), (uint)w.GetInt(1)) ? 1L : 0L);
            case BuiltinMethodKind.GFILLRECTANGLE:
                return engine.Graphics.FillRectangle((int)w.GetInt(0), (int)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3), (int)w.GetInt(4)) ? 1L : 0L;
            case BuiltinMethodKind.GSETBRUSH:
                return engine.Graphics.SetBrush((int)w.GetInt(0), (uint)w.GetInt(1)) ? 1L : 0L;
            case BuiltinMethodKind.GGETBRUSH:
                return engine.Graphics.GetBrush((int)w.GetInt(0));
            case BuiltinMethodKind.GSETPEN:
                return engine.Graphics.SetPen((int)w.GetInt(0), (uint)w.GetInt(1), w.HasArg(2) ? (float)w.GetInt(2, 1) : 1f) ? 1L : 0L;
            case BuiltinMethodKind.GGETPEN:
                return engine.Graphics.GetPen((int)w.GetInt(0));
            case BuiltinMethodKind.GGETPENWIDTH:
                return engine.Graphics.GetPenWidth((int)w.GetInt(0));
            case BuiltinMethodKind.GSETFONT:
                return engine.Graphics.SetFont((int)w.GetInt(0), w.GetStr(1), (float)w.GetInt(2), w.HasArg(3) ? (FontStyleFlags)(w.GetInt(3) & 0xF) : FontStyleFlags.Regular) ? 1L : 0L;
            case BuiltinMethodKind.GGETFONTSIZE:
                return engine.Graphics.GetFontSize((int)w.GetInt(0));
            case BuiltinMethodKind.GGETFONTSTYLE:
                return (long)engine.Graphics.GetFontStyle((int)w.GetInt(0));
            case BuiltinMethodKind.GDASHSTYLE:
                return engine.Graphics.SetDashStyle((int)w.GetInt(0), (GraphicsDashStyle)w.GetInt(1), (GraphicsDashCap)w.GetInt(2)) ? 1L : 0L;
            case BuiltinMethodKind.GDRAWLINE:
                return engine.Graphics.DrawLine((int)w.GetInt(0), (int)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3), (int)w.GetInt(4)) ? 1L : 0L;
            case BuiltinMethodKind.GDRAWTEXT:
                return engine.Graphics.DrawText((int)w.GetInt(0), w.GetStr(1), w.HasArg(2) ? (int)w.GetInt(2) : 0, w.HasArg(3) ? (int)w.GetInt(3) : 0) ? 1L : 0L;
            case BuiltinMethodKind.GDRAWG:
            {
                long[,]? cm = null;
                if (w.HasArg(10) && w.GetTag(10) == CallArgTag.ByRef)
                {
                    ReadOnlySpan<long> flat = w.ResolveRefIntSpan(10);
                    if (flat.Length >= 25)
                    {
                        cm = new long[5, 5];
                        for (int r = 0; r < 5; r++)
                        {
                            for (int c = 0; c < 5; c++)
                            {
                                cm[r, c] = flat[(r * 5) + c];
                            }
                        }
                    }
                }

                return engine.Graphics.DrawGraphics(
                    (int)w.GetInt(0),
                    (int)w.GetInt(1),
                    (int)w.GetInt(2),
                    (int)w.GetInt(3),
                    (int)w.GetInt(4),
                    (int)w.GetInt(5),
                    (int)w.GetInt(6),
                    (int)w.GetInt(7),
                    (int)w.GetInt(8),
                    (int)w.GetInt(9),
                    cm) ? 1L : 0L;
            }
            case BuiltinMethodKind.GDRAWGWITHMASK:
                return engine.Graphics.DrawGraphicsWithMask((int)w.GetInt(0), (int)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3), (int)w.GetInt(4)) ? 1L : 0L;
            case BuiltinMethodKind.GDRAWGWITHROTATE:
                return engine.Graphics.DrawGraphicsWithRotate((int)w.GetInt(0), (int)w.GetInt(1), (float)w.GetInt(2), w.HasArg(3) ? (float)w.GetInt(3) : 0f, w.HasArg(4) ? (float)w.GetInt(4) : 0f) ? 1L : 0L;
            case BuiltinMethodKind.GROTATE:
                return engine.Graphics.Rotate((int)w.GetInt(0), (float)w.GetInt(1), w.HasArg(2) ? (float)w.GetInt(2) : 0f, w.HasArg(3) ? (float)w.GetInt(3) : 0f) ? 1L : 0L;
            case BuiltinMethodKind.GDRAWSPRITE:
                return engine.Graphics.DrawSprite(
                    (int)w.GetInt(0),
                    engine.Sprites,
                    w.GetStr(1),
                    w.HasArg(2) ? (int)w.GetInt(2) : 0,
                    w.HasArg(3) ? (int)w.GetInt(3) : 0,
                    w.HasArg(4) ? (int)w.GetInt(4) : null,
                    w.HasArg(5) ? (int)w.GetInt(5) : null) ? 1L : 0L;
            case BuiltinMethodKind.GSAVE:
                return engine.Graphics.Save((int)w.GetInt(0), (int)w.GetInt(1)) ? 1L : 0L;
            case BuiltinMethodKind.GLOAD:
                return engine.Graphics.Load((int)w.GetInt(0), (int)w.GetInt(1)) ? 1L : 0L;
            case BuiltinMethodKind.GSETCOLOR:
                return engine.Graphics.SetColor((int)w.GetInt(0), (uint)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3)) ? 1L : 0L;
            case BuiltinMethodKind.GGETCOLOR:
                return engine.Graphics.IsCreated((int)w.GetInt(0))
                    ? engine.Graphics.GetColor((int)w.GetInt(0), (int)w.GetInt(1), (int)w.GetInt(2))
                    : -1L;
            case BuiltinMethodKind.SPRITECREATE:
                return engine.Sprites.CreateSprite(
                    w.GetStr(0),
                    (int)w.GetInt(1),
                    engine.Graphics,
                    w.HasArg(2) ? (int)w.GetInt(2) : 0,
                    w.HasArg(3) ? (int)w.GetInt(3) : 0,
                    w.HasArg(4) ? (int)w.GetInt(4) : null,
                    w.HasArg(5) ? (int)w.GetInt(5) : null,
                    w.HasArg(6) ? (int)w.GetInt(6) : 0,
                    w.HasArg(7) ? (int)w.GetInt(7) : 0) ? 1L : 0L;
            case BuiltinMethodKind.SPRITEANIMECREATE:
                return engine.Sprites.CreateAnimatedSprite(w.GetStr(0), (int)w.GetInt(1), (int)w.GetInt(2)) ? 1L : 0L;
            case BuiltinMethodKind.SPRITEANIMEADDFRAME:
                return engine.Sprites.AddAnimatedFrame(
                    w.GetStr(0),
                    (int)w.GetInt(1),
                    (int)w.GetInt(2),
                    (int)w.GetInt(3),
                    (int)w.GetInt(4),
                    (int)w.GetInt(5),
                    w.HasArg(6) ? (int)w.GetInt(6) : 0,
                    w.HasArg(7) ? (int)w.GetInt(7) : 0,
                    w.HasArg(8) ? (int)w.GetInt(8) : 100) ? 1L : 0L;
            case BuiltinMethodKind.SPRITEWIDTH:
                return engine.Sprites.GetWidth(w.GetStr(0));
            case BuiltinMethodKind.SPRITEHEIGHT:
                return engine.Sprites.GetHeight(w.GetStr(0));
            case BuiltinMethodKind.SPRITEPOSX:
                return engine.Sprites.GetPosX(w.GetStr(0));
            case BuiltinMethodKind.SPRITEPOSY:
                return engine.Sprites.GetPosY(w.GetStr(0));
            case BuiltinMethodKind.SPRITESETPOS:
                return engine.Sprites.SetPos(w.GetStr(0), (int)w.GetInt(1), (int)w.GetInt(2)) ? 1L : 0L;
            case BuiltinMethodKind.SPRITEMOVE:
                return engine.Sprites.Move(w.GetStr(0), (int)w.GetInt(1), (int)w.GetInt(2)) ? 1L : 0L;
            case BuiltinMethodKind.SPRITECREATED:
                return engine.Sprites.Exists(w.GetStr(0)) ? 1L : 0L;
            case BuiltinMethodKind.SPRITEDISPOSE:
                return engine.Sprites.DisposeSprite(w.GetStr(0)) ? 1L : 0L;
            case BuiltinMethodKind.SPRITEDISPOSEALL:
            {
                int before = engine.Sprites.Count;
                engine.Sprites.DisposeAll(onlyDynamic: true);
                return Math.Max(0, before - engine.Sprites.Count);
            }
            case BuiltinMethodKind.CBGCLEAR:
                engine.ConsoleBuffer.CbgClear();
                return 1L;
            case BuiltinMethodKind.CBGCLEARBUTTON:
                engine.ConsoleBuffer.CbgClearButtons();
                return 1L;
            case BuiltinMethodKind.CBGREMOVERANGE:
                engine.ConsoleBuffer.CbgRemoveRange((int)w.GetInt(0), (int)w.GetInt(1));
                return 1L;
            case BuiltinMethodKind.CBGSETG:
                engine.ConsoleBuffer.CbgSetGraphics((int)w.GetInt(0), (int)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3));
                return 1L;
            case BuiltinMethodKind.CBGSETSPRITE:
                engine.ConsoleBuffer.CbgSetSprite(w.GetStr(0), (int)w.GetInt(1), (int)w.GetInt(2), (int)w.GetInt(3));
                return 1L;
            case BuiltinMethodKind.CBGSETBUTTONSPRITE:
                engine.ConsoleBuffer.CbgSetButtonSprite(
                    (int)w.GetInt(0),
                    w.GetStr(1),
                    w.GetStr(2),
                    (int)w.GetInt(3),
                    (int)w.GetInt(4),
                    (int)w.GetInt(5),
                    w.HasArg(6) ? w.GetStr(6) : null);
                return 1L;
            case BuiltinMethodKind.CBGSETBMAPG:
                engine.ConsoleBuffer.CbgSetButtonMap((int)w.GetInt(0));
                return 1L;
            case BuiltinMethodKind.CBGREMOVEBMAP:
                engine.ConsoleBuffer.CbgRemoveButtonMap();
                return 1L;

            // DataTable (20 Int methods)
            case BuiltinMethodKind.DT_CREATE: return EvalDataTableInt(engine, DataTableSubOp.Create, w);
            case BuiltinMethodKind.DT_EXIST: return EvalDataTableInt(engine, DataTableSubOp.Exist, w);
            case BuiltinMethodKind.DT_RELEASE: return EvalDataTableInt(engine, DataTableSubOp.Release, w);
            case BuiltinMethodKind.DT_NOCASE: return EvalDataTableInt(engine, DataTableSubOp.NoCase, w);
            case BuiltinMethodKind.DT_CLEAR: return EvalDataTableInt(engine, DataTableSubOp.Clear, w);
            case BuiltinMethodKind.DT_COLUMN_ADD: return EvalDataTableInt(engine, DataTableSubOp.ColumnAdd, w);
            case BuiltinMethodKind.DT_COLUMN_NAMES: return EvalDataTableInt(engine, DataTableSubOp.ColumnNames, w);
            case BuiltinMethodKind.DT_COLUMN_EXIST: return EvalDataTableInt(engine, DataTableSubOp.ColumnExist, w);
            case BuiltinMethodKind.DT_COLUMN_REMOVE: return EvalDataTableInt(engine, DataTableSubOp.ColumnRemove, w);
            case BuiltinMethodKind.DT_COLUMN_LENGTH: return EvalDataTableInt(engine, DataTableSubOp.ColumnLength, w);
            case BuiltinMethodKind.DT_ROW_ADD: return EvalDataTableInt(engine, DataTableSubOp.RowAdd, w);
            case BuiltinMethodKind.DT_ROW_SET: return EvalDataTableInt(engine, DataTableSubOp.RowSet, w);
            case BuiltinMethodKind.DT_ROW_REMOVE: return EvalDataTableInt(engine, DataTableSubOp.RowRemove, w);
            case BuiltinMethodKind.DT_ROW_LENGTH: return EvalDataTableInt(engine, DataTableSubOp.RowLength, w);
            case BuiltinMethodKind.DT_CELL_GET: return EvalDataTableInt(engine, DataTableSubOp.CellGet, w);
            case BuiltinMethodKind.DT_CELL_ISNULL: return EvalDataTableInt(engine, DataTableSubOp.CellIsNull, w);
            case BuiltinMethodKind.DT_CELL_SET: return EvalDataTableInt(engine, DataTableSubOp.CellSet, w);
            case BuiltinMethodKind.DT_SELECT: return EvalDataTableInt(engine, DataTableSubOp.Select, w);
            case BuiltinMethodKind.DT_FROMXML: return EvalDataTableInt(engine, DataTableSubOp.FromXml, w);

            // XML (17 Int methods)
            case BuiltinMethodKind.XML_DOCUMENT: return EvalXmlInt(engine, XmlSubOp.Document, w);
            case BuiltinMethodKind.XML_RELEASE: return EvalXmlInt(engine, XmlSubOp.Release, w);
            case BuiltinMethodKind.XML_EXIST: return EvalXmlInt(engine, XmlSubOp.Exist, w);
            case BuiltinMethodKind.XML_GET: return EvalXmlInt(engine, XmlSubOp.Get, w);
            case BuiltinMethodKind.XML_GET_BYNAME: return EvalXmlInt(engine, XmlSubOp.GetByName, w);
            case BuiltinMethodKind.XML_SET: return EvalXmlInt(engine, XmlSubOp.Set, w);
            case BuiltinMethodKind.XML_SET_BYNAME: return EvalXmlInt(engine, XmlSubOp.SetByName, w);
            case BuiltinMethodKind.XML_ADDNODE: return EvalXmlInt(engine, XmlSubOp.AddNode, w);
            case BuiltinMethodKind.XML_ADDNODE_BYNAME: return EvalXmlInt(engine, XmlSubOp.AddNodeByName, w);
            case BuiltinMethodKind.XML_REMOVENODE: return EvalXmlInt(engine, XmlSubOp.RemoveNode, w);
            case BuiltinMethodKind.XML_REMOVENODE_BYNAME: return EvalXmlInt(engine, XmlSubOp.RemoveNodeByName, w);
            case BuiltinMethodKind.XML_REPLACE: return EvalXmlInt(engine, XmlSubOp.Replace, w);
            case BuiltinMethodKind.XML_REPLACE_BYNAME: return EvalXmlInt(engine, XmlSubOp.ReplaceByName, w);
            case BuiltinMethodKind.XML_ADDATTRIBUTE: return EvalXmlInt(engine, XmlSubOp.AddAttribute, w);
            case BuiltinMethodKind.XML_ADDATTRIBUTE_BYNAME: return EvalXmlInt(engine, XmlSubOp.AddAttributeByName, w);
            case BuiltinMethodKind.XML_REMOVEATTRIBUTE: return EvalXmlInt(engine, XmlSubOp.RemoveAttribute, w);
            case BuiltinMethodKind.XML_REMOVEATTRIBUTE_BYNAME: return EvalXmlInt(engine, XmlSubOp.RemoveAttributeByName, w);

            // MAP (9 Int methods)
            case BuiltinMethodKind.MAP_CREATE: return EvalMapInt(engine, MapSubOp.Create, w);
            case BuiltinMethodKind.MAP_EXIST: return EvalMapInt(engine, MapSubOp.Exist, w);
            case BuiltinMethodKind.MAP_RELEASE: return EvalMapInt(engine, MapSubOp.Release, w);
            case BuiltinMethodKind.MAP_CLEAR: return EvalMapInt(engine, MapSubOp.Clear, w);
            case BuiltinMethodKind.MAP_SIZE: return EvalMapInt(engine, MapSubOp.Size, w);
            case BuiltinMethodKind.MAP_HAS: return EvalMapInt(engine, MapSubOp.Has, w);
            case BuiltinMethodKind.MAP_SET: return EvalMapInt(engine, MapSubOp.Set, w);
            case BuiltinMethodKind.MAP_REMOVE: return EvalMapInt(engine, MapSubOp.Remove, w);
            case BuiltinMethodKind.MAP_FROMXML: return EvalMapInt(engine, MapSubOp.FromXml, w);

            default:
                return 0L;
        }
    }

    // -------------------------------------------------------------------------
    // BuiltinCallStr (Built-in Expression Methods Returning String)
    // -------------------------------------------------------------------------

    public static string ExecuteBuiltinMethodStr(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var kind = (BuiltinMethodKind)insn.Immediate;
        var w = new CallWindow(engine, frame, insn);
        VariableStore vars = engine.RequireVariableStore();

        switch (kind)
        {
            case BuiltinMethodKind.RepeatString:
            {
                string unit = w.GetStr(0);
                long times = w.GetInt(1);
                if (times <= 0 || string.IsNullOrEmpty(unit)) return string.Empty;
                if (times == 1) return unit;
                var sb = new StringBuilder(checked(unit.Length * (int)times));
                for (int i = 0; i < times; i++) sb.Append(unit);
                return sb.ToString();
            }

            case BuiltinMethodKind.SUBSTRING:
                return EastAsianStringOps.SubstringS(
                    w.GetStr(0),
                    w.GetInt(1, 0L),
                    w.HasArg(2) ? w.GetInt(2, -1L) : -1L,
                    engine.Options.LanguageCodePage);

            case BuiltinMethodKind.SUBSTRINGU:
                return EastAsianStringOps.SubstringU(
                    w.GetStr(0),
                    w.GetInt(1, 0L),
                    w.HasArg(2) ? w.GetInt(2, -1L) : -1L);

            case BuiltinMethodKind.CHARATU:
                return EastAsianStringOps.CharAtU(w.GetStr(0), w.GetInt(1));

            case BuiltinMethodKind.TOUPPER:
                return EastAsianStringOps.ToUpper(w.GetStr(0));

            case BuiltinMethodKind.TOLOWER:
                return EastAsianStringOps.ToLower(w.GetStr(0));

            case BuiltinMethodKind.TOHALF:
                return EastAsianStringOps.ToHalf(w.GetStr(0));

            case BuiltinMethodKind.TOFULL:
                return EastAsianStringOps.ToFull(w.GetStr(0));

            case BuiltinMethodKind.REPLACE:
            {
                int mode = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                if (mode == 1 && w.GetTag(2) == CallArgTag.ByRef)
                {
                    return EastAsianStringOps.Replace(w.GetStr(0), w.GetStr(1), string.Empty, 1, vars.Regexes, w.ResolveRefStrSpan(2));
                }

                return EastAsianStringOps.Replace(w.GetStr(0), w.GetStr(1), w.GetStr(2), mode, vars.Regexes);
            }

            case BuiltinMethodKind.ESCAPE:
                return EastAsianStringOps.Escape(w.GetStr(0));

            case BuiltinMethodKind.UNICODE:
                return EastAsianStringOps.Unicode(w.GetInt(0));

            case BuiltinMethodKind.CONVERT:
                return EastAsianStringOps.ConvertRadix(w.GetInt(0), w.GetInt(1));

            case BuiltinMethodKind.STRJOIN:
            {
                string delim = w.HasArg(1) ? w.GetStr(1, ",") : ",";
                int start = w.HasArg(2) ? (int)w.GetInt(2, 0L) : 0;
                int count = w.HasArg(3) ? (int)w.GetInt(3, -1L) : -1;
                return w.ResolveRefValueKind(0) == VariableValueKind.Str
                    ? EastAsianStringOps.StrJoin(w.ResolveRefStrSpan(0), delim, start, count)
                    : EastAsianStringOps.StrJoinInt(w.ResolveRefIntSpan(0), delim, start, count);
            }

            case BuiltinMethodKind.BARSTR:
                return EastAsianStringOps.BarStr(
                    w.GetInt(0),
                    w.GetInt(1),
                    w.GetInt(2),
                    vars.ReplaceConfig.BarChar1,
                    vars.ReplaceConfig.BarChar2);

            case BuiltinMethodKind.MONEYSTR:
                return EastAsianStringOps.MoneyStr(
                    w.GetInt(0),
                    w.HasArg(1) ? w.GetStr(1) : null,
                    vars.ReplaceConfig.MoneyLabel,
                    vars.ReplaceConfig.MoneyFirst);

            case BuiltinMethodKind.GETTIMES:
                return DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

            case BuiltinMethodKind.GETFONT:
                return engine.CurrentFontName;

            case BuiltinMethodKind.CURRENTALIGN:
                return engine.CurrentAlignmentName;

            case BuiltinMethodKind.GETDOINGFUNCTION:
                return frame.FunctionName;

            case BuiltinMethodKind.ERDNAME:
            {
                var (slotId, _, _) = w.GetByRef(0);
                VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
                return vars.ResolveErdName(slot.Name, w.GetInt(1), w.HasArg(2) ? (int)w.GetInt(2, 1) : 1);
            }

            case BuiltinMethodKind.CSVNAME:
            case BuiltinMethodKind.CSVCALLNAME:
            case BuiltinMethodKind.CSVNICKNAME:
            case BuiltinMethodKind.CSVMASTERNAME:
            case BuiltinMethodKind.CSVCSTR:
                return EvalCsvCharaStr(vars.Characters, kind, w.GetInt(0), w.HasArg(1) ? (int)w.GetInt(1) : 0, w.HasArg(2) && w.GetInt(2) != 0);

            case BuiltinMethodKind.GGETFONT:
                return engine.Graphics.GetFont((int)w.GetInt(0));

            case BuiltinMethodKind.HTML_ESCAPE:
                return HtmlPrintEngine.Escape(w.GetStr(0));

            case BuiltinMethodKind.HTML_TOPLAINTEXT:
                return HtmlPrintEngine.ToPlainText(w.GetStr(0));

            case BuiltinMethodKind.DT_CELL_GETS:
                return EvalDataTableCellGets(engine, w);

            case BuiltinMethodKind.DT_TOXML:
                return EvalDataTableToXml(engine, w);

            case BuiltinMethodKind.XML_TOSTR:
                return vars.Xmls.ToStr(w.GetAnyAsKeyString(0));

            case BuiltinMethodKind.MAP_GET:
                return EvalMapStr(engine, MapSubOp.Get, w);

            case BuiltinMethodKind.MAP_GETKEYS:
                return EvalMapStr(engine, MapSubOp.GetKeys, w);

            case BuiltinMethodKind.MAP_TOXML:
                return EvalMapStr(engine, MapSubOp.ToXml, w);

            default:
                return string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    // BuiltinInstruction (Statement-Form Instructions)
    // Returns non-null EngineStepStatus ONLY when suspending, halting, or reloading frame!
    // -------------------------------------------------------------------------

    public static EngineStepStatus? ExecuteBuiltinInstruction(EmueraEngine engine, CallFrame frame, Instruction insn)
    {
        var kind = (BuiltinInstructionKind)insn.Immediate;
        var w = new CallWindow(engine, frame, insn);
        VariableStore vars = engine.RequireVariableStore();
        CharacterStore chars = vars.Characters;

        switch (kind)
        {
            // Character Roster Instructions
            case BuiltinInstructionKind.ADDCHARA:
                for (int i = 0; i < Math.Max(1, w.Count); i++)
                {
                    chars.AddCharacter(w.GetInt(i));
                }

                return null;

            case BuiltinInstructionKind.ADDSPCHARA:
                for (int i = 0; i < Math.Max(1, w.Count); i++)
                {
                    chars.AddSpCharacter(w.GetInt(i), enforceCompatiSwitch: true);
                }

                return null;

            case BuiltinInstructionKind.ADDDEFCHARA:
                chars.AddDefaultCharacters(vars.GameBase.DefaultChara);
                return null;

            case BuiltinInstructionKind.ADDVOIDCHARA:
                chars.AddVoidCharacter();
                return null;

            case BuiltinInstructionKind.DELCHARA:
            {
                Span<long> ids = w.Count <= 16 ? stackalloc long[w.Count] : new long[w.Count];
                for (int i = 0; i < w.Count; i++) ids[i] = w.GetInt(i);
                chars.DeleteCharacters(ids);
                return null;
            }

            case BuiltinInstructionKind.DELALLCHARA:
                chars.ClearCharacters();
                return null;

            case BuiltinInstructionKind.SWAPCHARA:
                chars.SwapCharacters(checked((int)w.GetInt(0)), checked((int)w.GetInt(1)));
                return null;

            case BuiltinInstructionKind.COPYCHARA:
                chars.CopyCharacter(checked((int)w.GetInt(0)), checked((int)w.GetInt(1)));
                return null;

            case BuiltinInstructionKind.ADDCOPYCHARA:
                for (int i = 0; i < w.Count; i++)
                {
                    chars.AddCopyCharacter(checked((int)w.GetInt(i)));
                }

                return null;

            case BuiltinInstructionKind.PICKUPCHARA:
            {
                Span<long> keep = w.Count <= 32 ? stackalloc long[w.Count] : new long[w.Count];
                for (int i = 0; i < w.Count; i++) keep[i] = w.GetInt(i);
                chars.PickUpCharacters(keep);
                return null;
            }

            case BuiltinInstructionKind.SORTCHARA:
            {
                int slotId = -1;
                long fixedSubValue = -1;
                bool ascending = true;
                if (w.Count >= 1 && w.GetTag(0) == CallArgTag.ByRef)
                {
                    var (sId, _, fixedSub) = w.GetByRef(0);
                    slotId = sId;
                    fixedSubValue = fixedSub;
                    ascending = !w.HasArg(1) || w.GetInt(1, 1L) >= 0;
                }
                else if (w.Count >= 1)
                {
                    ascending = w.GetInt(0, 1L) >= 0;
                }

                if (fixedSubValue >= 0)
                {
                    Span<long> oneSub = [fixedSubValue];
                    chars.SortCharacters(slotId, oneSub, ascending, fixMaster: true);
                }
                else
                {
                    chars.SortCharacters(slotId, default, ascending, fixMaster: true);
                }

                return null;
            }

            case BuiltinInstructionKind.RESET_STAIN:
                chars.ResetStain(checked((int)w.GetInt(0)));
                return null;

            // Array & Bitwise Instructions
            case BuiltinInstructionKind.VARSET:
            {
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    Span<string> dst = w.ResolveRefStrSpan(0);
                    string fill = w.HasArg(1) ? w.GetStr(1) : string.Empty;
                    int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                    int end = w.HasArg(3) ? (int)w.GetInt(3) : dst.Length;
                    SpanBuiltins.VarSet(dst, fill, start, end);
                }
                else
                {
                    Span<long> dst = w.ResolveRefIntSpan(0);
                    long fill = w.HasArg(1) ? w.GetInt(1) : 0L;
                    int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                    int end = w.HasArg(3) ? (int)w.GetInt(3) : dst.Length;
                    SpanBuiltins.VarSet(dst, fill, start, end);
                }

                return null;
            }

            case BuiltinInstructionKind.CVARSET:
            {
                var (slotId, _, fixedSub) = w.GetByRef(0);
                long subIdx = w.HasArg(1) ? w.GetInt(1) : (fixedSub >= 0 ? fixedSub : 0L);
                int start = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                int end = w.HasArg(4) ? (int)w.GetInt(4) : chars.Count;
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    chars.CVarSetStr(slotId, [subIdx], w.HasArg(2) ? w.GetStr(2) : string.Empty, start, end);
                }
                else
                {
                    chars.CVarSetInt(slotId, [subIdx], w.HasArg(2) ? w.GetInt(2) : 0L, start, end);
                }

                return null;
            }

            case BuiltinInstructionKind.ARRAYSHIFT:
            {
                int shift = (int)w.GetInt(1);
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    Span<string> dst = w.ResolveRefStrSpan(0);
                    string def = w.GetStr(2);
                    int start = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                    int num = w.HasArg(4) ? (int)w.GetInt(4) : -1;
                    SpanBuiltins.ArrayShift(dst, shift, def, start, num);
                }
                else
                {
                    Span<long> dst = w.ResolveRefIntSpan(0);
                    long def = w.GetInt(2);
                    int start = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                    int num = w.HasArg(4) ? (int)w.GetInt(4) : -1;
                    SpanBuiltins.ArrayShift(dst, shift, def, start, num);
                }

                return null;
            }

            case BuiltinInstructionKind.ARRAYREMOVE:
            {
                int start = (int)w.GetInt(1);
                int count = (int)w.GetInt(2);
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    SpanBuiltins.ArrayRemove(w.ResolveRefStrSpan(0), string.Empty, start, count);
                }
                else
                {
                    SpanBuiltins.ArrayRemove(w.ResolveRefIntSpan(0), 0L, start, count);
                }

                return null;
            }

            case BuiltinInstructionKind.ARRAYSORT:
            {
                bool asc = !w.HasArg(1) || w.GetInt(1, 1L) >= 0;
                int start = w.HasArg(2) ? (int)w.GetInt(2) : 0;
                int count = w.HasArg(3) ? (int)w.GetInt(3) : -1;
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    SpanBuiltins.ArraySort(w.ResolveRefStrSpan(0), asc, start, count);
                }
                else
                {
                    SpanBuiltins.ArraySort(w.ResolveRefIntSpan(0), asc, start, count);
                }

                return null;
            }

            case BuiltinInstructionKind.ARRAYCOPY:
            {
                string srcName = w.GetStr(0);
                string dstName = w.GetStr(1);
                CopyNamedArray(engine, frame, srcName, dstName);
                return null;
            }

            case BuiltinInstructionKind.SPLIT:
            {
                string src = w.GetStr(0);
                string delim = w.GetStr(1);
                Span<string> target = w.Count >= 3 && w.GetTag(2) == CallArgTag.ByRef
                    ? w.ResolveRefStrSpan(2)
                    : vars.ResultsSpan;
                int splitCount = EastAsianStringOps.Split(src, delim, target);
                if (w.Count >= 4 && w.GetTag(3) == CallArgTag.ByRef)
                {
                    w.WriteRefScalarInt(3, splitCount);
                }
                else
                {
                    vars.Result = splitCount;
                }

                return null;
            }

            case BuiltinInstructionKind.SETBIT:
            case BuiltinInstructionKind.CLEARBIT:
            case BuiltinInstructionKind.INVERTBIT:
            {
                Span<long> dst = w.ResolveRefIntSpan(0);
                var (_, _, fixedIdx) = w.GetByRef(0);
                int elemIdx = fixedIdx >= 0 && fixedIdx < dst.Length ? fixedIdx : 0;
                for (int i = 1; i < w.Count; i++)
                {
                    long bit = w.GetInt(i);
                    dst[elemIdx] = kind switch
                    {
                        BuiltinInstructionKind.SETBIT => SpanBuiltins.SetBit(dst[elemIdx], bit),
                        BuiltinInstructionKind.CLEARBIT => SpanBuiltins.ClearBit(dst[elemIdx], bit),
                        _ => SpanBuiltins.InvertBit(dst[elemIdx], bit),
                    };
                }

                return null;
            }

            case BuiltinInstructionKind.SWAP:
            {
                if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
                {
                    string a = w.ReadRefScalarStr(0);
                    string b = w.ReadRefScalarStr(1);
                    w.WriteRefScalarStr(0, b);
                    w.WriteRefScalarStr(1, a);
                }
                else
                {
                    Span<long> s0 = w.ResolveRefIntSpan(0);
                    Span<long> s1 = w.ResolveRefIntSpan(1);
                    int i0 = Math.Max(0, w.GetByRef(0).FixedIndex0);
                    int i1 = Math.Max(0, w.GetByRef(1).FixedIndex0);
                    (s0[i0], s1[i1]) = (s1[i1], s0[i0]);
                }

                return null;
            }

            case BuiltinInstructionKind.POWER:
                w.WriteRefScalarInt(0, SpanBuiltins.Power(w.GetInt(1), w.GetInt(2)));
                return null;

            // RNG & Data Reset / Save / Load
            case BuiltinInstructionKind.RANDOMIZE:
                vars.Random.Randomize(w.HasArg(0) ? w.GetInt(0) : Environment.TickCount64);
                return null;

            case BuiltinInstructionKind.DUMPRAND:
                vars.DumpRandToSlot();
                return null;

            case BuiltinInstructionKind.INITRAND:
                vars.InitRandFromSlot();
                return null;

            case BuiltinInstructionKind.RESETDATA:
                vars.ResetNonGlobalVariables();
                return null;

            case BuiltinInstructionKind.RESETGLOBAL:
                vars.ResetGlobalVariables();
                return null;

            case BuiltinInstructionKind.PUTFORM:
                vars.SaveDataText += w.GetStr(0);
                return null;

            case BuiltinInstructionKind.SAVEDATA:
                engine.SaveToSlot(checked((int)w.GetInt(0)), w.GetStr(1));
                return null;

            case BuiltinInstructionKind.LOADDATA:
                engine.SystemFlow.ExecuteLoadData(checked((int)w.GetInt(0)));
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.DELDATA:
                engine.DeleteSaveSlot(checked((int)w.GetInt(0)));
                return null;

            case BuiltinInstructionKind.SAVEGAME:
                engine.SystemFlow.EnterSaveOrLoadDialog(isSave: true, fromOpening: false);
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.LOADGAME:
                engine.SystemFlow.EnterSaveOrLoadDialog(isSave: false, fromOpening: false);
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.SAVEGLOBAL:
                engine.SaveGlobal();
                return null;

            case BuiltinInstructionKind.LOADGLOBAL:
                vars.Result = engine.LoadGlobal() ? 1L : 0L;
                return null;

            // Control Flow & System Flow
            case BuiltinInstructionKind.RESTART:
                frame.ProgramCounter = frame.EntryPc;
                return null;

            case BuiltinInstructionKind.THROW:
                throw new InvalidOperationException(w.HasArg(0) ? w.GetStr(0) : "Script THROW executed.");

            case BuiltinInstructionKind.ASSERT:
                if (engine.Options.UseDebugCommand && w.GetInt(0) == 0L)
                {
                    throw new InvalidOperationException("Script ASSERT failed.");
                }

                return null;

            case BuiltinInstructionKind.BEGIN:
                engine.SystemFlow.BeginPhaseByName(w.GetStr(0), isForceBegin: false);
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.FORCE_BEGIN:
                engine.SystemFlow.BeginPhaseByName(w.GetStr(0), isForceBegin: true);
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.DOTRAIN:
                engine.SystemFlow.StartDoTrain(w.GetInt(0));
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.CALLTRAIN:
                engine.SystemFlow.StartCallTrain(w.GetInt(0));
                return EngineStepStatus.Running;

            case BuiltinInstructionKind.STOPCALLTRAIN:
                engine.SystemFlow.StopCallTrain();
                return null;

            case BuiltinInstructionKind.QUIT:
            case BuiltinInstructionKind.FORCE_QUIT:
                engine.HaltFromSystemQuit(restartRequested: false);
                return EngineStepStatus.Completed;

            case BuiltinInstructionKind.QUIT_AND_RESTART:
            case BuiltinInstructionKind.FORCE_QUIT_AND_RESTART:
                engine.HaltFromSystemQuit(restartRequested: true);
                return EngineStepStatus.Completed;

            // Console State & Statement Forms
            case BuiltinInstructionKind.SKIPDISP:
                engine.IsSkipDisplay = w.GetInt(0) != 0L;
                return null;

            case BuiltinInstructionKind.REDRAW:
                engine.CurrentRedrawMode = (int)w.GetInt(0, 1L);
                return null;

            case BuiltinInstructionKind.BREAKBUTTON:
                engine.AdvanceButtonGeneration();
                return null;

            case BuiltinInstructionKind.STRLEN:
            case BuiltinInstructionKind.STRLENFORM:
                vars.Result = EastAsianStringOps.GetStrlenS(w.GetStr(0), engine.Options.LanguageCodePage);
                return null;

            case BuiltinInstructionKind.STRLENU:
            case BuiltinInstructionKind.STRLENFORMU:
                vars.Result = EastAsianStringOps.GetStrlenSU(w.GetStr(0));
                return null;

            case BuiltinInstructionKind.GETTIME:
            {
                DateTime now = DateTime.Now;
                long packed = now.Year;
                packed = (packed * 100) + now.Month;
                packed = (packed * 100) + now.Day;
                packed = (packed * 100) + now.Hour;
                packed = (packed * 100) + now.Minute;
                packed = (packed * 100) + now.Second;
                packed = (packed * 1000) + now.Millisecond;
                vars.Result = packed;
                vars.Results = now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                return null;
            }

            case BuiltinInstructionKind.ENCODETOUNI:
            {
                string s = w.GetStr(0);
                Span<long> res = vars.ResultSpan;
                res[0] = s.Length;
                int limit = Math.Min(s.Length, res.Length - 1);
                for (int i = 0; i < limit; i++)
                {
                    res[i + 1] = s[i];
                }

                return null;
            }

            case BuiltinInstructionKind.DT_COLUMN_OPTIONS:
                EvalDataTableInt(engine, DataTableSubOp.ColumnOptions, w);
                return null;

            // Input & Suspension Statement Forms
            case BuiltinInstructionKind.WAIT:
            case BuiltinInstructionKind.WAITANYKEY:
            case BuiltinInstructionKind.FORCEWAIT:
                return (!engine.IsSkipDisplay || kind == BuiltinInstructionKind.FORCEWAIT) && engine.IssueAnyKeyWait()
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.TWAIT:
                return engine.IssueTimedWait(w.GetInt(0), forceNoSkip: w.GetInt(1) != 0)
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.AWAIT:
                engine.LastAwaitMilliseconds = Math.Max(1L, w.GetInt(0, 1L));
                engine.EmitSnapshotInternal();
                return EngineStepStatus.Yielded;

            case BuiltinInstructionKind.INPUT:
                return engine.IssueScriptInput(InputRequestKind.IntValue, WaitInputFlags.None, hasDef: w.HasArg(0), defInt: w.GetInt(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.INPUTS:
                return engine.IssueScriptInput(InputRequestKind.StringValue, WaitInputFlags.None, hasDef: w.HasArg(0), defStr: w.GetStr(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.ONEINPUT:
                return engine.IssueScriptInput(InputRequestKind.OneInt, WaitInputFlags.None, hasDef: w.HasArg(0), defInt: w.GetInt(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.ONEINPUTS:
                return engine.IssueScriptInput(InputRequestKind.OneString, WaitInputFlags.None, hasDef: w.HasArg(0), defStr: w.GetStr(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.TINPUT:
                return engine.IssueScriptInput(
                    InputRequestKind.IntValue,
                    WaitInputFlags.HasDefault | WaitInputFlags.HasTimeout,
                    hasDef: true,
                    defInt: w.GetInt(1),
                    timeoutMs: w.GetInt(0),
                    timeoutMsg: w.HasArg(3) ? w.GetStr(3) : vars.ReplaceConfig.TimeupLabel)
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.TINPUTS:
                return engine.IssueScriptInput(
                    InputRequestKind.StringValue,
                    WaitInputFlags.HasDefault | WaitInputFlags.HasTimeout,
                    hasDef: true,
                    defStr: w.GetStr(1),
                    timeoutMs: w.GetInt(0),
                    timeoutMsg: w.HasArg(3) ? w.GetStr(3) : vars.ReplaceConfig.TimeupLabel)
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.TONEINPUT:
                return engine.IssueScriptInput(
                    InputRequestKind.OneInt,
                    WaitInputFlags.HasDefault | WaitInputFlags.HasTimeout,
                    hasDef: true,
                    defInt: w.GetInt(1),
                    timeoutMs: w.GetInt(0),
                    timeoutMsg: w.HasArg(3) ? w.GetStr(3) : vars.ReplaceConfig.TimeupLabel)
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.TONEINPUTS:
                return engine.IssueScriptInput(
                    InputRequestKind.OneString,
                    WaitInputFlags.HasDefault | WaitInputFlags.HasTimeout,
                    hasDef: true,
                    defStr: w.GetStr(1),
                    timeoutMs: w.GetInt(0),
                    timeoutMsg: w.HasArg(3) ? w.GetStr(3) : vars.ReplaceConfig.TimeupLabel)
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.BINPUT:
                return engine.IssueScriptInput(InputRequestKind.IntValue, WaitInputFlags.ButtonInventoryOnly, hasDef: w.HasArg(0), defInt: w.GetInt(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.BINPUTS:
                return engine.IssueScriptInput(InputRequestKind.StringValue, WaitInputFlags.ButtonInventoryOnly, hasDef: w.HasArg(0), defStr: w.GetStr(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.ONEBINPUT:
                return engine.IssueScriptInput(InputRequestKind.OneInt, WaitInputFlags.ButtonInventoryOnly, hasDef: w.HasArg(0), defInt: w.GetInt(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.ONEBINPUTS:
                return engine.IssueScriptInput(InputRequestKind.OneString, WaitInputFlags.ButtonInventoryOnly, hasDef: w.HasArg(0), defStr: w.GetStr(0))
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.INPUTMOUSEKEY:
                return engine.IssueScriptInput(
                    InputRequestKind.MouseClick,
                    WaitInputFlags.PrimitiveMouseKey | (w.HasArg(0) && w.GetInt(0) > 0 ? WaitInputFlags.HasTimeout : WaitInputFlags.None),
                    timeoutMs: w.HasArg(0) && w.GetInt(0) > 0 ? w.GetInt(0) : null)
                    ? EngineStepStatus.WaitingInput
                    : null;

            case BuiltinInstructionKind.INPUTANY:
                return engine.IssueScriptInput(InputRequestKind.StringValue, WaitInputFlags.AnyValue)
                    ? EngineStepStatus.WaitingInput
                    : null;

            default:
                return null;
        }
    }

    // -------------------------------------------------------------------------
    // Shared Subsystem Implementation Helpers
    // -------------------------------------------------------------------------

    private static long EvalDataTableInt(EmueraEngine engine, DataTableSubOp op, CallWindow w)
    {
        VariableStore vars = engine.RequireVariableStore();
        DataTableStore dt = vars.DataTables;

        switch (op)
        {
            case DataTableSubOp.Create: return dt.Create(w.GetStr(0));
            case DataTableSubOp.Exist: return dt.Exists(w.GetStr(0));
            case DataTableSubOp.Release: return dt.Release(w.GetStr(0));
            case DataTableSubOp.Clear: return dt.Clear(w.GetStr(0));
            case DataTableSubOp.NoCase: return dt.SetNoCase(w.GetStr(0), w.GetInt(1));

            case DataTableSubOp.ColumnAdd:
            {
                string table = w.GetStr(0);
                string col = w.GetStr(1);
                bool allowNull = !w.HasArg(3) || w.GetInt(3, 1L) != 0;
                if (!w.HasArg(2)) return dt.AddColumn(table, col, typeof(string), allowNull);
                return w.GetTag(2) == CallArgTag.Str
                    ? dt.AddColumnByTypeName(table, col, w.GetStr(2), allowNull)
                    : dt.AddColumnByCode(table, col, w.GetInt(2), allowNull);
            }

            case DataTableSubOp.ColumnExist: return dt.ColumnExists(w.GetStr(0), w.GetStr(1));
            case DataTableSubOp.ColumnRemove: return dt.RemoveColumn(w.GetStr(0), w.GetStr(1));
            case DataTableSubOp.ColumnLength: return dt.ColumnLength(w.GetStr(0));
            case DataTableSubOp.ColumnNames:
            {
                Span<string> dst = w.Count >= 2 && w.GetTag(1) == CallArgTag.ByRef
                    ? w.ResolveRefStrSpan(1)
                    : vars.ResultsSpan;
                return dt.ColumnNames(w.GetStr(0), dst);
            }

            case DataTableSubOp.ColumnOptions:
            {
                object? defVal = w.GetTag(2) == CallArgTag.Str ? w.GetStr(2) : w.GetInt(2);
                long res = dt.SetColumnDefault(w.GetStr(0), w.GetStr(1), defVal);
                vars.Result = res;
                return res;
            }

            case DataTableSubOp.RowAdd:
            case DataTableSubOp.RowSet:
            {
                bool isSet = op == DataTableSubOp.RowSet;
                int offset = isSet ? 2 : 1;
                string table = w.GetStr(0);
                long rowId = isSet ? w.GetInt(1) : 0L;

                if (w.Count == offset + 3 && w.GetTag(offset) == CallArgTag.ByRef && w.GetTag(offset + 1) == CallArgTag.ByRef)
                {
                    ReadOnlySpan<string> names = w.ResolveRefStrSpan(offset);
                    int cnt = (int)Math.Min(names.Length, w.GetInt(offset + 2));
                    var fields = new (string, object?)[Math.Max(0, cnt)];
                    if (w.ResolveRefValueKind(offset + 1) == VariableValueKind.Str)
                    {
                        ReadOnlySpan<string> vals = w.ResolveRefStrSpan(offset + 1);
                        cnt = Math.Min(cnt, vals.Length);
                        for (int i = 0; i < cnt; i++) fields[i] = (names[i], vals[i]);
                    }
                    else
                    {
                        ReadOnlySpan<long> vals = w.ResolveRefIntSpan(offset + 1);
                        cnt = Math.Min(cnt, vals.Length);
                        for (int i = 0; i < cnt; i++) fields[i] = (names[i], vals[i]);
                    }

                    return isSet ? dt.SetRow(table, rowId, fields.AsSpan(0, cnt)) : dt.AddRow(table, fields.AsSpan(0, cnt));
                }

                int pairCount = (w.Count - offset) / 2;
                var pairs = pairCount > 0 ? new (string, object?)[pairCount] : [];
                for (int i = 0; i < pairCount; i++)
                {
                    int nameIdx = offset + (i * 2);
                    int valIdx = nameIdx + 1;
                    object? val = w.GetTag(valIdx) switch
                    {
                        CallArgTag.Str => w.GetStr(valIdx),
                        CallArgTag.Int => w.GetInt(valIdx),
                        _ => null,
                    };
                    pairs[i] = (w.GetStr(nameIdx), val);
                }

                return isSet ? dt.SetRow(table, rowId, pairs) : dt.AddRow(table, pairs);
            }

            case DataTableSubOp.RowRemove:
            {
                if (w.Count == 3 && w.GetTag(1) == CallArgTag.ByRef)
                {
                    return dt.RemoveRows(w.GetStr(0), w.ResolveRefIntSpan(1), (int)w.GetInt(2));
                }

                return dt.RemoveRow(w.GetStr(0), w.GetInt(1));
            }

            case DataTableSubOp.RowLength:
                return dt.RowLength(w.GetStr(0));

            case DataTableSubOp.CellGet:
                return dt.GetCellInt(w.GetStr(0), w.GetInt(1), w.GetStr(2), w.HasArg(3) && w.GetInt(3) != 0);

            case DataTableSubOp.CellIsNull:
                return dt.IsCellNull(w.GetStr(0), w.GetInt(1), w.GetStr(2), w.HasArg(3) && w.GetInt(3) != 0);

            case DataTableSubOp.CellSet:
            {
                object? val = !w.HasArg(3)
                    ? null
                    : w.GetTag(3) == CallArgTag.Str ? w.GetStr(3) : w.GetInt(3);
                bool asId = w.HasArg(4) && w.GetInt(4) != 0;
                return dt.SetCell(w.GetStr(0), w.GetInt(1), w.GetStr(2), val, asId);
            }

            case DataTableSubOp.Select:
            {
                string? filter = w.HasArg(1) ? w.GetStr(1) : null;
                string? sort = w.HasArg(2) ? w.GetStr(2) : null;
                if (w.Count >= 4 && w.GetTag(3) == CallArgTag.ByRef)
                {
                    return dt.Select(w.GetStr(0), filter, sort, w.ResolveRefIntSpan(3), writeCountAtIndexZero: false);
                }

                return dt.Select(w.GetStr(0), filter, sort, vars.ResultSpan, writeCountAtIndexZero: true);
            }

            case DataTableSubOp.FromXml:
                return dt.FromXml(w.GetStr(0), w.GetStr(1), w.GetStr(2));

            default:
                return 0L;
        }
    }

    private static string EvalDataTableCellGets(EmueraEngine engine, CallWindow w) =>
        engine.RequireVariableStore().DataTables.GetCellStr(
            w.GetStr(0),
            w.GetInt(1),
            w.GetStr(2),
            w.HasArg(3) && w.GetInt(3) != 0);

    private static string EvalDataTableToXml(EmueraEngine engine, CallWindow w)
    {
        VariableStore vars = engine.RequireVariableStore();
        string dataXml = vars.DataTables.ToXml(w.GetStr(0), out string schemaXml);
        if (w.Count >= 2 && w.GetTag(1) == CallArgTag.ByRef)
        {
            w.WriteRefScalarStr(1, schemaXml);
        }
        else if (vars.ResultsSpan.Length > 1)
        {
            vars.ResultsSpan[1] = schemaXml;
        }

        return dataXml;
    }

    private static long EvalXmlInt(EmueraEngine engine, XmlSubOp op, CallWindow w)
    {
        VariableStore vars = engine.RequireVariableStore();
        XmlStore xmls = vars.Xmls;

        switch (op)
        {
            case XmlSubOp.Document:
                return xmls.CreateDocument(w.GetAnyAsKeyString(0), w.GetStr(1));

            case XmlSubOp.Exist:
                return xmls.Exists(w.GetAnyAsKeyString(0));

            case XmlSubOp.Release:
                return xmls.Release(w.GetAnyAsKeyString(0));

            case XmlSubOp.Get:
            case XmlSubOp.GetByName:
            {
                bool byName = op == XmlSubOp.GetByName;
                bool isDocKey = w.GetTag(0) == CallArgTag.Int || (byName && w.GetTag(0) == CallArgTag.Str);
                string keyOrInline = isDocKey ? w.GetAnyAsKeyString(0) : (w.GetTag(0) == CallArgTag.ByRef ? w.ReadRefScalarStr(0) : w.GetStr(0));
                long style = w.HasArg(3) ? w.GetInt(3) : 0L;

                Span<string> dest = default;
                if (w.Count >= 3)
                {
                    if (w.GetTag(2) == CallArgTag.ByRef)
                    {
                        dest = w.ResolveRefStrSpan(2);
                    }
                    else if (w.GetInt(2) != 0)
                    {
                        dest = vars.ResultsSpan;
                    }
                }

                return xmls.Get(keyOrInline, isDocKey, w.GetStr(1), dest, style);
            }

            case XmlSubOp.Set:
            case XmlSubOp.SetByName:
            {
                bool byName = op == XmlSubOp.SetByName;
                bool isRefXml = w.GetTag(0) == CallArgTag.ByRef;
                bool isDocKey = !isRefXml && (w.GetTag(0) == CallArgTag.Int || byName);
                string keyOrInline = isRefXml ? w.ReadRefScalarStr(0) : w.GetAnyAsKeyString(0);
                bool setAll = w.HasArg(3) && w.GetInt(3) != 0;
                long style = w.HasArg(4) ? w.GetInt(4) : 0L;
                long count = xmls.Set(keyOrInline, isDocKey, w.GetStr(1), w.GetStr(2), out string? mutated, setAll, style);
                if (isRefXml && mutated is not null)
                {
                    w.WriteRefScalarStr(0, mutated);
                }

                return count;
            }

            case XmlSubOp.AddNode:
            case XmlSubOp.AddNodeByName:
            {
                bool byName = op == XmlSubOp.AddNodeByName;
                bool isRefXml = w.GetTag(0) == CallArgTag.ByRef;
                bool isDocKey = !isRefXml && (w.GetTag(0) == CallArgTag.Int || byName);
                string keyOrInline = isRefXml ? w.ReadRefScalarStr(0) : w.GetAnyAsKeyString(0);
                int method = w.HasArg(3) ? (int)w.GetInt(3) : 0;
                bool setAll = w.HasArg(4) && w.GetInt(4) != 0;
                long count = xmls.AddNode(keyOrInline, isDocKey, w.GetStr(1), w.GetStr(2), out string? mutated, method, setAll);
                if (isRefXml && mutated is not null)
                {
                    w.WriteRefScalarStr(0, mutated);
                }

                return count;
            }

            case XmlSubOp.AddAttribute:
            case XmlSubOp.AddAttributeByName:
            {
                bool byName = op == XmlSubOp.AddAttributeByName;
                bool isRefXml = w.GetTag(0) == CallArgTag.ByRef;
                bool isDocKey = !isRefXml && (w.GetTag(0) == CallArgTag.Int || byName);
                string keyOrInline = isRefXml ? w.ReadRefScalarStr(0) : w.GetAnyAsKeyString(0);
                string attrVal = w.HasArg(3) ? w.GetStr(3) : string.Empty;
                int method = w.HasArg(4) ? (int)w.GetInt(4) : 0;
                bool setAll = w.HasArg(5) && w.GetInt(5) != 0;
                long count = xmls.AddAttribute(keyOrInline, isDocKey, w.GetStr(1), w.GetStr(2), attrVal, out string? mutated, method, setAll);
                if (isRefXml && mutated is not null)
                {
                    w.WriteRefScalarStr(0, mutated);
                }

                return count;
            }

            case XmlSubOp.RemoveNode:
            case XmlSubOp.RemoveNodeByName:
            case XmlSubOp.RemoveAttribute:
            case XmlSubOp.RemoveAttributeByName:
            {
                bool byName = op is XmlSubOp.RemoveNodeByName or XmlSubOp.RemoveAttributeByName;
                bool isAttr = op is XmlSubOp.RemoveAttribute or XmlSubOp.RemoveAttributeByName;
                bool isRefXml = w.GetTag(0) == CallArgTag.ByRef;
                bool isDocKey = !isRefXml && (w.GetTag(0) == CallArgTag.Int || byName);
                string keyOrInline = isRefXml ? w.ReadRefScalarStr(0) : w.GetAnyAsKeyString(0);
                bool setAll = w.HasArg(2) && w.GetInt(2) != 0;
                long count = xmls.Remove(keyOrInline, isDocKey, w.GetStr(1), isAttr, out string? mutated, setAll);
                if (isRefXml && mutated is not null)
                {
                    w.WriteRefScalarStr(0, mutated);
                }

                return count;
            }

            case XmlSubOp.Replace:
            case XmlSubOp.ReplaceByName:
            {
                if (w.Count == 2)
                {
                    return xmls.Replace(w.GetAnyAsKeyString(0), isDocumentKey: true, xpath: null, w.GetStr(1), out _);
                }

                bool byName = op == XmlSubOp.ReplaceByName;
                bool isRefXml = w.GetTag(0) == CallArgTag.ByRef;
                bool isDocKey = !isRefXml && (w.GetTag(0) == CallArgTag.Int || byName);
                string keyOrInline = isRefXml ? w.ReadRefScalarStr(0) : w.GetAnyAsKeyString(0);
                bool setAll = w.HasArg(3) && w.GetInt(3) != 0;
                long count = xmls.Replace(keyOrInline, isDocKey, w.GetStr(1), w.GetStr(2), out string? mutated, setAll);
                if (isRefXml && mutated is not null)
                {
                    w.WriteRefScalarStr(0, mutated);
                }

                return count;
            }

            default:
                return 0L;
        }
    }

    private static long EvalMapInt(EmueraEngine engine, MapSubOp op, CallWindow w)
    {
        MapStore maps = engine.RequireVariableStore().Maps;
        return op switch
        {
            MapSubOp.Create => maps.Create(w.GetStr(0)),
            MapSubOp.Exist => maps.Exists(w.GetStr(0)),
            MapSubOp.Release => maps.Release(w.GetStr(0)),
            MapSubOp.Clear => maps.Clear(w.GetStr(0)),
            MapSubOp.Size => maps.Size(w.GetStr(0)),
            MapSubOp.Has => maps.Has(w.GetStr(0), w.GetStr(1)),
            MapSubOp.Set => maps.Set(w.GetStr(0), w.GetStr(1), w.GetStr(2)),
            MapSubOp.Remove => maps.Remove(w.GetStr(0), w.GetStr(1)),
            MapSubOp.FromXml => maps.FromXml(w.GetStr(0), w.GetStr(1)),
            _ => 0L,
        };
    }

    private static string EvalMapStr(EmueraEngine engine, MapSubOp op, CallWindow w)
    {
        VariableStore vars = engine.RequireVariableStore();
        MapStore maps = vars.Maps;

        switch (op)
        {
            case MapSubOp.Get:
                return maps.Get(w.GetStr(0), w.GetStr(1));

            case MapSubOp.ToXml:
                return maps.ToXml(w.GetStr(0));

            case MapSubOp.GetKeys:
            {
                if (w.Count == 1)
                {
                    return maps.GetKeysCsv(w.GetStr(0));
                }

                if (w.Count == 3 && w.GetTag(1) == CallArgTag.ByRef)
                {
                    if (w.GetInt(2) == 0) return string.Empty;
                    vars.Result = maps.GetKeysToSpan(w.GetStr(0), w.ResolveRefStrSpan(1));
                    return string.Empty;
                }

                if (w.GetInt(1) == 0)
                {
                    return string.Empty;
                }

                vars.Result = maps.GetKeysToSpan(w.GetStr(0), vars.ResultsSpan);
                return vars.Results;
            }

            default:
                return string.Empty;
        }
    }

    private static long EvalArrayMultiSort(EmueraEngine engine, CallWindow w)
    {
        if (w.Count < 1 || w.GetTag(0) != CallArgTag.ByRef)
        {
            return 0L;
        }

        int[] perm;
        if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
        {
            if (!SpanBuiltins.TryBuildMultiSortPermutation(w.ResolveRefStrSpan(0), ascending: true, fixedLength: -1, abortOnEmptyWhenUnbounded: false, out perm))
            {
                return 0L;
            }
        }
        else
        {
            perm = SpanBuiltins.BuildMultiSortPermutation(w.ResolveRefIntSpan(0), ascending: true, fixedLength: -1);
        }

        for (int i = 0; i < w.Count; i++)
        {
            if (!ApplyMultiSortToRefArg(engine, w, i, perm))
            {
                return 0L;
            }
        }

        return 1L;
    }

    private static long EvalArrayMultiSortEx(EmueraEngine engine, CallWindow w)
    {
        VariableStore vars = engine.RequireVariableStore();
        bool asc = !w.HasArg(2) || w.GetInt(2, 1L) != 0;
        int fixedLen = w.HasArg(3) ? (int)w.GetInt(3) : -1;
        if (fixedLen == 0)
        {
            return 0L;
        }

        int[] perm;
        if (w.GetTag(0) == CallArgTag.ByRef)
        {
            if (w.ResolveRefValueKind(0) == VariableValueKind.Str)
            {
                if (!SpanBuiltins.TryBuildMultiSortPermutation(w.ResolveRefStrSpan(0), asc, fixedLen, abortOnEmptyWhenUnbounded: true, out perm))
                {
                    return 0L;
                }
            }
            else
            {
                perm = SpanBuiltins.BuildMultiSortPermutation(w.ResolveRefIntSpan(0), asc, fixedLen);
            }
        }
        else
        {
            string keyVarName = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(vars, w.Frame, w.GetStr(0).Trim(), null);
            if (!vars.TryResolveSlot(keyVarName, out int keySlot))
            {
                return 0L;
            }

            VariableSlotStorage ks = vars.GetSlotOrThrow(keySlot);
            if (ks.ValueKind == VariableValueKind.Str)
            {
                if (!SpanBuiltins.TryBuildMultiSortPermutation(vars.GetStrSpan(keySlot), asc, fixedLen, abortOnEmptyWhenUnbounded: true, out perm))
                {
                    return 0L;
                }
            }
            else
            {
                perm = SpanBuiltins.BuildMultiSortPermutation(vars.GetIntSpan(keySlot), asc, fixedLen);
            }
        }

        ReadOnlySpan<string> targetNames = w.ResolveRefStrSpan(1);
        for (int i = 0; i < targetNames.Length; i++)
        {
            string tName = targetNames[i];
            if (string.IsNullOrWhiteSpace(tName))
            {
                break;
            }

            if (!ApplyMultiSortByName(vars, w.Frame, tName.Trim(), perm))
            {
                return 0L;
            }
        }

        return 1L;
    }

    private static bool ApplyMultiSortToRefArg(EmueraEngine engine, CallWindow w, int argIndex, ReadOnlySpan<int> perm)
    {
        var (slotId, origin, _) = w.GetByRef(argIndex);
        if (origin is VariableSlotOrigin.FrameLocal or VariableSlotOrigin.RefPassthrough)
        {
            FrameLocalStorage loc = w.Frame.LocalSlots[slotId];
            int stride = loc.Dim1 * loc.Dim2;
            return loc.ValueKind == VariableValueKind.Int
                ? SpanBuiltins.ApplyFirstDimensionPermutation(loc.GetIntSpan(), loc.Dim0, stride, perm)
                : SpanBuiltins.ApplyFirstDimensionPermutation(loc.GetStrSpan(), loc.Dim0, stride, perm);
        }

        VariableStore vars = engine.RequireVariableStore();
        VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
        int rowStride = slot.Dim1 * slot.Dim2;
        return slot.ValueKind == VariableValueKind.Int
            ? SpanBuiltins.ApplyFirstDimensionPermutation(vars.GetIntSpan(slotId), slot.Dim0, rowStride, perm)
            : SpanBuiltins.ApplyFirstDimensionPermutation(vars.GetStrSpan(slotId), slot.Dim0, rowStride, perm);
    }

    private static bool ApplyMultiSortByName(VariableStore vars, CallFrame frame, string name, ReadOnlySpan<int> perm)
    {
        if (frame.TryGetLocal(name, vars.CompilerOptions.IgnoreCase, out FrameLocalStorage? loc) && loc is not null)
        {
            int stride = loc.Dim1 * loc.Dim2;
            return loc.ValueKind == VariableValueKind.Int
                ? SpanBuiltins.ApplyFirstDimensionPermutation(loc.GetIntSpan(), loc.Dim0, stride, perm)
                : SpanBuiltins.ApplyFirstDimensionPermutation(loc.GetStrSpan(), loc.Dim0, stride, perm);
        }

        string lookup = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(vars, frame, name, null);
        if (!vars.TryResolveSlot(lookup, out int slotId))
        {
            return false;
        }

        VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
        int rowStride = slot.Dim1 * slot.Dim2;
        return slot.ValueKind == VariableValueKind.Int
            ? SpanBuiltins.ApplyFirstDimensionPermutation(vars.GetIntSpan(slotId), slot.Dim0, rowStride, perm)
            : SpanBuiltins.ApplyFirstDimensionPermutation(vars.GetStrSpan(slotId), slot.Dim0, rowStride, perm);
    }

    private static void CopyNamedArray(EmueraEngine engine, CallFrame frame, string srcName, string dstName)
    {
        VariableStore vars = engine.RequireVariableStore();
        bool srcIsLocal = frame.TryGetLocal(srcName, vars.CompilerOptions.IgnoreCase, out FrameLocalStorage? srcLoc) && srcLoc is not null;
        bool dstIsLocal = frame.TryGetLocal(dstName, vars.CompilerOptions.IgnoreCase, out FrameLocalStorage? dstLoc) && dstLoc is not null;

        int srcSlot = -1;
        int dstSlot = -1;
        if (!srcIsLocal)
        {
            string sLookup = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(vars, frame, srcName, null);
            if (!vars.TryResolveSlot(sLookup, out srcSlot))
            {
                throw new KeyNotFoundException($"ARRAYCOPY source variable '{srcName}' not found.");
            }
        }

        if (!dstIsLocal)
        {
            string dLookup = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(vars, frame, dstName, null);
            if (!vars.TryResolveSlot(dLookup, out dstSlot))
            {
                throw new KeyNotFoundException($"ARRAYCOPY destination variable '{dstName}' not found.");
            }
        }

        VariableValueKind srcKind = srcIsLocal ? srcLoc!.ValueKind : vars.GetSlotOrThrow(srcSlot).ValueKind;
        VariableValueKind dstKind = dstIsLocal ? dstLoc!.ValueKind : vars.GetSlotOrThrow(dstSlot).ValueKind;
        if (srcKind != dstKind)
        {
            throw new InvalidOperationException($"ARRAYCOPY type mismatch between '{srcName}' and '{dstName}'.");
        }

        if (srcKind == VariableValueKind.Int)
        {
            ReadOnlySpan<long> srcSpan = srcIsLocal ? srcLoc!.GetIntSpan() : vars.GetIntSpan(srcSlot);
            Span<long> dstSpan = dstIsLocal ? dstLoc!.GetIntSpan() : vars.GetIntSpan(dstSlot);
            SpanBuiltins.ArrayCopy(srcSpan, dstSpan);
        }
        else
        {
            ReadOnlySpan<string> srcSpan = srcIsLocal ? srcLoc!.GetStrSpan() : vars.GetStrSpan(srcSlot);
            Span<string> dstSpan = dstIsLocal ? dstLoc!.GetStrSpan() : vars.GetStrSpan(dstSlot);
            SpanBuiltins.ArrayCopy(srcSpan, dstSpan);
        }
    }

    private static long EvalCsvCharaInt(CharacterStore chars, BuiltinMethodKind kind, long charaNo, int index, bool sp)
    {
        CharacterTemplate? tmpl = chars.TryGetTemplate(charaNo, sp);
        if (tmpl is null)
        {
            return 0L;
        }

        IReadOnlyDictionary<int, long> dict = kind switch
        {
            BuiltinMethodKind.CSVBASE => tmpl.MaxBase.Count > 0 ? tmpl.MaxBase : tmpl.Base,
            BuiltinMethodKind.CSVABL => tmpl.Abl,
            BuiltinMethodKind.CSVMARK => tmpl.Mark,
            BuiltinMethodKind.CSVEXP => tmpl.Exp,
            BuiltinMethodKind.CSVRELATION => tmpl.Relation,
            BuiltinMethodKind.CSVTALENT => tmpl.Talent,
            BuiltinMethodKind.CSVCFLAG => tmpl.CFlag,
            BuiltinMethodKind.CSVEQUIP => tmpl.Equip,
            BuiltinMethodKind.CSVJUEL => tmpl.Juel,
            _ => tmpl.CFlag,
        };

        return dict.TryGetValue(index, out long v) ? v : 0L;
    }

    private static string EvalCsvCharaStr(CharacterStore chars, BuiltinMethodKind kind, long charaNo, int indexOrSp, bool sp)
    {
        bool useSp = kind == BuiltinMethodKind.CSVCSTR ? sp : indexOrSp != 0;
        CharacterTemplate? tmpl = chars.TryGetTemplate(charaNo, useSp);
        if (tmpl is null)
        {
            return string.Empty;
        }

        return kind switch
        {
            BuiltinMethodKind.CSVNAME => tmpl.Name,
            BuiltinMethodKind.CSVCALLNAME => tmpl.CallName,
            BuiltinMethodKind.CSVNICKNAME => tmpl.NickName,
            BuiltinMethodKind.CSVMASTERNAME => tmpl.MasterName,
            BuiltinMethodKind.CSVCSTR => tmpl.CStr.TryGetValue(indexOrSp, out string? s) ? s : string.Empty,
            _ => string.Empty,
        };
    }

    private static long SetResults0AndReturn(VariableStore vars, string text, long code)
    {
        vars.Results = text;
        return code;
    }

    private static long RgbaToRgb24(uint rgba) =>
        (rgba >> 8) & 0xFF_FFFF;
}
