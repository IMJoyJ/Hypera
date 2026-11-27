using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Hypera.VirtualFs;

namespace Hypera.Persistence;

/// <summary>
/// Upstream-compatible Emuera save/load serializer supporting:
/// - Legacy text .sav / global.sav (auto-detecting UTF-8 BOM / UTF-8 / Shift-JIS / GB18030)
/// - Binary .sav (magic 0x0A1A0A0D41524589) with Ebdb RLE zero-run compression tokens
/// - GZip-compressed binary .sav (magic 0x0A50495A41524589)
/// - Character rosters, #DIM SAVEDATA / GLOBAL / CHARADATA, and VarExt Map / Xml / DataTable.
/// </summary>
public static class EraSaveSerializer
{
    public const ulong BinaryHeaderMagic = 0x0A1A0A0D41524589UL; // \x89ERA\r\n\x1A\n
    public const ulong ZipHeaderMagic = 0x0A50495A41524589UL;    // \x89ERAZIP\n
    public const uint CurrentBinaryVersion = 1808u;

    // Legacy text format markers (upstream EraDataStream constants)
    public const string Emu1700Start = "__EMUERA_STRAT__";
    public const string Emu1708Start = "__EMUERA_1708_STRAT__";
    public const string Emu1729Start = "__EMUERA_1729_STRAT__";
    public const string Emu1803Start = "__EMUERA_1803_STRAT__";
    public const string Emu1808Start = "__EMUERA_1808_STRAT__";
    public const string Finisher = "__FINISHED";
    public const string EmuSeparator = "__EMU_SEPARATOR__";

    // Ebdb binary compression tokens
    private const byte EbdbByteMax = 0xCF;
    private const byte EbdbInt16 = 0xD0;
    private const byte EbdbInt32 = 0xD1;
    private const byte EbdbInt64 = 0xD2;
    private const byte EbdbString = 0xD8;
    private const byte EbdbEoA1 = 0xE0;
    private const byte EbdbEoA2 = 0xE1;
    private const byte EbdbZero = 0xF0;
    private const byte EbdbZeroA1 = 0xF1;
    private const byte EbdbZeroA2 = 0xF2;
    private const byte EbdbEoD = 0xFF;

    public static byte[] Save(EraSaveSnapshot snapshot, EraSaveFormat format)
    {
        using var ms = new MemoryStream();
        Save(snapshot, ms, format);
        return ms.ToArray();
    }

    public static void Save(EraSaveSnapshot snapshot, Stream output, EraSaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(output);

        switch (format)
        {
            case EraSaveFormat.Text:
                SaveText(snapshot, output);
                break;
            case EraSaveFormat.Binary:
                SaveBinary(snapshot, output, compressed: false);
                break;
            case EraSaveFormat.GZipBinary:
                SaveBinary(snapshot, output, compressed: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported save format.");
        }
    }

    public static EraSaveFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8)
        {
            ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(data[0..8]);
            if (magic == BinaryHeaderMagic)
            {
                return EraSaveFormat.Binary;
            }

            if (magic == ZipHeaderMagic)
            {
                return EraSaveFormat.GZipBinary;
            }
        }

