using Hypera.Bytecode;
using Hypera.Rendering;

namespace Hypera.Runtime;

public sealed partial class EmueraEngine
{
    internal sealed class EngineUndoCheckpoint : IDisposable
    {
        private bool _disposed;

        public required VariableStore.VariableStoreUndoSnapshot? Variables { get; init; }
        public required VirtualConsoleBuffer.ConsoleBufferUndoSnapshot Console { get; init; }
        public required SystemFlowController.SystemFlowUndoSnapshot SystemFlow { get; init; }
        public required GraphicsManager.GraphicsUndoDelta GraphicsDelta { get; init; }
        public required SpriteRegistry.SpriteUndoDelta SpriteDelta { get; init; }
        public required List<CallFrame> CallStack { get; init; }
        public required List<EventDispatchCursor> EventStack { get; init; }
        public required List<ButtonDescriptor> CurrentCycleButtons { get; init; }
        public required EngineStepStatus Status { get; init; }
        public required InputRequest? PendingInputRequest { get; init; }
        public required PendingScriptInputMeta? PendingScriptInputMeta { get; init; }
        public required long InputGeneration { get; init; }
        public required long ButtonGeneration { get; init; }
        public required long LastAwaitMilliseconds { get; init; }
        public required bool IsSkipDisplay { get; init; }
        public required int CurrentRedrawMode { get; init; }
        public required uint CurrentForeColorRgba { get; init; }
        public required uint CurrentBackColorRgba { get; init; }
        public required int CurrentFontStyle { get; init; }
        public required string CurrentFontName { get; init; }
        public required string CurrentAlignmentName { get; init; }
        public required string TextBoxText { get; init; }
        public required (int X, int Y, int Width)? TextBoxBounds { get; init; }
        public required int AnimeTimerIntervalMs { get; init; }
        public required bool BitmapCacheEnabledForNextLine { get; init; }
        public required int ForceKanaMode { get; init; }
        public required bool SkipLogEnabled { get; init; }
        public required bool FlowInputEnabled { get; init; }
        public required long FlowInputDefaultInt { get; init; }
        public required bool FlowInputCanSkip { get; init; }
        public required bool FlowInputForceSkip { get; init; }
        public required bool FlowInputStringEnabled { get; init; }
        public required string FlowInputDefaultStr { get; init; }
        public required string LastOutputLog { get; init; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            GraphicsDelta.Dispose();
        }
    }

    private readonly LinkedList<EngineUndoCheckpoint> _undoHistory = new();
    private int _maxUndoSteps = 1;

    /// <summary>
    /// Maximum number of <c>INPUT*</c> undo checkpoints retained in memory (default: 1; 0 disables undo).
    /// </summary>
    public int MaxUndoSteps
    {
        get => _maxUndoSteps;
        set
        {
            int normalized = Math.Max(0, value);
            _maxUndoSteps = normalized;
            if (normalized == 0)
            {
                ClearUndoHistory();
            }
            else
            {
                TrimUndoHistoryToLimit();
            }
        }
    }

    /// <summary>Number of rollback checkpoints currently available.</summary>
    public int UndoCount => _undoHistory.Count;

    /// <summary>True when at least one prior <c>INPUT*</c> checkpoint is available for rollback.</summary>
    public bool CanUndo => _undoHistory.Count > 0;

    /// <summary>
    /// Clears and disposes all retained undo checkpoints (e.g., across save/load persistence boundaries).
    /// </summary>
    public void ClearUndoHistory()
    {
        Graphics.DetachUndoDelta();
        Sprites.DetachUndoDelta();

        for (LinkedListNode<EngineUndoCheckpoint>? node = _undoHistory.First; node is not null; node = node.Next)
        {
            node.Value.Dispose();
        }

        _undoHistory.Clear();
    }

