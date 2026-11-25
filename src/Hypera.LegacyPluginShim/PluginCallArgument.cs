using Hypera.Runtime;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem;

/// <summary>
/// Binds a caller-side argument (literal, variable slot, character slot, call-frame register, or delegate)
/// to a <see cref="PluginMethodParameter"/> so mutations made inside <see cref="IPluginMethod.Execute"/>
/// are written back to the caller after execution.
/// </summary>
public sealed class PluginCallArgument
{
    private readonly Func<PluginMethodParameter> _reader;
    private readonly Action<PluginMethodParameter>? _writer;

    private PluginCallArgument(Func<PluginMethodParameter> reader, Action<PluginMethodParameter>? writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public bool CanWriteBack => _writer is not null;

    public PluginMethodParameter CreateParameter() => _reader();

    public void WriteBack(PluginMethodParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        _writer?.Invoke(parameter);
    }

    public static PluginCallArgument FromValue(long value) =>
        new(() => new PluginMethodParameter(value), writer: null);

    public static PluginCallArgument FromValue(string value) =>
        new(() => new PluginMethodParameter(value ?? string.Empty), writer: null);

    public static PluginCallArgument FromIntRef(Func<long> getter, Action<long> setter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);
        return new(
            () => new PluginMethodParameter(getter()),
            p => setter(p.intValue));
    }

    public static PluginCallArgument FromStrRef(Func<string> getter, Action<string> setter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);
        return new(
            () => new PluginMethodParameter(getter() ?? string.Empty),
            p => setter(p.strValue ?? string.Empty));
    }

    public static PluginCallArgument FromIntRegister(CallFrame frame, int registerIndex)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new(
            () => new PluginMethodParameter(frame.IntRegisters[registerIndex]),
            p => frame.IntRegisters[registerIndex] = p.intValue);
    }

    public static PluginCallArgument FromStrRegister(CallFrame frame, int registerIndex)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new(
            () => new PluginMethodParameter(frame.StrRegisters[registerIndex]),
            p => frame.StrRegisters[registerIndex] = p.strValue ?? string.Empty);
    }

    public static PluginCallArgument FromVariableIntSlot(IVariableStore store, int slotId, params long[] indices)
    {
        ArgumentNullException.ThrowIfNull(store);
        long[] captured = indices.Length == 0 ? [] : (long[])indices.Clone();
        return new(
            () => new PluginMethodParameter(store.GetInt(slotId, captured)),
            p => store.SetInt(slotId, p.intValue, captured));
    }

    public static PluginCallArgument FromVariableStrSlot(IVariableStore store, int slotId, params long[] indices)
    {
        ArgumentNullException.ThrowIfNull(store);
        long[] captured = indices.Length == 0 ? [] : (long[])indices.Clone();
        return new(
            () => new PluginMethodParameter(store.GetStr(slotId, captured)),
            p => store.SetStr(slotId, p.strValue ?? string.Empty, captured));
    }

    public static PluginCallArgument FromCharacterIntSlot(ICharacterStore store, int characterIndex, int slotId, params long[] indices)
    {
        ArgumentNullException.ThrowIfNull(store);
        long[] captured = indices.Length == 0 ? [] : (long[])indices.Clone();
        return new(
            () => new PluginMethodParameter(store.GetInt(characterIndex, slotId, captured)),
            p => store.SetInt(characterIndex, slotId, p.intValue, captured));
    }

    public static PluginCallArgument FromCharacterStrSlot(ICharacterStore store, int characterIndex, int slotId, params long[] indices)
    {
        ArgumentNullException.ThrowIfNull(store);
        long[] captured = indices.Length == 0 ? [] : (long[])indices.Clone();
        return new(
            () => new PluginMethodParameter(store.GetStr(characterIndex, slotId, captured)),
            p => store.SetStr(characterIndex, slotId, p.strValue ?? string.Empty, captured));
    }
}
