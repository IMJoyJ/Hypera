using System.Collections.Immutable;
using System.Globalization;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;

namespace Hypera.Runtime;

/// <summary>
/// Fine-grained internal state of the Era game system lifecycle controller.
/// </summary>
public enum SystemFlowState : byte
{
    Inactive = 0,
    NormalScript = 1,

    TitleBegin,
    OpeningWaitInput,
    OpeningTitleLoadGame,

    FirstBegin,

    ShopBegin,
    ShopAfterEventShop,
    AutoSaveAfterSaveInfo,
    AutoSaveAfterCustom,
    ShopAfterShowShop,
    ShopWaitInput,
    ShopAfterEventBuy,

    TrainBegin,
    TrainAfterEventTrain,
    TrainAfterShowStatus,
    TrainComAbleLoop,
    TrainAfterShowUserCom,
    TrainWaitInput,
    TrainAfterEventCom,
    TrainAfterComXX,
    TrainAfterSourceCheck,
    TrainAfterEventComEnd,
    TrainComEndWaitAnyKey,
    TrainDoTrain,

    AfterTrainBegin,

    AblUpBegin,
    AblUpAfterShowJuel,
    AblUpAfterShowSelect,
    AblUpWaitInput,
    AblUpAfterAblUpXX,

    TurnEndBegin,

    SaveGameBegin,
    SaveGameWaitInput,
    SaveGameWaitOverwrite,
    SaveGameAfterSaveInfo,

    LoadGameBegin,
    LoadGameWaitInput,
    LoadGameOpeningBegin,
    LoadGameOpeningWaitInput,

    LoadDataAfterLoaded,
    LoadDataAfterSystemLoadEnd,
    LoadDataAfterEventLoad,
}

/// <summary>
/// Stackless priority-grouped event invocation cursor supporting <c>#ONLY</c>, <c>#PRI</c>, normal, <c>#LATER</c>,
/// and <c>#SINGLE</c> group-local interruption (<c>RESULT == 1</c> skips only the remaining handlers in the current priority group).
/// </summary>
public sealed class EventDispatchCursor
{
    private readonly List<EventHandlerEntry>[] _priorityTiers; // [0]=Only, [1]=Pri, [2]=Normal, [3]=Later
    private readonly bool _hasOnly;
    private int _currentTier;
    private int _indexInTier = -1;

    public EventDispatchCursor(string eventName, ImmutableArray<EventHandlerEntry> handlers, int callerDepth)
    {
        EventName = eventName;
        CallerStackDepth = callerDepth;
        _priorityTiers = [[], [], [], []];

        foreach (EventHandlerEntry h in handlers)
        {
            if ((h.Flags & EventDirectiveFlags.Only) != 0)
            {
                if (_priorityTiers[0].Count == 0)
                {
                    _priorityTiers[0].Add(h);
                }

                _hasOnly = true;
            }
        }

        if (_hasOnly)
        {
            _currentTier = 0;
            return;
        }

        // Partition into #PRI (1), Normal (2), #LATER (3), handling dual #PRI+#LATER entries cleanly
        bool hasSeparateLaterDuplicates = false;
        var seenFuncLater = new HashSet<int>();
        foreach (EventHandlerEntry h in handlers)
        {
            if ((h.Flags & EventDirectiveFlags.Later) != 0 && (h.Flags & EventDirectiveFlags.Pri) == 0)
            {
                seenFuncLater.Add(h.FunctionId);
            }
        }

        foreach (EventHandlerEntry h in handlers)
        {
            bool isPri = (h.Flags & EventDirectiveFlags.Pri) != 0;
            bool isLater = (h.Flags & EventDirectiveFlags.Later) != 0;

            if (isPri)
            {
                _priorityTiers[1].Add(h);
                if (isLater && !seenFuncLater.Contains(h.FunctionId) && !hasSeparateLaterDuplicates)
                {
                    _priorityTiers[3].Add(new EventHandlerEntry(h.FunctionId, h.Flags & ~EventDirectiveFlags.Pri, h.DefinitionOrder));
                }
            }
            else if (isLater)
            {
                _priorityTiers[3].Add(h);
            }
            else
            {
                _priorityTiers[2].Add(h);
            }
        }

        _currentTier = 1;
    }

    private EventDispatchCursor(
        string eventName,
        int callerStackDepth,
        List<EventHandlerEntry>[] priorityTiers,
        bool hasOnly,
        int currentTier,
        int indexInTier,
        EventHandlerEntry? activeHandler)
    {
        EventName = eventName;
        CallerStackDepth = callerStackDepth;
        _priorityTiers = priorityTiers;
        _hasOnly = hasOnly;
        _currentTier = currentTier;
        _indexInTier = indexInTier;
        ActiveHandler = activeHandler;
    }

    internal EventDispatchCursor CloneSnapshot()
    {
        var tiersCopy = new List<EventHandlerEntry>[_priorityTiers.Length];
        for (int i = 0; i < _priorityTiers.Length; i++)
        {
            tiersCopy[i] = [.. _priorityTiers[i]];
        }

        return new EventDispatchCursor(
            EventName,
            CallerStackDepth,
            tiersCopy,
            _hasOnly,
            _currentTier,
            _indexInTier,
            ActiveHandler);
    }

    public string EventName { get; }
    public int CallerStackDepth { get; }
    public EventHandlerEntry? ActiveHandler { get; private set; }

    public bool TryAdvance(long lastResultValue, bool isFirstAdvance, out EventHandlerEntry nextHandler)
    {
        if (!isFirstAdvance && ActiveHandler is EventHandlerEntry prev)
        {
            if (_hasOnly || (prev.Flags & EventDirectiveFlags.Only) != 0)
            {
                ActiveHandler = null;
                nextHandler = default;
                return false;
            }

            // Upstream Emuera 1752+1756: #SINGLE returning strictly 1 aborts current priority tier ONLY!
            if ((prev.Flags & EventDirectiveFlags.Single) != 0 && lastResultValue == 1L)
            {
                _currentTier++;
                _indexInTier = -1;
            }
        }

        while (_currentTier < _priorityTiers.Length)
        {
            _indexInTier++;
            List<EventHandlerEntry> tierList = _priorityTiers[_currentTier];
            if (_indexInTier < tierList.Count)
            {
                nextHandler = tierList[_indexInTier];
                ActiveHandler = nextHandler;
                return true;
            }

            if (_hasOnly)
            {
                break;
            }

            _currentTier++;
            _indexInTier = -1;
        }

        ActiveHandler = null;
        nextHandler = default;
        return false;
    }
}