    /// <summary>
    /// Rolls back engine state (variables, CoW character roster, call stack, system flow, console buffer,
    /// graphics/sprites, and SFMT-19937 RNG state) to immediately before the most recent accepted <c>INPUT*</c>.
    /// Live <c>System.Random</c> (<c>UseNewRandom</c> / <c>RandNew</c>) is intentionally NOT rolled back.
    /// </summary>
    public bool TryUndo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_undoHistory.Last is null)
        {
            return false;
        }

        EngineUndoCheckpoint checkpoint = _undoHistory.Last.Value;
        _undoHistory.RemoveLast();

        // 1. Revert graphics & dynamic sprites delta for the undone turn.
        Graphics.RestoreUndoDelta(checkpoint.GraphicsDelta);
        Sprites.RestoreUndoDelta(checkpoint.SpriteDelta);

        // 2. Restore variables, CoW character roster, and SFMT RNG (leaving System.Random live stream intact).
        if (FastVariables is not null && checkpoint.Variables is not null)
        {
            FastVariables.RestoreUndoSnapshot(checkpoint.Variables);
        }

        // 3. Restore call stack, event cursors, and system flow state machine.
        _callStack.Clear();
        _callStack.AddRange(checkpoint.CallStack);
        _eventStack.Clear();
        _eventStack.AddRange(checkpoint.EventStack);
        SystemFlow.RestoreUndoSnapshot(checkpoint.SystemFlow);

        // 4. Restore virtual console buffer & active buttons.
        ConsoleBuffer.RestoreUndoSnapshot(checkpoint.Console);
        _currentCycleButtons.Clear();
        _currentCycleButtons.AddRange(checkpoint.CurrentCycleButtons);

        // 5. Restore engine execution status, pending input prompt, and rendering/flow flags.
        FaultException = null;
        RestartRequested = false;
        Status = checkpoint.Status;
        PendingInputRequest = checkpoint.PendingInputRequest;
        _pendingScriptInputMeta = checkpoint.PendingScriptInputMeta;
        _inputGeneration = checkpoint.InputGeneration;
        _buttonGeneration = checkpoint.ButtonGeneration;
        LastAwaitMilliseconds = checkpoint.LastAwaitMilliseconds;
        IsSkipDisplay = checkpoint.IsSkipDisplay;
        CurrentRedrawMode = checkpoint.CurrentRedrawMode;
        CurrentForeColorRgba = checkpoint.CurrentForeColorRgba;
        CurrentBackColorRgba = checkpoint.CurrentBackColorRgba;
        CurrentFontStyle = checkpoint.CurrentFontStyle;
        CurrentFontName = checkpoint.CurrentFontName;
        CurrentAlignmentName = checkpoint.CurrentAlignmentName;
        TextBoxText = checkpoint.TextBoxText;
        TextBoxBounds = checkpoint.TextBoxBounds;
        AnimeTimerIntervalMs = checkpoint.AnimeTimerIntervalMs;
        BitmapCacheEnabledForNextLine = checkpoint.BitmapCacheEnabledForNextLine;
        ForceKanaMode = checkpoint.ForceKanaMode;
        SkipLogEnabled = checkpoint.SkipLogEnabled;
        FlowInputEnabled = checkpoint.FlowInputEnabled;
        FlowInputDefaultInt = checkpoint.FlowInputDefaultInt;
        FlowInputCanSkip = checkpoint.FlowInputCanSkip;
        FlowInputForceSkip = checkpoint.FlowInputForceSkip;
        FlowInputStringEnabled = checkpoint.FlowInputStringEnabled;
        FlowInputDefaultStr = checkpoint.FlowInputDefaultStr;
        LastOutputLog = checkpoint.LastOutputLog;

        checkpoint.Dispose();

        NotifyRedrawFlush();
        EmitSnapshotInternal();
        return true;
    }

    private bool ShouldCaptureUndoCheckpoint(InputRequest? request) =>
        _maxUndoSteps > 0 &&
        request is not null &&
        request.Kind is InputRequestKind.IntValue
            or InputRequestKind.StringValue
            or InputRequestKind.OneInt
            or InputRequestKind.OneString;

    private EngineUndoCheckpoint PushUndoCheckpointBeforeInput()
    {
        bool hasActiveBoundRef = HasAnyActiveBoundRef();
        Dictionary<object, object>? liveToSnap = hasActiveBoundRef
            ? new Dictionary<object, object>(ReferenceEqualityComparer.Instance)
            : null;

        VariableStore.VariableStoreUndoSnapshot? varSnap = FastVariables?.CaptureUndoSnapshot(liveToSnap);

        var savedFrames = new List<CallFrame>(_callStack.Count);
        for (int i = 0; i < _callStack.Count; i++)
        {
            savedFrames.Add(_callStack[i].CloneSnapshot(liveToSnap));
        }

        if (hasActiveBoundRef && liveToSnap is not null)
        {
            RemapCapturedBoundRefsAndProtectLiveCharaRefs(savedFrames, liveToSnap);
        }

        var savedEvents = new List<EventDispatchCursor>(_eventStack.Count);
        for (int i = 0; i < _eventStack.Count; i++)
        {
            savedEvents.Add(_eventStack[i].CloneSnapshot());
        }

        var checkpoint = new EngineUndoCheckpoint
        {
            Variables = varSnap,
            Console = ConsoleBuffer.CaptureUndoSnapshot(),
            SystemFlow = SystemFlow.CaptureUndoSnapshot(),
            GraphicsDelta = Graphics.BeginUndoDelta(),
            SpriteDelta = Sprites.BeginUndoDelta(),
            CallStack = savedFrames,
            EventStack = savedEvents,
            CurrentCycleButtons = [.. _currentCycleButtons],
            Status = Status,
            PendingInputRequest = PendingInputRequest,
            PendingScriptInputMeta = _pendingScriptInputMeta,
            InputGeneration = _inputGeneration,
            ButtonGeneration = _buttonGeneration,
            LastAwaitMilliseconds = LastAwaitMilliseconds,
            IsSkipDisplay = IsSkipDisplay,
            CurrentRedrawMode = CurrentRedrawMode,
            CurrentForeColorRgba = CurrentForeColorRgba,
            CurrentBackColorRgba = CurrentBackColorRgba,
            CurrentFontStyle = CurrentFontStyle,
            CurrentFontName = CurrentFontName,
            CurrentAlignmentName = CurrentAlignmentName,
            TextBoxText = TextBoxText,
            TextBoxBounds = TextBoxBounds,
            AnimeTimerIntervalMs = AnimeTimerIntervalMs,
            BitmapCacheEnabledForNextLine = BitmapCacheEnabledForNextLine,
            ForceKanaMode = ForceKanaMode,
            SkipLogEnabled = SkipLogEnabled,
            FlowInputEnabled = FlowInputEnabled,
            FlowInputDefaultInt = FlowInputDefaultInt,
            FlowInputCanSkip = FlowInputCanSkip,
            FlowInputForceSkip = FlowInputForceSkip,
            FlowInputStringEnabled = FlowInputStringEnabled,
            FlowInputDefaultStr = FlowInputDefaultStr,
            LastOutputLog = LastOutputLog,
        };

        _undoHistory.AddLast(checkpoint);
        return checkpoint;
    }

    private void CommitUndoCheckpoint(EngineUndoCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return;
        }

        TrimUndoHistoryToLimit();
    }

    private void DiscardUncommittedCheckpoint(EngineUndoCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return;
        }

        if (_undoHistory.Last is not null && ReferenceEquals(_undoHistory.Last.Value, checkpoint))
        {
            _undoHistory.RemoveLast();
        }

        Graphics.DetachUndoDelta();
        Sprites.DetachUndoDelta();
        checkpoint.Dispose();
    }

    private void TrimUndoHistoryToLimit()
    {
        while (_undoHistory.Count > _maxUndoSteps && _undoHistory.First is not null)
        {
            EngineUndoCheckpoint oldest = _undoHistory.First.Value;
            _undoHistory.RemoveFirst();
            oldest.Dispose();
        }
    }

    private bool HasAnyActiveBoundRef()
    {
        for (int f = 0; f < _callStack.Count; f++)
        {
            FrameLocalStorage[] locals = _callStack[f].LocalSlots;
            for (int i = 0; i < locals.Length; i++)
            {
                if (locals[i].Scope == VariableScopeKind.Reference && locals[i].IsBoundRef)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RemapCapturedBoundRefsAndProtectLiveCharaRefs(
        List<CallFrame> savedFrames,
        Dictionary<object, object> liveToSnap)
    {
        CharacterStore? chars = FastCharacters;
        Dictionary<object, object>? charaRefSplitMap = null;

        for (int f = 0; f < savedFrames.Count; f++)
        {
            FrameLocalStorage[] snapLocals = savedFrames[f].LocalSlots;
            FrameLocalStorage[] liveLocals = _callStack[f].LocalSlots;

            for (int i = 0; i < snapLocals.Length; i++)
            {
                FrameLocalStorage snapLocal = snapLocals[i];
                if (snapLocal.Scope != VariableScopeKind.Reference || !snapLocal.IsBoundRef)
                {
                    continue;
                }

                if (snapLocal.IntArray is long[] intArr)
                {
                    if (liveToSnap.TryGetValue(intArr, out object? mapped) && mapped is long[] mappedInt)
                    {
                        snapLocal.IntArray = mappedInt;
                    }
                    else if (chars is not null)
                    {
                        // REF points directly into a CoW-shared CharacterRecord array: split the live character
                        // so live writes through REF never mutate the frozen snapshot's CharacterRecord.
                        charaRefSplitMap ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
                        if (!charaRefSplitMap.TryGetValue(intArr, out object? splitObj) &&
                            chars.TrySplitLiveCharacterForRef(intArr, out splitObj) &&
                            splitObj is not null)
                        {
                            charaRefSplitMap[intArr] = splitObj;
                        }

                        if (splitObj is long[] liveSplitInt)
                        {
                            liveLocals[i].IntArray = liveSplitInt;
                        }
                    }
                }

                if (snapLocal.StrArray is string[] strArr)
                {
                    if (liveToSnap.TryGetValue(strArr, out object? mapped) && mapped is string[] mappedStr)
                    {
                        snapLocal.StrArray = mappedStr;
                    }
                    else if (chars is not null)
                    {
                        charaRefSplitMap ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
                        if (!charaRefSplitMap.TryGetValue(strArr, out object? splitObj) &&
                            chars.TrySplitLiveCharacterForRef(strArr, out splitObj) &&
                            splitObj is not null)
                        {
                            charaRefSplitMap[strArr] = splitObj;
                        }

                        if (splitObj is string[] liveSplitStr)
                        {
                            liveLocals[i].StrArray = liveSplitStr;
                        }
                    }
                }
            }
        }
    }
}
