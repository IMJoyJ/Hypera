namespace Hypera.Runtime;

/// <summary>
/// Instance-isolated SIMD-oriented Fast Mersenne Twister (SFMT-19937) pseudo-random generator
/// matching upstream Emuera's <c>MTRandom</c>, <c>RANDDATA</c> (625 slots), <c>INITRAND</c>/<c>DUMPRAND</c>,
/// and eramaker 15-bit <c>CompatiRAND</c> semantics.
/// </summary>
public sealed class SfmtRandom
{
    public const int Mexp = 19937;
    public const int BlockCount = (Mexp / 128) + 1; // 156
    public const int N32 = BlockCount * 4;          // 624
    public const int RandDataLength = N32 + 1;      // 625

    private const int Pos1 = 122;
    private const int Sl1 = 18;
    private const int Sr1 = 11;
    private const uint Msk1 = 0xdfffffefU;
    private const uint Msk2 = 0xddfecb7fU;
    private const uint Msk3 = 0xbffaffffU;
    private const uint Msk4 = 0xbffffff6U;
    private const uint Parity1 = 0x00000001U;
    private const uint Parity2 = 0x00000000U;
    private const uint Parity3 = 0x00000000U;
    private const uint Parity4 = 0x13c9e684U;

    private readonly uint[] _sfmt = new uint[N32];
    private int _idx;
    private Random _newRandom;

    public SfmtRandom()
        : this(Environment.TickCount64)
    {
    }

    public SfmtRandom(long seed)
    {
        _newRandom = new Random(unchecked((int)seed));
        Randomize(seed);
    }

    public bool UseNewRandom { get; set; }

    public void Randomize(long seed)
    {
        unchecked
        {
            InitGenRand((uint)seed);
            _newRandom = new Random((int)seed);
        }
    }

    public uint NextUInt32()
    {
        if ((uint)_idx >= N32)
        {
            GenRandAll19937();
            _idx = 0;
        }

        return _sfmt[_idx++];
    }

    public ulong NextUInt64()
    {
        ulong hi = NextUInt32();
        return (hi << 32) + NextUInt32();
    }

    public long NextInt64() => unchecked((long)NextUInt64());

    public long NextInt64(long max)
    {
        if (max <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "RAND maximum must be positive.");
        }

        if (UseNewRandom)
        {
            return _newRandom.NextInt64(max);
        }

        return (long)(NextUInt64() % (ulong)max);
    }

    public long NextInt64(long min, long max)
    {
        if (max <= min)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "RAND maximum must be strictly greater than minimum.");
        }

        ulong span = unchecked((ulong)(max - min));
        if (UseNewRandom)
        {
            return min + (long)((ulong)_newRandom.NextInt64() % span);
        }

        return unchecked(min + (long)(NextUInt64() % span));
    }

    /// <summary>
    /// Evaluates eramaker-compatible 15-bit pseudo-variable <c>RAND:X</c>:
    /// <c>X == 0</c> returns <c>0</c>; otherwise returns <c>(NextInt64(32768)) % |X|</c>.
    /// </summary>
    public long NextCompatiRand(long max)
    {
        if (max == 0)
        {
            return 0L;
        }

        ulong absMax = max < 0 ? unchecked((ulong)(-max)) : (ulong)max;
        long sample15 = NextInt64(32768L);
        return (long)((ulong)sample15 % absMax);
    }

    public double NextDouble() => NextUInt32() * (1.0 / 4294967296.0);

    public void DumpRand(Span<long> destination)
    {
        if (destination.Length < RandDataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), $"RANDDATA span must hold at least {RandDataLength} elements.");
        }

        for (int i = 0; i < N32; i++)
        {
            destination[i] = _sfmt[i];
        }

        destination[N32] = _idx;
    }

    public void InitRand(ReadOnlySpan<long> source)
    {
        if (source.Length < RandDataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(source), $"RANDDATA span must hold at least {RandDataLength} elements.");
        }

        for (int i = 0; i < N32; i++)
        {
            _sfmt[i] = unchecked((uint)source[i]);
        }

        int loadedIdx = unchecked((int)source[N32]);
        _idx = loadedIdx < 0 ? N32 : loadedIdx;
    }

    private void InitGenRand(uint seed)
    {
        _sfmt[0] = seed;
        for (int i = 1; i < N32; i++)
        {
            _sfmt[i] = unchecked((uint)((1812433253U * (_sfmt[i - 1] ^ (_sfmt[i - 1] >> 30))) + (uint)i));
        }

        PeriodCertification();
        _idx = N32;
    }

    private void PeriodCertification()
    {
        ReadOnlySpan<uint> parity = [Parity1, Parity2, Parity3, Parity4];
        uint inner = 0;

        for (int i = 0; i < 4; i++)
        {
            inner ^= _sfmt[i] & parity[i];
        }

        for (int shift = 16; shift > 0; shift >>= 1)
        {
            inner ^= inner >> shift;
        }

        if ((inner & 1U) == 1U)
        {
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            uint work = 1U;
            for (int j = 0; j < 32; j++)
            {
                if ((work & parity[i]) != 0)
                {
                    _sfmt[i] ^= work;
                    return;
                }

                work <<= 1;
            }
        }
    }

    private void GenRandAll19937()
    {
        uint[] p = _sfmt;
        int a = 0;
        int b = Pos1 * 4;
        int c = (BlockCount - 2) * 4;
        int d = (BlockCount - 1) * 4;

        do
        {
            p[a + 3] = p[a + 3] ^ (p[a + 3] << 8) ^ (p[a + 2] >> 24) ^ (p[c + 3] >> 8) ^ ((p[b + 3] >> Sr1) & Msk4) ^ (p[d + 3] << Sl1);
            p[a + 2] = p[a + 2] ^ (p[a + 2] << 8) ^ (p[a + 1] >> 24) ^ (p[c + 3] << 24) ^ (p[c + 2] >> 8) ^ ((p[b + 2] >> Sr1) & Msk3) ^ (p[d + 2] << Sl1);
            p[a + 1] = p[a + 1] ^ (p[a + 1] << 8) ^ (p[a + 0] >> 24) ^ (p[c + 2] << 24) ^ (p[c + 1] >> 8) ^ ((p[b + 1] >> Sr1) & Msk2) ^ (p[d + 1] << Sl1);
            p[a + 0] = p[a + 0] ^ (p[a + 0] << 8) ^ (p[c + 1] << 24) ^ (p[c + 0] >> 8) ^ ((p[b + 0] >> Sr1) & Msk1) ^ (p[d + 0] << Sl1);

            c = d;
            d = a;
            a += 4;
            b += 4;
            if (b >= N32)
            {
                b = 0;
            }
        }
        while (a < N32);
    }
}
