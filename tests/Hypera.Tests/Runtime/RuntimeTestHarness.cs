using System.Collections.Immutable;
using System.Text;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Runtime;
using Hypera.VirtualFs;

namespace Hypera.Tests.Runtime;

internal sealed class MemoryTestVfs : IVirtualFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string RootDescription => "memory://runtime-tests";

    public void AddFile(string virtualPath, string utf8Content) =>
        _files[Normalize(virtualPath)] = Encoding.UTF8.GetBytes(utf8Content);

    public void AddFile(string virtualPath, byte[] bytes) =>
        _files[Normalize(virtualPath)] = bytes;

    public bool FileExists(string virtualPath) =>
        _files.ContainsKey(Normalize(virtualPath));

    public bool DirectoryExists(string virtualPath)
    {
        string prefix = Normalize(virtualPath).TrimEnd('/');
        if (prefix.Length == 0)
        {
            return true;
        }

        prefix += "/";
        foreach (string key in _files.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Stream OpenRead(string virtualPath) =>
        new MemoryStream(ReadAllBytes(virtualPath), writable: false);

    public byte[] ReadAllBytes(string virtualPath) =>
        _files.TryGetValue(Normalize(virtualPath), out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException($"Missing VFS file: {virtualPath}");

    public string ReadAllText(string virtualPath, Encoding? overrideEncoding = null) =>
        (overrideEncoding ?? Encoding.UTF8).GetString(ReadAllBytes(virtualPath));

    public IEnumerable<string> EnumerateFiles(
        string directoryVirtualPath = "",
        string searchPattern = "*",
        bool recursive = true)
    {
        string prefix = Normalize(directoryVirtualPath).Trim('/');
        foreach (string key in _files.Keys)
        {
            if (prefix.Length > 0 && !key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!recursive && prefix.Length > 0)
            {
                string rest = key[(prefix.Length + 1)..];
                if (rest.Contains('/'))
                {
                    continue;
                }
            }

            yield return key;
        }
    }

    public void Dispose()
    {
    }

    private static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
}

internal sealed class TestModuleBuilder
{
    private static readonly VariableStore SharedSlotCatalog = new(BytecodeModule.Empty);

    private readonly List<Instruction> _instructions = [];
    private readonly List<long> _intConstants = [];
    private readonly List<string> _strConstants = [];
    private readonly List<FunctionDescriptor> _functions = [];
    private readonly List<EventFunctionGroup> _eventGroups = [];
    private readonly List<VariableSlotDescriptor> _staticVars = [];

    public CompilerOptions CompilerOptions { get; set; } = CompilerOptions.Default;
    public ReplaceConfiguration ReplaceConfig { get; set; } = ReplaceConfiguration.Default;

    public int CurrentPc => _instructions.Count;

    public int AddIntConst(long value)
    {
        int idx = _intConstants.IndexOf(value);
        if (idx >= 0)
        {
            return idx;
        }

        _intConstants.Add(value);
        return _intConstants.Count - 1;
    }

    public int AddStrConst(string value)
    {
        int idx = _strConstants.IndexOf(value);
        if (idx >= 0)
        {
            return idx;
        }

        _strConstants.Add(value);
        return _strConstants.Count - 1;
    }

    public int AddStaticVar(
        int slotId,
        string name,
        VariableValueKind valueKind,
        VariableScopeKind scope,
        params int[] dimensions)
    {
        _staticVars.Add(new VariableSlotDescriptor(
            slotId,
            name,
            valueKind,
            scope,
            dimensions.Length == 0 ? [1] : [.. dimensions],
            IsCharaData: scope == VariableScopeKind.CharacterDim,
            IsSavedata: scope == VariableScopeKind.GlobalDimSavedata));
        return slotId;
    }

    public int Emit(Instruction insn)
    {
        int pc = _instructions.Count;
        _instructions.Add(insn);
        return pc;
    }

    public void Patch(int pc, Instruction insn) => _instructions[pc] = insn;

    public int AddFunction(
        string name,
        Action<TestModuleBuilder> bodyEmitter,
        ushort intRegs = 16,
        ushort strRegs = 8,
        bool isMethod = false,
        VariableValueKind? returnKind = null,
        EventDirectiveFlags eventFlags = EventDirectiveFlags.None,
        ImmutableArray<FunctionParameterDescriptor>? parameters = null,
        ImmutableArray<VariableSlotDescriptor>? locals = null)
    {
        int funcId = _functions.Count;
        int entryPc = _instructions.Count;
        bodyEmitter(this);
        int count = _instructions.Count - entryPc;
        _functions.Add(new FunctionDescriptor(
            funcId,
            name,
            entryPc,
            count,
            intRegs,
            strRegs,
            isMethod,
            returnKind,
            eventFlags,
            parameters ?? [],
            locals ?? []));
        return funcId;
    }

    public void AddEventGroup(string eventName, params EventHandlerEntry[] entries) =>
        _eventGroups.Add(new EventFunctionGroup(eventName, [.. entries]));

    public BytecodeModule Build() =>
        new()
        {
            Header = BytecodeModuleHeader.Create(
                (uint)_instructions.Count,
                (uint)_functions.Count,
                (uint)_intConstants.Count,
                (uint)_strConstants.Count,
                (uint)_staticVars.Count),
            CompilerOptions = CompilerOptions,
            ReplaceConfig = ReplaceConfig,
            Instructions = [.. _instructions],
            IntConstants = [.. _intConstants],
            StringConstants = [.. _strConstants],
            Functions = [.. _functions],
            EventGroups = [.. _eventGroups],
            StaticVariables = [.. _staticVars],
        };

    public EmueraEngine BuildEngine(RuntimeOptions? options = null, MemoryTestVfs? vfs = null, GameData? gameData = null)
    {
        BytecodeModule module = Build();
        RuntimeOptions opts = options ?? RuntimeOptions.Default;
        var vars = new VariableStore(module);
        var engine = new EmueraEngine(module, vfs ?? new MemoryTestVfs(), opts, vars, vars.Characters);
        if (gameData is not null)
        {
            engine.ApplyGameData(gameData);
        }

        return engine;
    }

    public static int BuiltinSlot(string name) =>
        SharedSlotCatalog.TryResolveSlot(name, out int slotId)
            ? slotId
            : throw new KeyNotFoundException($"Unknown builtin variable '{name}'.");
}
