using System.Globalization;
using System.Reflection;
using System.Text;
using Hypera.Audio;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Hypera.Rendering;
using Hypera.VirtualFs;

namespace Hypera.Runtime;

/// <summary>
/// Tracks pending script-level input validation metadata across stackless <see cref="OpCode.WaitInput"/> suspensions.
/// </summary>
internal sealed record PendingScriptInputMeta(
    InputRequestKind Kind,
    WaitInputFlags Flags,
    bool HasDefault,
    long DefaultInt,
    string DefaultStr,
    int? TargetRegister);

/// <summary>
/// Fully instance-isolated Hypera execution engine (zero static mutable game state).
/// Multiple <see cref="EmueraEngine"/> instances can run concurrently in the same process.
/// </summary>
public sealed class EmueraEngine : IDisposable
{
    private readonly List<CallFrame> _callStack = [];
    private readonly List<EventDispatchCursor> _eventStack = [];
    private readonly List<DisplayOperation> _displayOperations = [];
    private readonly List<ButtonDescriptor> _currentCycleButtons = [];
    private readonly Dictionary<string, int> _functionsByName;
    private readonly Dictionary<string, EventFunctionGroup> _eventGroupsByName;

    private long _frameSequence;
    private long _inputGeneration;
    private long _buttonGeneration = 1;
    private PendingScriptInputMeta? _pendingScriptInputMeta;
    private WritableSaveDirectory? _saveDirectory;
    private EraSaveSnapshot? _memoryGlobalSnapshot;
    private bool _disposed;

    public EmueraEngine(
        BytecodeModule module,
        IVirtualFileSystem fileSystem,
        RuntimeOptions? options = null,
        IVariableStore? variables = null,
        ICharacterStore? characters = null,
        IAudioBackend? audio = null,
        IDisplaySnapshotSink? displaySink = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(fileSystem);

        Module = module;
        FileSystem = fileSystem;
        Options = options ?? RuntimeOptions.Default;

        CharacterStore? concreteChars = characters as CharacterStore;
        VariableStore? concreteVars = variables as VariableStore;

        if (variables is null && characters is null)
        {
            concreteChars = new CharacterStore(module.CompilerOptions, module.ReplaceConfig);
            concreteVars = new VariableStore(module, concreteChars);
            Variables = concreteVars;
            Characters = concreteChars;
        }
        else if (variables is null && concreteChars is not null)
        {
            concreteVars = new VariableStore(module, concreteChars);
            Variables = concreteVars;
            Characters = concreteChars;
        }
        else if (concreteVars is not null && characters is null)
        {
            Variables = concreteVars;
            Characters = concreteVars.Characters;
            concreteChars = concreteVars.Characters;
        }
        else
        {
            Variables = variables;
            Characters = characters;
        }

        FastVariables = concreteVars;
        FastCharacters = concreteChars;

        if (concreteVars is not null)
        {
            concreteVars.CurrentFileProvider = ResolveCurrentSourceFile;
            concreteVars.CurrentLineProvider = ResolveCurrentSourceLine;
            concreteVars.CurrentFunctionProvider = () => _callStack.Count > 0 ? _callStack[^1].FunctionName : string.Empty;
        }

        Audio = audio ?? new NullAudioBackend();
        DisplaySink = displaySink;
        Viewport = new LogicalViewport(
            Options.WindowX,
            Options.WindowY,
            Options.FontSize,
            Options.LineHeight);

        CurrentForeColorRgba = Options.ForeColorRgba;
        CurrentBackColorRgba = Options.BackColorRgba;
        CurrentFontName = Options.FontName;

        Fonts = new FontRegistry();
        Fonts.RegisterFromVfs(fileSystem);
        Glyphs = new GlyphMeasurer(Fonts);
        Sprites = new SpriteRegistry();
        Sprites.LoadResourcesFromVfs(fileSystem);
        Graphics = new GraphicsManager(Fonts, Glyphs, defaultFontName: Options.FontName, defaultFontSize: Options.FontSize);
        ConsoleBuffer = new VirtualConsoleBuffer(
            Viewport,
            Glyphs,
            Sprites,
            Options.FontName,
            Options.ForeColorRgba,
            Options.BackColorRgba,
            Options.FocusColorRgba,
            Options.MaxLog);
        SystemFlow = new SystemFlowController(this);

        StringComparer funcCmp = (module.CompilerOptions.IgnoreCase && !module.CompilerOptions.CompatiFunctionNoIgnoreCase)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        _functionsByName = new Dictionary<string, int>(funcCmp);
        for (int i = 0; i < module.Functions.Length; i++)
        {
            FunctionDescriptor fn = module.Functions[i];
            if (fn.EventFlags == EventDirectiveFlags.None)
            {
                _functionsByName.TryAdd(fn.Name, i);
            }
        }

        // Fallback: allow lookup of any function by name if not already mapped
        for (int i = 0; i < module.Functions.Length; i++)
        {
            _functionsByName.TryAdd(module.Functions[i].Name, i);
        }

        _eventGroupsByName = new Dictionary<string, EventFunctionGroup>(funcCmp);
        foreach (EventFunctionGroup grp in module.EventGroups)
        {
            _eventGroupsByName[grp.EventName] = grp;
        }
    }

    public BytecodeModule Module { get; }
    public IVirtualFileSystem FileSystem { get; }
    public RuntimeOptions Options { get; }
    public IVariableStore? Variables { get; }
    public ICharacterStore? Characters { get; }
    public IAudioBackend Audio { get; }
    public IDisplaySnapshotSink? DisplaySink { get; }
    public LogicalViewport Viewport { get; private set; }

    public VariableStore? FastVariables { get; }
    public CharacterStore? FastCharacters { get; }
    public SystemFlowController SystemFlow { get; }
    public IDynamicEvaluator? DynamicEvaluator { get; set; }
    public ISharpPluginHost? SharpPluginHost { get; set; }
    public WritableSaveDirectory? SaveDirectory => _saveDirectory;
    internal bool OwnsFileSystem { get; set; }

    public FontRegistry Fonts { get; }
    public GlyphMeasurer Glyphs { get; }
    public SpriteRegistry Sprites { get; }
    public GraphicsManager Graphics { get; }
    public VirtualConsoleBuffer ConsoleBuffer { get; }

    public static EmueraEngine LoadFromDirectory(string gameDirectory, HyperaGameLoadOptions? options = null)
    {
        HyperaGameHost host = HyperaGameHost.LoadFromDirectory(gameDirectory, options);
        host.Engine.OwnsFileSystem = true;
        return host.Engine;
    }

    public static EmueraEngine LoadFromHepArchive(string hepFilePath, HyperaGameLoadOptions? options = null)
    {
        HyperaGameHost host = HyperaGameHost.LoadFromHepArchive(hepFilePath, options);
        host.Engine.OwnsFileSystem = true;
        return host.Engine;
    }

    public static EmueraEngine LoadFromZipArchive(string zipFilePath, HyperaGameLoadOptions? options = null)
    {
        HyperaGameHost host = HyperaGameHost.LoadFromZipArchive(zipFilePath, options);
        host.Engine.OwnsFileSystem = true;
        return host.Engine;
    }

    public static EmueraEngine LoadFromVfs(
        IVirtualFileSystem vfs,
        HyperaGameLoadOptions? options = null,
        BytecodeModule? preloadedModule = null) =>
        HyperaGameHost.LoadFromVfs(vfs, ownsVfs: false, options, preloadedModule).Engine;

    public EngineStepStatus Status { get; private set; } = EngineStepStatus.Running;
    public InputRequest? PendingInputRequest { get; private set; }
    public Exception? FaultException { get; private set; }
    public IReadOnlyList<CallFrame> CallStack => _callStack;

    public long LastAwaitMilliseconds { get; internal set; }
    public bool RestartRequested { get; private set; }
    public bool IsSkipDisplay { get; internal set; }
    public int CurrentRedrawMode { get; internal set; } = 1;
    public uint CurrentForeColorRgba { get; private set; }
    public uint CurrentBackColorRgba { get; private set; }
    public int CurrentFontStyle { get; internal set; }
    public string CurrentFontName { get; internal set; }
    public string CurrentAlignmentName { get; private set; } = "LEFT";

    internal List<CallFrame> InternalCallStack => _callStack;
    internal bool HasActiveFrames => _callStack.Count > 0;
    internal IDynamicEvaluator ActiveDynamicEvaluator => DynamicEvaluator ?? DefaultRuntimeDynamicEvaluator.Instance;

    public void ConfigureSaveDirectory(WritableSaveDirectory saveDirectory)
    {
        ArgumentNullException.ThrowIfNull(saveDirectory);
        _saveDirectory = saveDirectory;
    }

    public void ApplyGameData(GameData gameData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireVariableStore().ApplyGameData(gameData);
    }

