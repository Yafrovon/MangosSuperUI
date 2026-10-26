using System.Text.Json;

namespace MangosSuperUI.Services;

/// <summary>
/// Spell Completer artifact store.
///
/// WHERE (2026-10-25, MSUIClient SPELL_CREATOR_PLAN.md P0.6): App_Data/spell-completer/{entry}/
/// under the content root, keyed by the completed spell's ENTRY. It used to live under
/// wwwroot/images/textures/custom/{SafeName(spellName)}, which made two spells with the same
/// name overwrite each other's design bytes and put unreviewed client uploads in a statically
/// served folder. Reads still fall back to that legacy folder, so a spell completed before the
/// move keeps rebuilding exactly as it did.
///
/// The MSUIClient creator exports a session file (spell-session.json) whose
/// spells carry their design as CONCRETE BYTES: patched effect M2s keyed by the
/// original model path, plus recolored BLPs. The Spell Completer page persists
/// those bytes here — under the same per-spell texture-cache directory the rest
/// of the patch pipeline already uses (wwwroot/images/textures/custom/{safeName})
/// — so every unified patch rebuild reproduces the design without the session
/// file being present.
///
/// Files written:
///   completer_manifest.json   — mapping of stored blobs to MPQ/model paths
///   completer_m2_{i}.bin      — patched effect M2 (keyed by ORIGINAL model path;
///                               the rebuild substitutes it for the matching
///                               cloned effect file, so the custom spell is
///                               isolated from the vanilla spell)
///   completer_extra_{i}.bin   — file added to the MPQ verbatim at its stated
///                               path: tinted BLPs at their original paths and
///                               patched GEOMETRY M2s (per-particle models whose
///                               path lives inside the host M2's bytes and
///                               cannot be re-pointed). These override the art
///                               globally — same semantics as the creator's own
///                               patch-4.MPQ export.
///   completer_audio_{i}.bin   — one phase's custom WAV/MP3. Unlike the two
///                               above these are not just dropped into the MPQ:
///                               each also needs a SoundEntries.dbc row and the
///                               cloned SpellVisualKit for its phase pointed at
///                               it, so the manifest carries the full DBC field
///                               set (volume, flags, distances, EAX) with them.
/// </summary>
public static class CompleterStore
{
    public const string ManifestName = "completer_manifest.json";

    public sealed class Manifest
    {
        public string TempName { get; set; } = "";
        public int SourceSpellEntry { get; set; }
        public string ExportedAtUtc { get; set; } = "";
        public List<ManifestModel> Models { get; set; } = new();
        public List<ManifestExtra> ExtraFiles { get; set; } = new();
        public List<ManifestAudio> AudioTracks { get; set; } = new();
        /// <summary>The creator's kit-level composition, applied at every rebuild.</summary>
        public SpellComposition? Composition { get; set; }
    }

    public sealed class ManifestModel
    {
        /// <summary>Original model path as exported (e.g. Spells\Fireball_Missile_Low.m2).</summary>
        public string OriginalPath { get; set; } = "";
        public string File { get; set; } = "";
    }

    public sealed class ManifestExtra
    {
        /// <summary>MPQ path the bytes are written to verbatim.</summary>
        public string MpqPath { get; set; } = "";
        public string File { get; set; } = "";
    }

    /// <summary>One phase's replacement sound, with everything SoundEntries.dbc
    /// needs to describe it. Field names mirror the creator's session schema so a
    /// v2 audio entry maps across one-for-one.</summary>
    public sealed class ManifestAudio
    {
        /// <summary>Which spell phase this replaces: precast, cast, missile,
        /// impact, state, channel or area.</summary>
        public string Cue { get; set; } = "";
        /// <summary>MPQ path the audio file is written to.</summary>
        public string MpqPath { get; set; } = "";
        public string File { get; set; } = "";
        /// <summary>The SoundEntries id the SOURCE spell used for this cue, kept
        /// for provenance — the completed spell always gets a fresh row.</summary>
        public uint SourceSoundId { get; set; }
        public float Volume { get; set; } = 1f;
        public bool Looping { get; set; }
        public bool NoDuplicates { get; set; }
        public uint SoundType { get; set; } = 1;
        public uint ExtraFlags { get; set; }
        public uint Eax { get; set; }
        public float MinDistance { get; set; }
        public float CutoffDistance { get; set; }
    }

