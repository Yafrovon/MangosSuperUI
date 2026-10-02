using System.Buffers.Binary;
using MangosSuperUI.Controllers;
using Xunit;

namespace MangosSuperUI.Tests;

public sealed class CharacterAppearanceTests
{
    [Fact]
    public void BareFeetComesFromFlagsNotRaceId()
    {
        var bytes = Table((1u, 14u), (6u, 12u), (8u, 14u));
        var rows = CharacterAppearanceController.ReadFlags(bytes);
        Assert.True(rows[0].BareFeet);
        Assert.False(rows[1].BareFeet);
        Assert.True(rows[2].BareFeet);
    }

    [Fact]
    public void MalformedHeadersAndTruncatedRowsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceController.ReadFlags([]));
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceController.ReadFlags(Table((1u, 12u))[..^1]));
        var bytes = Table((1u, 12u));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceController.ReadFlags(bytes));
    }

    private static byte[] Table(params (uint Id, uint Flags)[] rows)
    {
        var bytes = new byte[20 + rows.Length * 8];
        "WDBC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 8);
        for (int i = 0; i < rows.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + i * 8), rows[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + i * 8), rows[i].Flags);
        }
        return bytes;
    }
}
