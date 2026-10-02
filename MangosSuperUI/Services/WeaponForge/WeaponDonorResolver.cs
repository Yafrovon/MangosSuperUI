using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using MangosSuperUI.Services.WeaponForge.RawM2;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>
/// Resolves one stock visual donor per weapon family, data-driven from the installed client
/// archives instead of hardcoded model names that may not exist. For a profile it tries the pinned
/// display row first (strict, then relaxed texture contract), then scans the mounted
/// ItemDisplayInfo for rows whose ModelName1 matches the family's patterns, extracts each
/// candidate M2, and accepts the first one that satisfies the writer's scaffold assumptions
/// (v256 parse, four inline views, exactly one submesh + one batch in view 0, a Type-2 texture
/// slot). The winning donor supplies everything type-specific downstream:
///
///   • the M2 scaffold the variable-topology writer appends onto (bones, attachments, sequences
///     — this is what makes a forged 2H/staff carry proper bone/attachment anatomy, a forged bow
///     keep its arrow attachments, and a forged throwing axe inherit the stock throw spin on bone 0);
///   • the DBC row's GroupSoundIndex (axe thunk vs sword shing), inventory icon, SpellVisualID
///     (the ranged projectile visual: bows 5, firearms 224, thrown 98) and whether ModelName2
///     mirrors ModelName1 (every stock thrown weapon);
///   • the fallback BLP when an import carries no texture;
///   • the measured VERTEX box (not the header bounds, which animated models inflate — thrown
///     spins, bow draws, muzzle flashes), from which the target length (X extent), the palm-back
///     fraction (−minX/extent) and the <see cref="WeaponOrientationHints"/> are derived — the
///     stock model itself records where Blizzard put the palm, so a staff grips mid-shaft, an axe
///     low on the haft, a bow at the centre of its limbs, with no tuned constants.
///
/// A profile may name a separate <see cref="WeaponTypeProfile.MeasureDisplayRow"/>: the scaffold
/// bytes still come from the structural donor, but length/palm/orientation and icon/sound/visual
/// come from that representative stock model (crossbows: clean hand-crossbow scaffold, two-hand
/// crossbow measurements).
///
/// The golden 1H sword pins display row 679 and reproduces the proven 1.095 extent / 0.188
/// palm-back values from its own bytes (its vertex box equals its header box, as it does for every
/// static melee donor). Resolution is cached per type for the app lifetime (stock rows never
/// change under a running app). Fails closed with a listed-patterns error when no stock donor
/// passes validation.
/// </summary>
public sealed class WeaponDonorResolver
{
    private readonly MpqReaderService _mpq;
    private readonly ILogger<WeaponDonorResolver> _logger;
    private readonly ConcurrentDictionary<string, WeaponDonorInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dbcLock = new();
    private DbcWriterService? _dbc;

    /// <summary>Bound on extract-and-parse attempts per scan pass, counted per DISTINCT model
    /// stem (hundreds of display rows reuse the same handful of .m2 files — 379 Sword_2H rows map
    /// to 36 models), so a family's whole stock model set is covered without turning resolution
    /// into a full-archive sweep.</summary>
    private const int MaxCandidateProbes = 60;

    public WeaponDonorResolver(MpqReaderService mpq, ILogger<WeaponDonorResolver> logger)
    {
        _mpq = mpq;
        _logger = logger;
    }

    public WeaponDonorInfo Resolve(WeaponTypeProfile profile) =>
        _cache.GetOrAdd(profile.Key, _ => ResolveUncached(profile));

