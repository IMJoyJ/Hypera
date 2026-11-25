using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Runtime;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class SpanBuiltinsAndCharacterStoreTests
{
    [Fact]
    public void SpanBuiltins_MatchAndFindElement_SimdAndConservativeLiteralBypass()
    {
        long[] nums = [10, 42, -7, 42, 99, 42, 0];
        Assert.Equal(3L, SpanBuiltins.Match(nums, 42L, 0, nums.Length));
        Assert.Equal(1L, SpanBuiltins.FindElement(nums, 42L, 0, nums.Length, last: false));
        Assert.Equal(5L, SpanBuiltins.FindElement(nums, 42L, 0, nums.Length, last: true));

        // Conservative literal detector: plain strings MUST bypass Regex compilation completely!
        var regexes = new RegexCache();
        string[] words = ["alpha", "reimu_hakurei", "marisa", "reimu_clone", "sakuya"];

        long idxSub = SpanBuiltins.FindElement(words, "reimu", regexes, 0, words.Length, isExact: false, last: false);
        long idxExact = SpanBuiltins.FindElement(words, "marisa", regexes, 0, words.Length, isExact: true, last: false);
        long lastSub = SpanBuiltins.FindElement(words, "reimu", regexes, 0, words.Length, isExact: false, last: true);

        Assert.Equal(1L, idxSub);
        Assert.Equal(2L, idxExact);
        Assert.Equal(3L, lastSub);
        Assert.Equal(0, regexes.CachedPatternCount); // Zero Regex instances compiled for literal patterns!

        // Pattern with regex metacharacters compiles & caches into RegexCache
        long idxRegex = SpanBuiltins.FindElement(words, "^reimu_c[a-z]+$", regexes, 0, words.Length, isExact: false, last: false);
        Assert.Equal(3L, idxRegex);
        Assert.Equal(1, regexes.CachedPatternCount);
    }

    [Fact]
    public void SpanBuiltins_ArrayMutationsMultiSortAndHalfOpenInRangeArray()
    {
        long[] arr = [1, 2, 3, 4, 5];
        SpanBuiltins.ArrayShift(arr, shift: 2, defaultValue: 99L, start: 1, count: 3);
        Assert.Equal([1L, 99L, 99L, 2L, 5L], arr);

        SpanBuiltins.ArrayRemove(arr, emptyValue: 0L, start: 1, count: 2);
        Assert.Equal([1L, 2L, 5L, 0L, 0L], arr);

        long[] sortTarget = [50, 10, 40, 20, 0];
        SpanBuiltins.ArraySort(sortTarget, ascending: true, start: 0, count: 4);
        Assert.Equal([10L, 20L, 40L, 50L, 0L], sortTarget);

        // INRANGE is inclusive [min, max], whereas INRANGEARRAY is half-open [min, max) per upstream Emuera!
        Assert.Equal(1L, SpanBuiltins.InRange(10, 5, 10));
        Assert.Equal(2L, SpanBuiltins.InRangeArray([10L, 20L, 40L, 50L], min: 10, max: 40, start: 0, end: 4));

        Assert.Equal(120L, SpanBuiltins.SumArray([10L, 20L, 40L, 50L], 0, 4));
        Assert.Equal(50L, SpanBuiltins.MaxArray([10L, 20L, 40L, 50L], 0, 4));
        Assert.Equal(10L, SpanBuiltins.MinArray([10L, 20L, 40L, 50L], 0, 4));

        // Multi-key permutation (ARRAYMSORTEX)
        long[] keys = [0, 300, 100, 200];
        string[] payload = ["zero", "C", "A", "B"];
        int[] perm = SpanBuiltins.BuildMultiSortPermutation(keys, ascending: true, fixedLength: 4);
        Assert.True(SpanBuiltins.ApplyFirstDimensionPermutation<long>(keys, 4, 1, perm));
        Assert.True(SpanBuiltins.ApplyFirstDimensionPermutation<string>(payload, 4, 1, perm));
        Assert.Equal([0L, 100L, 200L, 300L], keys);
        Assert.Equal(["zero", "A", "B", "C"], payload);

        // Bit ops
        long bits = 0;
        bits = SpanBuiltins.SetBit(bits, 0);
        bits = SpanBuiltins.SetBit(bits, 3);
        bits = SpanBuiltins.SetBit(bits, 62);
        Assert.Equal(1L, SpanBuiltins.GetBit(bits, 3));
        Assert.Equal(0L, SpanBuiltins.GetBit(bits, 2));
        bits = SpanBuiltins.InvertBit(bits, 3);
        bits = SpanBuiltins.InvertBit(bits, 2);
        Assert.Equal(0L, SpanBuiltins.GetBit(bits, 3));
        Assert.Equal(1L, SpanBuiltins.GetBit(bits, 2));
        bits = SpanBuiltins.ClearBit(bits, 2);
        Assert.Equal(0L, SpanBuiltins.GetBit(bits, 2));
    }

    [Fact]
    public void CharacterStore_LifecycleSpCharaSortPickupFindAndUserCharaData()
    {
        var mb = new TestModuleBuilder
        {
            CompilerOptions = CompilerOptions.Default with { CompatiSPChara = true },
        };
        int customCharaSlot = mb.AddStaticVar(1200, "CUSTOM_RANK", VariableValueKind.Int, VariableScopeKind.CharacterDim, 4);
        BytecodeModule module = mb.Build();

        var normalTmpl = new CharacterTemplate
        {
            Id = 1,
            IsSpChara = false,
            Name = "Reimu",
            CallName = "Reimu",
            NickName = "Miko",
            MasterName = "Master",
            Base = new Dictionary<int, long> { [0] = 2000 },
            MaxBase = new Dictionary<int, long> { [0] = 2000 },
            Abl = new Dictionary<int, long> { [0] = 5 },
            Talent = new Dictionary<int, long> { [10] = 1 },
            CStr = new Dictionary<int, string> { [0] = "Hakurei Shrine" },
        };

        var spTmpl = normalTmpl with
        {
            IsSpChara = true,
            Name = "EX-Reimu",
            Abl = new Dictionary<int, long> { [0] = 99 },
        };

        var sakuyaTmpl = normalTmpl with
        {
            Id = 3,
            Name = "Sakuya",
            CallName = "Sakuya",
            Abl = new Dictionary<int, long> { [0] = 12 },
        };

        var vars = new VariableStore(module);
        CharacterStore chars = vars.Characters;
        chars.RegisterTemplate(normalTmpl);
        chars.RegisterTemplate(spTmpl);
        chars.RegisterTemplate(sakuyaTmpl);

        int nameSlot = TestModuleBuilder.BuiltinSlot("NAME");
        int cflagSlot = TestModuleBuilder.BuiltinSlot("CFLAG");
        int ablSlot = TestModuleBuilder.BuiltinSlot("ABL");

        chars.AddVoidCharacter(); // index 0 = MASTER
        chars.SetStr(0, nameSlot, "Player");
        chars.AddCharacter(1);    // index 1 = Reimu
        chars.AddSpCharacter(1);  // index 2 = EX-Reimu (CompatiSPChara == true!)
        chars.AddCharacter(3);    // index 3 = Sakuya

        Assert.Equal(4, chars.Count);
        Assert.Equal("EX-Reimu", chars.GetStr(2, nameSlot, []));
        Assert.Equal(1L, chars.GetInt(2, cflagSlot, [0]));

        // User CHARADATA variable access & SORTCHARA descending by user CHARADATA (fixing MASTER at index 0!)
        chars.SetInt(0, customCharaSlot, 9999, [0]);
        chars.SetInt(1, customCharaSlot, 100, [0]);
        chars.SetInt(2, customCharaSlot, 300, [0]);
        chars.SetInt(3, customCharaSlot, 200, [0]);

        vars.SetInt("TARGET", 1, 0);
        chars.SortCharacters(customCharaSlot, [0], ascending: false, fixMaster: true);

        Assert.Equal("Player", chars.GetStr(0, nameSlot, []));
        Assert.Equal("EX-Reimu", chars.GetStr(1, nameSlot, []));
        Assert.Equal("Sakuya", chars.GetStr(2, nameSlot, []));
        Assert.Equal("Reimu", chars.GetStr(3, nameSlot, []));
        Assert.Equal(3L, vars.GetInt("TARGET", 0));

        Assert.Equal(2L, chars.FindCharaStr(nameSlot, [], "Sakuya", start: 0, end: 4, last: false));
        Assert.Equal(1L, chars.CMatchStr(nameSlot, [], "Sakuya", startChara: 0, endChara: 4));

        chars.CVarSetInt(ablSlot, [1], value: 77, startChara: 1, endChara: 4);
        Assert.Equal(0L, chars.GetInt(0, ablSlot, [1]));
        Assert.Equal(77L * 3, chars.SumCArray(ablSlot, [1], startChara: 0, endChara: 4));

        chars.CopyCharacter(sourceIndex: 1, destIndex: 3);
        Assert.Equal("EX-Reimu", chars.GetStr(3, nameSlot, []));

        chars.PickUpCharacters([0, 2]);
        Assert.Equal(2, chars.Count);
        Assert.Equal("Sakuya", chars.GetStr(1, nameSlot, []));
    }
}
