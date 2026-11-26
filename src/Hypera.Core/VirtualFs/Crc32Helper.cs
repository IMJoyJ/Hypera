namespace Hypera.VirtualFs;

internal static class Crc32Helper
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320u;
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    public const uint Initial = 0xFFFFFFFFu;

    public static uint Update(uint runningCrc, ReadOnlySpan<byte> data)
    {
        uint crc = runningCrc;
        for (int i = 0; i < data.Length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Finalize(uint runningCrc) => ~runningCrc;

    public static uint Compute(ReadOnlySpan<byte> data) => Finalize(Update(Initial, data));
}
