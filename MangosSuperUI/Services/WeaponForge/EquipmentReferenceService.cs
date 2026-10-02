using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using MangosSuperUI.Services.ArmorForge;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>Read-only study of original mounted equipment. Statistics describe the requested sample,
/// never an art-quality verdict or a historical polygon budget. All input bytes come from MPQs.</summary>
public sealed class EquipmentReferenceService
{
    public const int MaximumPageSize = 100;
    private readonly LegacyImportSources _sources;
    private readonly ArmorImportSources _armor;
    private readonly WeaponPreviewService _preview;

    public EquipmentReferenceService(LegacyImportSources sources, ArmorImportSources armor, WeaponPreviewService preview)
        => (_sources, _armor, _preview) = (sources, armor, preview);

    public static string ValidateSource(string? source) => source?.ToLowerInvariant() switch
    {
        "vanilla" => "vanilla", "tbc" => "tbc",
        _ => throw new ArgumentException("Source must be vanilla or tbc; modern Classic and later expansions are not this corpus.")
    };

    public static string ValidateKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "weapon" => "weapon", "armor" => "armor",
        _ => throw new ArgumentException("Kind must be weapon or armor.")
    };

    public static string ValidateRaceGender(string value)
    {
        var match = ArmorNaming.HelmVariantSuffixes.Concat(new[] { "BeM", "BeF", "DrM", "DrF" })
            .FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException("Unknown helm race/gender suffix.");
    }

    public EquipmentReferencePage Browse(string source, string kind, string? family, string? search, int skip, int take)
    {
        var context = Open(source, kind);
        var filtered = Filter(context.Items, family, search);
        skip = Math.Max(0, skip); take = Math.Clamp(take, 1, MaximumPageSize);
        var items = filtered.Skip(skip).Take(take).ToArray();
        return new(context.Provenance, kind, filtered.Count, skip, take, skip + items.Length < filtered.Count,
            items, context.Items.Select(x => x.Family).Distinct().Order().ToArray(), context.Notes);
    }

    public EquipmentReferenceInspection Inspect(string source, string kind, uint displayId,
        string? family, string raceGender, bool includePreview, CancellationToken cancellationToken)
    {
        var context = Open(source, kind);
        var item = Find(context, displayId, family);
        return Inspect(context, item, ValidateRaceGender(raceGender), includePreview, cancellationToken);
    }

    public EquipmentReferenceSample Measure(string source, string kind, string? family, string? search,
        int skip, int take, string raceGender, CancellationToken cancellationToken)
    {
        var context = Open(source, kind);
        raceGender = ValidateRaceGender(raceGender);
        var filtered = Filter(context.Items, family, search);
        skip = Math.Max(0, skip); take = Math.Clamp(take, 1, MaximumPageSize);
        var items = filtered.Skip(skip).Take(take).ToArray();
        var inspections = new List<EquipmentReferenceInspection>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspections.Add(Inspect(context, item, raceGender, false, cancellationToken));
        }
        var groups = inspections.GroupBy(x => x.Item.Family).Select(group =>
        {
            var models = group.SelectMany(x => x.Models).Where(x => x.Geometry is not null)
                .DistinctBy(x => x.Geometry!.GeometrySha256).ToArray();
            return new EquipmentReferenceDistribution(group.Key, group.Count(), models.Length,
                Quantiles(models.Select(x => (double)x.Geometry!.VertexCount)),
                Quantiles(models.Select(x => (double)x.Geometry!.TriangleCount)),
                Quantiles(models.Select(x => (double)x.TextureSlots.Count)));
        }).ToArray();
        return new(context.Provenance, kind, filtered.Count, skip, take, skip + items.Length < filtered.Count,
            raceGender, inspections, groups,
            "Descriptive statistics for this display-ID-ordered page only, deduplicated by exact ordered positions and indices within each family. Not a random sample, complete corpus, historical budget, or visual acceptance test. Painted armor has no independent model and contributes no zero-triangle observations.");
    }

    public byte[] Texture(string source, string kind, uint displayId, string? family, string raceGender, int index)
    {
        var context = Open(source, kind);
        var item = Find(context, displayId, family);
        var resolved = Resolve(context, item, ValidateRaceGender(raceGender));
        if (index < 0 || index >= resolved.Textures.Count) throw new KeyNotFoundException("Texture index not found.");
        var bytes = context.Source.Mpq.ExtractFile(resolved.Textures[index]);
        if (bytes is null) throw new KeyNotFoundException("Original texture member is missing.");
        ValidateTextureEnvelope(bytes);
        return BlpDecoder.ToPngBytes(bytes);
    }

    private Context Open(string source, string kind)
    {
        source = ValidateSource(source); kind = ValidateKind(kind);
        var lane = _sources.Get(source);
        var status = lane.Mpq.Status();
        if (!status.Configured || status.ArchiveCount == 0 || status.Error is not null)
            throw new InvalidOperationException(status.Error ?? $"The {lane.Label} original MPQ mount is unavailable.");
        var bytes = lane.Mpq.ExtractFile(WeaponNaming.ItemDisplayInfoMember)
            ?? throw new InvalidOperationException("Original ItemDisplayInfo.dbc is missing from the selected mount.");
        var dbc = DbcWriterService.ReadDbc(bytes, "equipment-reference:" + source);
        var provenance = new EquipmentReferenceProvenance(source, lane.Label, status.ArchiveCount,
            WeaponNaming.ItemDisplayInfoMember, Hash(bytes), DateTimeOffset.UtcNow,
            source == "vanilla" ? "VanillaMpqSource stock archive allowlist (base through patch-2); asset hashes identify actual inputs."
                : "Configured TBC MPQ mount with its patch precedence; lane name alone does not certify unmodified Blizzard bytes.");
        var notes = new List<string> { "Preview is a source-byte WebGL study view, not proof of the in-game result. Geometry is the first parsed skin view; material passes can draw it more than once." };
        var items = new List<EquipmentReferenceItem>();
        if (kind == "weapon")
        {
            notes.Add("Uses the existing Forge weapon index, which excludes distinct paired weapon models. Family/name metadata comes from the item catalog where available, otherwise model naming; it does not establish asset authorship date.");
            var names = lane.Items.ByDisplayId;
            foreach (var entry in lane.Mpq.WeaponIndex())
            {
                names.TryGetValue(entry.DisplayRow, out var candidates);
                var item = candidates?.OrderBy(x => x.Entry).FirstOrDefault();
                string familyKey = item is null ? InferFamily(entry.ModelStem, entry.M2Path)
                    : LegacyItemCatalog.TypeKeyFor(item.ItemClass, item.Subclass) ?? "unknown";
                items.Add(new(entry.DisplayRow, item?.Entry, item?.Name ?? entry.ModelStem, familyKey,
                    "Modelled", null, item?.Quality, item?.InventoryType, item?.ItemLevel, null,
                    entry.M2Path, entry.BlpPath, item is null ? "model-name inference" : "item catalog class/subclass"));
            }
        }
        else if (source == "tbc")
        {
            items.AddRange(_armor.Tbc.Catalog.Browse().Select(x => new EquipmentReferenceItem(x.DisplayId,
                x.Entry, x.Name, x.FamilyKey, x.RenderKind.ToString(), x.Material.ToString(), x.Quality,
                x.InventoryType, x.ItemLevel, x.SetName, null, null, "TBC armor item catalog"))
                .DistinctBy(x => (x.DisplayId, x.Family)));
        }
        else
        {
            notes.Add("Vanilla armor browses raw original display rows. Item slot, material and item name are not inferred from shared set-template rows. Family body means all referenced body components, not one equipped item's overlay.");
            foreach (var row in dbc.GetAllRows())
            {
                if (row.Length < 22) continue;
                string model = Stem(dbc.ReadString(row[1]));
                string familyKey = model.StartsWith("Helm", StringComparison.OrdinalIgnoreCase) ? "helm"
                    : model.Contains("Shoulder", StringComparison.OrdinalIgnoreCase) ? "shoulder"
                    : "body";
                if (familyKey == "body" && !Enumerable.Range(14, 8).Any(i => dbc.ReadString(row[i]).Length > 0)) continue;
                string label = model.Length > 0 ? model : Enumerable.Range(14, 8).Select(i => dbc.ReadString(row[i])).First(x => x.Length > 0);
                items.Add(new(row[0], null, $"Display {row[0]}: {label}", familyKey,
                    familyKey == "body" ? "Painted" : "Modelled", null, null, null, null, null, null, null,
                    "raw original display row; model-name family classification"));
            }
        }
        return new(lane, kind, dbc, ItemDisplayInfoLayout.DetectComponentBase(dbc), provenance,
            items.OrderBy(x => x.DisplayId).ThenBy(x => x.Family, StringComparer.Ordinal).ToArray(), notes);
    }

    private static IReadOnlyList<EquipmentReferenceItem> Filter(IReadOnlyList<EquipmentReferenceItem> items, string? family, string? search)
    {
        if (search?.Length > 200) throw new ArgumentException("Search is limited to 200 characters.");
        return items.Where(x => string.IsNullOrWhiteSpace(family) || x.Family.Equals(family, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || x.DisplayId.ToString() == search.Trim() || x.Entry?.ToString() == search.Trim())
            .ToArray();
    }

    private static EquipmentReferenceItem Find(Context context, uint id, string? family)
    {
        var matches = context.Items.Where(x => x.DisplayId == id && (string.IsNullOrWhiteSpace(family)
            || x.Family.Equals(family, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0) throw new KeyNotFoundException("Display not found in the selected reference catalog.");
        if (matches.Length > 1) throw new ArgumentException("This display is used in several slots; specify family.");
        return matches[0];
    }

    private Resolved Resolve(Context context, EquipmentReferenceItem item, string raceGender)
    {
        var row = context.Dbc.GetRow(item.DisplayId) ?? throw new KeyNotFoundException("Original display row is missing.");
        string S(int field) => field < row.Length ? context.Dbc.ReadString(row[field]) : "";
        var models = new List<ModelSource>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void TexturePath(string? path) { if (!string.IsNullOrWhiteSpace(path)) paths.Add(path); }
        if (context.Kind == "weapon") models.Add(new(item.ModelPath!, item.TexturePath, "weapon"));
        else if (item.Family == "helm")
        {
            models.Add(new($@"{ArmorNaming.HeadDir}\{Stem(S(1))}_{raceGender}.m2",
                S(3).Length > 0 ? $@"{ArmorNaming.HeadDir}\{Stem(S(3))}.blp" : null, raceGender));
        }
        else if (item.Family == "shoulder")
        {
            if (S(1).Length > 0) models.Add(new($@"{ArmorNaming.ShoulderDir}\{Stem(S(1))}.m2",
                S(3).Length > 0 ? $@"{ArmorNaming.ShoulderDir}\{Stem(S(3))}.blp" : null, "left"));
            if (S(2).Length > 0) models.Add(new($@"{ArmorNaming.ShoulderDir}\{Stem(S(2))}.m2",
                S(4).Length > 0 ? $@"{ArmorNaming.ShoulderDir}\{Stem(S(4))}.blp" : null, "right"));
        }
        else if (item.RenderKind == nameof(ArmorRenderKind.Cloak))
            TexturePath(S(3).Length > 0 ? $@"{ArmorNaming.CapeDir}\{Stem(S(3))}.blp" : null);
        else
        {
            IEnumerable<int> slots = item.Family == "body" ? Enumerable.Range(0, 8) : ArmorTypeCatalog.Get(item.Family).PaintedSlots;
            foreach (int slot in slots)
            {
                var partial = S(context.ComponentBase + slot);
                if (partial.Length == 0) continue;
                bool found = false;
                foreach (var suffix in new[] { "_M", "_F", "_U", "" })
                {
                    string path = $@"Item\TextureComponents\{ArmorNaming.ComponentSubdirs[slot]}\{partial}{suffix}.blp";
                    if (!context.Source.Mpq.HasFile(path)) continue;
                    TexturePath(path); found = true;
                }
                if (!found) TexturePath($@"Item\TextureComponents\{ArmorNaming.ComponentSubdirs[slot]}\{partial}_U.blp");
            }
        }
        foreach (var model in models)
        {
            TexturePath(model.TexturePath);
            var parsed = context.Source.Mpq.LoadM2Detailed(model.Path);
            model.Model = parsed.Model; model.Bytes = parsed.M2Bytes; model.Error = parsed.Error;
            if (parsed.Model is null) continue;
            if ((context.Source.Key == "vanilla" && parsed.Model.Version is not (256 or 257))
                || parsed.Model.Version is < 256 or > 263)
            {
                model.Error = $"M2 version {parsed.Model.Version} is outside the selected original-client cohort.";
                model.Model = null;
                continue;
            }
            foreach (var texture in parsed.Model.Textures) TexturePath(WeaponPreviewService.StockPreviewTexturePath(texture));
        }
        return new(models, paths.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private EquipmentReferenceInspection Inspect(Context context, EquipmentReferenceItem item, string raceGender,
        bool includePreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(context, item, raceGender);
        var textureBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var textures = new List<EquipmentReferenceTexture>();
        foreach (var path in resolved.Textures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = $"/EquipmentReference/Texture?source={context.Source.Key}&kind={context.Kind}&displayId={item.DisplayId}&family={Uri.EscapeDataString(item.Family)}&raceGender={raceGender}&index={textures.Count}";
            var bytes = context.Source.Mpq.ExtractFile(path);
            if (bytes is null) { textures.Add(new(path, null, 0, null, null, "Original BLP member missing.")); continue; }
            try
            {
                ValidateTextureEnvelope(bytes);
                var pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
                var metrics = TextureStatistics(pixels, width, height);
                textures.Add(new(path, Hash(bytes), bytes.Length, metrics, url, null));
                textureBytes[path] = bytes;
            }
            catch (Exception ex) { textures.Add(new(path, Hash(bytes), bytes.Length, null, null, ex.Message)); }
        }
        var models = new List<EquipmentReferenceModel>();
        foreach (var source in resolved.Models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Model is not { } model || source.Bytes is null)
            {
                models.Add(new(source.Path, source.Variant, source.Bytes is null ? null : Hash(source.Bytes),
                    source.Bytes?.Length ?? 0, null, null, Array.Empty<EquipmentReferenceTextureSlot>(), null, source.Error));
                continue;
            }
            var slots = model.Textures.Select((t, i) => new EquipmentReferenceTextureSlot(i, t.Type, t.Flags,
                WeaponPreviewService.UsesDisplayTexture(t) ? source.TexturePath : WeaponPreviewService.StockPreviewTexturePath(t),
                WeaponPreviewService.SampledTextureSlots(model).Contains(i))).ToArray();
            WeaponPreviewResult? preview = null;
            if (includePreview)
            {
                byte[]? skin = null;
                if (source.TexturePath is not null) textureBytes.TryGetValue(source.TexturePath, out skin);
                try { preview = _preview.RenderFromBytes(source.Bytes, skin, textureBytes, preserveSourceGraph: true); }
                catch (Exception ex) { preview = WeaponPreviewResult.Fail(ex.Message); }
            }
            models.Add(new(source.Path, source.Variant, Hash(source.Bytes), source.Bytes.Length, model.Version,
                GeometryStatistics(model), slots, preview, null));
        }
        int shift = context.ComponentBase - 14;
        var raw = context.Dbc.GetRow(item.DisplayId)!;
        uint F(int index) => index + shift < raw.Length ? raw[index + shift] : 0;
        return new(context.Provenance, item, raceGender, models, textures,
            new[] { F(6), F(7), F(8) }, new[] { F(12), F(13) },
            "Metrics and preview describe original source bytes only. No native-client fit, animation, race coverage, coherence or art validation has been performed. ItemVisual-mounted effects are not included in this study preview.");
    }

    public static EquipmentReferenceGeometry GeometryStatistics(M2Model model)
    {
        using var hashStream = new MemoryStream();
        using var writer = new BinaryWriter(hashStream);
        writer.Write(model.Vertices.Count); writer.Write(model.Indices.Count);
        foreach (var vertex in model.Vertices) { writer.Write(vertex.PosX); writer.Write(vertex.PosY); writer.Write(vertex.PosZ); }
        foreach (var index in model.Indices) writer.Write(index);
        var positions = model.Vertices.Select(x => new Vector3(x.PosX, x.PosY, x.PosZ)).ToArray();
        int invalid = positions.Count(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y) || !float.IsFinite(x.Z));
        var finite = positions.Where(x => float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z)).ToArray();
        return new(model.Vertices.Count, finite.Distinct().Count(), model.Indices.Count / 3, model.Submeshes.Count,
            model.Batches.Count, model.Bones.Count, model.ParticleEmitterCount, model.RibbonEmitterCount,
            Hash(hashStream.ToArray()), Bounds(finite), Bounds(finite.Select(x => new Vector3(x.X, -x.Z, x.Y)).ToArray()), invalid);
    }

    private static EquipmentReferenceBounds? Bounds(Vector3[] values)
    {
        if (values.Length == 0) return null;
        var min = values.Aggregate(Vector3.Min); var max = values.Aggregate(Vector3.Max);
        return new(new[] { min.X, min.Y, min.Z }, new[] { max.X, max.Y, max.Z },
            new[] { max.X - min.X, max.Y - min.Y, max.Z - min.Z });
    }

    public static EquipmentReferenceQuantiles? Quantiles(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        double Q(double p)
        {
            double position = (sorted.Length - 1) * p;
            int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }
        return new(sorted.Length, sorted[0], Q(.25), Q(.5), Q(.75), Q(.9), sorted[^1]);
    }

    public static EquipmentReferenceTextureStatistics TextureStatistics(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != bgra.Length)
            throw new ArgumentException("Decoded texture dimensions do not match BGRA pixels.");
        double weight = 0, weightedLuminance = 0; var bins = new double[16];
        int visible = 0, translucent = 0;
        static double Linear(byte channel) { double c = channel / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double alpha = bgra[i + 3] / 255.0;
            if (alpha == 0) continue;
            visible++; if (alpha < 1) translucent++;
            double luminance = .2126 * Linear(bgra[i + 2]) + .7152 * Linear(bgra[i + 1]) + .0722 * Linear(bgra[i]);
            weight += alpha; weightedLuminance += luminance * alpha;
            bins[Math.Min(15, (int)(luminance * 16))] += alpha;
        }
        return new(width, height, (long)width * height, visible, translucent,
            (double)visible / (width * (double)height), weight == 0 ? null : weightedLuminance / weight,
            bins.Select(x => weight == 0 ? 0 : x / weight).ToArray(), "Linear-sRGB relative luminance, alpha-weighted, 16 equal-width bins from 0 to 1; transparent pixels excluded. Not a material or lighting judgment.");
    }

    private static void ValidateTextureEnvelope(byte[] bytes)
    {
        if (bytes.Length < 148 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x32504c42)
            throw new InvalidDataException("Only bounded BLP2 decoding is supported by the reference texture endpoint.");
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)), height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        if (width == 0 || height == 0 || width > 4096 || height > 4096 || (ulong)width * height > 4_194_304)
            throw new InvalidDataException("Texture exceeds the study decode limit of 4096 per axis / 4 megapixels.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Stem(string name) => Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
    private static string InferFamily(string stem, string path)
    {
        if (path.Contains("\\Shield\\", StringComparison.OrdinalIgnoreCase)) return "shield";
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sword_1H"] = "sword1h", ["Sword_2H"] = "sword2h", ["Axe_1H"] = "axe1h", ["Axe_2H"] = "axe2h",
            ["Mace_1H"] = "mace1h", ["Mace_2H"] = "mace2h", ["Knife"] = "dagger", ["Dagger"] = "dagger",
            ["Stave"] = "staff", ["Staff"] = "staff", ["Polearm"] = "polearm", ["Bow"] = "bow",
            ["Crossbow"] = "crossbow", ["Gun"] = "gun", ["Wand"] = "wand", ["Thrown"] = "thrown"
        };
        return keys.FirstOrDefault(x => stem.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase)).Value ?? "unknown";
    }

    private sealed record Context(LegacyImportSource Source, string Kind, DbcWriterService Dbc, int ComponentBase,
        EquipmentReferenceProvenance Provenance, IReadOnlyList<EquipmentReferenceItem> Items, IReadOnlyList<string> Notes);
    private sealed record Resolved(IReadOnlyList<ModelSource> Models, IReadOnlyList<string> Textures);
    private sealed class ModelSource(string path, string? texturePath, string variant)
    {
        public string Path { get; } = path;
        public string? TexturePath { get; } = texturePath;
        public string Variant { get; } = variant;
        public M2Model? Model { get; set; }
        public byte[]? Bytes { get; set; }
        public string? Error { get; set; }
    }
}