/// <summary>
/// Saved execution snapshot captured when entering modal <c>SAVEGAME</c> / <c>LOADGAME</c> system screens,
/// restored if the user selects <c>[100] Cancel</c> or completes saving.
/// </summary>
internal sealed record SuspendedSystemSnapshot(
    SystemFlowState FlowState,
    SystemPhaseKind CurrentPhase,
    List<CallFrame> SavedCallStack,
    List<EventDispatchCursor> SavedEventStack);

/// <summary>
/// Stackless Era system flow state machine driving <c>Title</c> -&gt; <c>EventFirst</c> -&gt; <c>Shop</c> / <c>@EVENTBUY</c> / <c>@USERSHOP</c>
/// -&gt; <c>Train</c> / <c>@COM_ABLE</c> / <c>@COM</c> / <c>@SOURCE_CHECK</c> / <c>@EVENTCOMEND</c> / <c>DOTRAIN</c> / <c>CALLTRAIN</c>
/// -&gt; <c>AfterTrain</c> -&gt; <c>AblUp</c> -&gt; <c>TurnEnd</c> -&gt; <c>Save</c>/<c>Load</c> and <c>BEGIN</c>/<c>FORCE_BEGIN</c>.
/// </summary>
public sealed class SystemFlowController
{
    public const int AutoSaveSlotIndex = 99;
    private readonly EmueraEngine _engine;

    // Train command state
    private int[] _comAbleMap = [];
    private int _lastCheckedComCode = -1;
    private int _lastAddedDisplayCom = -1;
    private int _printedComColumns;
    private long _doTrainCom = -1;
    private bool _needWaitAfterEventComEnd;

    // CALLTRAIN batch command queue
    private readonly Queue<long> _callTrainCommands = [];
    private bool _isCallTrainActive;

    // SAVEGAME / LOADGAME dialog state
    private SuspendedSystemSnapshot? _suspendedModalState;
    private int _saveDialogPage;
    private int _pendingSaveTargetSlot = -1;
    private bool[] _slotAvailableFlags = new bool[21];

    public SystemFlowController(EmueraEngine engine)
    {
        _engine = engine;
    }

    public bool IsActive => State != SystemFlowState.Inactive;
    public SystemFlowState State { get; private set; } = SystemFlowState.Inactive;
    public SystemPhaseKind CurrentPhase { get; private set; } = SystemPhaseKind.Unknown;
    public bool IsAwaitingSystemInput { get; private set; }
    public bool IsCallTrainActive => _isCallTrainActive;

