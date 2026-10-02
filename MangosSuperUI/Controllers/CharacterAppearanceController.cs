using System.Buffers.Binary;
using System.Security.Cryptography;
using MangosSuperUI.Services;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

/// <summary>Read-only appearance policy from the currently mounted client DBC.</summary>
public sealed class CharacterAppearanceController(MpqReaderService mpq) : Controller
{
    [HttpGet]
    public IActionResult Races()
    {
        const string path = @"DBFilesClient\ChrRaces.dbc";
        var bytes = mpq.ExtractFile(path);
        if (bytes is null) return NotFound(new { error = "Mounted ChrRaces.dbc is missing." });
        try
        {
            return Json(new { path, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                races = ReadFlags(bytes) });
        }
        catch (InvalidDataException ex) { return StatusCode(422, new { error = ex.Message }); }
    }

    public static IReadOnlyList<RaceAppearanceFlags> ReadFlags(byte[] bytes)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("WDBC"u8))
            throw new InvalidDataException("Invalid race table header.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        uint stride = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        uint strings = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        if (count > 1024 || fields < 2 || stride < 8 || stride != (ulong)fields * 4 ||
            20UL + (ulong)count * stride + strings > (ulong)bytes.Length)
            throw new InvalidDataException("Invalid race table bounds.");
        var result = new List<RaceAppearanceFlags>((int)count);
        for (int row = 0; row < count; row++)
        {
            int offset = checked(20 + row * (int)stride);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            result.Add(new(id, flags, (flags & 2) != 0));
        }
        return result;
    }
}

public sealed record RaceAppearanceFlags(uint Id, uint Flags, bool BareFeet);