public sealed record EquipmentReferenceProvenance(string Source, string Label, int ArchiveCount,
    string DbcMember, string DbcSha256, DateTimeOffset MeasuredAtUtc, string MountPolicy);
public sealed record EquipmentReferenceItem(uint DisplayId, uint? Entry, string Name, string Family,
    string RenderKind, string? Material, int? Quality, int? InventoryType, int? ItemLevel, string? SetName,
    string? ModelPath, string? TexturePath, string ClassificationEvidence);
public sealed record EquipmentReferencePage(EquipmentReferenceProvenance Provenance, string Kind, int Total,
    int Skip, int Take, bool HasMore, IReadOnlyList<EquipmentReferenceItem> Items, IReadOnlyList<string> Families, IReadOnlyList<string> Notes);
public sealed record EquipmentReferenceBounds(float[] Min, float[] Max, float[] Size);
public sealed record EquipmentReferenceGeometry(int VertexCount, int DistinctPositions, int TriangleCount,
    int SubmeshCount, int BatchCount, int BoneCount, uint ParticleEmitterCount, uint RibbonEmitterCount,
    string GeometrySha256, EquipmentReferenceBounds? ReaderYUpBounds, EquipmentReferenceBounds? WowZUpBounds, int NonFinitePositions);
public sealed record EquipmentReferenceTextureSlot(int Slot, uint Type, uint Flags, string? Path, bool SampledByBatch);
public sealed record EquipmentReferenceModel(string Path, string Variant, string? Sha256, int ByteLength,
    uint? Version, EquipmentReferenceGeometry? Geometry, IReadOnlyList<EquipmentReferenceTextureSlot> TextureSlots,
    WeaponPreviewResult? Preview, string? Error);