    private WeaponDonorInfo ResolveUncached(WeaponTypeProfile profile)
    {
        var dbc = LoadDbc();
        WeaponDonorInfo? info = null;
        bool relaxed = false;

        if (profile.PinnedDisplayRow is { } pinned)
        {
            var row = dbc.GetRow(pinned);
            if (row is null)
            {
                _logger.LogWarning("WeaponDonorResolver: pinned donor display row {Row} for {Type} is missing from the mounted ItemDisplayInfo{Fallback}",
                    pinned, profile.Label, profile.DonorModelPatterns.Length > 0 ? " — falling back to pattern scan" : "");
            }
            else
            {
                info = TryCandidate(dbc, row, profile, strict: true);
                if (info is null)
                {
                    info = TryCandidate(dbc, row, profile, strict: false);
                    relaxed = info is not null;
                }
                if (info is null)
                    _logger.LogWarning("WeaponDonorResolver: pinned donor display row {Row} for {Type} failed structural validation{Fallback}",
                        pinned, profile.Label, profile.DonorModelPatterns.Length > 0 ? " — falling back to pattern scan" : "");
            }
        }

        if (info is null && profile.DonorModelPatterns.Length > 0)
        {
            // Two passes: strict prefers single-texture Type-2 donors with an extractable BLP
            // (the exact golden-sword shape); relaxed accepts any donor whose first texture slot is
            // Type 2, which still means the DBC TextureName1 drives what the batch samples.
            foreach (bool strict in new[] { true, false })
            {
                int probes = 0;
                var probedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in MatchingRows(dbc, profile))
                {
                    // One probe per model: every later row naming the same .m2 would fail (or pass)
                    // identically on structure, and the first row for a model wins deterministically.
                    if (!probedStems.Add(ModelStem(dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName1])))) continue;
                    if (++probes > MaxCandidateProbes) break;
                    info = TryCandidate(dbc, row, profile, strict);
                    if (info is not null) { relaxed = !strict; break; }
                }
                if (info is not null) break;
            }
        }

        if (info is null)
            throw new InvalidOperationException(
                $"No usable stock visual donor found for {profile.Label} " +
                $"(pinned row: {(profile.PinnedDisplayRow?.ToString() ?? "none")}; ModelName1 patterns tried: {string.Join(", ", profile.DonorModelPatterns)}).");

        if (profile.MeasureDisplayRow is { } measure && measure != info.DisplayRow)
            info = ApplyMeasureRow(dbc, info, measure, profile);

        if (profile.Key.Equals("wand", StringComparison.OrdinalIgnoreCase))
            ValidateWandVisual(info.SpellVisualId);

        _logger.LogInformation(
            "WeaponDonorResolver: {Type} → display row {Row} ({Model}){Measure}, extent {Extent:0.###}, palm-back {Back:P0}, {Hints}, spellVisual {Visual}, model2 {Mirror}{Relaxed}",
            profile.Key, info.DisplayRow, info.ModelName,
            info.MeasureDisplayRow is { } mr ? $" measured on row {mr} ({info.MeasureModelName})" : "",
            info.ExtentX, info.PalmBackFraction, info.Orientation, info.SpellVisualId,
            info.MirrorModelName2 ? "mirrored" : "empty",
            relaxed ? " (relaxed texture pass)" : "");
        return info;
    }

    // A valid M2 scaffold need not be a usable ranged display. Fail before reserving any IDs
    // when the mounted donor cannot supply Shoot's precast/cast and projectile references.
    private void ValidateWandVisual(uint visualId)
    {
        DbcWriterService Read(string member) => DbcWriterService.ReadDbc(
            _mpq.ExtractFile(member) ?? throw new InvalidOperationException($"Missing wand visual table {member}."), member);
        ValidateWandVisualRows(visualId, Read(@"DBFilesClient\SpellVisual.dbc"),
            Read(@"DBFilesClient\SpellVisualKit.dbc"), Read(@"DBFilesClient\SpellVisualEffectName.dbc"));
    }

    internal static void ValidateWandVisualRows(uint visualId, DbcWriterService visuals,
        DbcWriterService kits, DbcWriterService effects)
    {
        var visual = visualId == 0 ? null : visuals.GetRow(visualId);
        if (visual is null || visual.Length <= 7 || visual[1] == 0 || visual[2] == 0 ||
            kits.GetRow(visual[1]) is null || kits.GetRow(visual[2]) is null ||
            visual[6] == 0 || visual[7] == 0 || effects.GetRow(visual[7]) is null)
            throw new InvalidOperationException($"Wand donor SpellVisual {visualId} lacks a complete stock Shoot visual chain; refusing to forge a silent wand.");
    }

    /// <summary>Stock rows (below the custom floor) whose ModelName1 starts with a family pattern
    /// and which carry no SECOND model — except a second model naming the same file, the stock
    /// thrown-weapon shape (fist weapons pair two different models and are never a valid
    /// scaffold) — in ascending row-id order so the choice is deterministic.</summary>
    private static IEnumerable<uint[]> MatchingRows(DbcWriterService dbc, WeaponTypeProfile profile)
    {
        var rows = new List<uint[]>();
        foreach (var row in dbc.GetAllRows())
        {
            if (row.Length < WeaponDisplayInfoRow.FieldCount) continue;
            if (row[WeaponDisplayInfoRow.F_Id] >= WeaponIdReservationService.ItemDisplayFloor) continue;
            var model = dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName1]);
            if (model.Length == 0) continue;
            var model2 = dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName2]);
            if (model2.Length != 0 && !string.Equals(model2, model, StringComparison.OrdinalIgnoreCase)) continue;

            string stem = ModelStem(model);
            foreach (var pattern in profile.DonorModelPatterns)
                if (stem.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(row);
                    break;
                }
        }
        rows.Sort((a, b) => a[0].CompareTo(b[0]));
        return rows;
    }

    private WeaponDonorInfo? TryCandidate(DbcWriterService dbc, uint[] row, WeaponTypeProfile profile, bool strict)
    {
        uint displayRow = row[WeaponDisplayInfoRow.F_Id];
        string modelName = dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName1]);
        string stem = ModelStem(modelName);
        if (stem.Length == 0) return null;
        string model2 = dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName2]);
        if (model2.Length != 0 && !string.Equals(model2, modelName, StringComparison.OrdinalIgnoreCase))
            return null; // paired (fist) models are never a scaffold

        string m2Path = $@"{profile.ComponentDir}\{stem}.m2";
        byte[]? m2;
        try { m2 = _mpq.ExtractFile(m2Path); }
        catch { return null; }
        if (m2 is null || m2.Length < 0x100) return null;

        var doc = RawM2Document.Parse(m2, out _);
        if (doc is null) return null;
        if (doc.Views.Count != 4) return null;
        var v0 = doc.Views[0];
        if (v0.Submeshes.Count != 1 || v0.Batches.Count != 1) return null;

        // Texture contract: the batch must sample a Type-2 (DBC-named) slot so TextureName1 drives
        // the pixels. Vanilla v256 header: nTextures at 0x5C, ofsTextures at 0x60, 16-byte records.
        uint nTextures = U32(m2, 0x5C);
        uint ofsTextures = U32(m2, 0x60);
        if (nTextures == 0 || ofsTextures + 16 > m2.Length) return null;
        if (U32(m2, (int)ofsTextures) != 2) return null;      // first slot must be Type 2
        if (strict && nTextures != 1) return null;

        var measured = MeasureModel(m2, doc);
        if (measured is null) return null;

        string texStem = dbc.ReadString(row[WeaponDisplayInfoRow.F_TextureName1]);
        string? blpPath = null;
        if (texStem.Length > 0)
        {
            string candidate = $@"{profile.ComponentDir}\{texStem}.blp";
            byte[]? blp = null;
            try { blp = _mpq.ExtractFile(candidate); } catch { /* treated as missing */ }
            if (blp is { Length: > 0 }) blpPath = candidate;
        }
        if (strict && blpPath is null) return null;

        return new WeaponDonorInfo
        {
            TypeKey = profile.Key,
            DisplayRow = displayRow,
            ModelName = stem,
            M2Path = m2Path,
            BlpPath = blpPath,
            IconStem = dbc.ReadString(row[WeaponDisplayInfoRow.F_InventoryIcon]),
            GroupSoundIndex = row[WeaponDisplayInfoRow.F_GroupSoundIndex],
            SpellVisualId = row[WeaponDisplayInfoRow.F_SpellVisualId],
            MirrorModelName2 = model2.Length != 0,
            ExtentX = measured.Value.Extent,
            PalmBackFraction = measured.Value.PalmBack,
            Orientation = measured.Value.Hints,
        };
    }

    /// <summary>Re-measure on a representative stock row: length, palm-back and orientation
    /// hints plus the presentation fields (icon, sound group, SpellVisual, ModelName2 mirror) come
    /// from it; the scaffold bytes stay with the structural donor. Falls back to the scaffold's
    /// own measurements (with a warning) when the row or its model is unusable.</summary>
    private WeaponDonorInfo ApplyMeasureRow(DbcWriterService dbc, WeaponDonorInfo scaffold, uint measureRow, WeaponTypeProfile profile)
    {
        var row = dbc.GetRow(measureRow);
        if (row is null)
        {
            _logger.LogWarning("WeaponDonorResolver: measure row {Row} for {Type} is missing; measuring the scaffold itself", measureRow, profile.Label);
            return scaffold;
        }
        string modelName = dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName1]);
        string stem = ModelStem(modelName);
        string m2Path = $@"{profile.ComponentDir}\{stem}.m2";
        byte[]? m2 = null;
        try { m2 = _mpq.ExtractFile(m2Path); } catch { /* reported below */ }
        var doc = m2 is { Length: >= 0x100 } ? RawM2Document.Parse(m2, out _) : null;
        var measured = doc is null ? null : MeasureModel(m2!, doc);
        if (measured is null)
        {
            _logger.LogWarning("WeaponDonorResolver: measure row {Row} ({Model}) for {Type} could not be measured; measuring the scaffold itself", measureRow, stem, profile.Label);
            return scaffold;
        }
        string model2 = dbc.ReadString(row[WeaponDisplayInfoRow.F_ModelName2]);
        string icon = dbc.ReadString(row[WeaponDisplayInfoRow.F_InventoryIcon]);
        return scaffold with
        {
            MeasureDisplayRow = measureRow,
            MeasureModelName = stem,
            ExtentX = measured.Value.Extent,
            PalmBackFraction = measured.Value.PalmBack,
            Orientation = measured.Value.Hints,
            IconStem = icon.Length > 0 ? icon : scaffold.IconStem,
            GroupSoundIndex = row[WeaponDisplayInfoRow.F_GroupSoundIndex],
            SpellVisualId = row[WeaponDisplayInfoRow.F_SpellVisualId],
            MirrorModelName2 = model2.Length != 0 && string.Equals(model2, modelName, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Length, palm-back and orientation from the model's VERTICES. The header box
    /// (0xB4/0xC0) is the animated extent — identical to the vertex box for every static melee
    /// donor, but inflated by the throw spin, the bow draw, and muzzle-flash emitters on ranged
    /// models — so it is only the fallback when a model carries no vertex array.</summary>
    private static (float Extent, float PalmBack, WeaponOrientationHints Hints)? MeasureModel(byte[] m2, RawM2Document doc)
    {
        Vector3[] positions;
        try { positions = doc.ReadVertexPositions(); }
        catch { positions = []; }

        Vector3 min, max;
        if (positions.Length >= 3)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }
        else
        {
            min = V3(m2, 0x0B4);
            max = V3(m2, 0x0C0);
        }
        float extent = max.X - min.X;
        if (!float.IsFinite(extent) || extent < 0.15f || extent > 6f) return null;
        float palmBack = Math.Clamp(-min.X / extent, 0f, 0.9f);
        var hints = positions.Length >= 3
            ? WeaponOrientationHints.Measure(positions)
            : new WeaponOrientationHints
            {
                WideAxisIsZ = (max.Z - min.Z) >= (max.Y - min.Y),
                ExtentY = max.Y - min.Y, ExtentZ = max.Z - min.Z,
                BoxCenterY = (min.Y + max.Y) * 0.5f, BoxCenterZ = (min.Z + max.Z) * 0.5f,
                TipSkewY = 0f, TipSkewZ = 0f, GripSkewY = 0f, GripSkewZ = 0f, TipSpreadRatio = 1f,
            };
        return (extent, palmBack, hints);
    }

    /// <summary>The effective mounted ItemDisplayInfo, read once. Excludes the Forge's own patch-5
    /// so donor scanning never reads Forge output back as input; stock rows are identical either way.</summary>
    private DbcWriterService LoadDbc()
    {
        lock (_dbcLock)
        {
            if (_dbc is not null) return _dbc;
            var bytes = _mpq.ExtractFile(WeaponNaming.ItemDisplayInfoMember,
                    skipArchive: name => name.StartsWith("patch-5", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Could not extract ItemDisplayInfo.dbc from the mounted archives.");
            _dbc = DbcWriterService.ReadDbc(bytes, WeaponNaming.ItemDisplayInfoMember);
            return _dbc;
        }
    }

    /// <summary>"ITEM\...\Sword_1H_Short_A_01.mdx" → "Sword_1H_Short_A_01".</summary>
    private static string ModelStem(string modelName)
    {
        int slash = modelName.LastIndexOfAny(['\\', '/']);
        string file = slash >= 0 ? modelName[(slash + 1)..] : modelName;
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static uint U32(byte[] b, int o) =>
        o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)) : 0;
    private static float F32(byte[] b, int o) =>
        o + 4 <= b.Length ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o, 4)) : 0f;
    private static Vector3 V3(byte[] b, int o) => new(F32(b, o), F32(b, o + 4), F32(b, o + 8));
}

