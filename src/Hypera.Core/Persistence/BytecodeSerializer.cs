using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Diagnostics;

namespace Hypera.Persistence;

/// <summary>
/// Binary serializer/deserializer for compiled <see cref="BytecodeModule"/> (.heb, magic "HEBC").
/// </summary>
public static class BytecodeSerializer
{
    public static byte[] Serialize(BytecodeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        using var ms = new MemoryStream();
        Serialize(module, ms);
        return ms.ToArray();
    }

    public static void Serialize(BytecodeModule module, Stream output)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(output);

        BytecodeModuleHeader header = BytecodeModuleHeader.Create(
            instructionCount: (uint)module.Instructions.Length,
            functionCount: (uint)module.Functions.Length,
            intConstantCount: (uint)module.IntConstants.Length,
            stringConstantCount: (uint)module.StringConstants.Length,
            staticVariableCount: (uint)module.StaticVariables.Length,
            flags: module.Header.Flags);

        Span<byte> headerBytes = stackalloc byte[BytecodeModuleHeader.SizeInBytes];
        header.WriteTo(headerBytes);
        output.Write(headerBytes);

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        WriteCompilerOptions(writer, module.CompilerOptions);
        WriteReplaceConfig(writer, module.ReplaceConfig);

        // Flat blittable Instruction[]
        if (!module.Instructions.IsEmpty)
        {
            ReadOnlySpan<Instruction> insns = module.Instructions.AsSpan();
            if (BitConverter.IsLittleEndian)
            {
                writer.Write(MemoryMarshal.AsBytes(insns));
            }
            else
            {
                foreach (Instruction insn in insns)
                {
                    writer.Write((ushort)insn.Op);
                    writer.Write(insn.Aux);
                    writer.Write(insn.A);
                    writer.Write(insn.B);
                    writer.Write(insn.C);
                    writer.Write(insn.D);
                    writer.Write(insn.Immediate);
                }
            }
        }

        // IntConstants
        foreach (long value in module.IntConstants)
        {
            writer.Write(value);
        }

        // StringConstants
        foreach (string str in module.StringConstants)
        {
            WriteUtf8String(writer, str);
        }

        // FunctionDescriptors
        foreach (FunctionDescriptor func in module.Functions)
        {
            WriteFunctionDescriptor(writer, func);
        }

        // EventFunctionGroups
        writer.Write(module.EventGroups.Length);
        foreach (EventFunctionGroup group in module.EventGroups)
        {
            WriteUtf8String(writer, group.EventName);
            writer.Write(group.OrderedHandlers.Length);
            foreach (EventHandlerEntry handler in group.OrderedHandlers)
            {
                writer.Write(handler.FunctionId);
                writer.Write((byte)handler.Flags);
                writer.Write(handler.DefinitionOrder);
            }
        }

        // StaticVariables
        foreach (VariableSlotDescriptor slot in module.StaticVariables)
        {
            WriteVariableSlot(writer, slot);
        }