        return EraSaveFormat.Text;
    }

    public static EraSaveHeader PeekHeader(ReadOnlySpan<byte> data, IEncodingDetector? encodingDetector = null)
    {
        EraSaveFormat format = DetectFormat(data);
        if (format == EraSaveFormat.Text)
        {
            string text = (encodingDetector ?? EncodingDetector.Default).Decode(data, out _);
            using var sr = new StringReader(text);
            long code = ReadLineLong(sr);
            long version = ReadLineLong(sr);
            string saveText = UnescapeTextLine(sr.ReadLine() ?? string.Empty);
            string? fourthLine = sr.ReadLine();
            EraSaveFileType fileType = fourthLine is not null && IsEmueraStartMarker(fourthLine)
                ? EraSaveFileType.Global
                : EraSaveFileType.Normal;
            return new EraSaveHeader(code, version, saveText, fileType, CurrentBinaryVersion, EraSaveFormat.Text);
        }

        using var ms = new MemoryStream(data.ToArray(), writable: false);
        return PeekBinaryHeader(ms, format == EraSaveFormat.GZipBinary);
    }

    public static EraSaveSnapshot Load(ReadOnlySpan<byte> data, IEncodingDetector? encodingDetector = null)
    {
        EraSaveFormat format = DetectFormat(data);
        if (format == EraSaveFormat.Text)
        {
            string text = (encodingDetector ?? EncodingDetector.Default).Decode(data, out _);
            return LoadText(text);
        }

        using var ms = new MemoryStream(data.ToArray(), writable: false);
        return LoadBinary(ms, compressed: format == EraSaveFormat.GZipBinary);
    }

    public static EraSaveSnapshot Load(Stream input, IEncodingDetector? encodingDetector = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return Load(ms.GetBuffer().AsSpan(0, (int)ms.Length), encodingDetector);
    }

    #region Binary & GZip Binary Serialization

    private static void SaveBinary(EraSaveSnapshot snapshot, Stream output, bool compressed)
    {
        // Upstream Emuera writes the 16-byte header (magic + version + dataCount) UNCOMPRESSED
        // before opening the GZip stream for compressed saves.
        Span<byte> headerBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(headerBytes[0..8], compressed ? ZipHeaderMagic : BinaryHeaderMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[8..12], snapshot.Header.BinaryVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[12..16], 0u); // DataCount reserved (0 in upstream)
        output.Write(headerBytes);

        if (compressed)
        {
            using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            WriteBinaryPayload(snapshot, gzip);
        }
        else
        {
            WriteBinaryPayload(snapshot, output);
        }
    }

    private static void WriteBinaryPayload(EraSaveSnapshot snapshot, Stream targetStream)
    {
        // Upstream EraBinaryDataWriter uses Encoding.Unicode (UTF-16LE) for all BinaryWriter strings.
        using var w = new BinaryWriter(targetStream, Encoding.Unicode, leaveOpen: true);

        w.Write((byte)snapshot.Header.FileType);
        w.Write(snapshot.Header.ScriptUniqueCode);
        w.Write(snapshot.Header.ScriptVersion);
        w.Write(snapshot.Header.SaveDataText ?? string.Empty);

        if (snapshot.Header.FileType is EraSaveFileType.Normal or EraSaveFileType.CharVar)
        {
            w.Write((long)snapshot.Characters.Count);
            foreach (EraCharacterSaveData chara in snapshot.Characters)
            {
                WriteCharacterBinary(w, chara);
            }
        }

        // Global/static SAVEDATA variables (or GLOBAL variables for global.sav)
        foreach ((string key, EraSaveValue value) in snapshot.Variables)
        {
            WriteWithKey(w, key, value);
        }

        w.Write((byte)EraSaveDataType.EndOfFile);

        // VarExt (EM Data: Map, Xml, DataTable)
        if (snapshot.Header.FileType is EraSaveFileType.Normal or EraSaveFileType.Global || snapshot.VarExtVariables.Count > 0)
        {
            foreach ((string key, EraSaveValue value) in snapshot.VarExtVariables)
            {
                WriteWithKey(w, key, value);
            }

            w.Write((byte)EraSaveDataType.EndOfFile);
        }

        w.Flush();
    }

    private static void WriteCharacterBinary(BinaryWriter w, EraCharacterSaveData chara)
    {
        WriteWithKey(w, "NO", EraSaveValue.FromInt(chara.No));
        if (chara.IsAssi != 0)
        {
            WriteWithKey(w, "ISASSI", EraSaveValue.FromInt(chara.IsAssi));
        }

        if (!string.IsNullOrEmpty(chara.Name))
        {
            WriteWithKey(w, "NAME", EraSaveValue.FromStr(chara.Name));
        }

        if (!string.IsNullOrEmpty(chara.CallName))
        {
            WriteWithKey(w, "CALLNAME", EraSaveValue.FromStr(chara.CallName));
        }

        if (!string.IsNullOrEmpty(chara.NickName))
        {
            WriteWithKey(w, "NICKNAME", EraSaveValue.FromStr(chara.NickName));
        }

        if (!string.IsNullOrEmpty(chara.MasterName))
        {
            WriteWithKey(w, "MASTERNAME", EraSaveValue.FromStr(chara.MasterName));
        }

        foreach ((string key, EraSaveValue value) in chara.BuiltinVariables)
        {
            WriteWithKey(w, key, value);
        }

        if (chara.UserVariables.Count > 0)
        {
            w.Write((byte)EraSaveDataType.Separator);
            foreach ((string key, EraSaveValue value) in chara.UserVariables)
            {
                WriteWithKey(w, key, value);
            }
        }

        w.Write((byte)EraSaveDataType.EndOfCharacter);
    }

    private static BinaryReader OpenBinaryPayloadReader(Stream input, bool compressed, out uint version)
    {
        input.Position = 0;
        Span<byte> headerBytes = stackalloc byte[16];
        input.ReadExactly(headerBytes);

        version = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[8..12]);
        uint dataCount = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[12..16]);
        if (dataCount > 0)
        {
            if (dataCount > 1024 * 1024)
            {
                throw new InvalidDataException($"Invalid binary save header dataCount: {dataCount}.");
            }

            byte[] extra = new byte[dataCount * 4];
            input.ReadExactly(extra);
        }

        Stream payloadStream = compressed
            ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true)
            : input;

        // Upstream EraBinaryDataReader uses Encoding.Unicode (UTF-16LE) for all BinaryReader strings.
        return new BinaryReader(payloadStream, Encoding.Unicode, leaveOpen: !compressed);
    }

    private static EraSaveHeader PeekBinaryHeader(Stream input, bool compressed)
    {
        using BinaryReader r = OpenBinaryPayloadReader(input, compressed, out uint version);

        byte fileTypeByte = r.ReadByte();
        if (fileTypeByte > (byte)EraSaveFileType.CharVar)
        {
            throw new InvalidDataException($"Invalid EraSaveFileType: 0x{fileTypeByte:X2}");
        }

        var fileType = (EraSaveFileType)fileTypeByte;
        long uniqueCode = r.ReadInt64();
        long scriptVersion = r.ReadInt64();
        string saveText = r.ReadString();

        return new EraSaveHeader(
            uniqueCode,
            scriptVersion,
            saveText,
            fileType,
            version,
            compressed ? EraSaveFormat.GZipBinary : EraSaveFormat.Binary);
    }

    private static EraSaveSnapshot LoadBinary(Stream input, bool compressed)
    {
        using BinaryReader r = OpenBinaryPayloadReader(input, compressed, out uint version);

        byte fileTypeByte = r.ReadByte();
        if (fileTypeByte > (byte)EraSaveFileType.CharVar)
        {
            throw new InvalidDataException($"Invalid EraSaveFileType: 0x{fileTypeByte:X2}");
        }

        var fileType = (EraSaveFileType)fileTypeByte;
        long uniqueCode = r.ReadInt64();
        long scriptVersion = r.ReadInt64();
        string saveText = r.ReadString();

        var header = new EraSaveHeader(
            uniqueCode,
            scriptVersion,
            saveText,
            fileType,
            version,
            compressed ? EraSaveFormat.GZipBinary : EraSaveFormat.Binary);

        var characters = new List<EraCharacterSaveData>();
        if (fileType is EraSaveFileType.Normal or EraSaveFileType.CharVar)
        {
            long charaCount = r.ReadInt64();
            if (charaCount < 0 || charaCount > 1_000_000)
            {
                throw new InvalidDataException($"Invalid character count in save file: {charaCount}.");
            }

            for (long i = 0; i < charaCount; i++)
            {
                characters.Add(ReadCharacterBinary(r));
            }
        }

        var variables = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase);
        var varExt = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase);

        // Section 1: Variables (terminated by EndOfFile 0xFF)
        ReadBinaryKeyValueSection(r, variables, varExt);

        // Section 2: VarExt / EM data (optional in vanilla Emuera; terminated by EndOfFile 0xFF or stream EOF)
        ReadBinaryKeyValueSection(r, variables, varExt);

        return new EraSaveSnapshot
        {
            Header = header,
            Characters = characters,
            Variables = variables,
            VarExtVariables = varExt,
        };
    }

    private static void ReadBinaryKeyValueSection(
        BinaryReader r,
        Dictionary<string, EraSaveValue> variables,
        Dictionary<string, EraSaveValue> varExt)
    {
        while (TryReadKeyValue(r, out EraSaveDataType tag, out string key, out EraSaveValue? value))
        {
            if (tag == EraSaveDataType.EndOfFile)
            {
                break;
            }

            if (value is null)
            {
                continue;
            }

            if (tag is EraSaveDataType.Map or EraSaveDataType.Xml or EraSaveDataType.DataTable)
            {
                varExt[key] = value;
            }
            else
            {
                variables[key] = value;
            }
        }
    }

    private static EraCharacterSaveData ReadCharacterBinary(BinaryReader r)
    {
        var chara = new EraCharacterSaveData();
        bool inUserSection = false;

        while (true)
        {
            var tag = (EraSaveDataType)r.ReadByte();
            if (tag == EraSaveDataType.EndOfCharacter || tag == EraSaveDataType.EndOfFile)
            {
                break;
            }

            if (tag == EraSaveDataType.Separator)
            {
                inUserSection = true;
                continue;
            }

            string key = r.ReadString();
            EraSaveValue val = ReadValuePayload(r, tag);

            if (inUserSection)
            {
                chara.UserVariables[key] = val;
                continue;
            }

            switch (key.ToUpperInvariant())
            {
                case "NO":
                    chara.No = val.AsInt();
                    break;
                case "ISASSI":
                    chara.IsAssi = val.AsInt();
                    break;
                case "NAME":
                    chara.Name = val.AsStr();
                    break;
                case "CALLNAME":
                    chara.CallName = val.AsStr();
                    break;
                case "NICKNAME":
                    chara.NickName = val.AsStr();
                    break;
                case "MASTERNAME":
                    chara.MasterName = val.AsStr();
                    break;
                default:
                    chara.BuiltinVariables[key] = val;
                    break;
            }
        }

        return chara;
    }

    private static bool TryReadKeyValue(BinaryReader r, out EraSaveDataType tag, out string key, out EraSaveValue? value)
    {
        int b;
        try
        {
            b = r.ReadByte();
        }
        catch (EndOfStreamException)
        {
            tag = EraSaveDataType.EndOfFile;
            key = string.Empty;
            value = null;
            return false;
        }

        tag = (EraSaveDataType)b;
        if (tag is EraSaveDataType.EndOfFile or EraSaveDataType.EndOfCharacter or EraSaveDataType.Separator)
        {
            key = string.Empty;
            value = null;
            return true;
        }

        key = r.ReadString();
        value = ReadValuePayload(r, tag);
        return true;
    }

    private static void WriteWithKey(BinaryWriter w, string key, EraSaveValue value)
    {
        w.Write((byte)value.DataType);
        w.Write(key);

        switch (value.DataType)
        {
            case EraSaveDataType.Int:
                WriteEbdbInt(w, value.AsInt());
                break;
            case EraSaveDataType.IntArray:
                WriteIntArray1D(w, value.AsIntArray());
                break;
            case EraSaveDataType.IntArray2D:
                WriteIntArray2D(w, value.AsIntArray2D());
                break;
            case EraSaveDataType.IntArray3D:
                WriteIntArray3D(w, value.AsIntArray3D());
                break;
            case EraSaveDataType.Str:
                w.Write(value.AsStr() ?? string.Empty);
                break;
            case EraSaveDataType.StrArray:
                WriteStrArray1D(w, value.AsStrArray());
                break;
            case EraSaveDataType.StrArray2D:
                WriteStrArray2D(w, value.AsStrArray2D());
                break;
            case EraSaveDataType.StrArray3D:
                WriteStrArray3D(w, value.AsStrArray3D());
                break;
            case EraSaveDataType.Map:
                WriteMap(w, value.AsMap());
                break;
            case EraSaveDataType.Xml:
                w.Write(value.AsXml() ?? string.Empty);
                break;
            case EraSaveDataType.DataTable:
                WriteDataTable(w, key, value.AsDataTable());
                break;
            default:
                throw new InvalidDataException($"Unsupported EraSaveDataType: {value.DataType}");
        }
    }

    private static EraSaveValue ReadValuePayload(BinaryReader r, EraSaveDataType tag) => tag switch
    {
        EraSaveDataType.Int => EraSaveValue.FromInt(ReadEbdbInt(r)),
        EraSaveDataType.IntArray => EraSaveValue.FromIntArray(ReadIntArray1D(r)),
        EraSaveDataType.IntArray2D => EraSaveValue.FromIntArray2D(ReadIntArray2D(r)),
        EraSaveDataType.IntArray3D => EraSaveValue.FromIntArray3D(ReadIntArray3D(r)),
        EraSaveDataType.Str => EraSaveValue.FromStr(r.ReadString()),
        EraSaveDataType.StrArray => EraSaveValue.FromStrArray(ReadStrArray1D(r)),
        EraSaveDataType.StrArray2D => EraSaveValue.FromStrArray2D(ReadStrArray2D(r)),
        EraSaveDataType.StrArray3D => EraSaveValue.FromStrArray3D(ReadStrArray3D(r)),
        EraSaveDataType.Map => EraSaveValue.FromMap(ReadMap(r)),
        EraSaveDataType.Xml => EraSaveValue.FromXml(r.ReadString()),
        EraSaveDataType.DataTable => EraSaveValue.FromDataTable(ReadDataTable(r)),
        _ => throw new InvalidDataException($"Unexpected EraSaveDataType tag: 0x{(byte)tag:X2}"),
    };

    #endregion

    #region Ebdb Primitives & RLE Array Encoders/Decoders

    private static void WriteEbdbInt(BinaryWriter w, long value)
    {
        if (value is >= 0 and <= EbdbByteMax)
        {
            w.Write((byte)value);
        }
        else if (value is >= short.MinValue and <= short.MaxValue)
        {
            w.Write(EbdbInt16);
            w.Write((short)value);
        }
        else if (value is >= int.MinValue and <= int.MaxValue)
        {
            w.Write(EbdbInt32);
            w.Write((int)value);
        }
        else
        {
            w.Write(EbdbInt64);
            w.Write(value);
        }
    }

    private static long ReadEbdbInt(BinaryReader r)
    {
        byte token = r.ReadByte();
        return token switch
        {
            <= EbdbByteMax => token,
            EbdbInt16 => r.ReadInt16(),
            EbdbInt32 => r.ReadInt32(),
            EbdbInt64 => r.ReadInt64(),
            _ => throw new InvalidDataException($"Unexpected Ebdb integer token: 0x{token:X2}"),
        };
    }

    private static void WriteEbdbString(BinaryWriter w, string value)
    {
        w.Write(EbdbString);
        w.Write(value ?? string.Empty);
    }

    private static void WriteIntArray1D(BinaryWriter w, long[] arr)
    {
        w.Write(arr.Length);
        WriteIntSpanRle(w, arr);
        w.Write(EbdbEoD);
    }

    private static long[] ReadIntArray1D(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len < 0)
        {
            throw new InvalidDataException($"Invalid IntArray1D length: {len}.");
        }

        long[] arr = new long[len];
        int idx = 0;
        while (true)
        {
            byte token = r.ReadByte();
            if (token == EbdbEoD)
            {
                break;
            }

            if (token == EbdbZero)
            {
                int count = checked((int)ReadEbdbInt(r));
                idx += count;
            }
            else
            {
                long v = ReadEbdbIntFromToken(r, token);
                if ((uint)idx < (uint)arr.Length)
                {
                    arr[idx] = v;
                }

                idx++;
            }
        }

        return arr;
    }

    private static void WriteIntArray2D(BinaryWriter w, long[,] arr)
    {
        int d0 = arr.GetLength(0);
        int d1 = arr.GetLength(1);
        w.Write(d0);
        w.Write(d1);

        int zeroRowRun = 0;
        for (int i = 0; i < d0; i++)
        {
            if (IsZeroRow(arr, i, d1))
            {
                zeroRowRun++;
                continue;
            }

            if (zeroRowRun > 0)
            {
                w.Write(EbdbZeroA1);
                WriteEbdbInt(w, zeroRowRun);
                zeroRowRun = 0;
            }

            int zeroRun = 0;
            for (int j = 0; j < d1; j++)
            {
                long v = arr[i, j];
                if (v == 0)
                {
                    zeroRun++;
                }
                else
                {
                    if (zeroRun > 0)
                    {
                        w.Write(EbdbZero);
                        WriteEbdbInt(w, zeroRun);
                        zeroRun = 0;
                    }

                    WriteEbdbInt(w, v);
                }
            }

            w.Write(EbdbEoA1);
        }

        w.Write(EbdbEoD);
    }

    private static long[,] ReadIntArray2D(BinaryReader r)
    {
        int d0 = r.ReadInt32();
        int d1 = r.ReadInt32();
        if (d0 < 0 || d1 < 0)
        {
            throw new InvalidDataException($"Invalid IntArray2D dimensions: {d0}x{d1}.");
        }

        long[,] arr = new long[d0, d1];
        int i = 0;
        int j = 0;

        while (true)
        {
            byte token = r.ReadByte();
            if (token == EbdbEoD)
            {
                break;
            }

            if (token == EbdbEoA1)
            {
                i++;
                j = 0;
                continue;
            }

            if (token == EbdbZeroA1)
            {
                int rows = checked((int)ReadEbdbInt(r));
                i += rows;
                j = 0;
                continue;
            }

            if (token == EbdbZero)
            {
                int zeros = checked((int)ReadEbdbInt(r));
                j += zeros;
                continue;
            }

            long v = ReadEbdbIntFromToken(r, token);
            if ((uint)i < (uint)d0 && (uint)j < (uint)d1)
            {
                arr[i, j] = v;
            }

            j++;
        }

        return arr;
    }

    private static void WriteIntArray3D(BinaryWriter w, long[,,] arr)
    {
        int d0 = arr.GetLength(0);
        int d1 = arr.GetLength(1);
        int d2 = arr.GetLength(2);
        w.Write(d0);
        w.Write(d1);
        w.Write(d2);

        int zeroSliceRun = 0;
        for (int i = 0; i < d0; i++)
        {
            if (IsZeroSlice(arr, i, d1, d2))
            {
                zeroSliceRun++;
                continue;
            }

            if (zeroSliceRun > 0)
            {
                w.Write(EbdbZeroA2);
                WriteEbdbInt(w, zeroSliceRun);
                zeroSliceRun = 0;
            }

            int zeroRowRun = 0;
            for (int j = 0; j < d1; j++)
            {
                if (IsZeroRow3D(arr, i, j, d2))
                {
                    zeroRowRun++;
                    continue;
                }

                if (zeroRowRun > 0)
                {
                    w.Write(EbdbZeroA1);
                    WriteEbdbInt(w, zeroRowRun);
                    zeroRowRun = 0;
                }

                int zeroRun = 0;
                for (int k = 0; k < d2; k++)
                {
                    long v = arr[i, j, k];
                    if (v == 0)
                    {
                        zeroRun++;
                    }
                    else
                    {
                        if (zeroRun > 0)
                        {
                            w.Write(EbdbZero);
                            WriteEbdbInt(w, zeroRun);
                            zeroRun = 0;
                        }

                        WriteEbdbInt(w, v);
                    }
                }

                w.Write(EbdbEoA1);
            }

            w.Write(EbdbEoA2);
        }

        w.Write(EbdbEoD);
    }

    private static long[,,] ReadIntArray3D(BinaryReader r)
    {
        int d0 = r.ReadInt32();
        int d1 = r.ReadInt32();
        int d2 = r.ReadInt32();
        if (d0 < 0 || d1 < 0 || d2 < 0)
        {
            throw new InvalidDataException($"Invalid IntArray3D dimensions: {d0}x{d1}x{d2}.");
        }

        long[,,] arr = new long[d0, d1, d2];
        int i = 0, j = 0, k = 0;

        while (true)
        {
            byte token = r.ReadByte();
            if (token == EbdbEoD)
            {
                break;
            }

            if (token == EbdbEoA2)
            {
                i++;
                j = 0;
                k = 0;
                continue;
            }

            if (token == EbdbZeroA2)
            {
                i += checked((int)ReadEbdbInt(r));
                j = 0;
                k = 0;
                continue;
            }

            if (token == EbdbEoA1)
            {
                j++;
                k = 0;
                continue;
            }

            if (token == EbdbZeroA1)
            {
                j += checked((int)ReadEbdbInt(r));
                k = 0;
                continue;
            }

            if (token == EbdbZero)
            {
                k += checked((int)ReadEbdbInt(r));
                continue;
            }

            long v = ReadEbdbIntFromToken(r, token);
            if ((uint)i < (uint)d0 && (uint)j < (uint)d1 && (uint)k < (uint)d2)
            {
                arr[i, j, k] = v;
            }

            k++;
        }

        return arr;
    }

    private static void WriteStrArray1D(BinaryWriter w, string[] arr)
    {
        w.Write(arr.Length);
        int emptyRun = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            string s = arr[i] ?? string.Empty;
            if (s.Length == 0)
            {
                emptyRun++;
            }
            else
            {
                if (emptyRun > 0)
                {
                    w.Write(EbdbZero);
                    WriteEbdbInt(w, emptyRun);
                    emptyRun = 0;
                }

                WriteEbdbString(w, s);
            }
        }

        w.Write(EbdbEoD);
    }

    private static string[] ReadStrArray1D(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len < 0)
        {
            throw new InvalidDataException($"Invalid StrArray1D length: {len}.");
        }

        string[] arr = new string[len];
        Array.Fill(arr, string.Empty);
        int idx = 0;

        while (true)
        {
            byte token = r.ReadByte();
            if (token == EbdbEoD)
            {
                break;
            }

            if (token == EbdbZero)
            {
                idx += checked((int)ReadEbdbInt(r));
            }
            else if (token == EbdbString)
            {
                string s = r.ReadString();
                if ((uint)idx < (uint)arr.Length)
                {
                    arr[idx] = s;
                }

                idx++;
            }
            else
            {
                throw new InvalidDataException($"Unexpected token 0x{token:X2} in StrArray1D.");
            }
        }

        return arr;
    }

    private static void WriteStrArray2D(BinaryWriter w, string[,] arr)
    {
        int d0 = arr.GetLength(0);
        int d1 = arr.GetLength(1);
        w.Write(d0);
        w.Write(d1);

        int emptyRowRun = 0;
        for (int i = 0; i < d0; i++)
        {
            if (IsEmptyRow(arr, i, d1))
            {
                emptyRowRun++;
                continue;
            }

            if (emptyRowRun > 0)
            {
                w.Write(EbdbZeroA1);
                WriteEbdbInt(w, emptyRowRun);
                emptyRowRun = 0;
            }

            int emptyRun = 0;
            for (int j = 0; j < d1; j++)
            {
                string s = arr[i, j] ?? string.Empty;
                if (s.Length == 0)
                {
                    emptyRun++;
                }
                else
                {
                    if (emptyRun > 0)
                    {
                        w.Write(EbdbZero);
                        WriteEbdbInt(w, emptyRun);
                        emptyRun = 0;
                    }

                    WriteEbdbString(w, s);
                }
            }

            w.Write(EbdbEoA1);
        }

        w.Write(EbdbEoD);
    }

    private static string[,] ReadStrArray2D(BinaryReader r)
    {
        int d0 = r.ReadInt32();
        int d1 = r.ReadInt32();
        if (d0 < 0 || d1 < 0)
        {
            throw new InvalidDataException($"Invalid StrArray2D dimensions: {d0}x{d1}.");
        }

        string[,] arr = new string[d0, d1];
        for (int a = 0; a < d0; a++)
        {
            for (int b = 0; b < d1; b++)
            {
                arr[a, b] = string.Empty;
            }
        }

        int i = 0, j = 0;
        while (true)
        {
            byte token = r.ReadByte();
            if (token == EbdbEoD)
            {
                break;
            }

            if (token == EbdbEoA1)
            {
                i++;
                j = 0;
            }
            else if (token == EbdbZeroA1)
            {
                i += checked((int)ReadEbdbInt(r));
                j = 0;
            }
            else if (token == EbdbZero)
            {
                j += checked((int)ReadEbdbInt(r));
            }
            else if (token == EbdbString)
            {
                string s = r.ReadString();
                if ((uint)i < (uint)d0 && (uint)j < (uint)d1)
                {
                    arr[i, j] = s;
                }

                j++;
            }
            else
            {
                throw new InvalidDataException($"Unexpected token 0x{token:X2} in StrArray2D.");
            }
        }

        return arr;
    }

    private static void WriteStrArray3D(BinaryWriter w, string[,,] arr)
    {
        int d0 = arr.GetLength(0);
        int d1 = arr.GetLength(1);
        int d2 = arr.GetLength(2);
        w.Write(d0);
        w.Write(d1);
        w.Write(d2);

        int emptySliceRun = 0;
        for (int i = 0; i < d0; i++)
        {
            if (IsEmptySlice3D(arr, i, d1, d2))
            {
                emptySliceRun++;
                continue;
            }

            if (emptySliceRun > 0)
            {
                w.Write(EbdbZeroA2);
                WriteEbdbInt(w, emptySliceRun);
                emptySliceRun = 0;
            }

            int emptyRowRun = 0;
            for (int j = 0; j < d1; j++)
            {
                if (IsEmptyRow3D(arr, i, j, d2))
                {
                    emptyRowRun++;
                    continue;
                }

                if (emptyRowRun > 0)
                {
                    w.Write(EbdbZeroA1);
                    WriteEbdbInt(w, emptyRowRun);
                    emptyRowRun = 0;
                }

                int emptyRun = 0;
                for (int k = 0; k < d2; k++)
                {
                    string s = arr[i, j, k] ?? string.Empty;
                    if (s.Length == 0)
                    {
                        emptyRun++;
                    }
                    else
                    {
                        if (emptyRun > 0)
                        {
                            w.Write(EbdbZero);
                            WriteEbdbInt(w, emptyRun);
                            emptyRun = 0;
                        }

                        WriteEbdbString(w, s);
                    }
                }

                w.Write(EbdbEoA1);
            }

            w.Write(EbdbEoA2);
        }

        w.Write(EbdbEoD);
    }

    private static string[,,] ReadStrArray3D(BinaryReader r)
    {
        int d0 = r.ReadInt32();
        int d1 = r.ReadInt32();
        int d2 = r.ReadInt32();
        if (d0 < 0 || d1 < 0 || d2 < 0)
        {
            throw new InvalidDataException($"Invalid StrArray3D dimensions: {d0}x{d1}x{d2}.");
        }

        string[,,] arr = new string[d0, d1, d2];
        for (int a = 0; a < d0; a++)
        {
            for (int b = 0; b < d1; b++)
            {
                for (int c = 0; c < d2; c++)
                {
                    arr[a, b, c] = string.Empty;
                }
            }
        }

        int i = 0, j = 0, k = 0;
        while (true)
        {
            byte token = r.ReadByte();
            if (token == EbdbEoD)
            {
                break;
            }

            if (token == EbdbEoA2)
            {
                i++;
                j = 0;
                k = 0;
            }
            else if (token == EbdbZeroA2)
            {
                i += checked((int)ReadEbdbInt(r));
                j = 0;
                k = 0;
            }
            else if (token == EbdbEoA1)
            {
                j++;
                k = 0;
            }
            else if (token == EbdbZeroA1)
            {
                j += checked((int)ReadEbdbInt(r));
                k = 0;
            }
            else if (token == EbdbZero)
            {
                k += checked((int)ReadEbdbInt(r));
            }
            else if (token == EbdbString)
            {
                string s = r.ReadString();
                if ((uint)i < (uint)d0 && (uint)j < (uint)d1 && (uint)k < (uint)d2)
                {
                    arr[i, j, k] = s;
                }

                k++;
            }
            else
            {
                throw new InvalidDataException($"Unexpected token 0x{token:X2} in StrArray3D.");
            }
        }

        return arr;
    }

    private static void WriteMap(BinaryWriter w, Dictionary<string, string> map)
    {
        w.Write(map.Count);
        foreach ((string k, string v) in map)
        {
            w.Write(k ?? string.Empty);
            w.Write(v ?? string.Empty);
        }
    }

    private static Dictionary<string, string> ReadMap(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException($"Invalid Map entry count: {count}.");
        }

        var map = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string k = r.ReadString();
            string v = r.ReadString();
            map[k] = v;
        }

        return map;
    }

    private static void WriteDataTable(BinaryWriter w, string key, EraDataTableSnapshot table)
    {
        // Upstream Emuera EM/EE writes DataTable as two raw UTF-16LE strings: WriteXmlSchema then WriteXml.
        using var dt = new DataTable(string.IsNullOrWhiteSpace(key) ? "Table" : key)
        {
            Locale = CultureInfo.InvariantCulture,
        };

        foreach (EraDataColumnDefinition col in table.Columns)
        {
            Type colType = ResolveDataTableColumnType(col.TypeName);
            DataColumn dc = dt.Columns.Add(col.Name, colType);
            dc.AllowDBNull = true;
        }

        foreach (object?[] rowCells in table.Rows)
        {
            DataRow dr = dt.NewRow();
            for (int c = 0; c < table.Columns.Count; c++)
            {
                object? cell = c < rowCells.Length ? rowCells[c] : null;
                if (cell is null)
                {
                    dr[c] = DBNull.Value;
                }
                else if (dt.Columns[c].DataType == typeof(string))
                {
                    dr[c] = cell.ToString() ?? string.Empty;
                }
                else
                {
                    dr[c] = Convert.ChangeType(cell, dt.Columns[c].DataType, CultureInfo.InvariantCulture);
                }
            }

            dt.Rows.Add(dr);
        }

        var sb = new StringBuilder();
        using var sw = new StringWriter(sb, CultureInfo.InvariantCulture);
        dt.WriteXmlSchema(sw);
        w.Write(sb.ToString());
        sb.Clear();
        dt.WriteXml(sw);
        w.Write(sb.ToString());
    }

    private static EraDataTableSnapshot ReadDataTable(BinaryReader r)
    {
        string schemaXml = r.ReadString();
        string dataXml = r.ReadString();

        using var dt = new DataTable
        {
            Locale = CultureInfo.InvariantCulture,
        };

        if (!string.IsNullOrWhiteSpace(schemaXml))
        {
            using var srSchema = new StringReader(schemaXml);
            dt.ReadXmlSchema(srSchema);
        }

        if (!string.IsNullOrWhiteSpace(dataXml))
        {
            using var srData = new StringReader(dataXml);
            dt.ReadXml(srData);
        }

        var columns = new List<EraDataColumnDefinition>(dt.Columns.Count);
        for (int c = 0; c < dt.Columns.Count; c++)
        {
            DataColumn dc = dt.Columns[c];
            columns.Add(new EraDataColumnDefinition(dc.ColumnName, FormatDataTableColumnType(dc.DataType)));
        }

        var rows = new List<object?[]>(dt.Rows.Count);
        for (int rowIdx = 0; rowIdx < dt.Rows.Count; rowIdx++)
        {
            DataRow dr = dt.Rows[rowIdx];
            object?[] cells = new object?[dt.Columns.Count];
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                object val = dr[c];
                if (val == DBNull.Value || val is null)
                {
                    cells[c] = null;
                }
                else if (val is sbyte or short or int or long or byte or ushort or uint)
                {
                    cells[c] = Convert.ToInt64(val, CultureInfo.InvariantCulture);
                }
                else
                {
                    cells[c] = val.ToString() ?? string.Empty;
                }
            }

            rows.Add(cells);
        }

        return new EraDataTableSnapshot { Columns = columns, Rows = rows };
    }

    private static Type ResolveDataTableColumnType(string? typeName) => (typeName ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "int8" or "sbyte" or "1" => typeof(sbyte),
        "int16" or "short" or "2" => typeof(short),
        "int32" or "int" or "3" => typeof(int),
        "int64" or "long" or "4" => typeof(long),
        _ => typeof(string),
    };

    private static string FormatDataTableColumnType(Type type)
    {
        if (type == typeof(sbyte)) return "Int8";
        if (type == typeof(short)) return "Int16";
        if (type == typeof(int)) return "Int32";
        if (type == typeof(long)) return "Int64";
        return "String";
    }

    private static void WriteIntSpanRle(BinaryWriter w, ReadOnlySpan<long> values)
    {
        int zeroRun = 0;
        for (int i = 0; i < values.Length; i++)
        {
            long v = values[i];
            if (v == 0)
            {
                zeroRun++;
            }
            else
            {
                if (zeroRun > 0)
                {
                    w.Write(EbdbZero);
                    WriteEbdbInt(w, zeroRun);
                    zeroRun = 0;
                }

                WriteEbdbInt(w, v);
            }
        }
    }

    private static long ReadEbdbIntFromToken(BinaryReader r, byte token) => token switch
    {
        <= EbdbByteMax => token,
        EbdbInt16 => r.ReadInt16(),
        EbdbInt32 => r.ReadInt32(),
        EbdbInt64 => r.ReadInt64(),
        _ => throw new InvalidDataException($"Unexpected Ebdb integer token: 0x{token:X2}"),
    };

    private static bool IsZeroRow(long[,] arr, int row, int d1)
    {
        for (int j = 0; j < d1; j++)
        {
            if (arr[row, j] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsZeroRow3D(long[,,] arr, int i, int j, int d2)
    {
        for (int k = 0; k < d2; k++)
        {
            if (arr[i, j, k] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsZeroSlice(long[,,] arr, int i, int d1, int d2)
    {
        for (int j = 0; j < d1; j++)
        {
            if (!IsZeroRow3D(arr, i, j, d2))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptyRow(string[,] arr, int row, int d1)
    {
        for (int j = 0; j < d1; j++)
        {
            if (!string.IsNullOrEmpty(arr[row, j]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptyRow3D(string[,,] arr, int i, int j, int d2)
    {
        for (int k = 0; k < d2; k++)
        {
            if (!string.IsNullOrEmpty(arr[i, j, k]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptySlice3D(string[,,] arr, int i, int d1, int d2)
    {
        for (int j = 0; j < d1; j++)
        {
            if (!IsEmptyRow3D(arr, i, j, d2))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Legacy Text .sav / global.sav Serialization

    private static void SaveText(EraSaveSnapshot snapshot, Stream output)
    {
        using var w = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        w.NewLine = "\r\n";

        w.WriteLine(snapshot.Header.ScriptUniqueCode.ToString(CultureInfo.InvariantCulture));
        w.WriteLine(snapshot.Header.ScriptVersion.ToString(CultureInfo.InvariantCulture));
        w.WriteLine(EscapeTextLine(snapshot.Header.SaveDataText));

        if (snapshot.Header.FileType == EraSaveFileType.Global)
        {
            w.WriteLine(Emu1808Start);
            WriteTextExtendedVariables(w, snapshot.Variables);
            w.WriteLine(EmuSeparator);
            WriteTextExtendedVariables(w, snapshot.VarExtVariables);
            w.WriteLine(Finisher);
            w.Flush();
            return;
        }

        w.WriteLine(snapshot.Characters.Count.ToString(CultureInfo.InvariantCulture));
        foreach (EraCharacterSaveData chara in snapshot.Characters)
        {
            w.WriteLine(chara.No.ToString(CultureInfo.InvariantCulture));
            w.WriteLine(EscapeTextLine(chara.Name));
            w.WriteLine(EscapeTextLine(chara.CallName));
            w.WriteLine(EscapeTextLine(chara.NickName));
            w.WriteLine(EscapeTextLine(chara.MasterName));
            w.WriteLine(chara.IsAssi.ToString(CultureInfo.InvariantCulture));
            WriteTextExtendedVariables(w, chara.BuiltinVariables);
            w.WriteLine(EmuSeparator);
            WriteTextExtendedVariables(w, chara.UserVariables);
            w.WriteLine(Finisher);
        }

        w.WriteLine(Emu1808Start);
        WriteTextExtendedVariables(w, snapshot.Variables);
        w.WriteLine(EmuSeparator);
        WriteTextExtendedVariables(w, snapshot.VarExtVariables);
        w.WriteLine(Finisher);
        w.Flush();
    }

    private static EraSaveSnapshot LoadText(string text)
    {
        using var sr = new StringReader(text);
        long uniqueCode = ReadLineLong(sr);
        long scriptVersion = ReadLineLong(sr);
        string saveText = UnescapeTextLine(sr.ReadLine() ?? string.Empty);

        string? fourthLine = sr.ReadLine();
        var characters = new List<EraCharacterSaveData>();
        var variables = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase);
        var varExt = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase);
        EraSaveFileType fileType = EraSaveFileType.Normal;

        if (fourthLine is not null && IsEmueraStartMarker(fourthLine))
        {
            // Global save text layout
            fileType = EraSaveFileType.Global;
            ReadTextExtendedSection(sr, variables, stopOnSeparator: true);
            ReadTextExtendedSection(sr, varExt, stopOnSeparator: false);
        }
        else if (fourthLine is not null && long.TryParse(fourthLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long charaCount))
        {
            for (long i = 0; i < charaCount; i++)
            {
                var chara = new EraCharacterSaveData
                {
                    No = ReadLineLong(sr),
                    Name = UnescapeTextLine(sr.ReadLine() ?? string.Empty),
                    CallName = UnescapeTextLine(sr.ReadLine() ?? string.Empty),
                    NickName = UnescapeTextLine(sr.ReadLine() ?? string.Empty),
                    MasterName = UnescapeTextLine(sr.ReadLine() ?? string.Empty),
                    IsAssi = ReadLineLong(sr),
                };

                ReadTextExtendedSection(sr, chara.BuiltinVariables, stopOnSeparator: true);
                ReadTextExtendedSection(sr, chara.UserVariables, stopOnSeparator: false);
                characters.Add(chara);
            }

            // Advance to Emuera start marker if present
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (IsEmueraStartMarker(line))
                {
                    ReadTextExtendedSection(sr, variables, stopOnSeparator: true);
                    ReadTextExtendedSection(sr, varExt, stopOnSeparator: false);
                    break;
                }
            }
        }

        return new EraSaveSnapshot
        {
            Header = new EraSaveHeader(uniqueCode, scriptVersion, saveText, fileType, CurrentBinaryVersion, EraSaveFormat.Text),
            Characters = characters,
            Variables = variables,
            VarExtVariables = varExt,
        };
    }

    private static bool IsEmueraStartMarker(string line) =>
        line is Emu1808Start or Emu1803Start or Emu1729Start or Emu1708Start or Emu1700Start;

    private static void WriteTextExtendedVariables(StreamWriter w, Dictionary<string, EraSaveValue> vars)
    {
        foreach ((string key, EraSaveValue val) in vars)
        {
            switch (val.DataType)
            {
                case EraSaveDataType.Int:
                    w.WriteLine($"I:{key}:{val.AsInt().ToString(CultureInfo.InvariantCulture)}");
                    break;
                case EraSaveDataType.Str:
                    w.WriteLine($"S:{key}:{EscapeTextLine(val.AsStr())}");
                    break;
                case EraSaveDataType.IntArray:
                {
                    long[] arr = val.AsIntArray();
                    w.WriteLine($"IA:{key}:{arr.Length}");
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i] != 0)
                        {
                            w.WriteLine($"{i}:{arr[i].ToString(CultureInfo.InvariantCulture)}");
                        }
                    }

                    w.WriteLine(Finisher);
                    break;
                }
                case EraSaveDataType.StrArray:
                {
                    string[] arr = val.AsStrArray();
                    w.WriteLine($"SA:{key}:{arr.Length}");
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(arr[i]))
                        {
                            w.WriteLine($"{i}:{EscapeTextLine(arr[i])}");
                        }
                    }

                    w.WriteLine(Finisher);
                    break;
                }
                case EraSaveDataType.IntArray2D:
                {
                    long[,] arr = val.AsIntArray2D();
                    int d0 = arr.GetLength(0);
                    int d1 = arr.GetLength(1);
                    w.WriteLine($"IA2:{key}:{d0},{d1}");
                    for (int i = 0; i < d0; i++)
                    {
                        for (int j = 0; j < d1; j++)
                        {
                            if (arr[i, j] != 0)
                            {
                                w.WriteLine($"{i},{j}:{arr[i, j].ToString(CultureInfo.InvariantCulture)}");
                            }
                        }
                    }

                    w.WriteLine(Finisher);
                    break;
                }
                case EraSaveDataType.Map:
                {
                    Dictionary<string, string> map = val.AsMap();
                    w.WriteLine($"MAP:{key}:{map.Count}");
                    foreach ((string mk, string mv) in map)
                    {
                        w.WriteLine($"{EscapeTextLine(mk)}\t{EscapeTextLine(mv)}");
                    }

                    w.WriteLine(Finisher);
                    break;
                }
                case EraSaveDataType.Xml:
                    w.WriteLine($"XML:{key}:{EscapeTextLine(val.AsXml())}");
                    break;
            }
        }
    }

    private static void ReadTextExtendedSection(
        StringReader sr,
        Dictionary<string, EraSaveValue> target,
        bool stopOnSeparator)
    {
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line == Finisher)
            {
                break;
            }

            if (stopOnSeparator && line == EmuSeparator)
            {
                break;
            }

            int firstColon = line.IndexOf(':');
            if (firstColon <= 0)
            {
                continue;
            }

            int secondColon = line.IndexOf(':', firstColon + 1);
            if (secondColon <= firstColon)
            {
                continue;
            }

            string kind = line[..firstColon];
            string key = line[(firstColon + 1)..secondColon];
            string rest = line[(secondColon + 1)..];

            switch (kind)
            {
                case "I":
                    if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                    {
                        target[key] = EraSaveValue.FromInt(iv);
                    }
                    break;
                case "S":
                    target[key] = EraSaveValue.FromStr(UnescapeTextLine(rest));
                    break;
                case "IA":
                {
                    int len = int.Parse(rest, CultureInfo.InvariantCulture);
                    long[] arr = new long[len];
                    string? itemLine;
                    while ((itemLine = sr.ReadLine()) is not null && itemLine != Finisher)
                    {
                        int sep = itemLine.IndexOf(':');
                        if (sep > 0 &&
                            int.TryParse(itemLine[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                            long.TryParse(itemLine[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) &&
                            (uint)idx < (uint)arr.Length)
                        {
                            arr[idx] = v;
                        }
                    }

                    target[key] = EraSaveValue.FromIntArray(arr);
                    break;
                }
                case "SA":
                {
                    int len = int.Parse(rest, CultureInfo.InvariantCulture);
                    string[] arr = new string[len];
                    Array.Fill(arr, string.Empty);
                    string? itemLine;
                    while ((itemLine = sr.ReadLine()) is not null && itemLine != Finisher)
                    {
                        int sep = itemLine.IndexOf(':');
                        if (sep > 0 &&
                            int.TryParse(itemLine[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                            (uint)idx < (uint)arr.Length)
                        {
                            arr[idx] = UnescapeTextLine(itemLine[(sep + 1)..]);
                        }
                    }

                    target[key] = EraSaveValue.FromStrArray(arr);
                    break;
                }
                case "IA2":
                {
                    string[] dims = rest.Split(',');
                    int d0 = int.Parse(dims[0], CultureInfo.InvariantCulture);
                    int d1 = int.Parse(dims[1], CultureInfo.InvariantCulture);
                    long[,] arr = new long[d0, d1];
                    string? itemLine;
                    while ((itemLine = sr.ReadLine()) is not null && itemLine != Finisher)
                    {
                        int sep = itemLine.IndexOf(':');
                        if (sep > 0)
                        {
                            string[] coords = itemLine[..sep].Split(',');
                            if (coords.Length == 2 &&
                                int.TryParse(coords[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) &&
                                int.TryParse(coords[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int j) &&
                                long.TryParse(itemLine[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) &&
                                (uint)i < (uint)d0 && (uint)j < (uint)d1)
                            {
                                arr[i, j] = v;
                            }
                        }
                    }

                    target[key] = EraSaveValue.FromIntArray2D(arr);
                    break;
                }
                case "MAP":
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    string? itemLine;
                    while ((itemLine = sr.ReadLine()) is not null && itemLine != Finisher)
                    {
                        int tab = itemLine.IndexOf('\t');
                        if (tab >= 0)
                        {
                            map[UnescapeTextLine(itemLine[..tab])] = UnescapeTextLine(itemLine[(tab + 1)..]);
                        }
                    }

                    target[key] = EraSaveValue.FromMap(map);
                    break;
                }
                case "XML":
                    target[key] = EraSaveValue.FromXml(UnescapeTextLine(rest));
                    break;
            }
        }
    }

    private static long ReadLineLong(StringReader sr)
    {
        string? line = sr.ReadLine();
        return line is not null && long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0L;
    }

    private static string EscapeTextLine(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string UnescapeTextLine(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\'))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                sb.Append(next switch
                {
                    'r' => '\r',
                    'n' => '\n',
                    '\\' => '\\',
                    _ => next,
                });
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    #endregion
}
