using System.Runtime.CompilerServices;
using Hypera.Bytecode;

namespace Hypera.Runtime;

/// <summary>
/// Direct array reference binding for <c>#DIM REF</c> / <c>#DIMS REF</c> parameters and local reference variables,
/// plus frame-scoped dynamic local arrays (<c>#DIM DYNAMIC</c>, <c>VARI</c>, <c>VARS</c>).
/// Zero boxing, zero indirection chains — points directly to the target backing array.
/// </summary>
public sealed class FrameLocalStorage
{
    public required int LocalSlotIndex { get; init; }
    public required string Name { get; init; }
    public required VariableValueKind ValueKind { get; init; }
    public required VariableScopeKind Scope { get; init; }

    public int Rank { get; set; }
    public int Dim0 { get; set; } = 1;
    public int Dim1 { get; set; } = 1;
    public int Dim2 { get; set; } = 1;
    public int Offset { get; set; }
    public int TotalLength { get; set; } = 1;

    public long[]? IntArray { get; set; }
    public string[]? StrArray { get; set; }
    public bool IsBoundRef { get; set; }

    public static FrameLocalStorage CreateDynamic(int slotIndex, VariableSlotDescriptor desc)
    {
        int rank = desc.Dimensions.Length;
        int d0 = rank >= 1 ? Math.Max(1, desc.Dimensions[0]) : 1;
        int d1 = rank >= 2 ? Math.Max(1, desc.Dimensions[1]) : 1;
        int d2 = rank >= 3 ? Math.Max(1, desc.Dimensions[2]) : 1;
        int total = checked(d0 * d1 * d2);

        long[]? ints = null;
        string[]? strs = null;
        if (desc.ValueKind == VariableValueKind.Int)
        {
            ints = new long[total];
        }
        else
        {
            strs = new string[total];
            Array.Fill(strs, string.Empty);
        }

        return new FrameLocalStorage
        {
            LocalSlotIndex = slotIndex,
            Name = desc.Name,
            ValueKind = desc.ValueKind,
            Scope = desc.Scope,
            Rank = rank,
            Dim0 = d0,
            Dim1 = d1,
            Dim2 = d2,
            Offset = 0,
            TotalLength = total,
            IntArray = ints,
            StrArray = strs,
            IsBoundRef = false,
        };
    }

    public static FrameLocalStorage CreateUnboundRef(int slotIndex, string name, VariableValueKind kind, int rank = 1) =>
        new()
        {
            LocalSlotIndex = slotIndex,
            Name = name,
            ValueKind = kind,
            Scope = VariableScopeKind.Reference,
            Rank = rank,
            Dim0 = 0,
            Dim1 = 1,
            Dim2 = 1,
            Offset = 0,
            TotalLength = 0,
            IsBoundRef = false,
        };

    public void BindIntArray(long[] backing, int offset, int rank, int dim0, int dim1 = 1, int dim2 = 1)
    {
        if (ValueKind != VariableValueKind.Int)
        {
            throw new InvalidOperationException($"Cannot bind integer array to string reference '{Name}'.");
        }

        IntArray = backing;
        StrArray = null;
        Offset = offset;
        Rank = rank;
        Dim0 = dim0;
        Dim1 = dim1;
        Dim2 = dim2;
        TotalLength = checked(dim0 * dim1 * dim2);
        IsBoundRef = true;
    }

    public void BindStrArray(string[] backing, int offset, int rank, int dim0, int dim1 = 1, int dim2 = 1)
    {
        if (ValueKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"Cannot bind string array to integer reference '{Name}'.");
        }