public sealed record EquipmentReferenceTextureStatistics(int Width, int Height, long Pixels, int VisiblePixels,
    int TranslucentPixels, double VisibleCoverage, double? MeanLuminance, double[] LuminanceHistogram, string Definition);
public sealed record EquipmentReferenceTexture(string Path, string? Sha256, int ByteLength,
    EquipmentReferenceTextureStatistics? Statistics, string? PngUrl, string? Error);
public sealed record EquipmentReferenceInspection(EquipmentReferenceProvenance Provenance, EquipmentReferenceItem Item,
    string RaceGender, IReadOnlyList<EquipmentReferenceModel> Models, IReadOnlyList<EquipmentReferenceTexture> Textures,
    uint[] GeosetGroups, uint[] HelmetVisibility, string Limitations);
public sealed record EquipmentReferenceQuantiles(int Count, double Min, double P25, double P50, double P75, double P90, double Max);
public sealed record EquipmentReferenceDistribution(string Family, int DisplayRows, int UniqueGeometries,
    EquipmentReferenceQuantiles? Vertices, EquipmentReferenceQuantiles? Triangles, EquipmentReferenceQuantiles? TextureSlots);
public sealed record EquipmentReferenceSample(EquipmentReferenceProvenance Provenance, string Kind, int Total,
    int Skip, int Take, bool HasMore, string RaceGender, IReadOnlyList<EquipmentReferenceInspection> Items,
    IReadOnlyList<EquipmentReferenceDistribution> Distributions, string SamplingPolicy);