    /// <summary>Same sanitization the texture cache and LoadPatchedM2s use.</summary>
    public static string SafeName(string spellName) =>
        new(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

    /// <summary>Which spell's store: the durable entry-keyed folder, with the legacy
    /// name-keyed wwwroot folder as the read fallback.</summary>
    public readonly record struct StoreKey(string ContentRoot, string WebRoot, int Entry, string SpellName)
    {
        public string Dir => Path.Combine(ContentRoot, "App_Data", "spell-completer", Entry.ToString());
        public string LegacyDir => DirFor(WebRoot, SpellName);

        /// <summary>The folder a READ uses: the durable one when it holds a manifest.</summary>
        public string ReadDir => File.Exists(Path.Combine(Dir, ManifestName)) ? Dir : LegacyDir;
    }

    public static string DirFor(string webRoot, string spellName) =>
        Path.Combine(webRoot, "images", "textures", "custom", SafeName(spellName));

    /// <summary>Canonical key for matching M2 paths across exporters: lowercase,
    /// backslashes, extension stripped (.m2/.mdx/.mdl are the same model).</summary>
    public static string NormalizeM2Key(string path)
    {
        string p = path.Replace('/', '\\').Trim().ToLowerInvariant();
        if (p.EndsWith(".m2") || p.EndsWith(".mdx") || p.EndsWith(".mdl"))
            p = p[..p.LastIndexOf('.')];
        return p;
    }

    /// <summary>Persist a completed spell's design bytes. Replaces any previous
    /// completer artifacts for this spell (re-completing is idempotent).</summary>
    public static void Save(StoreKey key, Manifest manifestMeta,
        List<(string originalPath, byte[] bytes)> models,
        List<(string mpqPath, byte[] bytes)> extraFiles,
        List<(ManifestAudio meta, byte[] bytes)>? audioTracks = null)
    {
        string dir = key.Dir;
        Directory.CreateDirectory(dir);

        foreach (string stale in Directory.GetFiles(dir, "completer_*"))
            File.Delete(stale);

        var manifest = new Manifest
        {
            TempName = manifestMeta.TempName,
            SourceSpellEntry = manifestMeta.SourceSpellEntry,
            ExportedAtUtc = manifestMeta.ExportedAtUtc,
            Composition = manifestMeta.Composition,
        };
        for (int i = 0; i < models.Count; i++)
        {
            string file = $"completer_m2_{i}.bin";
            File.WriteAllBytes(Path.Combine(dir, file), models[i].bytes);
            manifest.Models.Add(new ManifestModel { OriginalPath = models[i].originalPath, File = file });
        }
        for (int i = 0; i < extraFiles.Count; i++)
        {
            string file = $"completer_extra_{i}.bin";
            File.WriteAllBytes(Path.Combine(dir, file), extraFiles[i].bytes);
            manifest.ExtraFiles.Add(new ManifestExtra { MpqPath = extraFiles[i].mpqPath, File = file });
        }
        for (int i = 0; i < (audioTracks?.Count ?? 0); i++)
        {
            string file = $"completer_audio_{i}.bin";
            File.WriteAllBytes(Path.Combine(dir, file), audioTracks![i].bytes);
            ManifestAudio meta = audioTracks[i].meta;
            meta.File = file;
            manifest.AudioTracks.Add(meta);
        }

        File.WriteAllText(Path.Combine(dir, ManifestName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Manifest? LoadManifest(StoreKey key)
    {
        string path = Path.Combine(key.ReadDir, ManifestName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Patched effect M2s keyed by NormalizeM2Key(original path), for
    /// SpellPatchRequest.PerPathPatchedM2s. Null when this spell has none.</summary>
    public static Dictionary<string, byte[]>? LoadPerPathM2s(StoreKey key)
    {
        var manifest = LoadManifest(key);
        if (manifest is null || manifest.Models.Count == 0) return null;
        string dir = key.ReadDir;
        var result = new Dictionary<string, byte[]>();
        foreach (var model in manifest.Models)
        {
            string file = Path.Combine(dir, model.File);
            if (File.Exists(file))
                result[NormalizeM2Key(model.OriginalPath)] = File.ReadAllBytes(file);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>This spell's custom phase audio, for SpellPatchRequest.CustomAudio.
    /// Null when it has none. A track whose blob went missing is dropped rather
    /// than returned empty — the patch builder would otherwise mint a
    /// SoundEntries row pointing at a file the MPQ never receives.</summary>
    public static List<CustomAudioTrack>? LoadAudio(StoreKey key)
    {
        var manifest = LoadManifest(key);
        if (manifest is null || manifest.AudioTracks.Count == 0) return null;
        string dir = key.ReadDir;
        var result = new List<CustomAudioTrack>();
        foreach (var track in manifest.AudioTracks)
        {
            string file = Path.Combine(dir, track.File);
            if (!File.Exists(file)) continue;
            result.Add(new CustomAudioTrack
            {
                Cue = track.Cue,
                MpqPath = track.MpqPath,
                Bytes = File.ReadAllBytes(file),
                SourceSoundId = track.SourceSoundId,
                Volume = track.Volume,
                Looping = track.Looping,
                NoDuplicates = track.NoDuplicates,
                SoundType = track.SoundType,
                ExtraFlags = track.ExtraFlags,
                Eax = track.Eax,
                MinDistance = track.MinDistance,
                CutoffDistance = track.CutoffDistance,
            });
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Verbatim MPQ files (tinted BLPs, geometry M2s), for
    /// SpellPatchRequest.ExtraMpqFiles. Null when this spell has none.</summary>
    public static Dictionary<string, byte[]>? LoadExtraFiles(StoreKey key)
    {
        var manifest = LoadManifest(key);
        if (manifest is null || manifest.ExtraFiles.Count == 0) return null;
        string dir = key.ReadDir;
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in manifest.ExtraFiles)
        {
            string file = Path.Combine(dir, extra.File);
            if (File.Exists(file))
                result[extra.MpqPath] = File.ReadAllBytes(file);
        }
        return result.Count > 0 ? result : null;
    }
}
