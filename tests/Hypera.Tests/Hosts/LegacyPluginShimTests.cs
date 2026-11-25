using System.Data;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Hypera.Bytecode;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using Xunit;

namespace Hypera.Tests.Hosts;

public sealed class LegacyPluginShimTests
{
    private sealed class MockVariableStore : IVariableStore
    {
        private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FLAG"] = 0,
            ["RESULT"] = 1,
            ["STR"] = 2,
            ["RESULTS"] = 3,
            ["CFLAG"] = 4,
            ["NAME"] = 5,
        };

        private readonly Dictionary<(int Slot, long Idx), long> _ints = [];
        private readonly Dictionary<(int Slot, long Idx), string> _strs = [];

        public bool TryResolveSlot(string name, out int slotId) => _slots.TryGetValue(name, out slotId);

        public long GetInt(int slotId, ReadOnlySpan<long> indices = default) =>
            _ints.GetValueOrDefault((slotId, indices.Length > 0 ? indices[0] : 0), 0L);

        public void SetInt(int slotId, long value, ReadOnlySpan<long> indices = default) =>
            _ints[(slotId, indices.Length > 0 ? indices[0] : 0)] = value;

        public string GetStr(int slotId, ReadOnlySpan<long> indices = default) =>
            _strs.GetValueOrDefault((slotId, indices.Length > 0 ? indices[0] : 0), string.Empty);

        public void SetStr(int slotId, string value, ReadOnlySpan<long> indices = default) =>
            _strs[(slotId, indices.Length > 0 ? indices[0] : 0)] = value;

        public bool TryResolveKeywordIndex(string variableName, int dimension, string keyword, out long index)
        {
            index = 0;
            return false;
        }

