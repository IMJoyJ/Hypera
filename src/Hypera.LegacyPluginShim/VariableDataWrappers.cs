using Hypera.Runtime;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem;

public class GlobalInt1dWrapper
{
    private readonly Func<EmueraEngine?> _engineAccessor;
    private readonly string _variableName;

    internal GlobalInt1dWrapper(Func<EmueraEngine?> engineAccessor, string variableName)
    {
        _engineAccessor = engineAccessor;
        _variableName = variableName;
    }

    public long this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public long this[long key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public long Get(string key) => Get(ResolveKeyword(_engineAccessor(), _variableName, key));

    public long Get(long key)
    {
        IVariableStore store = RequireVariables(_engineAccessor());
        return store.TryResolveSlot(_variableName, out int slot) ? store.GetInt(slot, [key]) : 0L;
    }

    public void Set(string key, long value) => Set(ResolveKeyword(_engineAccessor(), _variableName, key), value);

    public void Set(long key, long value)
    {
        IVariableStore store = RequireVariables(_engineAccessor());
        if (store.TryResolveSlot(_variableName, out int slot))
        {
            store.SetInt(slot, value, [key]);
        }
    }

    internal static IVariableStore RequireVariables(EmueraEngine? engine) =>
        engine?.Variables ?? throw new InvalidOperationException("No active Hypera EmueraEngine with IVariableStore is bound to this execution context.");

    internal static ICharacterStore RequireCharacters(EmueraEngine? engine) =>
        engine?.Characters ?? throw new InvalidOperationException("No active Hypera EmueraEngine with ICharacterStore is bound to this execution context.");

    internal static long ResolveKeyword(EmueraEngine? engine, string varName, string key)
    {
        IVariableStore store = RequireVariables(engine);
        return store.TryResolveKeywordIndex(varName, 1, key, out long idx)
            ? idx
            : throw new KeyNotFoundException($"Unknown CSV keyword '{key}' for variable '{varName}'.");
    }
}

public class GlobalString1dWrapper
{
    private readonly Func<EmueraEngine?> _engineAccessor;
    private readonly string _variableName;

    internal GlobalString1dWrapper(Func<EmueraEngine?> engineAccessor, string variableName)
    {
        _engineAccessor = engineAccessor;
        _variableName = variableName;
    }

    public string this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public string this[long key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public string Get(string key) => Get(GlobalInt1dWrapper.ResolveKeyword(_engineAccessor(), _variableName, key));

    public string Get(long key)
    {
        IVariableStore store = GlobalInt1dWrapper.RequireVariables(_engineAccessor());
        return store.TryResolveSlot(_variableName, out int slot) ? store.GetStr(slot, [key]) : string.Empty;
    }

    public void Set(string key, string value) => Set(GlobalInt1dWrapper.ResolveKeyword(_engineAccessor(), _variableName, key), value);

    public void Set(long key, string value)
    {
        IVariableStore store = GlobalInt1dWrapper.RequireVariables(_engineAccessor());
        if (store.TryResolveSlot(_variableName, out int slot))
        {
            store.SetStr(slot, value, [key]);
        }
    }
}

public class GlobalConstString1dWrapper
{
    private readonly Func<EmueraEngine?> _engineAccessor;
    private readonly string _variableName;

    internal GlobalConstString1dWrapper(Func<EmueraEngine?> engineAccessor, string variableName)
    {
        _engineAccessor = engineAccessor;
        _variableName = variableName;
    }

    public string this[string key] => Get(key);
    public string this[long key] => Get(key);

    public string Get(string key) => Get(GlobalInt1dWrapper.ResolveKeyword(_engineAccessor(), _variableName, key));

    public string Get(long key)
    {
        IVariableStore store = GlobalInt1dWrapper.RequireVariables(_engineAccessor());
        return store.TryResolveSlot(_variableName, out int slot) ? store.GetStr(slot, [key]) : string.Empty;
    }
}

public class GlobalConstInt1dWrapper
{
    private readonly Func<EmueraEngine?> _engineAccessor;
    private readonly string _variableName;

    internal GlobalConstInt1dWrapper(Func<EmueraEngine?> engineAccessor, string variableName)
    {
        _engineAccessor = engineAccessor;
        _variableName = variableName;
    }

    public long this[string key] => Get(key);
    public long this[long key] => Get(key);

    public long Get(string key) => Get(GlobalInt1dWrapper.ResolveKeyword(_engineAccessor(), _variableName, key));

    public long Get(long key)
    {
        IVariableStore store = GlobalInt1dWrapper.RequireVariables(_engineAccessor());
        return store.TryResolveSlot(_variableName, out int slot) ? store.GetInt(slot, [key]) : 0L;
    }
}

public class CharInt1dWrapper
{
    private readonly EmueraEngine? _engine;
    private readonly int _charIndex;
    private readonly string _variableName;

