using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Hypera.Runtime;

/// <summary>
/// High-performance <see cref="Span{T}"/> and SIMD-accelerated implementations for Emuera array,
/// character-range, bitwise, and mathematical built-in operations.
/// </summary>
public static class SpanBuiltins
{
    // -------------------------------------------------------------------------
    // VARSET & Bulk Fill
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VarSet(Span<long> array, long value = 0L, int start = 0, int end = -1)
    {
        Span<long> slice = SliceHalfOpen(array, start, end);
        slice.Fill(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VarSet(Span<string> array, string? value = null, int start = 0, int end = -1)
    {
        Span<string> slice = SliceHalfOpen(array, start, end);
        slice.Fill(value ?? string.Empty);
    }

    // -------------------------------------------------------------------------
    // MATCH (Vectorized via .NET 10 MemoryExtensions.Count)
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Match(ReadOnlySpan<long> array, long target, int start = 0, int end = -1)
    {
        ReadOnlySpan<long> slice = SliceHalfOpen(array, start, end);
        return slice.IsEmpty ? 0L : MemoryExtensions.Count(slice, target);
    }

    public static long Match(ReadOnlySpan<string> array, string? target, int start = 0, int end = -1)
    {
        ReadOnlySpan<string> slice = SliceHalfOpen(array, start, end);
        string needle = target ?? string.Empty;
        long count = 0;
        for (int i = 0; i < slice.Length; i++)
        {
            if (string.Equals(slice[i] ?? string.Empty, needle, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    // -------------------------------------------------------------------------
    // FINDELEMENT / FINDLASTELEMENT (SIMD for Int64; Literal Fast-Path for Strings)
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long FindElement(ReadOnlySpan<long> array, long target, int start = 0, int end = -1, bool last = false)
    {
        int actualEnd = end < 0 ? array.Length : end;
        if (start < 0 || actualEnd > array.Length || start > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"FINDELEMENT range [{start}..{actualEnd}) is outside array bounds [0..{array.Length}).");
        }

        if (start >= actualEnd)
        {
            return -1L;
        }

        ReadOnlySpan<long> slice = array.Slice(start, actualEnd - start);
        int offset = last ? MemoryExtensions.LastIndexOf(slice, target) : MemoryExtensions.IndexOf(slice, target);
        return offset < 0 ? -1L : start + offset;
    }

    public static long FindElement(
        ReadOnlySpan<string> array,
        string? pattern,
        RegexCache regexCache,
        int start = 0,
        int end = -1,
        bool isExact = false,
        bool last = false,
        bool isEscapedLiteral = false)
    {
        int actualEnd = end < 0 ? array.Length : end;
        if (start < 0 || actualEnd > array.Length || start > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"FINDELEMENT range [{start}..{actualEnd}) is outside array bounds [0..{array.Length}).");
        }

        if (start >= actualEnd)
        {
            return -1L;
        }

        string pat = pattern ?? string.Empty;
        string? literal = null;
        if (isEscapedLiteral || RegexCache.TryGetLiteralPattern(pat, out literal))
        {
            string lit = isEscapedLiteral ? pat : literal!;
            return FindLiteralStringElement(array, lit, start, actualEnd, isExact, last);
        }

        Regex regex = regexCache.GetRegex(pat);
        if (last)
        {
            for (int i = actualEnd - 1; i >= start; i--)
            {
                string elem = array[i] ?? string.Empty;
                if (MatchesRegexElement(regex, elem, isExact))
                {
                    return i;
                }
            }
        }
        else
        {
            for (int i = start; i < actualEnd; i++)
            {
                string elem = array[i] ?? string.Empty;
                if (MatchesRegexElement(regex, elem, isExact))
                {
                    return i;
                }
            }
        }

        return -1L;
    }

    private static long FindLiteralStringElement(
        ReadOnlySpan<string> array,
        string literal,
        int start,
        int end,
        bool isExact,
        bool last)
    {
        if (last)
        {
            for (int i = end - 1; i >= start; i--)
            {
                string elem = array[i] ?? string.Empty;
                bool matched = isExact
                    ? string.Equals(elem, literal, StringComparison.Ordinal)
                    : elem.Contains(literal, StringComparison.Ordinal);
                if (matched)
                {
                    return i;
                }
            }
        }
        else
        {
            for (int i = start; i < end; i++)
            {
                string elem = array[i] ?? string.Empty;
                bool matched = isExact
                    ? string.Equals(elem, literal, StringComparison.Ordinal)
                    : elem.Contains(literal, StringComparison.Ordinal);
                if (matched)
                {
                    return i;
                }
            }
        }

        return -1L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesRegexElement(Regex regex, string element, bool isExact)
    {
        if (!isExact)
        {
            return regex.IsMatch(element);
        }

        Match m = regex.Match(element);
        return m.Success && m.Length == element.Length;
    }

    // -------------------------------------------------------------------------
    // Zero-Allocation ARRAYSHIFT, ARRAYREMOVE, ARRAYSORT, ARRAYCOPY, ARRAYMSORT*
    // -------------------------------------------------------------------------

    public static void ArrayShift<T>(Span<T> array, int shift, T defaultValue, int start = 0, int count = -1)
    {
        if ((uint)start >= (uint)array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "ARRAYSHIFT start index out of range.");
        }

        int actualCount = count < 0 ? array.Length - start : Math.Min(count, array.Length - start);
        if (actualCount <= 0 || shift == 0)
        {
            return;
        }

        Span<T> window = array.Slice(start, actualCount);
        if (Math.Abs((long)shift) >= window.Length)
        {
            window.Fill(defaultValue);
            return;
        }

        if (shift > 0)
        {
            window[..^shift].CopyTo(window[shift..]);
            window[..shift].Fill(defaultValue);
        }
        else
        {
            int left = -shift;
            window[left..].CopyTo(window[..^left]);
            window[^left..].Fill(defaultValue);
        }
    }

    public static void ArrayRemove<T>(Span<T> array, T emptyValue, int start, int count = -1)
    {
        if ((uint)start >= (uint)array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "ARRAYREMOVE start index out of range.");
        }

        if (count <= 0 || (long)start + count >= array.Length)
        {
            array[start..].Fill(emptyValue);
            return;
        }

        int tailLen = array.Length - (start + count);
        array.Slice(start + count, tailLen).CopyTo(array.Slice(start, tailLen));
        array.Slice(start + tailLen).Fill(emptyValue);
    }

    public static void ArraySort(Span<long> array, bool ascending = true, int start = 0, int count = -1)
    {
        if ((uint)start >= (uint)array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "ARRAYSORT start index out of range.");
        }

        int actualCount = count <= 0 ? array.Length - start : count;
        if ((long)start + actualCount > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "ARRAYSORT count exceeds array length.");
        }

        Span<long> slice = array.Slice(start, actualCount);
        slice.Sort();
        if (!ascending)
        {
            slice.Reverse();
        }
    }

    public static void ArraySort(Span<string> array, bool ascending = true, int start = 0, int count = -1)
    {
        if ((uint)start >= (uint)array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "ARRAYSORT start index out of range.");
        }

        int actualCount = count <= 0 ? array.Length - start : count;
        if ((long)start + actualCount > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "ARRAYSORT count exceeds array length.");
        }

