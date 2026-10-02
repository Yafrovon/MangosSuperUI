using System.Security.Cryptography;
using System.Text.Json;
using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.WeaponForge;

namespace MangosSuperUI.Services.UnifiedPatch;

/// <summary>
/// Owns the ONE client patch that carries ItemDisplayInfo.dbc. It gathers rows and members from
/// every lane that writes that table — retextures, forged weapons, forged armor — packs them into a
/// single archive, deploys it, and RETIRES the per-lane patches it replaces.
///
/// The retirement is not tidiness, it is correctness. MPQ resolves whole files by rank, so a
/// leftover patch-6 outranks patch-4 and would keep serving its own stale ItemDisplayInfo.dbc
/// forever — the unified patch would build perfectly and change nothing in game.
///
/// Lanes are resolved lazily from the service provider rather than injected. Each of them can
/// trigger a rebuild, so constructor injection would close a dependency cycle.
/// </summary>
public sealed class UnifiedPatchService
{
    /// <summary>The single archive. patch-4 because it is the LOWEST of the three it replaces:
    /// taking the highest would leave the other two ranking above it until they were deleted, so a
    /// half-applied migration would silently keep shadowing. At the bottom, a stale leftover can
    /// only ever lose to us.</summary>
    public const string PatchFileName = "patch-4.MPQ";

    /// <summary>Per-lane archives this patch subsumes. Deleted from the client on every deploy.</summary>
    public static readonly string[] SupersededPatchFileNames = { "patch-5.MPQ", "patch-6.MPQ" };

    private readonly MpqReaderService _mpq;
    private readonly UnifiedPatchBuilder _builder;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<UnifiedPatchService> _logger;

    public UnifiedPatchService(MpqReaderService mpq, UnifiedPatchBuilder builder, IServiceProvider services,
        IConfiguration config, IWebHostEnvironment env, ILogger<UnifiedPatchService> logger)
    {
        _mpq = mpq; _builder = builder; _services = services;
        _config = config; _env = env; _logger = logger;
    }

    public string ArtifactDir => Path.Combine(_env.WebRootPath, "patches", "unified");
    public string ArtifactPath => Path.Combine(ArtifactDir, PatchFileName);

    // ── Pending rebuild queue ────────────────────────────────────────────────────────────────
    //
    // Forging used to rebuild AND deploy this patch on every single item. That is one full three-lane
    // repack per weapon, and a deploy that fails whenever the game client is running — for one item
    // at a time. Lanes now QUEUE a change here instead and the operator ships the whole batch with
    // one "Rebuild patch" click. The queue lives in a small file next to the artifact (this service
    // is scoped, so nothing in-memory survives the request, and the file survives a restart too).

    /// <summary>One registry change that has not yet been packaged into the patch.</summary>
    public sealed record PendingChange(string Lane, string Reason, DateTime QueuedUtc);

    private static readonly object PendingLock = new();
    private string PendingPath => Path.Combine(ArtifactDir, "pending-rebuild.json");

    /// <summary>Changes made since the artifact was last (re)built, oldest first.</summary>
    public IReadOnlyList<PendingChange> PendingChanges
    {
        get { lock (PendingLock) return ReadPending(); }
    }

    /// <summary>Record that a lane changed the registry without rebuilding the patch. Returns the
    /// queue depth after the add, for the lane's own result message.</summary>
    public int QueueChange(string lane, string reason)
    {
        lock (PendingLock)
        {
            var list = ReadPending();
            list.Add(new PendingChange(lane, reason, DateTime.UtcNow));
            WritePending(list);
            _logger.LogInformation("UnifiedPatch: queued rebuild ({Lane}: {Reason}) — {Count} change(s) pending", lane, reason, list.Count);
            return list.Count;
        }
    }

    private void ClearPending()
    {
        lock (PendingLock) TryDelete(PendingPath);
    }