        // SourceMap
        WriteSourceMap(writer, module.SourceMap);
        writer.Flush();
    }

    public static BytecodeModule Deserialize(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray(), writable: false);
        return Deserialize(ms);
    }

    public static BytecodeModule Deserialize(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        Span<byte> headerBytes = stackalloc byte[BytecodeModuleHeader.SizeInBytes];
        try
        {
            input.ReadExactly(headerBytes);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Truncated .heb bytecode header.", ex);
        }

        if (!BytecodeModuleHeader.TryRead(headerBytes, out BytecodeModuleHeader header))
        {
            throw new InvalidDataException("Invalid .heb bytecode magic or unsupported version.");
        }

        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        CompilerOptions compilerOptions = ReadCompilerOptions(reader);
        ReplaceConfiguration replaceConfig = ReadReplaceConfig(reader);

        // Flat Instruction[]
        int insnCount = checked((int)header.InstructionCount);
        var instructions = ImmutableArray.CreateBuilder<Instruction>(insnCount);
        if (insnCount > 0)
        {
            int byteCount = checked(insnCount * Instruction.SizeInBytes);
            byte[] rawInsns = reader.ReadBytes(byteCount);
            if (rawInsns.Length != byteCount)
            {
                throw new InvalidDataException("Truncated .heb instruction stream.");
            }

            if (BitConverter.IsLittleEndian)
            {
                ReadOnlySpan<Instruction> casted = MemoryMarshal.Cast<byte, Instruction>(rawInsns);
                foreach (Instruction insn in casted)
                {
                    instructions.Add(insn);
                }
            }
            else
            {
                using var insnReader = new BinaryReader(new MemoryStream(rawInsns, writable: false));
                for (int i = 0; i < insnCount; i++)
                {
                    instructions.Add(new Instruction(
                        (OpCode)insnReader.ReadUInt16(),
                        insnReader.ReadUInt16(),
                        insnReader.ReadUInt16(),
                        insnReader.ReadUInt16(),
                        insnReader.ReadUInt16(),
                        insnReader.ReadUInt16(),
                        insnReader.ReadInt32()));
                }
            }
        }

        // IntConstants
        int intConstCount = checked((int)header.IntConstantCount);
        var intConstants = ImmutableArray.CreateBuilder<long>(intConstCount);
        for (int i = 0; i < intConstCount; i++)
        {
            intConstants.Add(reader.ReadInt64());
        }

        // StringConstants
        int strConstCount = checked((int)header.StringConstantCount);
        var strConstants = ImmutableArray.CreateBuilder<string>(strConstCount);
        for (int i = 0; i < strConstCount; i++)
        {
            strConstants.Add(ReadUtf8String(reader));
        }

        // Functions
        int funcCount = checked((int)header.FunctionCount);
        var functions = ImmutableArray.CreateBuilder<FunctionDescriptor>(funcCount);
        for (int i = 0; i < funcCount; i++)
        {
            functions.Add(ReadFunctionDescriptor(reader));
        }

        // EventGroups
        int eventGroupCount = reader.ReadInt32();
        if (eventGroupCount < 0)
        {
            throw new InvalidDataException("Invalid negative event group count in .heb.");
        }

        var eventGroups = ImmutableArray.CreateBuilder<EventFunctionGroup>(eventGroupCount);
        for (int i = 0; i < eventGroupCount; i++)
        {
            string eventName = ReadUtf8String(reader);
            int handlerCount = reader.ReadInt32();
            var handlers = ImmutableArray.CreateBuilder<EventHandlerEntry>(handlerCount);
            for (int h = 0; h < handlerCount; h++)
            {
                int funcId = reader.ReadInt32();
                var flags = (EventDirectiveFlags)reader.ReadByte();
                int defOrder = reader.ReadInt32();
                handlers.Add(new EventHandlerEntry(funcId, flags, defOrder));
            }

            eventGroups.Add(new EventFunctionGroup(eventName, handlers.MoveToImmutable()));
        }

        // StaticVariables
        int staticVarCount = checked((int)header.StaticVariableCount);
        var staticVars = ImmutableArray.CreateBuilder<VariableSlotDescriptor>(staticVarCount);
        for (int i = 0; i < staticVarCount; i++)
        {
            staticVars.Add(ReadVariableSlot(reader));
        }

        // SourceMap
        SourceMap sourceMap = ReadSourceMap(reader);

        return new BytecodeModule
        {
            Header = header,
            CompilerOptions = compilerOptions,
            ReplaceConfig = replaceConfig,
            Instructions = instructions.MoveToImmutable(),
            IntConstants = intConstants.MoveToImmutable(),
            StringConstants = strConstants.MoveToImmutable(),
            Functions = functions.MoveToImmutable(),
            EventGroups = eventGroups.MoveToImmutable(),
            StaticVariables = staticVars.MoveToImmutable(),
            SourceMap = sourceMap,
        };
    }

    private static void WriteCompilerOptions(BinaryWriter w, CompilerOptions o)
    {
        uint flags = 0;
        if (o.IgnoreCase) flags |= 1u << 0;
        if (o.CompatiFunctionNoIgnoreCase) flags |= 1u << 1;
        if (o.SystemAllowFullSpace) flags |= 1u << 2;
        if (o.SystemIgnoreTripleSymbol) flags |= 1u << 3;
        if (o.CompatiErrorLine) flags |= 1u << 4;
        if (o.CompatiRAND) flags |= 1u << 5;
        if (o.CompatiCALLNAME) flags |= 1u << 6;
        if (o.CompatiFuncArgOptional) flags |= 1u << 7;
        if (o.CompatiFuncArgAutoConvert) flags |= 1u << 8;
        if (o.CompatiSPChara) flags |= 1u << 9;
        if (o.CompatiCallEvent) flags |= 1u << 10;
        if (o.CompatiLinefeedAs1739) flags |= 1u << 11;
        if (o.TimesNotRigorousCalculation) flags |= 1u << 12;
        if (o.SystemNoTarget) flags |= 1u << 13;
        if (o.SystemIgnoreStringSet) flags |= 1u << 14;
        if (o.IgnoreUncalledFunction) flags |= 1u << 15;
        if (o.AllowFunctionOverloading) flags |= 1u << 16;
        if (o.WarnFunctionOverloading) flags |= 1u << 17;
        if (o.WarnNormalFunctionOverloading) flags |= 1u << 18;
        if (o.CheckDuplicateIdentifier) flags |= 1u << 19;
        if (o.UseRenameFile) flags |= 1u << 20;
        if (o.UseReplaceFile) flags |= 1u << 21;
        if (o.SearchSubdirectory) flags |= 1u << 22;
        if (o.SortWithFilename) flags |= 1u << 23;
        if (o.UseERD) flags |= 1u << 24;
        if (o.VarsizeDimConfig) flags |= 1u << 25;
        if (o.UseScopedVariableInstruction) flags |= 1u << 26;
        if (o.UseNewRandom) flags |= 1u << 27;
        if (o.UseButtonFocusBackgroundColor) flags |= 1u << 28;

        w.Write(flags);
        w.Write((byte)o.SifBlankLineBehavior);
        WriteUtf8String(w, o.ReplaceContinuationBR);
    }

    private static CompilerOptions ReadCompilerOptions(BinaryReader r)
    {
        uint flags = r.ReadUInt32();
        var sifBehavior = (SifBlankLineBehavior)r.ReadByte();
        string continuationBr = ReadUtf8String(r);

        return new CompilerOptions
        {
            IgnoreCase = (flags & (1u << 0)) != 0,
            CompatiFunctionNoIgnoreCase = (flags & (1u << 1)) != 0,
            SystemAllowFullSpace = (flags & (1u << 2)) != 0,
            SystemIgnoreTripleSymbol = (flags & (1u << 3)) != 0,
            CompatiErrorLine = (flags & (1u << 4)) != 0,
            CompatiRAND = (flags & (1u << 5)) != 0,
            CompatiCALLNAME = (flags & (1u << 6)) != 0,
            CompatiFuncArgOptional = (flags & (1u << 7)) != 0,
            CompatiFuncArgAutoConvert = (flags & (1u << 8)) != 0,
            CompatiSPChara = (flags & (1u << 9)) != 0,
            CompatiCallEvent = (flags & (1u << 10)) != 0,
            CompatiLinefeedAs1739 = (flags & (1u << 11)) != 0,
            TimesNotRigorousCalculation = (flags & (1u << 12)) != 0,
            SystemNoTarget = (flags & (1u << 13)) != 0,
            SystemIgnoreStringSet = (flags & (1u << 14)) != 0,
            IgnoreUncalledFunction = (flags & (1u << 15)) != 0,
            AllowFunctionOverloading = (flags & (1u << 16)) != 0,
            WarnFunctionOverloading = (flags & (1u << 17)) != 0,
            WarnNormalFunctionOverloading = (flags & (1u << 18)) != 0,
            CheckDuplicateIdentifier = (flags & (1u << 19)) != 0,
            UseRenameFile = (flags & (1u << 20)) != 0,
            UseReplaceFile = (flags & (1u << 21)) != 0,
            SearchSubdirectory = (flags & (1u << 22)) != 0,
            SortWithFilename = (flags & (1u << 23)) != 0,
            UseERD = (flags & (1u << 24)) != 0,
            VarsizeDimConfig = (flags & (1u << 25)) != 0,
            UseScopedVariableInstruction = (flags & (1u << 26)) != 0,
            UseNewRandom = (flags & (1u << 27)) != 0,
            UseButtonFocusBackgroundColor = (flags & (1u << 28)) != 0,
            SifBlankLineBehavior = sifBehavior,
            ReplaceContinuationBR = continuationBr,
        };
    }

    private static void WriteReplaceConfig(BinaryWriter w, ReplaceConfiguration c)
    {
        WriteUtf8String(w, c.MoneyLabel);
        w.Write(c.MoneyFirst);
        WriteUtf8String(w, c.LoadLabel);
        w.Write(c.MaxShopItem);
        WriteUtf8String(w, c.DrawLineString);
        w.Write((ushort)c.BarChar1);
        w.Write((ushort)c.BarChar2);
        WriteUtf8String(w, c.TitleMenuString0);
        WriteUtf8String(w, c.TitleMenuString1);
        w.Write(c.ComAbleDefault);
        WriteLongList(w, c.StainDefault);
        WriteUtf8String(w, c.TimeupLabel);
        WriteLongList(w, c.ExpLvDef);
        WriteLongList(w, c.PalamLvDef);
        w.Write(c.PbandDef);
        w.Write(c.RelationDef);
    }

    private static ReplaceConfiguration ReadReplaceConfig(BinaryReader r)
    {
        string moneyLabel = ReadUtf8String(r);
        bool moneyFirst = r.ReadBoolean();
        string loadLabel = ReadUtf8String(r);
        int maxShopItem = r.ReadInt32();
        string drawLineString = ReadUtf8String(r);
        char barChar1 = (char)r.ReadUInt16();
        char barChar2 = (char)r.ReadUInt16();
        string title0 = ReadUtf8String(r);
        string title1 = ReadUtf8String(r);
        int comAbleDefault = r.ReadInt32();
        long[] stainDef = ReadLongList(r);
        string timeupLabel = ReadUtf8String(r);
        long[] expLvDef = ReadLongList(r);
        long[] palamLvDef = ReadLongList(r);
        long pbandDef = r.ReadInt64();
        long relationDef = r.ReadInt64();

        return new ReplaceConfiguration
        {
            MoneyLabel = moneyLabel,
            MoneyFirst = moneyFirst,
            LoadLabel = loadLabel,
            MaxShopItem = maxShopItem,
            DrawLineString = drawLineString,
            BarChar1 = barChar1,
            BarChar2 = barChar2,
            TitleMenuString0 = title0,
            TitleMenuString1 = title1,
            ComAbleDefault = comAbleDefault,
            StainDefault = stainDef,
            TimeupLabel = timeupLabel,
            ExpLvDef = expLvDef,
            PalamLvDef = palamLvDef,
            PbandDef = pbandDef,
            RelationDef = relationDef,
        };
    }

    private static void WriteFunctionDescriptor(BinaryWriter w, FunctionDescriptor f)
    {
        w.Write(f.FunctionId);
        WriteUtf8String(w, f.Name);
        w.Write(f.EntryPc);
        w.Write(f.InstructionCount);
        w.Write(f.IntRegisterCount);
        w.Write(f.StrRegisterCount);
        w.Write(f.IsMethodFunction);
        w.Write(f.MethodReturnKind.HasValue ? (byte)f.MethodReturnKind.Value : (byte)0xFF);
        w.Write((byte)f.EventFlags);

        w.Write(f.Parameters.Length);
        foreach (FunctionParameterDescriptor p in f.Parameters)
        {
            WriteUtf8String(w, p.Name);
            w.Write((byte)p.ValueKind);
            w.Write(p.IsByRef);
            w.Write(p.RegisterOrSlotIndex);
            w.Write(p.HasDefaultValue);
            w.Write(p.DefaultIntValue);
            w.Write(p.DefaultStrValue is not null);
            if (p.DefaultStrValue is not null)
            {
                WriteUtf8String(w, p.DefaultStrValue);
            }
        }

        w.Write(f.LocalVariables.Length);
        foreach (VariableSlotDescriptor local in f.LocalVariables)
        {
            WriteVariableSlot(w, local);
        }
    }

    private static FunctionDescriptor ReadFunctionDescriptor(BinaryReader r)
    {
        int id = r.ReadInt32();
        string name = ReadUtf8String(r);
        int entryPc = r.ReadInt32();
        int insnCount = r.ReadInt32();
        ushort intRegCount = r.ReadUInt16();
        ushort strRegCount = r.ReadUInt16();
        bool isMethod = r.ReadBoolean();
        byte retKindByte = r.ReadByte();
        VariableValueKind? retKind = retKindByte == 0xFF ? null : (VariableValueKind)retKindByte;
        var eventFlags = (EventDirectiveFlags)r.ReadByte();

        int paramCount = r.ReadInt32();
        var parameters = ImmutableArray.CreateBuilder<FunctionParameterDescriptor>(paramCount);
        for (int i = 0; i < paramCount; i++)
        {
            string pName = ReadUtf8String(r);
            var pKind = (VariableValueKind)r.ReadByte();
            bool isByRef = r.ReadBoolean();
            int regIndex = r.ReadInt32();
            bool hasDefault = r.ReadBoolean();
            long defInt = r.ReadInt64();
            bool hasDefStr = r.ReadBoolean();
            string? defStr = hasDefStr ? ReadUtf8String(r) : null;
            parameters.Add(new FunctionParameterDescriptor(pName, pKind, isByRef, regIndex, hasDefault, defInt, defStr));
        }

        int localCount = r.ReadInt32();
        var locals = ImmutableArray.CreateBuilder<VariableSlotDescriptor>(localCount);
        for (int i = 0; i < localCount; i++)
        {
            locals.Add(ReadVariableSlot(r));
        }

        return new FunctionDescriptor(
            id,
            name,
            entryPc,
            insnCount,
            intRegCount,
            strRegCount,
            isMethod,
            retKind,
            eventFlags,
            parameters.MoveToImmutable(),
            locals.MoveToImmutable());
    }

    private static void WriteVariableSlot(BinaryWriter w, VariableSlotDescriptor v)
    {
        w.Write(v.SlotId);
        WriteUtf8String(w, v.Name);
        w.Write((byte)v.ValueKind);
        w.Write((byte)v.Scope);
        w.Write(v.Dimensions.Length);
        foreach (int dim in v.Dimensions)
        {
            w.Write(dim);
        }

        bool hasDefaults = (v.ValueKind == VariableValueKind.Int && !v.DefaultIntValues.IsDefaultOrEmpty)
            || (v.ValueKind == VariableValueKind.Str && !v.DefaultStrValues.IsDefaultOrEmpty);

        byte flags = 0;
        if (v.IsCharaData) flags |= 1 << 0;
        if (v.IsSavedata) flags |= 1 << 1;
        if (v.IsGlobal) flags |= 1 << 2;
        if (v.IsConst) flags |= 1 << 3;
        if (hasDefaults) flags |= 1 << 4;
        w.Write(flags);

        if (hasDefaults)
        {
            if (v.ValueKind == VariableValueKind.Int)
            {
                w.Write(v.DefaultIntValues.Length);
                foreach (long defVal in v.DefaultIntValues)
                {
                    w.Write(defVal);
                }
            }
            else
            {
                w.Write(v.DefaultStrValues.Length);
                foreach (string defVal in v.DefaultStrValues)
                {
                    WriteUtf8String(w, defVal ?? string.Empty);
                }
            }
        }
    }

    private static VariableSlotDescriptor ReadVariableSlot(BinaryReader r)
    {
        int slotId = r.ReadInt32();
        string name = ReadUtf8String(r);
        var valueKind = (VariableValueKind)r.ReadByte();
        var scope = (VariableScopeKind)r.ReadByte();
        int dimCount = r.ReadInt32();
        var dims = ImmutableArray.CreateBuilder<int>(dimCount);
        for (int i = 0; i < dimCount; i++)
        {
            dims.Add(r.ReadInt32());
        }

        byte flags = r.ReadByte();
        ImmutableArray<long> defaultInts = default;
        ImmutableArray<string> defaultStrs = default;
        if ((flags & (1 << 4)) != 0)
        {
            int defCount = r.ReadInt32();
            if (valueKind == VariableValueKind.Int)
            {
                var arr = ImmutableArray.CreateBuilder<long>(defCount);
                for (int i = 0; i < defCount; i++)
                {
                    arr.Add(r.ReadInt64());
                }

                defaultInts = arr.MoveToImmutable();
            }
            else
            {
                var arr = ImmutableArray.CreateBuilder<string>(defCount);
                for (int i = 0; i < defCount; i++)
                {
                    arr.Add(ReadUtf8String(r));
                }

                defaultStrs = arr.MoveToImmutable();
            }
        }

        return new VariableSlotDescriptor(
            slotId,
            name,
            valueKind,
            scope,
            dims.MoveToImmutable(),
            IsCharaData: (flags & (1 << 0)) != 0,
            IsSavedata: (flags & (1 << 1)) != 0,
            IsGlobal: (flags & (1 << 2)) != 0,
            IsConst: (flags & (1 << 3)) != 0,
            DefaultIntValues: defaultInts,
            DefaultStrValues: defaultStrs);
    }

    private static void WriteSourceMap(BinaryWriter w, SourceMap map)
    {
        w.Write(map.Files.Count);
        foreach (SourceFileEntry file in map.Files)
        {
            w.Write(file.FileId);
            WriteUtf8String(w, file.VirtualPath);
            WriteUtf8String(w, file.DisplayName);
        }

        w.Write(map.Runs.Count);
        foreach (SourceMapRun run in map.Runs)
        {
            w.Write(run.StartPc);
            w.Write(run.FileId);
            w.Write(run.LineNo);
        }
    }

    private static SourceMap ReadSourceMap(BinaryReader r)
    {
        int fileCount = r.ReadInt32();
        var files = new SourceFileEntry[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            int id = r.ReadInt32();
            string vpath = ReadUtf8String(r);
            string display = ReadUtf8String(r);
            files[i] = new SourceFileEntry(id, vpath, display);
        }

        int runCount = r.ReadInt32();
        var runs = new SourceMapRun[runCount];
        for (int i = 0; i < runCount; i++)
        {
            int startPc = r.ReadInt32();
            int fileId = r.ReadInt32();
            int lineNo = r.ReadInt32();
            runs[i] = new SourceMapRun(startPc, fileId, lineNo);
        }

        return new SourceMap(files, runs);
    }

    private static void WriteUtf8String(BinaryWriter w, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        w.Write((uint)bytes.Length);
        w.Write(bytes);
    }

    private static string ReadUtf8String(BinaryReader r)
    {
        uint len = r.ReadUInt32();
        if (len == 0)
        {
            return string.Empty;
        }

        byte[] bytes = r.ReadBytes(checked((int)len));
        if (bytes.Length != len)
        {
            throw new InvalidDataException("Truncated UTF-8 string in .heb module.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteLongList(BinaryWriter w, IReadOnlyList<long> values)
    {
        w.Write(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            w.Write(values[i]);
        }
    }

    private static long[] ReadLongList(BinaryReader r)
    {
        int count = r.ReadInt32();
        long[] values = new long[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = r.ReadInt64();
        }

        return values;
    }
}
