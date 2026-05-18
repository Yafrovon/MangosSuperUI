using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Owns the writer-version stamps for every on-disk cache that depends
/// on server code (M2 → GLB pipelines, BLP → PNG decoders, etc.) and
/// runs a one-shot startup sweep that deletes any cached file whose
/// embedded version stamp doesn't match the current writer.
///
/// === Problem this solves ===
/// Server code changes invalidate cached outputs. Example: when
/// SkinnedGlbWriter's attachment-position math changed (Session L), every
/// previously-generated HumanMale.glb on disk became wrong. Existing
/// logic checks File.Exists() and returns the stale path — no way to
/// know the content is bad. Without a version stamp, users would have
/// to manually `rm` the file to fix it.
///
/// === Design — auto-version from assembly identity ===
/// Previous revisions of this file used hand-bumped int constants
/// (SkinnedGlbVersion = 2, RigidGlbVersion = 4, ...). That worked but
/// required the developer who changed a writer to remember to also bump
/// the version. Session O handoff Lesson 1 calls this user-hostile:
/// every release that forgets the bump silently serves stale grey
/// content to users whose browser cached the old URL. The Items-page
/// Might-helm-grey bug in May 2026 was exactly this — the writer had
/// been fixed, the on-disk cache had been wiped, but the URL stamps
/// hadn't changed, so browser HTTP caches still served the old grey
/// GLBs.
///
/// New approach: at process startup, compute each version stamp by
/// hashing the assembly that owns the relevant writer. The hash is
/// truncated to a 16-bit unsigned int so it fits in URLs the same way
/// the old constants did (HumanMale.v37214.glb instead of HumanMale.v3.glb).
/// Any rebuild of the assembly produces a different ModuleVersionId
/// (via the compiler's per-build GUID) → different hash → different
/// stamp → every old URL is now a cache miss → fresh content gets
/// fetched everywhere.
///
/// Each generator embeds its stamp in the filename, between the natural
/// name and the extension:
///
///   {natural}.v{stamp}.{ext}
///
///   HumanMale.glb              → HumanMale.v37214.glb
///   31506_helm_HuM.glb         → 31506_helm_HuM.v44103.glb
///   HumanMaleSkin00_00.png     → HumanMaleSkin00_00.v9128.png
///
/// On startup, SweepDirectory enumerates every file in a registered
/// directory and deletes anything whose filename either lacks the
/// .v{stamp}. marker or has a non-current stamp. Service code uses
/// MakeVersioned() to build the path it expects; the natural File.Exists
/// fast-path implicitly enforces version match.
///
/// === Hash inputs ===
/// We hash the ModuleVersionId (a GUID the compiler regenerates on every
/// build of an assembly, baked into the manifest) of the assembly that
/// contains the writer type. This is the same identifier .NET itself
/// uses to tell a "Hello.dll built today" apart from a "Hello.dll built
/// yesterday." We add the writer's MetadataToken as a witness so
/// splitting writers into their own assemblies in a future refactor
/// produces different stamps per writer (rather than collapsing them
/// all to the same MVID).
///
/// === When the same code rebuilds and the stamp changes anyway ===
/// MVIDs change on every successful compilation, even if no source
/// changed. So a "no observable change" rebuild still flips every
/// stamp, forcing a cache wipe + regen on next startup. That's
/// wasteful in theory but the cost is small (re-decode the BLPs, re-
/// write the GLBs — same files, same outputs) and the alternative is
/// either a (much) more expensive source-hash or accepting silent
/// stale-content bugs. We pick the cheap option that's always correct.
///
/// === Constants are now properties ===
/// Existing code reads CacheVersionRegistry.RigidGlbVersion as a const.
/// We keep that interface — they're still ints, still accessible
/// statically — they're just computed once at type-init from the live
/// assembly instead of being hand-typed. Every caller continues to work
/// unchanged.
/// </summary>
public class CacheVersionRegistry
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CacheVersionRegistry> _logger;

    // ── Auto-derived version stamps ────────────────────────────────────
    //
    // Each property is a 16-bit unsigned int derived from a hash of the
    // assembly that owns the corresponding writer. Static readonly so
    // they're computed once at first access (effectively at app startup
    // when the first cache path is built) and stable for the lifetime
    // of the process.
    //
    // Why witness types rather than just "this assembly":
    //   Today every writer is in MangosSuperUI.dll, so the three stamps
    //   would all collapse to the same value — that works but means a
    //   change to GlbWriter forces regen of character GLBs and skin
    //   PNGs too. Witness types let us re-aim at sub-assemblies later
    //   without touching this code: if SkinnedGlbWriter moves to a
    //   separate DLL, that DLL's MVID drives SkinnedGlbVersion and
    //   the others stay independent.
    //
    // Why 16-bit truncation:
    //   The stamps appear in URLs (HumanMale.v37214.glb). 5 hex digits
    //   would be uglier than 5 decimal digits. 65,536 distinct values
    //   gives ~0.0015% collision probability per release pair — not
    //   zero but vanishingly small for human-scale iteration. If two
    //   adjacent releases ever DID collide, the only harm is one round
    //   of "looks like it should have updated but the cache hit" — a
    //   re-bump (touch the source file and rebuild) clears it. Cheap
    //   recovery.

    public static int SkinnedGlbVersion { get; } =
        ComputeStamp(typeof(SkinnedGlbWriter));

    public static int RigidGlbVersion { get; } =
        ComputeStamp(typeof(GlbWriter));

    public static int SkinPngVersion { get; } =
        ComputeStamp(typeof(CharacterSkinCompositor));

    public CacheVersionRegistry(
        IWebHostEnvironment env,
        ILogger<CacheVersionRegistry> logger)
    {
        _env = env;
        _logger = logger;
    }

    // ────────────────────────────────────────────────────────────────────
    // Stamp computation
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute a stable 16-bit version stamp for the assembly containing
    /// the given witness type. Inputs:
    ///   - witness assembly's ModuleVersionId (changes per build)
    ///   - witness type's MetadataToken (stable across builds; lets two
    ///     witnesses in the same assembly produce the same stamp, two
    ///     witnesses in different assemblies produce different stamps)
    ///
    /// Result is in range [1, 65535] — we exclude 0 so a defaulted int
    /// is visibly wrong if it ever shows up in a filename ("v0" would
    /// indicate this method was bypassed somehow).
    ///
    /// SHA256 because it's in the BCL and we don't care about cost
    /// (called three times at startup, never again). Any non-broken
    /// hash would do — SHA1, MD5, even a CRC of the GUID bytes — but
    /// SHA256 lets us avoid a "why aren't we using a stronger hash?"
    /// code-review conversation we'd be wasting time on.
    /// </summary>
    private static int ComputeStamp(Type witness)
    {
        try
        {
            var asm = witness.Assembly;
            // ModuleVersionId is a GUID baked into the assembly manifest
            // by the compiler. Different on every build, identical across
            // every process that loads the same DLL.
            var mvid = asm.ManifestModule.ModuleVersionId;
            var token = witness.MetadataToken;

            var buf = new byte[16 + 4];
            mvid.ToByteArray().CopyTo(buf, 0);
            BitConverter.GetBytes(token).CopyTo(buf, 16);

            byte[] hash = SHA256.HashData(buf);
            // Take 2 bytes → 16-bit. Mask off zero to keep us in [1, 65535].
            int stamp = ((hash[0] << 8) | hash[1]);
            if (stamp == 0) stamp = 1;
            return stamp;
        }
        catch
        {
            // Defensive: if reflection somehow throws (e.g. assembly was
            // loaded in a context that hides the manifest), fall back to
            // a constant — the cache will still work, just won't
            // auto-invalidate. Better than crashing at startup.
            return 1;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Public API: path construction
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a versioned cache path by inserting ".v{N}" between the base
    /// name and the extension.
    ///
    ///   MakeVersioned("/x/HumanMale.glb", 37214)  → "/x/HumanMale.v37214.glb"
    ///   MakeVersioned("/x/foo_bar.png", 9128)     → "/x/foo_bar.v9128.png"
    ///
    /// Works on the filesystem path or the web URL — doesn't care about
    /// which side of the wwwroot it sees. Pass either consistently.
    /// </summary>
    public static string MakeVersioned(string naturalPath, int version)
    {
        if (string.IsNullOrEmpty(naturalPath)) return naturalPath;
        var dir = Path.GetDirectoryName(naturalPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(naturalPath);
        var ext = Path.GetExtension(naturalPath);   // includes the dot
        var versioned = $"{name}.v{version}{ext}";
        // Use the same separator the input used — if it had forward
        // slashes (web URL) preserve them.
        if (naturalPath.Contains('/') && !naturalPath.Contains('\\'))
            return string.IsNullOrEmpty(dir) ? versioned : $"{dir.Replace('\\', '/')}/{versioned}";
        return Path.Combine(dir, versioned);
    }

    // ────────────────────────────────────────────────────────────────────
    // Public API: startup sweep
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Delete every file in <paramref name="subdirRelativeToWwwroot"/>
    /// whose filename doesn't carry ".v{currentVersion}." between basename
    /// and extension. Recursive — sweeps subdirectories too, so per-
    /// displayId cache layouts like /body_atlas_cache/12345/slot5_xxx.png
    /// work.
    ///
    /// If the directory doesn't exist, returns silently — the relevant
    /// service will create it on first write.
    ///
    /// Returns the count of files deleted so the caller can log a summary.
    /// </summary>
    public int SweepDirectory(string subdirRelativeToWwwroot, int currentVersion,
        string? extensionFilter = null)
    {
        var fullDir = Path.Combine(_env.WebRootPath, subdirRelativeToWwwroot);
        if (!Directory.Exists(fullDir)) return 0;

        int deleted = 0;
        int kept = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                // Optional extension filter so a sweep that targets GLBs
                // doesn't accidentally delete PNGs sitting in the same dir.
                if (!string.IsNullOrEmpty(extensionFilter) &&
                    !path.EndsWith(extensionFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (HasCurrentVersionStamp(path, currentVersion))
                {
                    kept++;
                    continue;
                }

                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "CacheSweep: failed to delete stale {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CacheSweep: enumeration failed in {Dir}", fullDir);
            return deleted;
        }

        if (deleted > 0 || kept > 0)
        {
            _logger.LogInformation(
                "CacheSweep: {Dir} (v{Ver}) — deleted {Del} stale files, kept {Kept} current",
                subdirRelativeToWwwroot, currentVersion, deleted, kept);
        }
        return deleted;
    }

    /// <summary>
    /// Run the standard set of sweeps for all known caches. Call once at
    /// service startup (after IWebHostEnvironment is available, before
    /// services that read from these caches make their first request).
    ///
    /// Adding a new cache category means: add a stamp property above AND
    /// add a sweep line here. No more version constants to remember to
    /// bump — every rebuild auto-invalidates.
    /// </summary>
    public void SweepAllOnStartup()
    {
        _logger.LogInformation(
            "CacheSweep: starting up — stamps SkinnedGlb=v{SG}, RigidGlb=v{RG}, SkinPng=v{SP}",
            SkinnedGlbVersion, RigidGlbVersion, SkinPngVersion);

        SweepDirectory("character_models", SkinnedGlbVersion, ".glb");
        SweepDirectory(Path.Combine("character_textures", "skin"), SkinPngVersion, ".png");
        SweepDirectory("item_models", RigidGlbVersion, ".glb");
    }

    // ────────────────────────────────────────────────────────────────────
    // Internals
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true iff the filename's basename ends with ".v{currentVersion}".
    /// Doesn't match ".v" followed by any number — only the EXACT current
    /// version, so any older-stamped files all get deleted when we're
    /// looking for the current stamp.
    ///
    ///   HasCurrent("/x/HumanMale.v37214.glb", 37214) → true
    ///   HasCurrent("/x/HumanMale.v9999.glb",  37214) → false
    ///   HasCurrent("/x/HumanMale.glb",        37214) → false   (unstamped)
    /// </summary>
    private static bool HasCurrentVersionStamp(string path, int currentVersion)
    {
        var basename = Path.GetFileNameWithoutExtension(path);
        return basename.EndsWith($".v{currentVersion}", StringComparison.Ordinal);
    }
}