using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Data;
using Hypera.Persistence;
using Hypera.Runtime;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class MemoryOptimizationTests
{
    [Fact]
    public void VariableStore_LazyHybridGrowableAndPagedSlots_PreserveSemanticsAndMinimizeAllocations()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_p0_lazy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                #DIM SMALL_ARR, 1000
                #DIMS SMALL_STR, 1000
                #DIM SAVEDATA GIANT_INT, 200000
                #DIMS SAVEDATA GIANT_STR, 200000
                #DIM INIT_ARR, 10 = 5, 6, 7
                #DIMS INIT_STR, 10 = "alpha", "beta"
                #DIM UNUSED_GIANT, 500000

                @SYSTEM_TITLE
                ; 1. Read untouched giant & write zero (must remain unallocated)
                A:0 = UNUSED_GIANT:123456
                UNUSED_GIANT:123456 = 0
                VARSET UNUSED_GIANT, 0

                ; 2. Growable small/medium array: write low, read high, grow high
                SMALL_ARR:3 = 33
                A:1 = SMALL_ARR:500
                SMALL_ARR:700 = 707
                A:2 = SMALL_ARR:3
                A:3 = SMALL_ARR:700

                SMALL_STR:2 '= "hi"
                TSTR:0 '= SMALL_STR:400
                SMALL_STR:600 '= "there"
                TSTR:1 '= SMALL_STR:2
                TSTR:2 '= SMALL_STR:600

                ; 3. Giant paged array: scattered writes, partial & full VARSET
                GIANT_INT:5 = 55
                GIANT_INT:150000 = 150000
                A:4 = GIANT_INT:99999
                VARSET GIANT_INT, 7, 1000, 1005
                A:5 = GIANT_INT:1002
                A:6 = GIANT_INT:1005
                VARSET GIANT_INT, 0, 1001, 1003
                A:7 = GIANT_INT:1000
                A:8 = GIANT_INT:1001
                A:9 = GIANT_INT:1003

                GIANT_STR:7 '= "card7"
                GIANT_STR:180000 '= "card180k"
                TSTR:3 '= GIANT_STR:7
                TSTR:4 '= GIANT_STR:180000
                TSTR:5 '= GIANT_STR:50000

                ; 4. Initializer invariant: write 0 over non-zero init sticks as 0, RESETDATA restores
                A:10 = INIT_ARR:0
                INIT_ARR:0 = 0
                A:11 = INIT_ARR:0
                A:12 = INIT_ARR:1
                VARSET INIT_ARR, 0
                A:13 = INIT_ARR:1

                TSTR:6 '= INIT_STR:0
                INIT_STR:0 '= ""
                TSTR:7 '= INIT_STR:0
                TSTR:8 '= INIT_STR:1

                ; 5. In-memory SAVEDATA / LOADDATA round-trip on giant paged SAVEDATA slots
                SAVEDATA 0, "PagedSave"
                VARSET GIANT_INT, 0
                VARSET GIANT_STR, ""
                A:14 = GIANT_INT:150000
                TSTR:9 '= GIANT_STR:180000
                CALL COLD_FUNC_CHECK
                QUIT

                @COLD_FUNC_CHECK
                ; FunctionStatic LOCAL/ARG zero write should work cleanly
                LOCAL:0 = 0
                LOCAL:12 = 99
                A:15 = LOCAL:12
                A:16 = LOCAL:500
                RETURN 0

                @NEVER_CALLED_1
                LOCAL:0 = 1
                RETURN 0

                @NEVER_CALLED_2
                LOCAL:0 = 2
                RETURN 0
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            VariableStore vars = host.Engine.FastVariables!;

            // Before stepping, cold functions and UNUSED_GIANT are NOT materialized!
            Assert.True(vars.TryResolveExistingSlot("UNUSED_GIANT", out int unusedId));
            Assert.Null(vars.TryGetMaterializedSlot(unusedId));
            Assert.True(vars.TryResolveExistingSlot("LOCAL@NEVER_CALLED_1", out int never1Id));
            Assert.Null(vars.TryGetMaterializedSlot(never1Id));

            // Bounds check still fires on unmaterialized slots
            Assert.Throws<IndexOutOfRangeException>(() => vars.GetInt1D(unusedId, -1));
            Assert.Throws<IndexOutOfRangeException>(() => vars.GetInt1D(unusedId, 500000));

            Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());

            // Verify UNUSED_GIANT & NEVER_CALLED_* never materialized backing storage
            VariableSlotStorage? unusedSlot = vars.TryGetMaterializedSlot(unusedId);
            Assert.True(unusedSlot is null || !unusedSlot.HasAllocatedStorage);
            Assert.Null(vars.TryGetMaterializedSlot(never1Id));

            // Verify all read/write semantics
            Assert.Equal(0L, vars.GetInt("A", 0));
            Assert.Equal(0L, vars.GetInt("A", 1));
            Assert.Equal(33L, vars.GetInt("A", 2));
            Assert.Equal(707L, vars.GetInt("A", 3));
            Assert.Equal(0L, vars.GetInt("A", 4));
            Assert.Equal(7L, vars.GetInt("A", 5));
            Assert.Equal(0L, vars.GetInt("A", 6));
            Assert.Equal(7L, vars.GetInt("A", 7));
            Assert.Equal(0L, vars.GetInt("A", 8));
            Assert.Equal(7L, vars.GetInt("A", 9));
            Assert.Equal(5L, vars.GetInt("A", 10));
            Assert.Equal(0L, vars.GetInt("A", 11));
            Assert.Equal(6L, vars.GetInt("A", 12));
            Assert.Equal(0L, vars.GetInt("A", 13));
            Assert.Equal(0L, vars.GetInt("A", 14));
            Assert.Equal(99L, vars.GetInt("A", 15));
            Assert.Equal(0L, vars.GetInt("A", 16));

            Assert.Equal(string.Empty, vars.GetStr("TSTR", 0));
            Assert.Equal("hi", vars.GetStr("TSTR", 1));
            Assert.Equal("there", vars.GetStr("TSTR", 2));
            Assert.Equal("card7", vars.GetStr("TSTR", 3));
            Assert.Equal("card180k", vars.GetStr("TSTR", 4));
            Assert.Equal(string.Empty, vars.GetStr("TSTR", 5));
            Assert.Equal("alpha", vars.GetStr("TSTR", 6));
            Assert.Equal(string.Empty, vars.GetStr("TSTR", 7));
            Assert.Equal("beta", vars.GetStr("TSTR", 8));
            Assert.Equal(string.Empty, vars.GetStr("TSTR", 9));

            // Verify ResetNonGlobalVariables restores INIT_ARR/INIT_STR defaults
            vars.ResetNonGlobalVariables();
            Assert.Equal(5L, vars.GetInt("INIT_ARR", 0));
            Assert.Equal(6L, vars.GetInt("INIT_ARR", 1));
            Assert.Equal("alpha", vars.GetStr("INIT_STR", 0));
            Assert.Equal("beta", vars.GetStr("INIT_STR", 1));

            // Verify in-memory save restore recovers giant paged SAVEDATA slots
            Assert.True(vars.TryLoadFromMemorySlot(0));
            Assert.Equal(55L, vars.GetInt("GIANT_INT", 5));
            Assert.Equal(150000L, vars.GetInt("GIANT_INT", 150000));
            Assert.Equal(7L, vars.GetInt("GIANT_INT", 1000));
            Assert.Equal(0L, vars.GetInt("GIANT_INT", 1001));
            Assert.Equal("card7", vars.GetStr("GIANT_STR", 7));
            Assert.Equal("card180k", vars.GetStr("GIANT_STR", 180000));
            Assert.Equal(string.Empty, vars.GetStr("GIANT_STR", 50000));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void CharacterStore_LazyGrowableArrays_VirtualDefaults_AndSharedGamePackage_IsolateConcurrentSessions()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_p1_shared_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));
        Directory.CreateDirectory(Path.Combine(workDir, "CSV"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "CSV", "_Replace.csv"),
                "RelationDef,100\n汚れの初期値,0/0/2/1\n");
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                FOR LOCAL, 0, 50
                    ADDVOIDCHARA
                NEXT
                ; Read virtual RELATION/STAIN defaults without forcing allocation on untouched characters
                A:0 = RELATION:10:5
                A:1 = STAIN:10:2
                A:2 = STAIN:10:3
                ; Mutate one character's RELATION and CFLAG
                RELATION:0:5 = 180
                CFLAG:0:200 = 777
                CSTR:0:5 '= "memo5"
                A:3 = RELATION:0:5
                A:4 = RELATION:0:6
                A:5 = CFLAG:0:200
                TSTR:0 '= CSTR:0:5
                ; Exercise lazy function hydration via GETVAR("LOCAL@HELPER_FN")
                CALL HELPER_FN
                A:6 = GETVAR("LOCAL@HELPER_FN:3")
                QUIT

                @HELPER_FN
                LOCAL:3 = 4321
                RETURN 0

                @COLD_UNHYDRATED_FN
                LOCAL:0 = 999
                RETURN 0
                """);

            using var vfs = new PhysicalDirectoryVfs(workDir);
            SharedGamePackage shared = HyperaGameHost.LoadSharedPackage(vfs, usePrebuiltBytecode: false);

            // Cold functions are NOT eagerly hydrated into shared.SharedSymbols at package load time
            Assert.Empty(shared.SharedSymbols.AllFunctions);

            using HyperaGameHost host1 = HyperaGameHost.LoadFromVfs(
                vfs,
                ownsVfs: false,
                options: new HyperaGameLoadOptions { AutoStartSystemFlow = true, SharedPackage = shared });
            using HyperaGameHost host2 = HyperaGameHost.LoadFromVfs(
                vfs,
                ownsVfs: false,
                options: new HyperaGameLoadOptions { AutoStartSystemFlow = true, SharedPackage = shared });

            // Both sessions share the exact same immutable Module, GameData, Config, and SymbolTable
            Assert.Same(host1.Module, host2.Module);
            Assert.Same(host1.GameData, host2.GameData);
            Assert.Same(host1.Config, host2.Config);
            Assert.Same(host1.DynamicCompiler.Symbols, host2.DynamicCompiler.Symbols);

            Assert.Equal(EngineStepStatus.Completed, host1.Engine.Step());
            Assert.Equal(EngineStepStatus.Completed, host2.Engine.Step());

            VariableStore v1 = host1.Engine.FastVariables!;
            VariableStore v2 = host2.Engine.FastVariables!;

            Assert.Equal(100L, v1.GetInt("A", 0));
            Assert.Equal(2L, v1.GetInt("A", 1));
            Assert.Equal(1L, v1.GetInt("A", 2));
            Assert.Equal(180L, v1.GetInt("A", 3));
            Assert.Equal(100L, v1.GetInt("A", 4));
            Assert.Equal(777L, v1.GetInt("A", 5));
            Assert.Equal("memo5", v1.GetStr("TSTR", 0));
            Assert.Equal(4321L, v1.GetInt("A", 6));

            // 50 characters x ~29 slots would be ~1,450 arrays eagerly; lazily only ~3 arrays were allocated!
            Assert.True(v1.Characters.AllocatedSlotArrayCount < 20,
                $"Expected lazy character storage (<20 allocated arrays across 50 chars), got {v1.Characters.AllocatedSlotArrayCount}.");

            // Complete mutable state isolation between concurrent sessions sharing SharedGamePackage
            v1.SetInt("FLAG", 9999, 0);
            v1.Characters.SetInt1D(0, v1.Characters.CFlagSlotId, 200, 1234);
            Assert.Equal(0L, v2.GetInt("FLAG", 0));
            Assert.Equal(777L, v2.Characters.GetInt1D(0, v2.Characters.CFlagSlotId, 200));

            // COLD_UNHYDRATED_FN still untouched in shared symbol table
            Assert.DoesNotContain(shared.SharedSymbols.AllFunctions, static f => f.Name.Equals("COLD_UNHYDRATED_FN", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
