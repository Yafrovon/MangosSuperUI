using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Generic WDBC round-trip reader/writer for vanilla WoW 1.12.1 DBC files.
/// 
/// Usage:
///   var dbc = DbcWriterService.ReadDbc(filePath);      // or ReadDbc(byte[] raw)
///   dbc.AddRow(newFields);                              // add new row
///   dbc.PatchRow(id, fieldIndex, newValue);             // modify existing row field
///   byte[] output = dbc.Write();                        // produce valid WDBC binary
///   File.WriteAllBytes(outputPath, output);
///
/// This is designed for DBC overlay patching: read the original DBC from the server's
/// data directory, add/modify rows for custom content, write a new DBC that goes into
/// patch-Z.MPQ so the client picks it up.
///
/// WDBC format:
///   Header: "WDBC" magic (4) + recordCount (4) + fieldCount (4) + recordSize (4) + stringBlockSize (4) = 20 bytes
///   Records: recordCount × recordSize bytes (flat uint32 array; floats and stringrefs are also 4 bytes)
///   StringBlock: null-terminated strings referenced by uint32 offsets in record fields
/// </summary>
public class DbcWriterService
{
    // ── Per-file state ──

    public int RecordCount => _records.Count;
    public int FieldCount { get; private set; }
    public int RecordSize { get; private set; }
    public string SourcePath { get; private set; } = "";

    private List<uint[]> _records = new();          // each row is an array of uint32 fields
    private Dictionary<uint, string> _strings = new(); // stringBlock offset → string value
    private byte[] _originalStringBlock = Array.Empty<byte>();

    // ── Indexed lookup: field[0] (usually ID) → row index ──
    private Dictionary<uint, int> _idIndex = new();

    // ── Read from file ──