    /// <summary>
    /// Starts or transitions the Era system state machine via <c>BEGIN</c> or <c>FORCE_BEGIN</c>,
    /// unwinding the entire call stack and event stack.
    /// </summary>
    public void BeginPhase(SystemPhaseKind phase, bool isForceBegin = false)
    {
        if (!isForceBegin && _engine.IsInsideMethodFunction())
        {
            throw new InvalidOperationException($"Cannot execute BEGIN {phase} inside a #FUNCTION/#FUNCTIONS method (use FORCE_BEGIN if intended).");
        }

        if (phase is SystemPhaseKind.Quit or SystemPhaseKind.QuitAndRestart or SystemPhaseKind.ForceQuit or SystemPhaseKind.ForceQuitAndRestart)
        {
            _engine.HaltFromSystemQuit(restartRequested: phase is SystemPhaseKind.QuitAndRestart or SystemPhaseKind.ForceQuitAndRestart);
            State = SystemFlowState.Inactive;
            return;
        }

        if (phase is SystemPhaseKind.SaveGame or SystemPhaseKind.LoadGame)
        {
            EnterSaveOrLoadDialog(isSave: phase == SystemPhaseKind.SaveGame, fromOpening: false);
            return;
        }

        _suspendedModalState = null;
        IsAwaitingSystemInput = false;
        _engine.UnwindAllFramesForBegin();
        CurrentPhase = phase;

        State = phase switch
        {
            SystemPhaseKind.Title => SystemFlowState.TitleBegin,
            SystemPhaseKind.First => SystemFlowState.FirstBegin,
            SystemPhaseKind.Shop => SystemFlowState.ShopBegin,
            SystemPhaseKind.Train => SystemFlowState.TrainBegin,
            SystemPhaseKind.AfterTrain => SystemFlowState.AfterTrainBegin,
            SystemPhaseKind.AblUp => SystemFlowState.AblUpBegin,
            SystemPhaseKind.TurnEnd => SystemFlowState.TurnEndBegin,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, $"Unsupported BEGIN phase '{phase}'."),
        };
    }

    public void BeginPhaseByName(string phaseKeyword, bool isForceBegin = false)
    {
        SystemPhaseKind kind = (phaseKeyword ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "TITLE" => SystemPhaseKind.Title,
            "FIRST" => SystemPhaseKind.First,
            "SHOP" => SystemPhaseKind.Shop,
            "TRAIN" => SystemPhaseKind.Train,
            "AFTERTRAIN" => SystemPhaseKind.AfterTrain,
            "ABLUP" => SystemPhaseKind.AblUp,
            "TURNEND" => SystemPhaseKind.TurnEnd,
            _ => throw new InvalidOperationException($"Unknown BEGIN system phase keyword '{phaseKeyword}'."),
        };

        BeginPhase(kind, isForceBegin);
    }

    /// <summary>
    /// <c>DOTRAIN comId</c>: unwinds active script frames and immediately executes training command <paramref name="comId"/>.
    /// </summary>
    public void StartDoTrain(long comId)
    {
        _doTrainCom = comId;
        IsAwaitingSystemInput = false;
        _engine.UnwindAllFramesForBegin();
        CurrentPhase = SystemPhaseKind.Train;
        State = SystemFlowState.TrainDoTrain;
    }

    /// <summary>
    /// <c>CALLTRAIN count</c>: queues <c>SELECTCOM:1 .. SELECTCOM:count</c> for non-interactive batch training execution.
    /// </summary>
    public void StartCallTrain(long count)
    {
        _callTrainCommands.Clear();
        VariableStore vars = _engine.RequireVariableStore();
        ReadOnlySpan<long> selectComArr = vars.GetIntSpan(vars.SelectComSlotId);
        int limit = (int)Math.Clamp(count, 0, selectComArr.Length - 1);
        for (int i = 1; i <= limit; i++)
        {
            _callTrainCommands.Enqueue(selectComArr[i]);
        }

        _isCallTrainActive = _callTrainCommands.Count > 0;
        if (_isCallTrainActive)
        {
            IsAwaitingSystemInput = false;
            _engine.UnwindAllFramesForBegin();
            CurrentPhase = SystemPhaseKind.Train;
            State = SystemFlowState.TrainAfterEventTrain;
        }
    }

    /// <summary>
    /// <c>STOPCALLTRAIN</c>: cancels active <c>CALLTRAIN</c> batch execution.
    /// </summary>
    public void StopCallTrain()
    {
        _callTrainCommands.Clear();
        _isCallTrainActive = false;
        _engine.IsSkipDisplay = false;
    }

    /// <summary>
    /// Enters interactive <c>SAVEGAME</c> or <c>LOADGAME</c> modal screen, preserving current call stack for <c>[100] Cancel</c>.
    /// </summary>
    public void EnterSaveOrLoadDialog(bool isSave, bool fromOpening)
    {
        if (!fromOpening)
        {
            _suspendedModalState = _engine.CaptureSuspendedSystemSnapshot(State, CurrentPhase);
            _engine.UnwindAllFramesForBegin();
        }

        CurrentPhase = isSave ? SystemPhaseKind.SaveGame : SystemPhaseKind.LoadGame;
        State = isSave
            ? SystemFlowState.SaveGameBegin
            : fromOpening
                ? SystemFlowState.LoadGameOpeningBegin
                : SystemFlowState.LoadGameBegin;
    }

    /// <summary>
    /// Executes <c>LOADDATA slot</c>: wipes the entire call stack &amp; modal snapshots, loads the slot,
    /// and transitions to <c>@SYSTEM_LOADEND</c> -&gt; <c>@EVENTLOAD</c> -&gt; <c>Shop</c>.
    /// </summary>
    public void ExecuteLoadData(int saveSlot)
    {
        if (!_engine.LoadFromSlot(saveSlot))
        {
            throw new InvalidOperationException($"Failed to load save slot {saveSlot}.");
        }

        _suspendedModalState = null;
        IsAwaitingSystemInput = false;
        _callTrainCommands.Clear();
        _isCallTrainActive = false;
        _engine.UnwindAllFramesForBegin();

        CurrentPhase = SystemPhaseKind.Shop;
        State = SystemFlowState.LoadDataAfterLoaded;
    }

    public void NotifyWaitOccurredInScript() => _needWaitAfterEventComEnd = false;

    public void DeliverSystemInput(InputResponse response)
    {
        IsAwaitingSystemInput = false;
        long inputVal = response.IntValue ?? EastAsianStringOps.ParseEmueraInt(response.StringValue, _engine.Options.LanguageCodePage);

        switch (State)
        {
            case SystemFlowState.OpeningWaitInput:
                HandleOpeningInput(inputVal);
                break;

            case SystemFlowState.ShopWaitInput:
                HandleShopInput(inputVal);
                break;

            case SystemFlowState.TrainWaitInput:
                HandleTrainInput(inputVal);
                break;

            case SystemFlowState.TrainComEndWaitAnyKey:
                State = SystemFlowState.TrainAfterEventTrain;
                break;

            case SystemFlowState.AblUpWaitInput:
                HandleAblUpInput(inputVal);
                break;

            case SystemFlowState.SaveGameWaitInput:
                HandleSaveGameInput(inputVal);
                break;

            case SystemFlowState.SaveGameWaitOverwrite:
                HandleSaveOverwriteInput(inputVal);
                break;

            case SystemFlowState.LoadGameWaitInput:
            case SystemFlowState.LoadGameOpeningWaitInput:
                HandleLoadGameInput(inputVal, fromOpening: State == SystemFlowState.LoadGameOpeningWaitInput);
                break;
        }
    }

    /// <summary>
    /// Advances the Era system state machine whenever the script call stack becomes empty.
    /// Returns <c>true</c> if new frames were pushed or a system input wait was issued;
    /// returns <c>false</c> when system flow has ended cleanly.
    /// </summary>
    public bool AdvanceWhenCallStackEmpty()
    {
        int safetyGuard = 0;
        while (State != SystemFlowState.Inactive && !_engine.HasActiveFrames && !IsAwaitingSystemInput)
        {
            if (++safetyGuard > 4096)
            {
                throw new InvalidOperationException("SystemFlowController exceeded transition safety limit without yielding or pushing a script frame.");
            }

            switch (State)
            {
                case SystemFlowState.NormalScript:
                    // Reached end of phase script (e.g. @EVENTFIRST / @EVENTEND / @EVENTTURNEND without an explicit BEGIN) -> complete cleanly
                    State = SystemFlowState.Inactive;
                    return false;

                case SystemFlowState.TitleBegin:
                    StepTitleBegin();
                    break;

                case SystemFlowState.OpeningTitleLoadGame:
                    State = SystemFlowState.TitleBegin;
                    break;

                case SystemFlowState.FirstBegin:
                    StepFirstBegin();
                    break;

                case SystemFlowState.ShopBegin:
                    StepShopBegin();
                    break;

                case SystemFlowState.ShopAfterEventShop:
                    StepShopAfterEventShop();
                    break;

                case SystemFlowState.AutoSaveAfterSaveInfo:
                    _engine.SaveToSlot(AutoSaveSlotIndex, _engine.RequireVariableStore().SaveDataText);
                    State = SystemFlowState.AutoSaveAfterCustom;
                    break;

                case SystemFlowState.AutoSaveAfterCustom:
                    State = SystemFlowState.ShopAfterShowShop;
                    if (!_engine.TryPushNamedFunction("SHOW_SHOP"))
                    {
                        throw new InvalidOperationException("Required Era system function '@SHOW_SHOP' was not found.");
                    }

                    break;

                case SystemFlowState.ShopAfterShowShop:
                    RequestSystemInput(InputRequestKind.IntValue);
                    State = SystemFlowState.ShopWaitInput;
                    return true;

                case SystemFlowState.ShopAfterEventBuy:
                    State = SystemFlowState.AutoSaveAfterCustom;
                    break;

                case SystemFlowState.TrainBegin:
                    StepTrainBegin();
                    break;

                case SystemFlowState.TrainAfterEventTrain:
                    StepTrainAfterEventTrain();
                    break;

                case SystemFlowState.TrainAfterShowStatus:
                    InitComAbleScan();
                    State = SystemFlowState.TrainComAbleLoop;
                    break;

                case SystemFlowState.TrainComAbleLoop:
                    StepTrainComAbleLoop();
                    break;

                case SystemFlowState.TrainAfterShowUserCom:
                    StepTrainAfterShowUserCom();
                    if (IsAwaitingSystemInput)
                    {
                        return true;
                    }

                    break;

                case SystemFlowState.TrainDoTrain:
                    StepTrainDoTrain();
                    break;

                case SystemFlowState.TrainAfterEventCom:
                    StepTrainAfterEventCom();
                    break;

                case SystemFlowState.TrainAfterComXX:
                    StepTrainAfterComXX();
                    break;

                case SystemFlowState.TrainAfterSourceCheck:
                    StepTrainAfterSourceCheck();
                    break;

                case SystemFlowState.TrainAfterEventComEnd:
                    StepTrainAfterEventComEnd();
                    if (IsAwaitingSystemInput)
                    {
                        return true;
                    }

                    break;

                case SystemFlowState.AfterTrainBegin:
                    StopCallTrain();
                    State = SystemFlowState.NormalScript;
                    if (!_engine.TryPushEventGroup("EVENTEND"))
                    {
                        throw new InvalidOperationException("Required Era event function '@EVENTEND' was not found.");
                    }

                    break;

                case SystemFlowState.AblUpBegin:
                    StopCallTrain();
                    State = SystemFlowState.AblUpAfterShowJuel;
                    if (!_engine.TryPushNamedFunction("SHOW_JUEL"))
                    {
                        throw new InvalidOperationException("Required Era system function '@SHOW_JUEL' was not found.");
                    }

                    break;

                case SystemFlowState.AblUpAfterShowJuel:
                    State = SystemFlowState.AblUpAfterShowSelect;
                    if (!_engine.TryPushNamedFunction("SHOW_ABLUP_SELECT"))
                    {
                        throw new InvalidOperationException("Required Era system function '@SHOW_ABLUP_SELECT' was not found.");
                    }

                    break;

                case SystemFlowState.AblUpAfterShowSelect:
                    RequestSystemInput(InputRequestKind.IntValue);
                    State = SystemFlowState.AblUpWaitInput;
                    return true;

                case SystemFlowState.AblUpAfterAblUpXX:
                    State = SystemFlowState.AblUpBegin;
                    break;

                case SystemFlowState.TurnEndBegin:
                    StopCallTrain();
                    State = SystemFlowState.NormalScript;
                    if (!_engine.TryPushEventGroup("EVENTTURNEND"))
                    {
                        throw new InvalidOperationException("Required Era event function '@EVENTTURNEND' was not found.");
                    }

                    break;

                case SystemFlowState.SaveGameBegin:
                    RenderSaveLoadMenu(isSave: true, fromOpening: false);
                    return true;

                case SystemFlowState.SaveGameAfterSaveInfo:
                    _engine.SaveToSlot(_pendingSaveTargetSlot, _engine.RequireVariableStore().SaveDataText);
                    RestoreSuspendedModalState();
                    break;

                case SystemFlowState.LoadGameBegin:
                    RenderSaveLoadMenu(isSave: false, fromOpening: false);
                    return true;

                case SystemFlowState.LoadGameOpeningBegin:
                    RenderSaveLoadMenu(isSave: false, fromOpening: true);
                    return true;

                case SystemFlowState.LoadDataAfterLoaded:
                    State = SystemFlowState.LoadDataAfterSystemLoadEnd;
                    _engine.TryPushNamedFunction("SYSTEM_LOADEND");
                    break;

                case SystemFlowState.LoadDataAfterSystemLoadEnd:
                    State = SystemFlowState.LoadDataAfterEventLoad;
                    _engine.TryPushEventGroup("EVENTLOAD");
                    break;

                case SystemFlowState.LoadDataAfterEventLoad:
                    // Default post-load fallback in Era is @SHOW_SHOP (without re-running AutoSave)
                    CurrentPhase = SystemPhaseKind.Shop;
                    State = SystemFlowState.AutoSaveAfterCustom;
                    break;

                default:
                    return false;
            }
        }

        return _engine.HasActiveFrames || IsAwaitingSystemInput;
    }

    // -------------------------------------------------------------------------
    // Phase Handlers
    // -------------------------------------------------------------------------

    private void StepTitleBegin()
    {
        StopCallTrain();
        _suspendedModalState = null;

        if (_engine.TryPushNamedFunction("SYSTEM_TITLE"))
        {
            State = SystemFlowState.NormalScript;
            return;
        }

        VariableStore vars = _engine.RequireVariableStore();
        ReplaceConfiguration rep = vars.ReplaceConfig;
        _engine.PrintDrawLine();
        if (!string.IsNullOrEmpty(vars.GameBase.Title))
        {
            _engine.PrintTextLine(vars.GameBase.Title);
        }

        if (!string.IsNullOrEmpty(vars.GameBase.Author))
        {
            _engine.PrintTextLine(vars.GameBase.Author);
        }

        _engine.PrintDrawLine();
        _engine.PrintButtonLine($"[0] {rep.TitleMenuString0}", 0);
        _engine.PrintButtonLine($"[1] {rep.TitleMenuString1}", 1);

        RequestSystemInput(InputRequestKind.IntValue);
        State = SystemFlowState.OpeningWaitInput;
    }

    private void HandleOpeningInput(long choice)
    {
        VariableStore vars = _engine.RequireVariableStore();
        if (choice == 0L)
        {
            vars.ResetNonGlobalVariables();
            vars.Characters.AddDefaultCharacters(vars.GameBase.DefaultChara);
            _engine.PrintDrawLine();
            CurrentPhase = SystemPhaseKind.First;
            State = SystemFlowState.FirstBegin;
        }
        else if (choice == 1L)
        {
            if (_engine.TryPushNamedFunction("TITLE_LOADGAME"))
            {
                State = SystemFlowState.OpeningTitleLoadGame;
            }
            else
            {
                State = SystemFlowState.LoadGameOpeningBegin;
            }
        }
        else
        {
            RequestSystemInput(InputRequestKind.IntValue);
            State = SystemFlowState.OpeningWaitInput;
        }
    }

    private void StepFirstBegin()
    {
        StopCallTrain();
        State = SystemFlowState.NormalScript;
        if (!_engine.TryPushEventGroup("EVENTFIRST"))
        {
            throw new InvalidOperationException("Required Era event function '@EVENTFIRST' was not found.");
        }
    }

    private void StepShopBegin()
    {
        StopCallTrain();
        State = SystemFlowState.ShopAfterEventShop;
        _engine.TryPushEventGroup("EVENTSHOP");
    }

    private void StepShopAfterEventShop()
    {
        if (_engine.Options.AutoSave)
        {
            if (_engine.TryPushNamedFunction("SYSTEM_AUTOSAVE"))
            {
                State = SystemFlowState.AutoSaveAfterCustom;
                return;
            }

            VariableStore vars = _engine.RequireVariableStore();
            vars.SaveDataText = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss ", CultureInfo.InvariantCulture);
            State = SystemFlowState.AutoSaveAfterSaveInfo;
            _engine.TryPushNamedFunction("SAVEINFO");
            return;
        }

        State = SystemFlowState.AutoSaveAfterCustom;
    }

    private void HandleShopInput(long choice)
    {
        VariableStore vars = _engine.RequireVariableStore();
        int maxShopItem = vars.ReplaceConfig.MaxShopItem;

        if (choice >= 0 && choice < maxShopItem)
        {
            ReadOnlySpan<long> sales = vars.GetIntSpan(vars.ItemSalesSlotId);
            ReadOnlySpan<long> prices = vars.GetIntSpan(vars.ItemPriceSlotId);
            Span<long> items = vars.GetIntSpan(vars.ItemSlotId);
            Span<long> money = vars.GetIntSpan(vars.MoneySlotId);

            bool onSale = (ulong)choice < (ulong)sales.Length && sales[(int)choice] != 0;
            long price = (ulong)choice < (ulong)prices.Length ? prices[(int)choice] : 0L;

            if (onSale && money.Length > 0 && money[0] >= price && (ulong)choice < (ulong)items.Length)
            {
                money[0] -= price;
                items[(int)choice] += 1L;
                vars.SetInt0D(vars.BoughtSlotId, choice);
                State = SystemFlowState.ShopAfterEventBuy;
                _engine.TryPushEventGroup("EVENTBUY");
                return;
            }

            // Out of stock or insufficient money -> re-wait in shop
            RequestSystemInput(InputRequestKind.IntValue);
            State = SystemFlowState.ShopWaitInput;
            return;
        }

        vars.Result = choice;
        State = SystemFlowState.ShopAfterEventBuy;
        if (!_engine.TryPushNamedFunction("USERSHOP"))
        {
            throw new InvalidOperationException("Required Era system function '@USERSHOP' was not found.");
        }
    }

    private void StepTrainBegin()
    {
        ResetVariablesOnTrainStart();
        State = SystemFlowState.TrainAfterEventTrain;
        _engine.TryPushEventGroup("EVENTTRAIN");
    }

    private void StepTrainAfterEventTrain()
    {
        VariableStore vars = _engine.RequireVariableStore();
        if (vars.NextCom >= 0)
        {
            vars.SelectCom = vars.NextCom;
            vars.NextCom = -1L;
            BeginExecuteSelectedCom();
            return;
        }

        if (_isCallTrainActive)
        {
            _engine.IsSkipDisplay = true;
        }

        State = SystemFlowState.TrainAfterShowStatus;
        _engine.TryPushNamedFunction("SHOW_STATUS");
    }

    private void InitComAbleScan()
    {
        VariableStore vars = _engine.RequireVariableStore();
        int trainLen = vars.TryGetSlot(vars.TrainNameSlotId)?.TotalLength ?? 0;
        if (_comAbleMap.Length != trainLen)
        {
            _comAbleMap = new int[trainLen];
        }

        _comAbleMap.AsSpan().Fill(-1);
        _lastCheckedComCode = -1;
        _lastAddedDisplayCom = -1;
        _printedComColumns = 0;
    }

    private void StepTrainComAbleLoop()
    {
        VariableStore vars = _engine.RequireVariableStore();
        ReadOnlySpan<string> trainNames = vars.TrainNameSlotId >= 0 ? vars.GetStrSpan(vars.TrainNameSlotId) : [];

        if (_lastCheckedComCode >= 0 && _lastCheckedComCode < trainNames.Length && !string.IsNullOrEmpty(trainNames[_lastCheckedComCode]))
        {
            _lastAddedDisplayCom++;
            if (vars.Result != 0)
            {
                RecordAvailableTrainCom(trainNames[_lastCheckedComCode], _lastCheckedComCode, _lastAddedDisplayCom);
            }
        }

        while (++_lastCheckedComCode < trainNames.Length)
        {
            string tName = trainNames[_lastCheckedComCode];
            if (string.IsNullOrEmpty(tName))
            {
                continue;
            }

            string funcName = $"COM_ABLE{_lastCheckedComCode}";
            if (_engine.TryPushNamedFunction(funcName))
            {
                return; // Suspend loop until @COM_ABLE{N} returns its RESULT!
            }

            _lastAddedDisplayCom++;
            if (vars.ReplaceConfig.ComAbleDefault != 0)
            {
                RecordAvailableTrainCom(tName, _lastCheckedComCode, _lastAddedDisplayCom);
            }
        }

        _engine.FlushPendingLine();

        State = SystemFlowState.TrainAfterShowUserCom;
        _engine.TryPushNamedFunction("SHOW_USERCOM");
    }

    private void RecordAvailableTrainCom(string trainName, int trainCode, int displayNo)
    {
        _comAbleMap[displayNo] = trainCode;
        if (!_isCallTrainActive)
        {
            _engine.PrintButtonColumn($"{trainName}[{displayNo,3}]", displayNo, rightAlign: true);
            _printedComColumns++;
            if (_engine.Options.PrintCPerLine > 0 && _printedComColumns % _engine.Options.PrintCPerLine == 0)
            {
                _engine.PrintNewLine();
            }
        }
    }

    private void StepTrainAfterShowUserCom()
    {
        _engine.IsSkipDisplay = false;
        ResetVariablesAfterShowUserCom();

        if (_isCallTrainActive && _callTrainCommands.Count > 0)
        {
            long nextCom = _callTrainCommands.Dequeue();
            HandleTrainInput(nextCom, isFromCallTrain: true);
            return;
        }

        RequestSystemInput(InputRequestKind.IntValue);
        State = SystemFlowState.TrainWaitInput;
    }

    private void HandleTrainInput(long inputVal, bool isFromCallTrain = false)
    {
        VariableStore vars = _engine.RequireVariableStore();
        int resolvedCom = -1;

        if (isFromCallTrain)
        {
            for (int i = 0; i < _comAbleMap.Length; i++)
            {
                if (_comAbleMap[i] == inputVal)
                {
                    resolvedCom = (int)inputVal;
                    break;
                }
            }
        }
        else if ((ulong)inputVal < (ulong)_comAbleMap.Length)
        {
            resolvedCom = _comAbleMap[(int)inputVal];
        }

        if (resolvedCom >= 0)
        {
            vars.SelectCom = resolvedCom;
            BeginExecuteSelectedCom();
        }
        else
        {
            vars.Result = inputVal;
            _needWaitAfterEventComEnd = false;
            State = SystemFlowState.TrainAfterEventComEnd;
            if (!_engine.TryPushNamedFunction("USERCOM"))
            {
                throw new InvalidOperationException("Required Era system function '@USERCOM' was not found.");
            }
        }
    }

    private void StepTrainDoTrain()
    {
        ResetVariablesAfterShowUserCom();
        _engine.RequireVariableStore().SelectCom = _doTrainCom;
        _doTrainCom = -1;
        BeginExecuteSelectedCom();
    }

    private void BeginExecuteSelectedCom()
    {
        ResetNowExBeforeCom();
        State = SystemFlowState.TrainAfterEventCom;
        _engine.TryPushEventGroup("EVENTCOM");
    }

    private void StepTrainAfterEventCom()
    {
        long selectCom = _engine.RequireVariableStore().SelectCom;
        State = SystemFlowState.TrainAfterComXX;
        if (!_engine.TryPushNamedFunction($"COM{selectCom}"))
        {
            throw new InvalidOperationException($"Required Era command function '@COM{selectCom}' was not found.");
        }
    }

    private void StepTrainAfterComXX()
    {
        VariableStore vars = _engine.RequireVariableStore();
        if (vars.Result == 0L)
        {
            _needWaitAfterEventComEnd = false;
            State = SystemFlowState.TrainAfterEventComEnd;
            return;
        }

        State = SystemFlowState.TrainAfterSourceCheck;
        if (!_engine.TryPushNamedFunction("SOURCE_CHECK"))
        {
            throw new InvalidOperationException("Required Era system function '@SOURCE_CHECK' was not found.");
        }
    }

    private void StepTrainAfterSourceCheck()
    {
        ClearSourceArraysAfterCheck();
        _needWaitAfterEventComEnd = true;
        State = SystemFlowState.TrainAfterEventComEnd;
        _engine.TryPushEventGroup("EVENTCOMEND");
    }

    private void StepTrainAfterEventComEnd()
    {
        if (_isCallTrainActive && _callTrainCommands.Count == 0)
        {
            _isCallTrainActive = false;
            _engine.IsSkipDisplay = false;
            _needWaitAfterEventComEnd = false;
            if (_engine.TryPushNamedFunction("CALLTRAINEND"))
            {
                return;
            }
        }

        if (_needWaitAfterEventComEnd && !_isCallTrainActive)
        {
            _needWaitAfterEventComEnd = false;
            RequestSystemInput(InputRequestKind.AnyKey);
            State = SystemFlowState.TrainComEndWaitAnyKey;
            return;
        }

        State = SystemFlowState.TrainAfterEventTrain;
    }

    private void HandleAblUpInput(long inputVal)
    {
        VariableStore vars = _engine.RequireVariableStore();
        if (inputVal is >= 0 and < 100)
        {
            if (_engine.TryPushNamedFunction($"ABLUP{inputVal}"))
            {
                State = SystemFlowState.AblUpAfterAblUpXX;
            }
            else
            {
                RequestSystemInput(InputRequestKind.IntValue);
                State = SystemFlowState.AblUpWaitInput;
            }
        }
        else
        {
            vars.Result = inputVal;
            State = SystemFlowState.AblUpAfterAblUpXX;
            if (!_engine.TryPushNamedFunction("USERABLUP"))
            {
                throw new InvalidOperationException("Required Era system function '@USERABLUP' was not found.");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Save / Load Dialog Handlers
    // -------------------------------------------------------------------------

    private void RenderSaveLoadMenu(bool isSave, bool fromOpening)
    {
        int saveCount = Math.Max(1, _engine.Options.SaveDataNos);
        if (_slotAvailableFlags.Length != saveCount + 1)
        {
            _slotAvailableFlags = new bool[saveCount + 1];
        }

        _engine.PrintTextLine(isSave ? "Save to which slot?" : "Load from which slot?");
        for (int i = 0; i < 20; i++)
        {
            int slotNo = (_saveDialogPage * 20) + i;
            if (slotNo >= saveCount)
            {
                break;
            }

            bool exists = _engine.CheckSaveSlotExists(slotNo, out string summary);
            _slotAvailableFlags[slotNo] = exists;
            _engine.PrintButtonLine($"[{slotNo,2}] {(exists ? summary : "----")}", slotNo);
        }

        if (!isSave)
        {
            bool autoExists = _engine.CheckSaveSlotExists(AutoSaveSlotIndex, out string autoSummary);
            _slotAvailableFlags[^1] = autoExists;
            _engine.PrintButtonLine($"[{AutoSaveSlotIndex,2}] {(autoExists ? autoSummary : "----")}", AutoSaveSlotIndex);
        }

        _engine.PrintButtonLine("[100] Cancel", 100);
        RequestSystemInput(InputRequestKind.IntValue);
        State = isSave
            ? SystemFlowState.SaveGameWaitInput
            : fromOpening
                ? SystemFlowState.LoadGameOpeningWaitInput
                : SystemFlowState.LoadGameWaitInput;
    }

    private void HandleSaveGameInput(long choice)
    {
        if (choice == 100L)
        {
            RestoreSuspendedModalState();
            return;
        }

        int maxSlots = _slotAvailableFlags.Length - 1;
        if (choice < 0 || choice >= maxSlots)
        {
            RequestSystemInput(InputRequestKind.IntValue);
            return;
        }

        int slot = (int)choice;
        if (slot / 20 != _saveDialogPage)
        {
            _saveDialogPage = slot / 20;
            State = SystemFlowState.SaveGameBegin;
            return;
        }

        _pendingSaveTargetSlot = slot;
        if (_slotAvailableFlags[slot])
        {
            _engine.PrintTextLine("Overwrite existing save data?");
            _engine.PrintButtonColumn("[0] Yes", 0, rightAlign: false);
            _engine.PrintButtonColumn("[1] No", 1, rightAlign: false);
            _engine.PrintNewLine();
            RequestSystemInput(InputRequestKind.IntValue);
            State = SystemFlowState.SaveGameWaitOverwrite;
            return;
        }

        BeginSaveInfoAndCommit();
    }

    private void HandleSaveOverwriteInput(long choice)
    {
        if (choice == 1L)
        {
            State = SystemFlowState.SaveGameBegin;
            return;
        }

        if (choice != 0L)
        {
            RequestSystemInput(InputRequestKind.IntValue);
            return;
        }

        BeginSaveInfoAndCommit();
    }

    private void BeginSaveInfoAndCommit()
    {
        VariableStore vars = _engine.RequireVariableStore();
        vars.SaveDataText = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss ", CultureInfo.InvariantCulture);
        State = SystemFlowState.SaveGameAfterSaveInfo;
        _engine.TryPushNamedFunction("SAVEINFO");
    }

    private void HandleLoadGameInput(long choice, bool fromOpening)
    {
        if (choice == 100L)
        {
            if (fromOpening)
            {
                State = SystemFlowState.TitleBegin;
            }
            else
            {
                RestoreSuspendedModalState();
            }

            return;
        }

        int maxSlots = _slotAvailableFlags.Length - 1;
        bool available = false;
        if (choice == AutoSaveSlotIndex)
        {
            available = _slotAvailableFlags[^1];
        }
        else if (choice >= 0 && choice < maxSlots)
        {
            int slot = (int)choice;
            if (slot / 20 != _saveDialogPage)
            {
                _saveDialogPage = slot / 20;
                State = fromOpening ? SystemFlowState.LoadGameOpeningBegin : SystemFlowState.LoadGameBegin;
                return;
            }

            available = _slotAvailableFlags[slot];
        }
        else
        {
            RequestSystemInput(InputRequestKind.IntValue);
            return;
        }

        if (!available)
        {
            State = fromOpening ? SystemFlowState.LoadGameOpeningBegin : SystemFlowState.LoadGameBegin;
            return;
        }

        ExecuteLoadData((int)choice);
    }

    private void RestoreSuspendedModalState()
    {
        if (_suspendedModalState is SuspendedSystemSnapshot snap)
        {
            _suspendedModalState = null;
            State = snap.FlowState;
            CurrentPhase = snap.CurrentPhase;
            _engine.RestoreSuspendedSystemSnapshot(snap);
        }
        else
        {
            State = SystemFlowState.ShopAfterShowShop;
        }
    }

    private void RequestSystemInput(InputRequestKind kind)
    {
        IsAwaitingSystemInput = true;
        _engine.IssueSystemInputRequest(kind);
    }

    // -------------------------------------------------------------------------
    // Era Built-in Parameter Reset Routines During Train
    // -------------------------------------------------------------------------

    private void ResetVariablesOnTrainStart()
    {
        VariableStore vars = _engine.RequireVariableStore();
        vars.SetInt0D(vars.AssiPlaySlotId, 0L);
        vars.PrevCom = -1L;
        vars.NextCom = -1L;
        vars.ClearSlot(vars.TFlagSlotId);
        vars.ClearSlot(vars.TStrSlotId);

        CharacterStore chars = vars.Characters;
        for (int c = 0; c < chars.Count; c++)
        {
            if (chars.GotJuelSlotId >= 0) chars.ClearCharacterSlot(c, chars.GotJuelSlotId);
            if (chars.TequipSlotId >= 0) chars.ClearCharacterSlot(c, chars.TequipSlotId);
            if (chars.ExSlotId >= 0) chars.ClearCharacterSlot(c, chars.ExSlotId);
            if (chars.SourceSlotId >= 0) chars.ClearCharacterSlot(c, chars.SourceSlotId);
            if (chars.TcvarSlotId >= 0) chars.ClearCharacterSlot(c, chars.TcvarSlotId);
            chars.ResetStain(c);
        }
    }

    private void ResetVariablesAfterShowUserCom()
    {
        VariableStore vars = _engine.RequireVariableStore();
        vars.ClearSlot(vars.UpSlotId);
        vars.ClearSlot(vars.DownSlotId);
        vars.ClearSlot(vars.LoseBaseSlotId);

        CharacterStore chars = vars.Characters;
        for (int c = 0; c < chars.Count; c++)
        {
            if (chars.DownBaseSlotId >= 0) chars.ClearCharacterSlot(c, chars.DownBaseSlotId);
            if (chars.CupSlotId >= 0) chars.ClearCharacterSlot(c, chars.CupSlotId);
            if (chars.CdownSlotId >= 0) chars.ClearCharacterSlot(c, chars.CdownSlotId);
        }
    }

    private void ResetNowExBeforeCom()
    {
        VariableStore vars = _engine.RequireVariableStore();
        CharacterStore chars = vars.Characters;
        for (int c = 0; c < chars.Count; c++)
        {
            if (chars.NowExSlotId >= 0) chars.ClearCharacterSlot(c, chars.NowExSlotId);
        }
    }

    internal sealed record SystemFlowUndoSnapshot(
        SystemFlowState State,
        SystemPhaseKind CurrentPhase,
        bool IsAwaitingSystemInput,
        int[] ComAbleMap,
        int LastCheckedComCode,
        int LastAddedDisplayCom,
        int PrintedComColumns,
        long DoTrainCom,
        bool NeedWaitAfterEventComEnd,
        long[] CallTrainCommands,
        bool IsCallTrainActive,
        SuspendedSystemSnapshot? SuspendedModalState,
        int SaveDialogPage,
        int PendingSaveTargetSlot,
        bool[] SlotAvailableFlags);

    internal SystemFlowUndoSnapshot CaptureUndoSnapshot()
    {
        SuspendedSystemSnapshot? modalCopy = null;
        if (_suspendedModalState is { } m)
        {
            var frames = new List<CallFrame>(m.SavedCallStack.Count);
            for (int i = 0; i < m.SavedCallStack.Count; i++)
            {
                frames.Add(m.SavedCallStack[i].CloneSnapshot());
            }

            var events = new List<EventDispatchCursor>(m.SavedEventStack.Count);
            for (int i = 0; i < m.SavedEventStack.Count; i++)
            {
                events.Add(m.SavedEventStack[i].CloneSnapshot());
            }

            modalCopy = new SuspendedSystemSnapshot(m.FlowState, m.CurrentPhase, frames, events);
        }

        return new SystemFlowUndoSnapshot(
            State: State,
            CurrentPhase: CurrentPhase,
            IsAwaitingSystemInput: IsAwaitingSystemInput,
            ComAbleMap: _comAbleMap.Length > 0 ? [.. _comAbleMap] : [],
            LastCheckedComCode: _lastCheckedComCode,
            LastAddedDisplayCom: _lastAddedDisplayCom,
            PrintedComColumns: _printedComColumns,
            DoTrainCom: _doTrainCom,
            NeedWaitAfterEventComEnd: _needWaitAfterEventComEnd,
            CallTrainCommands: _callTrainCommands.Count > 0 ? _callTrainCommands.ToArray() : [],
            IsCallTrainActive: _isCallTrainActive,
            SuspendedModalState: modalCopy,
            SaveDialogPage: _saveDialogPage,
            PendingSaveTargetSlot: _pendingSaveTargetSlot,
            SlotAvailableFlags: [.. _slotAvailableFlags]);
    }

    internal void RestoreUndoSnapshot(SystemFlowUndoSnapshot snapshot)
    {
        State = snapshot.State;
        CurrentPhase = snapshot.CurrentPhase;
        IsAwaitingSystemInput = snapshot.IsAwaitingSystemInput;
        _comAbleMap = snapshot.ComAbleMap.Length > 0 ? [.. snapshot.ComAbleMap] : [];
        _lastCheckedComCode = snapshot.LastCheckedComCode;
        _lastAddedDisplayCom = snapshot.LastAddedDisplayCom;
        _printedComColumns = snapshot.PrintedComColumns;
        _doTrainCom = snapshot.DoTrainCom;
        _needWaitAfterEventComEnd = snapshot.NeedWaitAfterEventComEnd;
        _callTrainCommands.Clear();
        for (int i = 0; i < snapshot.CallTrainCommands.Length; i++)
        {
            _callTrainCommands.Enqueue(snapshot.CallTrainCommands[i]);
        }

        _isCallTrainActive = snapshot.IsCallTrainActive;
        _suspendedModalState = snapshot.SuspendedModalState;
        _saveDialogPage = snapshot.SaveDialogPage;
        _pendingSaveTargetSlot = snapshot.PendingSaveTargetSlot;
        _slotAvailableFlags = [.. snapshot.SlotAvailableFlags];
    }

    private void ClearSourceArraysAfterCheck()
    {
        VariableStore vars = _engine.RequireVariableStore();
        CharacterStore chars = vars.Characters;
        for (int c = 0; c < chars.Count; c++)
        {
            if (chars.SourceSlotId >= 0) chars.ClearCharacterSlot(c, chars.SourceSlotId);
        }
    }
}