        StrArray = backing;
        IntArray = null;
        Offset = offset;
        Rank = rank;
        Dim0 = dim0;
        Dim1 = dim1;
        Dim2 = dim2;
        TotalLength = checked(dim0 * dim1 * dim2);
        IsBoundRef = true;
    }

    internal void ResetDynamicValues()
    {
        if (IntArray is not null)
        {
            Array.Clear(IntArray);
        }

        if (StrArray is not null)
        {
            Array.Fill(StrArray, string.Empty);
        }
    }

    internal void ResetUnboundRef()
    {
        IntArray = null;
        StrArray = null;
        Offset = 0;
        Dim0 = 0;
        Dim1 = 1;
        Dim2 = 1;
        TotalLength = 0;
        IsBoundRef = false;
    }

    public void CopyBindingFrom(FrameLocalStorage other)
    {
        if (other.ValueKind != ValueKind)
        {
            throw new InvalidOperationException($"Type mismatch binding reference '{Name}' from '{other.Name}'.");
        }

        if (Scope == VariableScopeKind.Reference && !other.IsBoundRef && other.Scope == VariableScopeKind.Reference)
        {
            throw new InvalidOperationException($"Reference variable '{other.Name}' is unbound.");
        }

        IntArray = other.IntArray;
        StrArray = other.StrArray;
        Offset = other.Offset;
        Rank = other.Rank;
        Dim0 = other.Dim0;
        Dim1 = other.Dim1;
        Dim2 = other.Dim2;
        TotalLength = other.TotalLength;
        IsBoundRef = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex0D() => EnsureReadyAndCheck0D();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex1D(long idx0)
    {
        EnsureReady();
        if ((ulong)idx0 >= (ulong)Dim0)
        {
            ThrowOutOfRange(idx0, Dim0);
        }

        return Offset + ((int)idx0 * Dim1 * Dim2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex2D(long idx0, long idx1)
    {
        EnsureReady();
        if ((ulong)idx0 >= (ulong)Dim0 || (ulong)idx1 >= (ulong)Dim1)
        {
            ThrowOutOfRange2D(idx0, idx1);
        }

        return Offset + ((((int)idx0 * Dim1) + (int)idx1) * Dim2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex3D(long idx0, long idx1, long idx2)
    {
        EnsureReady();
        if ((ulong)idx0 >= (ulong)Dim0 || (ulong)idx1 >= (ulong)Dim1 || (ulong)idx2 >= (ulong)Dim2)
        {
            ThrowOutOfRange3D(idx0, idx1, idx2);
        }

        return Offset + (((((int)idx0 * Dim1) + (int)idx1) * Dim2) + (int)idx2);
    }

    public int ComputeFlatIndex(ReadOnlySpan<long> indices)
    {
        return indices.Length switch
        {
            0 => ComputeFlatIndex0D(),
            1 => ComputeFlatIndex1D(indices[0]),
            2 => ComputeFlatIndex2D(indices[0], indices[1]),
            3 => ComputeFlatIndex3D(indices[0], indices[1], indices[2]),
            _ => throw new ArgumentOutOfRangeException(nameof(indices), "Variables support at most 3 dimensions."),
        };
    }

    public Span<long> GetIntSpan()
    {
        EnsureReady();
        return IntArray!.AsSpan(Offset, TotalLength);
    }

    public Span<string> GetStrSpan()
    {
        EnsureReady();
        return StrArray!.AsSpan(Offset, TotalLength);
    }

    public int GetDimensionLength(int dimension) =>
        dimension switch
        {
            0 => Dim0,
            1 => Rank >= 2 ? Dim1 : 0,
            2 => Rank >= 3 ? Dim2 : 0,
            _ => 0,
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int EnsureReadyAndCheck0D()
    {
        EnsureReady();
        if (TotalLength <= 0)
        {
            ThrowOutOfRange(0, TotalLength);
        }

        return Offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureReady()
    {
        if (Scope == VariableScopeKind.Reference && !IsBoundRef)
        {
            ThrowUnbound();
        }
    }

    private void ThrowUnbound() =>
        throw new InvalidOperationException($"Reference variable '{Name}' has not been bound to an array.");

    private void ThrowOutOfRange(long idx, int len) =>
        throw new IndexOutOfRangeException($"Index {idx} is out of bounds [0..{len}) for local/ref variable '{Name}'.");

    private void ThrowOutOfRange2D(long idx0, long idx1) =>
        throw new IndexOutOfRangeException($"Index ({idx0}, {idx1}) is out of bounds ({Dim0}, {Dim1}) for local/ref variable '{Name}'.");

    private void ThrowOutOfRange3D(long idx0, long idx1, long idx2) =>
        throw new IndexOutOfRangeException($"Index ({idx0}, {idx1}, {idx2}) is out of bounds ({Dim0}, {Dim1}, {Dim2}) for local/ref variable '{Name}'.");
}