    public static DbcWriterService ReadDbc(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"DBC file not found: {filePath}");
        return ReadDbc(File.ReadAllBytes(filePath), filePath);
    }

    public static DbcWriterService ReadDbc(byte[] data, string sourcePath = "")
    {
        var dbc = new DbcWriterService { SourcePath = sourcePath };

        if (data.Length < 20)
            throw new InvalidDataException("DBC file too small for header");

        var magic = Encoding.ASCII.GetString(data, 0, 4);
        if (magic != "WDBC")
            throw new InvalidDataException($"Invalid DBC magic: {magic}");

        int recordCount = BitConverter.ToInt32(data, 4);
        int fieldCount = BitConverter.ToInt32(data, 8);
        int recordSize = BitConverter.ToInt32(data, 12);
        int stringBlockSize = BitConverter.ToInt32(data, 16);

        dbc.FieldCount = fieldCount;
        dbc.RecordSize = recordSize;

        int headerSize = 20;
        int recordsEnd = headerSize + recordCount * recordSize;

        if (data.Length < recordsEnd + stringBlockSize)
            throw new InvalidDataException($"DBC file truncated: expected {recordsEnd + stringBlockSize}, got {data.Length}");

        // Copy the original string block (we'll reuse it + append new strings)
        dbc._originalStringBlock = new byte[stringBlockSize];
        Array.Copy(data, recordsEnd, dbc._originalStringBlock, 0, stringBlockSize);

        // Parse records — each field is 4 bytes (uint32/int32/float all fit in 4 bytes)
        int fieldsPerRecord = recordSize / 4;
        for (int i = 0; i < recordCount; i++)
        {
            var fields = new uint[fieldsPerRecord];
            int rowStart = headerSize + i * recordSize;
            for (int f = 0; f < fieldsPerRecord; f++)
            {
                fields[f] = BitConverter.ToUInt32(data, rowStart + f * 4);
            }
            dbc._records.Add(fields);

            // Index by field[0] (the ID field)
            dbc._idIndex[fields[0]] = i;
        }

        return dbc;
    }

    // ── Query ──

    /// <summary>Get a row by its ID (field[0] value). Returns null if not found.</summary>
    public uint[]? GetRow(uint id)
    {
        if (_idIndex.TryGetValue(id, out int idx))
            return _records[idx];
        return null;
    }

    /// <summary>Get all rows as a list of uint32 arrays.</summary>
    public IReadOnlyList<uint[]> GetAllRows() => _records.AsReadOnly();

    /// <summary>Get the maximum ID value across all rows.</summary>
    public uint GetMaxId()
    {
        uint max = 0;
        foreach (var row in _records)
            if (row.Length > 0 && row[0] > max)
                max = row[0];
        return max;
    }

    /// <summary>Read a string from the original string block at the given offset.</summary>
    public string ReadString(uint offset)
    {
        if (offset == 0 || offset >= _originalStringBlock.Length)
            return "";
        int end = (int)offset;
        while (end < _originalStringBlock.Length && _originalStringBlock[end] != 0)
            end++;
        return Encoding.UTF8.GetString(_originalStringBlock, (int)offset, end - (int)offset);
    }

    /// <summary>Read a float from a uint32 field value (reinterpret bits).</summary>
    public static float UintToFloat(uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return BitConverter.ToSingle(bytes, 0);
    }

    /// <summary>Convert a float to uint32 for storage in a DBC field.</summary>
    public static uint FloatToUint(float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return BitConverter.ToUInt32(bytes, 0);
    }

    // ── Mutate ──

    /// <summary>
    /// Remove all rows where the ID (field[0]) matches the predicate.
    /// Used to scrub custom entries from previously-patched DBCs before regeneration.
    /// Returns the number of rows removed.
    /// </summary>
    public int RemoveRowsWhere(Func<uint, bool> idPredicate)
    {
        var toRemove = new List<uint>();
        foreach (var row in _records)
        {
            if (row.Length > 0 && idPredicate(row[0]))
                toRemove.Add(row[0]);
        }

        int removed = 0;
        foreach (var id in toRemove)
        {
            if (_idIndex.TryGetValue(id, out int idx))
            {
                _records.RemoveAt(idx);
                _idIndex.Remove(id);
                removed++;

                // Rebuild index for all rows after the removed one
                for (int i = idx; i < _records.Count; i++)
                    _idIndex[_records[i][0]] = i;
            }
        }

        return removed;
    }

    /// <summary>Add a new row with the given field values. Must have the correct field count.</summary>
    public void AddRow(uint[] fields)
    {
        int expectedFields = RecordSize / 4;
        if (fields.Length != expectedFields)
            throw new ArgumentException($"Row has {fields.Length} fields, expected {expectedFields}");

        _records.Add(fields);
        _idIndex[fields[0]] = _records.Count - 1;
    }

    /// <summary>Clone an existing row by ID, assign a new ID, return the cloned fields for modification.</summary>
    public uint[] CloneRow(uint sourceId, uint newId)
    {
        var source = GetRow(sourceId)
            ?? throw new KeyNotFoundException($"Source row ID {sourceId} not found");

        var clone = (uint[])source.Clone();
        clone[0] = newId; // Set new ID
        AddRow(clone);
        return clone;
    }

    /// <summary>Patch a single field in an existing row.</summary>
    public void PatchRow(uint id, int fieldIndex, uint newValue)
    {
        if (!_idIndex.TryGetValue(id, out int idx))
            throw new KeyNotFoundException($"Row ID {id} not found");
        if (fieldIndex < 0 || fieldIndex >= _records[idx].Length)
            throw new ArgumentOutOfRangeException(nameof(fieldIndex));

        _records[idx][fieldIndex] = newValue;
    }

    /// <summary>Patch a float field in an existing row.</summary>
    public void PatchRowFloat(uint id, int fieldIndex, float newValue)
    {
        PatchRow(id, fieldIndex, FloatToUint(newValue));
    }

    /// <summary>
    /// Add a new string to the string block and return its offset.
    /// The string will be appended when Write() is called.
    /// </summary>
    public uint AddString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        // Check if the string already exists in the original block
        uint existing = FindStringInBlock(value);
        if (existing != 0)
            return existing;

        // Will be appended at the end of the original string block
        uint offset = (uint)_originalStringBlock.Length;
        foreach (var kv in _strings)
        {
            // Check already-queued strings too
            if (kv.Value == value)
                return kv.Key;
        }

        // Account for previously added strings
        foreach (var kv in _strings)
        {
            uint end = (uint)(kv.Key + Encoding.UTF8.GetByteCount(kv.Value) + 1);
            if (end > offset) offset = end;
        }

        _strings[offset] = value;
        return offset;
    }

    private uint FindStringInBlock(string value)
    {
        if (_originalStringBlock.Length <= 1) return 0;

        byte[] needle = Encoding.UTF8.GetBytes(value);
        for (int i = 1; i < _originalStringBlock.Length - needle.Length; i++)
        {
            // Must start at a string boundary (preceded by null or at position 0)
            if (i > 0 && _originalStringBlock[i - 1] != 0) continue;

            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (_originalStringBlock[i + j] != needle[j]) { match = false; break; }
            }
            if (match && i + needle.Length < _originalStringBlock.Length
                      && _originalStringBlock[i + needle.Length] == 0)
            {
                return (uint)i;
            }
        }
        return 0;
    }

    // ── Write ──

    /// <summary>Produce a complete, valid WDBC binary with all modifications applied.</summary>
    public byte[] Write()
    {
        int fieldsPerRecord = RecordSize / 4;

        // Build the new string block: original block + appended strings
        using var stringBlockStream = new MemoryStream();
        stringBlockStream.Write(_originalStringBlock, 0, _originalStringBlock.Length);

        // Sort new strings by offset to write them in order
        foreach (var kv in _strings.OrderBy(s => s.Key))
        {
            // Pad to the correct offset if needed
            while (stringBlockStream.Length < kv.Key)
                stringBlockStream.WriteByte(0);

            byte[] strBytes = Encoding.UTF8.GetBytes(kv.Value);
            stringBlockStream.Write(strBytes, 0, strBytes.Length);
            stringBlockStream.WriteByte(0); // null terminator
        }

        byte[] newStringBlock = stringBlockStream.ToArray();
        // String block must be at least 1 byte (the empty-string null)
        if (newStringBlock.Length == 0) newStringBlock = new byte[] { 0 };

        // Write output
        using var output = new MemoryStream();
        using var bw = new BinaryWriter(output);

        // Header
        bw.Write(Encoding.ASCII.GetBytes("WDBC"));
        bw.Write(_records.Count);       // recordCount
        bw.Write(FieldCount);           // fieldCount
        bw.Write(RecordSize);           // recordSize
        bw.Write(newStringBlock.Length); // stringBlockSize

        // Records
        foreach (var row in _records)
        {
            for (int f = 0; f < fieldsPerRecord; f++)
            {
                bw.Write(f < row.Length ? row[f] : 0u);
            }
        }

        // String block
        bw.Write(newStringBlock);

        return output.ToArray();
    }
}