    /// <summary>
    /// Starts the standard Era game system flow (<c>BEGIN TITLE</c> or custom initial phase).
    /// </summary>
    public void StartSystemFlow(SystemPhaseKind initialPhase = SystemPhaseKind.Title)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SystemFlow.BeginPhase(initialPhase, isForceBegin: true);
        Status = EngineStepStatus.Running;
    }

    public void PushEntryFrame(FunctionDescriptor entryFunction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entryFunction);

        _callStack.Add(CreateActivationFrame(entryFunction, callerReturnReg: 0, expectsStrReturn: false, discardReturnReg: true));
        Status = EngineStepStatus.Running;
    }

    public EngineStepStatus Step(int maxInstructions = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Status is EngineStepStatus.WaitingInput or EngineStepStatus.Completed or EngineStepStatus.Faulted)
        {
            return Status;
        }

        int budget = maxInstructions > 0 ? maxInstructions : Options.DefaultStepInstructionBudget;
        Status = EngineStepStatus.Running;

        try
        {
            EngineStepStatus nextStatus = VmDispatcher.ExecuteStep(this, budget);
            Status = nextStatus;
            return Status;
        }
        catch (Exception ex)
        {
            FaultException = ex;
            Status = EngineStepStatus.Faulted;
            return Status;
        }
    }

    public void SubmitInput(InputResponse response)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(response);

        if (Status != EngineStepStatus.WaitingInput || PendingInputRequest is null)
        {
            throw new InvalidOperationException("Engine is not currently awaiting input.");
        }

        if (response.GenerationId != PendingInputRequest.GenerationId)
        {
            return; // Ignore stale out-of-generation input events safely.
        }

        if (FastVariables is not null)
        {
            FastVariables.LastInputWasTimeout = response.IsTimeout;
            ApplyHitTestColorSlots(FastVariables, response.HitTest);
        }

        if (SystemFlow.IsAwaitingSystemInput)
        {
            PendingInputRequest = null;
            _pendingScriptInputMeta = null;
            AdvanceButtonGeneration();
            Status = EngineStepStatus.Running;
            SystemFlow.DeliverSystemInput(response);
            return;
        }

        if (_pendingScriptInputMeta is not null && !TryProcessScriptInput(response, _pendingScriptInputMeta))
        {
            // Invalid input (e.g. non-matching button in BINPUT/ONEBINPUT or empty non-numeric INPUT without default): stay in WaitingInput!
            return;
        }

        PendingInputRequest = null;
        _pendingScriptInputMeta = null;
        AdvanceButtonGeneration();
        Status = EngineStepStatus.Running;
    }

    public void AppendDisplayOperation(DisplayOperation operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _displayOperations.Add(operation);
    }

    public DisplaySnapshot CaptureSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new DisplaySnapshot(
            ++_frameSequence,
            Viewport,
            CurrentBackColorRgba,
            [.. _displayOperations],
            PendingInputRequest);
    }

    public IReadOnlyList<SourceLocation> GetSourceStackTrace()
    {
        var locations = new List<SourceLocation>(_callStack.Count);
        for (int i = _callStack.Count - 1; i >= 0; i--)
        {
            int pc = Math.Max(0, _callStack[i].ProgramCounter - 1);
            if (Module.SourceMap.TryLookup(pc, out SourceLocation loc))
            {
                locations.Add(loc);
            }
        }

        return locations;
    }

    // -------------------------------------------------------------------------
    // Internal Call, Event & Stack Unwinding Machinery
    // -------------------------------------------------------------------------

    internal VariableStore RequireVariableStore() =>
        FastVariables ?? throw new InvalidOperationException("Operation requires a concrete Hypera VariableStore.");

    internal IVariableStore RequireVariablesInterface() =>
        Variables ?? throw new InvalidOperationException("No IVariableStore is attached to this EmueraEngine.");

    internal ICharacterStore RequireCharactersInterface() =>
        Characters ?? throw new InvalidOperationException("No ICharacterStore is attached to this EmueraEngine.");

    internal bool FunctionExists(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (_functionsByName.ContainsKey(name.Trim()) || _eventGroupsByName.ContainsKey(name.Trim()));

    internal bool IsInsideMethodFunction()
    {
        for (int i = _callStack.Count - 1; i >= 0; i--)
        {
            if (_callStack[i].IsMethodFunction)
            {
                return true;
            }
        }

        return false;
    }

    internal void UnwindAllFramesForBegin()
    {
        _callStack.Clear();
        _eventStack.Clear();
        PendingInputRequest = null;
        _pendingScriptInputMeta = null;
        if (Status == EngineStepStatus.WaitingInput)
        {
            Status = EngineStepStatus.Running;
        }
    }

    internal void ClearEventStack() => _eventStack.Clear();

    internal void HaltFromSystemQuit(bool restartRequested)
    {
        RestartRequested = restartRequested;
        UnwindAllFramesForBegin();
        Status = EngineStepStatus.Completed;
    }

    internal SuspendedSystemSnapshot CaptureSuspendedSystemSnapshot(SystemFlowState flowState, SystemPhaseKind currentPhase)
    {
        var savedFrames = new List<CallFrame>(_callStack.Count);
        for (int i = 0; i < _callStack.Count; i++)
        {
            savedFrames.Add(_callStack[i].CloneSnapshot());
        }

        return new SuspendedSystemSnapshot(flowState, currentPhase, savedFrames, [.. _eventStack]);
    }

    internal void RestoreSuspendedSystemSnapshot(SuspendedSystemSnapshot snapshot)
    {
        _callStack.Clear();
        _callStack.AddRange(snapshot.SavedCallStack);
        _eventStack.Clear();
        _eventStack.AddRange(snapshot.SavedEventStack);
        PendingInputRequest = null;
        _pendingScriptInputMeta = null;
    }

    internal bool TryPushNamedFunction(string functionName)
    {
        if (!_functionsByName.TryGetValue(functionName, out int fnId))
        {
            return false;
        }

        FunctionDescriptor desc = Module.Functions[fnId];
        ResetFunctionLocalStatics(desc.Name);
        _callStack.Add(CreateActivationFrame(desc, 0, expectsStrReturn: false, discardReturnReg: true));
        return true;
    }

    internal bool TryPushEventGroup(string eventName)
    {
        if (!_eventGroupsByName.TryGetValue(eventName, out EventFunctionGroup? grp) || grp.OrderedHandlers.IsEmpty)
        {
            return false;
        }

        var cursor = new EventDispatchCursor(eventName, grp.OrderedHandlers, _callStack.Count);
        if (!cursor.TryAdvance(0L, isFirstAdvance: true, out EventHandlerEntry first))
        {
            return false;
        }

        _eventStack.Add(cursor);
        PushEventHandlerFrame(first);
        return true;
    }

    internal void InvokeScriptCallEvent(string eventName)
    {
        if (_eventGroupsByName.TryGetValue(eventName, out EventFunctionGroup? grp))
        {
            TryPushEventGroup(eventName);
            return;
        }

        if (_functionsByName.ContainsKey(eventName))
        {
            throw new InvalidOperationException($"CALLEVENT cannot target non-event function '@{eventName}'.");
        }
    }

    internal void InvokeStaticCall(CallFrame caller, Instruction insn, bool isTry, bool isJump)
    {
        int fnId = insn.Immediate;
        if ((uint)fnId >= (uint)Module.Functions.Length)
        {
            HandleMissingCallTarget(caller, $"#<invalid:{fnId}>", isTry);
            return;
        }

        FunctionDescriptor target = Module.Functions[fnId];
        InvokeResolvedTarget(caller, insn, target, isJump);
    }

    internal void InvokeDynamicCall(CallFrame caller, Instruction insn, bool isJump)
    {
        ushort nameStrReg = (ushort)(insn.Immediate & 0xFFFF);
        bool isTry = (insn.Immediate & (1 << 16)) != 0;
        bool isMethodCall = (insn.Aux & BytecodeAbi.CallIsMethodBit) != 0;

        string targetName = (nameStrReg < caller.StrRegisters.Length ? caller.StrRegisters[nameStrReg] : string.Empty).Trim();
        if (targetName.StartsWith('@'))
        {
            targetName = targetName[1..];
        }

        if (_eventGroupsByName.ContainsKey(targetName) && !Module.CompilerOptions.CompatiCallEvent)
        {
            if (isTry)
            {
                HandleMissingCallTarget(caller, targetName, isTry: true);
                return;
            }

            throw new InvalidOperationException($"Cannot CALL event function '@{targetName}' unless CompatiCallEvent is enabled.");
        }

        if (_eventGroupsByName.ContainsKey(targetName) && Module.CompilerOptions.CompatiCallEvent && !isMethodCall)
        {
            caller.ActiveCatchTargetPc = null;
            if (isJump)
            {
                _callStack.RemoveAt(_callStack.Count - 1);
            }

            TryPushEventGroup(targetName);
            return;
        }

        if (!_functionsByName.TryGetValue(targetName, out int fnId))
        {
            HandleMissingCallTarget(caller, targetName, isTry);
            return;
        }

        FunctionDescriptor target = Module.Functions[fnId];
        if (target.IsMethodFunction != isMethodCall)
        {
            if (isTry)
            {
                HandleMissingCallTarget(caller, targetName, isTry: true);
                return;
            }

            throw new InvalidOperationException($"Function kind mismatch calling '@{targetName}'.");
        }

        caller.ActiveCatchTargetPc = null;
        InvokeResolvedTarget(caller, insn, target, isJump);
    }

    private void HandleMissingCallTarget(CallFrame caller, string targetName, bool isTry)
    {
        if (!isTry)
        {
            throw new KeyNotFoundException($"Target function '@{targetName}' was not found.");
        }

        if (caller.ActiveCatchTargetPc is int catchPc)
        {
            caller.ProgramCounter = catchPc;
            caller.ActiveCatchTargetPc = null;
        }
    }

    private void InvokeResolvedTarget(CallFrame caller, Instruction insn, FunctionDescriptor target, bool isJump)
    {
        caller.ActiveCatchTargetPc = null;
        bool expectsStr = (insn.Aux & BytecodeAbi.CallExpectsStringReturnBit) != 0;
        ushort returnReg = isJump ? caller.CallerReturnRegister : insn.A;
        bool discardRegWrite = isJump ? caller.DiscardCallerRegisterWrite : false;

        CallFrame callee = CreateActivationFrame(target, returnReg, expectsStr, discardRegWrite);
        BindCallArguments(caller, callee, target, insn);

        if (isJump)
        {
            _callStack[^1] = callee;
        }
        else
        {
            _callStack.Add(callee);
        }
    }

    private CallFrame CreateActivationFrame(
        FunctionDescriptor desc,
        ushort callerReturnReg,
        bool expectsStrReturn,
        bool discardReturnReg)
    {
        // Count frame-scoped local descriptors (FunctionDynamic + Reference)
        int frameLocalCount = 0;
        for (int i = 0; i < desc.LocalVariables.Length; i++)
        {
            if (desc.LocalVariables[i].Scope is VariableScopeKind.FunctionDynamic or VariableScopeKind.Reference)
            {
                frameLocalCount = Math.Max(frameLocalCount, desc.LocalVariables[i].SlotId + 1);
            }
        }

        FrameLocalStorage[] localSlots = frameLocalCount > 0 ? new FrameLocalStorage[frameLocalCount] : [];
        for (int i = 0; i < desc.LocalVariables.Length; i++)
        {
            VariableSlotDescriptor lv = desc.LocalVariables[i];
            if (lv.Scope == VariableScopeKind.FunctionDynamic)
            {
                localSlots[lv.SlotId] = FrameLocalStorage.CreateDynamic(lv.SlotId, lv);
            }
            else if (lv.Scope == VariableScopeKind.Reference)
            {
                localSlots[lv.SlotId] = FrameLocalStorage.CreateUnboundRef(lv.SlotId, lv.Name, lv.ValueKind, Math.Max(1, lv.Dimensions.Length));
            }
        }

        return new CallFrame(
            desc.FunctionId,
            desc.Name,
            desc.EntryPc,
            Math.Max((int)desc.IntRegisterCount, 1),
            Math.Max((int)desc.StrRegisterCount, 1),
            callerReturnReg,
            expectsStrReturn)
        {
            IsMethodFunction = desc.IsMethodFunction,
            DiscardCallerRegisterWrite = discardReturnReg,
            LocalSlots = localSlots,
        };
    }

    private void BindCallArguments(CallFrame caller, CallFrame callee, FunctionDescriptor target, Instruction insn)
    {
        // Clear ARG@FUNC / ARGS@FUNC before binding arguments
        ResetFunctionArgStatics(target.Name);

        int passedCount = insn.D;
        ushort intBase = insn.B;
        ushort strBase = insn.C;
        ushort aux = insn.Aux;

        CallArgTag GetArgTag(int idx)
        {
            if ((uint)idx >= (uint)passedCount)
            {
                return CallArgTag.Omitted;
            }

            if (passedCount <= BytecodeAbi.MaxInlineUserCallArgs)
            {
                return BytecodeAbi.UnpackUserCallArgTag(aux, idx);
            }

            int tagReg = intBase + passedCount + idx;
            return tagReg < caller.IntRegisters.Length ? (CallArgTag)caller.IntRegisters[tagReg] : CallArgTag.Omitted;
        }

        // Bind declared formal parameters
        for (int i = 0; i < target.Parameters.Length; i++)
        {
            FunctionParameterDescriptor p = target.Parameters[i];
            CallArgTag tag = GetArgTag(i);

            if (p.IsByRef)
            {
                if (tag != CallArgTag.ByRef)
                {
                    throw new InvalidOperationException($"Missing by-reference argument #{i + 1} for '@{target.Name}'.");
                }

                var (slotId, origin, fixedIdx0) = BytecodeAbi.UnpackByRefTarget(caller.IntRegisters[intBase + i]);
                FrameLocalStorage dstRef = callee.LocalSlots[p.RegisterOrSlotIndex];
                if (origin is VariableSlotOrigin.FrameLocal or VariableSlotOrigin.RefPassthrough)
                {
                    dstRef.CopyBindingFrom(caller.LocalSlots[slotId]);
                }
                else
                {
                    VmDispatcher.BindRefToGlobalOrCharaSlot(this, dstRef, slotId, fixedIdx0);
                }

                continue;
            }

            if (p.ValueKind == VariableValueKind.Int)
            {
                long val = tag == CallArgTag.Int
                    ? caller.IntRegisters[intBase + i]
                    : p.HasDefaultValue ? p.DefaultIntValue : 0L;
                WriteBoundParameterInt(callee, p, val);
            }
            else
            {
                string val = tag switch
                {
                    CallArgTag.Str => caller.StrRegisters[strBase + i] ?? string.Empty,
                    CallArgTag.Int when Module.CompilerOptions.CompatiFuncArgAutoConvert =>
                        caller.IntRegisters[intBase + i].ToString(CultureInfo.InvariantCulture),
                    _ => p.HasDefaultValue ? (p.DefaultStrValue ?? string.Empty) : string.Empty,
                };
                WriteBoundParameterStr(callee, p, val);
            }
        }

        // Also mirror positional non-declared arguments into ARG@FUNC / ARGS@FUNC when no formal parameters declared
        if (target.Parameters.IsEmpty && passedCount > 0 && FastVariables is not null)
        {
            for (int i = 0; i < passedCount; i++)
            {
                CallArgTag tag = GetArgTag(i);
                if (tag == CallArgTag.Int && FastVariables.TryResolveSlot($"ARG@{target.Name}", out int argSlot))
                {
                    Span<long> span = FastVariables.GetIntSpan(argSlot);
                    if ((uint)i < (uint)span.Length) span[i] = caller.IntRegisters[intBase + i];
                }
                else if (tag == CallArgTag.Str && FastVariables.TryResolveSlot($"ARGS@{target.Name}", out int argsSlot))
                {
                    Span<string> span = FastVariables.GetStrSpan(argsSlot);
                    if ((uint)i < (uint)span.Length) span[i] = caller.StrRegisters[strBase + i] ?? string.Empty;
                }
            }
        }
    }

    private void WriteBoundParameterInt(CallFrame callee, FunctionParameterDescriptor p, long val)
    {
        int colon = p.Name.IndexOf(':');
        if (colon > 0 && FastVariables is not null)
        {
            long sub = long.Parse(p.Name.AsSpan(colon + 1), CultureInfo.InvariantCulture);
            FastVariables.SetInt1D(p.RegisterOrSlotIndex, sub, val);
            return;
        }

        if (p.RegisterOrSlotIndex < callee.LocalSlots.Length &&
            callee.LocalSlots[p.RegisterOrSlotIndex] is { ValueKind: VariableValueKind.Int } loc &&
            string.Equals(loc.Name, p.Name, StringComparison.OrdinalIgnoreCase))
        {
            loc.IntArray![0] = val;
            return;
        }

        FastVariables?.SetInt0D(p.RegisterOrSlotIndex, val);
    }

    private void WriteBoundParameterStr(CallFrame callee, FunctionParameterDescriptor p, string val)
    {
        int colon = p.Name.IndexOf(':');
        if (colon > 0 && FastVariables is not null)
        {
            long sub = long.Parse(p.Name.AsSpan(colon + 1), CultureInfo.InvariantCulture);
            FastVariables.SetStr1D(p.RegisterOrSlotIndex, sub, val);
            return;
        }

        if (p.RegisterOrSlotIndex < callee.LocalSlots.Length &&
            callee.LocalSlots[p.RegisterOrSlotIndex] is { ValueKind: VariableValueKind.Str } loc &&
            string.Equals(loc.Name, p.Name, StringComparison.OrdinalIgnoreCase))
        {
            loc.StrArray![0] = val;
            return;
        }

        FastVariables?.SetStr0D(p.RegisterOrSlotIndex, val);
    }

    private void ResetFunctionArgStatics(string funcName)
    {
        if (FastVariables is null)
        {
            return;
        }

        if (HasRegisteredName($"ARG@{funcName}") && FastVariables.TryResolveSlot($"ARG@{funcName}", out int argSlot))
        {
            FastVariables.GetIntSpan(argSlot).Clear();
        }

        if (HasRegisteredName($"ARGS@{funcName}") && FastVariables.TryResolveSlot($"ARGS@{funcName}", out int argsSlot))
        {
            FastVariables.GetStrSpan(argsSlot).Fill(string.Empty);
        }
    }

    private void ResetFunctionLocalStatics(string funcName)
    {
        ResetFunctionArgStatics(funcName);
    }

    private bool HasRegisteredName(string varName)
    {
        if (FastVariables is null) return false;
        StringComparison cmp = Module.CompilerOptions.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string k in FastVariables.VariableNames)
        {
            if (string.Equals(k, varName, cmp)) return true;
        }

        return false;
    }

    internal void CompleteCurrentFrameVoid()
    {
        CallFrame finished = _callStack[^1];
        _callStack.RemoveAt(_callStack.Count - 1);
        if (!finished.IsMethodFunction && FastVariables is not null)
        {
            FastVariables.Result = 0L;
        }

        AdvanceEventStackIfMatchingDepth(0L);
    }

    internal void CompleteCurrentFrameInt(long returnValue, bool syncResultArray)
    {
        CallFrame finished = _callStack[^1];
        _callStack.RemoveAt(_callStack.Count - 1);

        if (syncResultArray && FastVariables is not null)
        {
            FastVariables.Result = returnValue;
        }

        if (_callStack.Count > 0 && !finished.DiscardCallerRegisterWrite && !finished.ExpectsStringReturn)
        {
            CallFrame caller = _callStack[^1];
            if (finished.CallerReturnRegister < caller.IntRegisters.Length)
            {
                caller.IntRegisters[finished.CallerReturnRegister] = returnValue;
            }
        }

        AdvanceEventStackIfMatchingDepth(returnValue);
    }

    internal void CompleteCurrentFrameStr(string returnValue, bool syncResultsArray)
    {
        CallFrame finished = _callStack[^1];
        _callStack.RemoveAt(_callStack.Count - 1);

        if (syncResultsArray && FastVariables is not null)
        {
            FastVariables.Results = returnValue;
        }

        if (_callStack.Count > 0 && !finished.DiscardCallerRegisterWrite && finished.ExpectsStringReturn)
        {
            CallFrame caller = _callStack[^1];
            if (finished.CallerReturnRegister < caller.StrRegisters.Length)
            {
                caller.StrRegisters[finished.CallerReturnRegister] = returnValue;
            }
        }

        AdvanceEventStackIfMatchingDepth(0L);
    }

    private void AdvanceEventStackIfMatchingDepth(long lastResult)
    {
        while (_eventStack.Count > 0 && _eventStack[^1].CallerStackDepth == _callStack.Count)
        {
            EventDispatchCursor cursor = _eventStack[^1];
            if (cursor.TryAdvance(lastResult, isFirstAdvance: false, out EventHandlerEntry nextHandler))
            {
                PushEventHandlerFrame(nextHandler);
                return;
            }

            _eventStack.RemoveAt(_eventStack.Count - 1);
        }
    }

    private void PushEventHandlerFrame(EventHandlerEntry entry)
    {
        FunctionDescriptor desc = Module.Functions[entry.FunctionId];
        _callStack.Add(CreateActivationFrame(desc, 0, expectsStrReturn: false, discardReturnReg: true));
    }

    internal bool TryAdvanceEventsOrSystemFlow()
    {
        if (_eventStack.Count > 0)
        {
            AdvanceEventStackIfMatchingDepth(FastVariables?.Result ?? 0L);
            if (_callStack.Count > 0)
            {
                return true;
            }
        }

        if (SystemFlow.IsActive)
        {
            return SystemFlow.AdvanceWhenCallStackEmpty();
        }

        return false;
    }

    internal void InvokeSharpPlugin(CallFrame caller, Instruction insn)
    {
        string methodName = (uint)insn.Immediate < (uint)Module.StringConstants.Length
            ? Module.StringConstants[insn.Immediate]
            : caller.StrRegisters[insn.A];

        if (SharpPluginHost is null)
        {
            throw new NotSupportedException($"No external CALLSHARP plugin host registered for method '{methodName}'.");
        }

        var w = new VmBuiltinExecutor.CallWindow(this, caller, insn);
        var args = new SharpPluginArgument[w.Count];
        for (int i = 0; i < w.Count; i++)
        {
            CallArgTag tag = w.GetTag(i);
            args[i] = tag == CallArgTag.Str
                ? new SharpPluginArgument(IsString: true, IntValue: 0L, StrValue: w.GetStr(i))
                : new SharpPluginArgument(IsString: false, IntValue: w.GetInt(i), StrValue: string.Empty);
        }

        SharpPluginHost.Invoke(methodName, args);
    }

    // -------------------------------------------------------------------------
    // Input Suspension & Validation (INPUT / ONEINPUT / TINPUT / BINPUT / INPUTMOUSEKEY)
    // -------------------------------------------------------------------------

    internal bool BeginScriptWaitInputFromInsn(CallFrame frame, Instruction insn)
    {
        var kind = (InputRequestKind)(insn.Aux & 0xFF);
        var flags = (WaitInputFlags)(insn.Aux & 0xFF00);
        bool hasDef = (flags & WaitInputFlags.HasDefault) != 0;
        bool hasTimeout = (flags & WaitInputFlags.HasTimeout) != 0;
        bool hasTimeoutMsg = (flags & WaitInputFlags.HasTimeoutMessage) != 0;

        long defInt = hasDef && kind is InputRequestKind.IntValue or InputRequestKind.OneInt && insn.B < frame.IntRegisters.Length
            ? frame.IntRegisters[insn.B]
            : 0L;
        string defStr = hasDef && kind is InputRequestKind.StringValue or InputRequestKind.OneString && insn.B < frame.StrRegisters.Length
            ? frame.StrRegisters[insn.B]
            : string.Empty;
        long? timeoutMs = hasTimeout
            ? (insn.C < frame.IntRegisters.Length && frame.IntRegisters[insn.C] > 0 ? frame.IntRegisters[insn.C] : insn.Immediate > 0 ? insn.Immediate : null)
            : null;
        string? timeoutMsg = hasTimeoutMsg && insn.D < frame.StrRegisters.Length ? frame.StrRegisters[insn.D] : null;
        int? targetReg = insn.A < frame.IntRegisters.Length || insn.A < frame.StrRegisters.Length ? insn.A : null;

        return IssueScriptInput(kind, flags, hasDef, defInt, defStr, timeoutMs, timeoutMsg, targetReg);
    }

    internal bool IssueAnyKeyWait() =>
        IssueScriptInput(InputRequestKind.AnyKey, WaitInputFlags.None);

    internal bool IssueTimedWait(long durationMs, bool forceNoSkip)
    {
        if (IsSkipDisplay && !forceNoSkip)
        {
            return false;
        }

        return IssueScriptInput(InputRequestKind.TimedWait, WaitInputFlags.HasTimeout, timeoutMs: Math.Max(0L, durationMs));
    }

    internal void IssueSystemInputRequest(InputRequestKind kind)
    {
        _inputGeneration++;
        _pendingScriptInputMeta = null;
        PendingInputRequest = new InputRequest(
            _inputGeneration,
            kind,
            AllowMouseHitTest: Options.UseMouse,
            ActiveButtons: SnapshotActiveButtonsForGeneration());
        Status = EngineStepStatus.WaitingInput;
        EmitSnapshotInternal();
    }

    internal bool IssueScriptInput(
        InputRequestKind kind,
        WaitInputFlags flags,
        bool hasDef = false,
        long defInt = 0L,
        string defStr = "",
        long? timeoutMs = null,
        string? timeoutMsg = null,
        int? targetReg = null)
    {
        SystemFlow.NotifyWaitOccurredInScript();
        IReadOnlyList<ButtonDescriptor> activeButtons = SnapshotActiveButtonsForGeneration();

        // BINPUT / BINPUTS / ONEBINPUT / ONEBINPUTS active button inventory check!
        if ((flags & WaitInputFlags.ButtonInventoryOnly) != 0)
        {
            bool requireIntButtons = kind is InputRequestKind.IntValue or InputRequestKind.OneInt;
            var matchingButtons = new List<ButtonDescriptor>(activeButtons.Count);
            foreach (ButtonDescriptor btn in activeButtons)
            {
                if (!requireIntButtons || btn.IsIntegerButton)
                {
                    matchingButtons.Add(btn);
                }
            }

            if (matchingButtons.Count == 0)
            {
                if (!hasDef)
                {
                    throw new InvalidOperationException("BINPUT/ONEBINPUT executed when zero matching active buttons exist in the current generation.");
                }

                if (FastVariables is not null)
                {
                    if (requireIntButtons) FastVariables.Result = defInt;
                    else FastVariables.Results = defStr;
                }

                return false;
            }

            activeButtons = matchingButtons;
        }

        _inputGeneration++;
        _pendingScriptInputMeta = new PendingScriptInputMeta(kind, flags, hasDef, defInt, defStr ?? string.Empty, targetReg);
        PendingInputRequest = new InputRequest(
            _inputGeneration,
            kind,
            TimeoutMilliseconds: timeoutMs,
            TimeoutMessage: timeoutMsg,
            AllowMouseHitTest: Options.UseMouse && (flags & WaitInputFlags.DisableMouseHitTest) == 0,
            ActiveButtons: activeButtons);
        Status = EngineStepStatus.WaitingInput;
        EmitSnapshotInternal();
        return true;
    }

    private bool TryProcessScriptInput(InputResponse response, PendingScriptInputMeta meta)
    {
        if (FastVariables is null && Variables is null)
        {
            return true;
        }

        VariableStore? vars = FastVariables;

        // 1. Primitive INPUTMOUSEKEY: populates RESULT:0..6 + RESULTS
        if ((meta.Flags & WaitInputFlags.PrimitiveMouseKey) != 0 || meta.Kind == InputRequestKind.MouseClick)
        {
            if (vars is not null)
            {
                Span<long> res = vars.ResultSpan;
                res[..Math.Min(7, res.Length)].Clear();
                if (response.IsTimeout)
                {
                    res[0] = 4L;
                    vars.Results = string.Empty;
                }
                else if (response.HitTest.MouseButton != 0 || response.HitTest.HasHitColor)
                {
                    res[0] = 1L;
                    res[1] = response.HitTest.MouseButton != 0 ? response.HitTest.MouseButton : 1L;
                    res[2] = response.HitTest.LogicalX;
                    res[3] = response.HitTest.LogicalY;
                    res[4] = response.HitTest.HasHitColor ? response.HitTest.Result3PrimaryColor : 0L;
                    res[5] = response.IntValue ?? 0L;
                    res[6] = response.HitTest.Result6HoverOrAuxColor;
                    vars.Results = response.StringValue ?? string.Empty;
                }
                else
                {
                    res[0] = 3L;
                    res[1] = response.IntValue ?? (response.StringValue is { Length: > 0 } s ? s[0] : 0L);
                    vars.Results = response.StringValue ?? string.Empty;
                }
            }

            return true;
        }

        if (meta.Kind is InputRequestKind.AnyKey or InputRequestKind.TimedWait)
        {
            return true;
        }

        bool isMouseInput = response.HitTest.MouseButton != 0 || response.HitTest.HasHitColor;
        bool isOneInput = meta.Kind is InputRequestKind.OneInt or InputRequestKind.OneString;
        bool isIntInput = meta.Kind is InputRequestKind.IntValue or InputRequestKind.OneInt;
        bool isButtonOnly = (meta.Flags & WaitInputFlags.ButtonInventoryOnly) != 0;

        if (isIntInput)
        {
            long resolvedInt;
            if (response.IsTimeout)
            {
                resolvedInt = meta.DefaultInt;
            }
            else if (TryExtractIntInput(response, isOneInput, isMouseInput, Options.AllowLongInputByMouse, Options.LanguageCodePage, out long parsed))
            {
                resolvedInt = parsed;
            }
            else if (meta.HasDefault && string.IsNullOrEmpty(response.StringValue) && response.IntValue is null)
            {
                resolvedInt = meta.DefaultInt;
            }
            else
            {
                return false; // Reject invalid non-integer input; stay suspended in WaitingInput!
            }

            if (isButtonOnly && !response.IsTimeout && !MatchesActiveIntButton(resolvedInt))
            {
                return false; // BINPUT / ONEBINPUT rejects values not in active button inventory!
            }

            if (vars is not null)
            {
                vars.Result = resolvedInt;
            }
            else if (Variables is not null && Variables.TryResolveSlot("RESULT", out int rSlot))
            {
                Variables.SetInt(rSlot, resolvedInt, [0]);
            }

            if (meta.TargetRegister is int dstIntReg && _callStack.Count > 0 && dstIntReg < _callStack[^1].IntRegisters.Length)
            {
                _callStack[^1].IntRegisters[dstIntReg] = resolvedInt;
            }

            return true;
        }
        else
        {
            string resolvedStr;
            if (response.IsTimeout)
            {
                resolvedStr = meta.DefaultStr;
            }
            else
            {
                string raw = response.StringValue ?? (response.IntValue.HasValue ? response.IntValue.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
                if (raw.Length == 0 && meta.HasDefault)
                {
                    resolvedStr = meta.DefaultStr;
                }
                else if (isOneInput && raw.Length > 1 && !(isMouseInput && Options.AllowLongInputByMouse))
                {
                    resolvedStr = raw[..1];
                }
                else
                {
                    resolvedStr = raw;
                }
            }

            if (isButtonOnly && !response.IsTimeout && !MatchesActiveStrButton(resolvedStr))
            {
                return false; // BINPUTS / ONEBINPUTS rejects strings not in active button inventory!
            }

            if ((meta.Flags & WaitInputFlags.AnyValue) != 0 && vars is not null && EastAsianStringOps.IsNumeric(resolvedStr))
            {
                vars.Result = EastAsianStringOps.ParseEmueraInt(resolvedStr, Options.LanguageCodePage);
            }

            if (vars is not null)
            {
                vars.Results = resolvedStr;
            }
            else if (Variables is not null && Variables.TryResolveSlot("RESULTS", out int rsSlot))
            {
                Variables.SetStr(rsSlot, resolvedStr, [0]);
            }

            if (meta.TargetRegister is int dstStrReg && _callStack.Count > 0 && dstStrReg < _callStack[^1].StrRegisters.Length)
            {
                _callStack[^1].StrRegisters[dstStrReg] = resolvedStr;
            }

            return true;
        }
    }

    private static bool TryExtractIntInput(
        InputResponse response,
        bool isOneInput,
        bool isMouseInput,
        bool allowLongByMouse,
        int codePage,
        out long value)
    {
        bool allowMultiDigit = !isOneInput || (isMouseInput && allowLongByMouse);
        if (response.IntValue is long directInt)
        {
            if (allowMultiDigit || directInt is >= 0 and <= 9)
            {
                value = directInt;
                return true;
            }

            string digits = directInt.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > 0 && char.IsAsciiDigit(digits[0]))
            {
                value = digits[0] - '0';
                return true;
            }

            value = 0L;
            return false;
        }

        if (!string.IsNullOrEmpty(response.StringValue))
        {
            string s = response.StringValue.Trim();
            if (!allowMultiDigit)
            {
                if (s.Length > 0 && char.IsAsciiDigit(s[0]))
                {
                    value = s[0] - '0';
                    return true;
                }

                value = 0L;
                return false;
            }

            if (EastAsianStringOps.IsNumeric(s))
            {
                value = EastAsianStringOps.ParseEmueraInt(s, codePage);
                return true;
            }
        }

        value = 0L;
        return false;
    }

    private bool MatchesActiveIntButton(long candidate)
    {
        IReadOnlyList<ButtonDescriptor>? buttons = PendingInputRequest?.ActiveButtons;
        if (buttons is null)
        {
            return false;
        }

        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].IsIntegerButton && buttons[i].IntValue == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesActiveStrButton(string candidate)
    {
        IReadOnlyList<ButtonDescriptor>? buttons = PendingInputRequest?.ActiveButtons;
        if (buttons is null)
        {
            return false;
        }

        for (int i = 0; i < buttons.Count; i++)
        {
            ButtonDescriptor b = buttons[i];
            string bStr = b.StringValue ?? (b.IsIntegerButton ? b.IntValue.ToString(CultureInfo.InvariantCulture) : string.Empty);
            if (string.Equals(bStr, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyHitTestColorSlots(VariableStore vars, InputHitTestSlots hit)
    {
        if (!hit.HasHitColor)
        {
            return;
        }

        Span<long> res = vars.ResultSpan;
        if (res.Length > 6)
        {
            res[3] = hit.Result3PrimaryColor;
            res[4] = hit.Result4SecondaryColor;
            res[6] = hit.Result6HoverOrAuxColor;
        }
    }

    internal void AdvanceButtonGeneration()
    {
        _buttonGeneration++;
        _currentCycleButtons.Clear();
        ConsoleBuffer.AdvanceButtonGeneration();
    }

    private IReadOnlyList<ButtonDescriptor> SnapshotActiveButtonsForGeneration() =>
        _currentCycleButtons.Count == 0 ? [] : [.. _currentCycleButtons];

    // -------------------------------------------------------------------------
    // Console Output & Save/Load Helpers
    // -------------------------------------------------------------------------

    public void RegisterButtonDescriptor(string text, bool isInt, long intValue, string? strValue = null, LogicalRect bounds = default)
    {
        _currentCycleButtons.Add(new ButtonDescriptor(
            _buttonGeneration,
            text,
            isInt,
            intValue,
            strValue,
            bounds,
            Options.FocusColorRgba));
    }

    internal void PrintStyled(string text, PrintModifierFlags flags)
    {
        uint color = (flags & PrintModifierFlags.DefaultColor) != 0 ? Options.ForeColorRgba : CurrentForeColorRgba;
        string projected = (flags & PrintModifierFlags.ForceKana) != 0 ? EastAsianStringOps.ToKatakana(text) : text;

        if ((flags & PrintModifierFlags.PlainText) == 0)
        {
            AutoDetectInlineBracketButtons(projected);
        }

        _displayOperations.Add(new DisplayOperation(
            DisplayOperationKind.PrintTextSpan,
            default,
            projected,
            color,
            CurrentFontName,
            Options.FontSize));
        ConsoleBuffer.CurrentFontFamily = CurrentFontName;
        ConsoleBuffer.FontStyle = (FontStyleFlags)(CurrentFontStyle & 0xF);
        ConsoleBuffer.Print(projected, useDefaultColor: (flags & PrintModifierFlags.DefaultColor) != 0);

        if ((flags & PrintModifierFlags.NewLine) != 0)
        {
            PrintNewLine();
        }
    }

    internal void PrintColumnCell(string text, bool rightAlign, PrintModifierFlags flags)
    {
        int widthCols = Math.Max(1, Options.PrintCLength);
        int textBytes = EastAsianStringOps.GetStrlenS(text, Options.LanguageCodePage);
        int pad = Math.Max(0, widthCols - textBytes);
        string padded = rightAlign ? new string(' ', pad) + text : text + new string(' ', pad);
        PrintStyled(padded, flags);
    }

    internal void PrintButtonSpan(string label, bool isInt, long intVal, string? strVal)
    {
        RegisterButtonDescriptor(label, isInt, intVal, strVal);
        _displayOperations.Add(new DisplayOperation(
            DisplayOperationKind.PrintButtonSpan,
            default,
            label,
            CurrentForeColorRgba,
            CurrentFontName,
            Options.FontSize,
            ButtonGeneration: _buttonGeneration,
            ButtonIntValue: isInt ? intVal : null,
            ButtonStrValue: isInt ? null : (strVal ?? string.Empty)));
        ConsoleBuffer.CurrentFontFamily = CurrentFontName;
        ConsoleBuffer.FontStyle = (FontStyleFlags)(CurrentFontStyle & 0xF);
        ConsoleBuffer.PrintButton(label, isInt ? intVal.ToString(CultureInfo.InvariantCulture) : (strVal ?? string.Empty));
    }

    internal void PrintButtonLine(string label, long intVal)
    {
        PrintButtonSpan(label, isInt: true, intVal, null);
        PrintNewLine();
    }

    internal void PrintButtonColumn(string label, long intVal, bool rightAlign)
    {
        int widthCols = Math.Max(1, Options.PrintCLength);
        int textBytes = EastAsianStringOps.GetStrlenS(label, Options.LanguageCodePage);
        int pad = Math.Max(0, widthCols - textBytes);
        if (rightAlign && pad > 0)
        {
            PrintStyled(new string(' ', pad), PrintModifierFlags.PlainText);
        }

        PrintButtonSpan(label, isInt: true, intVal, null);
        if (!rightAlign && pad > 0)
        {
            PrintStyled(new string(' ', pad), PrintModifierFlags.PlainText);
        }
    }

    internal void PrintTextLine(string text) => PrintStyled(text, PrintModifierFlags.NewLine);

    internal void PrintNewLine()
    {
        _displayOperations.Add(new DisplayOperation(DisplayOperationKind.NewLine, default));
        ConsoleBuffer.NewLine();
        SyncLineCount();
    }

    internal void PrintDrawLine(string? customPattern = null)
    {
        string unit = string.IsNullOrEmpty(customPattern) ? (FastVariables?.ReplaceConfig.DrawLineString ?? "-") : customPattern;
        int cols = Math.Max(1, Viewport.LogicalWidth / Math.Max(1, Options.FontSize / 2));
        var sb = new StringBuilder(cols + 4);
        int unitWidth = Math.Max(1, EastAsianStringOps.GetStrlenS(unit, Options.LanguageCodePage));
        for (int w = 0; w + unitWidth <= cols; w += unitWidth)
        {
            sb.Append(unit);
        }

        _displayOperations.Add(new DisplayOperation(DisplayOperationKind.DrawLineRule, default, sb.ToString(), CurrentForeColorRgba, CurrentFontName, Options.FontSize));
        ConsoleBuffer.PrintLine(sb.ToString());
        SyncLineCount();
    }

    internal void ClearLines(int logicalCount)
    {
        if (logicalCount <= 0)
        {
            return;
        }

        ConsoleBuffer.ClearLine(logicalCount);
        SyncLineCount();
    }

    internal void ReuseLastLine(string replacement)
    {
        ConsoleBuffer.ClearLine(1);
        PrintStyled(replacement, PrintModifierFlags.PlainText);
    }

    internal void PrintHtml(string html, bool isIsland, bool noNewLine)
    {
        ConsoleBuffer.CurrentFontFamily = CurrentFontName;
        ConsoleBuffer.FontStyle = (FontStyleFlags)(CurrentFontStyle & 0xF);
        if (isIsland)
        {
            ConsoleBuffer.HtmlPrintIsland(html);
        }
        else
        {
            ConsoleBuffer.HtmlPrint(html, noWrap: noNewLine);
            SyncLineCount();
        }

        // Also register any <button value='...'> elements parsed from HTML into current button generation!
        ExtractHtmlButtons(html);
        _displayOperations.Add(new DisplayOperation(DisplayOperationKind.PrintTextSpan, default, HtmlPrintEngine.ToPlainText(html), CurrentForeColorRgba, CurrentFontName, Options.FontSize));
        if (!noNewLine && !isIsland)
        {
            _displayOperations.Add(new DisplayOperation(DisplayOperationKind.NewLine, default));
        }
    }

    internal void SetForeColorRgb24(long rgb)
    {
        CurrentForeColorRgba = (((uint)(rgb & 0xFF_FFFF)) << 8) | 0xFFu;
        ConsoleBuffer.CurrentForeColorRgba = CurrentForeColorRgba;
    }

    internal void ResetForeColor()
    {
        CurrentForeColorRgba = Options.ForeColorRgba;
        ConsoleBuffer.CurrentForeColorRgba = CurrentForeColorRgba;
    }

    internal void SetBackColorRgb24(long rgb)
    {
        CurrentBackColorRgba = (((uint)(rgb & 0xFF_FFFF)) << 8) | 0xFFu;
        ConsoleBuffer.CurrentBackColorRgba = CurrentBackColorRgba;
    }

    internal void ResetBackColor()
    {
        CurrentBackColorRgba = Options.BackColorRgba;
        ConsoleBuffer.CurrentBackColorRgba = CurrentBackColorRgba;
    }

    internal void SetTextAlignment(string align)
    {
        switch (align.Trim().ToUpperInvariant())
        {
            case "CENTER":
                CurrentAlignmentName = "CENTER";
                ConsoleBuffer.Alignment = TextAlignment.Center;
                break;
            case "RIGHT":
                CurrentAlignmentName = "RIGHT";
                ConsoleBuffer.Alignment = TextAlignment.Right;
                break;
            default:
                CurrentAlignmentName = "LEFT";
                ConsoleBuffer.Alignment = TextAlignment.Left;
                break;
        }
    }

    internal bool CheckSoundExists(string relativeSoundPath)
    {
        if (string.IsNullOrWhiteSpace(relativeSoundPath)) return false;
        string normalized = VfsPathNormalizer.Normalize(relativeSoundPath);
        return FileSystem.FileExists(normalized) || FileSystem.FileExists("sound/" + normalized);
    }

    internal void SaveToSlot(int slotNo, string? description = null)
    {
        VariableStore vars = RequireVariableStore();
        EngineSaveSlotSnapshot memSnap = vars.SaveToMemorySlot(slotNo, description);

        if (_saveDirectory is not null)
        {
            EraSaveFormat fmt = Options.ZipSaveData
                ? EraSaveFormat.GZipBinary
                : Options.SystemSaveInBinary ? EraSaveFormat.Binary : EraSaveFormat.Text;
            var eraSnap = BuildEraSaveSnapshot(vars, memSnap.SaveText, EraSaveFileType.Normal, fmt);
            string relPath = _saveDirectory.GetSaveSlotVirtualPath(slotNo, Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, EraSaveSerializer.Save(eraSnap, fmt));
        }
    }

    internal bool LoadFromSlot(int slotNo)
    {
        VariableStore vars = RequireVariableStore();
        if (_saveDirectory is not null)
        {
            string relPath = _saveDirectory.GetSaveSlotVirtualPath(slotNo, Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(relPath);
            if (File.Exists(fullPath))
            {
                EraSaveSnapshot loaded = EraSaveSerializer.Load(File.ReadAllBytes(fullPath));
                ApplyEraSaveSnapshot(vars, loaded, slotNo);
                return true;
            }
        }

        if (vars.TryLoadFromMemorySlot(slotNo))
        {
            return true;
        }

        return false;
    }

    internal bool CheckSaveSlotExists(int slotNo, out string summary)
    {
        VariableStore vars = RequireVariableStore();
        if (_saveDirectory is not null)
        {
            string relPath = _saveDirectory.GetSaveSlotVirtualPath(slotNo, Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(relPath);
            if (File.Exists(fullPath))
            {
                EraSaveHeader header = EraSaveSerializer.PeekHeader(File.ReadAllBytes(fullPath));
                summary = header.SaveDataText;
                return true;
            }
        }

        if (vars.HasMemorySaveSlot(slotNo, out EngineSaveSlotSnapshot? mem) && mem is not null)
        {
            summary = mem.SaveText;
            return true;
        }

        summary = string.Empty;
        return false;
    }

    internal void DeleteSaveSlot(int slotNo)
    {
        FastVariables?.DeleteMemorySaveSlot(slotNo);
        if (_saveDirectory is not null)
        {
            string relPath = _saveDirectory.GetSaveSlotVirtualPath(slotNo, Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(relPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    internal void SaveGlobal()
    {
        VariableStore vars = RequireVariableStore();
        EraSaveFormat fmt = Options.ZipSaveData
            ? EraSaveFormat.GZipBinary
            : Options.SystemSaveInBinary ? EraSaveFormat.Binary : EraSaveFormat.Text;
        _memoryGlobalSnapshot = BuildEraSaveSnapshot(vars, string.Empty, EraSaveFileType.Global, fmt);

        if (_saveDirectory is not null)
        {
            string rel = _saveDirectory.GetGlobalSaveVirtualPath(Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, EraSaveSerializer.Save(_memoryGlobalSnapshot, fmt));
        }
    }

    internal bool LoadGlobal()
    {
        VariableStore vars = RequireVariableStore();
        if (_saveDirectory is not null)
        {
            string rel = _saveDirectory.GetGlobalSaveVirtualPath(Options.UseSaveFolder);
            string fullPath = _saveDirectory.ResolveConfinedPath(rel);
            if (File.Exists(fullPath))
            {
                EraSaveSnapshot loaded = EraSaveSerializer.Load(File.ReadAllBytes(fullPath));
                ApplyGlobalSnapshot(vars, loaded);
                return true;
            }
        }

        if (_memoryGlobalSnapshot is not null)
        {
            ApplyGlobalSnapshot(vars, _memoryGlobalSnapshot);
            return true;
        }

        return false;
    }

    private static EraSaveSnapshot BuildEraSaveSnapshot(VariableStore vars, string saveText, EraSaveFileType fileType, EraSaveFormat fmt)
    {
        var snap = new EraSaveSnapshot
        {
            Header = new EraSaveHeader(vars.GameBase.Code, vars.GameBase.Version, saveText, fileType, EraSaveSerializer.CurrentBinaryVersion, fmt),
        };

        // 1. Characters (for normal game saves)
        if (fileType == EraSaveFileType.Normal)
        {
            CharacterStore chars = vars.Characters;
            for (int c = 0; c < chars.Count; c++)
            {
                var charaData = new EraCharacterSaveData
                {
                    No = chars.NoSlotId >= 0 ? chars.GetInt0D(c, chars.NoSlotId) : 0L,
                    IsAssi = chars.IsAssiSlotId >= 0 ? chars.GetInt0D(c, chars.IsAssiSlotId) : 0L,
                    Name = chars.NameSlotId >= 0 ? chars.GetStr0D(c, chars.NameSlotId) : string.Empty,
                    CallName = chars.CallNameSlotId >= 0 ? chars.GetStr0D(c, chars.CallNameSlotId) : string.Empty,
                    NickName = chars.NickNameSlotId >= 0 ? chars.GetStr0D(c, chars.NickNameSlotId) : string.Empty,
                    MasterName = chars.MasterNameSlotId >= 0 ? chars.GetStr0D(c, chars.MasterNameSlotId) : string.Empty,
                };

                foreach (CharacterSlotSchema schema in chars.IntSlotSchemas)
                {
                    if (!schema.IsSavedata ||
                        schema.GlobalSlotId == chars.NoSlotId ||
                        schema.GlobalSlotId == chars.IsAssiSlotId)
                    {
                        continue;
                    }

                    ReadOnlySpan<long> raw = chars.GetIntSpan(c, schema.GlobalSlotId);
                    EraSaveValue val = PackIntSaveValue(raw, schema.SubRank, schema.Dim0, schema.Dim1, schema.Dim2);
                    if (schema.IsUserDefined)
                    {
                        charaData.UserVariables[schema.Name] = val;
                    }
                    else
                    {
                        charaData.BuiltinVariables[schema.Name] = val;
                    }
                }

                foreach (CharacterSlotSchema schema in chars.StrSlotSchemas)
                {
                    if (!schema.IsSavedata ||
                        schema.GlobalSlotId == chars.NameSlotId ||
                        schema.GlobalSlotId == chars.CallNameSlotId ||
                        schema.GlobalSlotId == chars.NickNameSlotId ||
                        schema.GlobalSlotId == chars.MasterNameSlotId)
                    {
                        continue;
                    }

                    ReadOnlySpan<string> raw = chars.GetStrSpan(c, schema.GlobalSlotId);
                    EraSaveValue val = PackStrSaveValue(raw, schema.SubRank, schema.Dim0, schema.Dim1, schema.Dim2);
                    if (schema.IsUserDefined)
                    {
                        charaData.UserVariables[schema.Name] = val;
                    }
                    else
                    {
                        charaData.BuiltinVariables[schema.Name] = val;
                    }
                }

                snap.Characters.Add(charaData);
            }
        }

        // 2. Non-character SAVEDATA / GLOBAL variables (1D, 2D, 3D)
        for (int i = 0; i < vars.SlotCount; i++)
        {
            VariableSlotStorage? slot = vars.TryGetSlot(i);
            if (slot is null || slot.IsConst || slot.IsCharaData || slot.CalcKind != BuiltinCalcKind.None)
            {
                continue;
            }

            bool include = fileType == EraSaveFileType.Global ? slot.IsGlobal : (!slot.IsGlobal && slot.IsSavedata);
            if (!include)
            {
                continue;
            }

            if (slot.IntData is not null)
            {
                snap.Variables[slot.Name] = PackIntSaveValue(slot.IntData, Math.Max(1, slot.Rank), slot.Dim0, slot.Dim1, slot.Dim2);
            }
            else if (slot.StrData is not null)
            {
                snap.Variables[slot.Name] = PackStrSaveValue(slot.StrData, Math.Max(1, slot.Rank), slot.Dim0, slot.Dim1, slot.Dim2);
            }
        }

        // 3. VarExt containers: Map, Xml, DataTable
        bool wantGlobal = fileType == EraSaveFileType.Global;
        foreach (string mapName in vars.Maps.MapNames)
        {
            if (vars.Maps.IsGlobal(mapName) != wantGlobal) continue;
            if (vars.Maps.TryGetMap(mapName) is { } map)
            {
                snap.VarExtVariables[mapName] = EraSaveValue.FromMap(new Dictionary<string, string>(map, StringComparer.Ordinal));
            }
        }

        foreach (string xmlKey in vars.Xmls.DocumentKeys)
        {
            if (vars.Xmls.IsGlobal(xmlKey) != wantGlobal) continue;
            string outerXml = vars.Xmls.ToStr(xmlKey);
            if (!string.IsNullOrEmpty(outerXml))
            {
                snap.VarExtVariables[xmlKey] = EraSaveValue.FromXml(outerXml);
            }
        }

        foreach (string dtName in vars.DataTables.TableNames)
        {
            if (vars.DataTables.IsGlobal(dtName) != wantGlobal) continue;
            if (vars.DataTables.ExportSnapshot(dtName) is { } dtSnap)
            {
                snap.VarExtVariables[dtName] = EraSaveValue.FromDataTable(dtSnap);
            }
        }

        return snap;
    }

    private static void ApplyEraSaveSnapshot(VariableStore vars, EraSaveSnapshot snap, int slotNo)
    {
        vars.ResetNonGlobalVariables();

        // 1. Restore character roster
        CharacterStore chars = vars.Characters;
        foreach (EraCharacterSaveData cData in snap.Characters)
        {
            int idx = chars.AddVoidCharacter();
            if (chars.NoSlotId >= 0) chars.SetInt0D(idx, chars.NoSlotId, cData.No);
            if (chars.IsAssiSlotId >= 0) chars.SetInt0D(idx, chars.IsAssiSlotId, cData.IsAssi);
            if (chars.NameSlotId >= 0) chars.SetStr0D(idx, chars.NameSlotId, cData.Name);
            if (chars.CallNameSlotId >= 0) chars.SetStr0D(idx, chars.CallNameSlotId, cData.CallName);
            if (chars.NickNameSlotId >= 0) chars.SetStr0D(idx, chars.NickNameSlotId, cData.NickName);
            if (chars.MasterNameSlotId >= 0) chars.SetStr0D(idx, chars.MasterNameSlotId, cData.MasterName);

            ApplyCharacterVarDict(chars, idx, cData.BuiltinVariables);
            ApplyCharacterVarDict(chars, idx, cData.UserVariables);
        }

        // 2. Restore non-character SAVEDATA variables
        foreach ((string name, EraSaveValue val) in snap.Variables)
        {
            if (!vars.TryResolveSlot(name, out int slotId)) continue;
            VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
            if (slot.IsGlobal || slot.IsConst || slot.IsCharaData) continue;
            UnpackSaveValueIntoSlot(slot, val);
        }

        // 3. Restore VarExt containers (Map / Xml / DataTable)
        ApplyVarExtSnapshot(vars, snap.VarExtVariables);

        vars.LastLoadNo = slotNo;
        vars.LastLoadVersion = snap.Header.ScriptVersion;
        vars.LastLoadText = snap.Header.SaveDataText;
    }

    private static void ApplyGlobalSnapshot(VariableStore vars, EraSaveSnapshot snap)
    {
        foreach ((string name, EraSaveValue val) in snap.Variables)
        {
            if (!vars.TryResolveSlot(name, out int slotId)) continue;
            VariableSlotStorage slot = vars.GetSlotOrThrow(slotId);
            if (!slot.IsGlobal) continue;
            UnpackSaveValueIntoSlot(slot, val);
        }

        ApplyVarExtSnapshot(vars, snap.VarExtVariables);
    }

    private static void ApplyCharacterVarDict(CharacterStore chars, int charaIdx, Dictionary<string, EraSaveValue> dict)
    {
        foreach ((string key, EraSaveValue val) in dict)
        {
            if (!chars.TryGetSchemaByName(key, out CharacterSlotSchema? schema) || schema is null)
            {
                continue;
            }

            if (schema.ValueKind == VariableValueKind.Int)
            {
                UnpackIntSaveValueIntoFlat(val, chars.GetIntSpan(charaIdx, schema.GlobalSlotId), schema.Dim0, schema.Dim1, schema.Dim2);
            }
            else
            {
                UnpackStrSaveValueIntoFlat(val, chars.GetStrSpan(charaIdx, schema.GlobalSlotId), schema.Dim0, schema.Dim1, schema.Dim2);
            }
        }
    }

    private static void ApplyVarExtSnapshot(VariableStore vars, Dictionary<string, EraSaveValue> varExt)
    {
        foreach ((string key, EraSaveValue val) in varExt)
        {
            switch (val.DataType)
            {
                case EraSaveDataType.Map:
                    vars.Maps.SetMapFromSnapshot(key, val.AsMap());
                    break;
                case EraSaveDataType.Xml:
                    vars.Xmls.SetDocumentFromSnapshot(key, val.AsXml());
                    break;
                case EraSaveDataType.DataTable:
                    vars.DataTables.ImportSnapshot(key, val.AsDataTable());
                    break;
            }
        }
    }

    private static EraSaveValue PackIntSaveValue(ReadOnlySpan<long> flat, int rank, int d0, int d1, int d2)
    {
        if (rank <= 0)
        {
            return EraSaveValue.FromInt(flat.Length > 0 ? flat[0] : 0L);
        }

        if (rank == 1)
        {
            return EraSaveValue.FromIntArray(flat.ToArray());
        }

        if (rank == 2)
        {
            var arr2 = new long[d0, d1];
            for (int i = 0; i < d0; i++)
            {
                for (int j = 0; j < d1; j++)
                {
                    int idx = (i * d1) + j;
                    if (idx < flat.Length) arr2[i, j] = flat[idx];
                }
            }

            return EraSaveValue.FromIntArray2D(arr2);
        }

        var arr3 = new long[d0, d1, d2];
        for (int i = 0; i < d0; i++)
        {
            for (int j = 0; j < d1; j++)
            {
                for (int k = 0; k < d2; k++)
                {
                    int idx = (((i * d1) + j) * d2) + k;
                    if (idx < flat.Length) arr3[i, j, k] = flat[idx];
                }
            }
        }

        return EraSaveValue.FromIntArray3D(arr3);
    }

    private static EraSaveValue PackStrSaveValue(ReadOnlySpan<string> flat, int rank, int d0, int d1, int d2)
    {
        if (rank <= 0)
        {
            return EraSaveValue.FromStr(flat.Length > 0 ? flat[0] : string.Empty);
        }

        if (rank == 1)
        {
            return EraSaveValue.FromStrArray(flat.ToArray());
        }

        if (rank == 2)
        {
            var arr2 = new string[d0, d1];
            for (int i = 0; i < d0; i++)
            {
                for (int j = 0; j < d1; j++)
                {
                    int idx = (i * d1) + j;
                    arr2[i, j] = idx < flat.Length ? (flat[idx] ?? string.Empty) : string.Empty;
                }
            }

            return EraSaveValue.FromStrArray2D(arr2);
        }

        var arr3 = new string[d0, d1, d2];
        for (int i = 0; i < d0; i++)
        {
            for (int j = 0; j < d1; j++)
            {
                for (int k = 0; k < d2; k++)
                {
                    int idx = (((i * d1) + j) * d2) + k;
                    arr3[i, j, k] = idx < flat.Length ? (flat[idx] ?? string.Empty) : string.Empty;
                }
            }
        }

        return EraSaveValue.FromStrArray3D(arr3);
    }

    private static void UnpackSaveValueIntoSlot(VariableSlotStorage slot, EraSaveValue val)
    {
        if (slot.IntData is not null)
        {
            UnpackIntSaveValueIntoFlat(val, slot.IntData, slot.Dim0, slot.Dim1, slot.Dim2);
        }
        else if (slot.StrData is not null)
        {
            UnpackStrSaveValueIntoFlat(val, slot.StrData, slot.Dim0, slot.Dim1, slot.Dim2);
        }
    }

    private static void UnpackIntSaveValueIntoFlat(EraSaveValue val, Span<long> dst, int d0, int d1, int d2)
    {
        switch (val.DataType)
        {
            case EraSaveDataType.Int:
                if (dst.Length > 0) dst[0] = val.AsInt();
                break;
            case EraSaveDataType.IntArray:
            {
                long[] src = val.AsIntArray();
                src.AsSpan(0, Math.Min(src.Length, dst.Length)).CopyTo(dst);
                break;
            }
            case EraSaveDataType.IntArray2D:
            {
                long[,] src = val.AsIntArray2D();
                int r0 = Math.Min(d0, src.GetLength(0));
                int r1 = Math.Min(d1, src.GetLength(1));
                for (int i = 0; i < r0; i++)
                {
                    for (int j = 0; j < r1; j++)
                    {
                        int idx = (i * d1) + j;
                        if ((uint)idx < (uint)dst.Length) dst[idx] = src[i, j];
                    }
                }
                break;
            }
            case EraSaveDataType.IntArray3D:
            {
                long[,,] src = val.AsIntArray3D();
                int r0 = Math.Min(d0, src.GetLength(0));
                int r1 = Math.Min(d1, src.GetLength(1));
                int r2 = Math.Min(d2, src.GetLength(2));
                for (int i = 0; i < r0; i++)
                {
                    for (int j = 0; j < r1; j++)
                    {
                        for (int k = 0; k < r2; k++)
                        {
                            int idx = (((i * d1) + j) * d2) + k;
                            if ((uint)idx < (uint)dst.Length) dst[idx] = src[i, j, k];
                        }
                    }
                }
                break;
            }
        }
    }

    private static void UnpackStrSaveValueIntoFlat(EraSaveValue val, Span<string> dst, int d0, int d1, int d2)
    {
        switch (val.DataType)
        {
            case EraSaveDataType.Str:
                if (dst.Length > 0) dst[0] = val.AsStr();
                break;
            case EraSaveDataType.StrArray:
            {
                string[] src = val.AsStrArray();
                src.AsSpan(0, Math.Min(src.Length, dst.Length)).CopyTo(dst);
                break;
            }
            case EraSaveDataType.StrArray2D:
            {
                string[,] src = val.AsStrArray2D();
                int r0 = Math.Min(d0, src.GetLength(0));
                int r1 = Math.Min(d1, src.GetLength(1));
                for (int i = 0; i < r0; i++)
                {
                    for (int j = 0; j < r1; j++)
                    {
                        int idx = (i * d1) + j;
                        if ((uint)idx < (uint)dst.Length) dst[idx] = src[i, j] ?? string.Empty;
                    }
                }
                break;
            }
            case EraSaveDataType.StrArray3D:
            {
                string[,,] src = val.AsStrArray3D();
                int r0 = Math.Min(d0, src.GetLength(0));
                int r1 = Math.Min(d1, src.GetLength(1));
                int r2 = Math.Min(d2, src.GetLength(2));
                for (int i = 0; i < r0; i++)
                {
                    for (int j = 0; j < r1; j++)
                    {
                        for (int k = 0; k < r2; k++)
                        {
                            int idx = (((i * d1) + j) * d2) + k;
                            if ((uint)idx < (uint)dst.Length) dst[idx] = src[i, j, k] ?? string.Empty;
                        }
                    }
                }
                break;
            }
        }
    }

    private void AutoDetectInlineBracketButtons(string text)
    {
        // Emuera automatically turns "[123] Choice" patterns in non-plain PRINT lines into clickable integer buttons
        int open = text.IndexOf('[');
        while (open >= 0)
        {
            int close = text.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }

            ReadOnlySpan<char> inner = text.AsSpan(open + 1, close - open - 1).Trim();
            if (!inner.IsEmpty && long.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long btnVal))
            {
                RegisterButtonDescriptor(text[open..(close + 1)], isInt: true, btnVal);
            }

            open = text.IndexOf('[', close + 1);
        }
    }

    private void ExtractHtmlButtons(string html)
    {
        ReadOnlySpan<char> span = html.AsSpan();
        int idx = span.IndexOf("<button", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            int tagEnd = span[idx..].IndexOf('>');
            if (tagEnd < 0) break;
            ReadOnlySpan<char> tag = span.Slice(idx, tagEnd + 1);
            int valPos = tag.IndexOf("value", StringComparison.OrdinalIgnoreCase);
            if (valPos >= 0)
            {
                int eq = tag[valPos..].IndexOf('=');
                if (eq >= 0)
                {
                    ReadOnlySpan<char> afterEq = tag[(valPos + eq + 1)..].TrimStart();
                    if (!afterEq.IsEmpty && (afterEq[0] is '\'' or '"'))
                    {
                        char q = afterEq[0];
                        int closeQ = afterEq[1..].IndexOf(q);
                        if (closeQ >= 0)
                        {
                            string valStr = afterEq.Slice(1, closeQ).ToString();
                            if (long.TryParse(valStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long intBtn))
                            {
                                RegisterButtonDescriptor(valStr, isInt: true, intBtn, valStr);
                            }
                            else
                            {
                                RegisterButtonDescriptor(valStr, isInt: false, 0L, valStr);
                            }
                        }
                    }
                }
            }

            span = span[(idx + tagEnd + 1)..];
            idx = span.IndexOf("<button", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SyncLineCount()
    {
        if (FastVariables is not null)
        {
            FastVariables.ContextLineCount = ConsoleBuffer.LineCount;
        }
    }

    private string ResolveCurrentSourceFile()
    {
        if (_callStack.Count == 0) return string.Empty;
        int pc = Math.Max(0, _callStack[^1].ProgramCounter - 1);
        return Module.SourceMap.TryLookup(pc, out SourceLocation loc) ? loc.DisplayName : string.Empty;
    }

    private long ResolveCurrentSourceLine()
    {
        if (_callStack.Count == 0) return 0L;
        int pc = Math.Max(0, _callStack[^1].ProgramCounter - 1);
        return Module.SourceMap.TryLookup(pc, out SourceLocation loc) ? loc.LineNo : 0L;
    }

    internal void EmitSnapshotInternal() => DisplaySink?.OnDisplaySnapshot(CaptureSnapshot());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Graphics.Dispose();
        Sprites.Dispose();
        Fonts.Dispose();
        Audio.Dispose();
        if (OwnsFileSystem)
        {
            FileSystem.Dispose();
        }
    }
}