        Span<string> slice = array.Slice(start, actualCount);
        slice.Sort(StringComparer.Ordinal);
        if (!ascending)
        {
            slice.Reverse();
        }
    }

    public static void ArrayCopy<T>(ReadOnlySpan<T> source, Span<T> destination)
    {
        int len = Math.Min(source.Length, destination.Length);
        source[..len].CopyTo(destination[..len]);
    }

    /// <summary>
    /// Computes stable index permutation for <c>ARRAYMSORT</c> / <c>ARRAYMSORTEX</c> over an integer key array.
    /// When <paramref name="fixedLength"/> is <c>-1</c>, stops at the first <c>0</c> element.
    /// </summary>
    public static int[] BuildMultiSortPermutation(ReadOnlySpan<long> keys, bool ascending = true, int fixedLength = -1)
    {
        if (fixedLength == 0 || keys.IsEmpty)
        {
            return [];
        }

        int limit = fixedLength > 0 ? Math.Min(fixedLength, keys.Length) : keys.Length;
        int count = 0;
        if (fixedLength < 0)
        {
            while (count < limit && keys[count] != 0L)
            {
                count++;
            }
        }
        else
        {
            count = limit;
        }

        int[] indices = new int[count];
        long[] keyCopy = keys[..count].ToArray();
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        Array.Sort(indices, (a, b) =>
        {
            int cmp = ascending ? keyCopy[a].CompareTo(keyCopy[b]) : keyCopy[b].CompareTo(keyCopy[a]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        return indices;
    }

    /// <summary>
    /// Computes stable index permutation for <c>ARRAYMSORT</c> / <c>ARRAYMSORTEX</c> over a string key array.
    /// When <paramref name="fixedLength"/> is <c>-1</c>, stops at the first empty element (<c>ARRAYMSORT</c>)
    /// or returns empty if <paramref name="abortOnEmptyWhenUnbounded"/> is true (<c>ARRAYMSORTEX</c>).
    /// </summary>
    public static bool TryBuildMultiSortPermutation(
        ReadOnlySpan<string> keys,
        bool ascending,
        int fixedLength,
        bool abortOnEmptyWhenUnbounded,
        out int[] permutation)
    {
        if (fixedLength == 0)
        {
            permutation = [];
            return false;
        }

        int limit = fixedLength > 0 ? Math.Min(fixedLength, keys.Length) : keys.Length;
        int count = 0;
        if (fixedLength < 0)
        {
            while (count < limit)
            {
                if (string.IsNullOrEmpty(keys[count]))
                {
                    if (abortOnEmptyWhenUnbounded)
                    {
                        permutation = [];
                        return false;
                    }

                    break;
                }

                count++;
            }
        }
        else
        {
            count = limit;
        }

        int[] indices = new int[count];
        string[] keyCopy = keys[..count].ToArray();
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        Array.Sort(indices, (a, b) =>
        {
            int cmp = ascending
                ? string.CompareOrdinal(keyCopy[a] ?? string.Empty, keyCopy[b] ?? string.Empty)
                : string.CompareOrdinal(keyCopy[b] ?? string.Empty, keyCopy[a] ?? string.Empty);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        permutation = indices;
        return true;
    }

    /// <summary>
    /// Applies a first-dimension permutation in-place to a flattened 1D/2D/3D backing array with stride <paramref name="rowStride"/>.
    /// </summary>
    public static bool ApplyFirstDimensionPermutation<T>(Span<T> flatArray, int dim0Length, int rowStride, ReadOnlySpan<int> permutation)
    {
        if (dim0Length < permutation.Length || rowStride <= 0)
        {
            return false;
        }

        if (permutation.IsEmpty)
        {
            return true;
        }

        int totalCopyElements = dim0Length * rowStride;
        T[] snapshot = flatArray[..totalCopyElements].ToArray();
        for (int i = 0; i < permutation.Length; i++)
        {
            int srcRow = permutation[i];
            snapshot.AsSpan(srcRow * rowStride, rowStride).CopyTo(flatArray.Slice(i * rowStride, rowStride));
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // SUMARRAY, MAXARRAY, MINARRAY, INRANGE*, GROUPMATCH, NOSAMES, ALLSAMES
    // -------------------------------------------------------------------------

    public static long SumArray(ReadOnlySpan<long> array, int start = 0, int end = -1)
    {
        ReadOnlySpan<long> slice = SliceHalfOpen(array, start, end);
        if (slice.IsEmpty)
        {
            return 0L;
        }

        long sum = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && slice.Length >= Vector<long>.Count)
        {
            ReadOnlySpan<Vector<long>> vectors = MemoryMarshal.Cast<long, Vector<long>>(slice);
            Vector<long> acc = Vector<long>.Zero;
            for (int v = 0; v < vectors.Length; v++)
            {
                acc += vectors[v];
            }

            sum = Vector.Sum(acc);
            i = vectors.Length * Vector<long>.Count;
        }

        for (; i < slice.Length; i++)
        {
            unchecked
            {
                sum += slice[i];
            }
        }

        return sum;
    }

    public static long MaxArray(ReadOnlySpan<long> array, int start = 0, int end = -1)
    {
        ReadOnlySpan<long> slice = SliceHalfOpen(array, start, end);
        if (slice.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "MAXARRAY range cannot be empty.");
        }

        long max = slice[0];
        for (int i = 1; i < slice.Length; i++)
        {
            if (slice[i] > max)
            {
                max = slice[i];
            }
        }

        return max;
    }

    public static long MinArray(ReadOnlySpan<long> array, int start = 0, int end = -1)
    {
        ReadOnlySpan<long> slice = SliceHalfOpen(array, start, end);
        if (slice.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "MINARRAY range cannot be empty.");
        }

        long min = slice[0];
        for (int i = 1; i < slice.Length; i++)
        {
            if (slice[i] < min)
            {
                min = slice[i];
            }
        }

        return min;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long InRange(long value, long min, long max) => (value >= min && value <= max) ? 1L : 0L;

    /// <summary>
    /// Emuera <c>INRANGEARRAY</c> counts elements satisfying <c>min &lt;= value &lt; max</c> (half-open upper bound per upstream).
    /// </summary>
    public static long InRangeArray(ReadOnlySpan<long> array, long min, long max, int start = 0, int end = -1)
    {
        ReadOnlySpan<long> slice = SliceHalfOpen(array, start, end);
        long count = 0;
        for (int i = 0; i < slice.Length; i++)
        {
            long v = slice[i];
            if (v >= min && v < max)
            {
                count++;
            }
        }

        return count;
    }

    public static long GroupMatch(long target, ReadOnlySpan<long> candidates)
    {
        return candidates.IsEmpty ? 0L : MemoryExtensions.Count(candidates, target);
    }

    public static long GroupMatch(string? target, ReadOnlySpan<string> candidates)
    {
        string needle = target ?? string.Empty;
        long count = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i] ?? string.Empty, needle, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    public static long AllSames(ReadOnlySpan<long> values)
    {
        if (values.Length <= 1)
        {
            return 1L;
        }

        long first = values[0];
        return MemoryExtensions.Count(values[1..], first) == values.Length - 1 ? 1L : 0L;
    }

    public static long AllSames(ReadOnlySpan<string> values)
    {
        if (values.Length <= 1)
        {
            return 1L;
        }

        string first = values[0] ?? string.Empty;
        for (int i = 1; i < values.Length; i++)
        {
            if (!string.Equals(values[i] ?? string.Empty, first, StringComparison.Ordinal))
            {
                return 0L;
            }
        }

        return 1L;
    }

    public static long NoSames(ReadOnlySpan<long> values)
    {
        if (values.Length <= 1)
        {
            return 1L;
        }

        if (values.Length <= 16)
        {
            for (int i = 0; i < values.Length; i++)
            {
                for (int j = i + 1; j < values.Length; j++)
                {
                    if (values[i] == values[j])
                    {
                        return 0L;
                    }
                }
            }

            return 1L;
        }

        var seen = new HashSet<long>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            if (!seen.Add(values[i]))
            {
                return 0L;
            }
        }

        return 1L;
    }

    public static long NoSames(ReadOnlySpan<string> values)
    {
        if (values.Length <= 1)
        {
            return 1L;
        }

        if (values.Length <= 16)
        {
            for (int i = 0; i < values.Length; i++)
            {
                string lhs = values[i] ?? string.Empty;
                for (int j = i + 1; j < values.Length; j++)
                {
                    if (string.Equals(lhs, values[j] ?? string.Empty, StringComparison.Ordinal))
                    {
                        return 0L;
                    }
                }
            }

            return 1L;
        }

        var seen = new HashSet<string>(values.Length, StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i++)
        {
            if (!seen.Add(values[i] ?? string.Empty))
            {
                return 0L;
            }
        }

        return 1L;
    }

    // -------------------------------------------------------------------------
    // Mathematical & Bitwise Built-ins
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Limit(long value, long min, long max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    public static long Power(long x, long y)
    {
        double result = Math.Pow(x, y);
        if (double.IsNaN(result) || double.IsInfinity(result) || result >= long.MaxValue || result <= long.MinValue)
        {
            throw new OverflowException($"POWER({x}, {y}) overflowed Int64 range.");
        }

        return (long)result;
    }

    public static long Sqrt(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "SQRT argument cannot be negative.");
        }

        return (long)Math.Sqrt(value);
    }

    public static long Cbrt(long value) => (long)Math.Cbrt(value);

    public static long Log(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "LOG argument must be positive.");
        }

        return (long)Math.Log(value);
    }

    public static long Log10(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "LOG10 argument must be positive.");
        }

        return (long)Math.Log10(value);
    }

    public static long Exponent(long value)
    {
        double exp = Math.Exp(value);
        if (double.IsInfinity(exp) || exp >= long.MaxValue)
        {
            throw new OverflowException($"EXPONENT({value}) overflowed Int64.");
        }

        return (long)exp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Abs(long value) => value == long.MinValue ? throw new OverflowException("ABS(Int64.MinValue) overflows.") : Math.Abs(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Sign(long value) => Math.Sign(value);

    public static long Times(long value, double factor, bool notRigorous = false)
    {
        if (notRigorous)
        {
            return unchecked((long)(value * factor));
        }

        try
        {
            decimal d = value * (decimal)factor;
            if (d <= long.MaxValue && d >= long.MinValue)
            {
                return (long)d;
            }
        }
        catch (OverflowException)
        {
        }

        return unchecked((long)(value * factor));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetBit(long value, long bitIndex)
    {
        ValidateBitIndex(bitIndex);
        return (value >> (int)bitIndex) & 1L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SetBit(long value, long bitIndex)
    {
        ValidateBitIndex(bitIndex);
        return value | (1L << (int)bitIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ClearBit(long value, long bitIndex)
    {
        ValidateBitIndex(bitIndex);
        return value & ~(1L << (int)bitIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long InvertBit(long value, long bitIndex)
    {
        ValidateBitIndex(bitIndex);
        return value ^ (1L << (int)bitIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateBitIndex(long bitIndex)
    {
        if ((ulong)bitIndex >= 64UL)
        {
            throw new ArgumentOutOfRangeException(nameof(bitIndex), bitIndex, "Bit index must be in [0..63].");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<T> SliceHalfOpen<T>(Span<T> array, int start, int end)
    {
        int actualEnd = end < 0 ? array.Length : end;
        if (start < 0 || actualEnd < start || actualEnd > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"Invalid array range [{start}..{actualEnd}) for length {array.Length}.");
        }

        return array.Slice(start, actualEnd - start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<T> SliceHalfOpen<T>(ReadOnlySpan<T> array, int start, int end)
    {
        int actualEnd = end < 0 ? array.Length : end;
        if (start < 0 || actualEnd < start || actualEnd > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"Invalid array range [{start}..{actualEnd}) for length {array.Length}.");
        }

        return array.Slice(start, actualEnd - start);
    }
}
