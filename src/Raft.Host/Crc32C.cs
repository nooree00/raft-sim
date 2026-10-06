using System;

namespace Raft.Host;

/// <summary>
/// CRC-32C (Castagnoli, reflected polynomial 0x82F63B78), table-driven (phase 9 decision 2: written
/// here rather than taken from a package). The published check value: "123456789" gives 0xE3069283.
/// </summary>
public static class Crc32C
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
