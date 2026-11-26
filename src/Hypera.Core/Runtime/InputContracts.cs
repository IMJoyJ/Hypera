using Hypera.Rendering;

namespace Hypera.Runtime;

public enum EngineStepStatus : byte
{
    Running = 0,
    Yielded = 1,
    WaitingInput = 2,
    Completed = 3,
    Faulted = 4,
}

public enum InputRequestKind : byte
{
    IntValue = 0,
    StringValue = 1,
    AnyKey = 2,
    OneInt = 3,
    OneString = 4,
    MouseClick = 5,
    TimedWait = 6,
}

public sealed record ButtonDescriptor(
    long GenerationId,
    string Text,
    bool IsIntegerButton,
    long IntValue,
    string? StringValue,
    LogicalRect Bounds,
    uint? FocusColorRgba = null);

public sealed record InputRequest(
    long GenerationId,
    InputRequestKind Kind,
    long? TimeoutMilliseconds = null,
    string? TimeoutMessage = null,
    bool AllowMouseHitTest = true,
    IReadOnlyList<ButtonDescriptor>? ActiveButtons = null,
    bool CanSkipWithMessageSkip = false);

/// <summary>
/// Captures CBG button-map and HTML srcm color-pick hit-test slots written to RESULT:3, RESULT:4, and RESULT:6.
/// </summary>
public readonly record struct InputHitTestSlots(
    bool HasHitColor,
    long Result3PrimaryColor,
    long Result4SecondaryColor,
    long Result6HoverOrAuxColor,
    int LogicalX,
    int LogicalY,
    int MouseButton = 0);

public sealed record InputResponse(
    long GenerationId,
    bool IsTimeout = false,
    long? IntValue = null,
    string? StringValue = null,
    InputHitTestSlots HitTest = default);
