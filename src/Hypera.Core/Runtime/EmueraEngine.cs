using Hypera.Audio;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Hypera.Rendering;
using Hypera.VirtualFs;

namespace Hypera.Runtime;

/// <summary>
/// Fully instance-isolated Hypera execution engine (zero static mutable game state).
/// Multiple <see cref="EmueraEngine"/> instances can run concurrently in the same process.
/// </summary>
public sealed class EmueraEngine : IDisposable
{
    private readonly List<CallFrame> _callStack = [];
    private readonly List<DisplayOperation> _displayOperations = [];
    private long _frameSequence;
    private long _inputGeneration;
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
        Variables = variables;
        Characters = characters;
        Audio = audio ?? new NullAudioBackend();
        DisplaySink = displaySink;
        Viewport = new LogicalViewport(
            Options.WindowX,
            Options.WindowY,
            Options.FontSize,
            Options.LineHeight);
    }

    public BytecodeModule Module { get; }
    public IVirtualFileSystem FileSystem { get; }
    public RuntimeOptions Options { get; }
    public IVariableStore? Variables { get; }
    public ICharacterStore? Characters { get; }
    public IAudioBackend Audio { get; }
    public IDisplaySnapshotSink? DisplaySink { get; }
    public LogicalViewport Viewport { get; private set; }

    public EngineStepStatus Status { get; private set; } = EngineStepStatus.Running;
    public InputRequest? PendingInputRequest { get; private set; }
    public Exception? FaultException { get; private set; }
    public IReadOnlyList<CallFrame> CallStack => _callStack;

    public void PushEntryFrame(FunctionDescriptor entryFunction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entryFunction);

        _callStack.Add(new CallFrame(
            entryFunction.FunctionId,
            entryFunction.Name,
            entryFunction.EntryPc,
            entryFunction.IntRegisterCount,
            entryFunction.StrRegisterCount));
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
        var instructions = Module.Instructions;

        try
        {
            int executed = 0;
            while (executed < budget)
            {
                if (_callStack.Count == 0)
                {
                    Status = EngineStepStatus.Completed;
                    return Status;
                }

                CallFrame frame = _callStack[^1];
                if ((uint)frame.ProgramCounter >= (uint)instructions.Length)
                {
                    _callStack.RemoveAt(_callStack.Count - 1);
                    continue;
                }

                Instruction insn = instructions[frame.ProgramCounter++];
                executed++;

                switch (insn.Op)
                {
                    case OpCode.Nop:
                        break;

                    case OpCode.Halt:
                        _callStack.Clear();
                        Status = EngineStepStatus.Completed;
                        return Status;

                    case OpCode.ReturnVoid:
                        _callStack.RemoveAt(_callStack.Count - 1);
                        break;

                    case OpCode.YieldFrame:
                        Status = EngineStepStatus.Yielded;
                        EmitSnapshot();
                        return Status;

                    case OpCode.WaitInput:
                        _inputGeneration++;
                        PendingInputRequest = new InputRequest(
                            _inputGeneration,
                            (InputRequestKind)insn.Aux);
                        Status = EngineStepStatus.WaitingInput;
                        EmitSnapshot();
                        return Status;

                    default:
                        throw new NotSupportedException(
                            $"Opcode '{insn.Op}' execution will be wired by the Wave 2 VM executor.");
                }
            }

            Status = _callStack.Count == 0 ? EngineStepStatus.Completed : EngineStepStatus.Yielded;
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

        PendingInputRequest = null;
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
            Options.BackColorRgba,
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

    private void EmitSnapshot() => DisplaySink?.OnDisplaySnapshot(CaptureSnapshot());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Audio.Dispose();
    }
}
