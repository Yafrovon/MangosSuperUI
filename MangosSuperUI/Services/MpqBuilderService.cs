using War3Net.IO.Mpq;
using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Creates MPQ patch archives for WoW client distribution.
/// 
/// Usage:
///   var builder = new MpqBuilderService();
///   builder.AddFile("DBFilesClient\\Spell.dbc", spellDbcBytes);
///   builder.AddFile("Spells\\Custom_Voidstrike_Cast.m2", m2Bytes);
///   builder.AddFile("Interface\\Icons\\CustomSpell_Voidstrike.blp", iconBlpBytes);
///   builder.Build(outputPath);
///
/// The output MPQ should be named "patch-Z.MPQ" (or patch-4.MPQ, etc.) and placed
/// in the WoW client's Data/ folder. Files in higher-alphabetical patches override
/// files in lower patches. "Z" ensures our custom content wins.
///
/// Depends on: War3Net.IO.Mpq (NuGet, already referenced in the Extractor project)
/// Note: MangosSuperUI web app needs this NuGet reference added to its .csproj.
/// </summary>
public class MpqBuilderService
{
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly ILogger<MpqBuilderService>? _logger;

    public MpqBuilderService(ILogger<MpqBuilderService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Add a file to the MPQ with the given virtual path.</summary>
    /// <param name="mpqPath">Path inside the MPQ, e.g. "DBFilesClient\\Spell.dbc"</param>
    /// <param name="data">File content bytes</param>
    public void AddFile(string mpqPath, byte[] data)
    {
        // Normalize path separators to backslash (MPQ convention)
        string normalizedPath = mpqPath.Replace('/', '\\');
        _files[normalizedPath] = data;
        _logger?.LogInformation("MpqBuilder: Queued {Path} ({Size} bytes)", normalizedPath, data.Length);
    }

    /// <summary>Add a file from disk.</summary>
    public void AddFileFromDisk(string mpqPath, string diskPath)
    {
        if (!File.Exists(diskPath))
            throw new FileNotFoundException($"File not found: {diskPath}");
        AddFile(mpqPath, File.ReadAllBytes(diskPath));
    }

    /// <summary>Number of files queued for packaging.</summary>
    public int FileCount => _files.Count;

    /// <summary>Total size of all queued files.</summary>
    public long TotalSize => _files.Values.Sum(f => (long)f.Length);

    /// <summary>List all queued file paths.</summary>
    public IReadOnlyCollection<string> GetQueuedPaths() => _files.Keys;

    /// <summary>
    /// Build the MPQ archive and write to disk.
    /// </summary>
    /// <param name="outputPath">Full path for the output .MPQ file</param>
    /// <returns>True if successful</returns>
    public bool Build(string outputPath)
    {
        if (_files.Count == 0)
        {
            _logger?.LogWarning("MpqBuilder: No files to package");
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Build the list of MpqFile entries
            var mpqFiles = new List<MpqFile>();

            foreach (var (mpqPath, data) in _files)
            {
                // Create MpqFile from stream
                var stream = new MemoryStream(data);
                var mpqFile = MpqFile.New(stream, mpqPath);

                // Set compression flags — use default (Deflate) for most files
                mpqFile.TargetFlags = MpqFileFlags.Exists | MpqFileFlags.CompressedMulti;

                mpqFiles.Add(mpqFile);
            }

            // Build the (listfile) entry — MPQ convention, lists all files
            var listFileContent = string.Join("\r\n", _files.Keys.OrderBy(k => k));
            var listFileStream = new MemoryStream(Encoding.UTF8.GetBytes(listFileContent));
            var listFile = MpqFile.New(listFileStream, "(listfile)");
            listFile.TargetFlags = MpqFileFlags.Exists | MpqFileFlags.CompressedMulti;
            mpqFiles.Add(listFile);

            // Create the MPQ archive
            using var outputStream = File.Create(outputPath);

            // War3Net API: Create(Stream, IEnumerable<MpqFile>, MpqArchiveCreateOptions, bool leaveOpen)
            ushort hashTableSize = (ushort)Math.Max(16, GetNextPowerOfTwo(mpqFiles.Count + 4));
            var createOptions = new MpqArchiveCreateOptions
            {
                HashTableSize = hashTableSize,
                BlockSize = 3, // 512 << 3 = 4096 byte blocks (standard)
            };

            MpqArchive.Create(outputStream, mpqFiles, createOptions);

            var fileInfo = new FileInfo(outputPath);
            _logger?.LogInformation(
                "MpqBuilder: Created {Path} ({FileCount} files, {Size} bytes)",
                outputPath, _files.Count, fileInfo.Length);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MpqBuilder: Failed to create MPQ at {Path}", outputPath);
            return false;
        }
    }

    /// <summary>Clear all queued files.</summary>
    public void Clear()
    {
        _files.Clear();
    }

    private static int GetNextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value) result <<= 1;
        return result;
    }
}