        public DataTable? TryGetDataTable(string tableName) => null;
        public void ResetNonGlobalVariables() { _ints.Clear(); _strs.Clear(); }
    }

    private sealed class MockCharacterStore : ICharacterStore
    {
        private readonly List<long> _charNos = [];
        private readonly Dictionary<(int Chara, int Slot, long Idx), long> _ints = [];
        private readonly Dictionary<(int Chara, int Slot, long Idx), string> _strs = [];

        public int Count => _charNos.Count;
        public int AddCharacter(long templateCharacterNo) { _charNos.Add(templateCharacterNo); return _charNos.Count - 1; }
        public void RemoveCharacterAt(int characterIndex) => _charNos.RemoveAt(characterIndex);
        public void ClearCharacters() { _charNos.Clear(); _ints.Clear(); _strs.Clear(); }
        public long GetCharacterNo(int characterIndex) => _charNos[characterIndex];
        public long GetInt(int characterIndex, int slotId, ReadOnlySpan<long> indices = default) =>
            _ints.GetValueOrDefault((characterIndex, slotId, indices.Length > 0 ? indices[0] : 0), 0L);
        public void SetInt(int characterIndex, int slotId, long value, ReadOnlySpan<long> indices = default) =>
            _ints[(characterIndex, slotId, indices.Length > 0 ? indices[0] : 0)] = value;
        public string GetStr(int characterIndex, int slotId, ReadOnlySpan<long> indices = default) =>
            _strs.GetValueOrDefault((characterIndex, slotId, indices.Length > 0 ? indices[0] : 0), string.Empty);
        public void SetStr(int characterIndex, int slotId, string value, ReadOnlySpan<long> indices = default) =>
            _strs[(characterIndex, slotId, indices.Length > 0 ? indices[0] : 0)] = value;
    }

    [Fact]
    public void DynamicPluginDll_ExecutesWritesBackArgumentsAndUnloadsCollectibleAlc()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_shim_test_{Guid.NewGuid():N}");
        string pluginsDir = Path.Combine(tempDir, "Plugins");
        Directory.CreateDirectory(pluginsDir);
        string pluginDllPath = Path.Combine(pluginsDir, "SampleLegacyPlugin.dll");

        try
        {
            EmitSampleLegacyPluginDll(pluginDllPath);

            var vars = new MockVariableStore();
            vars.SetInt(0, 100, [5]); // FLAG:5 = 100
            vars.SetStr(2, "hello", [1]); // STR:1 = "hello"

            using var vfs = new PhysicalDirectoryVfs(tempDir);
            using var engine = new EmueraEngine(BytecodeModule.Empty, vfs, variables: vars);
            var frame = new CallFrame(0, "MAIN", 0, 4, 4);
            frame.IntRegisters[0] = 42;
            frame.StrRegisters[0] = "hypera";

            WeakReference alcWeakRef = LoadInvokeAndDispose(engine, pluginsDir, vars, frame);

            // Verify mutated PluginMethodParameter values were written back to caller slots/registers!
            Assert.Equal(84, frame.IntRegisters[0]);
            Assert.Equal("HYPERA!", frame.StrRegisters[0]);
            Assert.Equal(999, vars.GetInt(1, [0])); // Plugin wrote PluginManager.GetInstance().RESULT[0] = 999
            Assert.Equal(105, vars.GetInt(0, [5])); // Mutated FLAG:5 via write-back

            // Verify plugin DLL was never locked on disk and collectible AssemblyLoadContext unloads cleanly
            File.Delete(pluginDllPath);
            Assert.False(File.Exists(pluginDllPath));

            for (int i = 0; i < 10 && alcWeakRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(alcWeakRef.IsAlive);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PluginManager_SupportsConsoleMethodsCharContextAndExecuteLineHandler()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_shim_api_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var vars = new MockVariableStore();
            var chars = new MockCharacterStore();
            int charIdx = chars.AddCharacter(77);

            string? executedLine = null;
            using var vfs = new PhysicalDirectoryVfs(tempDir);
            using var engine = new EmueraEngine(BytecodeModule.Empty, vfs, variables: vars, characters: chars);
            using var pm = new PluginManager(engine, line => executedLine = line);

            Assert.Throws<InvalidOperationException>(() => PluginManager.GetInstance());

            using (pm.EnterScope())
            {
                Assert.Same(pm, PluginManager.GetInstance());

                PluginAPICharContext ctx = PluginManager.CreateCharContext(charIdx);
                ctx.NAME.Set("Reimu");
                ctx.CFLAG[3] = 456;
                Assert.Equal("Reimu", ctx.NAME.Get());
                Assert.Equal(456, ctx.CFLAG[3]);
                Assert.Equal([77L], pm.GetCharacterIDs());

                pm.SetFont("Consolas");
                pm.SetBgColor(Color.FromArgb(255, 16, 32, 48));
                pm.Print("Hello");
                pm.PrintButton("Play", 10);
                pm.PrintBar();
                pm.PrintNewLine();
                pm.ExecuteLine("PRINTL TEST");

                Assert.Equal("Consolas", pm.CurrentFontName);
                Assert.Equal(0x102030FFu, pm.CurrentBackgroundColorRgba);
                Assert.Equal("PRINTL TEST", executedLine);

                DisplaySnapshot snap = engine.CaptureSnapshot();
                Assert.Contains(snap.Operations, op => op.Kind == DisplayOperationKind.PrintTextSpan && op.Text == "Hello");
                Assert.Contains(snap.Operations, op => op.Kind == DisplayOperationKind.PrintButtonSpan && op.ButtonIntValue == 10);
                Assert.Contains(snap.Operations, op => op.Kind == DisplayOperationKind.DrawLineRule);
            }

            Assert.Throws<InvalidOperationException>(() => PluginManager.GetInstance());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadInvokeAndDispose(
        EmueraEngine engine,
        string pluginsDir,
        MockVariableStore vars,
        CallFrame frame)
    {
        using var pm = new PluginManager(engine);
        IReadOnlyList<WeakReference> refs = pm.LoadPluginsFromDirectory(pluginsDir);
        Assert.Single(refs);
        Assert.True(pm.HasMethod("MutateAndTouchEngine"));

        pm.InvokeMethod("MutateAndTouchEngine",
        [
            PluginCallArgument.FromIntRegister(frame, 0),
            PluginCallArgument.FromStrRegister(frame, 0),
            PluginCallArgument.FromVariableIntSlot(vars, 0, 5),
        ]);

        pm.UnloadPlugins();
        return refs[0];
    }

    private static void EmitSampleLegacyPluginDll(string outputPath)
    {
        var asmName = new AssemblyName("SampleLegacyPlugin");
        var ab = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        ModuleBuilder mb = ab.DefineDynamicModule("SampleLegacyPlugin.dll");

        TypeBuilder methodTb = mb.DefineType(
            "SamplePlugin.MutateMethod",
            TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(object),
            [typeof(IPluginMethod)]);

        PropertyBuilder nameProp = methodTb.DefineProperty("Name", PropertyAttributes.None, typeof(string), null);
        MethodBuilder getName = methodTb.DefineMethod(
            "get_Name",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(string),
            Type.EmptyTypes);
        ILGenerator ilName = getName.GetILGenerator();
        ilName.Emit(OpCodes.Ldstr, "MutateAndTouchEngine");
        ilName.Emit(OpCodes.Ret);
        nameProp.SetGetMethod(getName);

        PropertyBuilder descProp = methodTb.DefineProperty("Description", PropertyAttributes.None, typeof(string), null);
        MethodBuilder getDesc = methodTb.DefineMethod(
            "get_Description",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(string),
            Type.EmptyTypes);
        ILGenerator ilDesc = getDesc.GetILGenerator();
        ilDesc.Emit(OpCodes.Ldstr, "Mutates args and updates RESULT[0]");
        ilDesc.Emit(OpCodes.Ret);
        descProp.SetGetMethod(getDesc);

        MethodBuilder execMethod = methodTb.DefineMethod(
            "Execute",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void),
            [typeof(PluginMethodParameter[])]);
        ILGenerator il = execMethod.GetILGenerator();

        FieldInfo intValueField = typeof(PluginMethodParameter).GetField(nameof(PluginMethodParameter.intValue))!;
        FieldInfo strValueField = typeof(PluginMethodParameter).GetField(nameof(PluginMethodParameter.strValue))!;
        MethodInfo toUpper = typeof(string).GetMethod(nameof(string.ToUpperInvariant), Type.EmptyTypes)!;
        MethodInfo concat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;
        MethodInfo getInstance = typeof(PluginManager).GetMethod(nameof(PluginManager.GetInstance), Type.EmptyTypes)!;
        FieldInfo resultField = typeof(PluginManager).GetField(nameof(PluginManager.RESULT))!;
        MethodInfo setResultItem = typeof(GlobalInt1dWrapper).GetMethod("set_Item", [typeof(long), typeof(long)])!;

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, intValueField);
        il.Emit(OpCodes.Ldc_I8, 2L);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Stfld, intValueField);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, strValueField);
        il.Emit(OpCodes.Callvirt, toUpper);
        il.Emit(OpCodes.Ldstr, "!");
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Stfld, strValueField);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, intValueField);
        il.Emit(OpCodes.Ldc_I8, 5L);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, intValueField);

        il.Emit(OpCodes.Call, getInstance);
        il.Emit(OpCodes.Ldfld, resultField);
        il.Emit(OpCodes.Ldc_I8, 0L);
        il.Emit(OpCodes.Ldc_I8, 999L);
        il.Emit(OpCodes.Callvirt, setResultItem);
        il.Emit(OpCodes.Ret);

        Type methodType = methodTb.CreateType();

        TypeBuilder manifestTb = mb.DefineType(
            "SamplePlugin.PluginManifest",
            TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(PluginManifestAbstract));

        void EmitStringProp(string propName, string value)
        {
            PropertyBuilder p = manifestTb.DefineProperty(propName, PropertyAttributes.None, typeof(string), null);
            MethodBuilder getter = manifestTb.DefineMethod(
                "get_" + propName,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(string),
                Type.EmptyTypes);
            ILGenerator pil = getter.GetILGenerator();
            pil.Emit(OpCodes.Ldstr, value);
            pil.Emit(OpCodes.Ret);
            p.SetGetMethod(getter);
        }

        EmitStringProp(nameof(PluginManifestAbstract.PluginName), "SampleLegacyPlugin");
        EmitStringProp(nameof(PluginManifestAbstract.PluginDescription), "Test plugin");
        EmitStringProp(nameof(PluginManifestAbstract.PluginVersion), "1.0.0");
        EmitStringProp(nameof(PluginManifestAbstract.PluginAuthor), "Hypera");

        ConstructorBuilder ctor = manifestTb.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        ILGenerator cil = ctor.GetILGenerator();
        cil.Emit(OpCodes.Ldarg_0);
        cil.Emit(OpCodes.Call, typeof(PluginManifestAbstract).GetConstructor(Type.EmptyTypes)!);
        cil.Emit(OpCodes.Ldarg_0);
        cil.Emit(OpCodes.Ldfld, typeof(PluginManifestAbstract).GetField("methods", BindingFlags.Instance | BindingFlags.NonPublic)!);
        cil.Emit(OpCodes.Newobj, methodType.GetConstructor(Type.EmptyTypes)!);
        cil.Emit(OpCodes.Callvirt, typeof(List<IPluginMethod>).GetMethod(nameof(List<IPluginMethod>.Add))!);
        cil.Emit(OpCodes.Ret);

        manifestTb.CreateType();

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        ab.Save(fs);
    }
}