/// <summary>The resolved stock donor for one weapon family — everything type-specific the
/// importer, writer, and build service consume.</summary>
public sealed record WeaponDonorInfo
{
    public required string TypeKey { get; init; }
    /// <summary>The structural scaffold row whose M2 bytes the writer appends onto.</summary>
    public required uint DisplayRow { get; init; }
    /// <summary>Model stem without directory/extension, e.g. "Sword_1H_Short_A_01".</summary>
    public required string ModelName { get; init; }
    public required string M2Path { get; init; }
    /// <summary>Extractable donor BLP member, when the donor row names one; null on the relaxed pass.</summary>
    public string? BlpPath { get; init; }
    public required string IconStem { get; init; }
    public required uint GroupSoundIndex { get; init; }
    /// <summary>ItemDisplayInfo field 10 of the representative stock row — the ranged projectile
    /// visual (bows 5, firearms 224, thrown 98); 0 on every melee donor. Carried onto forged rows.</summary>
    public uint SpellVisualId { get; init; }
    /// <summary>True when the representative stock row sets ModelName2 = ModelName1 (every stock
    /// thrown weapon); the forged display row mirrors its own model name the same way.</summary>
    public bool MirrorModelName2 { get; init; }
    /// <summary>Donor vertex-box X extent (WoW units) — the length imports are scaled to.</summary>
    public required float ExtentX { get; init; }
    /// <summary>−minX/extent of the donor vertex box: how far the weapon reaches behind the palm.
    /// 0.188 for the golden sword; ~mid-shaft for staves; 0.5 for bows.</summary>
    public required float PalmBackFraction { get; init; }
    /// <summary>Cross-section facts (wide axis, tip-side skew, box centre) measured from the
    /// representative stock model; consumed by <see cref="WeaponNormalizer"/>.</summary>
    public required WeaponOrientationHints Orientation { get; init; }
    /// <summary>When the profile measures on a separate representative row, that row and model.</summary>
    public uint? MeasureDisplayRow { get; init; }
    public string? MeasureModelName { get; init; }
}