    private List<PendingChange> ReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return new List<PendingChange>();
            return JsonSerializer.Deserialize<List<PendingChange>>(File.ReadAllText(PendingPath)) ?? new List<PendingChange>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UnifiedPatch: could not read the pending-rebuild queue; treating it as empty");
            return new List<PendingChange>();
        }
    }

    private void WritePending(List<PendingChange> list)
    {
        Directory.CreateDirectory(ArtifactDir);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(list));
    }

    /// <summary>What the operator needs to know on the forge pages: is there a patch, is it in the
    /// configured client Data directory, does that copy match, and how many changes await a rebuild.
    /// This replaces the per-lane checks that compared patch-5 / patch-6 files that no longer exist.</summary>
    public UnifiedPatchDeployStatus DeployStatus()
    {
        var pending = PendingChanges;
        var dataPath = ClientDataPath;
        bool built = File.Exists(ArtifactPath);
        string? target = dataPath is null ? null : Path.Combine(dataPath, PatchFileName);
        bool deployed = target is not null && File.Exists(target);
        ForgeFileComparison? comparison = null;
        string message;

        if (dataPath is null)
            message = "no existing configured client Data directory; other clients are not inspected";
        else if (!built && !deployed)
            message = "no patch built yet";
        else if (built && !deployed)
        {
            comparison = ForgeArtifactDiagnostics.CompareFiles(ArtifactPath, target!);
            message = comparison.Message;
        }
        else if (!built)
            message = $"{PatchFileName} exists in the configured client Data directory, but no unified build is available for comparison";
        else
        {
            comparison = ForgeArtifactDiagnostics.CompareFiles(ArtifactPath, target!);
            message = comparison.Message;
        }

        if (pending.Count > 0)
            message = $"{pending.Count} change(s) queued since the last rebuild; Rebuild patch packages these changes. " + message;

        return new UnifiedPatchDeployStatus
        {
            Configured = dataPath is not null,
            Built = built,
            Deployed = deployed,
            Stale = comparison?.Stale ?? false,
            ComparisonKnown = comparison?.ComparisonKnown ?? false,
            ComparisonState = comparison?.State ?? "unavailable",
            ReferencePath = ArtifactPath,
            TargetPath = target,
            ExpectedSha256 = comparison?.ExpectedSha256,
            ActualSha256 = comparison?.ActualSha256,
            Pending = pending.Count,
            PendingReasons = pending.Select(c => $"{c.Lane}: {c.Reason}").ToArray(),
            Message = message,
        };
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private string? ClientDataPath
    {
        get
        {
            var p = _config["Vmangos:ClientDataPath"] ?? _config["SpellCreator:ClientDataPath"];
            return !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p : null;
        }
    }

    /// <summary>Rebuild the single patch from every lane's database state.</summary>
    /// <param name="reason">Free text for the log — what triggered this.</param>
    /// <param name="deploy">Copy into the live client and retire the superseded archives. False
    /// builds the artifact only, which is how you inspect the output before switching over.</param>
    public async Task<UnifiedPatchSummary> RebuildAsync(string reason, bool deploy = true)
    {
        var diag = new ForgeDiagnostics("unified");

        var weaponSvc = _services.GetService(typeof(CustomWeaponBuildService)) as CustomWeaponBuildService;
        var armorSvc = _services.GetService(typeof(CustomArmorBuildService)) as CustomArmorBuildService;
        var retextureSvc = _services.GetService(typeof(ItemRetextureService)) as ItemRetextureService;

        var weapon = weaponSvc is not null ? await weaponSvc.GetPatchContributionAsync(diag) : null;
        var armor = armorSvc is not null ? await armorSvc.GetPatchContributionAsync(diag, PatchFileName) : null;
        var retexture = retextureSvc is not null ? await retextureSvc.GetPatchContributionAsync(diag) : null;

        int rowCount = (weapon?.Displays.Count ?? 0) + (armor?.Displays.Count ?? 0) + (retexture?.Displays.Count ?? 0);
        int setCount = armor?.Sets.Count ?? 0;

        // Nothing anywhere: remove the patch rather than ship an empty archive that still shadows
        // whatever sits beneath it.
        if (rowCount == 0 && setCount == 0)
        {
            var removed = deploy ? RemoveFromClient(PatchFileName) : (Ok: true, Changed: false, Message: "not deployed (build-only)");
            var retiredEmpty = deploy ? RetireSupersededPatches() : Array.Empty<string>();
            TryDelete(ArtifactPath);
            ClearPending();
            _logger.LogInformation("UnifiedPatch: nothing to build ({Reason}) — {Msg}", reason, removed.Message);
            return new UnifiedPatchSummary
            {
                Ok = true,
                Message = $"no retextures, weapons or armor in the registry — {PatchFileName} removed ({removed.Message})",
                RetiredPatches = retiredEmpty,
                Diagnostics = diag.Items.Select(i => i.ToString()).ToArray(),
            };
        }

        byte[] baseDbc = ResolveBaseDbc();

        var input = new UnifiedPatchInput
        {
            CleanItemDisplayInfoDbc = baseDbc,
            RetextureDisplays = retexture?.Displays ?? [],
            WeaponDisplays = weapon?.Displays ?? [],
            ArmorDisplays = armor?.Displays ?? [],
            RetextureMembers = retexture?.Members ?? [],
            WeaponMembers = weapon?.Members ?? [],
            ArmorMembers = armor?.Members ?? [],
            Sets = armor?.Sets ?? [],
            CleanItemSetDbc = armor?.CleanItemSetDbc,
            SetsOmitted = armor?.SetsOmitted ?? false,
            Diagnostics = diag,
        };

        string tempDir = Path.Combine(Path.GetTempPath(), "unifiedpatch", Guid.NewGuid().ToString("N")[..8]);
        var patch = _builder.Build(input, tempDir);

        Directory.CreateDirectory(ArtifactDir);
        File.WriteAllBytes(ArtifactPath, patch.MpqBytes);
        // The artifact now reflects every queued change. Whether it reached the CLIENT is a separate
        // question, answered by DeployStatus comparing the two files.
        ClearPending();

        string deployMessage = "not deployed (build-only)";
        IReadOnlyList<string> retired = Array.Empty<string>();
        string serverSetState = "NotAttempted";
        string serverSetMessage = "sets not deployed (build-only)";

        if (deploy)
        {
            deployMessage = DeployToClient(patch.MpqBytes).Message;
            // Order matters: put the new archive in place FIRST, then remove the ones that outrank
            // it. The reverse leaves a window with no item art mounted at all.
            retired = RetireSupersededPatches();

            // The client-side member and the server-side file are two separate deliveries; mangosd
            // only ever reads the second one, and zeroes every forged set_id without it.
            if (armorSvc is not null)
            {
                var setDeploy = armorSvc.DeployItemSetToServer(patch.ItemSetDbcBytes, input.SetsOmitted);
                serverSetState = setDeploy.State.ToString();
                serverSetMessage = setDeploy.Message;
                if (setDeploy.State == ItemSetDeployState.Failed)
                    _logger.LogWarning("UnifiedPatch: server ItemSet.dbc NOT deployed — {Msg}", setDeploy.Message);
            }
        }

        _logger.LogInformation(
            "UnifiedPatch: rebuilt ({Reason}) — {Rows} rows ({Rt} retexture, {W} weapon, {A} armor), " +
            "{Members} members, {Bytes:N0} bytes. {Deploy}",
            reason, patch.TotalRows, patch.RetextureRows, patch.WeaponRows, patch.ArmorRows,
            patch.Members.Count, patch.MpqBytes.Length, deployMessage);

        return new UnifiedPatchSummary
        {
            Ok = patch.AllVerified,
            Message = $"{PatchFileName}: {patch.TotalRows} display rows, {patch.Members.Count} members. {deployMessage}",
            RetextureRows = patch.RetextureRows,
            WeaponRows = patch.WeaponRows,
            ArmorRows = patch.ArmorRows,
            SetCount = patch.SetCount,
            MemberCount = patch.Members.Count,
            SkippedWeapons = weapon?.SkippedCount ?? 0,
            SkippedArmor = armor?.SkippedCount ?? 0,
            AllVerified = patch.AllVerified,
            MpqSha256 = patch.MpqSha256,
            Bytes = patch.MpqBytes.Length,
            ArtifactPath = ArtifactPath,
            DeployMessage = deployMessage,
            RetiredPatches = retired,
            ServerItemSetState = serverSetState,
            ServerItemSetMessage = serverSetMessage,
            Diagnostics = diag.Items.Select(i => i.ToString()).ToArray(),
        };
    }

    /// <summary>Stock ItemDisplayInfo.dbc from strictly BENEATH the unified patch, so it never reads
    /// its own previous output back as input. Skipping by RANK rather than by name is what keeps the
    /// chain one-way: any archive at or above our rank is excluded, including the superseded
    /// per-lane patches while they are still lying around in a half-migrated client.</summary>
    private byte[] ResolveBaseDbc()
    {
        var cfgPath = _config["UnifiedPatch:CleanDbcPath"] ?? _config["WeaponForge:CleanDbcPath"];
        if (!string.IsNullOrWhiteSpace(cfgPath) && File.Exists(cfgPath))
            return File.ReadAllBytes(cfgPath);

        // The effective mounted state from strictly BENEATH this patch, so the builder never reads
        // its own previous output back as input. Skipping by RANK rather than by name is what keeps
        // that a one-way chain — see the note on MpqPatchOrder.
        int myRank = MpqPatchOrder.Rank(PatchFileName);
        return _mpq.ExtractFile(WeaponNaming.ItemDisplayInfoMember,
                   skipArchive: name => MpqPatchOrder.Rank(name) >= myRank)
               ?? throw new InvalidOperationException(
                   "Could not extract a base ItemDisplayInfo.dbc from the mounted archives.");
    }

    private (bool Ok, string Message) DeployToClient(byte[] mpqBytes)
    {
        var dataPath = ClientDataPath;
        if (dataPath is null) return (false, "no client Data path configured — copy the patch in yourself");
        try
        {
            File.WriteAllBytes(Path.Combine(dataPath, PatchFileName), mpqBytes);
            return (true, $"deployed {PatchFileName} to {dataPath}");
        }
        catch (Exception ex)
        {
            return (false, $"deploy failed ({ex.Message}) — the client is probably running; close it and rebuild");
        }
    }

    /// <summary>Delete the per-lane archives this patch replaces out of the live client. They outrank
    /// patch-4, so leaving one behind means the unified patch is built, deployed and completely
    /// inert. Reports what actually moved so a half-migrated client is visible rather than silent.</summary>
    private IReadOnlyList<string> RetireSupersededPatches()
    {
        var retired = new List<string>();
        foreach (var name in SupersededPatchFileNames)
        {
            var r = RemoveFromClient(name);
            if (r.Changed) retired.Add(name);
            if (!r.Ok)
                _logger.LogWarning("UnifiedPatch: could not retire {Patch} — {Msg}. It OUTRANKS {Mine} " +
                    "and will keep shadowing it until removed by hand.", name, r.Message, PatchFileName);
        }
        if (retired.Count > 0)
            _logger.LogInformation("UnifiedPatch: retired superseded archive(s): {Retired}", string.Join(", ", retired));
        return retired;
    }

    private (bool Ok, bool Changed, string Message) RemoveFromClient(string fileName)
    {
        var dataPath = ClientDataPath;
        if (dataPath is null) return (true, false, "no client Data path configured");
        string target = Path.Combine(dataPath, fileName);
        if (!File.Exists(target)) return (true, false, $"{fileName} not present in the client");
        try
        {
            File.Delete(target);
            return (true, true, $"removed {fileName} from {dataPath}");
        }
        catch (Exception ex)
        {
            return (false, false, $"could not remove {fileName}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}

public sealed class UnifiedPatchDeployStatus
{
    public bool Configured { get; init; }
    public bool Built { get; init; }
    public bool Deployed { get; init; }
    /// <summary>The configured client Data copy is missing or differs from the unified build.</summary>
    public bool Stale { get; init; }
    public bool ComparisonKnown { get; init; }
    public string ComparisonState { get; init; } = "unavailable";
    public string? ReferencePath { get; init; }
    public string? TargetPath { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? ActualSha256 { get; init; }
    /// <summary>Registry changes queued since the artifact was last rebuilt.</summary>
    public int Pending { get; init; }
    public IReadOnlyList<string> PendingReasons { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = "";
}

public sealed class UnifiedPatchSummary
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";

    public int RetextureRows { get; init; }
    public int WeaponRows { get; init; }
    public int ArmorRows { get; init; }
    public int SetCount { get; init; }
    public int MemberCount { get; init; }

    /// <summary>Registry entries with no packageable bytes. They are NOT in the patch and will render
    /// as the error model; re-forge them.</summary>
    public int SkippedWeapons { get; init; }
    public int SkippedArmor { get; init; }

    public bool AllVerified { get; init; }
    public string? MpqSha256 { get; init; }
    public long Bytes { get; init; }
    public string? ArtifactPath { get; init; }
    public string? DeployMessage { get; init; }

    /// <summary>Superseded per-lane archives actually deleted from the client this run.</summary>
    public IReadOnlyList<string> RetiredPatches { get; init; } = Array.Empty<string>();

    public string ServerItemSetState { get; init; } = "";
    public string ServerItemSetMessage { get; init; } = "";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public int TotalRows => RetextureRows + WeaponRows + ArmorRows;
}
