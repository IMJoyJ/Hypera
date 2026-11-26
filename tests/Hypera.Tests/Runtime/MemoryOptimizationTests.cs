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
}