    internal CharInt1dWrapper(EmueraEngine? engine, long charId, string variableName)
    {
        _engine = engine;
        _charIndex = checked((int)charId);
        _variableName = variableName;
    }

    public long this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public long this[long key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public long Get(string key) => Get(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, key));

    public long Get(long key)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        return vars.TryResolveSlot(_variableName, out int slot) ? chars.GetInt(_charIndex, slot, [key]) : 0L;
    }

    public void Set(string key, long value) => Set(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, key), value);

    public void Set(long key, long value)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        if (vars.TryResolveSlot(_variableName, out int slot))
        {
            chars.SetInt(_charIndex, slot, value, [key]);
        }
    }
}

public class CharInt2dWrapper
{
    private readonly EmueraEngine? _engine;
    private readonly int _charIndex;
    private readonly string _variableName;

    internal CharInt2dWrapper(EmueraEngine? engine, long charId, string variableName)
    {
        _engine = engine;
        _charIndex = checked((int)charId);
        _variableName = variableName;
    }

    public long this[string keyA, string keyB] { get => Get(keyA, keyB); set => Set(keyA, keyB, value); }
    public long this[long keyA, string keyB] { get => Get(keyA, keyB); set => Set(keyA, keyB, value); }
    public long this[string keyA, long keyB] { get => Get(keyA, keyB); set => Set(keyA, keyB, value); }
    public long this[long keyA, long keyB] { get => Get(keyA, keyB); set => Set(keyA, keyB, value); }

    public long Get(string keyA, string keyB) =>
        Get(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyA), GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyB));

    public long Get(long keyA, string keyB) =>
        Get(keyA, GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyB));

    public long Get(string keyA, long keyB) =>
        Get(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyA), keyB);

    public long Get(long keyA, long keyB)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        return vars.TryResolveSlot(_variableName, out int slot) ? chars.GetInt(_charIndex, slot, [keyA, keyB]) : 0L;
    }

    public void Set(string keyA, string keyB, long value) =>
        Set(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyA), GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyB), value);

    public void Set(long keyA, string keyB, long value) =>
        Set(keyA, GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyB), value);

    public void Set(string keyA, long keyB, long value) =>
        Set(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, keyA), keyB, value);

    public void Set(long keyA, long keyB, long value)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        if (vars.TryResolveSlot(_variableName, out int slot))
        {
            chars.SetInt(_charIndex, slot, value, [keyA, keyB]);
        }
    }
}

public class CharUserdefinedInt1dWrapper
{
    private readonly EmueraEngine? _engine;
    private readonly int _charIndex;

    internal CharUserdefinedInt1dWrapper(EmueraEngine? engine, long charId)
    {
        _engine = engine;
        _charIndex = checked((int)charId);
    }

    public long this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public long this[string key, long idx]
    {
        get => Get(key, idx);
        set => Set(key, value, idx);
    }

    public long Get(string key, long idx = 0)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        return vars.TryResolveSlot(key, out int slot) ? chars.GetInt(_charIndex, slot, [idx]) : 0L;
    }

    public void Set(string key, long value, long idx = 0)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        if (vars.TryResolveSlot(key, out int slot))
        {
            chars.SetInt(_charIndex, slot, value, [idx]);
        }
    }
}

public class CharStringWrapper
{
    private readonly EmueraEngine? _engine;
    private readonly int _charIndex;
    private readonly string _variableName;

    internal CharStringWrapper(EmueraEngine? engine, long charId, string variableName)
    {
        _engine = engine;
        _charIndex = checked((int)charId);
        _variableName = variableName;
    }

    public string Get()
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        return vars.TryResolveSlot(_variableName, out int slot) ? chars.GetStr(_charIndex, slot) : string.Empty;
    }

    public void Set(string value)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        if (vars.TryResolveSlot(_variableName, out int slot))
        {
            chars.SetStr(_charIndex, slot, value);
        }
    }
}

public class CharString1dWrapper
{
    private readonly EmueraEngine? _engine;
    private readonly int _charIndex;
    private readonly string _variableName;

    internal CharString1dWrapper(EmueraEngine? engine, long charId, string variableName)
    {
        _engine = engine;
        _charIndex = checked((int)charId);
        _variableName = variableName;
    }

    public string this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public string this[long key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public string Get(string key) => Get(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, key));

    public string Get(long key)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        return vars.TryResolveSlot(_variableName, out int slot) ? chars.GetStr(_charIndex, slot, [key]) : string.Empty;
    }

    public void Set(string key, string value) => Set(GlobalInt1dWrapper.ResolveKeyword(_engine, _variableName, key), value);

    public void Set(long key, string value)
    {
        IVariableStore vars = GlobalInt1dWrapper.RequireVariables(_engine);
        ICharacterStore chars = GlobalInt1dWrapper.RequireCharacters(_engine);
        if (vars.TryResolveSlot(_variableName, out int slot))
        {
            chars.SetStr(_charIndex, slot, value, [key]);
        }
    }
}
