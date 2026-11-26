using System.Buffers.Binary;
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
        Span<byte> magicBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(magicBytes, compressed ? ZipHeaderMagic : BinaryHeaderMagic);
        output.Write(magicBytes);

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
        using var w = new BinaryWriter(targetStream, Encoding.UTF8, leaveOpen: true);

        w.Write(snapshot.Header.BinaryVersion);
        w.Write(0u); // DataCount reserved (0 in upstream)
        w.Write((byte)snapshot.Header.FileType);

        WriteEbdbInt(w, snapshot.Header.ScriptUniqueCode);
        WriteEbdbInt(w, snapshot.Header.ScriptVersion);
        WriteEbdbString(w, snapshot.Header.SaveDataText);

        if (snapshot.Header.FileType is EraSaveFileType.Normal or EraSaveFileType.CharVar)
        {
            WriteEbdbInt(w, snapshot.Characters.Count);
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
        foreach ((string key, EraSaveValue value) in snapshot.VarExtVariables)
        {
            WriteWithKey(w, key, value);
        }

        w.Write((byte)EraSaveDataType.EndOfFile);
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

    private static EraSaveHeader PeekBinaryHeader(Stream input, bool compressed)
    {
        input.Position = 8; // skip 8-byte magic
        using Stream payloadStream = compressed
            ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true)
            : input;
        using var r = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: true);

        uint version = r.ReadUInt32();
        _ = r.ReadUInt32(); // dataCount
        var fileType = (EraSaveFileType)r.ReadByte();
        long uniqueCode = ReadEbdbInt(r);
        long scriptVersion = ReadEbdbInt(r);
        string saveText = ReadEbdbString(r);

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
        input.Position = 8; // skip 8-byte magic
        using Stream payloadStream = compressed
            ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true)
            : input;
        using var r = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: true);

        uint version = r.ReadUInt32();
        _ = r.ReadUInt32(); // DataCount
        var fileType = (EraSaveFileType)r.ReadByte();
        long uniqueCode = ReadEbdbInt(r);
        long scriptVersion = ReadEbdbInt(r);
        string saveText = ReadEbdbString(r);

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
            long charaCount = ReadEbdbInt(r);
            for (long i = 0; i < charaCount; i++)
            {
                characters.Add(ReadCharacterBinary(r));
            }
        }

        var variables = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase);
        while (TryReadKeyValue(r, out EraSaveDataType tag, out string key, out EraSaveValue? value))
        {
            if (tag == EraSaveDataType.EndOfFile)
            {
                break;
            }

            if (value is not null)
            {
                variables[key] = value;
            }
        }

        var varExt = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase);
        while (TryReadKeyValue(r, out EraSaveDataType tag, out string key, out EraSaveValue? value))
        {
            if (tag == EraSaveDataType.EndOfFile)
            {
                break;
            }

            if (value is not null)
            {
                varExt[key] = value;
            }
        }

        return new EraSaveSnapshot
        {
            Header = header,
            Characters = characters,
            Variables = variables,
            VarExtVariables = varExt,
        };
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
                WriteEbdbString(w, value.AsStr());
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
                WriteEbdbString(w, value.AsXml());
                break;
            case EraSaveDataType.DataTable:
                WriteDataTable(w, value.AsDataTable());
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
        EraSaveDataType.Str => EraSaveValue.FromStr(ReadEbdbString(r)),
        EraSaveDataType.StrArray => EraSaveValue.FromStrArray(ReadStrArray1D(r)),
        EraSaveDataType.StrArray2D => EraSaveValue.FromStrArray2D(ReadStrArray2D(r)),
        EraSaveDataType.StrArray3D => EraSaveValue.FromStrArray3D(ReadStrArray3D(r)),
        EraSaveDataType.Map => EraSaveValue.FromMap(ReadMap(r)),
        EraSaveDataType.Xml => EraSaveValue.FromXml(ReadEbdbString(r)),
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

    private static string ReadEbdbString(BinaryReader r)
    {
        byte token = r.ReadByte();
        if (token != EbdbString)
        {
            throw new InvalidDataException($"Expected Ebdb String token 0xD8, got 0x{token:X2}.");
        }

        return r.ReadString();
    }

    private static void WriteIntArray1D(BinaryWriter w, long[] arr)
    {
        WriteEbdbInt(w, arr.Length);
        WriteIntSpanRle(w, arr);
        w.Write(EbdbEoD);
    }

    private static long[] ReadIntArray1D(BinaryReader r)
    {
        int len = checked((int)ReadEbdbInt(r));
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
        WriteEbdbInt(w, d0);
        WriteEbdbInt(w, d1);

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
        int d0 = checked((int)ReadEbdbInt(r));
        int d1 = checked((int)ReadEbdbInt(r));
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
        WriteEbdbInt(w, d0);
        WriteEbdbInt(w, d1);
        WriteEbdbInt(w, d2);

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
        int d0 = checked((int)ReadEbdbInt(r));
        int d1 = checked((int)ReadEbdbInt(r));
        int d2 = checked((int)ReadEbdbInt(r));
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
        WriteEbdbInt(w, arr.Length);
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
        int len = checked((int)ReadEbdbInt(r));
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
        WriteEbdbInt(w, d0);
        WriteEbdbInt(w, d1);

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
        int d0 = checked((int)ReadEbdbInt(r));
        int d1 = checked((int)ReadEbdbInt(r));
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
        WriteEbdbInt(w, d0);
        WriteEbdbInt(w, d1);
        WriteEbdbInt(w, d2);

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
        int d0 = checked((int)ReadEbdbInt(r));
        int d1 = checked((int)ReadEbdbInt(r));
        int d2 = checked((int)ReadEbdbInt(r));
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
        WriteEbdbInt(w, map.Count);
        foreach ((string k, string v) in map)
        {
            WriteEbdbString(w, k);
            WriteEbdbString(w, v);
        }
    }

    private static Dictionary<string, string> ReadMap(BinaryReader r)
    {
        int count = checked((int)ReadEbdbInt(r));
        var map = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string k = ReadEbdbString(r);
            string v = ReadEbdbString(r);
            map[k] = v;
        }

        return map;
    }

    private static void WriteDataTable(BinaryWriter w, EraDataTableSnapshot table)
    {
        WriteEbdbInt(w, table.Columns.Count);
        foreach (EraDataColumnDefinition col in table.Columns)
        {
            WriteEbdbString(w, col.Name);
            WriteEbdbString(w, col.TypeName);
        }

        WriteEbdbInt(w, table.Rows.Count);
        foreach (object?[] row in table.Rows)
        {
            for (int c = 0; c < table.Columns.Count; c++)
            {
                object? cell = c < row.Length ? row[c] : null;
                if (cell is null)
                {
                    w.Write((byte)0);
                }
                else if (cell is long l)
                {
                    w.Write((byte)1);
                    WriteEbdbInt(w, l);
                }
                else if (cell is int iv)
                {
                    w.Write((byte)1);
                    WriteEbdbInt(w, iv);
                }
                else
                {
                    w.Write((byte)2);
                    WriteEbdbString(w, cell.ToString() ?? string.Empty);
                }
            }
        }
    }

    private static EraDataTableSnapshot ReadDataTable(BinaryReader r)
    {
        int colCount = checked((int)ReadEbdbInt(r));
        var columns = new List<EraDataColumnDefinition>(colCount);
        for (int i = 0; i < colCount; i++)
        {
            string name = ReadEbdbString(r);
            string typeName = ReadEbdbString(r);
            columns.Add(new EraDataColumnDefinition(name, typeName));
        }

        int rowCount = checked((int)ReadEbdbInt(r));
        var rows = new List<object?[]>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            object?[] row = new object?[colCount];
            for (int c = 0; c < colCount; c++)
            {
                byte cellKind = r.ReadByte();
                row[c] = cellKind switch
                {
                    0 => null,
                    1 => ReadEbdbInt(r),
                    2 => ReadEbdbString(r),
                    _ => throw new InvalidDataException($"Unknown DataTable cell tag: {cellKind}"),
                };
            }

            rows.Add(row);
        }

        return new EraDataTableSnapshot { Columns = columns, Rows = rows };
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
